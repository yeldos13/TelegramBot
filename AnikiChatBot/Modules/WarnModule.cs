using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class WarnModule(MuteStore mutes, string filePath = "warns.json")
    {
        public const int Limit = 3;

        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "warn", Description = "Предупреждение ответом на сообщение (для админов)" },
            new BotCommand { Command = "unwarn", Description = "Снять предупреждения (для админов)" }
        ];

        private readonly Dictionary<string, int> _warns = JsonFile.Load<Dictionary<string, int>>(filePath, "Warn") ?? [];

        private string _botUsername = "";

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        private static string Key(long chatId, long userId) => $"{chatId}:{userId}";

        internal int Add(long chatId, long userId)
        {
            lock (_warns)
            {
                int count = _warns.GetValueOrDefault(Key(chatId, userId)) + 1;
                if (count >= Limit)
                    _warns.Remove(Key(chatId, userId));
                else
                    _warns[Key(chatId, userId)] = count;

                JsonFile.Save(filePath, _warns, "Warn");
                return count;
            }
        }

        internal bool Reset(long chatId, long userId)
        {
            lock (_warns)
            {
                if (!_warns.Remove(Key(chatId, userId)))
                    return false;

                JsonFile.Save(filePath, _warns, "Warn");
                return true;
            }
        }

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (BotCommands.Parse(message.Text, _botUsername) is not { Command: "warn" or "unwarn" } command)
                return false;

            var text = await ExecuteAsync(bot, message, command, ct);
            var sent = await bot.SendMessage(message.Chat.Id, text, replyParameters: message.MessageId, cancellationToken: ct);
            if (text.StartsWith("Ответь") || text.StartsWith("Только") || text.StartsWith("Админ"))
                Cleanup.DeleteCommandLater(bot, message, sent);
            return true;
        }

        private async Task<string> ExecuteAsync(ITelegramBotClient bot, Message message, ParsedCommand command, CancellationToken ct)
        {
            if (!await ChatAdmins.IsAdminAsync(bot, message, ct))
                return "Только админы могут выдавать предупреждения.";

            if (message.ReplyToMessage?.From is not { IsBot: false } target)
                return $"Ответь /{command.Command} на сообщение участника.";

            long chatId = message.Chat.Id;
            string name = Users.NameWithUsername(target);

            if (command.Command == "unwarn")
                return Reset(chatId, target.Id) ? $"✅ Снял предупреждения с {name}." : $"У {name} нет предупреждений.";

            var member = await bot.GetChatMember(chatId, target.Id, ct);
            if (member.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator)
                return "Админам предупреждения не выдаю.";

            string reason = command.Args.Length > 0 ? $" ({command.Args})" : "";
            int count = Add(chatId, target.Id);
            if (count < Limit)
                return $"⚠️ {name} получает предупреждение {count}/{Limit}{reason}. На {Limit}-м будет мут.";

            try
            {
                await bot.RestrictChatMember(chatId, target.Id, SpamModule.SendPermissions(false), cancellationToken: ct);
                mutes.Add(chatId, new MuteStore.MutedUser
                {
                    UserId = target.Id,
                    Username = target.Username,
                    Name = Users.DisplayName(target),
                    At = DateTime.Now,
                    Reason = "предупреждения"
                });
                Console.WriteLine($"[Warn] {name} замьючен в чате {chatId} за {Limit} предупреждения");
                return $"🔇 {name} получает {Limit}-е предупреждение{reason} и бессрочный мут. Снять: /unmute ответом на сообщение (владелец).";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Warn] Не удалось замьютить {name}: {ex.Message}");
                return $"⚠️ {name} получает {Limit}-е предупреждение{reason}, но замьютить не получилось (нет права «Блокировка участников»).";
            }
        }
    }
}
