using AnikiChatBot.Modules.Media;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class MediaModule
    {
        public const long MaxVideoBytes = 50L * 1024 * 1024;
        private const long MaxPhotoBytes = 10L * 1024 * 1024;

        public const long MaxDownloadBytes = 500L * 1024 * 1024;
        private const long CompressTargetBytes = 48L * 1024 * 1024;

        private const int AlbumSize = 10;
        private const int MaxLinksPerMessage = 3;

        private static readonly Regex LinkRegex = new(@"https?://[^\s<>""']+", RegexOptions.Compiled);

        private const int MaxParallel = 2;

        private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "AnikiChatBot");

        private readonly YtDlp _ytDlp;
        private readonly FFmpeg _ffmpeg;
        private readonly List<IMediaSource> _sources;
        private readonly OwnerNotifier? _notifier;
        private readonly SemaphoreSlim _slots = new(MaxParallel);

        private int _sentCount;
        private int _failedCount;

        public int SentCount => _sentCount;
        public int FailedCount => _failedCount;
        public string? LastError { get; private set; }

        public Task<string> GetYtDlpVersionAsync(CancellationToken ct) => _ytDlp.GetVersionAsync(ct);

        public MediaModule(string ytDlpPath, string ffmpegPath, string? cookiesFile, OwnerNotifier? notifier = null)
        {
            _ytDlp = new YtDlp(ytDlpPath, ffmpegPath, cookiesFile);
            _ffmpeg = new FFmpeg(ffmpegPath);
            _notifier = notifier;
            _sources =
            [
                new YouTubeSource(_ytDlp),
                new InstagramSource(_ytDlp),
                new TikTokSource(_ytDlp),
                new XSource()
            ];
        }

        public async Task UpdateYtDlpAsync(CancellationToken ct)
        {
            int acquired = 0;
            try
            {
                for (; acquired < MaxParallel; acquired++)
                    await _slots.WaitAsync(ct);

                string? error = await _ytDlp.UpdateAsync(ct);
                if (error != null)
                {
                    Console.Error.WriteLine($"[yt-dlp] Не удалось обновить: {error}");
                    _notifier?.Notify("ytdlp-update", $"Не удалось обновить yt-dlp: {error}");
                }
            }
            finally
            {
                if (acquired > 0)
                    _slots.Release(acquired);
            }
        }

        public static void CleanupTempFiles()
        {
            if (!Directory.Exists(TempRoot))
                return;

            int deleted = 0;
            foreach (var dir in Directory.GetDirectories(TempRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1))
                    {
                        Directory.Delete(dir, recursive: true);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Media] Не удалось удалить {dir}: {ex.Message}");
                }
            }

            if (deleted > 0)
                Console.WriteLine($"[Media] Удалено временных папок после прошлого запуска: {deleted}");
        }

        public IMediaSource? FindSource(Uri uri) => _sources.FirstOrDefault(s => s.CanHandle(uri));

        public Task HandleMediaCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            var message = update.Message;
            if (message?.Text is not { } text)
                return Task.CompletedTask;

            foreach (var uri in ExtractLinks(text))
            {
                var source = FindSource(uri);
                if (source == null)
                    continue;

                _ = Task.Run(() => ProcessLinkAsync(bot, message, uri, source, ct), ct);
            }

            return Task.CompletedTask;
        }

        public static IEnumerable<Uri> ExtractLinks(string text)
        {
            return LinkRegex.Matches(text)
                .Select(m => m.Value.TrimEnd('.', ',', '!', '?', ')', ']'))
                .Distinct()
                .Take(MaxLinksPerMessage)
                .Select(link => Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri : null)
                .OfType<Uri>();
        }

        private async Task ProcessLinkAsync(ITelegramBotClient bot, Message message, Uri uri, IMediaSource source, CancellationToken ct)
        {
            string workDir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));

            try
            {
                await _slots.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(workDir);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));

                await bot.SendChatAction(message.Chat.Id, ChatAction.UploadVideo, cancellationToken: timeout.Token);

                var items = await source.ExtractAsync(uri, workDir, timeout.Token);
                if (items.Count == 0)
                {
                    Console.WriteLine($"[Media] {uri}: медиа не найдено");
                    return;
                }

                bool hasOversized = await PrepareFilesAsync(items, workDir, timeout.Token);
                var ready = items.Where(x => x.FilePath != null).ToList();

                if (ready.Count == 0)
                {
                    if (hasOversized)
                    {
                        await bot.SendMessage(message.Chat.Id, "Видео слишком длинное — даже сжатым оно не влезает в лимит Telegram 50 МБ.",
                            replyParameters: message.MessageId, cancellationToken: timeout.Token);
                    }
                    return;
                }

                await SendAsync(bot, message, ready, timeout.Token);
                Interlocked.Increment(ref _sentCount);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[Media] {uri}: превышено время обработки");
                Interlocked.Increment(ref _failedCount);
                LastError = $"{DateTime.Now:dd.MM HH:mm} {uri}: превышено время обработки";
                _notifier?.Notify($"media-timeout:{source.GetType().Name}", $"Скачивание не уложилось в 10 минут: {uri}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Media] {uri}: {ex.Message}");
                Interlocked.Increment(ref _failedCount);
                LastError = $"{DateTime.Now:dd.MM HH:mm} {uri}: {ex.Message}";
                _notifier?.Notify($"media:{source.GetType().Name}", $"Не удалось скачать {uri}\n{ex.Message}");
            }
            finally
            {
                _slots.Release();
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }

        private async Task<bool> PrepareFilesAsync(List<MediaItem> items, string workDir, CancellationToken ct)
        {
            bool hasOversized = false;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];

                if (item.FilePath == null && item.Url != null)
                {
                    string path = Path.Combine(workDir, $"photo_{i}.jpg");

                    try
                    {
                        if (await MediaHttp.DownloadAsync(item.Url, path, MaxPhotoBytes, ct))
                            item.FilePath = path;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"[Media] Не удалось скачать {item.Url}: {ex.Message}");
                    }
                }
                else if (item.FilePath != null && new FileInfo(item.FilePath).Length > MaxVideoBytes)
                {
                    string? compressed = await _ffmpeg.CompressAsync(item.FilePath, CompressTargetBytes, ct);

                    if (compressed != null)
                    {
                        Console.WriteLine($"[Media] Сжато: {new FileInfo(item.FilePath).Length / 1048576} МБ -> {new FileInfo(compressed).Length / 1048576} МБ");
                        item.FilePath = compressed;
                    }
                    else
                    {
                        item.FilePath = null;
                        hasOversized = true;
                    }
                }
            }

            return hasOversized;
        }

        private static async Task SendAsync(ITelegramBotClient bot, Message message, List<MediaItem> items, CancellationToken ct)
        {
            foreach (var animation in items.Where(x => x.Kind == MediaKind.Animation))
            {
                await using var stream = File.OpenRead(animation.FilePath!);
                await bot.SendAnimation(message.Chat.Id, InputFile.FromStream(stream, "animation.mp4"),
                    replyParameters: message.MessageId,
                    duration: animation.Duration, width: animation.Width, height: animation.Height,
                    cancellationToken: ct);
            }

            foreach (var chunk in items.Where(x => x.Kind != MediaKind.Animation).Chunk(AlbumSize))
            {
                var streams = new List<Stream>();

                try
                {
                    if (chunk.Length == 1)
                    {
                        var item = chunk[0];
                        var stream = File.OpenRead(item.FilePath!);
                        streams.Add(stream);

                        if (item.Kind == MediaKind.Photo)
                        {
                            await bot.SendPhoto(message.Chat.Id, InputFile.FromStream(stream, Path.GetFileName(item.FilePath)),
                                replyParameters: message.MessageId, cancellationToken: ct);
                        }
                        else
                        {
                            await bot.SendVideo(message.Chat.Id, InputFile.FromStream(stream, "video.mp4"),
                                replyParameters: message.MessageId,
                                duration: item.Duration, width: item.Width, height: item.Height,
                                supportsStreaming: true, cancellationToken: ct);
                        }
                        continue;
                    }

                    var album = new List<IAlbumInputMedia>();
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        var item = chunk[i];
                        var stream = File.OpenRead(item.FilePath!);
                        streams.Add(stream);

                        if (item.Kind == MediaKind.Photo)
                        {
                            album.Add(new InputMediaPhoto(InputFile.FromStream(stream, $"photo{i}.jpg")));
                        }
                        else
                        {
                            var video = new InputMediaVideo(InputFile.FromStream(stream, $"video{i}.mp4")) { SupportsStreaming = true };
                            if (item.Duration is { } duration) video.Duration = duration;
                            if (item.Width is { } width) video.Width = width;
                            if (item.Height is { } height) video.Height = height;
                            album.Add(video);
                        }
                    }

                    await bot.SendMediaGroup(message.Chat.Id, album, replyParameters: message.MessageId, cancellationToken: ct);
                }
                finally
                {
                    foreach (var stream in streams)
                        stream.Dispose();
                }
            }
        }
    }
}
