using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class InstagramModule
    {
        public HttpClient httpClient { get; set; }

        private const string CobaltApiUrl = "http://192.168.1.156:7172/api/json";
        public async Task HandleInstagramCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text == null) return;

            string messageText = update.Message.Text;

            var linkMatch = Regex.Match(messageText, @"https?://(www\.)?instagram\.com/(reel|p)/[^?\s]+");
            if (!linkMatch.Success) return;

            string instagramUrl = linkMatch.Value;

            var loadingMessage = await bot.SendMessage(
                chatId: update.Message.Chat.Id,
                text: "Скачиваю...",
                replyParameters: update.Message.MessageId,
                cancellationToken: ct
            );

            try
            {
                var requestBody = new
                {
                    url = instagramUrl,
                    vQuality = "720",
                    vCodec = "h264",
                    filenamePattern = "basic",
                    isAudioOnly = false
                };

                string jsonPayload = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, CobaltApiUrl);

                request.Headers.Add("Accept", "application/json");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync(ct);
                    throw new Exception($"Cobalt API вернул статус {response.StatusCode}: {errorContent}");
                }

                string jsonString = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp))
                {
                    string status = statusProp.GetString() ?? "";

                    if (status == "error")
                    {
                        string textError = root.GetProperty("text").GetString() ?? "Неизвестная ошибка Cobalt";
                        throw new Exception(textError);
                    }

                    if ((status == "stream" || status == "redirect" || status == "success") && root.TryGetProperty("url", out var urlProp))
                    {
                        string videoUrl = urlProp.GetString()!;

                        await bot.SendVideo(
                            chatId: update.Message.Chat.Id,
                            video: InputFile.FromUri(videoUrl),
                            replyParameters: update.Message.MessageId,
                            cancellationToken: ct
                        );

                        await bot.DeleteMessage(update.Message.Chat.Id, loadingMessage.MessageId, ct);
                        return;
                    }
                }

                throw new Exception("Не удалось распарсить ответ от Cobalt.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cobalt Error]: {ex.Message}");
                await bot.EditMessageText(
                    chatId: update.Message.Chat.Id,
                    messageId: loadingMessage.MessageId,
                    text: $"Ошибка Cobalt: {ex.Message}",
                    cancellationToken: ct
                );
            }
        }
    }
}
