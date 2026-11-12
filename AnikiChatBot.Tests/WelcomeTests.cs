using AnikiChatBot.Modules;
using Telegram.Bot.Types;

namespace AnikiChatBot.Tests
{
    public class WelcomeTests
    {
        private const string Font = @"C:\Windows\Fonts\segoeuib.ttf";
        private const string Ffmpeg = @"C:\FFMPEG\bin\ffmpeg.exe";

        [Fact]
        public void Joined_members_skip_bots()
        {
            var update = new Update
            {
                Message = new Message
                {
                    Chat = new Chat { Id = 1 },
                    NewChatMembers = [new User { Id = 7, FirstName = "Аня" }, new User { Id = 8, FirstName = "Bot", IsBot = true }],
                }
            };

            Assert.Equal([7L], MembersModule.GetJoinedMembers(update).Select(x => x.User.Id));
        }

        [Fact]
        public void Text_uses_username_drops_missing_glyphs_and_shrinks_long_names()
        {
            if (!File.Exists(Font))
                return;

            var module = new WelcomeModule("welcome.mp4", Ffmpeg, Font);

            var short_ = module.BuildText(new User { Username = "riona", FirstName = "Риона" });
            Assert.Equal(["Welcome to the group! riona-kun!"], short_.Lines);
            Assert.Equal(26f, short_.Size);
            Assert.Equal(["Welcome to the group! Аня-kun!"], module.BuildText(new User { FirstName = "Аня 🦊" }).Lines);
            Assert.Equal(["Welcome to the group! newbie-kun!"], module.BuildText(new User { FirstName = "🦊" }).Lines);

            var long_ = module.BuildText(new User { Username = "Konstantin_Konstantinopolsky" });
            Assert.Equal(["Welcome to the group!", "Konstantin_Konstantinopolsky-kun!"], long_.Lines);
            Assert.Equal(26f, long_.Size);

            Assert.True(module.BuildText(new User { FirstName = new string('Ш', 32) }).Size < 26f);
        }

        [Fact]
        public async Task Renders_video_when_template_is_available()
        {
            string? video = Environment.GetEnvironmentVariable("ANIKI_WELCOME_VIDEO");
            if (video == null || !File.Exists(video) || !File.Exists(Ffmpeg) || !File.Exists(Font))
                return;

            var module = new WelcomeModule(video, Ffmpeg, Font);
            foreach (var (folder, name) in new[] { ("short", "riona"), ("long", "Konstantin_Konstantinopolsky") })
            {
                string dir = Path.Combine(Path.GetTempPath(), "aniki_welcome_test", folder);
                var output = await module.RenderAsync(new User { Username = name }, dir, default);

                Assert.NotNull(output);
                Assert.True(new FileInfo(output).Length > 10_000);
            }
        }
    }
}
