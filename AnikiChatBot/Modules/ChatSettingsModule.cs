using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class ChatSettingsModule(string filePath = "chat_settings.json")
    {
        public const string Welcome = "welcome";
        public const string Gifs = "gifs";
        public const string Replies = "replies";

        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "settings", Description = "Приветствие, гифки и автоответы в чате (для админов)" },
        ];

        private static readonly (string Key, string Name, string Alias)[] Features =
        [
            (Welcome, "приветствие", "приветствие"),
            (Gifs, "случайные гифки", "гифки"),
            (Replies, "автоответы", "автоответы"),
        ];

        private readonly Dictionary<long, HashSet<string>> _off =
            JsonFile.Load<Dictionary<long, HashSet<string>>>(filePath, "Settings") ?? [];

        private string _botUsername = "";

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public bool IsOn(long chatId, string feature)
        {
            lock (_off)
                return !(_off.TryGetValue(chatId, out var off) && off.Contains(feature));
        }

        internal bool Toggle(long chatId, string feature)
        {
            lock (_off)
            {
                if (!_off.TryGetValue(chatId, out var off))
                    _off[chatId] = off = [];

                bool on = !off.Add(feature);
                if (on)
                    off.Remove(feature);

                JsonFile.Save(filePath, _off, "Settings");
                return on;
            }
        }

        internal static string? FindFeature(string arg) =>
            Features.FirstOrDefault(f => arg.Equals(f.Key, StringComparison.OrdinalIgnoreCase) || arg.Equals(f.Alias, StringComparison.OrdinalIgnoreCase)).Key;

        internal string Describe(long chatId) =>
            "Настройки чата:\n" +
            string.Join("\n", Features.Select(f => $"{(IsOn(chatId, f.Key) ? "✅" : "❌")} {f.Name} — /settings {f.Alias}")) +
            "\n\nКоманда с названием включает или выключает (только админы).";

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (BotCommands.Parse(message.Text, _botUsername) is not { Command: "settings" } parsed)
                return false;

            long chatId = message.Chat.Id;
            string text;

            if (FindFeature(parsed.Args) is not { } feature)
                text = Describe(chatId);
            else if (!await ChatAdmins.IsAdminAsync(bot, message, ct))
                text = "Менять настройки могут только админы.";
            else
            {
                string name = Features.First(f => f.Key == feature).Name;
                text = Toggle(chatId, feature) ? $"✅ Включил: {name}." : $"❌ Выключил: {name}.";
            }

            var sent = await bot.SendMessage(chatId, text, replyParameters: message.MessageId, cancellationToken: ct);
            Cleanup.DeleteCommandLater(bot, message, sent);
            return true;
        }
    }
}
