using System.Text.Json;

namespace AnikiChatBot.Modules
{
    public class CryptoRates
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);

        public static readonly IReadOnlyDictionary<string, string> Coins = new Dictionary<string, string>
        {
            ["BTC"] = "bitcoin",
            ["ETH"] = "ethereum",
            ["USDT"] = "tether",
            ["USDC"] = "usd-coin",
            ["TON"] = "the-open-network",
            ["SOL"] = "solana",
            ["BNB"] = "binancecoin",
            ["XRP"] = "ripple",
            ["DOGE"] = "dogecoin",
            ["LTC"] = "litecoin",
            ["TRX"] = "tron",
        };

        public static bool IsCrypto(string code) => Coins.ContainsKey(code);

        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private Dictionary<string, double> _rubPrices = new();
        private DateTime _updated = DateTime.MinValue;

        public CryptoRates(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<Dictionary<string, double>?> GetRubPricesAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _updated < CacheLifetime && _rubPrices.Count > 0)
                    return _rubPrices;

                try
                {
                    using var timeout = new CancellationTokenSource(ApiTimeout);
                    string ids = string.Join(",", Coins.Values);
                    string json = await _httpClient.GetStringAsync(
                        $"https://api.coingecko.com/api/v3/simple/price?ids={ids}&vs_currencies=rub", timeout.Token);

                    _rubPrices = ParseRubPrices(json);
                    _updated = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Crypto] Не удалось получить курсы криптовалют: {ex.Message}");
                }

                return _rubPrices.Count > 0 ? _rubPrices : null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public static Dictionary<string, double> ParseRubPrices(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var prices = new Dictionary<string, double>();

            foreach (var (code, id) in Coins)
            {
                if (doc.RootElement.TryGetProperty(id, out var coin)
                    && coin.TryGetProperty("rub", out var rub)
                    && rub.ValueKind == JsonValueKind.Number)
                {
                    prices[code] = rub.GetDouble();
                }
            }

            return prices;
        }
    }
}
