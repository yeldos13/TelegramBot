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

        private const string Multiplier = @"кк|kk|к|k|тыс\.?|тысяч[аи]?|млн|миллион(?:а|ов)?|млрд";

        private const string Num = @"((?<!\d)(?:\d{1,3}(?:[   ]\d{3})+|\d+)(?:[.,]\d+)*(?:\s?(?:" + Multiplier + @"))?)";

        private static readonly Regex NamedCurrencyRegex = new Regex(
            @"(?:" + Num + @"\s*(?:рубл[яьей]+|руб|р|₽)(?!\w))|" +
            @"(?:(?:\$|доллар[аов]*|бакс[аов]*)\s*" + Num + @"|" + Num + @"\s*(?:\$|доллар[аов]*|бакс[аов]*)(?!\w))|" +
            @"(?:(?:€|евро)\s*" + Num + @"|" + Num + @"\s*(?:€|евро)(?!\w))|" +
            @"(?:" + Num + @"\s*(?:тенге|тг|kzt|₸)(?!\w))|" +
            @"(?:" + Num + @"\s*(?:грив[еньеяидлз]*|грн|uah|₴)(?!\w))|" +
            @"(?:" + Num + @"\s*(?:бел\.?\s*руб(?:л[яьей]+|ь)?|бр|byn)(?!\w))|" +
            @"(?:(?:c\$)\s*" + Num + @"|" + Num + @"\s*(?:c\$|cad|канадск[аиоыхьйе]*\s*доллар[аов]*)(?!\w))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] NamedGroupCurrencies =
            ["", "RUB", "USD", "USD", "EUR", "EUR", "KZT", "UAH", "BYN", "CAD", "CAD"];

        private static readonly Regex GenericCurrencyRegex = new Regex(
            Num + @"\s*([a-zA-Z]{3})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MultiplierRegex = new Regex(
            @"^(.*?)\s?(" + Multiplier + @")$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const int MaxAmountsPerMessage = 3;

        public record ParsedAmount(double Amount, string Currency);

        private static readonly string[] PopularCurrencies = ["RUB", "USD", "EUR", "KZT", "UAH", "BYN", "CAD"];

        private readonly HttpClient _httpClient;
        private readonly string _exchangeApiKey;
        private readonly OwnerNotifier? _notifier;
        private readonly SemaphoreSlim _ratesLock = new(1, 1);

        private DateTime _lastRatesUpdate = DateTime.MinValue;
        private Dictionary<string, double> _cachedRates = new();

        public DateTime LastRatesUpdateUtc => _lastRatesUpdate;

        private readonly StatsService? _stats;

        public CurrencyModule(HttpClient httpClient, string exchangeApiKey, OwnerNotifier? notifier = null, StatsService? stats = null)
        {
            _httpClient = httpClient;
            _exchangeApiKey = exchangeApiKey;
            _notifier = notifier;
            _stats = stats;
            LoadCache();
        }

        public async Task HandleCurrencyCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text is not { } text || string.IsNullOrEmpty(text)) return;

            var amounts = ParseAmounts(text);
            if (amounts.Count == 0) return;

            var rates = await GetExchangeRatesAsync();
            if (rates == null) return;

            var blocks = amounts
                .Where(a => a.Currency == "RUB" || rates.ContainsKey(a.Currency))
                .Take(MaxAmountsPerMessage)
                .Select(a => BuildConversion(a.Amount, a.Currency, rates))
                .ToList();

            if (blocks.Count == 0) return;

            Message sentMessage = await bot.SendMessage(
                chatId: update.Message.Chat.Id,
                text: string.Join("\n\n", blocks),
                parseMode: ParseMode.Markdown,
                replyParameters: new ReplyParameters { MessageId = update.Message.Id },
                cancellationToken: ct
            );

            _stats?.RecordConversion(update.Message.Chat.Id);
        }

        private static string BuildConversion(double amount, string sourceCurrency, Dictionary<string, double> rates)
        {
            double rubAmount = sourceCurrency == "RUB" ? amount : amount / rates[sourceCurrency];

            var currencies = PopularCurrencies.ToList();
            if (!currencies.Contains(sourceCurrency))
                currencies.Insert(0, sourceCurrency);

            var sb = new StringBuilder();
            sb.AppendLine($"*{amount:N2} {GetCurrencyTargetInfo(sourceCurrency)}:*");
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

            return sb.ToString().TrimEnd();
        }

        public static bool TryParseAmount(string text, out double amount, out string currency)
        {
            var first = ParseAmounts(text).FirstOrDefault();
            amount = first?.Amount ?? 0;
            currency = first?.Currency ?? "";
            return first != null;
        }

        public static List<ParsedAmount> ParseAmounts(string text)
        {
            var found = new List<(int Index, int Length, ParsedAmount Amount)>();

            foreach (Match match in NamedCurrencyRegex.Matches(text))
            {
                for (int group = 1; group < NamedGroupCurrencies.Length; group++)
                {
                    if (match.Groups[group].Success && ParseNumber(match.Groups[group].Value) is { } value)
                    {
                        found.Add((match.Index, match.Length, new ParsedAmount(value, NamedGroupCurrencies[group])));
                        break;
                    }
                }
            }

            foreach (Match match in GenericCurrencyRegex.Matches(text))
            {
                bool overlaps = found.Any(f => match.Index < f.Index + f.Length && f.Index < match.Index + match.Length);
                if (!overlaps && ParseNumber(match.Groups[1].Value) is { } value)
                    found.Add((match.Index, match.Length, new ParsedAmount(value, match.Groups[2].Value.ToUpperInvariant())));
            }

            return found
                .OrderBy(f => f.Index)
                .Select(f => f.Amount)
                .Where(a => a.Amount > 0)
                .Distinct()
                .ToList();
        }

        public static double? ParseNumber(string raw)
        {
            string text = raw.Trim();
            double multiplier = 1;

            var suffix = MultiplierRegex.Match(text);
            if (suffix.Success)
            {
                text = suffix.Groups[1].Value;
                string word = suffix.Groups[2].Value.ToLowerInvariant();
                multiplier = word switch
                {
                    "кк" or "kk" or "млн" => 1e6,
                    _ when word.StartsWith("миллион") => 1e6,
                    "млрд" => 1e9,
                    _ => 1e3
                };
            }

            text = text.Replace(" ", "").Replace(" ", "").Replace(" ", "");

            int commas = text.Count(c => c == ','), dots = text.Count(c => c == '.');
            if (commas > 0 && dots > 0)
            {
                char decimalSeparator = text.LastIndexOf(',') > text.LastIndexOf('.') ? ',' : '.';
                char thousandsSeparator = decimalSeparator == ',' ? '.' : ',';
                text = text.Replace(thousandsSeparator.ToString(), "").Replace(decimalSeparator, '.');
            }
            else if (commas + dots > 1)
            {
                text = text.Replace(",", "").Replace(".", "");
            }
            else if (commas + dots == 1)
            {
                int separator = text.IndexOfAny([',', '.']);
                string integerPart = text[..separator], fraction = text[(separator + 1)..];

                bool thousands = fraction.Length == 3 && integerPart.Length is >= 1 and <= 3 && integerPart != "0";
                text = thousands ? integerPart + fraction : integerPart + "." + fraction;
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value * multiplier
                : null;
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
