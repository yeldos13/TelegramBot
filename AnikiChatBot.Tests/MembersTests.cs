using AnikiChatBot.Modules;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Tests
{
    public class MembersTests
    {
        private const long BotId = 999;
        private static readonly Chat Group = new Chat { Id = -100, Type = ChatType.Supergroup };
        private static readonly User Vasya = new User { Id = 1, FirstName = "Вася", LastName = "Пупкин", Username = "vasya" };
        private static readonly User Admin = new User { Id = 2, FirstName = "Админ" };

        private static Update MemberChange(User user, User from, ChatMember oldMember, ChatMember newMember) => new Update
        {
            ChatMember = new ChatMemberUpdated
            {
                Chat = Group,
                From = from,
                Date = DateTime.UtcNow,
                OldChatMember = oldMember,
                NewChatMember = newMember
            }
        };

        [Fact]
        public void Detects_member_who_left()
        {
            var update = MemberChange(Vasya, Vasya, new ChatMemberMember { User = Vasya }, new ChatMemberLeft { User = Vasya });

            var left = MembersModule.GetLeftMember(update, BotId);

            Assert.NotNull(left);
            Assert.Equal(-100, left.ChatId);
            Assert.Equal(Vasya.Id, left.User.Id);
        }

        [Fact]
        public void Ignores_kicks_and_bans()
        {
            var banned = MemberChange(Vasya, Admin, new ChatMemberMember { User = Vasya }, new ChatMemberBanned { User = Vasya });
            var removed = MemberChange(Vasya, Admin, new ChatMemberMember { User = Vasya }, new ChatMemberLeft { User = Vasya });

            Assert.Null(MembersModule.GetLeftMember(banned, BotId));
            Assert.Null(MembersModule.GetLeftMember(removed, BotId));
        }

        [Fact]
        public void Ignores_joins_and_promotions()
        {
            var joined = MemberChange(Vasya, Vasya, new ChatMemberLeft { User = Vasya }, new ChatMemberMember { User = Vasya });
            var promoted = MemberChange(Vasya, Admin, new ChatMemberMember { User = Vasya }, new ChatMemberAdministrator { User = Vasya });

            Assert.Null(MembersModule.GetLeftMember(joined, BotId));
            Assert.Null(MembersModule.GetLeftMember(promoted, BotId));
        }

        [Fact]
        public void Ignores_ban_of_user_who_was_not_in_chat()
        {
            var update = MemberChange(Vasya, Admin, new ChatMemberLeft { User = Vasya }, new ChatMemberBanned { User = Vasya });

            Assert.Null(MembersModule.GetLeftMember(update, BotId));
        }

        [Fact]
        public void Ignores_bot_itself()
        {
            var bot = new User { Id = BotId, FirstName = "Bot", IsBot = true };
            var update = MemberChange(bot, Admin, new ChatMemberMember { User = bot }, new ChatMemberBanned { User = bot });

            Assert.Null(MembersModule.GetLeftMember(update, BotId));
        }

        [Fact]
        public void Detects_left_member_service_message()
        {
            var selfLeft = new Update { Message = new Message { Chat = Group, From = Vasya, LeftChatMember = Vasya } };
            var removed = new Update { Message = new Message { Chat = Group, From = Admin, LeftChatMember = Vasya } };

            Assert.NotNull(MembersModule.GetLeftMember(selfLeft, BotId));
            Assert.Null(MembersModule.GetLeftMember(removed, BotId));
        }

        [Fact]
        public void Builds_text_with_owner_tag()
        {
            Assert.Equal("Вася Пупкин (@vasya) покинул(а) чат @owner", MembersModule.BuildLeaveText(Vasya, "@owner"));
            Assert.Equal("Админ покинул(а) чат @owner", MembersModule.BuildLeaveText(Admin, "owner"));
            Assert.Equal("Админ покинул(а) чат", MembersModule.BuildLeaveText(Admin, null));
        }

        [Theory]
        [InlineData(0, 0, 0, "1 мин")]
        [InlineData(0, 0, 42, "42 мин")]
        [InlineData(0, 3, 5, "3 ч 5 мин")]
        [InlineData(2, 4, 30, "2 д 4 ч")]
        public void Formats_uptime(int days, int hours, int minutes, string expected)
        {
            Assert.Equal(expected, BotWorker.FormatUptime(new TimeSpan(days, hours, minutes, 0)));
        }
    }
}
