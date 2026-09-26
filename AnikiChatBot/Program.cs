using AnikiChatBot.Modules;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var token = config["BotToken"]
    ?? throw new Exception("Cant find BotToken in appsettings.json");

var rawChatIds = config["AllowedChatIds"]
    ?? throw new Exception("Cant find AllowedChatIds in appsettings.json");

HashSet<long> allowedChatIds = rawChatIds
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(long.Parse)
    .ToHashSet();

var exchangeApiKey = config["ExchangeApiKey"]
    ?? throw new Exception("Cant find ExchangeApiKey in appsettings.json");

var botClient = new TelegramBotClient(token);
using var cts = new CancellationTokenSource();
using var httpClient = new HttpClient();

var mediaModule = new MediaModule(
    ytDlpPath: config["YtDlpPath"] ?? @"C:\YTDLP\yt-dlp.exe",
    ffmpegPath: config["FfmpegPath"] ?? @"C:\FFMPEG\bin\ffmpeg.exe",
    cookiesFile: config["CookiesFile"]);

_ = mediaModule.UpdateYtDlpAsync(cts.Token);

DateTime lastRatesUpdate = DateTime.MinValue;
Dictionary<string, double> cachedRates = new();
string cacheFilePath = "rates_cache.json";

if (File.Exists(cacheFilePath))
{
    try
    {
        string cacheJson = File.ReadAllText(cacheFilePath);
        using JsonDocument doc = JsonDocument.Parse(cacheJson);

        if (doc.RootElement.TryGetProperty("lastRatesUpdate", out var dateProp))
            lastRatesUpdate = dateProp.GetDateTime();

        if (doc.RootElement.TryGetProperty("rates", out var ratesProp))
        {
            foreach (var prop in ratesProp.EnumerateObject())
            {
                cachedRates[prop.Name] = prop.Value.GetDouble();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Cache] Не удалось прочитать файл кэша при старте: {ex.Message}");
    }
}

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = Array.Empty<UpdateType>()
};

await botClient.DropPendingUpdates(cts.Token);

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandlePollingErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

var me = await botClient.GetMe(cts.Token);
Console.WriteLine($"Bot @{me.Username} started. Allowed chats count: {allowedChatIds.Count}");

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { });

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    if (update.Message is null || !allowedChatIds.Contains(update.Message.Chat.Id))
        return;

    var currencyModule = new CurrencyModule
    {
        lastRatesUpdate = lastRatesUpdate,
        exchangeApiKey = exchangeApiKey,
        cachedRates = cachedRates,
        httpClient = httpClient
    };

    await currencyModule.HandleCurrencyCommand(bot, update, ct);

    lastRatesUpdate = currencyModule.lastRatesUpdate;
    cachedRates = currencyModule.cachedRates;

    await mediaModule.HandleMediaCommand(bot, update, ct);

    var repeaterModule = new RepeaterModule
    {
        httpClient = httpClient
    };

    await repeaterModule.HandleRepeaterCommand(bot, update, ct);
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
