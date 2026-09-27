using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class OwnerToolsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"log_test_{Guid.NewGuid():N}");

        public OwnerToolsTests() => Directory.CreateDirectory(_dir);

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        [Fact]
        public void Mute_list_shows_names_usernames_and_reasons()
        {
            Assert.Contains("никого", SpamModule.BuildMuteList([]));

            string list = SpamModule.BuildMuteList(
            [
                new MuteStore.MutedUser { UserId = 2, Name = "Петя", Reason = "флуд", At = new DateTime(2026, 9, 27, 13, 0, 0) },
                new MuteStore.MutedUser { UserId = 1, Name = "Вася", Username = "vasya", Reason = "спам", At = new DateTime(2026, 9, 27, 12, 40, 0) },
            ]);

            Assert.Contains("🔇 В муте (2):", list);
            Assert.Contains("1. Вася (@vasya) — спам, 27.09 12:40", list);
            Assert.Contains("2. Петя — флуд, 27.09 13:00", list);
        }

        [Theory]
        [InlineData("", 30)]
        [InlineData("80", 80)]
        [InlineData("1000", 200)]
        [InlineData("-5", 30)]
        [InlineData("abc", 30)]
        public void Log_line_count(string args, int expected)
        {
            Assert.Equal(expected, BotWorker.ParseLogLines(args));
        }

        [Fact]
        public void Reads_tail_of_newest_log()
        {
            File.WriteAllLines(Path.Combine(_dir, "bot-20260926.log"), ["старый лог"]);
            File.SetLastWriteTime(Path.Combine(_dir, "bot-20260926.log"), DateTime.Now.AddDays(-1));
            File.WriteAllLines(Path.Combine(_dir, "bot-20260927.log"), Enumerable.Range(1, 100).Select(i => $"строка {i}"));

            string tail = FileLog.ReadTail(_dir, 3);

            Assert.Equal("строка 98\nстрока 99\nстрока 100", tail);
        }

        [Fact]
        public void Tail_fits_into_one_telegram_message()
        {
            File.WriteAllLines(Path.Combine(_dir, "bot-20260927.log"), Enumerable.Range(1, 200).Select(i => $"{i} " + new string('x', 100)));

            string tail = FileLog.ReadTail(_dir, 200);

            Assert.True(tail.Length <= 4000);
            Assert.EndsWith("200 " + new string('x', 100), tail);
        }

        [Fact]
        public void Empty_log_directory()
        {
            Assert.Equal("Лог пуст.", FileLog.ReadTail(Path.Combine(_dir, "missing"), 30));
        }
    }
}
