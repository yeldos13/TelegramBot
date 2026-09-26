using AnikiChatBot.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot
{
    public class BotWorker : BackgroundService
    {
        private const string RunningMarkerFile = "running.flag";
        private static readonly TimeSpan YtDlpUpdateInterval = TimeSpan.FromHours(24);

        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient = new HttpClient();

        private HashSet<long> _allowedChatIds = new();
        private OwnerNotifier _notifier = null!;
        private CurrencyModule _currencyModule = null!;
        private MediaModule _mediaModule = null!;
        private RepeaterModule _repeaterModule = null!;

        public BotWorker(IConfiguration config)
        {
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                try { File.Delete(RunningMarkerFile); } catch { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Fatal] {ex}");

                if (_notifier != null)
                    await _notifier.NotifyNowAsync($"Бот упал и будет перезапущен:\n{ex.Message}", TimeSpan.FromSeconds(10));

                Environment.Exit(1);
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            bool crashedLastTime = File.Exists(RunningMarkerFile);
            File.WriteAllText(RunningMarkerFile, DateTime.Now.ToString("O"));

            var token = _config["BotToken"]
                ?? throw new Exception("Cant find BotToken in appsettings.json");

            var rawChatIds = _config["AllowedChatIds"]
                ?? throw new Exception("Cant find AllowedChatIds in appsettings.json");

            _allowedChatIds = rawChatIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(long.Parse)
                .ToHashSet();

            var exchangeApiKey = _config["ExchangeApiKey"]
                ?? throw new Exception("Cant find ExchangeApiKey in appsettings.json");

            var botClient = new TelegramBotClient(token);

            _notifier = new OwnerNotifier(_config["OwnerUsername"]);
            _notifier.Attach(botClient);

            MediaModule.CleanupTempFiles();

            _currencyModule = new CurrencyModule(_httpClient, exchangeApiKey, _notifier);
            _repeaterModule = new RepeaterModule();
            _mediaModule = new MediaModule(
                ytDlpPath: _config["YtDlpPath"] ?? @"C:\YTDLP\yt-dlp.exe",
                ffmpegPath: _config["FfmpegPath"] ?? @"C:\FFMPEG\bin\ffmpeg.exe",
                cookiesFile: _config["CookiesFile"],
                notifier: _notifier);

            await botClient.DropPendingUpdates(ct);

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
                cancellationToken: ct
            );

            var me = await botClient.GetMe(ct);
            Console.WriteLine($"Bot @{me.Username} started. Allowed chats count: {_allowedChatIds.Count}, replies: {_repeaterModule.Count}");

            if (!_notifier.HasOwner)
                Console.WriteLine($"[Notifier] Владелец ещё не известен — пусть напишет @{me.Username} /start в личку");

            if (crashedLastTime)
                _notifier.Notify("restart", "Бот перезапустился после сбоя. Подробности — в логе за сегодня.");

            await UpdateYtDlpLoopAsync(ct);
        }

        private async Task UpdateYtDlpLoopAsync(CancellationToken ct)
        {
            while (true)
            {
                await _mediaModule.UpdateYtDlpAsync(ct);
                await Task.Delay(YtDlpUpdateInterval, ct);
            }
        }

        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message is not { } message)
                return;

            if (_notifier.TryRegisterOwner(message))
            {
                if (message.Text?.StartsWith("/start") == true)
                {
                    await RunModuleAsync("Notifier", () => bot.SendMessage(message.Chat.Id,
                        "Привет! Сюда буду присылать уведомления об ошибках бота.", cancellationToken: ct));
                }
                return;
            }

            if (!_allowedChatIds.Contains(message.Chat.Id))
                return;

            await RunModuleAsync("Currency", () => _currencyModule.HandleCurrencyCommand(bot, update, ct));
            await RunModuleAsync("Media", () => _mediaModule.HandleMediaCommand(bot, update, ct));
            await RunModuleAsync("Repeater", () => _repeaterModule.HandleRepeaterCommand(bot, update, ct));
        }

        private async Task RunModuleAsync(string name, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[{name}] {ex.Message}");
                _notifier.Notify($"module:{name}", $"Ошибка в модуле {name}: {ex.Message}");
            }
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            var errorMessage = ex switch
            {
                ApiRequestException apiEx => $"ERROR Telegram API: [{apiEx.ErrorCode}] {apiEx.Message}",
                _ => ex.ToString()
            };
            Console.Error.WriteLine(errorMessage);

            if (ex is ApiRequestException api)
                _notifier.Notify($"polling:{api.ErrorCode}", $"Ошибка Telegram API: [{api.ErrorCode}] {api.Message}");

            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _httpClient.Dispose();
            base.Dispose();
        }
    }
}
