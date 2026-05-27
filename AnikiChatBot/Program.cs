using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;
using AnikiChatBot.Modules;

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
    if (update.Message.Chat.Id != allowedChatId)
        return;

    Console.WriteLine($"[{update.Message.Date}] {update.Message.From?.Username}: {update.Message.Text}");

    var currencyModule = new CurrencyModule
    {
        lastRatesUpdate = lastRatesUpdate,
        exchangeApiKey = exchangeApiKey,
        cachedRates = cachedRates,
        httpClient = httpClient
    };

    await currencyModule.HandleCurrencyCommand(bot, update, ct);

    if (update.Message.ReplyToMessage is { } replyToMessage && !string.IsNullOrWhiteSpace(replyToMessage.Text))
    {
        string triggerText = replyToMessage.Text.Replace("\r", "").Replace("\n", " ").Trim();
        string answerText = update.Message.Text.Replace("\r", "").Replace("\n", " ").Trim();

        if (triggerText != answerText && !string.IsNullOrEmpty(triggerText) && !string.IsNullOrEmpty(answerText))
        {
            repliesDatabase[triggerText] = answerText;
            SaveRepliesToFile();
            return;
        }
    }

    string cleanedText = update.Message.Text.Trim();
    if (repliesDatabase.TryGetValue(cleanedText, out var savedAnswer))
    {
        await bot.SendMessage(
            chatId: update.Message.Chat.Id,
            text: savedAnswer,
            parseMode: ParseMode.Markdown,
            replyParameters: new ReplyParameters { MessageId = update.Message.Id },
            cancellationToken: ct
        );
    }
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
