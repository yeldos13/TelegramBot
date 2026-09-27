using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class RepeaterModule
    {
        const string FilePath = "replies.txt";

        readonly ConcurrentDictionary<string, string> repliesDatabase = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly object saveLock = new object();

        readonly StatsService? stats;

        public RepeaterModule(StatsService? stats = null)
        {
            this.stats = stats;
            LoadRepliesFromFile();
        }

        public int Count => repliesDatabase.Count;

        public async Task HandleRepeaterCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message is not { } message || string.IsNullOrWhiteSpace(message.Text))
                return;

            if (message.ReplyToMessage is { } replyToMessage && !string.IsNullOrWhiteSpace(replyToMessage.Text))
            {
                string triggerText = Normalize(replyToMessage.Text);
                string answerText = Normalize(message.Text);

                if (triggerText != answerText && !string.IsNullOrEmpty(triggerText) && !string.IsNullOrEmpty(answerText))
                {
                    repliesDatabase[triggerText] = answerText;
                    SaveRepliesToFile();
                    return;
                }
            }

            if (repliesDatabase.TryGetValue(Normalize(message.Text), out var savedAnswer))
            {
                await bot.SendMessage(
                    chatId: message.Chat.Id,
                    text: savedAnswer,
                    replyParameters: new ReplyParameters { MessageId = message.Id },
                    cancellationToken: ct
                );

                stats?.RecordReply(message.Chat.Id);
            }
        }

        public static string Normalize(string text)
        {
            return text.Replace("\r", "").Replace("\n", " ").Trim();
        }

        void SaveRepliesToFile()
        {
            lock (saveLock)
            {
                try
                {
                    var lines = repliesDatabase.Select(kvp => $"{kvp.Key}:::{kvp.Value}");

                    string tempPath = FilePath + ".tmp";
                    File.WriteAllLines(tempPath, lines);
                    File.Move(tempPath, FilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error while writing file {FilePath}: {ex.Message}");
                }
            }
        }

        void LoadRepliesFromFile()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                foreach (var line in File.ReadAllLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(":::", 2);
                    if (parts.Length == 2)
                    {
                        string trigger = parts[0].Trim();
                        string answer = parts[1].Trim();
                        repliesDatabase[trigger] = answer;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error while reading file {FilePath}: {ex.Message}");
            }
        }
    }
}
