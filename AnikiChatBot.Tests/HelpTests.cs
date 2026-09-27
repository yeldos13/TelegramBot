using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class HelpTests
    {
        [Fact]
        public void Help_mentions_every_command()
        {
            foreach (var command in PenisModule.Commands.Concat(FunModule.Commands))
                Assert.Contains($"/{command.Command}", HelpModule.Text);
        }

        [Fact]
        public void Help_fits_into_one_message()
        {
            Assert.True(HelpModule.Text.Length < 4096);
        }
    }
}
