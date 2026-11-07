namespace AnikiChatBot.Modules.Quote
{
    public class SavedQuote
    {
        public int Id { get; set; }
        public string FileId { get; set; } = "";
        public int MessageId { get; set; }
        public string Author { get; set; } = "";
        public string Preview { get; set; } = "";
        public DateTime At { get; set; }
        public HashSet<long> Up { get; set; } = [];
        public HashSet<long> Down { get; set; } = [];

        public int Score => Up.Count - Down.Count;
    }

    public class QuoteStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly Dictionary<long, List<SavedQuote>> _chats;

        public QuoteStore(string filePath = "quotes.json")
        {
            _filePath = filePath;
            _chats = JsonFile.Load<Dictionary<long, List<SavedQuote>>>(_filePath, "Quote") ?? [];
        }

        public int NextId(long chatId)
        {
            lock (_lock)
                return _chats.TryGetValue(chatId, out var list) && list.Count > 0 ? list.Max(q => q.Id) + 1 : 1;
        }

        public void Add(long chatId, SavedQuote quote)
        {
            lock (_lock)
            {
                if (!_chats.TryGetValue(chatId, out var list))
                    _chats[chatId] = list = [];

                list.Add(quote);
                Save();
            }
        }

        public (int Up, int Down)? Vote(long chatId, int quoteId, long userId, bool up)
        {
            lock (_lock)
            {
                var quote = _chats.GetValueOrDefault(chatId)?.FirstOrDefault(q => q.Id == quoteId);
                if (quote == null)
                    return null;

                var chosen = up ? quote.Up : quote.Down;
                var other = up ? quote.Down : quote.Up;
                if (!chosen.Remove(userId))
                    chosen.Add(userId);
                other.Remove(userId);

                Save();
                return (quote.Up.Count, quote.Down.Count);
            }
        }

        public IReadOnlyList<SavedQuote> Top(long chatId, int count)
        {
            lock (_lock)
                return _chats.GetValueOrDefault(chatId)?
                    .Where(q => q.Score > 0)
                    .OrderByDescending(q => q.Score)
                    .ThenByDescending(q => q.At)
                    .Take(count)
                    .ToList() ?? [];
        }

        public SavedQuote? RandomTop(long chatId, Random random)
        {
            lock (_lock)
            {
                var top = _chats.GetValueOrDefault(chatId)?.Where(q => q.Score > 0).ToList();
                return top is { Count: > 0 } ? top[random.Next(top.Count)] : null;
            }
        }

        private void Save() => JsonFile.Save(_filePath, _chats, "Quote");
    }
}
