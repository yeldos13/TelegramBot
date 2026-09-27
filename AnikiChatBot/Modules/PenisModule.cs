using AnikiChatBot.Modules.Penis;
using System.Collections.Concurrent;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace AnikiChatBot.Modules
{
    public class PenisModule
    {
        private const int TopSize = 50;
        private const int MaxNameLength = 64;
        private static readonly TimeSpan DuelLifetime = TimeSpan.FromMinutes(10);

        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "grow", Description = "Вырастить пенис (раз в день)" },
            new BotCommand { Command = "my", Description = "Мой пенис" },
            new BotCommand { Command = "top", Description = "Топ пенисов чата" },
            new BotCommand { Command = "name", Description = "Дать пенису имя" },
            new BotCommand { Command = "duel", Description = "Вызвать на дуэль (можно ответом на сообщение)" },
            new BotCommand { Command = "penisday", Description = "Выбрать пенис дня" },
            new BotCommand { Command = "achievements", Description = "Достижения" },
        ];

        private record PendingDuel(string Id, long ChatId, long ChallengerId, long? TargetId, DateTime CreatedAt);

        private readonly PenisStore _store;
        private readonly Random _random;
        private readonly ConcurrentDictionary<string, PendingDuel> _duels = new();
        private string _botUsername = "";

        public PenisModule(PenisStore store, Random? random = null)
        {
            _store = store;
            _random = random ?? Random.Shared;
        }

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (message.From is not { } user || ParseCommand(message.Text, _botUsername) is not { } parsed)
                return false;

            string? reply = parsed.Command switch
            {
                "grow" => Grow(message.Chat.Id, user),
                "my" => My(message.Chat.Id, user),
                "top" => Top(message.Chat.Id),
                "name" => SetName(message.Chat.Id, user, parsed.Args),
                "achievements" => Achievements(user),
                "penisday" => PenisOfDay(message.Chat.Id),
                "duel" => null,
                _ => ""
            };

            if (reply == "")
                return false;

            if (parsed.Command == "duel")
            {
                await StartDuelAsync(bot, message, user, ct);
                return true;
            }

            await bot.SendMessage(message.Chat.Id, reply!, replyParameters: message.MessageId, cancellationToken: ct);
            return true;
        }

        public record ParsedCommand(string Command, string Args);

        public static ParsedCommand? ParseCommand(string? text, string botUsername)
        {
            if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
                return null;

            int space = text.IndexOfAny([' ', '\n']);
            string head = space < 0 ? text[1..] : text[1..space];
            string args = space < 0 ? "" : text[(space + 1)..].Trim();

            int at = head.IndexOf('@');
            if (at >= 0)
            {
                if (!string.Equals(head[(at + 1)..], botUsername, StringComparison.OrdinalIgnoreCase))
                    return null;
                head = head[..at];
            }

            return new ParsedCommand(head.ToLowerInvariant(), args);
        }

        public static string DisplayName(User user)
        {
            string name = string.Join(" ", new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(name) ? user.Username ?? "Аноним" : name;
        }

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
                        Size = PenisGame.RollInitialSize(_random),
                        LastGrow = today,
                        Streak = 1
                    };
                    chat.Players[user.Id] = player;

                    int chats = data.Chats.Values.Count(c => c.Players.ContainsKey(user.Id));
                    var chatAchievements = Award(data, user.Id, PenisAchievements.CheckChats(chats));

                    return $"🍆 {name}, у тебя появился пенис! Его размер — {player.Size} см.\n" +
                           "Расти его командой /grow раз в день." + FormatAchievements(chatAchievements);
                }

                player.DisplayName = name;

                if (!PenisGame.CanGrow(player, today))
                {
                    var wait = PenisGame.TimeUntilNextGrow(now);
                    return $"{name}, ты уже рос сегодня ({player.Size} см). Следующая попытка через {FormatWait(wait)}.";
                }

                int oldSize = player.Size;
                PenisGame.ApplyGrow(player, today, PenisGame.RollDelta(_random));
                int change = player.Size - oldSize;

                string result = change switch
                {
                    > 0 => $"🍆 {name}, твой пенис вырос на {change} см.",
                    < 0 => $"🥀 {name}, твой пенис уменьшился на {-change} см.",
                    _ => $"😐 {name}, твой пенис не изменился."
                };

                var earned = Award(data, user.Id, PenisAchievements.CheckAfterGrow(player, now));
                return $"{result}\nТеперь он {player.Size} см.\nСледующая попытка — завтра." + FormatAchievements(earned);
            });
        }

        internal string My(long chatId, User user)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            return _store.Read(data =>
            {
                if (!data.Chats.TryGetValue(chatId, out var chat) || !chat.Players.TryGetValue(user.Id, out var player))
                    return "У тебя пока нет пениса. Напиши /grow";

                var ranked = chat.Players.Values.OrderByDescending(p => p.Size).ToList();
                int place = ranked.FindIndex(p => p.UserId == user.Id) + 1;
                int achievements = data.Achievements.GetValueOrDefault(user.Id)?.Count ?? 0;

                var sb = new StringBuilder();
                sb.AppendLine($"🍆 {DisplayName(user)}");
                sb.AppendLine($"Имя: {player.Name ?? "без имени (/name)"}");
                sb.AppendLine($"Размер: {player.Size} см");
                sb.AppendLine($"Место в чате: {place} из {ranked.Count}");
                sb.AppendLine($"Дней подряд: {player.Streak}");

                if (player.History.Count > 0)
                    sb.AppendLine($"Последнее изменение: {FormatDelta(player.History[^1].Delta)} см");

                sb.AppendLine($"Достижений: {achievements} из {PenisAchievements.All.Count}");
                sb.Append(PenisGame.CanGrow(player, today)
                    ? "Сегодня ещё можно /grow"
                    : $"Следующий /grow через {FormatWait(PenisGame.TimeUntilNextGrow(DateTime.Now))}");

                return sb.ToString();
            });
        }

        internal string Top(long chatId)
        {
            return _store.Read(data =>
            {
                if (!data.Chats.TryGetValue(chatId, out var chat) || chat.Players.Count == 0)
                    return "Пока никто не растил. Напиши /grow";

                var sb = new StringBuilder("🏆 Топ пенисов чата:\n\n");
                int place = 0;

                foreach (var player in chat.Players.Values.OrderByDescending(p => p.Size).Take(TopSize))
                {
                    place++;
                    string medal = place switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"{place}." };
                    string name = player.Name != null ? $" «{player.Name}»" : "";
                    sb.AppendLine($"{medal} {player.DisplayName}{name} — {player.Size} см");
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
                    return "У тебя пока нет пениса. Напиши /grow";

                if (newName.Length == 0)
                    return player.Name == null
                        ? "У твоего пениса нет имени. Задай его: /name Имя"
                        : $"Твой пенис зовут «{player.Name}». Сменить: /name Новое имя";

                if (newName.Length > MaxNameLength)
                    return $"Слишком длинное имя — максимум {MaxNameLength} символа.";

                player.Name = newName;
                var earned = Award(data, user.Id, [PenisAchievements.Named]);
                return $"🏷️ Теперь твой пенис зовут «{newName}»." + FormatAchievements(earned);
            });
        }

        internal string Achievements(User user)
        {
            return _store.Read(data =>
            {
                var owned = data.Achievements.GetValueOrDefault(user.Id) ?? [];
                var sb = new StringBuilder($"🏆 Достижения {DisplayName(user)}: {owned.Count} из {PenisAchievements.All.Count}\n\n");

                foreach (var a in PenisAchievements.All)
                    sb.AppendLine($"{(owned.Contains(a.Id) ? "✅" : "▫️")} {a.Title} — {a.Description}");

                return sb.ToString().TrimEnd();
            });
        }

        internal string PenisOfDay(long chatId)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            return _store.Update(data =>
            {
                var chat = GetChat(data, chatId);

                if (chat.PenisOfDayDate == today && chat.PenisOfDayUserId is { } chosenId
                    && chat.Players.TryGetValue(chosenId, out var chosen))
                    return $"🌞 Пенис дня уже выбран: {chosen.DisplayName} ({chosen.Size} см). Следующий — завтра.";

                if (chat.Players.Count == 0)
                    return "В чате ещё никто не растил пенис. Напиши /grow";

                var players = chat.Players.Values.ToList();
                var winner = players[_random.Next(players.Count)];
                chat.PenisOfDayDate = today;
                chat.PenisOfDayUserId = winner.UserId;

                var earned = Award(data, winner.UserId, [PenisAchievements.PenisOfDay]);
                return $"🌞 Пенис дня сегодня — {winner.DisplayName} ({winner.Size} см)!" + FormatAchievements(earned);
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
                error = "У тебя пока нет пениса. Напиши /grow";
            else if (_duels.Values.Any(d => d.ChatId == chatId && d.ChallengerId == user.Id))
                error = "У тебя уже есть открытый вызов — дождись, пока его примут.";

            if (error != null)
            {
                await bot.SendMessage(chatId, error, replyParameters: message.MessageId, cancellationToken: ct);
                return;
            }

            var duel = new PendingDuel(Guid.NewGuid().ToString("N")[..12], chatId, user.Id, target?.Id, DateTime.UtcNow);
            _duels[duel.Id] = duel;

            string whom = target != null ? DisplayName(target) : "любого желающего";
            await bot.SendMessage(chatId,
                $"⚔️ {DisplayName(user)} ({challenger!.Size} см) вызывает на дуэль {whom}!\n" +
                $"Вызов действует {DuelLifetime.TotalMinutes:0} минут.",
                replyParameters: message.MessageId,
                replyMarkup: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⚔️ Принять", $"duel:{duel.Id}")),
                cancellationToken: ct);
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
                return;
            }

            string? refusal = null;
            if (acceptor.Id == duel.ChallengerId)
                refusal = "Нельзя принять свой вызов";
            else if (duel.TargetId != null && duel.TargetId != acceptor.Id)
                refusal = "Этот вызов не тебе";
            else if (!_store.Read(data => data.Chats.GetValueOrDefault(duel.ChatId)?.Players.ContainsKey(acceptor.Id) == true))
                refusal = "Сначала вырасти пенис: /grow";

            if (refusal != null || !_duels.TryRemove(id, out _))
            {
                await bot.AnswerCallbackQuery(query.Id, refusal ?? "Вызов уже приняли", showAlert: refusal != null, cancellationToken: ct);
                return;
            }

            string text = _store.Update(data =>
            {
                var chat = GetChat(data, duel.ChatId);
                var a = chat.Players[duel.ChallengerId];
                var b = chat.Players[acceptor.Id];
                b.DisplayName = DisplayName(acceptor);

                var result = PenisGame.ResolveDuel(a.Size, b.Size, _random);
                var (winner, loser) = result.ChallengerWins ? (a, b) : (b, a);

                string header = $"⚔️ Дуэль: {a.DisplayName} ({a.Size} см) vs {b.DisplayName} ({b.Size} см)";
                winner.Size += result.Transfer;
                loser.Size -= result.Transfer;

                string hit = result.Hit switch
                {
                    DuelHit.Critical => " Критический удар! 💥",
                    DuelHit.Knockout => " Нокаут! 🥊",
                    _ => ""
                };

                string outcome = result.Transfer > 0
                    ? $"🏆 Победил {winner.DisplayName} и отнял {result.Transfer} см.{hit}"
                    : $"🏆 Победил {winner.DisplayName}, но отнимать у соперника уже нечего.";

                return $"{header}\n{outcome}\n\n{a.DisplayName}: {a.Size} см · {b.DisplayName}: {b.Size} см";
            });

            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            await bot.EditMessageText(message.Chat.Id, message.MessageId, text, cancellationToken: ct);
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

            return ids.Where(owned.Add).Select(PenisAchievements.Get).ToList();
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
