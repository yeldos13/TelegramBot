using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var token = config["BotToken"]
    ?? throw new Exception("Cant find BotToken in appsettings.json");

var allowedChatId = long.Parse(config["AllowedChatId"]
    ?? throw new Exception("Cant find AllowedChatId in appsettings.json"));

var exchangeApiKey = config["ExchangeApiKey"]
    ?? throw new Exception("Cant find ExchangeApiKey in appsettings.json");

var botClient = new TelegramBotClient(token);
using var cts = new CancellationTokenSource();
using var httpClient = new HttpClient();

const string FilePath = "replies.txt";
var repliesDatabase = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
LoadRepliesFromFile();

DateTime lastRatesUpdate = DateTime.MinValue;
Dictionary<string, double> cachedRates = new();

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = Array.Empty<UpdateType>()
};

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandlePollingErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

var me = await botClient.GetMe();
Console.WriteLine($"Bot @{me.Username} started. Allowed chat: {allowedChatId}");
Console.WriteLine($"Loaded {repliesDatabase.Count} pairs from {FilePath}");
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { });

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    if (update.Message is not { } message) return;
    if (message.Text is not { } text) return;

    var chatId = message.Chat.Id;

    if (chatId != allowedChatId)
    {
        Console.WriteLine($"Ignored message from unauthorized chat: {chatId}");
        return;
    }

    Console.WriteLine($"[{chatId}] {message.From?.Username}: {text}");

    double rubAmount = 0;
    string sourceCurrency = "";
    double originalAmount = 0;

    var rubMatch = Regex.Match(text, @"(\d+(?:[.,]\d+)?)\s*(?:рубл[яьей]|руб|р)\b", RegexOptions.IgnoreCase);
    var usdMatch = Regex.Match(text, @"(?:\$|доллар[аов]*)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:\$|доллар[аов]*|бакс[аов]*)\b", RegexOptions.IgnoreCase);
    var eurMatch = Regex.Match(text, @"(?:€|евро)\s*(\d+(?:[.,]\d+)?)|(\d+(?:[.,]\d+)?)\s*(?:€|евро)\b", RegexOptions.IgnoreCase);
    var kztMatch = Regex.Match(text, @"(\d+(?:[.,]\d+)?)\s*(?:тенге|тг|kzt)\b", RegexOptions.IgnoreCase);

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

            string responseText = $"💰 *{originalAmount:N2} {currencySign}:*\n\n" +
                                  (sourceCurrency != "RUB" ? $"*RUB:* {resRub:N2} ₽\n" : "") +
                                  (sourceCurrency != "USD" ? $"*USD:* ${resUsd:N2}\n" : "") +
                                  (sourceCurrency != "EUR" ? $"*EUR:* {resEur:N2} €\n" : "") +
                                  (sourceCurrency != "KZT" ? $"*KZT:* {resKzt:N2} ₸" : "");

            await bot.SendMessage(
                chatId: chatId,
                text: responseText.TrimEnd(),
                parseMode: ParseMode.Markdown,
                replyParameters: new ReplyParameters { MessageId = message.Id },
                cancellationToken: ct
            );
            return;
        }
    }

    if (message.ReplyToMessage is { } replyToMessage && !string.IsNullOrWhiteSpace(replyToMessage.Text))
    {
        string triggerText = replyToMessage.Text.Replace("\r", "").Replace("\n", " ").Trim();
        string answerText = text.Replace("\r", "").Replace("\n", " ").Trim();

        if (triggerText != answerText && !string.IsNullOrEmpty(triggerText) && !string.IsNullOrEmpty(answerText))
        {
            repliesDatabase[triggerText] = answerText;
            SaveRepliesToFile();
            return;
        }
    }

    string cleanedText = text.Trim();
    if (repliesDatabase.TryGetValue(cleanedText, out var savedAnswer))
    {
        await bot.SendMessage(
            chatId: chatId,
            text: savedAnswer,
            parseMode: ParseMode.Markdown,
            replyParameters: new ReplyParameters { MessageId = message.Id },
            cancellationToken: ct
        );
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

void LoadRepliesFromFile()
{
    try
    {
        if (!File.Exists(FilePath))
        {
            File.Create(FilePath).Dispose();
            return;
        }

        var lines = File.ReadAllLines(FilePath);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(":::", 2);
            if (parts.Length == 2)
            {
                string trigger = parts[0].Trim();
                string answer = parts[1].Trim();
                repliesDatabase[trigger] = answer;
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error while reading file {FilePath}: {ex.Message}");
    }
}

void SaveRepliesToFile()
{
    try
    {
        var lines = new List<string>();
        foreach (var kvp in repliesDatabase)
        {
            lines.Add($"{kvp.Key}:::{kvp.Value}");
        }

        File.WriteAllLines(FilePath, lines);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error while writing file {FilePath}: {ex.Message}");
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
{
    var errorMessage = ex switch
    {
        ApiRequestException apiEx => $"ERROR Telegram API: [{apiEx.ErrorCode}] {apiEx.Message}",
        _ => ex.ToString()
    };
    Console.Error.WriteLine(errorMessage);
    return Task.CompletedTask;
}
