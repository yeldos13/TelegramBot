using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class NewcomerLinksModule
    {
        public static readonly TimeSpan NewcomerPeriod = TimeSpan.FromHours(24);

        private static readonly Regex LinkRegex = new(
            @"(https?://|www\.|t\.me/|telegram\.me/)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly OwnerNotifier _notifier;
        private readonly StatsService? _stats;

        private readonly Dictionary<long, Dictionary<long, DateTime>> _joined;

        public NewcomerLinksModule(OwnerNotifier notifier, string filePath = "newcomers.json", StatsService? stats = null)
        {
            _notifier = notifier;
            _stats = stats;
            _filePath = filePath;
            _joined = Load() ?? new Dictionary<long, Dictionary<long, DateTime>>();
        }

        public void RecordJoin(long chatId, long userId, DateTime nowUtc)
        {
            lock (_lock)
            {
                if (!_joined.TryGetValue(chatId, out var users))
                    _joined[chatId] = users = new Dictionary<long, DateTime>();

                users[userId] = nowUtc;
                Cleanup(nowUtc);
                Save();
            }
        }

        public bool IsNewcomer(long chatId, long userId, DateTime nowUtc)
        {
            lock (_lock)
                return _joined.GetValueOrDefault(chatId)?.TryGetValue(userId, out var joinedAt) == true
                    && nowUtc - joinedAt < NewcomerPeriod;
        }

        public void HandleJoins(Update update)
        {
            var now = DateTime.UtcNow;
            foreach (var (chat, user) in MembersModule.GetJoinedMembers(update))
                OnJoin(chat, user, now);
        }

        public const int RaidThreshold = 5;
        public static readonly TimeSpan RaidWindow = TimeSpan.FromMinutes(5);

        private record RecentJoin(long ChatId, long UserId, string Name, DateTime At);

        private readonly List<RecentJoin> _recentJoins = new();

        private void OnJoin(Chat chat, User user, DateTime nowUtc)
        {
            lock (_lock)
                if (_joined.GetValueOrDefault(chat.Id)?.TryGetValue(user.Id, out var last) == true && nowUtc - last < TimeSpan.FromMinutes(1))
                    return;

            RecordJoin(chat.Id, user.Id, nowUtc);

            string name = Users.NameWithUsername(user);
            Console.WriteLine($"[Newcomer] {name} вошёл в чат {chat.Id}");
            _stats?.RecordJoined(chat.Id, name);
            _notifier.Notify($"join:{chat.Id}:{user.Id}", BuildJoinNotice(chat.Title ?? chat.Id.ToString(), name, user.Id));

            if (RegisterRecentJoin(chat.Id, chat.Title, user.Id, name, nowUtc) is { } alert)
            {
                Console.WriteLine($"[Newcomer] Наплыв новичков в чате {chat.Id}");
                _notifier.Notify($"raid:{chat.Id}", alert);
            }
        }

        public static string BuildJoinNotice(string chatTitle, string name, long userId) =>
            $"👋 В «{chatTitle}» новый участник: {name} (id {userId})\n" +
            $"Первые {NewcomerPeriod.TotalHours:0} ч его ссылки я буду удалять и пересылать тебе. Снять ограничение: /trust {userId}";

        internal string? RegisterRecentJoin(long chatId, string? chatTitle, long userId, string name, DateTime nowUtc)
        {
            lock (_lock)
            {
                _recentJoins.RemoveAll(j => nowUtc - j.At > RaidWindow || j.ChatId == chatId && j.UserId == userId);
                _recentJoins.Add(new RecentJoin(chatId, userId, name, nowUtc));

                var inChat = _recentJoins.Where(j => j.ChatId == chatId).ToList();
                if (inChat.Count < RaidThreshold)
                    return null;

                return $"🚨 Наплыв в «{chatTitle ?? chatId.ToString()}»: за {RaidWindow.TotalMinutes:0} минут зашли {inChat.Count} человек:\n" +
                       string.Join("\n", inChat.Select(j => "• " + j.Name)) +
                       "\n\nСсылки и рекламу от новичков я и так удаляю. Если это боты — проверь чат.";
            }
        }

        private string _botUsername = "";

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public bool Trust(long userId)
        {
            lock (_lock)
            {
                bool removed = false;
                foreach (var users in _joined.Values)
                    removed |= users.Remove(userId);

                if (removed)
                    Save();
                return removed;
            }
        }

        public string TrustText(long? userId, string? name = null)
        {
            if (userId is not { } id)
                return "Ответь /trust на сообщение новичка или напиши /trust id (id есть в уведомлении о входе).";

            string who = name ?? $"id {id}";
            return Trust(id) ? $"✅ Снял ограничение: ссылки от {who} больше не удаляю." : $"{who} и так без ограничений.";
        }

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (BotCommands.Parse(message.Text, _botUsername) is not { Command: "trust" } command)
                return false;

            string text;
            if (!_notifier.IsOwner(message.From))
                text = "Эта команда только для владельца бота.";
            else if (message.ReplyToMessage?.From is { IsBot: false } target)
                text = TrustText(target.Id, Users.NameWithUsername(target));
            else
                text = TrustText(long.TryParse(command.Args, out long id) ? id : null);

            var sent = await bot.SendMessage(message.Chat.Id, text, replyParameters: message.MessageId, cancellationToken: ct);
            AnikiChatBot.Cleanup.DeleteCommandLater(bot, message, sent);
            return true;
        }

        public async Task<bool> HandleMessage(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (message.From is not { IsBot: false } user
                || !IsNewcomer(message.Chat.Id, user.Id, DateTime.UtcNow))
                return false;

            string? what = HasLink(message) ? "ссылку" : LooksLikeAd(message) ? "похожее на рекламу" : null;
            if (what is null)
                return false;

            string name = Users.NameWithUsername(user);
            string chatTitle = message.Chat.Title ?? message.Chat.Id.ToString();

            await _notifier.ForwardToOwnerAsync(message.Chat.Id, message.MessageId,
                $"🔗 Новичок {name} прислал {what} в «{chatTitle}» — сообщение удалено из чата. Снять ограничение: /trust {user.Id}", ct);

            try
            {
                await bot.DeleteMessage(message.Chat.Id, message.MessageId, ct);
                Console.WriteLine($"[Newcomer] Удалено сообщение ({what}) от {name} в чате {message.Chat.Id}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Newcomer] Не удалось удалить сообщение от {name}: {ex.Message}");
            }
            return true;
        }

        private static readonly Regex AdRegex = new(
            @"(пиш|напиш|обращ|стуч)\w*\s+(мне\s+)?в\s+(лс|личк|личн|директ)|(?<!\w)в\s+лс(?!\w)|заработ|подработ|доход\w*\s+от|пассивн\w+\s+доход|" +
            @"удал[её]нн\w+\s+(работ|занят)|ищу\s+(людей|партн[её]р|сотрудник)|набира\w+\s+(людей|команд)|\d+\s*(\$|₽|руб|usdt|тыс|к)\S*\s+(в|за)\s+(день|недел|час)|" +
            @"казино|ставк\w*\s+на\s+спорт|интим|18\+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MixedScriptRegex = new(
            @"(?<!\w)(?=[а-яёa-z]*[а-яё])(?=[а-яёa-z]*[a-z])[а-яёa-z]{4,}(?!\w)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool LooksLikeAd(Message message)
        {
            var entities = (message.Entities ?? []).Concat(message.CaptionEntities ?? []);
            if (entities.Any(e => e.Type is MessageEntityType.Mention or MessageEntityType.TextMention))
                return true;

            string text = message.Text ?? message.Caption ?? "";
            return AdRegex.IsMatch(text) || MixedScriptRegex.IsMatch(text);
        }

        public static bool HasLink(Message message)
        {
            var entities = (message.Entities ?? []).Concat(message.CaptionEntities ?? []);
            if (entities.Any(e => e.Type is MessageEntityType.Url or MessageEntityType.TextLink))
                return true;

            string text = message.Text ?? message.Caption ?? "";
            return LinkRegex.IsMatch(text);
        }

        private void Cleanup(DateTime nowUtc)
        {
            foreach (var users in _joined.Values)
                foreach (var old in users.Where(u => nowUtc - u.Value >= NewcomerPeriod).ToList())
                    users.Remove(old.Key);
        }

        private Dictionary<long, Dictionary<long, DateTime>>? Load() =>
            JsonFile.Load<Dictionary<long, Dictionary<long, DateTime>>>(_filePath, "Newcomer");

        private void Save() => JsonFile.Save(_filePath, _joined, "Newcomer");
    }
}
