using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);

        private const string Multiplier = @"кк|kk|к|k|тыс\.?|тысяч[аи]?|млн|миллион(?:а|ов)?|млрд";

        private const string Num = @"((?<!\d)(?:\d{1,3}(?:[   ]\d{3})+|\d+)(?:[.,]\d+)*(?:\s?(?:" + Multiplier + @"))?)";

        private static readonly Regex NamedCurrencyRegex = new Regex(
            @"(?:" + Num + @"\s*(?:рубл[яьей]+|руб|р|₽)(?!\w))|" +
            @"(?:(?:\$|доллар[аов]*|бакс[аов]*)\s*" + Num + @"|" + Num + @"\s*(?:\$|доллар[аов]*|бакс[аов]*)(?!\w))|" +
            @"(?:(?:€|евро)\s*" + Num + @"|" + Num + @"\s*(?:€|евро)(?!\w))|" +
            @"(?:" + Num + @"\s*(?:тенге|тг|kzt|₸)(?!\w))|" +
            @"(?:" + Num + @"\s*(?:грив[еньеяидлз]*|грн|uah|₴)(?!\w))|" +
            @"(?:" + Num + @"\s*(?:бел\.?\s*руб(?:л[яьей]+|ь)?|бр|byn)(?!\w))|" +
            @"(?:(?:c\$)\s*" + Num + @"|" + Num + @"\s*(?:c\$|cad|канадск[аиоыхьйе]*\s*доллар[аов]*)(?!\w))|" +
            @"(?:₿\s*" + Num + @"|" + Num + @"\s*(?:биткоин[аов]*|₿)(?!\w))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] NamedGroupCurrencies =
            ["", "RUB", "USD", "USD", "EUR", "EUR", "KZT", "UAH", "BYN", "CAD", "CAD", "BTC", "BTC"];

        private static readonly Regex GenericCurrencyRegex = new Regex(
            Num + @"\s*([a-zA-Z]{3,4})\b",
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
        private readonly CryptoRates _crypto;
        private string _botUsername = "";

        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "rates", Description = "Курсы валют и криптовалют на сегодня" },
        ];

        public CurrencyModule(HttpClient httpClient, string exchangeApiKey, OwnerNotifier? notifier = null, StatsService? stats = null)
        {
            _httpClient = httpClient;
            _exchangeApiKey = exchangeApiKey;
            _notifier = notifier;
            _stats = stats;
            _crypto = new CryptoRates(httpClient);
            LoadCache();
            _morning = JsonFile.Load<RatesSnapshot>(MorningFilePath, "Currency");
        }

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task HandleCurrencyCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Text is not { } text || string.IsNullOrEmpty(text)) return;

            if (BotCommands.Parse(text, _botUsername)?.Command == "rates")
            {
                await SendRatesAsync(bot, update.Message, ct);
                return;
            }

            var amounts = ParseAmounts(text);
            if (amounts.Count == 0) return;

            var rates = await GetExchangeRatesAsync();
            if (rates == null) return;

            var crypto = amounts.Any(a => CryptoRates.IsCrypto(a.Currency)) ? await _crypto.GetRubPricesAsync() : null;

            var blocks = amounts
                .Select(a => (a.Amount, a.Currency, Rub: ToRub(a.Amount, a.Currency, rates, crypto)))
                .Where(a => a.Rub != null)
                .Take(MaxAmountsPerMessage)
                .Select(a => BuildConversion(a.Amount, a.Currency, a.Rub!.Value, rates))
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
            Cleanup.DeleteLater(bot, update.Message.Chat.Id, Cleanup.CommandDelay, update.Message.Id, sentMessage.MessageId);
        }

        public static string FormatAmount(double value) =>
            Math.Abs(value) >= 1 || value == 0 ? value.ToString("N2") : value.ToString("0.########");

        public static double? ToRub(double amount, string currency, Dictionary<string, double> rates, Dictionary<string, double>? crypto)
        {
            if (currency == "RUB")
                return amount;
            if (crypto != null && crypto.TryGetValue(currency, out double coinRub))
                return amount * coinRub;
            if (!CryptoRates.IsCrypto(currency) && rates.TryGetValue(currency, out double rate) && rate > 0)
                return amount / rate;
            return null;
        }

        private async Task SendRatesAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            var rates = await GetExchangeRatesAsync();
            var crypto = await _crypto.GetRubPricesAsync();

            string text = rates == null
                ? "Не удалось получить курсы, попробуй позже."
                : BuildRatesTable(rates, crypto, _lastRatesUpdate.ToLocalTime());

            var sent = await bot.SendMessage(message.Chat.Id, text, replyParameters: message.MessageId, cancellationToken: ct);
            Cleanup.DeleteCommandLater(bot, message, sent);
        }

        public class RatesSnapshot
        {
            public DateOnly Date { get; set; }
            public Dictionary<string, double> Rates { get; set; } = new();
            public Dictionary<string, double>? Crypto { get; set; }
        }

        private const string MorningFilePath = "morning_rates.json";
        private RatesSnapshot? _morning;

        public bool IsMorningPostDue(DateOnly today) => _morning?.Date != today;

        public async Task<string?> BuildMorningRatesAsync(DateOnly today)
        {
            var rates = await GetExchangeRatesAsync();
            if (rates == null)
                return null;

            var crypto = await _crypto.GetRubPricesAsync();
            string text = "☀️ Доброе утро!\n" + BuildRatesTable(rates, crypto, _lastRatesUpdate.ToLocalTime(), _morning);

            _morning = new RatesSnapshot { Date = today, Rates = new(rates), Crypto = crypto == null ? null : new(crypto) };
            JsonFile.Save(MorningFilePath, _morning, "Currency");
            return text;
        }

        public static string FormatChange(double delta) =>
            Math.Abs(delta) < 0.005 ? "" : $" ({(delta > 0 ? "+" : "−")}{Math.Abs(delta):N2})";

        public static string FormatPercentChange(double now, double before)
        {
            if (before <= 0)
                return "";
            double percent = (now - before) / before * 100;
            return Math.Abs(percent) < 0.05 ? "" : $" ({(percent > 0 ? "+" : "−")}{Math.Abs(percent):N1}%)";
        }

        public static string BuildRatesTable(Dictionary<string, double> rates, Dictionary<string, double>? crypto, DateTime updated,
            RatesSnapshot? previous = null)
        {
            double Rub(string code) => 1 / rates[code];
            double Kzt(string code) => rates["KZT"] / rates[code];

            var sb = new StringBuilder($"💱 Курсы на {updated:dd.MM HH:mm}\n");
            if (previous != null)
                sb.AppendLine($"В скобках — изменение с {previous.Date:dd.MM}");
            sb.AppendLine();

            foreach (var code in new[] { "USD", "EUR", "CAD", "BYN", "UAH" })
            {
                if (!rates.ContainsKey(code))
                    continue;

                string flag = GetCurrencyTargetInfo(code).Split(' ')[0];
                string line = $"{flag} 1 {code} = {Rub(code):N2} ₽";
                if (previous?.Rates.GetValueOrDefault(code) is > 0 and var before)
                    line += FormatChange(Rub(code) - 1 / before);
                if (rates.ContainsKey("KZT"))
                    line += $" · {Kzt(code):N2} ₸";
                sb.AppendLine(line);
            }

            if (rates.TryGetValue("KZT", out double kztPerRub))
            {
                sb.AppendLine($"🇷🇺 1 RUB = {kztPerRub:N2} ₸");
                sb.AppendLine($"🇰🇿 1000 KZT = {1000 / kztPerRub:N2} ₽");
            }

            if (crypto is { Count: > 0 } && rates.TryGetValue("USD", out double usdPerRub))
            {
                sb.AppendLine();
                foreach (var code in new[] { "BTC", "ETH", "TON", "SOL", "USDT" })
                {
                    if (!crypto.TryGetValue(code, out double rub))
                        continue;

                    string line = $"🪙 1 {code} = {rub * usdPerRub:N2} $ · {rub:N2} ₽";
                    if (code != "USDT" && previous?.Crypto?.GetValueOrDefault(code) is > 0 and var beforeRub
                        && previous.Rates.GetValueOrDefault("USD") is > 0 and var beforeUsd)
                        line += FormatPercentChange(rub * usdPerRub, beforeRub * beforeUsd);
                    sb.AppendLine(line);
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildConversion(double amount, string sourceCurrency, double rubAmount, Dictionary<string, double> rates)
        {
            var currencies = PopularCurrencies.ToList();
            if (!currencies.Contains(sourceCurrency))
                currencies.Insert(0, sourceCurrency);

            var sb = new StringBuilder();
            sb.AppendLine($"*{FormatAmount(amount)} {GetCurrencyTargetInfo(sourceCurrency)}:*");
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

                sb.AppendLine($"{GetCurrencyTargetInfo(currency)} *{currency}:* {FormatAmount(targetAmount)}");
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
                _ when CryptoRates.IsCrypto(code) => $"🪙 {code}",
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
                    using var timeout = new CancellationTokenSource(ApiTimeout);
                    string url = $"https://v6.exchangerate-api.com/v6/{_exchangeApiKey}/latest/RUB";
                    string jsonString = await _httpClient.GetStringAsync(url, timeout.Token);

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

        private class RatesCache
        {
            [JsonPropertyName("lastRatesUpdate")] public DateTime LastRatesUpdate { get; set; }
            [JsonPropertyName("rates")] public Dictionary<string, double> Rates { get; set; } = new();
        }

        private void LoadCache()
        {
            if (JsonFile.Load<RatesCache>(CacheFilePath, "Currency") is { } cache)
            {
                _lastRatesUpdate = cache.LastRatesUpdate;
                _cachedRates = cache.Rates;
            }
        }

        private void SaveCache() =>
            JsonFile.Save(CacheFilePath, new RatesCache { LastRatesUpdate = _lastRatesUpdate, Rates = _cachedRates }, "Currency");
    }
}
