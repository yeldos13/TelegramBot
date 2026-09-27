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
        private const int MaxHistory = 30;

        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly GameData _data;

        public PenisStore(string filePath = "penis.json")
        {
            _filePath = filePath;
            _data = Load() ?? new GameData();
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
                foreach (var chat in _data.Chats.Values)
                    foreach (var player in chat.Players.Values)
                        if (player.History.Count > MaxHistory)
                            player.History.RemoveRange(0, player.History.Count - MaxHistory);
                Save();
                return result;
            }
        }

        private GameData? Load()
        {
            try
            {
                return File.Exists(_filePath)
                    ? JsonSerializer.Deserialize<GameData>(File.ReadAllText(_filePath))
                    : null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Penis] Не удалось прочитать {_filePath}: {ex.Message}");

                try { File.Copy(_filePath, _filePath + ".broken", overwrite: true); } catch { }
                return null;
            }
        }

        private void Save()
        {
            try
            {
                string tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(_data));
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Penis] Не удалось сохранить {_filePath}: {ex.Message}");
            }
        }
    }
}
