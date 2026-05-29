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

        public MediaModule()
        {
            _ytdl = new YoutubeDL();

            _ytdl.YoutubeDLPath = @"C:\YTDLP\yt-dlp.exe";
            _ytdl.FFmpegPath = @"C:\FFMPEG\bin\ffmpeg.exe";
        }

        public async Task HandleMediaCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text == null)
                return;

            var linkMatch = Regex.Match(update.Message.Text, @"https?://[^\s]+");
            if (!linkMatch.Success)
                return;

            string mediaUrl = linkMatch.Value;

            var videoInfo = await _ytdl.RunVideoDataFetch(mediaUrl, ct: ct);
            if (!videoInfo.Success)
                return;

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
                        await bot.SendMessage(update.Message.Chat.Id, "Видео слишком весит больше 100 МБ. Я не могу его отправить.", replyParameters: update.Message.MessageId, cancellationToken: ct);
                        return;
                    }
                    throw new Exception(string.Join(Environment.NewLine, result.ErrorOutput));
                }

                string actualFilePath = tempFilePath;

                if (!File.Exists(actualFilePath))
                {
                    string baseName = Path.GetFileNameWithoutExtension(tempFileName);
                    var matchingFiles = Directory.GetFiles(Path.GetTempPath(), $"{baseName}.*");
                    if (matchingFiles.Length > 0)
                        actualFilePath = matchingFiles[0];
                    else
                        return;
                }

                FileInfo fileInfo = new FileInfo(actualFilePath);
                long maxSizeBytes = 100 * 1024 * 1024;

                if (fileInfo.Length > maxSizeBytes)
                {
                    await bot.SendMessage(update.Message.Chat.Id, "Финальный файл превысил 100 МБ. Отмена отправки.", replyParameters: update.Message.MessageId, cancellationToken: ct);

                    if (File.Exists(actualFilePath))
                        File.Delete(actualFilePath);
                    return;
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

                if (File.Exists(actualFilePath))
                    File.Delete(actualFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[yt-dlp Error]: {ex.Message}");

                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
            }
        }
    }
}
