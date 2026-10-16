using AnikiChatBot.Modules;
using Telegram.Bot.Types;

namespace AnikiChatBot.Tests
{
    public class RandomGifTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"gifs_test_{Guid.NewGuid():N}");
        private static readonly DateTime Now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }

        [Theory]
        [InlineData("3", 0.03)]
        [InlineData("3%", 0.03)]
        [InlineData("0.05", 0.05)]
        [InlineData("2,5%", 0.025)]
        [InlineData("1", 1.0)]
        [InlineData("0", 0.0)]
        [InlineData(null, 0.03)]
        [InlineData("ерунда", 0.03)]
        [InlineData("-5", 0.03)]
        public void Parses_chance(string? value, double expected)
        {
            Assert.Equal(expected, RandomGifModule.ParseChance(value, 0.03), 6);
        }

        [Fact]
        public void Creates_folder_and_lists_only_gifs()
        {
            var module = new RandomGifModule(_folder, 1, TimeSpan.Zero);
            Assert.True(Directory.Exists(_folder));

            File.WriteAllText(Path.Combine(_folder, "a.gif"), "");
            File.WriteAllText(Path.Combine(_folder, "b.MP4"), "");
            File.WriteAllText(Path.Combine(_folder, "c.txt"), "");

            Assert.Equal(["a.gif", "b.MP4"], module.ListGifs().Select(Path.GetFileName).Order());
        }

        [Fact]
        public void Always_replies_with_full_chance()
        {
            var module = new RandomGifModule(_folder, 1, TimeSpan.FromMinutes(10));

            Assert.True(module.ShouldReply(-1, Now));
        }

        [Fact]
        public void Never_replies_with_zero_chance()
        {
            var module = new RandomGifModule(_folder, 0, TimeSpan.Zero);

            Assert.DoesNotContain(true, Enumerable.Range(0, 100).Select(i => module.ShouldReply(-1, Now.AddMinutes(i))));
        }

        [Fact]
        public void Chance_is_roughly_respected()
        {
            var module = new RandomGifModule(_folder, 0.1, TimeSpan.Zero, new Random(7));

            int hits = Enumerable.Range(0, 10000).Count(i => module.ShouldReply(-1, Now.AddSeconds(i)));

            Assert.InRange(hits, 800, 1200);
        }

        [Theory]
        [InlineData("привет всем", true)]
        [InlineData("/grow", false)]
        [InlineData("смотри https://example.com", false)]
        public void Skips_commands_and_links(string text, bool expected)
        {
            var message = new Message { Text = text, From = new User { Id = 1, FirstName = "Вася" }, Chat = new Chat { Id = -1 } };

            Assert.Equal(expected, RandomGifModule.IsCandidate(message));
        }

        [Fact]
        public void Skips_bots()
        {
            var message = new Message { Text = "привет", From = new User { Id = 1, FirstName = "Бот", IsBot = true }, Chat = new Chat { Id = -1 } };

            Assert.False(RandomGifModule.IsCandidate(message));
        }
    }
}
