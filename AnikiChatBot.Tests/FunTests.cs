using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class FunTests : IDisposable
    {
        private readonly string _file = $"stats_fun_{Guid.NewGuid():N}.json";

        public void Dispose() => File.Delete(_file);

        [Fact]
        public void Roll_respects_range()
        {
            var fun = new FunModule(new StatsService(_file), new Random(1));

            for (int i = 0; i < 200; i++)
            {
                int value = int.Parse(fun.Roll("6").Split(' ')[1]);
                Assert.InRange(value, 1, 6);
            }

            Assert.EndsWith("(из 100)", fun.Roll(""));
            Assert.EndsWith("(из 100)", fun.Roll("abc"));
        }

        [Fact]
        public void Coin_gives_both_sides()
        {
            var fun = new FunModule(new StatsService(_file), new Random(1));
            var results = Enumerable.Range(0, 100).Select(_ => fun.Coin()).Distinct().ToList();

            Assert.Equal(2, results.Count);
        }

        [Fact]
        public void Who_picks_from_this_week_active_users()
        {
            var stats = new StatsService(_file);
            var fun = new FunModule(stats, new Random(1));

            Assert.Contains("не из кого", fun.Who(-1, "кто платит").ToLower());

            stats.RecordMessage(-1, 1, "Вася");
            stats.RecordMessage(-1, 2, "Петя");

            string answer = fun.Who(-1, "кто сегодня платит?");
            Assert.StartsWith("🎯 кто сегодня платит? — ", answer);
            Assert.True(answer.EndsWith("Вася") || answer.EndsWith("Петя"));
        }
    }
}
