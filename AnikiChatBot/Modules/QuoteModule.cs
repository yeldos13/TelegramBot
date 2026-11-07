using System.Globalization;
using System.Net;
using AnikiChatBot.Modules.Quote;
using SkiaSharp;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AnikiChatBot.Modules
{
    public record QuoteFlags(bool Png = false, bool WithReply = false, bool WithMedia = false, float Scale = 1, SKColor? Background = null);

    public class QuoteModule
    {
        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "q", Description = "Стикер-цитата: ответь на сообщение" },
            new BotCommand { Command = "qtop", Description = "Лучшие цитаты чата" },
            new BotCommand { Command = "qrand", Description = "Случайная цитата из лучших" },
        ];

        public const string Usage =
            "Ответь командой /q на сообщение. Можно добавить:\n" +
            "p — картинка PNG (без оценок и архива)\n" +
            "r — с сообщением, на которое это был ответ\n" +
            "m — с фото или видео из сообщения\n" +
            "s1.5 — масштаб от 0.5 до 2\n" +
            "красный или #ff8800 — цвет фона";

        private const string CallbackPrefix = "quote:";
        private const int TopSize = 10;
        private const int RememberedReplies = 5000;

        private static readonly Dictionary<string, SKColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["белый"] = SKColors.White, ["white"] = SKColors.White,
            ["чёрный"] = SKColors.Black, ["черный"] = SKColors.Black, ["black"] = SKColors.Black,
            ["серый"] = new(0x60, 0x60, 0x60), ["gray"] = new(0x60, 0x60, 0x60),
            ["красный"] = new(0xB7, 0x2C, 0x2C), ["red"] = new(0xB7, 0x2C, 0x2C),
            ["оранжевый"] = new(0xD9, 0x7A, 0x1E), ["orange"] = new(0xD9, 0x7A, 0x1E),
            ["жёлтый"] = new(0xE8, 0xC5, 0x3A), ["желтый"] = new(0xE8, 0xC5, 0x3A), ["yellow"] = new(0xE8, 0xC5, 0x3A),
            ["зелёный"] = new(0x2E, 0x7D, 0x46), ["зеленый"] = new(0x2E, 0x7D, 0x46), ["green"] = new(0x2E, 0x7D, 0x46),
            ["синий"] = new(0x25, 0x5F, 0xB5), ["blue"] = new(0x25, 0x5F, 0xB5),
            ["фиолетовый"] = new(0x6A, 0x3F, 0xB5), ["purple"] = new(0x6A, 0x3F, 0xB5),
            ["розовый"] = new(0xD8, 0x5C, 0x9A), ["pink"] = new(0xD8, 0x5C, 0x9A),
        };

        private readonly QuoteStore _store;
        private readonly Random _random;
        private readonly Dictionary<(long Chat, int Message), (string Name, string Text)> _replies = [];
        private readonly Queue<(long Chat, int Message)> _replyOrder = new();
        private string _botUsername = "";

        public QuoteModule(QuoteStore? store = null, Random? random = null)
        {
            _store = store ?? new QuoteStore();
            _random = random ?? Random.Shared;
        }

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public void Remember(Message message)
        {
            if (message.ReplyToMessage is not { } target)
                return;

            var key = (message.Chat.Id, message.MessageId);
            if (!_replies.TryAdd(key, (Author(target).Name, BodyText(target) ?? "Медиа")))
                return;

            _replyOrder.Enqueue(key);
            if (_replyOrder.Count > RememberedReplies)
                _replies.Remove(_replyOrder.Dequeue());
        }

        public async Task<bool> HandleCommand(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (BotCommands.Parse(message.Text, _botUsername) is not { } parsed)
                return false;

            switch (parsed.Command)
            {
                case "q":
                    await QuoteAsync(bot, message, parsed.Args, ct);
                    return true;
                case "qtop":
                    await TopAsync(bot, message, ct);
                    return true;
                case "qrand":
                    await RandomAsync(bot, message, ct);
                    return true;
                default:
                    return false;
            }
        }

        public async Task HandleCallback(ITelegramBotClient bot, CallbackQuery query, CancellationToken ct)
        {
            if (query.Data?.StartsWith(CallbackPrefix) != true || query.Message is not { } message)
                return;

            var parts = query.Data[CallbackPrefix.Length..].Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int id))
            {
                await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                return;
            }

            if (_store.Vote(message.Chat.Id, id, query.From.Id, parts[1] == "up") is not { } votes)
            {
                await bot.AnswerCallbackQuery(query.Id, "Цитата не найдена", cancellationToken: ct);
                return;
            }

            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            await bot.EditMessageReplyMarkup(message.Chat.Id, message.MessageId, Keyboard(id, votes.Up, votes.Down), cancellationToken: ct);
        }

        private async Task QuoteAsync(ITelegramBotClient bot, Message command, string args, CancellationToken ct)
        {
            long chatId = command.Chat.Id;

            if (!TryParseFlags(args, out var flags, out string? badFlag))
            {
                await ReplyAsync(bot, command, $"Не понял «{badFlag}».\n\n{Usage}", ct);
                return;
            }

            if (command.ReplyToMessage is not { } source)
            {
                await ReplyAsync(bot, command, Usage, ct);
                return;
            }

            string? text = BodyText(source);
            string? mediaId = flags.WithMedia || text == null ? MediaFileId(source) : null;
            if (text == null && mediaId == null)
            {
                await ReplyAsync(bot, command, "Тут нечего цитировать: в сообщении нет текста или картинки.", ct);
                return;
            }

            (string Name, string Text)? reply = flags.WithReply && _replies.TryGetValue((chatId, source.MessageId), out var known) ? known : null;
            var author = Author(source);

            var card = new QuoteCard(
                author.Name,
                author.UserId ?? author.Name.Sum(c => c),
                text,
                author.UserId is { } userId ? await LoadAvatarAsync(bot, userId, ct) : null,
                mediaId != null ? await DownloadAsync(bot, mediaId, ct) : null,
                reply?.Name,
                reply?.Text,
                flags.Background,
                flags.Scale);

            var replyTo = new ReplyParameters { MessageId = source.MessageId, AllowSendingWithoutReply = true };

            if (flags.Png)
            {
                using var png = new MemoryStream(QuoteRenderer.RenderPng(card));
                await bot.SendDocument(chatId, InputFile.FromStream(png, "quote.png"), replyParameters: replyTo, cancellationToken: ct);
            }
            else
            {
                int id = _store.NextId(chatId);
                using var webp = new MemoryStream(QuoteRenderer.RenderSticker(card));
                var sent = await bot.SendSticker(chatId, InputFile.FromStream(webp, "quote.webp"),
                    replyParameters: replyTo, replyMarkup: Keyboard(id, 0, 0), cancellationToken: ct);

                _store.Add(chatId, new SavedQuote
                {
                    Id = id,
                    FileId = sent.Sticker?.FileId ?? "",
                    MessageId = sent.MessageId,
                    Author = author.Name,
                    Preview = Preview(text ?? "Медиа"),
                    At = DateTime.UtcNow,
                });
            }

            if (flags.WithReply && reply == null)
                await ReplyAsync(bot, command, "Без блока ответа: это не ответ, или сообщение старше последнего запуска бота.", ct);
            else
                Cleanup.DeleteLater(bot, chatId, Cleanup.CommandDelay, command.MessageId);
        }

        private async Task TopAsync(ITelegramBotClient bot, Message command, CancellationToken ct)
        {
            string text = BuildTop(command.Chat.Id, _store.Top(command.Chat.Id, TopSize));
            var sent = await bot.SendMessage(command.Chat.Id, text, parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyParameters: command.MessageId, cancellationToken: ct);
            Cleanup.DeleteCommandLater(bot, command, sent);
        }

        private async Task RandomAsync(ITelegramBotClient bot, Message command, CancellationToken ct)
        {
            if (_store.RandomTop(command.Chat.Id, _random) is not { } quote)
            {
                await ReplyAsync(bot, command, NoTopQuotes, ct);
                return;
            }

            await bot.SendSticker(command.Chat.Id, InputFile.FromFileId(quote.FileId),
                replyMarkup: Keyboard(quote.Id, quote.Up.Count, quote.Down.Count), cancellationToken: ct);
            Cleanup.DeleteLater(bot, command.Chat.Id, Cleanup.CommandDelay, command.MessageId);
        }

        private const string NoTopQuotes = "Пока нет цитат с плюсами. Сделай цитату через /q и поставь 👍.";

        private static async Task ReplyAsync(ITelegramBotClient bot, Message command, string text, CancellationToken ct)
        {
            var sent = await bot.SendMessage(command.Chat.Id, text, replyParameters: command.MessageId, cancellationToken: ct);
            Cleanup.DeleteCommandLater(bot, command, sent);
        }

        internal static string BuildTop(long chatId, IReadOnlyList<SavedQuote> quotes)
        {
            if (quotes.Count == 0)
                return NoTopQuotes;

            var lines = quotes.Select((q, i) =>
            {
                string label = WebUtility.HtmlEncode($"{q.Author}: {q.Preview}");
                string body = MessageLink(chatId, q.MessageId) is { } link ? $"<a href=\"{link}\">{label}</a>" : label;
                return $"{i + 1}. 👍{q.Up.Count} 👎{q.Down.Count} {body}";
            });

            return "🏆 Лучшие цитаты\n\n" + string.Join("\n", lines);
        }

        internal static string? MessageLink(long chatId, int messageId)
        {
            const long supergroupBase = -1_000_000_000_000;
            return chatId < supergroupBase ? $"https://t.me/c/{supergroupBase - chatId}/{messageId}" : null;
        }

        internal static bool TryParseFlags(string args, out QuoteFlags flags, out string? bad)
        {
            flags = new QuoteFlags();
            bad = null;

            foreach (string token in args.Split(' ', '\n').Where(t => t.Length > 0))
            {
                string lower = token.ToLowerInvariant();

                if (lower == "p")
                    flags = flags with { Png = true };
                else if (lower == "r")
                    flags = flags with { WithReply = true };
                else if (lower == "m")
                    flags = flags with { WithMedia = true };
                else if (lower[0] == 's' && float.TryParse(lower[1..].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) && float.IsFinite(scale))
                    flags = flags with { Scale = Math.Clamp(scale, 0.5f, 2f) };
                else if (NamedColors.TryGetValue(lower, out var named))
                    flags = flags with { Background = named };
                else if (lower[0] == '#' && SKColor.TryParse(lower, out var hex))
                    flags = flags with { Background = hex.WithAlpha(255) };
                else
                {
                    bad = token;
                    return false;
                }
            }

            return true;
        }

        internal static (long? UserId, string Name) Author(Message message) => message.ForwardOrigin switch
        {
            MessageOriginUser origin => (origin.SenderUser.Id, Users.DisplayName(origin.SenderUser)),
            MessageOriginHiddenUser origin => (null, origin.SenderUserName),
            MessageOriginChat origin => (null, origin.SenderChat.Title ?? "Чат"),
            MessageOriginChannel origin => (null, origin.Chat.Title ?? "Канал"),
            _ when message.SenderChat is { } chat => (null, chat.Title ?? "Чат"),
            _ when message.From is { } user => (user.Id, Users.DisplayName(user)),
            _ => (null, "Аноним"),
        };

        internal static string? BodyText(Message message)
        {
            string? text = (message.Text ?? message.Caption)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        internal static string? MediaFileId(Message message)
        {
            if (message.Photo is { Length: > 0 } photo)
                return photo[^1].FileId;

            if (message.Sticker is { } sticker)
                return sticker.IsAnimated || sticker.IsVideo ? sticker.Thumbnail?.FileId : sticker.FileId;

            return (message.Video?.Thumbnail ?? message.Animation?.Thumbnail ?? message.VideoNote?.Thumbnail ?? message.Document?.Thumbnail)?.FileId;
        }

        internal static string Preview(string text)
        {
            const int length = 50;
            string line = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var info = new StringInfo(line);
            return info.LengthInTextElements <= length ? line : info.SubstringByTextElements(0, length) + "…";
        }

        private static InlineKeyboardMarkup Keyboard(int id, int up, int down) => new(
            InlineKeyboardButton.WithCallbackData($"👍 {up}", $"{CallbackPrefix}{id}:up"),
            InlineKeyboardButton.WithCallbackData($"👎 {down}", $"{CallbackPrefix}{id}:down"));

        private static async Task<byte[]?> LoadAvatarAsync(ITelegramBotClient bot, long userId, CancellationToken ct)
        {
            try
            {
                var photos = await bot.GetUserProfilePhotos(userId, limit: 1, cancellationToken: ct);
                if (photos.Photos is not { Length: > 0 } sets || sets[0].Length == 0)
                    return null;

                return await DownloadAsync(bot, sets[0][Math.Min(1, sets[0].Length - 1)].FileId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Quote] Не удалось получить аватар: {ex.GetType().Name}");
                return null;
            }
        }

        private static async Task<byte[]?> DownloadAsync(ITelegramBotClient bot, string fileId, CancellationToken ct)
        {
            try
            {
                using var stream = new MemoryStream();
                await bot.GetInfoAndDownloadFile(fileId, stream, ct);
                return stream.ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Quote] Не удалось скачать файл: {ex.GetType().Name}");
                return null;
            }
        }
    }
}
