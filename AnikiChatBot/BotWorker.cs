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

        private const string RestartRequestedFile = "restart-requested.txt";

        private readonly IConfiguration _config;

        private readonly DateTime _startedAt = DateTime.Now;

        private HashSet<long> _allowedChatIds = new();
        private OwnerNotifier _notifier = null!;
        private readonly StatsService _stats = new StatsService();
        private readonly Modules.Penis.PenisStore _penisStore = new Modules.Penis.PenisStore();
        private readonly MuteStore _mutes = new MuteStore();
        private readonly PenisModule _penisModule;
        private readonly HelpModule _helpModule = new HelpModule();
        private SpamModule _spamModule = null!;
        private NewcomerLinksModule _newcomerModule = null!;
        private readonly FunModule _funModule;
        private CurrencyModule _currencyModule = null!;
        private MediaModule _mediaModule = null!;
        private RepeaterModule _repeaterModule = null!;
        private MembersModule _membersModule = null!;

        public BotWorker(IConfiguration config)
        {
            _config = config;
            _penisModule = new PenisModule(_penisStore, stats: _stats);
            _funModule = new FunModule(_stats, mutes: _mutes);
        }

        private void FlushAll()
        {
            _stats.Flush();
            _penisStore.Flush();
            _repeaterModule?.Flush();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                FlushAll();
                try { File.Delete(RunningMarkerFile); } catch { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Fatal] {ex}");
                FlushAll();

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
            _spamModule = new SpamModule(_config["OwnerUsername"], _mutes, _stats);
            _newcomerModule = new NewcomerLinksModule(_notifier);

            MediaModule.CleanupTempFiles();

            _currencyModule = new CurrencyModule(Modules.Media.MediaHttp.Client, exchangeApiKey, _notifier, _stats);
            _repeaterModule = new RepeaterModule(_stats);
            _mediaModule = new MediaModule(
                ytDlpPath: _config["YtDlpPath"] ?? @"C:\YTDLP\yt-dlp.exe",
                ffmpegPath: _config["FfmpegPath"] ?? @"C:\FFMPEG\bin\ffmpeg.exe",
                cookiesFile: _config["CookiesFile"],
                notifier: _notifier,
                stats: _stats);

            var me = await WaitForTelegramAsync(() => botClient.GetMe(ct), ct);

            await botClient.DropPendingUpdates(ct);

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.ChatMember, UpdateType.CallbackQuery] },
                cancellationToken: ct
            );

            _membersModule.SetBotId(me.Id);
            _penisModule.SetBotUsername(me.Username);
            _helpModule.SetBotUsername(me.Username);
            _spamModule.SetBotUsername(me.Username);
            _funModule.SetBotUsername(me.Username);
            _currencyModule.SetBotUsername(me.Username);

            await RunModuleAsync("Commands", () => botClient.SetMyCommands(
                [HelpModule.Command, .. PenisModule.Commands, .. FunModule.Commands, .. CurrencyModule.Commands],
                scope: new Telegram.Bot.Types.BotCommandScopeAllGroupChats(), cancellationToken: ct));
            Console.WriteLine($"Bot @{me.Username} started. Allowed chats count: {_allowedChatIds.Count}, replies: {_repeaterModule.Count}");

            if (!_notifier.HasOwner)
                Console.WriteLine($"[Notifier] Владелец ещё не известен — пусть напишет @{me.Username} /start в личку");

            if (crashedLastTime)
                _notifier.Notify("restart", "Бот перезапустился после сбоя. Подробности — в логе за сегодня.");

            if (File.Exists(RestartRequestedFile))
            {
                File.Delete(RestartRequestedFile);
                _notifier.Notify("restart-done", "✅ Перезапущен по команде.");
            }

            await Task.WhenAll(UpdateYtDlpLoopAsync(ct), WeeklyReportLoopAsync(botClient, ct), CookieCheckLoopAsync(ct));
        }

        public static async Task<T> WaitForTelegramAsync<T>(Func<Task<T>> request, CancellationToken ct,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            delay ??= Task.Delay;
            var wait = TimeSpan.FromSeconds(5);
            var started = DateTime.Now;

            while (true)
            {
                try
                {
                    var result = await request();
                    if (DateTime.Now - started > TimeSpan.FromSeconds(1))
                        Console.WriteLine($"[Startup] Связь с Telegram появилась через {(DateTime.Now - started).TotalSeconds:0} с");
                    return result;
                }
                catch (RequestException ex) when (ex is not ApiRequestException)
                {
                    Console.Error.WriteLine($"[Startup] Нет связи с Telegram ({ex.InnerException?.Message ?? ex.Message}), повтор через {wait.TotalSeconds:0} с");
                    await delay(wait, ct);
                    wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 60));
                }
            }
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

        private async Task CookieCheckLoopAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            while (true)
            {
                await RunModuleAsync("Cookies", () => _mediaModule.CheckInstagramCookiesAsync(ct));
                await Task.Delay(TimeSpan.FromHours(24), ct);
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
                {
                    _newcomerModule.HandleJoins(update);
                    await RunModuleAsync("Members", () => _membersModule.HandleMembersUpdate(bot, update, ct));
                }
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

            if (message.NewChatMembers is { Length: > 0 })
            {
                _newcomerModule.HandleJoins(update);
                return;
            }

            if (await HandledByAsync("Spam", () => _spamModule.HandleMessage(bot, message, ct))
                || await HandledByAsync("Newcomer", () => _newcomerModule.HandleMessage(bot, message, ct)))
                return;

            if (message.From is { IsBot: false } author)
                _stats.RecordMessage(message.Chat.Id, author.Id, Users.DisplayName(author));

            if (await HandledByAsync("Moderation", () => _spamModule.HandleModerationCommand(bot, message, ct))
                || await HandledByAsync("Help", () => _helpModule.HandleCommand(bot, message, ct))
                || await HandledByAsync("Fun", () => _funModule.HandleCommand(bot, message, ct))
                || await HandledByAsync("Penis", () => _penisModule.HandleCommand(bot, message, ct)))
                return;

            await RunModuleAsync("Currency", () => _currencyModule.HandleCurrencyCommand(bot, update, ct));
            await RunModuleAsync("Media", () => _mediaModule.HandleMediaCommand(bot, update, ct));
            await RunModuleAsync("Repeater", () => _repeaterModule.HandleRepeaterCommand(bot, update, ct));
        }

        private async Task HandleOwnerCommandAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            string command = message.Text?.Split(' ', '@')[0].ToLowerInvariant() ?? "";
            string args = message.Text?.Split(' ', 2).ElementAtOrDefault(1)?.Trim() ?? "";

            const string commands =
                "/status — состояние бота\n" +
                "/log — последние 30 строк лога (/log 80 — больше)\n" +
                "/restart — перезапустить бота\n" +
                "В чате: /mutelist — кого замьютил бот, /unmute — снять мут";

            if (command == "/restart")
            {
                await RestartAsync(bot, message.Chat.Id, ct);
                return;
            }

            string reply = command switch
            {
                "/start" => "Привет! Сюда буду присылать уведомления об ошибках бота.\n\n" + commands,
                "/status" => await BuildStatusAsync(ct),
                "/log" => FileLog.ReadTail(Path.Combine(AppContext.BaseDirectory, "logs"), ParseLogLines(args)),
                _ => "Команды:\n" + commands
            };

            await bot.SendMessage(message.Chat.Id, reply, cancellationToken: ct);
        }

        private async Task RestartAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
        {
            if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            {
                await bot.SendMessage(chatId, "Бот запущен не как служба — перезапусти его вручную.", cancellationToken: ct);
                return;
            }

            await bot.SendMessage(chatId, "♻️ Перезапускаюсь…", cancellationToken: ct);
            File.WriteAllText(RestartRequestedFile, DateTime.Now.ToString("O"));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"Start-Sleep 2; Restart-Service AnikiChatBot -Force\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        public static int ParseLogLines(string args) =>
            int.TryParse(args, out int n) && n > 0 ? Math.Min(n, 200) : 30;

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
            lines.Add(_mediaModule.LastCookieCheck is { } check
                ? $"Cookies Instagram: {check.Status switch
                {
                    Modules.Media.YtDlp.CookieStatus.Valid => "✅ работают",
                    Modules.Media.YtDlp.CookieStatus.Invalid => "❌ сессия истекла",
                    Modules.Media.YtDlp.CookieStatus.NotConfigured => "не настроены",
                    _ => "не удалось проверить"
                }} (проверено {check.At:dd.MM HH:mm})"
                : "Cookies Instagram: ещё не проверялись");

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

        private async Task<bool> HandledByAsync(string name, Func<Task<bool>> action)
        {
            bool handled = false;
            await RunModuleAsync(name, async () => handled = await action());
            return handled;
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
    }
}
