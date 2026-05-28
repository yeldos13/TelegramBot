using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class MediaModule
    {
        public HttpClient httpClient { get; set; }

        private const string CobaltApiUrl = "http://localhost:9000";

        public async Task HandleMediaCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text == null) return;

            string messageText = update.Message.Text;

            var linkMatch = Regex.Match(messageText, @"https?://(www\.|vm\.|vt\.|v\.|music\.)?(instagram\.com|tiktok\.com|youtube\.com|youtu\.be|twitter\.com|x\.com|reddit\.com|pinterest\.com|vk\.com)/[^\s]+");
            if (!linkMatch.Success) return;

            string mediaUrl = linkMatch.Value;

            var loadingMessage = await bot.SendMessage(
                chatId: update.Message.Chat.Id,
                text: "Скачиваю медиа...",
                replyParameters: update.Message.MessageId,
                cancellationToken: ct
            );

            try
            {
                var requestBody = new
                {
                    url = mediaUrl,
                    videoQuality = "720",
                    filenameStyle = "basic",
                    downloadMode = "auto"
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                string jsonPayload = JsonSerializer.Serialize(requestBody, jsonOptions);

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

                if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "error")
                {
                    string textError = "Неизвестная ошибка Cobalt";
                    if (root.TryGetProperty("error", out var errorObj) && errorObj.TryGetProperty("code", out var codeProp))
                    {
                        textError = codeProp.GetString() ?? textError;
                    }
                    throw new Exception(textError);
                }

                if (root.TryGetProperty("url", out var urlProp))
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

                throw new Exception("Не удалось получить прямую ссылку на видео.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cobalt Error]: {ex.Message}");
                await bot.EditMessageText(
                    chatId: update.Message.Chat.Id,
                    messageId: loadingMessage.MessageId,
                    text: $"Ошибка скачивания: {ex.Message}",
                    cancellationToken: ct
                );
            }
        }
    }
}
