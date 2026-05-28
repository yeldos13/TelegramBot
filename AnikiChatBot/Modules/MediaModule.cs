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
            if (update.Message?.Text == null) return;

            string messageText = update.Message.Text;

            var linkMatch = Regex.Match(messageText, @"https?://[^\s]+");
            if (!linkMatch.Success) return;

            string mediaUrl = linkMatch.Value;

            var videoInfo = await _ytdl.RunVideoDataFetch(mediaUrl, ct: ct);

            if (!videoInfo.Success)
            {
                return;
            }

            string tempFileName = $"{Guid.NewGuid()}_video.mp4";
            string tempFilePath = Path.Combine(Path.GetTempPath(), tempFileName);

            try
            {
                var options = new OptionSet
                {
                    Format = "bestvideo[height<=720]+bestaudio/best[height<=720]/best",
                    MergeOutputFormat = YoutubeDLSharp.Options.DownloadMergeFormat.Mp4,
                    Output = tempFilePath
                };

                var result = await _ytdl.RunVideoDownload(mediaUrl, overrideOptions: options, ct: ct);

                if (!result.Success)
                {
                    throw new Exception(string.Join(Environment.NewLine, result.ErrorOutput));
                }

                string actualFilePath = tempFilePath;
                if (!File.Exists(actualFilePath))
                {
                    var matchingFiles = Directory.GetFiles(Path.GetTempPath(), $"{Guid.NewGuid()}_video.*");
                    if (matchingFiles.Length > 0)
                    {
                        actualFilePath = matchingFiles[0];
                    }
                    else
                    {
                        return;
                    }
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
                {
                    File.Delete(actualFilePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[yt-dlp Error]: {ex.Message}");

                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
            }
        }
    }
}
