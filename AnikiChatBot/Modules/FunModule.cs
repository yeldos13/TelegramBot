using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class FunModule
    {
        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "roll", Description = "Случайное число 1–100 (или /roll 6)" },
            new BotCommand { Command = "coin", Description = "Подбросить монетку" },
            new BotCommand { Command = "who", Description = "Выбрать случайного участника: /who кто сегодня платит" },
        ];

        private readonly StatsService _stats;
        private readonly Random _random;
        private string _botUsername = "";

        public FunModule(StatsService stats, Random? random = null)
        {
            _stats = stats;
            _random = random ?? Random.Shared;
        }

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (PenisModule.ParseCommand(message.Text, _botUsername) is not { } parsed)
                return false;

            string? reply = parsed.Command switch
            {
                "roll" => Roll(parsed.Args),
                "coin" => Coin(),
                "who" => Who(message.Chat.Id, parsed.Args),
                _ => null
            };

            if (reply == null)
                return false;

            await bot.SendMessage(message.Chat.Id, reply, replyParameters: message.MessageId, cancellationToken: ct);
            return true;
        }

        internal string Roll(string args)
        {
            int max = int.TryParse(args, out int n) && n >= 2 ? Math.Min(n, 1_000_000) : 100;
            return $"🎲 {_random.Next(1, max + 1)} (из {max})";
        }

        internal string Coin() => _random.Next(2) == 0 ? "🪙 Орёл" : "🪙 Решка";

        internal string Who(long chatId, string question)
        {
            var names = _stats.GetActiveNames(chatId);
            if (names.Count == 0)
                return "Не из кого выбирать — на этой неделе в чате ещё никто не писал.";

            string winner = names[_random.Next(names.Count)];
            question = question.Trim().TrimEnd('?');
            return string.IsNullOrEmpty(question)
                ? $"🎯 {winner}"
                : $"🎯 {question}? — {winner}";
        }
    }
}
