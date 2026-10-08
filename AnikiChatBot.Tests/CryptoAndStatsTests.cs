using AnikiChatBot.Modules;
using Telegram.Bot.Types;

namespace AnikiChatBot.Tests
{
    public class CryptoAndStatsTests : IDisposable
    {
        private readonly string _file = $"stats_crypto_{Guid.NewGuid():N}.json";

        public void Dispose() => File.Delete(_file);

        private static readonly Dictionary<string, double> Rates = new()
        {
            ["USD"] = 0.0125, ["EUR"] = 0.0107, ["KZT"] = 6.0, ["UAH"] = 0.5, ["BYN"] = 0.04, ["CAD"] = 0.017, ["GBP"] = 0.0093
        };

        private static readonly Dictionary<string, double> Crypto = new() { ["BTC"] = 7_000_000, ["USDT"] = 80, ["TON"] = 120 };

        [Theory]
        [InlineData("0.1 btc", 0.1, "BTC")]
        [InlineData("50 usdt", 50, "USDT")]
        [InlineData("₿0.5", 0.5, "BTC")]
        [InlineData("2 биткоина", 2, "BTC")]
        [InlineData("100 TON", 100, "TON")]
        [InlineData("1000 doge", 1000, "DOGE")]
        public void Parses_crypto_amounts(string text, double amount, string currency)
        {
            Assert.True(CurrencyModule.TryParseAmount(text, out double a, out string c));
            Assert.Equal(amount, a, precision: 6);
            Assert.Equal(currency, c);
        }

        [Fact]
        public void Converts_crypto_and_fiat_to_rubles()
        {
            Assert.Equal(700_000, CurrencyModule.ToRub(0.1, "BTC", Rates, Crypto)!.Value, precision: 3);
            Assert.Equal(8000, CurrencyModule.ToRub(100, "USD", Rates, Crypto)!.Value, precision: 3);
            Assert.Equal(100, CurrencyModule.ToRub(100, "RUB", Rates, null));

            Assert.Null(CurrencyModule.ToRub(5, "DAYS", Rates, Crypto));

            Assert.Null(CurrencyModule.ToRub(1, "BTC", Rates, null));
            Assert.Null(CurrencyModule.ToRub(1, "ETH", Rates, Crypto));
        }

        [Fact]
        public void Small_amounts_keep_significant_digits()
        {
            Assert.Equal(1234.5.ToString("N2"), CurrencyModule.FormatAmount(1234.5));
            Assert.Equal(0.00123.ToString("0.########"), CurrencyModule.FormatAmount(0.00123));
            Assert.NotEqual(0.0.ToString("N2"), CurrencyModule.FormatAmount(0.001));
        }

        [Fact]
        public void Parses_coingecko_response()
        {
            var prices = CryptoRates.ParseRubPrices(
                """{"bitcoin":{"rub":6998836},"tether":{"rub":83.37},"the-open-network":{"rub":124.93},"unknown":{"rub":1}}""");

            Assert.Equal(6998836, prices["BTC"]);
            Assert.Equal(83.37, prices["USDT"]);
            Assert.Equal(124.93, prices["TON"]);
            Assert.False(prices.ContainsKey("ETH"));
        }

        [Fact]
        public void Rates_table_lists_fiat_and_crypto()
        {
            string table = CurrencyModule.BuildRatesTable(Rates, Crypto, new DateTime(2026, 9, 30, 16, 45, 0));

            Assert.StartsWith("💱 Курсы на 30.09 16:45", table);
            Assert.Contains($"1 USD = {80.0:N2} ₽ · {480.0:N2} ₸", table);
            Assert.Contains($"🇷🇺 1 RUB = {6.0:N2} ₸", table);
            Assert.Contains($"🪙 1 BTC = {87500.0:N2} $ · {7000000.0:N2} ₽", table);
            Assert.DoesNotContain("ETH", table);
        }

        [Fact]
        public void Rates_table_without_crypto()
        {
            string table = CurrencyModule.BuildRatesTable(Rates, null, DateTime.Now);

            Assert.Contains("USD", table);
            Assert.DoesNotContain("🪙", table);
        }

        [Fact]
        public void Personal_stats_show_messages_rank_and_game()
        {
            var stats = new StatsService(_file);
            var fun = new FunModule(stats);
            var vasya = new User { Id = 1, FirstName = "Вася" };

            Assert.Contains("ещё не писал", fun.Stats(-1, vasya));

            for (int i = 0; i < 3; i++) stats.RecordMessage(-1, 1, "Вася");
            for (int i = 0; i < 5; i++) stats.RecordMessage(-1, 2, "Петя");
            stats.RecordGrowth(-1, 1, "Вася", 12);
            stats.RecordDuelWin(-1, 1, "Вася");

            string text = fun.Stats(-1, vasya);
            Assert.Contains("💬 Сообщений: 3 — 2-е место из 2", text);
            Assert.Contains("🐷 Игра: +12 т, побед в дуэлях: 1", text);
        }
    }
}
