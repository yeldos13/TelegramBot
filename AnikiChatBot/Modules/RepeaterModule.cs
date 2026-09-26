using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class RepeaterModule
    {
        ConcurrentDictionary<string, string> repliesDatabase = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string FilePath = "replies.txt";
        public HttpClient httpClient;

        public async Task HandleRepeaterCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            LoadRepliesFromFile();

            if (string.IsNullOrWhiteSpace(update.Message.Text))
                return;

            if (update.Message.ReplyToMessage is { } replyToMessage && !string.IsNullOrWhiteSpace(replyToMessage.Text))
            {
                string triggerText = replyToMessage.Text.Replace("\r", "").Replace("\n", " ").Trim();
                string answerText = update.Message.Text.Replace("\r", "").Replace("\n", " ").Trim();

                if (triggerText != answerText && !string.IsNullOrEmpty(triggerText) && !string.IsNullOrEmpty(answerText))
                {
                    repliesDatabase[triggerText] = answerText;
                    SaveRepliesToFile();
                    return;
                }
            }

            string cleanedText = update.Message.Text.Trim();
            if (repliesDatabase.TryGetValue(cleanedText, out var savedAnswer))
            {
                await bot.SendMessage(
                    chatId: update.Message.Chat.Id,
                    text: savedAnswer,
                    replyParameters: new ReplyParameters { MessageId = update.Message.Id },
                    cancellationToken: ct
                );
            }
        }

        void SaveRepliesToFile()
        {
            try
            {
                var lines = new List<string>();
                foreach (var kvp in repliesDatabase)
                {
                    lines.Add($"{kvp.Key}:::{kvp.Value}");
                }

                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error while writing file {FilePath}: {ex.Message}");
            }
        }

        void LoadRepliesFromFile()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    File.Create(FilePath).Dispose();
                    return;
                }

                var lines = File.ReadAllLines(FilePath);
                foreach (var line in lines)
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
