using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class SpamModule
    {
        public const int Threshold = 7;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

        public const int FloodThreshold = 15;
        public static readonly TimeSpan FloodWindow = TimeSpan.FromSeconds(30);

        public enum HitKind { Spam, Flood }

        public record Hit(HitKind Kind, List<int> MessageIds);

        private record Entry(string Key, int MessageId, DateTime At);

        private const int CleanupEvery = 500;

        private readonly ConcurrentDictionary<(long ChatId, long UserId), List<Entry>> _recent = new();
        private readonly string? _ownerUsername;
        private readonly MuteStore _mutes;
        private readonly StatsService? _stats;
        private string _botUsername = "";
        private int _registered;

        public SpamModule(string? ownerUsername, MuteStore? mutes = null, StatsService? stats = null)
        {
            _ownerUsername = ownerUsername?.TrimStart('@');
            _mutes = mutes ?? new MuteStore();
            _stats = stats;
        }

        internal int TrackedUsers => _recent.Count;

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleMessage(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (message.From is not { IsBot: false } user)
                return false;

            string key = GetKey(message) ?? $"other:{message.MessageId}";

            if (Register(message.Chat.Id, user.Id, key, message.MessageId, DateTime.UtcNow) is not { } hit)
                return false;

            string name = Users.NameWithUsername(user);
            Console.WriteLine($"[Spam] {hit.Kind}: {name} в чате {message.Chat.Id}, сообщений: {hit.MessageIds.Count}");

            _stats?.RemoveMessages(message.Chat.Id, user.Id, hit.MessageIds.Count - 1);

            try
            {
                await bot.DeleteMessages(message.Chat.Id, hit.MessageIds, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Spam] Не удалось удалить сообщения: {ex.Message}");
            }

            bool muted;
            try
            {
                await bot.RestrictChatMember(message.Chat.Id, user.Id, MutedPermissions(), cancellationToken: ct);
                muted = true;

                _mutes.Add(message.Chat.Id, new MuteStore.MutedUser
                {
                    UserId = user.Id,
                    Username = user.Username,
                    Name = Users.DisplayName(user),
                    At = DateTime.Now,
                    Reason = hit.Kind == HitKind.Spam ? "спам" : "флуд"
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Spam] Не удалось замьютить {name}: {ex.Message}");
                muted = false;
            }

            await bot.SendMessage(message.Chat.Id, BuildNotice(name, hit.Kind, muted, _ownerUsername), cancellationToken: ct);
            return true;
        }

        public async Task<bool> HandleModerationCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (BotCommands.Parse(message.Text, _botUsername) is not { Command: "unmute" or "mutelist" } command)
                return false;

            if (string.IsNullOrEmpty(_ownerUsername)
                || !string.Equals(message.From?.Username, _ownerUsername, StringComparison.OrdinalIgnoreCase))
            {
                var refusal = await bot.SendMessage(message.Chat.Id, "Эта команда только для владельца бота.",
                    replyParameters: message.MessageId, cancellationToken: ct);
                Cleanup.DeleteCommandLater(bot, message, refusal);
                return true;
            }

            if (command.Command == "mutelist")
            {
                var list = await bot.SendMessage(message.Chat.Id, BuildMuteList(_mutes.List(message.Chat.Id)),
                    replyParameters: message.MessageId, cancellationToken: ct);
                Cleanup.DeleteCommandLater(bot, message, list);
                return true;
            }

            await UnmuteAsync(bot, message, command.Args, ct);
            return true;
        }

        public static string BuildMuteList(IReadOnlyList<MuteStore.MutedUser> muted)
        {
            if (muted.Count == 0)
                return "🔊 Бот сейчас никого не держит в муте.";

            var lines = muted
                .OrderBy(m => m.At)
                .Select((m, i) => $"{i + 1}. {m.Name}{(m.Username != null ? $" (@{m.Username})" : "")} — {m.Reason}, {m.At:dd.MM HH:mm}");

            return $"🔇 В муте ({muted.Count}):\n" + string.Join("\n", lines) +
                   "\n\nСнять: /unmute @username или /unmute ответом на сообщение";
        }

        private async Task UnmuteAsync(ITelegramBotClient bot, Message message, string args, CancellationToken ct)
        {
            long chatId = message.Chat.Id;
            long? userId = null;
            string? name = null;

            if (message.ReplyToMessage?.From is { IsBot: false } replied)
            {
                userId = replied.Id;
                name = Users.DisplayName(replied);
            }
            else if (args.StartsWith('@') && _mutes.FindByUsername(chatId, args) is { } found)
            {
                userId = found.UserId;
                name = found.Name;
            }

            if (userId == null)
            {
                var hint = await bot.SendMessage(chatId,
                    "Ответь /unmute на сообщение человека или напиши /unmute @username " +
                    "(по юзернейму — только тех, кого замьютил бот, список: /mutelist).",
                    replyParameters: message.MessageId, cancellationToken: ct);
                Cleanup.DeleteCommandLater(bot, message, hint);
                return;
            }

            var chat = await bot.GetChat(chatId, ct);
            await bot.RestrictChatMember(chatId, userId.Value, chat.Permissions ?? FullPermissions(), cancellationToken: ct);
            _mutes.Remove(chatId, userId.Value);

            await bot.SendMessage(chatId, $"🔊 {name} размучен(а).", replyParameters: message.MessageId, cancellationToken: ct);
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

        public Hit? Register(long chatId, long userId, string key, int messageId, DateTime now)
        {
            if (Interlocked.Increment(ref _registered) % CleanupEvery == 0)
                RemoveIdleUsers(now);

            var entries = _recent.GetOrAdd((chatId, userId), _ => new List<Entry>());

            lock (entries)
            {
                entries.RemoveAll(e => now - e.At > Window);
                entries.Add(new Entry(key, messageId, now));

                var same = entries.Where(e => e.Key == key).ToList();
                if (same.Count >= Threshold)
                {
                    entries.RemoveAll(e => e.Key == key);
                    return new Hit(HitKind.Spam, same.Select(e => e.MessageId).ToList());
                }

                var flood = entries.Where(e => now - e.At <= FloodWindow).ToList();
                if (flood.Count >= FloodThreshold)
                {
                    entries.Clear();
                    return new Hit(HitKind.Flood, flood.Select(e => e.MessageId).ToList());
                }

                return null;
            }
        }

        internal void RemoveIdleUsers(DateTime now)
        {
            foreach (var (key, entries) in _recent)
            {
                lock (entries)
                {
                    if (entries.All(e => now - e.At > Window))
                        _recent.TryRemove(key, out _);
                }
            }
        }

        public static string BuildNotice(string name, HitKind kind, bool muted, string? ownerUsername)
        {
            string reason = kind == HitKind.Spam
                ? $"спам ({Threshold} одинаковых сообщений)"
                : $"флуд ({FloodThreshold} сообщений за {FloodWindow.TotalSeconds:0} секунд)";

            string text = muted
                ? $"🔇 {name} получает бессрочный мут за {reason}. Сообщения удалены, размутить может админ."
                : $"🧹 {name}: {reason} — сообщения удалены, но замьютить не получилось (это админ или у бота нет прав).";

            return string.IsNullOrEmpty(ownerUsername) ? text : $"{text} @{ownerUsername}";
        }

        private static ChatPermissions MutedPermissions() => SendPermissions(false);

        private static ChatPermissions FullPermissions() => SendPermissions(true);

        private static ChatPermissions SendPermissions(bool allowed) => new ChatPermissions
        {
            CanSendMessages = allowed,
            CanSendAudios = allowed,
            CanSendDocuments = allowed,
            CanSendPhotos = allowed,
            CanSendVideos = allowed,
            CanSendVideoNotes = allowed,
            CanSendVoiceNotes = allowed,
            CanSendPolls = allowed,
            CanSendOtherMessages = allowed,
            CanAddWebPagePreviews = allowed
        };
    }
}
