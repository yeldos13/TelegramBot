using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class HelpModule
    {
        public static readonly BotCommand Command = new BotCommand { Command = "help", Description = "Что умеет бот" };

        public const string Text =
            "🤖 Что я умею\n" +
            "\n" +
            "💱 Курсы валют — напиши сумму с валютой: «100$», «5000 тг», «20 евро», «15 GBP», и я пересчитаю её в популярные валюты.\n" +
            "\n" +
            "🎬 Медиа — кинь ссылку, и я пришлю видео или фото:\n" +
            "YouTube Shorts и посты, Instagram reels и посты, TikTok видео и фото, X (Twitter).\n" +
            "\n" +
            "💬 Автоответы — ответь на сообщение, и когда кто-то снова напишет тот же текст, я отвечу так же.\n" +
            "\n" +
            "🍆 Игра «Вырасти пенис»\n" +
            "/grow — вырастить (раз в день)\n" +
            "/my — мой пенис\n" +
            "/top — топ чата\n" +
            "/name Имя — дать пенису имя\n" +
            "/duel — дуэль (ответом на сообщение — вызов конкретному человеку)\n" +
            "/penisday — пенис дня\n" +
            "/give 10 — подарить сантиметры (ответом на сообщение)\n" +
            "/achievements — достижения\n" +
            "\n" +
            "🎲 Развлечения\n" +
            "/roll — случайное число 1–100 (/roll 6 — от 1 до 6)\n" +
            "/coin — орёл или решка\n" +
            "/who кто сегодня платит — выберу случайного участника\n" +
            "\n" +
            "📊 По воскресеньям в 20:00 — итоги недели.\n" +
            "\n" +
            "🛡 Антиспам: 7 одинаковых сообщений за 2 минуты или 15 сообщений за 30 секунд — удаляю их и даю бессрочный мут. " +
            "Ссылки от новичков в первые сутки удаляю.";

        private string _botUsername = "";

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (PenisModule.ParseCommand(message.Text, _botUsername)?.Command != "help")
                return false;

            await bot.SendMessage(message.Chat.Id, Text, replyParameters: message.MessageId, cancellationToken: ct);
            return true;
        }
    }
}
