using AnikiChatBot.Modules.Media;
using System.Net;
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

        private const int MaxParallel = 1;

        private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "AnikiChatBot");

        private readonly YtDlp _ytDlp;
        private readonly FFmpeg _ffmpeg;
        private readonly List<IMediaSource> _sources;
        private readonly OwnerNotifier? _notifier;
        private readonly StatsService? _stats;
        private readonly SemaphoreSlim _slots = new(MaxParallel);

        private int _sentCount;
        private int _failedCount;

        public int SentCount => _sentCount;
        public int FailedCount => _failedCount;
        public string? LastError { get; private set; }

        public Task<string> GetYtDlpVersionAsync(CancellationToken ct) => _ytDlp.GetVersionAsync(ct);

        public MediaModule(string ytDlpPath, string ffmpegPath, string? cookiesFile,
            OwnerNotifier? notifier = null, StatsService? stats = null)
        {
            _stats = stats;
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

        private class Post
        {
            public required Message Message { get; init; }
            public required string Author { get; init; }
            public long? AuthorId { get; init; }
            public required string UserText { get; init; }
            public int Remaining;
            public int Sent;
            public int TextUsed;
        }

        public Task HandleMediaCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            var message = update.Message;
            if (message?.Text is not { } text)
                return Task.CompletedTask;

            var links = ExtractLinkStrings(text)
                .Select(link => (Link: link, Uri: new Uri(link)))
                .Select(x => (x.Link, x.Uri, Source: FindSource(x.Uri)))
                .Where(x => x.Source != null)
                .ToList();

            if (links.Count == 0)
                return Task.CompletedTask;

            var post = new Post
            {
                Message = message,
                Author = message.From != null ? PenisModule.DisplayName(message.From) : message.SenderChat?.Title ?? "Кто-то",
                AuthorId = message.SenderChat == null ? message.From?.Id : null,
                UserText = RemoveLinks(text, links.Select(l => l.Link)),
                Remaining = links.Count
            };

            foreach (var (_, uri, source) in links)
                _ = Task.Run(() => ProcessLinkAsync(bot, post, uri, source!, ct), ct);

            return Task.CompletedTask;
        }

        public static IEnumerable<Uri> ExtractLinks(string text) =>
            ExtractLinkStrings(text).Select(link => new Uri(link));

        private static IEnumerable<string> ExtractLinkStrings(string text)
        {
            return LinkRegex.Matches(text)
                .Select(m => m.Value.TrimEnd('.', ',', '!', '?', ')', ']'))
                .Distinct()
                .Take(MaxLinksPerMessage)
                .Where(link => Uri.TryCreate(link, UriKind.Absolute, out _));
        }

        public static string RemoveLinks(string text, IEnumerable<string> links)
        {
            foreach (string link in links)
                text = text.Replace(link, " ");

            return Regex.Replace(text, @"[ \t]+", " ").Replace(" \n", "\n").Replace("\n ", "\n").Trim();
        }

        public static string BuildCaption(string author, long? authorId, string url, string? userText)
        {
            string name = WebUtility.HtmlEncode(author);
            if (authorId != null)
                name = $"<a href=\"tg://user?id={authorId}\">{name}</a>";

            string caption = $"<b>{name}</b>: <a href=\"{WebUtility.HtmlEncode(url)}\">ссылка</a>";

            if (!string.IsNullOrWhiteSpace(userText))
            {
                if (userText.Length > MaxUserTextInCaption)
                    userText = userText[..MaxUserTextInCaption] + "…";
                caption += "\n" + WebUtility.HtmlEncode(userText);
            }

            return caption;
        }

        private const int MaxUserTextInCaption = 900;

        private async Task ProcessLinkAsync(ITelegramBotClient bot, Post post, Uri uri, IMediaSource source, CancellationToken ct)
        {
            var message = post.Message;
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

                string? userText = Interlocked.Exchange(ref post.TextUsed, 1) == 0 ? post.UserText : null;
                string caption = BuildCaption(post.Author, post.AuthorId, uri.ToString(), userText);

                await SendAsync(bot, message, ready, caption, timeout.Token);
                Interlocked.Increment(ref post.Sent);
                Interlocked.Increment(ref _sentCount);
                _stats?.RecordMedia(message.Chat.Id);
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

                if (Interlocked.Decrement(ref post.Remaining) == 0 && post.Sent > 0)
                {
                    try
                    {
                        await bot.DeleteMessage(message.Chat.Id, message.MessageId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Media] Не удалось удалить сообщение со ссылкой (нужно право «Удаление сообщений»): {ex.Message}");
                    }
                }
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

        private static async Task SendAsync(ITelegramBotClient bot, Message message, List<MediaItem> items, string caption, CancellationToken ct)
        {
            ReplyParameters? reply = message.ReplyToMessage is { } original
                ? new ReplyParameters { MessageId = original.MessageId, AllowSendingWithoutReply = true }
                : null;
            int? threadId = message.IsTopicMessage ? message.MessageThreadId : null;

            string? pendingCaption = caption;
            string? TakeCaption() => Interlocked.Exchange(ref pendingCaption, null);

            foreach (var animation in items.Where(x => x.Kind == MediaKind.Animation))
            {
                await using var stream = File.OpenRead(animation.FilePath!);
                await bot.SendAnimation(message.Chat.Id, InputFile.FromStream(stream, "animation.mp4"),
                    caption: TakeCaption(), parseMode: ParseMode.Html,
                    replyParameters: reply, messageThreadId: threadId,
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
                                caption: TakeCaption(), parseMode: ParseMode.Html,
                                replyParameters: reply, messageThreadId: threadId, cancellationToken: ct);
                        }
                        else
                        {
                            await bot.SendVideo(message.Chat.Id, InputFile.FromStream(stream, "video.mp4"),
                                caption: TakeCaption(), parseMode: ParseMode.Html,
                                replyParameters: reply, messageThreadId: threadId,
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

                        string? itemCaption = i == 0 ? TakeCaption() : null;

                        if (item.Kind == MediaKind.Photo)
                        {
                            album.Add(new InputMediaPhoto(InputFile.FromStream(stream, $"photo{i}.jpg"))
                            {
                                Caption = itemCaption,
                                ParseMode = ParseMode.Html
                            });
                        }
                        else
                        {
                            var video = new InputMediaVideo(InputFile.FromStream(stream, $"video{i}.mp4"))
                            {
                                SupportsStreaming = true,
                                Caption = itemCaption,
                                ParseMode = ParseMode.Html
                            };
                            if (item.Duration is { } duration) video.Duration = duration;
                            if (item.Width is { } width) video.Width = width;
                            if (item.Height is { } height) video.Height = height;
                            album.Add(video);
                        }
                    }

                    await bot.SendMediaGroup(message.Chat.Id, album,
                        replyParameters: reply, messageThreadId: threadId, cancellationToken: ct);
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
