using System.Text;
using System.Text.Json;

namespace AnikiChatBot
{
    public class StatsService
    {
        private const string FilePath = "stats.json";

        public const DayOfWeek ReportDay = DayOfWeek.Sunday;
        public static readonly TimeSpan ReportTime = TimeSpan.FromHours(20);

        public class ChatStats
        {
            public int MediaSent { get; set; }
            public int Conversions { get; set; }
            public int RepliesSent { get; set; }
            public List<string> LeftMembers { get; set; } = new();
        }

        public class State
        {
            public DateTime PeriodStart { get; set; }
            public Dictionary<long, ChatStats> Chats { get; set; } = new();
        }

        private readonly object _lock = new object();
        private readonly string _filePath;
        private State _state;

        public StatsService(string filePath = FilePath)
        {
            _filePath = filePath;
            _state = Load() ?? new State { PeriodStart = DateTime.Now };
        }

        public DateTime PeriodStart
        {
            get { lock (_lock) return _state.PeriodStart; }
        }

        public void RecordMedia(long chatId) => Update(chatId, s => s.MediaSent++);
        public void RecordConversion(long chatId) => Update(chatId, s => s.Conversions++);
        public void RecordReply(long chatId) => Update(chatId, s => s.RepliesSent++);
        public void RecordLeft(long chatId, string name) => Update(chatId, s => s.LeftMembers.Add(name));

        public ChatStats GetChat(long chatId)
        {
            lock (_lock)
            {
                var s = _state.Chats.GetValueOrDefault(chatId) ?? new ChatStats();
                return new ChatStats
                {
                    MediaSent = s.MediaSent,
                    Conversions = s.Conversions,
                    RepliesSent = s.RepliesSent,
                    LeftMembers = s.LeftMembers.ToList()
                };
            }
        }

        public static DateTime NextReportTime(DateTime from)
        {
            var candidate = from.Date + ReportTime;
            while (candidate.DayOfWeek != ReportDay || candidate <= from)
                candidate = candidate.AddDays(1).Date + ReportTime;
            return candidate;
        }

        public bool IsReportDue(DateTime now) => now >= NextReportTime(PeriodStart);

        public void StartNewPeriod(DateTime now)
        {
            lock (_lock)
            {
                _state = new State { PeriodStart = now };
                Save();
            }
        }

        public static string BuildReport(ChatStats stats, DateTime from, DateTime to, string? ownerUsername)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📊 Итоги недели ({from:dd.MM} – {to:dd.MM})");
            sb.AppendLine();
            sb.AppendLine($"Скачано медиа: {stats.MediaSent}");
            sb.AppendLine($"Конвертаций валют: {stats.Conversions}");
            sb.AppendLine($"Автоответов: {stats.RepliesSent}");

            sb.AppendLine(stats.LeftMembers.Count == 0
                ? "Никто не покинул чат"
                : $"Покинули чат ({stats.LeftMembers.Count}): {string.Join(", ", stats.LeftMembers)}");

            string? tag = ownerUsername?.TrimStart('@');
            if (!string.IsNullOrEmpty(tag))
            {
                sb.AppendLine();
                sb.Append($"@{tag}");
            }

            return sb.ToString().TrimEnd();
        }

        private void Update(long chatId, Action<ChatStats> change)
        {
            lock (_lock)
            {
                if (!_state.Chats.TryGetValue(chatId, out var stats))
                    _state.Chats[chatId] = stats = new ChatStats();

                change(stats);
                Save();
            }
        }

        private State? Load()
        {
            try
            {
                return File.Exists(_filePath)
                    ? JsonSerializer.Deserialize<State>(File.ReadAllText(_filePath))
                    : null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Stats] Не удалось прочитать {_filePath}: {ex.Message}");
                return null;
            }
        }

        private void Save()
        {
            try
            {
                string tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Stats] Не удалось сохранить {_filePath}: {ex.Message}");
            }
        }
    }
}
