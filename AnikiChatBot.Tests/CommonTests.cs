using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class CommonTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"common_test_{Guid.NewGuid():N}");

        public CommonTests() => Directory.CreateDirectory(_dir);

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private record Sample(string Name, int Value);

        [Fact]
        public void Json_file_round_trip()
        {
            string path = Path.Combine(_dir, "a.json");
            JsonFile.Save(path, new Sample("x", 5), "Test");

            Assert.Equal(new Sample("x", 5), JsonFile.Load<Sample>(path, "Test"));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void Broken_json_is_kept_as_copy_and_not_loaded()
        {
            string path = Path.Combine(_dir, "b.json");
            File.WriteAllText(path, "{ не json");

            Assert.Null(JsonFile.Load<Sample>(path, "Test"));
            Assert.True(File.Exists(path + ".broken"));
            Assert.Null(JsonFile.Load<Sample>(Path.Combine(_dir, "missing.json"), "Test"));
        }

        [Fact]
        public void Periodic_saver_saves_only_when_dirty()
        {
            int saves = 0;
            var saver = new PeriodicSaver(() => saves++, new object(), TimeSpan.FromHours(1));

            saver.Flush();
            Assert.Equal(0, saves);

            saver.MarkDirty();
            saver.MarkDirty();
            saver.Flush();
            saver.Flush();
            Assert.Equal(1, saves);

            saver.SaveNow();
            Assert.Equal(2, saves);
        }

        [Fact]
        public void Spam_messages_are_removed_from_activity()
        {
            var stats = new StatsService(Path.Combine(_dir, "stats.json"));
            for (int i = 0; i < 10; i++)
                stats.RecordMessage(-1, 5, "Спамер");

            stats.RemoveMessages(-1, 5, 6);
            Assert.Equal(4, stats.GetChat(-1).Players[5].Messages);

            stats.RemoveMessages(-1, 5, 100);
            Assert.Equal(0, stats.GetChat(-1).Players[5].Messages);
            Assert.Empty(stats.GetActivePlayers(-1));
        }

        [Fact]
        public void Who_skips_muted_users()
        {
            var stats = new StatsService(Path.Combine(_dir, "stats.json"));
            var mutes = new MuteStore(Path.Combine(_dir, "muted.json"));
            stats.RecordMessage(-1, 1, "Вася");
            stats.RecordMessage(-1, 2, "Спамер");
            mutes.Add(-1, new MuteStore.MutedUser { UserId = 2, Name = "Спамер" });

            var fun = new FunModule(stats, new Random(1), mutes);
            for (int i = 0; i < 20; i++)
                Assert.EndsWith("Вася", fun.Who(-1, "кто"));
        }

        [Fact]
        public void Spam_guard_forgets_idle_users()
        {
            var spam = new SpamModule(null, new MuteStore(Path.Combine(_dir, "muted.json")));
            var start = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

            spam.Register(-1, 1, "text:a", 1, start);
            spam.Register(-1, 2, "text:b", 2, start.AddMinutes(5));
            Assert.Equal(2, spam.TrackedUsers);

            spam.RemoveIdleUsers(start.AddMinutes(6));
            Assert.Equal(1, spam.TrackedUsers);
        }
    }
}
