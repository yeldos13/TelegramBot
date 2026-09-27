using System.Text.Json;

namespace AnikiChatBot.Modules.Penis
{
    public class GrowEntry
    {
        public DateOnly Date { get; set; }
        public int Delta { get; set; }
        public int SizeAfter { get; set; }
    }

    public class Player
    {
        public long UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? Name { get; set; }
        public int Size { get; set; }
        public DateOnly? LastGrow { get; set; }

        public int Streak { get; set; }

        public int DuelWins { get; set; }
        public int DuelLosses { get; set; }

        public List<GrowEntry> History { get; set; } = new();
    }

    public class ChatGame
    {
        public Dictionary<long, Player> Players { get; set; } = new();
        public DateOnly? PenisOfDayDate { get; set; }
        public long? PenisOfDayUserId { get; set; }
    }

    public class GameData
    {
        public Dictionary<long, ChatGame> Chats { get; set; } = new();

        public Dictionary<long, HashSet<string>> Achievements { get; set; } = new();
    }

    public class PenisStore
    {
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);
        private static readonly JsonSerializerOptions Compact = new();

        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly GameData _data;
        private readonly PeriodicSaver _saver;

        public PenisStore(string filePath = "penis.json")
        {
            _filePath = filePath;
            _data = JsonFile.Load<GameData>(filePath, "Penis") ?? new GameData();
            _saver = new PeriodicSaver(() => JsonFile.Save(_filePath, _data, "Penis", Compact), _lock, SaveInterval);
        }

        public T Read<T>(Func<GameData, T> read)
        {
            lock (_lock)
                return read(_data);
        }

        public T Update<T>(Func<GameData, T> change)
        {
            lock (_lock)
            {
                var result = change(_data);
                _saver.MarkDirty();
                return result;
            }
        }

        public void Flush() => _saver.Flush();
    }
}
