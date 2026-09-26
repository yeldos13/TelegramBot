using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Tests
{
    public class OwnerNotifierTests : IDisposable
    {
        public OwnerNotifierTests() => File.Delete("owner_id.txt");

        public void Dispose() => File.Delete("owner_id.txt");

        private static Message CreateMessage(string? username, long userId, ChatType chatType, long chatId) => new Message
        {
            From = new User { Id = userId, Username = username, FirstName = "Test" },
            Chat = new Chat { Id = chatId, Type = chatType }
        };

        [Fact]
        public void Registers_owner_from_private_chat()
        {
            var notifier = new OwnerNotifier("@owner");

            Assert.True(notifier.TryRegisterOwner(CreateMessage("owner", 42, ChatType.Private, 42)));
            Assert.True(notifier.HasOwner);
            Assert.Equal("42", File.ReadAllText("owner_id.txt"));

            Assert.True(new OwnerNotifier("owner").HasOwner);
        }

        [Fact]
        public void Registers_owner_from_group_but_does_not_treat_it_as_private()
        {
            var notifier = new OwnerNotifier("owner");

            Assert.False(notifier.TryRegisterOwner(CreateMessage("owner", 42, ChatType.Supergroup, -100)));
            Assert.True(notifier.HasOwner);
        }

        [Fact]
        public void Ignores_other_users()
        {
            var notifier = new OwnerNotifier("owner");

            Assert.False(notifier.TryRegisterOwner(CreateMessage("someone", 7, ChatType.Private, 7)));
            Assert.False(notifier.TryRegisterOwner(CreateMessage(null, 8, ChatType.Private, 8)));
            Assert.False(notifier.HasOwner);
        }

        [Fact]
        public void Sends_same_alert_at_most_once_per_hour()
        {
            var notifier = new OwnerNotifier("owner");

            Assert.True(notifier.ShouldSend("media:XSource"));
            Assert.False(notifier.ShouldSend("media:XSource"));
            Assert.True(notifier.ShouldSend("currency"));
        }
    }
}
