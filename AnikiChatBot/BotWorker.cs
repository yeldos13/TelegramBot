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

        private readonly DateTime _startedAt = DateTime.Now;

        private HashSet<long> _allowedChatIds = new();
        private OwnerNotifier _notifier = null!;
        private readonly StatsService _stats = new StatsService();
        private readonly PenisModule _penisModule;
        private readonly HelpModule _helpModule = new HelpModule();
        private SpamModule _spamModule = null!;
        private CurrencyModule _currencyModule = null!;
        private MediaModule _mediaModule = null!;
        private RepeaterModule _repeaterModule = null!;
        private MembersModule _membersModule = null!;

        public BotWorker(IConfiguration config)
        {
            _config = config;
            _penisModule = new PenisModule(new Modules.Penis.PenisStore(), stats: _stats);
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

            _membersModule = new MembersModule(_config["OwnerUsername"], _stats);
            _spamModule = new SpamModule(_config["OwnerUsername"]);

            MediaModule.CleanupTempFiles();

            _currencyModule = new CurrencyModule(_httpClient, exchangeApiKey, _notifier, _stats);
            _repeaterModule = new RepeaterModule(_stats);
            _mediaModule = new MediaModule(
                ytDlpPath: _config["YtDlpPath"] ?? @"C:\YTDLP\yt-dlp.exe",
                ffmpegPath: _config["FfmpegPath"] ?? @"C:\FFMPEG\bin\ffmpeg.exe",
                cookiesFile: _config["CookiesFile"],
                notifier: _notifier,
                stats: _stats);

            await botClient.DropPendingUpdates(ct);

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.ChatMember, UpdateType.CallbackQuery] },
                cancellationToken: ct
            );

            var me = await botClient.GetMe(ct);
            _membersModule.SetBotId(me.Id);
            _penisModule.SetBotUsername(me.Username);
            _helpModule.SetBotUsername(me.Username);

            await RunModuleAsync("Commands", () => botClient.SetMyCommands([HelpModule.Command, .. PenisModule.Commands],
                scope: new Telegram.Bot.Types.BotCommandScopeAllGroupChats(), cancellationToken: ct));
            Console.WriteLine($"Bot @{me.Username} started. Allowed chats count: {_allowedChatIds.Count}, replies: {_repeaterModule.Count}");

            if (!_notifier.HasOwner)
                Console.WriteLine($"[Notifier] Владелец ещё не известен — пусть напишет @{me.Username} /start в личку");

            if (crashedLastTime)
                _notifier.Notify("restart", "Бот перезапустился после сбоя. Подробности — в логе за сегодня.");

            await Task.WhenAll(UpdateYtDlpLoopAsync(ct), WeeklyReportLoopAsync(botClient, ct));
        }

        private async Task WeeklyReportLoopAsync(ITelegramBotClient bot, CancellationToken ct)
        {
            while (true)
            {
                var now = DateTime.Now;
                if (_stats.IsReportDue(now))
                {
                    var periodStart = _stats.PeriodStart;

                    foreach (long chatId in _allowedChatIds)
                    {
                        string report = StatsService.BuildReport(_stats.GetChat(chatId), periodStart, now,
                            _config["OwnerUsername"], _penisModule.GetLeader(chatId));
                        await RunModuleAsync("WeeklyReport", () => bot.SendMessage(chatId, report, cancellationToken: ct));
                    }

                    _stats.StartNewPeriod(now);
                    Console.WriteLine("[Stats] Недельный отчёт отправлен");
                }

                await Task.Delay(TimeSpan.FromMinutes(10), ct);
            }
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
            if (update.ChatMember is { } change)
            {
                if (_allowedChatIds.Contains(change.Chat.Id))
                    await RunModuleAsync("Members", () => _membersModule.HandleMembersUpdate(bot, update, ct));
                return;
            }

            if (update.CallbackQuery is { Message: { } callbackMessage } query)
            {
                if (_allowedChatIds.Contains(callbackMessage.Chat.Id))
                    await RunModuleAsync("Penis", () => _penisModule.HandleCallback(bot, query, ct));
                return;
            }

            if (update.Message is not { } message)
                return;

            if (_notifier.TryRegisterOwner(message))
            {
                await RunModuleAsync("Owner", () => HandleOwnerCommandAsync(bot, message, ct));
                return;
            }

            if (!_allowedChatIds.Contains(message.Chat.Id))
                return;

            if (message.LeftChatMember != null)
            {
                await RunModuleAsync("Members", () => _membersModule.HandleMembersUpdate(bot, update, ct));
                return;
            }

            bool isSpam = false;
            await RunModuleAsync("Spam", async () => isSpam = await _spamModule.HandleMessage(bot, message, ct));
            if (isSpam)
                return;

            bool isCommand = false;
            await RunModuleAsync("Help", async () => isCommand = await _helpModule.HandleCommand(bot, message, ct));
            if (isCommand)
                return;

            await RunModuleAsync("Penis", async () => isCommand = await _penisModule.HandleCommand(bot, message, ct));
            if (isCommand)
                return;

            await RunModuleAsync("Currency", () => _currencyModule.HandleCurrencyCommand(bot, update, ct));
            await RunModuleAsync("Media", () => _mediaModule.HandleMediaCommand(bot, update, ct));
            await RunModuleAsync("Repeater", () => _repeaterModule.HandleRepeaterCommand(bot, update, ct));
        }

        private async Task HandleOwnerCommandAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            string command = message.Text?.Split(' ', '@')[0].ToLowerInvariant() ?? "";

            string reply = command switch
            {
                "/start" => "Привет! Сюда буду присылать уведомления об ошибках бота.\n/status — состояние бота",
                "/status" => await BuildStatusAsync(ct),
                _ => "Команды:\n/status — состояние бота"
            };

            await bot.SendMessage(message.Chat.Id, reply, cancellationToken: ct);
        }

        private async Task<string> BuildStatusAsync(CancellationToken ct)
        {
            var lines = new List<string>
            {
                $"✅ Работает {FormatUptime(DateTime.Now - _startedAt)} (с {_startedAt:dd.MM HH:mm})",
                $"Режим: {(Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? "служба Windows" : "консоль")}",
                $"Чатов: {_allowedChatIds.Count}, автоответов: {_repeaterModule.Count}",
                $"Медиа: отправлено {_mediaModule.SentCount}, ошибок {_mediaModule.FailedCount}",
            };

            if (_mediaModule.LastError is { } lastError)
                lines.Add($"Последняя ошибка: {lastError}");

            lines.Add(_currencyModule.LastRatesUpdateUtc == DateTime.MinValue
                ? "Курсы: ещё не загружались"
                : $"Курсы обновлены: {_currencyModule.LastRatesUpdateUtc.ToLocalTime():dd.MM HH:mm}");

            lines.Add($"Недельный отчёт: {StatsService.NextReportTime(_stats.PeriodStart):dd.MM HH:mm}");
            lines.Add($"yt-dlp: {await _mediaModule.GetYtDlpVersionAsync(ct)}");

            return string.Join("\n", lines);
        }

        public static string FormatUptime(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1)
                return $"{(int)uptime.TotalDays} д {uptime.Hours} ч";
            if (uptime.TotalHours >= 1)
                return $"{uptime.Hours} ч {uptime.Minutes} мин";
            return $"{Math.Max(1, uptime.Minutes)} мин";
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
