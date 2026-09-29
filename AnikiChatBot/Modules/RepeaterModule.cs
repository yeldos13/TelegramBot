using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class RepeaterModule
    {
        const string DefaultFilePath = "replies.txt";
        const string Separator = ":::";

        public const int MaxTriggerLength = 200;
        public const int MaxAnswerLength = 500;

        static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);
        static readonly Regex LinkRegex = new(@"(https?://|www\.|t\.me/)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly Regex MentionRegex = new(@"(?<!\w)@[A-Za-z0-9_]{4,32}", RegexOptions.Compiled);
        static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled);

        readonly ConcurrentDictionary<string, (string Trigger, string Answer)> repliesDatabase = new();
        readonly object saveLock = new object();
        readonly string filePath;
        readonly PeriodicSaver saver;

        readonly StatsService? stats;

        public RepeaterModule(StatsService? stats = null, string filePath = DefaultFilePath)
        {
            this.stats = stats;
            this.filePath = filePath;
            LoadRepliesFromFile();
            saver = new PeriodicSaver(SaveRepliesToFile, saveLock, SaveInterval);
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

                bool replyToBot = replyToMessage.From?.IsBot == true;

                if (!replyToBot && !HasMention(replyToMessage) && ShouldLearn(triggerText, answerText))
                {
                    repliesDatabase[MatchKey(triggerText)] = (triggerText, answerText);
                    MarkDirty();
                    return;
                }
            }

            if (HasMention(message))
                return;

            if (repliesDatabase.TryGetValue(MatchKey(message.Text), out var saved))
            {
                await bot.SendMessage(
                    chatId: message.Chat.Id,
                    text: saved.Answer,
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

        public static string MatchKey(string text)
        {
            string normalized = SpacesRegex.Replace(Normalize(text), " ").ToLowerInvariant();

            int start = 0, end = normalized.Length;
            while (start < end && !IsWordChar(normalized, start)) start++;
            while (end > start && !IsWordChar(normalized, end - 1)) end--;

            return start < end ? normalized[start..end] : normalized;
        }

        static bool IsWordChar(string text, int index) => char.IsLetterOrDigit(text[index]);

        public static bool ShouldLearn(string trigger, string answer)
        {
            return !string.IsNullOrEmpty(trigger)
                && !string.IsNullOrEmpty(answer)
                && MatchKey(trigger) != MatchKey(answer)
                && !IsJunk(trigger, answer);
        }

        public static bool HasMention(Message message)
        {
            if ((message.Entities ?? []).Any(e => e.Type is MessageEntityType.Mention or MessageEntityType.TextMention))
                return true;

            return MentionRegex.IsMatch(message.Text ?? "");
        }

        public static bool IsJunk(string trigger, string answer)
        {
            return trigger.StartsWith('/')
                || answer.StartsWith('/')
                || LinkRegex.IsMatch(trigger)
                || MentionRegex.IsMatch(trigger)
                || trigger.Length > MaxTriggerLength
                || answer.Length > MaxAnswerLength
                || trigger.Contains(Separator)
                || answer.Contains(Separator);
        }

        public void Flush() => saver.Flush();

        void MarkDirty() => saver.MarkDirty();

        void SaveRepliesToFile()
        {
            try
            {
                var lines = repliesDatabase.Values.Select(r => $"{r.Trigger}{Separator}{r.Answer}");

                string tempPath = filePath + ".tmp";
                File.WriteAllLines(tempPath, lines, Encoding.UTF8);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error while writing file {filePath}: {ex.Message}");
            }
        }

        void LoadRepliesFromFile()
        {
            try
            {
                if (!File.Exists(filePath))
                    return;

                int lines = 0, junk = 0;
                foreach (var line in File.ReadAllLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(Separator, 2);
                    if (parts.Length != 2) continue;

                    lines++;
                    string trigger = parts[0].Trim();
                    string answer = parts[1].Trim();

                    if (!ShouldLearn(trigger, answer))
                    {
                        junk++;
                        continue;
                    }

                    repliesDatabase[MatchKey(trigger)] = (trigger, answer);
                }

                int merged = lines - junk - repliesDatabase.Count;
                if (junk > 0 || merged > 0)
                {
                    string backup = $"{Path.GetFileNameWithoutExtension(filePath)}.backup-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
                    File.Copy(filePath, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath))!, backup), overwrite: true);
                    SaveRepliesToFile();
                    Console.WriteLine($"[Repeater] База почищена: убрано мусора {junk}, объединено дублей {merged}. Копия старой базы — {backup}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error while reading file {filePath}: {ex.Message}");
            }
        }
    }
}
