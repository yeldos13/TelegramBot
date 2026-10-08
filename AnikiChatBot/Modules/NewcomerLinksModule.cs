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

        private readonly Dictionary<long, Dictionary<long, DateTime>> _joined;

        public NewcomerLinksModule(OwnerNotifier notifier, string filePath = "newcomers.json")
        {
            _notifier = notifier;
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

            if (update.Message is { NewChatMembers: { Length: > 0 } members } message)
            {
                foreach (var member in members.Where(m => !m.IsBot))
                    OnJoin(message.Chat, member, now);
            }

            if (update.ChatMember is { } change
                && change.OldChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked
                && change.NewChatMember.Status is ChatMemberStatus.Member or ChatMemberStatus.Restricted
                && !change.NewChatMember.User.IsBot)
            {
                OnJoin(change.Chat, change.NewChatMember.User, now);
            }
        }

        public const int RaidThreshold = 5;
        public static readonly TimeSpan RaidWindow = TimeSpan.FromMinutes(5);

        private record RecentJoin(long ChatId, long UserId, string Name, DateTime At);

        private readonly List<RecentJoin> _recentJoins = new();

        private void OnJoin(Chat chat, User user, DateTime nowUtc)
        {
            RecordJoin(chat.Id, user.Id, nowUtc);

            if (RegisterRecentJoin(chat.Id, chat.Title, user.Id, Users.NameWithUsername(user), nowUtc) is { } alert)
            {
                Console.WriteLine($"[Newcomer] Наплыв новичков в чате {chat.Id}");
                _notifier.Notify($"raid:{chat.Id}", alert);
            }
        }

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
                       "\n\nСсылки от новичков я и так удаляю. Если это боты — проверь чат.";
            }
        }

        public async Task<bool> HandleMessage(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (message.From is not { IsBot: false } user
                || !HasLink(message)
                || !IsNewcomer(message.Chat.Id, user.Id, DateTime.UtcNow))
                return false;

            string name = Users.NameWithUsername(user);
            string chatTitle = message.Chat.Title ?? message.Chat.Id.ToString();

            await _notifier.ForwardToOwnerAsync(message.Chat.Id, message.MessageId,
                $"🔗 Новичок {name} прислал ссылку в «{chatTitle}» — сообщение удалено из чата:", ct);

            await bot.DeleteMessage(message.Chat.Id, message.MessageId, ct);
            Console.WriteLine($"[Newcomer] Удалена ссылка от {name} в чате {message.Chat.Id}");
            return true;
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
