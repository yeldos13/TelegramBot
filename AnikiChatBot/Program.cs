using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var token = config["BotToken"]
    ?? throw new Exception("Cant find BotToken in appsettings.json");

var botClient = new TelegramBotClient(token);

using var cts = new CancellationTokenSource();

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
Console.WriteLine($"Bot @{me.Username} started. press Ctrl+C for stop.");
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { });

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    if (update.Message is not { } message) return;
    if (message.Text is not { } text) return;

    var chatId = message.Chat.Id;
    Console.WriteLine($"[{chatId}] {message.From?.Username}: {text}");

    await bot.SendMessage(chatId,
                $"{text}",
                parseMode: ParseMode.Markdown, cancellationToken: ct);
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
