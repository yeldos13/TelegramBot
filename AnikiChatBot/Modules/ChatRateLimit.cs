namespace AnikiChatBot.Modules
{
    public class ChatRateLimit(int limit, TimeSpan window)
    {
        private readonly Dictionary<long, Queue<DateTime>> _sent = [];

        public bool TryTake(long chatId, DateTime now)
        {
            lock (_sent)
            {
                if (!_sent.TryGetValue(chatId, out var times))
                    _sent[chatId] = times = new Queue<DateTime>();

                while (times.Count > 0 && now - times.Peek() >= window)
                    times.Dequeue();

                if (times.Count >= limit)
                    return false;

                times.Enqueue(now);
                return true;
            }
        }
    }
}
