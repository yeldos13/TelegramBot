using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class RepeaterTests
    {
        [Theory]
        [InlineData("  привет  ", "привет")]
        [InlineData("строка 1\r\nстрока 2", "строка 1 строка 2")]
        [InlineData("a\nb", "a b")]
        public void Normalizes_text_to_single_line(string input, string expected)
        {
            Assert.Equal(expected, RepeaterModule.Normalize(input));
        }
    }
}
