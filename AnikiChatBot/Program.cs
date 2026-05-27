using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Configuration;
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

    var instagramModule = new InstagramModule
    {
        httpClient = httpClient
    };

    await instagramModule.HandleInstagramCommand(bot, update, ct);

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
