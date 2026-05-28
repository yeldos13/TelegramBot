using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class CurrencyModule
    {
        public DateTime lastRatesUpdate;
        public string exchangeApiKey;
        public Dictionary<string, double> cachedRates = new Dictionary<string, double>();
        public HttpClient httpClient;

        private static readonly Regex NamedCurrencyRegex = new Regex(
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:рубл[яьей]+|руб|р|₽)\b)|" +
            @"(?:(?:\$|доллар[аов]*|бакс[аов]*)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:\$|доллар[аов]*|бакс[аов]*)\b)|" +
            @"(?:(?:€|евро)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:€|евро)\b)|" +
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:тенге|тг|kzt|₸)\b)|" +
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:грив[еньеяидлз]*|грн|uah|₴)\b)|" +
            @"(?:(\d+(?:[.,]\d+)?)\s*(?:бел\.?\s*руб(?:л[яьей]+|ь)?|бр|byn)\b)|" +
            @"(?:(?:c\$)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:c\$|cad|канадск[аиоыхьйе]*\s*доллар[аов]*)\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex GenericCurrencyRegex = new Regex(
            @"(?:(\d+(?:[.,]\d+)?)\s*([a-zA-Z]{3})\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public async Task HandleCurrencyCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(update.Message?.Text)) return;

            string amountStr = "";
            string sourceCurrency = "";

            var namedMatch = NamedCurrencyRegex.Match(update.Message.Text);

            if (namedMatch.Success)
            {
                if (!string.IsNullOrEmpty(namedMatch.Groups[1].Value)) { amountStr = namedMatch.Groups[1].Value; sourceCurrency = "RUB"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[2].Value)) { amountStr = namedMatch.Groups[2].Value; sourceCurrency = "USD"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[3].Value)) { amountStr = namedMatch.Groups[3].Value; sourceCurrency = "USD"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[4].Value)) { amountStr = namedMatch.Groups[4].Value; sourceCurrency = "EUR"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[5].Value)) { amountStr = namedMatch.Groups[5].Value; sourceCurrency = "EUR"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[6].Value)) { amountStr = namedMatch.Groups[6].Value; sourceCurrency = "KZT"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[7].Value)) { amountStr = namedMatch.Groups[7].Value; sourceCurrency = "UAH"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[8].Value)) { amountStr = namedMatch.Groups[8].Value; sourceCurrency = "BYN"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[9].Value)) { amountStr = namedMatch.Groups[9].Value; sourceCurrency = "CAD"; }
                else if (!string.IsNullOrEmpty(namedMatch.Groups[10].Value)) { amountStr = namedMatch.Groups[10].Value; sourceCurrency = "CAD"; }
            }
            else
            {
                var genericMatch = GenericCurrencyRegex.Match(update.Message.Text);
                if (genericMatch.Success)
                {
                    amountStr = genericMatch.Groups[1].Value;
                    sourceCurrency = genericMatch.Groups[2].Value.ToUpper();
                }
            }

            if (string.IsNullOrEmpty(sourceCurrency)) return;

            if (!double.TryParse(amountStr.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double originalAmount))
                return;

            var rates = await GetExchangeRatesAsync();
            if (rates == null) return;

            if (sourceCurrency != "RUB" && !rates.ContainsKey(sourceCurrency)) return;

            double rubAmount = sourceCurrency == "RUB" ? originalAmount : (originalAmount / rates[sourceCurrency]);

            var popularCurrencies = new List<string> { "RUB", "USD", "EUR", "KZT", "UAH", "BYN", "CAD" };

            if (!popularCurrencies.Contains(sourceCurrency))
            {
                popularCurrencies.Insert(0, sourceCurrency);
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"*{originalAmount:N2} {GetCurrencyTargetInfo(sourceCurrency)}:*");
            sb.AppendLine();

            foreach (var currency in popularCurrencies)
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

            await bot.SendMessage(
                chatId: update.Message.Chat.Id,
                text: sb.ToString().TrimEnd(),
                parseMode: ParseMode.Markdown,
                replyParameters: new ReplyParameters { MessageId = update.Message.Id },
                cancellationToken: ct
            );
        }

        private string GetCurrencyTargetInfo(string code)
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

        async Task<Dictionary<string, double>?> GetExchangeRatesAsync()
        {
            if ((DateTime.UtcNow - lastRatesUpdate).TotalHours < 6 && cachedRates.Count > 0)
                return cachedRates;

            try
            {
                string url = $"https://v6.exchangerate-api.com/v6/{exchangeApiKey}/latest/RUB";
                string jsonString = await httpClient.GetStringAsync(url);

                using JsonDocument doc = JsonDocument.Parse(jsonString);
                JsonElement root = doc.RootElement;

                if (root.GetProperty("result").GetString() == "success")
                {
                    var conversionRates = root.GetProperty("conversion_rates");

                    cachedRates.Clear();

                    foreach (var property in conversionRates.EnumerateObject())
                    {
                        cachedRates[property.Name] = property.Value.GetDouble();
                    }

                    lastRatesUpdate = DateTime.UtcNow;
                    return cachedRates;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error while getting exchange rates: {ex.Message}");
            }

            if (cachedRates.Count > 0) return cachedRates;

            return null;
        }
    }
}
