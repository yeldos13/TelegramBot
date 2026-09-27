using System.Text.Json;

namespace AnikiChatBot.Modules
{
    public class MuteStore
    {
        public class MutedUser
        {
            public long UserId { get; set; }
            public string? Username { get; set; }
            public string Name { get; set; } = "";
            public DateTime At { get; set; }
            public string Reason { get; set; } = "";
        }

        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly Dictionary<long, List<MutedUser>> _chats;

        public MuteStore(string filePath = "muted.json")
        {
            _filePath = filePath;
            _chats = Load() ?? new Dictionary<long, List<MutedUser>>();
        }

        public void Add(long chatId, MutedUser user)
        {
            lock (_lock)
            {
                if (!_chats.TryGetValue(chatId, out var list))
                    _chats[chatId] = list = new List<MutedUser>();

                list.RemoveAll(u => u.UserId == user.UserId);
                list.Add(user);
                Save();
            }
        }

        public MutedUser? FindByUsername(long chatId, string username)
        {
            username = username.TrimStart('@');
            lock (_lock)
                return _chats.GetValueOrDefault(chatId)?
                    .FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        public void Remove(long chatId, long userId)
        {
            lock (_lock)
            {
                if (_chats.TryGetValue(chatId, out var list) && list.RemoveAll(u => u.UserId == userId) > 0)
                    Save();
            }
        }

        public IReadOnlyList<MutedUser> List(long chatId)
        {
            lock (_lock)
                return _chats.GetValueOrDefault(chatId)?.ToList() ?? [];
        }

        private Dictionary<long, List<MutedUser>>? Load() =>
            JsonFile.Load<Dictionary<long, List<MutedUser>>>(_filePath, "Mute");

        private void Save() => JsonFile.Save(_filePath, _chats, "Mute");
    }
}
