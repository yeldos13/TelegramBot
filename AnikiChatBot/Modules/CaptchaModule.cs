using System.Net;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AnikiChatBot.Modules
{
    public class CaptchaModule(OwnerNotifier? notifier, Func<long, long, bool> isMuted, string filePath = "captcha.json")
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
        private const string CallbackPrefix = "captcha:";

        public class Pending
        {
            public long ChatId { get; set; }
            public long UserId { get; set; }
            public string Name { get; set; } = "";
            public int MessageId { get; set; }
            public DateTime Until { get; set; }
        }

        private readonly Dictionary<string, Pending> _pending =
            JsonFile.Load<Dictionary<string, Pending>>(filePath, "Captcha") ?? [];

        private static string Key(long chatId, long userId) => $"{chatId}:{userId}";

        internal bool TryReserve(long chatId, User user, DateTime nowUtc)
        {
            lock (_pending)
            {
                if (_pending.ContainsKey(Key(chatId, user.Id)))
                    return false;

                _pending[Key(chatId, user.Id)] = new Pending { ChatId = chatId, UserId = user.Id, Name = Users.NameWithUsername(user), Until = nowUtc + Timeout };
                return true;
            }
        }

        internal Pending? Take(long chatId, long userId)
        {
            lock (_pending)
            {
                if (!_pending.Remove(Key(chatId, userId), out var pending))
                    return null;

                Save();
                return pending;
            }
        }

        internal List<Pending> TakeExpired(DateTime nowUtc)
        {
            lock (_pending)
            {
                var expired = _pending.Values.Where(p => p.Until <= nowUtc).ToList();
                foreach (var p in expired)
                    _pending.Remove(Key(p.ChatId, p.UserId));

                if (expired.Count > 0)
                    Save();
                return expired;
            }
        }

        private void Save() => JsonFile.Save(filePath, _pending, "Captcha");

        public void HandleJoins(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            foreach (var (chat, user) in MembersModule.GetJoinedMembers(update))
                if (TryReserve(chat.Id, user, now))
                    _ = Task.Run(() => AskAsync(bot, chat.Id, user, ct), ct);
        }

        private async Task AskAsync(ITelegramBotClient bot, long chatId, User user, CancellationToken ct)
        {
            try
            {
                await bot.RestrictChatMember(chatId, user.Id, SpamModule.SendPermissions(false), cancellationToken: ct);

                string mention = $"<a href=\"tg://user?id={user.Id}\">{WebUtility.HtmlEncode(Users.DisplayName(user))}</a>";
                var sent = await bot.SendMessage(chatId,
                    $"{mention}, нажми кнопку в течение {Timeout.TotalMinutes:0} минут, что ты не бот, иначе я удалю тебя из чата.",
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Я не бот", CallbackPrefix + user.Id)),
                    cancellationToken: ct);

                lock (_pending)
                {
                    if (_pending.TryGetValue(Key(chatId, user.Id), out var pending))
                    {
                        pending.MessageId = sent.MessageId;
                        Save();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Take(chatId, user.Id);
                Console.Error.WriteLine($"[Captcha] Не удалось выдать капчу (нужно право «Блокировка участников»): {ex.Message}");
            }
        }

        public async Task<bool> HandleCallback(ITelegramBotClient bot, CallbackQuery query, Action<Chat, User> onPassed, CancellationToken ct)
        {
            if (query.Data?.StartsWith(CallbackPrefix) != true || query.Message is not { } message)
                return false;

            if (!long.TryParse(query.Data[CallbackPrefix.Length..], out long userId) || query.From.Id != userId)
            {
                await bot.AnswerCallbackQuery(query.Id, "Это кнопка не для тебя", cancellationToken: ct);
                return true;
            }

            if (Take(message.Chat.Id, userId) is null)
            {
                await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                return true;
            }

            if (!isMuted(message.Chat.Id, userId))
            {
                try
                {
                    var chat = await bot.GetChat(message.Chat.Id, ct);
                    await bot.RestrictChatMember(message.Chat.Id, userId, chat.Permissions ?? SpamModule.SendPermissions(true), cancellationToken: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    string name = Users.NameWithUsername(query.From);
                    Console.Error.WriteLine($"[Captcha] Не удалось снять ограничение с {name}: {ex.Message}");
                    notifier?.Notify($"captcha-fail:{message.Chat.Id}:{userId}",
                        $"⚠️ {name} (id {userId}) прошёл капчу в «{message.Chat.Title}», но снять ограничение не получилось — сними руками.");
                }
            }

            await bot.AnswerCallbackQuery(query.Id, "Добро пожаловать!", cancellationToken: ct);
            await DeleteAsync(bot, message.Chat.Id, message.MessageId);
            onPassed(message.Chat, query.From);
            return true;
        }

        public async Task KickExpiredAsync(ITelegramBotClient bot, CancellationToken ct)
        {
            foreach (var p in TakeExpired(DateTime.UtcNow))
            {
                try
                {
                    await bot.BanChatMember(p.ChatId, p.UserId, cancellationToken: ct);
                    await bot.UnbanChatMember(p.ChatId, p.UserId, onlyIfBanned: true, cancellationToken: ct);
                    Console.WriteLine($"[Captcha] {p.Name} не прошёл капчу и удалён из чата {p.ChatId}");
                    notifier?.Notify($"captcha:{p.ChatId}:{p.UserId}", $"🤖 {p.Name} (id {p.UserId}) не нажал «Я не бот» — удалил из чата.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[Captcha] Не удалось удалить {p.Name}: {ex.Message}");
                }

                if (p.MessageId != 0)
                    await DeleteAsync(bot, p.ChatId, p.MessageId);
            }
        }

        private static async Task DeleteAsync(ITelegramBotClient bot, long chatId, int messageId)
        {
            try { await bot.DeleteMessage(chatId, messageId, CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine($"[Captcha] Не удалось удалить сообщение: {ex.Message}"); }
        }
    }
}
