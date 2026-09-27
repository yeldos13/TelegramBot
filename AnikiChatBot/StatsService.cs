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

            public Dictionary<long, WeekPlayer> Players { get; set; } = new();
        }

        public class WeekPlayer
        {
            public string Name { get; set; } = "";
            public int Growth { get; set; }
            public int DuelWins { get; set; }
            public int Messages { get; set; }
        }

        public record GameLeader(string Name, int Size);

        public class State
        {
            public DateTime PeriodStart { get; set; }
            public Dictionary<long, ChatStats> Chats { get; set; } = new();
        }

        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

        private readonly object _lock = new object();
        private readonly string _filePath;
        private readonly Timer _saveTimer;
        private State _state;
        private bool _dirty;

        public StatsService(string filePath = FilePath)
        {
            _filePath = filePath;
            _state = Load() ?? new State { PeriodStart = DateTime.Now };
            _saveTimer = new Timer(_ => Flush(), null, SaveInterval, SaveInterval);
        }

        public void Flush()
        {
            lock (_lock)
            {
                if (!_dirty)
                    return;
                Save();
                _dirty = false;
            }
        }

        public DateTime PeriodStart
        {
            get { lock (_lock) return _state.PeriodStart; }
        }

        public void RecordMedia(long chatId) => Update(chatId, s => s.MediaSent++);
        public void RecordConversion(long chatId) => Update(chatId, s => s.Conversions++);
        public void RecordReply(long chatId) => Update(chatId, s => s.RepliesSent++);
        public void RecordLeft(long chatId, string name) => Update(chatId, s => s.LeftMembers.Add(name));

        public void RecordGrowth(long chatId, long userId, string name, int change) =>
            Update(chatId, s => GetPlayer(s, userId, name).Growth += change);

        public void RecordDuelWin(long chatId, long userId, string name) =>
            Update(chatId, s => GetPlayer(s, userId, name).DuelWins++);

        public void RecordMessage(long chatId, long userId, string name) =>
            Update(chatId, s => GetPlayer(s, userId, name).Messages++);

        public List<string> GetActiveNames(long chatId)
        {
            lock (_lock)
                return _state.Chats.GetValueOrDefault(chatId)?.Players.Values
                    .Where(p => p.Messages > 0).Select(p => p.Name).ToList() ?? [];
        }

        private static WeekPlayer GetPlayer(ChatStats stats, long userId, string name)
        {
            if (!stats.Players.TryGetValue(userId, out var player))
                stats.Players[userId] = player = new WeekPlayer();
            player.Name = name;
            return player;
        }

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
                    LeftMembers = s.LeftMembers.ToList(),
                    Players = s.Players.ToDictionary(p => p.Key,
                        p => new WeekPlayer { Name = p.Value.Name, Growth = p.Value.Growth, DuelWins = p.Value.DuelWins, Messages = p.Value.Messages })
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
                _dirty = false;
            }
        }

        public static string BuildReport(ChatStats stats, DateTime from, DateTime to, string? ownerUsername, GameLeader? leader = null)
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

            var active = stats.Players.Values.Where(p => p.Messages > 0).OrderByDescending(p => p.Messages).Take(3).ToList();
            if (active.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("💬 Самые активные");
                string[] medals = ["🥇", "🥈", "🥉"];
                for (int i = 0; i < active.Count; i++)
                    sb.AppendLine($"{medals[i]} {active[i].Name} — {active[i].Messages} сообщ.");
            }

            var game = new List<string>();
            if (leader != null)
                game.Add($"Самый большой: {leader.Name} — {leader.Size} см");

            var grower = stats.Players.Values.Where(p => p.Growth > 0).MaxBy(p => p.Growth);
            if (grower != null)
                game.Add($"Больше всех вырос: {grower.Name} (+{grower.Growth} см)");

            var duelist = stats.Players.Values.Where(p => p.DuelWins > 0).MaxBy(p => p.DuelWins);
            if (duelist != null)
                game.Add($"Больше всех побед в дуэлях: {duelist.Name} ({duelist.DuelWins})");

            if (game.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("🍆 Игра");
                foreach (var line in game)
                    sb.AppendLine(line);
            }

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
                _dirty = true;
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
