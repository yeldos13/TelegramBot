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
            "💱 Курсы валют — напиши сумму с валютой: «100$», «5000 тг», «20 евро», «15 GBP», «0.1 btc», «50 usdt», " +
            "и я пересчитаю её в популярные валюты.\n" +
            "/rates — курсы валют и криптовалют на сегодня\n" +
            "\n" +
            "🎬 Медиа — кинь ссылку, и я заменю сообщение на видео или фото с подписью «имя: ссылка» (и твоим текстом, если он был). " +
            "Допиши soy — пришлю без подписи ответом, а твоё сообщение оставлю:\n" +
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
            "/stats — твоя статистика за неделю\n" +
            "\n" +
            "📊 По воскресеньям в 20:00 — итоги недели.\n" +
            "\n" +
            "🛡 Антиспам: 7 одинаковых сообщений за 2 минуты или 15 сообщений за 30 секунд — удаляю их и даю бессрочный мут. " +
            "Ссылки от новичков в первые сутки удаляю.";

        private string _botUsername = "";

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (BotCommands.Parse(message.Text, _botUsername)?.Command != "help")
                return false;

            var sent = await bot.SendMessage(message.Chat.Id, Text, replyParameters: message.MessageId, cancellationToken: ct);
            Cleanup.DeleteCommandLater(bot, message, sent);
            return true;
        }
    }
}
