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
        private const int AlbumSize = 10;
        private const int MaxLinksPerMessage = 3;

        private static readonly Regex LinkRegex = new(@"https?://[^\s<>""']+", RegexOptions.Compiled);

        private readonly YtDlp _ytDlp;
        private readonly List<IMediaSource> _sources;

        private readonly SemaphoreSlim _slots = new(2);

        public MediaModule(string ytDlpPath, string ffmpegPath, string? cookiesFile)
        {
            _ytDlp = new YtDlp(ytDlpPath, ffmpegPath, cookiesFile);
            _sources =
            [
                new YouTubeSource(_ytDlp),
                new InstagramSource(_ytDlp),
                new TikTokSource(_ytDlp),
                new XSource()
            ];
        }

        public Task UpdateYtDlpAsync(CancellationToken ct) => _ytDlp.UpdateAsync(ct);

        public Task HandleMediaCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            var message = update.Message;
            if (message?.Text is not { } text)
                return Task.CompletedTask;

            var links = LinkRegex.Matches(text)
                .Select(m => m.Value.TrimEnd('.', ',', '!', '?', ')', ']'))
                .Distinct()
                .Take(MaxLinksPerMessage);

            foreach (string link in links)
            {
                if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
                    continue;

                var source = _sources.FirstOrDefault(s => s.CanHandle(uri));
                if (source == null)
                    continue;

                _ = Task.Run(() => ProcessLinkAsync(bot, message, uri, source, ct), ct);
            }

            return Task.CompletedTask;
        }

        private async Task ProcessLinkAsync(ITelegramBotClient bot, Message message, Uri uri, IMediaSource source, CancellationToken ct)
        {
            string workDir = Path.Combine(Path.GetTempPath(), "AnikiChatBot", Guid.NewGuid().ToString("N"));

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
                timeout.CancelAfter(TimeSpan.FromMinutes(5));

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
                        await bot.SendMessage(message.Chat.Id, "Видео больше 50 МБ — Telegram не даёт боту его отправить.",
                            replyParameters: message.MessageId, cancellationToken: timeout.Token);
                    }
                    return;
                }

                await SendAsync(bot, message, ready, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[Media] {uri}: превышено время обработки");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Media] {uri}: {ex.Message}");
            }
            finally
            {
                _slots.Release();
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }

        private static async Task<bool> PrepareFilesAsync(List<MediaItem> items, string workDir, CancellationToken ct)
        {
            bool hasOversized = false;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];

                if (item.FilePath == null && item.Url != null)
                {
                    string path = Path.Combine(workDir, $"photo_{i}.jpg");
                    long limit = item.Kind == MediaKind.Photo ? MaxPhotoBytes : MaxVideoBytes;

                    try
                    {
                        if (await MediaHttp.DownloadAsync(item.Url, path, limit, ct))
                            item.FilePath = path;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"[Media] Не удалось скачать {item.Url}: {ex.Message}");
                    }
                }
                else if (item.FilePath != null && new FileInfo(item.FilePath).Length > MaxVideoBytes)
                {
                    item.FilePath = null;
                    hasOversized = true;
                }
            }

            return hasOversized;
        }

        private static async Task SendAsync(ITelegramBotClient bot, Message message, List<MediaItem> items, CancellationToken ct)
        {
            foreach (var chunk in items.Chunk(AlbumSize))
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
