using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot
{
    public class OwnerNotifier
    {
        private const string OwnerIdFile = "owner_id.txt";
        private static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

        private readonly string? _ownerUsername;
        private readonly ConcurrentDictionary<string, DateTime> _lastSent = new();
        private ITelegramBotClient? _bot;
        private long? _ownerId;

        public OwnerNotifier(string? ownerUsername)
        {
            _ownerUsername = ownerUsername?.TrimStart('@');

            if (File.Exists(OwnerIdFile) && long.TryParse(File.ReadAllText(OwnerIdFile).Trim(), out long id))
                _ownerId = id;
        }

        public bool HasOwner => _ownerId != null;

        public void Attach(ITelegramBotClient bot) => _bot = bot;

        public bool TryRegisterOwner(Message message)
        {
            if (string.IsNullOrEmpty(_ownerUsername) || message.From is not { } from
                || !string.Equals(from.Username, _ownerUsername, StringComparison.OrdinalIgnoreCase))
                return false;

            if (_ownerId != from.Id)
            {
                _ownerId = from.Id;
                try
                {
                    File.WriteAllText(OwnerIdFile, from.Id.ToString());
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Notifier] Не удалось сохранить id владельца: {ex.Message}");
                }
                Console.WriteLine($"[Notifier] Владелец @{_ownerUsername} зарегистрирован");
            }

            return message.Chat.Type == ChatType.Private;
        }

        public void Notify(string key, string text)
        {
            if (!ShouldSend(key))
                return;

            _ = SendAsync(text, CancellationToken.None);
        }

        public async Task NotifyNowAsync(string text, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await SendAsync(text, cts.Token);
        }

        internal bool ShouldSend(string key)
        {
            var now = DateTime.UtcNow;
            bool send = false;

            _lastSent.AddOrUpdate(key,
                _ => { send = true; return now; },
                (_, last) =>
                {
                    if (now - last < Cooldown)
                        return last;
                    send = true;
                    return now;
                });

            return send;
        }

        private async Task SendAsync(string text, CancellationToken ct)
        {
            if (_bot == null || _ownerId is not { } ownerId)
                return;

            if (text.Length > 3500)
                text = text[..3500] + "…";

            try
            {
                await _bot.SendMessage(ownerId, "⚠️ " + text, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Notifier] Не удалось отправить уведомление владельцу " +
                    $"(он должен написать боту /start в личку): {ex.Message}");
            }
        }
    }
}
