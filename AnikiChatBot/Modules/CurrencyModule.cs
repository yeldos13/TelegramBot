using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class CurrencyModule
    {
        private const string CacheFilePath = "rates_cache.json";
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

        private static readonly Regex NamedCurrencyRegex = new Regex(
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:рубл[яьей]+|руб|р|₽)(?!\w))|" +
            @"(?:(?:\$|доллар[аов]*|бакс[аов]*)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:\$|доллар[аов]*|бакс[аов]*)(?!\w))|" +
            @"(?:(?:€|евро)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:€|евро)(?!\w))|" +
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:тенге|тг|kzt|₸)(?!\w))|" +
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:грив[еньеяидлз]*|грн|uah|₴)(?!\w))|" +
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:бел\.?\s*руб(?:л[яьей]+|ь)?|бр|byn)(?!\w))|" +
            @"(?:(?:c\$)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:c\$|cad|канадск[аиоыхьйе]*\s*доллар[аов]*)(?!\w))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] NamedGroupCurrencies =
            ["", "RUB", "USD", "USD", "EUR", "EUR", "KZT", "UAH", "BYN", "CAD", "CAD"];

        private static readonly Regex GenericCurrencyRegex = new Regex(
            @"(?:(\d+(?:[.,]\d+)?)\s*([a-zA-Z]{3})\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] PopularCurrencies = ["RUB", "USD", "EUR", "KZT", "UAH", "BYN", "CAD"];

        private readonly HttpClient _httpClient;
        private readonly string _exchangeApiKey;
        private readonly OwnerNotifier? _notifier;
        private readonly SemaphoreSlim _ratesLock = new(1, 1);

        private DateTime _lastRatesUpdate = DateTime.MinValue;
        private Dictionary<string, double> _cachedRates = new();

        public DateTime LastRatesUpdateUtc => _lastRatesUpdate;

        public CurrencyModule(HttpClient httpClient, string exchangeApiKey, OwnerNotifier? notifier = null)
        {
            _httpClient = httpClient;
            _exchangeApiKey = exchangeApiKey;
            _notifier = notifier;
            LoadCache();
        }

        public async Task HandleCurrencyCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text is not { } text || string.IsNullOrEmpty(text)) return;

            if (!TryParseAmount(text, out double originalAmount, out string sourceCurrency)) return;

            var rates = await GetExchangeRatesAsync();
            if (rates == null) return;

            if (sourceCurrency != "RUB" && !rates.ContainsKey(sourceCurrency)) return;

            double rubAmount = sourceCurrency == "RUB" ? originalAmount : (originalAmount / rates[sourceCurrency]);

            var currencies = PopularCurrencies.ToList();
            if (!currencies.Contains(sourceCurrency))
                currencies.Insert(0, sourceCurrency);

            var sb = new StringBuilder();
            sb.AppendLine($"*{originalAmount:N2} {GetCurrencyTargetInfo(sourceCurrency)}:*");
            sb.AppendLine();

            foreach (var currency in currencies)
            {
                if (currency == sourceCurrency) continue;

                double targetAmount;
                if (currency == "RUB")
                {
                    targetAmount = rubAmount;
                }
                else
                {
                    if (!rates.TryGetValue(currency, out double rate)) continue;
                    targetAmount = rubAmount * rate;
                }

                sb.AppendLine($"{GetCurrencyTargetInfo(currency)} *{currency}:* {targetAmount:N2}");
            }

            Message sentMessage = await bot.SendMessage(
                chatId: update.Message.Chat.Id,
                text: sb.ToString().TrimEnd(),
                parseMode: ParseMode.Markdown,
                replyParameters: new ReplyParameters { MessageId = update.Message.Id },
                cancellationToken: ct
            );
        }

        public static bool TryParseAmount(string text, out double amount, out string currency)
        {
            amount = 0;
            currency = "";
            string amountStr = "";

            var namedMatch = NamedCurrencyRegex.Match(text);
            if (namedMatch.Success)
            {
                for (int group = 1; group < NamedGroupCurrencies.Length; group++)
                {
                    if (!string.IsNullOrEmpty(namedMatch.Groups[group].Value))
                    {
                        amountStr = namedMatch.Groups[group].Value;
                        currency = NamedGroupCurrencies[group];
                        break;
                    }
                }
            }
            else
            {
                var genericMatch = GenericCurrencyRegex.Match(text);
                if (genericMatch.Success)
                {
                    amountStr = genericMatch.Groups[1].Value;
                    currency = genericMatch.Groups[2].Value.ToUpperInvariant();
                }
            }

            if (string.IsNullOrEmpty(currency))
                return false;

            return double.TryParse(amountStr.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
        }

        private async Task DeleteMessageAfterDelayAsync(ITelegramBotClient bot, long chatId, int messageId, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
                await bot.DeleteMessage(chatId, messageId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CurrencyModule] Ошибка при автоудалении сообщения: {ex.Message}");
            }
        }

        private static string GetCurrencyTargetInfo(string code)
        {
            return code switch
            {
                "USD" => "🇺🇸 $",
                "EUR" => "🇪🇺 €",
                "RUB" => "🇷🇺 ₽",
                "KZT" => "🇰🇿 ₸",
                "UAH" => "🇺🇦 ₴",
                "BYN" => "🇧🇾 Б",
                "CAD" => "🇨🇦 C$",
                _ => $"💰 {code}"
            };
        }

        private async Task<Dictionary<string, double>?> GetExchangeRatesAsync()
        {
            await _ratesLock.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _lastRatesUpdate < CacheLifetime && _cachedRates.Count > 0)
                    return _cachedRates;

                try
                {
                    string url = $"https://v6.exchangerate-api.com/v6/{_exchangeApiKey}/latest/RUB";
                    string jsonString = await _httpClient.GetStringAsync(url);

                    using JsonDocument doc = JsonDocument.Parse(jsonString);
                    JsonElement root = doc.RootElement;

                    if (root.GetProperty("result").GetString() == "success")
                    {
                        var rates = new Dictionary<string, double>();
                        foreach (var property in root.GetProperty("conversion_rates").EnumerateObject())
                            rates[property.Name] = property.Value.GetDouble();

                        _cachedRates = rates;
                        _lastRatesUpdate = DateTime.UtcNow;
                        SaveCache();
                    }
                    else
                    {
                        string errorType = root.TryGetProperty("error-type", out var errorProp) ? errorProp.GetString() ?? "" : "";
                        Console.Error.WriteLine($"Exchange rates API returned error: {errorType}");
                        _notifier?.Notify("currency", $"API курсов валют вернул ошибку: {errorType}. Используется кэш от {_lastRatesUpdate:dd.MM HH:mm} UTC.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error while getting exchange rates from API: {ex.Message}");
                    _notifier?.Notify("currency", $"Не удалось получить курсы валют: {ex.Message}");
                }

                return _cachedRates.Count > 0 ? _cachedRates : null;
            }
            finally
            {
                _ratesLock.Release();
            }
        }

        private void LoadCache()
        {
            if (!File.Exists(CacheFilePath))
                return;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(CacheFilePath));

                if (doc.RootElement.TryGetProperty("lastRatesUpdate", out var dateProp))
                    _lastRatesUpdate = dateProp.GetDateTime();

                if (doc.RootElement.TryGetProperty("rates", out var ratesProp))
                {
                    foreach (var prop in ratesProp.EnumerateObject())
                        _cachedRates[prop.Name] = prop.Value.GetDouble();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cache] Не удалось прочитать файл кэша при старте: {ex.Message}");
            }
        }

        private void SaveCache()
        {
            try
            {
                var cacheData = new { lastRatesUpdate = _lastRatesUpdate, rates = _cachedRates };
                string serializedCache = JsonSerializer.Serialize(cacheData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CacheFilePath, serializedCache);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CurrencyModule] Не удалось сохранить кэш в файл: {ex.Message}");
            }
        }
    }
}
