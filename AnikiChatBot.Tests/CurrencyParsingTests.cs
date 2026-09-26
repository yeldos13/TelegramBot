using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class CurrencyParsingTests
    {
        [Theory]
        [InlineData("5 р", 5, "RUB")]
        [InlineData("1000 рублей", 1000, "RUB")]
        [InlineData("300₽", 300, "RUB")]
        [InlineData("$100", 100, "USD")]
        [InlineData("100$", 100, "USD")]
        [InlineData("20 баксов", 20, "USD")]
        [InlineData("1,5 доллара", 1.5, "USD")]
        [InlineData("€7", 7, "EUR")]
        [InlineData("3 евро", 3, "EUR")]
        [InlineData("5000 тг", 5000, "KZT")]
        [InlineData("5000 тенге", 5000, "KZT")]
        [InlineData("50 грн", 50, "UAH")]
        [InlineData("10 бр", 10, "BYN")]
        [InlineData("10 бел руб", 10, "BYN")]
        [InlineData("c$20", 20, "CAD")]
        [InlineData("20 cad", 20, "CAD")]
        [InlineData("20 GBP", 20, "GBP")]
        [InlineData("за 2.5 jpy купил", 2.5, "JPY")]
        [InlineData("это стоит 100$ в магазине", 100, "USD")]
        public void Parses_amount_and_currency(string text, double expectedAmount, string expectedCurrency)
        {
            Assert.True(CurrencyModule.TryParseAmount(text, out double amount, out string currency));
            Assert.Equal(expectedAmount, amount, precision: 6);
            Assert.Equal(expectedCurrency, currency);
        }

        [Theory]
        [InlineData("привет")]
        [InlineData("просто число 100")]
        [InlineData("купил 5 рыбок")]
        [InlineData("через 3 бра")]
        [InlineData("")]
        public void Ignores_text_without_currency(string text)
        {
            Assert.False(CurrencyModule.TryParseAmount(text, out _, out _));
        }
    }
}
