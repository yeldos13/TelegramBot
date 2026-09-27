using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class SpamModule
    {
        public const int Threshold = 7;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

        private record Entry(string Key, int MessageId, DateTime At);

        private readonly ConcurrentDictionary<(long ChatId, long UserId), List<Entry>> _recent = new();
        private readonly string? _ownerUsername;

        public SpamModule(string? ownerUsername)
        {
            _ownerUsername = ownerUsername?.TrimStart('@');
        }

        public async Task<bool> HandleMessage(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (message.From is not { IsBot: false } user || GetKey(message) is not { } key)
                return false;

            var spamIds = Register(message.Chat.Id, user.Id, key, message.MessageId, DateTime.UtcNow);
            if (spamIds == null)
                return false;

            string name = PenisModule.DisplayName(user) + (user.Username != null ? $" (@{user.Username})" : "");
            Console.WriteLine($"[Spam] {name} в чате {message.Chat.Id}: {spamIds.Count} одинаковых сообщений");

            try
            {
                await bot.DeleteMessages(message.Chat.Id, spamIds, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Spam] Не удалось удалить спам: {ex.Message}");
            }

            bool muted;
            try
            {
                await bot.RestrictChatMember(message.Chat.Id, user.Id, MutedPermissions(), cancellationToken: ct);
                muted = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Spam] Не удалось замьютить {name}: {ex.Message}");
                muted = false;
            }

            await bot.SendMessage(message.Chat.Id, BuildNotice(name, muted, _ownerUsername), cancellationToken: ct);
            return true;
        }

        public static string? GetKey(Message message)
        {
            string? media =
                message.Sticker?.FileUniqueId ??
                message.Animation?.FileUniqueId ??
                message.Photo?.LastOrDefault()?.FileUniqueId ??
                message.Video?.FileUniqueId ??
                message.VideoNote?.FileUniqueId ??
                message.Voice?.FileUniqueId ??
                message.Document?.FileUniqueId;

            string? text = (message.Text ?? message.Caption)?.Trim().ToLowerInvariant();

            if (media != null)
                return $"media:{media}:{text}";

            return string.IsNullOrEmpty(text) ? null : $"text:{text}";
        }

        public List<int>? Register(long chatId, long userId, string key, int messageId, DateTime now)
        {
            var entries = _recent.GetOrAdd((chatId, userId), _ => new List<Entry>());

            lock (entries)
            {
                entries.RemoveAll(e => now - e.At > Window);
                entries.Add(new Entry(key, messageId, now));

                var same = entries.Where(e => e.Key == key).ToList();
                if (same.Count < Threshold)
                    return null;

                entries.RemoveAll(e => e.Key == key);
                return same.Select(e => e.MessageId).ToList();
            }
        }

        public static string BuildNotice(string name, bool muted, string? ownerUsername)
        {
            string text = muted
                ? $"🔇 {name} получает бессрочный мут за спам ({Threshold} одинаковых сообщений). Спам удалён, размутить может админ."
                : $"🧹 {name} спамит ({Threshold} одинаковых сообщений) — спам удалён, но замьютить не получилось (это админ или у бота нет прав).";

            return string.IsNullOrEmpty(ownerUsername) ? text : $"{text} @{ownerUsername}";
        }

        private static ChatPermissions MutedPermissions() => new ChatPermissions
        {
            CanSendMessages = false,
            CanSendAudios = false,
            CanSendDocuments = false,
            CanSendPhotos = false,
            CanSendVideos = false,
            CanSendVideoNotes = false,
            CanSendVoiceNotes = false,
            CanSendPolls = false,
            CanSendOtherMessages = false,
            CanAddWebPagePreviews = false
        };
    }
}
