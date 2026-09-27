using AnikiChatBot.Modules;
using Telegram.Bot.Types;

namespace AnikiChatBot.Tests
{
    public class SpamTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Seventh_identical_message_triggers_and_returns_all_ids()
        {
            var spam = new SpamModule("owner");

            for (int i = 1; i <= 6; i++)
                Assert.Null(spam.Register(-1, 5, "text:привет", i, Start.AddSeconds(i)));

            var ids = spam.Register(-1, 5, "text:привет", 7, Start.AddSeconds(7));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7], ids);

            Assert.Null(spam.Register(-1, 5, "text:привет", 8, Start.AddSeconds(8)));
        }

        [Fact]
        public void Different_messages_users_and_chats_are_counted_separately()
        {
            var spam = new SpamModule(null);

            for (int i = 0; i < 6; i++)
            {
                Assert.Null(spam.Register(-1, 5, "text:a", i, Start));
                Assert.Null(spam.Register(-1, 6, "text:a", 100 + i, Start));
                Assert.Null(spam.Register(-2, 5, "text:a", 200 + i, Start));
                Assert.Null(spam.Register(-1, 5, "text:b", 300 + i, Start));
            }
        }

        [Fact]
        public void Old_messages_outside_window_are_forgotten()
        {
            var spam = new SpamModule(null);

            for (int i = 0; i < 6; i++)
                spam.Register(-1, 5, "text:a", i, Start);

            Assert.Null(spam.Register(-1, 5, "text:a", 7, Start.AddMinutes(3)));
        }

        [Theory]
        [InlineData("Привет", "  привет ")]
        [InlineData("SPAM", "spam")]
        public void Text_comparison_ignores_case_and_spaces(string a, string b)
        {
            Assert.Equal(
                SpamModule.GetKey(new Message { Text = a }),
                SpamModule.GetKey(new Message { Text = b }));
        }

        [Fact]
        public void Same_sticker_is_spam_but_empty_message_is_not()
        {
            var sticker = new Message { Sticker = new Sticker { FileId = "x", FileUniqueId = "st1" } };
            var otherSticker = new Message { Sticker = new Sticker { FileId = "y", FileUniqueId = "st2" } };

            Assert.NotNull(SpamModule.GetKey(sticker));
            Assert.NotEqual(SpamModule.GetKey(sticker), SpamModule.GetKey(otherSticker));
            Assert.Null(SpamModule.GetKey(new Message()));
        }

        [Fact]
        public void Notice_tags_owner()
        {
            Assert.EndsWith("@owner", SpamModule.BuildNotice("Вася", muted: true, "owner"));
            Assert.Contains("бессрочный мут", SpamModule.BuildNotice("Вася", muted: true, null));
            Assert.Contains("замьютить не получилось", SpamModule.BuildNotice("Вася", muted: false, null));
        }
    }
}
