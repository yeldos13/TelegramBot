using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class MembersModule
    {
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<(long ChatId, long UserId), DateTime> _recent = new();
        private readonly string? _tagUsername;
        private long _botId;

        private readonly StatsService? _stats;

        public MembersModule(string? tagUsername, StatsService? stats = null)
        {
            _tagUsername = tagUsername;
            _stats = stats;
        }

        public void SetBotId(long botId) => _botId = botId;

        public async Task HandleMembersUpdate(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (GetLeftMember(update, _botId) is not { } left)
                return;

            var now = DateTime.UtcNow;
            var key = (left.ChatId, left.User.Id);

            if (_recent.TryGetValue(key, out var last) && now - last < DuplicateWindow)
                return;
            _recent[key] = now;

            foreach (var old in _recent.Where(x => now - x.Value > DuplicateWindow).ToList())
                _recent.TryRemove(old.Key, out _);

            _stats?.RecordLeft(left.ChatId, BuildDisplayName(left.User));
            await bot.SendMessage(left.ChatId, BuildLeaveText(left.User, _tagUsername), cancellationToken: ct);
        }

        public static string BuildDisplayName(User user)
        {
            string name = string.Join(" ", new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(name))
                name = "Участник";

            if (!string.IsNullOrEmpty(user.Username))
                name += $" (@{user.Username})";

            return name;
        }

        public record LeftMember(long ChatId, User User);

        public static LeftMember? GetLeftMember(Update update, long botId)
        {
            if (update.ChatMember is { } change)
            {
                var oldStatus = change.OldChatMember.Status;

                bool wasInChat = oldStatus is ChatMemberStatus.Member or ChatMemberStatus.Administrator
                    or ChatMemberStatus.Creator or ChatMemberStatus.Restricted;

                if (change.OldChatMember is ChatMemberRestricted { IsMember: false })
                    wasInChat = false;

                var user = change.NewChatMember.User;
                bool leftByThemselves = change.NewChatMember.Status == ChatMemberStatus.Left && change.From.Id == user.Id;

                if (!wasInChat || !leftByThemselves || user.Id == botId)
                    return null;

                return new LeftMember(change.Chat.Id, user);
            }

            if (update.Message is { LeftChatMember: { } leftUser } message
                && leftUser.Id != botId
                && message.From?.Id == leftUser.Id)
            {
                return new LeftMember(message.Chat.Id, leftUser);
            }

            return null;
        }

        public static string BuildLeaveText(User user, string? tagUsername)
        {
            string text = $"{BuildDisplayName(user)} покинул(а) чат";
            string? tag = tagUsername?.TrimStart('@');
            return string.IsNullOrEmpty(tag) ? text : $"{text} @{tag}";
        }
    }
}
