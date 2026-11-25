using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class WarnTests : IDisposable
    {
        private readonly string _file = $"warns_test_{Guid.NewGuid():N}.json";
        private readonly string _muteFile = $"muted_test_{Guid.NewGuid():N}.json";

        public void Dispose()
        {
            File.Delete(_file);
            File.Delete(_muteFile);
        }

        [Fact]
        public void Counts_warns_per_chat_and_resets_at_limit()
        {
            var warns = new WarnModule(new MuteStore(_muteFile), _file);
            Assert.Equal(1, warns.Add(-1, 5));
            Assert.Equal(1, warns.Add(-2, 5));
            Assert.Equal(2, new WarnModule(new MuteStore(_muteFile), _file).Add(-1, 5));

            warns = new WarnModule(new MuteStore(_muteFile), _file);
            Assert.Equal(WarnModule.Limit, warns.Add(-1, 5));
            Assert.Equal(1, warns.Add(-1, 5));
        }

        [Fact]
        public void Unwarn_clears_warns()
        {
            var warns = new WarnModule(new MuteStore(_muteFile), _file);
            warns.Add(-1, 5);
            Assert.True(warns.Reset(-1, 5));
            Assert.False(warns.Reset(-1, 5));
            Assert.Equal(1, warns.Add(-1, 5));
        }
    }
}
