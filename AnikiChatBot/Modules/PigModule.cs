using AnikiChatBot.Modules.Pig;
using System.Collections.Concurrent;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace AnikiChatBot.Modules
{
    public class PigModule
    {
        private const int TopSize = 50;
        private const int MaxNameLength = 64;
        private static readonly TimeSpan DuelLifetime = TimeSpan.FromMinutes(10);

        private static readonly TimeSpan AutoDeleteDelay = Cleanup.CommandDelay;
        private static readonly HashSet<string> KeptCommands = ["top", "pigday"];

        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "grow", Description = "Откормить свинью (раз в день)" },
            new BotCommand { Command = "my", Description = "Моя свинья" },
            new BotCommand { Command = "top", Description = "Топ свиней чата" },
            new BotCommand { Command = "name", Description = "Дать свинье имя" },
            new BotCommand { Command = "duel", Description = "Вызвать на дуэль (можно ответом на сообщение)" },
            new BotCommand { Command = "pigday", Description = "Выбрать свинью дня" },
            new BotCommand { Command = "give", Description = "Подарить тонны: /give 10 ответом на сообщение" },
            new BotCommand { Command = "achievements", Description = "Достижения" },
        ];

        private record PendingDuel(string Id, long ChatId, long ChallengerId, long? TargetId, DateTime CreatedAt);

        private readonly PigStore _store;
        private readonly Random _random;
        private readonly ConcurrentDictionary<string, PendingDuel> _duels = new();
        private string _botUsername = "";

        private readonly StatsService? _stats;

        public PigModule(PigStore store, Random? random = null, StatsService? stats = null)
        {
            _store = store;
            _random = random ?? Random.Shared;
            _stats = stats;
        }

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (message.From is not { } user || BotCommands.Parse(message.Text, _botUsername) is not { } parsed)
                return false;

            if (parsed.Command == "duel")
            {
                await StartDuelAsync(bot, message, user, ct);
                return true;
            }

            string? reply = parsed.Command switch
            {
                "grow" => Grow(message.Chat.Id, user),
                "my" => My(message.Chat.Id, user),
                "top" => Top(message.Chat.Id),
                "name" => SetName(message.Chat.Id, user, parsed.Args),
                "achievements" => Achievements(user),
                "pigday" => PigOfDay(message.Chat.Id),
                "give" => Give(message.Chat.Id, user, message.ReplyToMessage?.From, parsed.Args),
                _ => null
            };

            if (reply == null)
                return false;

            var sent = await bot.SendMessage(message.Chat.Id, reply, replyParameters: message.MessageId, cancellationToken: ct);

            if (!KeptCommands.Contains(parsed.Command))
                ScheduleDelete(bot, message.Chat.Id, AutoDeleteDelay, message.MessageId, sent.MessageId);

            return true;
        }

        private static void ScheduleDelete(ITelegramBotClient bot, long chatId, TimeSpan delay, params int[] messageIds) =>
            Cleanup.DeleteLater(bot, chatId, delay, messageIds);

        private static string DisplayName(User user) => Users.DisplayName(user);

        internal string Grow(long chatId, User user)
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            string name = DisplayName(user);

            return _store.Update(data =>
            {
                var chat = GetChat(data, chatId);

                if (!chat.Players.TryGetValue(user.Id, out var player))
                {
                    player = new Player
                    {
                        UserId = user.Id,
                        DisplayName = name,
                        Size = PigGame.RollInitialSize(_random),
                        LastGrow = today,
                        Streak = 1
                    };
                    chat.Players[user.Id] = player;

                    int chats = data.Chats.Values.Count(c => c.Players.ContainsKey(user.Id));
                    var chatAchievements = Award(data, user.Id, PigAchievements.CheckChats(chats));

                    return $"🐷 {name}, у тебя появилась свинья! Она весит {player.Size} т.\n" +
                           "Откармливай её командой /grow раз в день." + FormatAchievements(chatAchievements);
                }

                player.DisplayName = name;

                if (!PigGame.CanGrow(player, today))
                {
                    var wait = PigGame.TimeUntilNextGrow(now);
                    return $"{name}, ты уже кормил свинью сегодня ({player.Size} т). Следующая попытка через {FormatWait(wait)}.";
                }

                int oldSize = player.Size;
                PigGame.ApplyGrow(player, today, PigGame.RollDelta(_random));
                int change = player.Size - oldSize;

                string result = change switch
                {
                    > 0 => $"🐷 {name}, твоя свинья поправилась на {change} т.",
                    < 0 => $"🥓 {name}, твоя свинья похудела на {-change} т.",
                    _ => $"😐 {name}, вес твоей свиньи не изменился."
                };

                _stats?.RecordGrowth(chatId, user.Id, name, change);
                var earned = Award(data, user.Id, PigAchievements.CheckAfterGrow(player, now));
                return $"{result}\nТеперь она весит {player.Size} т.\nСледующая попытка — завтра." + FormatAchievements(earned);
            });
        }

        internal string My(long chatId, User user)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            return _store.Read(data =>
            {
                if (!data.Chats.TryGetValue(chatId, out var chat) || !chat.Players.TryGetValue(user.Id, out var player))
                    return "У тебя пока нет свиньи. Напиши /grow";

                var ranked = chat.Players.Values.OrderByDescending(p => p.Size).ToList();
                int place = ranked.FindIndex(p => p.UserId == user.Id) + 1;
                int achievements = data.Achievements.GetValueOrDefault(user.Id)?.Count ?? 0;

                var sb = new StringBuilder();
                sb.AppendLine($"🐷 {DisplayName(user)}");
                sb.AppendLine($"Имя: {player.Name ?? "без имени (/name)"}");
                sb.AppendLine($"Вес: {player.Size} т");
                sb.AppendLine($"Место в чате: {place} из {ranked.Count}");
                sb.AppendLine($"Дней подряд: {player.Streak}");

                if (player.History.Count > 0)
                    sb.AppendLine($"Последнее изменение: {FormatDelta(player.History[^1].Delta)} т");

                sb.AppendLine($"Дуэли: {player.DuelWins} побед, {player.DuelLosses} поражений");
                sb.AppendLine($"Достижений: {achievements} из {PigAchievements.All.Count}");
                sb.Append(PigGame.CanGrow(player, today)
                    ? "Сегодня ещё можно /grow"
                    : $"Следующий /grow через {FormatWait(PigGame.TimeUntilNextGrow(DateTime.Now))}");

                return sb.ToString();
            });
        }

        internal string Top(long chatId)
        {
            return _store.Read(data =>
            {
                if (!data.Chats.TryGetValue(chatId, out var chat) || chat.Players.Count == 0)
                    return "Пока никто не завёл свинью. Напиши /grow";

                var sb = new StringBuilder("🏆 Топ свиней чата:\n\n");
                int place = 0;

                foreach (var player in chat.Players.Values.OrderByDescending(p => p.Size).Take(TopSize))
                {
                    place++;
                    string medal = place switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"{place}." };
                    string name = player.Name != null ? $" «{player.Name}»" : "";
                    sb.AppendLine($"{medal} {player.DisplayName}{name} — {player.Size} т");
                }

                return sb.ToString().TrimEnd();
            });
        }

        internal string SetName(long chatId, User user, string args)
        {
            string newName = args.Replace('\n', ' ').Replace("\r", "").Trim();

            return _store.Update(data =>
            {
                if (!data.Chats.TryGetValue(chatId, out var chat) || !chat.Players.TryGetValue(user.Id, out var player))
                    return "У тебя пока нет свиньи. Напиши /grow";

                if (newName.Length == 0)
                    return player.Name == null
                        ? "У твоей свиньи нет имени. Задай его: /name Имя"
                        : $"Твою свинью зовут «{player.Name}». Сменить: /name Новое имя";

                if (newName.Length > MaxNameLength)
                    return $"Слишком длинное имя — максимум {MaxNameLength} символа.";

                player.Name = newName;
                var earned = Award(data, user.Id, [PigAchievements.Named]);
                return $"🏷️ Теперь твою свинью зовут «{newName}»." + FormatAchievements(earned);
            });
        }

        internal string Achievements(User user)
        {
            return _store.Read(data =>
            {
                var owned = data.Achievements.GetValueOrDefault(user.Id) ?? [];
                var sb = new StringBuilder($"🏆 Достижения {DisplayName(user)}: {owned.Count} из {PigAchievements.All.Count}\n\n");

                foreach (var a in PigAchievements.All)
                    sb.AppendLine($"{(owned.Contains(a.Id) ? "✅" : "▫️")} {a.Title} — {a.Description}");

                return sb.ToString().TrimEnd();
            });
        }

        internal string PigOfDay(long chatId)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            return _store.Update(data =>
            {
                var chat = GetChat(data, chatId);

                if (chat.PigOfDayDate == today && chat.PigOfDayUserId is { } chosenId
                    && chat.Players.TryGetValue(chosenId, out var chosen))
                    return $"🌞 Свинья дня уже выбрана: {chosen.DisplayName} ({chosen.Size} т). Следующая — завтра.";

                if (chat.Players.Count == 0)
                    return "В чате ещё никто не завёл свинью. Напиши /grow";

                var players = chat.Players.Values.ToList();
                var winner = players[_random.Next(players.Count)];
                chat.PigOfDayDate = today;
                chat.PigOfDayUserId = winner.UserId;

                var earned = Award(data, winner.UserId, [PigAchievements.PigOfDay]);
                return $"🌞 Свинья дня сегодня — {winner.DisplayName} ({winner.Size} т)!" + FormatAchievements(earned);
            });
        }

        private async Task StartDuelAsync(ITelegramBotClient bot, Message message, User user, CancellationToken ct)
        {
            long chatId = message.Chat.Id;
            RemoveExpiredDuels();

            var challenger = _store.Read(data =>
                data.Chats.GetValueOrDefault(chatId)?.Players.GetValueOrDefault(user.Id));

            string? error = null;
            User? target = message.ReplyToMessage?.From;
            if (target != null && (target.IsBot || target.Id == user.Id))
                target = null;

            if (challenger == null)
                error = "У тебя пока нет свиньи. Напиши /grow";
            else if (_duels.Values.Any(d => d.ChatId == chatId && d.ChallengerId == user.Id))
                error = "У тебя уже есть открытый вызов — дождись, пока его примут.";

            if (error != null)
            {
                var errorMessage = await bot.SendMessage(chatId, error, replyParameters: message.MessageId, cancellationToken: ct);
                ScheduleDelete(bot, chatId, AutoDeleteDelay, message.MessageId, errorMessage.MessageId);
                return;
            }

            var duel = new PendingDuel(Guid.NewGuid().ToString("N")[..12], chatId, user.Id, target?.Id, DateTime.UtcNow);
            _duels[duel.Id] = duel;

            string whom = target != null ? DisplayName(target) : "любого желающего";
            var challenge = await bot.SendMessage(chatId,
                $"⚔️ {DisplayName(user)} ({challenger!.Size} т) вызывает на дуэль {whom}!\n" +
                $"Вызов действует {DuelLifetime.TotalMinutes:0} минут.",
                replyParameters: message.MessageId,
                replyMarkup: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⚔️ Принять", $"duel:{duel.Id}")),
                cancellationToken: ct);

            ScheduleDelete(bot, chatId, AutoDeleteDelay, message.MessageId);
            _ = Task.Run(async () =>
            {
                await Task.Delay(DuelLifetime);
                if (_duels.TryRemove(duel.Id, out _))
                    ScheduleDelete(bot, chatId, TimeSpan.Zero, challenge.MessageId);
            });
        }

        public async Task HandleCallback(ITelegramBotClient bot, CallbackQuery query, CancellationToken ct)
        {
            if (query.Data?.StartsWith("duel:") != true || query.Message is not { } message)
                return;

            string id = query.Data["duel:".Length..];
            var acceptor = query.From;

            if (!_duels.TryGetValue(id, out var duel) || DateTime.UtcNow - duel.CreatedAt > DuelLifetime)
            {
                _duels.TryRemove(id, out _);
                await bot.AnswerCallbackQuery(query.Id, "Вызов устарел", cancellationToken: ct);
                await bot.EditMessageText(message.Chat.Id, message.MessageId, message.Text + "\n\n⌛ Вызов устарел.", cancellationToken: ct);
                ScheduleDelete(bot, message.Chat.Id, AutoDeleteDelay, message.MessageId);
                return;
            }

            string? refusal = null;
            if (acceptor.Id == duel.ChallengerId)
                refusal = "Нельзя принять свой вызов";
            else if (duel.TargetId != null && duel.TargetId != acceptor.Id)
                refusal = "Этот вызов не тебе";
            else if (!_store.Read(data => data.Chats.GetValueOrDefault(duel.ChatId)?.Players.ContainsKey(acceptor.Id) == true))
                refusal = "Сначала заведи свинью: /grow";

            if (refusal != null || !_duels.TryRemove(id, out _))
            {
                await bot.AnswerCallbackQuery(query.Id, refusal ?? "Вызов уже приняли", showAlert: refusal != null, cancellationToken: ct);
                return;
            }

            string text = PlayDuel(duel.ChatId, duel.ChallengerId, acceptor);

            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            await bot.EditMessageText(message.Chat.Id, message.MessageId, text, cancellationToken: ct);
            ScheduleDelete(bot, message.Chat.Id, AutoDeleteDelay, message.MessageId);
        }

        internal string Give(long chatId, User user, User? target, string args)
        {
            if (target == null || target.IsBot || target.Id == user.Id)
                return "Ответь /give 10 на сообщение того, кому даришь.";

            if (!int.TryParse(args.Trim(), out int amount) || amount <= 0)
                return "Укажи, сколько подарить: /give 10";

            return _store.Update(data =>
            {
                var chat = GetChat(data, chatId);

                if (!chat.Players.TryGetValue(user.Id, out var giver))
                    return "У тебя пока нет свиньи. Напиши /grow";

                if (!chat.Players.TryGetValue(target.Id, out var receiver))
                    return $"У {DisplayName(target)} ещё нет свиньи — пусть сначала напишет /grow.";

                int canGive = giver.Size - PigGame.MinSize;
                if (amount > canGive)
                    return canGive > 0
                        ? $"Столько нет — можно подарить не больше {canGive} т."
                        : "Дарить нечего — у тебя минимальный вес.";

                giver.DisplayName = DisplayName(user);
                receiver.DisplayName = DisplayName(target);
                giver.Size -= amount;
                receiver.Size += amount;

                _stats?.RecordGrowth(chatId, giver.UserId, giver.DisplayName, -amount);
                _stats?.RecordGrowth(chatId, receiver.UserId, receiver.DisplayName, amount);

                return $"🎁 {giver.DisplayName} дарит {receiver.DisplayName} {amount} т.\n" +
                       $"{giver.DisplayName}: {giver.Size} т · {receiver.DisplayName}: {receiver.Size} т";
            });
        }

        internal string PlayDuel(long chatId, long challengerId, User acceptor)
        {
            return _store.Update(data =>
            {
                var chat = GetChat(data, chatId);
                var a = chat.Players[challengerId];
                var b = chat.Players[acceptor.Id];
                b.DisplayName = DisplayName(acceptor);

                var result = PigGame.ResolveDuel(a.Size, b.Size, _random);
                var (winner, loser) = result.ChallengerWins ? (a, b) : (b, a);
                int winnerBefore = winner.Size, loserBefore = loser.Size;

                string header = $"⚔️ Дуэль: {a.DisplayName} ({a.Size} т) vs {b.DisplayName} ({b.Size} т)";
                winner.Size += result.Transfer;
                loser.Size -= result.Transfer;
                winner.DuelWins++;
                loser.DuelLosses++;

                _stats?.RecordGrowth(chatId, winner.UserId, winner.DisplayName, result.Transfer);
                _stats?.RecordGrowth(chatId, loser.UserId, loser.DisplayName, -result.Transfer);
                _stats?.RecordDuelWin(chatId, winner.UserId, winner.DisplayName);

                string hit = result.Hit switch
                {
                    DuelHit.Critical => " Критический удар! 💥",
                    DuelHit.Knockout => " Нокаут! 🥊",
                    _ => ""
                };

                string outcome = result.Transfer > 0
                    ? $"🏆 Победил {winner.DisplayName} и отнял {result.Transfer} т.{hit}"
                    : $"🏆 Победил {winner.DisplayName}, но отнимать у соперника уже нечего.";

                var earned = Award(data, winner.UserId,
                    PigAchievements.CheckAfterDuelWin(winner, winnerBefore, loserBefore, result.Hit));

                return $"{header}\n{outcome}\n\n{a.DisplayName}: {a.Size} т · {b.DisplayName}: {b.Size} т"
                    + FormatAchievements(earned);
            });
        }

        public StatsService.GameLeader? GetLeader(long chatId)
        {
            return _store.Read(data =>
            {
                var top = data.Chats.GetValueOrDefault(chatId)?.Players.Values.MaxBy(p => p.Size);
                return top == null ? null : new StatsService.GameLeader(top.DisplayName, top.Size);
            });
        }

        private void RemoveExpiredDuels()
        {
            foreach (var duel in _duels.Values.Where(d => DateTime.UtcNow - d.CreatedAt > DuelLifetime).ToList())
                _duels.TryRemove(duel.Id, out _);
        }

        private static ChatGame GetChat(GameData data, long chatId)
        {
            if (!data.Chats.TryGetValue(chatId, out var chat))
                data.Chats[chatId] = chat = new ChatGame();
            return chat;
        }

        private static List<Achievement> Award(GameData data, long userId, IEnumerable<string> ids)
        {
            if (!data.Achievements.TryGetValue(userId, out var owned))
                data.Achievements[userId] = owned = new HashSet<string>();

            return ids.Where(owned.Add).Select(PigAchievements.Get).ToList();
        }

        private static string FormatAchievements(List<Achievement> earned)
        {
            if (earned.Count == 0)
                return "";

            return "\n\n" + string.Join("\n", earned.Select(a => $"🏆 Новое достижение: {a.Title} — {a.Description}"));
        }

        private static string FormatDelta(int delta) => delta > 0 ? $"+{delta}" : delta.ToString();

        public static string FormatWait(TimeSpan wait)
        {
            if (wait.TotalHours >= 1)
                return $"{(int)wait.TotalHours} ч {wait.Minutes} мин";
            return $"{Math.Max(1, wait.Minutes)} мин";
        }
    }
}
