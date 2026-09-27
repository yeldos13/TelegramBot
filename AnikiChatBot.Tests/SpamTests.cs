using AnikiChatBot.Modules;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Tests
{
    public class SpamTests : IDisposable
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        private readonly string _muteFile = $"muted_test_{Guid.NewGuid():N}.json";
        private readonly string _newcomersFile = $"newcomers_test_{Guid.NewGuid():N}.json";

        public void Dispose()
        {
            File.Delete(_muteFile);
            File.Delete(_newcomersFile);
        }

        private SpamModule NewSpam() => new SpamModule("owner", new MuteStore(_muteFile));

        [Fact]
        public void Seventh_identical_message_is_spam_and_returns_all_ids()
        {
            var spam = NewSpam();

            for (int i = 1; i <= 6; i++)
                Assert.Null(spam.Register(-1, 5, "text:привет", i, Start.AddSeconds(i * 10)));

            var hit = spam.Register(-1, 5, "text:привет", 7, Start.AddSeconds(70));
            Assert.Equal(SpamModule.HitKind.Spam, hit?.Kind);
            Assert.Equal([1, 2, 3, 4, 5, 6, 7], hit!.MessageIds);

            Assert.Null(spam.Register(-1, 5, "text:привет", 8, Start.AddSeconds(80)));
        }

        [Fact]
        public void Fifteen_different_messages_in_30_seconds_is_flood()
        {
            var spam = NewSpam();

            for (int i = 1; i <= 14; i++)
                Assert.Null(spam.Register(-1, 5, $"text:{i}", i, Start.AddSeconds(i)));

            var hit = spam.Register(-1, 5, "text:15", 15, Start.AddSeconds(15));
            Assert.Equal(SpamModule.HitKind.Flood, hit?.Kind);
            Assert.Equal(15, hit!.MessageIds.Count);
        }

        [Fact]
        public void Fifteen_messages_spread_over_time_is_not_flood()
        {
            var spam = NewSpam();

            for (int i = 1; i <= 20; i++)
                Assert.Null(spam.Register(-1, 5, $"text:{i}", i, Start.AddSeconds(i * 5)));
        }

        [Fact]
        public void Different_messages_users_and_chats_are_counted_separately()
        {
            var spam = NewSpam();

            for (int i = 0; i < 6; i++)
            {
                Assert.Null(spam.Register(-1, 5, "text:a", i, Start.AddSeconds(i * 20)));
                Assert.Null(spam.Register(-1, 6, "text:a", 100 + i, Start.AddSeconds(i * 20)));
                Assert.Null(spam.Register(-2, 5, "text:a", 200 + i, Start.AddSeconds(i * 20)));
            }
        }

        [Fact]
        public void Old_messages_outside_window_are_forgotten()
        {
            var spam = NewSpam();

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
        public void Notice_tags_owner_and_names_reason()
        {
            Assert.EndsWith("@owner", SpamModule.BuildNotice("Вася", SpamModule.HitKind.Spam, true, "owner"));
            Assert.Contains("бессрочный мут за спам", SpamModule.BuildNotice("Вася", SpamModule.HitKind.Spam, true, null));
            Assert.Contains("за флуд (15 сообщений за 30 секунд)", SpamModule.BuildNotice("Вася", SpamModule.HitKind.Flood, true, null));
            Assert.Contains("замьютить не получилось", SpamModule.BuildNotice("Вася", SpamModule.HitKind.Spam, false, null));
        }

        [Fact]
        public void Mute_store_finds_by_username_and_survives_restart()
        {
            var store = new MuteStore(_muteFile);
            store.Add(-1, new MuteStore.MutedUser { UserId = 5, Username = "Spammer", Name = "Спамер" });

            var restarted = new MuteStore(_muteFile);
            Assert.Equal(5, restarted.FindByUsername(-1, "@spammer")?.UserId);
            Assert.Null(restarted.FindByUsername(-2, "spammer"));

            restarted.Remove(-1, 5);
            Assert.Null(new MuteStore(_muteFile).FindByUsername(-1, "spammer"));
        }

        [Theory]
        [InlineData("заходи https://spam.com", true)]
        [InlineData("канал t.me/spamchannel", true)]
        [InlineData("www.example.org", true)]
        [InlineData("просто текст", false)]
        [InlineData("почта test@mail.ru", false)]
        public void Detects_links(string text, bool expected)
        {
            Assert.Equal(expected, NewcomerLinksModule.HasLink(new Message { Text = text }));
        }

        [Fact]
        public void Detects_hidden_text_links()
        {
            var message = new Message
            {
                Text = "нажми сюда",
                Entities = [new MessageEntity { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = "https://spam.com" }]
            };

            Assert.True(NewcomerLinksModule.HasLink(message));
        }

        [Fact]
        public void Newcomer_status_lasts_24_hours_and_survives_restart()
        {
            var module = new NewcomerLinksModule(new OwnerNotifier("owner"), _newcomersFile);
            module.RecordJoin(-1, 5, Start);

            var restarted = new NewcomerLinksModule(new OwnerNotifier("owner"), _newcomersFile);
            Assert.True(restarted.IsNewcomer(-1, 5, Start.AddHours(23)));
            Assert.False(restarted.IsNewcomer(-1, 5, Start.AddHours(25)));
            Assert.False(restarted.IsNewcomer(-1, 6, Start));
            Assert.False(restarted.IsNewcomer(-2, 5, Start));
        }

        [Fact]
        public void Joins_are_recorded_from_service_messages()
        {
            var module = new NewcomerLinksModule(new OwnerNotifier("owner"), _newcomersFile);
            module.HandleJoins(new Update
            {
                Message = new Message
                {
                    Chat = new Chat { Id = -1 },
                    NewChatMembers = [new User { Id = 5, FirstName = "Новичок" }, new User { Id = 6, FirstName = "Бот", IsBot = true }]
                }
            });

            Assert.True(module.IsNewcomer(-1, 5, DateTime.UtcNow));
            Assert.False(module.IsNewcomer(-1, 6, DateTime.UtcNow));
        }
    }
}
