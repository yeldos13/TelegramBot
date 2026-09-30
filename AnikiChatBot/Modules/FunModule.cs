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
            new BotCommand { Command = "stats", Description = "Моя статистика за неделю" },
        ];

        private readonly StatsService _stats;
        private readonly MuteStore? _mutes;
        private readonly Random _random;
        private string _botUsername = "";

        public FunModule(StatsService stats, Random? random = null, MuteStore? mutes = null)
        {
            _stats = stats;
            _random = random ?? Random.Shared;
            _mutes = mutes;
        }

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (BotCommands.Parse(message.Text, _botUsername) is not { } parsed)
                return false;

            string? reply = parsed.Command switch
            {
                "roll" => Roll(parsed.Args),
                "coin" => Coin(),
                "who" => Who(message.Chat.Id, parsed.Args),
                "stats" => message.From is { } user ? Stats(message.Chat.Id, user) : null,
                _ => null
            };

            if (reply == null)
                return false;

            await bot.SendMessage(message.Chat.Id, reply, replyParameters: message.MessageId, cancellationToken: ct);
            return true;
        }

        internal string Stats(long chatId, User user)
        {
            var chat = _stats.GetChat(chatId);
            string name = Users.DisplayName(user);

            var ranked = chat.Players
                .Where(p => p.Value.Messages > 0)
                .OrderByDescending(p => p.Value.Messages)
                .Select(p => p.Key)
                .ToList();

            if (!chat.Players.TryGetValue(user.Id, out var me) || me.Messages == 0)
                return $"📊 {name}, на этой неделе ты ещё не писал(а) в чат.";

            int place = ranked.IndexOf(user.Id) + 1;
            var lines = new List<string>
            {
                $"📊 {name}, твоя неделя (с {_stats.PeriodStart:dd.MM}):",
                $"💬 Сообщений: {me.Messages} — {place}-е место из {ranked.Count}"
            };

            if (me.Growth != 0 || me.DuelWins > 0)
                lines.Add($"🍆 Игра: {(me.Growth > 0 ? "+" : "")}{me.Growth} см, побед в дуэлях: {me.DuelWins}");

            return string.Join("\n", lines);
        }

        internal string Roll(string args)
        {
            int max = int.TryParse(args, out int n) && n >= 2 ? Math.Min(n, 1_000_000) : 100;
            return $"🎲 {_random.Next(1, max + 1)} (из {max})";
        }

        internal string Coin() => _random.Next(2) == 0 ? "🪙 Орёл" : "🪙 Решка";

        internal string Who(long chatId, string question)
        {
            var muted = _mutes?.List(chatId).Select(m => m.UserId).ToHashSet() ?? [];
            var names = _stats.GetActivePlayers(chatId).Where(p => !muted.Contains(p.UserId)).Select(p => p.Name).ToList();
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
