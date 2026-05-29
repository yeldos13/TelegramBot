using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace AnikiChatBot.Modules
{
    public class MediaModule
    {
        private readonly YoutubeDL _ytdl;
        private readonly HttpClient _httpClient;
        private const string LocalApiBaseUrl = "http://localhost:3000/api";

        public MediaModule()
        {
            _ytdl = new YoutubeDL();
            _ytdl.YoutubeDLPath = @"C:\YTDLP\yt-dlp.exe";
            _ytdl.FFmpegPath = @"C:\FFMPEG\bin\ffmpeg.exe";

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task HandleMediaCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text == null)
                return;

            var linkMatch = Regex.Match(update.Message.Text, @"https?://[^\s]+");
            if (!linkMatch.Success)
                return;

            string mediaUrl = linkMatch.Value;
            long chatId = update.Message.Chat.Id;
            int messageId = update.Message.MessageId;

            bool downloadedViaYtdlp = await TryDownloadViaYtDlp(bot, update, mediaUrl, ct);
            if (downloadedViaYtdlp)
            {
                return;
            }

            if (mediaUrl.Contains("instagram.com") || mediaUrl.Contains("tiktok.com") ||
                mediaUrl.Contains("twitter.com") || mediaUrl.Contains("x.com") ||
                mediaUrl.Contains("pinterest.com") || mediaUrl.Contains("pin.it") ||
                mediaUrl.Contains("reddit.com"))
            {
                try
                {
                    bool processedViaApi = await TryDownloadPhotosViaLocalApi(bot, chatId, messageId, mediaUrl, ct);
                    if (processedViaApi)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Local API Error]: {ex.Message}");
                }
            }
        }

        private async Task<bool> TryDownloadViaYtDlp(ITelegramBotClient bot, Update update, string mediaUrl, CancellationToken ct)
        {
            var videoInfo = await _ytdl.RunVideoDataFetch(mediaUrl, ct: ct);
            if (!videoInfo.Success)
            {
                return false;
            }

            if (videoInfo.Data.Duration == 0 && (mediaUrl.Contains("tiktok.com") || mediaUrl.Contains("instagram.com")))
            {
                return false;
            }

            string tempFileName = $"{Guid.NewGuid()}_video.mp4";
            string tempFilePath = Path.Combine(Path.GetTempPath(), tempFileName);

            try
            {
                var options = new OptionSet
                {
                    Format = "bestvideo[height<=720]+bestaudio/best[height<=720]/best",
                    MergeOutputFormat = DownloadMergeFormat.Mp4,
                    Output = tempFilePath,
                    MaxFilesize = "100M"
                };

                var result = await _ytdl.RunVideoDownload(mediaUrl, overrideOptions: options, ct: ct);

                if (!result.Success)
                {
                    if (result.ErrorOutput.Any(line => line.Contains("File is larger than max-file-size")))
                    {
                        return true;
                    }
                    return false;
                }

                string actualFilePath = tempFilePath;
                if (!File.Exists(actualFilePath))
                {
                    string baseName = Path.GetFileNameWithoutExtension(tempFileName);
                    var matchingFiles = Directory.GetFiles(Path.GetTempPath(), $"{baseName}.*");
                    if (matchingFiles.Length > 0)
                        actualFilePath = matchingFiles[0];
                    else
                        return false;
                }

                FileInfo fileInfo = new FileInfo(actualFilePath);
                long maxSizeBytes = 100 * 1024 * 1024;

                if (fileInfo.Length > maxSizeBytes)
                {
                    if (File.Exists(actualFilePath)) File.Delete(actualFilePath);
                    return true;
                }

                using (var videoStream = new FileStream(actualFilePath, FileMode.Open, FileAccess.Read))
                {
                    await bot.SendVideo(
                        chatId: update.Message.Chat.Id,
                        video: InputFile.FromStream(videoStream, "video.mp4"),
                        replyParameters: update.Message.MessageId,
                        cancellationToken: ct
                    );
                }

                if (File.Exists(actualFilePath)) File.Delete(actualFilePath);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[yt-dlp Inner Error]: {ex.Message}");
                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                return false;
            }
        }

        private async Task<bool> TryDownloadPhotosViaLocalApi(ITelegramBotClient bot, long chatId, int messageId, string url, CancellationToken ct)
        {
            string platform = null;

            if (url.Contains("instagram.com")) platform = "meta";
            else if (url.Contains("tiktok.com")) platform = "tiktok";
            else if (url.Contains("twitter.com") || url.Contains("x.com")) platform = "twitter";
            else if (url.Contains("pinterest.com") || url.Contains("pin.it")) platform = "pinterest";
            else if (url.Contains("reddit.com")) platform = "reddit";

            if (platform == null)
                return false;

            string apiUrl = $"{LocalApiBaseUrl}/{platform}/download?url={Uri.EscapeDataString(url)}";

            var response = await _httpClient.GetAsync(apiUrl, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            string jsonString = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(jsonString);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("success", out JsonElement successProp) && !successProp.GetBoolean())
                return false;

            if (!root.TryGetProperty("data", out JsonElement firstData))
                return false;

            if (!firstData.TryGetProperty("data", out JsonElement innerData) || innerData.ValueKind != JsonValueKind.Array)
                innerData = firstData;

            List<string> rawUrls = new List<string>();
            if (innerData.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in innerData.EnumerateArray())
                {
                    if (item.TryGetProperty("url", out JsonElement urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    {
                        string mediaLink = urlProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(mediaLink))
                            rawUrls.Add(mediaLink);
                    }
                }
            }

            if (rawUrls.Count == 0)
                return false;

            var targetUrls = rawUrls.Take(10).ToList();
            var streamsToDispose = new List<Stream>();
            var mediaGroup = new List<IAlbumInputMedia>();

            try
            {
                for (int i = 0; i < targetUrls.Count; i++)
                {
                    var fileResponse = await _httpClient.GetAsync(targetUrls[i], ct);
                    if (!fileResponse.IsSuccessStatusCode)
                        continue;

                    var memoryStream = new MemoryStream();
                    await fileResponse.Content.CopyToAsync(memoryStream, ct);
                    memoryStream.Position = 0;

                    streamsToDispose.Add(memoryStream);

                    string fileName = $"photo{i}.jpg";
                    var inputMedia = new InputMediaPhoto(InputFile.FromStream(memoryStream, fileName));
                    mediaGroup.Add(inputMedia);
                }

                if (mediaGroup.Count > 0)
                {
                    await bot.SendMediaGroup(chatId, mediaGroup, replyParameters: messageId, cancellationToken: ct);
                    return true;
                }
            }
            finally
            {
                foreach (var stream in streamsToDispose)
                    stream.Dispose();
            }

            return false;
        }
    }
}
