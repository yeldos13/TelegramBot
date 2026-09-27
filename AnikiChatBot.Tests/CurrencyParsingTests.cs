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
        [InlineData("10 000 тг", 10000, "KZT")]
        [InlineData("1 500 000 рублей", 1500000, "RUB")]
        [InlineData("1,000$", 1000, "USD")]
        [InlineData("1,000,000 рублей", 1000000, "RUB")]
        [InlineData("12.500 евро", 12500, "EUR")]
        [InlineData("1.234,56 евро", 1234.56, "EUR")]
        [InlineData("1,234.56$", 1234.56, "USD")]
        [InlineData("0.125 btc", 0.125, "BTC")]
        [InlineData("1234,567 тг", 1234.567, "KZT")]
        [InlineData("5к рублей", 5000, "RUB")]
        [InlineData("5k$", 5000, "USD")]
        [InlineData("$5k", 5000, "USD")]
        [InlineData("10 тыс руб", 10000, "RUB")]
        [InlineData("2.5 млн тенге", 2500000, "KZT")]
        [InlineData("1кк рублей", 1000000, "RUB")]
        [InlineData("в 2020 году 100$", 100, "USD")]
        public void Parses_thousands_separators_and_multipliers(string text, double expectedAmount, string expectedCurrency)
        {
            Assert.True(CurrencyModule.TryParseAmount(text, out double amount, out string currency));
            Assert.Equal(expectedAmount, amount, precision: 6);
            Assert.Equal(expectedCurrency, currency);
        }

        [Fact]
        public void Finds_all_amounts_in_order()
        {
            var amounts = CurrencyModule.ParseAmounts("было 100$, стало 50€ и ещё 20 GBP");

            Assert.Equal(
                [new CurrencyModule.ParsedAmount(100, "USD"), new CurrencyModule.ParsedAmount(50, "EUR"), new CurrencyModule.ParsedAmount(20, "GBP")],
                amounts);
        }

        [Fact]
        public void Skips_zero_and_duplicate_amounts()
        {
            Assert.Empty(CurrencyModule.ParseAmounts("0$"));
            Assert.Single(CurrencyModule.ParseAmounts("100$ или всё-таки 100$?"));
        }

        [Theory]
        [InlineData("привет")]
        [InlineData("просто число 100")]
        [InlineData("купил 5 рыбок")]
        [InlineData("через 3 бра")]
        [InlineData("5 кг картошки")]
        [InlineData("0$")]
        [InlineData("")]
        public void Ignores_text_without_currency(string text)
        {
            Assert.False(CurrencyModule.TryParseAmount(text, out _, out _));
        }
    }
}
