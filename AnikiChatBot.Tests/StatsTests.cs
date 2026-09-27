namespace AnikiChatBot.Tests
{
    public class StatsTests : IDisposable
    {
        private readonly string _file = $"stats_test_{Guid.NewGuid():N}.json";

        public void Dispose() => File.Delete(_file);

        [Theory]
        [InlineData("2026-09-23 12:00", "2026-09-27 20:00")]
        [InlineData("2026-09-27 19:59", "2026-09-27 20:00")]
        [InlineData("2026-09-27 20:00", "2026-10-04 20:00")]
        [InlineData("2026-09-27 21:00", "2026-10-04 20:00")]
        public void Next_report_is_sunday_evening(string from, string expected)
        {
            Assert.Equal(DateTime.Parse(expected), StatsService.NextReportTime(DateTime.Parse(from)));
        }

        [Fact]
        public void Report_is_due_after_sunday_evening_even_if_bot_was_off()
        {
            var stats = new StatsService(_file);
            stats.StartNewPeriod(DateTime.Parse("2026-09-20 20:00"));

            Assert.False(stats.IsReportDue(DateTime.Parse("2026-09-27 19:00")));
            Assert.True(stats.IsReportDue(DateTime.Parse("2026-09-27 20:05")));
            Assert.True(stats.IsReportDue(DateTime.Parse("2026-09-29 09:00")));
        }

        [Fact]
        public void Counts_per_chat_and_survives_restart()
        {
            var stats = new StatsService(_file);
            stats.RecordMedia(-1);
            stats.RecordMedia(-1);
            stats.RecordConversion(-1);
            stats.RecordReply(-2);
            stats.RecordLeft(-1, "Вася");

            var restarted = new StatsService(_file);
            var chat1 = restarted.GetChat(-1);

            Assert.Equal(2, chat1.MediaSent);
            Assert.Equal(1, chat1.Conversions);
            Assert.Equal(0, chat1.RepliesSent);
            Assert.Equal(["Вася"], chat1.LeftMembers);
            Assert.Equal(1, restarted.GetChat(-2).RepliesSent);
            Assert.Equal(0, restarted.GetChat(-3).MediaSent);
        }

        [Fact]
        public void New_period_resets_counters()
        {
            var stats = new StatsService(_file);
            stats.RecordMedia(-1);
            stats.StartNewPeriod(DateTime.Now);

            Assert.Equal(0, stats.GetChat(-1).MediaSent);
        }

        [Fact]
        public void Builds_report_with_owner_tag()
        {
            var chat = new StatsService.ChatStats { MediaSent = 12, Conversions = 30, RepliesSent = 5, LeftMembers = ["Вася (@vasya)", "Петя"] };

            string report = StatsService.BuildReport(chat, DateTime.Parse("2026-09-20"), DateTime.Parse("2026-09-27"), "@owner");

            Assert.Equal(
                "📊 Итоги недели (20.09 – 27.09)\n\n" +
                "Скачано медиа: 12\n" +
                "Конвертаций валют: 30\n" +
                "Автоответов: 5\n" +
                "Покинули чат (2): Вася (@vasya), Петя\n\n" +
                "@owner",
                report.Replace("\r\n", "\n"));
        }

        [Fact]
        public void Report_without_leavers_says_so()
        {
            string report = StatsService.BuildReport(new StatsService.ChatStats(), DateTime.Now, DateTime.Now, null);

            Assert.Contains("Никто не покинул чат", report);
            Assert.DoesNotContain("@", report);
            Assert.DoesNotContain("Игра", report);
        }

        [Fact]
        public void Report_includes_game_section()
        {
            var stats = new StatsService(_file);
            stats.RecordGrowth(-1, 1, "Вася", 15);
            stats.RecordGrowth(-1, 2, "Петя", 30);
            stats.RecordGrowth(-1, 2, "Петя", -5);
            stats.RecordDuelWin(-1, 1, "Вася");
            stats.RecordDuelWin(-1, 1, "Вася");

            string report = StatsService.BuildReport(stats.GetChat(-1), DateTime.Parse("2026-09-20"), DateTime.Parse("2026-09-27"),
                "owner", new StatsService.GameLeader("Вася", 120)).Replace("\r\n", "\n");

            Assert.Contains(
                "🍆 Игра\n" +
                "Самый большой: Вася — 120 см\n" +
                "Больше всех вырос: Петя (+25 см)\n" +
                "Больше всех побед в дуэлях: Вася (2)\n\n" +
                "@owner",
                report);
        }
    }
}
