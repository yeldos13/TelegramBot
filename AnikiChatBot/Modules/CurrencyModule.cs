using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static System.Net.Mime.MediaTypeNames;

namespace AnikiChatBot.Modules
{
    public class CurrencyModule
    {
        public DateTime lastRatesUpdate;
        public string exchangeApiKey;
        public Dictionary<string, double> cachedRates;
        public HttpClient httpClient;

        public async Task HandleCurrencyCommand(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            double rubAmount = 0;
            string sourceCurrency = "";
            double originalAmount = 0;

            var rubMatch = Regex.Match(update.Message.Text, @"(\d+(?:[.,]\d+)?)\s*(?:рубл[яьей]|руб|р)\b", RegexOptions.IgnoreCase);
            var usdMatch = Regex.Match(update.Message.Text, @"(?:\$|доллар[аов]*)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:\$|доллар[аов]*|бакс[аов]*)\b", RegexOptions.IgnoreCase);
            var eurMatch = Regex.Match(update.Message.Text, @"(?:€|евро)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:€|евро)\b", RegexOptions.IgnoreCase);
            var kztMatch = Regex.Match(update.Message.Text, @"(\d+(?:[.,]\d+)?)\s*(?:тенге|тг|kzt)\b", RegexOptions.IgnoreCase);

            var rates = await GetExchangeRatesAsync();

            if (rates != null && rates.ContainsKey("USD") && rates.ContainsKey("EUR") && rates.ContainsKey("KZT"))
            {
                if (rubMatch.Success && double.TryParse(rubMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rub))
                {
                    originalAmount = rub;
                    rubAmount = rub;
                    sourceCurrency = "RUB";
                }
                else if (usdMatch.Success)
                {
                    string val = !string.IsNullOrEmpty(usdMatch.Groups[1].Value) ? usdMatch.Groups[1].Value : usdMatch.Groups[2].Value;
                    if (double.TryParse(val.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double usd))
                    {
                        originalAmount = usd;
                        rubAmount = usd / rates["USD"];
                        sourceCurrency = "USD";
                    }
                }
                else if (eurMatch.Success)
                {
                    string val = !string.IsNullOrEmpty(eurMatch.Groups[1].Value) ? eurMatch.Groups[1].Value : eurMatch.Groups[2].Value;
                    if (double.TryParse(val.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double eur))
                    {
                        originalAmount = eur;
                        rubAmount = eur / rates["EUR"];
                        sourceCurrency = "EUR";
                    }
                }
                else if (kztMatch.Success)
                {
                    if (double.TryParse(kztMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double kzt))
                    {
                        originalAmount = kzt;
                        rubAmount = kzt / rates["KZT"];
                        sourceCurrency = "KZT";
                    }
                }

                if (!string.IsNullOrEmpty(sourceCurrency))
                {
                    double resRub = rubAmount;
                    double resUsd = rubAmount * rates["USD"];
                    double resEur = rubAmount * rates["EUR"];
                    double resKzt = rubAmount * rates["KZT"];

                    string currencySign = sourceCurrency switch { "USD" => "$", "EUR" => "€", "KZT" => "₸", _ => "RUB" };

                    string responseText = $"*{originalAmount:N2} {currencySign}:*\n\n" +
                                          (sourceCurrency != "RUB" ? $"*RUB:* {resRub:N2} ₽\n" : "") +
                                          (sourceCurrency != "USD" ? $"*USD:* {resUsd:N2} $\n" : "") +
                                          (sourceCurrency != "EUR" ? $"*EUR:* {resEur:N2} €\n" : "") +
                                          (sourceCurrency != "KZT" ? $"*KZT:* {resKzt:N2} ₸" : "");

                    await bot.SendMessage(
                        chatId: update.Message.Chat.Id,
                        text: responseText.TrimEnd(),
                        parseMode: ParseMode.Markdown,
                        replyParameters: new ReplyParameters { MessageId = update.Message.Id },
                        cancellationToken: ct
                    );
                    return;
                }
            }
        }

        async Task<Dictionary<string, double>?> GetExchangeRatesAsync()
        {
            if ((DateTime.UtcNow - lastRatesUpdate).TotalHours < 1 && cachedRates.Count > 0)
            {
                return cachedRates;
            }

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
                    cachedRates["USD"] = conversionRates.GetProperty("USD").GetDouble();
                    cachedRates["EUR"] = conversionRates.GetProperty("EUR").GetDouble();
                    cachedRates["KZT"] = conversionRates.GetProperty("KZT").GetDouble();

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
