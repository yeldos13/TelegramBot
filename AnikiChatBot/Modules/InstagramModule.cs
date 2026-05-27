using System.Text.RegularExpressions;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class InstagramModule
    {
        public HttpClient httpClient { get; set; }

        public async Task HandleInstagramCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text == null) return;

            string messageText = update.Message.Text;

            var linkMatch = Regex.Match(messageText, @"https?://(www\.)?instagram\.com/(reel|p)/[^?\s]+");

            if (!linkMatch.Success) return;

            string instagramUrl = linkMatch.Value;

            try
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Instagram Error]: {ex.Message}");
                await bot.EditMessageText(
                    chatId: update.Message.Chat.Id,
                    messageId: update.Message.Id,
                    text: "Ошибка при скачивании видео",
                    cancellationToken: ct
                );
            }
        }
    }
}
