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
    public record QuoteFlags(bool Png = false, bool WithReply = false, bool WithMedia = false, float Scale = 1, SKColor? Background = null, int Count = 1, bool File = false, bool Crop = false);

    public class QuoteModule
    {
        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "q", Description = "Стикер-цитата: ответь на сообщение" },
            new BotCommand { Command = "qtop", Description = "Лучшие цитаты чата" },
            new BotCommand { Command = "qrand", Description = "Случайная цитата из лучших" },
            new BotCommand { Command = "qcolor", Description = "Цвет фона цитат в чате (для админов)" },
            new BotCommand { Command = "qrate", Description = "Включить или выключить оценки (для админов)" },
        ];

        public const string Usage =
            "Ответь командой /q на сообщение. Можно добавить:\n" +
            "3 — это сообщение и следующие за ним, всего до 10\n" +
            "p — обычная картинка, i — файл PNG с прозрачностью (без оценок и архива)\n" +
            "c — с фото, обрезанным по центру\n" +
            "r — с сообщением, на которое это был ответ\n" +
            "m — с фото или видео из сообщения\n" +
            "s1.5 — масштаб от 0.5 до 2\n" +
            "красный или #ff8800 — цвет фона";

        private const string CallbackPrefix = "quote:";
        private const int TopSize = 10;
        private const int RememberedMessages = 1000;
        private const int MaxQuotedMessages = 10;

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
        private readonly QuoteSettingsStore _settings;
        private readonly Random _random;
        private readonly Dictionary<long, List<Message>> _recent = [];
        private string _botUsername = "";

        public QuoteModule(QuoteStore? store = null, Random? random = null, QuoteSettingsStore? settings = null)
        {
            _store = store ?? new QuoteStore();
            _settings = settings ?? new QuoteSettingsStore();
            _random = random ?? Random.Shared;
        }

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public void Remember(Message message)
        {
            if (message.Text?.StartsWith('/') == true || (BodyText(message) == null && MediaFileId(message) == null))
                return;

            if (!_recent.TryGetValue(message.Chat.Id, out var list))
                _recent[message.Chat.Id] = list = [];

            list.Add(message);
            if (list.Count > RememberedMessages)
                list.RemoveAt(0);
        }

        internal IReadOnlyList<Message> Range(Message source, int count)
        {
            if (!_recent.TryGetValue(source.Chat.Id, out var list))
                return [source];

            int start = list.FindIndex(m => m.MessageId == source.MessageId);
            return start < 0 ? [source] : list.GetRange(start, Math.Min(count, list.Count - start));
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
                case "qcolor":
                    await SetColorAsync(bot, message, parsed.Args, ct);
                    return true;
                case "qrate":
                    await ToggleRatingAsync(bot, message, ct);
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

            var messages = Range(source, flags.Count);
            var settings = _settings.Get(chatId);
            SKColor? background = flags.Background ?? (SKColor.TryParse(settings.Background, out var saved) ? saved : null);

            var avatars = new Dictionary<long, byte[]?>();
            var customEmoji = new Dictionary<string, byte[]>();
            var cards = new List<QuoteCard>();
            string? preview = null;

            foreach (var item in messages)
            {
                string? body = BodyText(item);
                string? mediaId = flags.WithMedia || flags.Crop || body == null ? MediaFileId(item) : null;
                if (body == null && mediaId == null)
                    continue;

                var from = Author(item);
                long? avatarOwner = from.UserId ?? AuthorChatId(item);
                byte[]? avatar = null;
                if (avatarOwner is { } owner && !avatars.TryGetValue(owner, out avatar))
                    avatars[owner] = avatar = from.UserId != null ? await LoadAvatarAsync(bot, owner, ct) : await LoadChatAvatarAsync(bot, owner, ct);

                await LoadCustomEmojiAsync(bot, item, customEmoji, ct);

                var target = flags.WithReply ? item.ReplyToMessage : null;
                preview ??= VisibleText(item);

                cards.Add(new QuoteCard(
                    from.Name,
                    from.UserId ?? from.Name.Sum(c => c),
                    body != null ? item.Text ?? item.Caption : null,
                    avatar,
                    mediaId != null ? await DownloadAsync(bot, mediaId, ct) : null,
                    target != null ? Author(target).Name : null,
                    target != null ? VisibleText(target) ?? "Медиа" : null,
                    background,
                    flags.Scale,
                    Spans(item, customEmoji),
                    flags.Crop));
            }

            if (cards.Count == 0)
            {
                await ReplyAsync(bot, command, "Тут нечего цитировать: в сообщении нет текста или картинки.", ct);
                return;
            }

            var quoted = cards.ToArray();
            var replyTo = new ReplyParameters { MessageId = source.MessageId, AllowSendingWithoutReply = true };

            if (flags.File)
            {
                using var png = new MemoryStream(QuoteRenderer.RenderPng(quoted));
                await bot.SendDocument(chatId, InputFile.FromStream(png, "quote.png"), replyParameters: replyTo, cancellationToken: ct);
            }
            else if (flags.Png)
            {
                using var photo = new MemoryStream(QuoteRenderer.RenderPhoto(quoted));
                await bot.SendPhoto(chatId, InputFile.FromStream(photo, "quote.png"), replyParameters: replyTo, cancellationToken: ct);
            }
            else
            {
                int id = _store.NextId(chatId);
                using var webp = new MemoryStream(QuoteRenderer.RenderSticker(quoted));
                var sent = await bot.SendSticker(chatId, InputFile.FromStream(webp, "quote.webp"),
                    replyParameters: replyTo, replyMarkup: settings.RatingOff ? null : Keyboard(id, 0, 0), cancellationToken: ct);

                _store.Add(chatId, new SavedQuote
                {
                    Id = id,
                    FileId = sent.Sticker?.FileId ?? "",
                    MessageId = sent.MessageId,
                    Author = Preview(string.Join(", ", quoted.Select(c => c.Name).Distinct())),
                    Preview = Preview(preview ?? "Медиа"),
                    At = DateTime.UtcNow,
                });
            }

            var notes = new List<string>();
            if (cards.Count < flags.Count)
                notes.Add($"Собрал {cards.Count} из {flags.Count}: остальных сообщений ещё нет, или бот их не видел после запуска.");
            if (flags.WithReply && cards.All(c => c.ReplyName == null))
                notes.Add("Без блока ответа: это не ответ, или сообщение старше последнего запуска бота.");

            if (notes.Count > 0)
                await ReplyAsync(bot, command, string.Join("\n", notes), ct);
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
                replyMarkup: _settings.Get(command.Chat.Id).RatingOff ? null : Keyboard(quote.Id, quote.Up.Count, quote.Down.Count), cancellationToken: ct);
            Cleanup.DeleteLater(bot, command.Chat.Id, Cleanup.CommandDelay, command.MessageId);
        }

        private async Task SetColorAsync(ITelegramBotClient bot, Message command, string args, CancellationToken ct)
        {
            long chatId = command.Chat.Id;
            string value = args.Trim();

            if (value.Length == 0)
            {
                string current = _settings.Get(chatId).Background ?? "по умолчанию";
                await ReplyAsync(bot, command, $"Цвет цитат: {current}.\n/qcolor красный или /qcolor #ff8800 — поменять, /qcolor сброс — вернуть обычный.", ct);
                return;
            }

            if (!await IsAdminAsync(bot, command, ct))
            {
                await ReplyAsync(bot, command, "Менять цвет цитат могут только админы.", ct);
                return;
            }

            if (value.ToLowerInvariant() is "сброс" or "reset")
            {
                _settings.Update(chatId, s => s.Background = null);
                await ReplyAsync(bot, command, "Вернул обычный цвет цитат.", ct);
            }
            else if (ParseColor(value) is { } color)
            {
                _settings.Update(chatId, s => s.Background = color.ToString());
                await ReplyAsync(bot, command, "Запомнил цвет цитат для этого чата.", ct);
            }
            else
            {
                await ReplyAsync(bot, command, $"Не понял цвет «{value}». Напиши, например, красный или #ff8800.", ct);
            }
        }

        private async Task ToggleRatingAsync(ITelegramBotClient bot, Message command, CancellationToken ct)
        {
            if (!await IsAdminAsync(bot, command, ct))
            {
                await ReplyAsync(bot, command, "Включать и выключать оценки могут только админы.", ct);
                return;
            }

            bool off = !_settings.Get(command.Chat.Id).RatingOff;
            _settings.Update(command.Chat.Id, s => s.RatingOff = off);
            await ReplyAsync(bot, command, off ? "Новые цитаты будут без кнопок 👍 и 👎." : "Новые цитаты снова с кнопками 👍 и 👎.", ct);
        }

        private static async Task<bool> IsAdminAsync(ITelegramBotClient bot, Message command, CancellationToken ct)
        {
            if (command.SenderChat?.Id == command.Chat.Id)
                return true;

            if (command.From is not { } user)
                return false;

            var member = await bot.GetChatMember(command.Chat.Id, user.Id, ct);
            return member.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
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

                if (lower.All(char.IsAsciiDigit))
                    flags = flags with { Count = int.TryParse(lower, out int count) ? Math.Clamp(count, 1, MaxQuotedMessages) : MaxQuotedMessages };
                else if (lower == "p")
                    flags = flags with { Png = true };
                else if (lower == "i")
                    flags = flags with { File = true };
                else if (lower == "c")
                    flags = flags with { Crop = true };
                else if (lower == "r")
                    flags = flags with { WithReply = true };
                else if (lower == "m")
                    flags = flags with { WithMedia = true };
                else if (lower[0] == 's' && float.TryParse(lower[1..].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) && float.IsFinite(scale))
                    flags = flags with { Scale = Math.Clamp(scale, 0.5f, 2f) };
                else if (ParseColor(lower) is { } color)
                    flags = flags with { Background = color };
                else
                {
                    bad = token;
                    return false;
                }
            }

            return true;
        }

        internal static SKColor? ParseColor(string value)
        {
            if (NamedColors.TryGetValue(value, out var named))
                return named;

            return value.StartsWith('#') && SKColor.TryParse(value, out var hex) ? hex.WithAlpha(255) : null;
        }

        internal static IReadOnlyList<QuoteSpan> Spans(Message message, IReadOnlyDictionary<string, byte[]> customEmoji)
        {
            var spans = new List<QuoteSpan>();

            foreach (var entity in Entities(message))
            {
                if (entity.Type == MessageEntityType.CustomEmoji)
                {
                    if (entity.CustomEmojiId != null && customEmoji.TryGetValue(entity.CustomEmojiId, out var image))
                        spans.Add(new QuoteSpan(entity.Offset, entity.Length, QuoteStyle.None, image));
                    continue;
                }

                var style = entity.Type switch
                {
                    MessageEntityType.Bold => QuoteStyle.Bold,
                    MessageEntityType.Italic => QuoteStyle.Italic,
                    MessageEntityType.Underline => QuoteStyle.Underline,
                    MessageEntityType.Strikethrough => QuoteStyle.Strike,
                    MessageEntityType.Spoiler => QuoteStyle.Spoiler,
                    MessageEntityType.Code or MessageEntityType.Pre => QuoteStyle.Mono,
                    MessageEntityType.TextLink or MessageEntityType.Url or MessageEntityType.Mention or MessageEntityType.TextMention
                        or MessageEntityType.Hashtag or MessageEntityType.Cashtag or MessageEntityType.BotCommand
                        or MessageEntityType.Email or MessageEntityType.PhoneNumber => QuoteStyle.Link,
                    _ => QuoteStyle.None,
                };

                if (style != QuoteStyle.None)
                    spans.Add(new QuoteSpan(entity.Offset, entity.Length, style));
            }

            return spans;
        }

        internal static string? VisibleText(Message message)
        {
            string? raw = message.Text ?? message.Caption;
            if (raw == null)
                return null;

            var chars = raw.ToCharArray();
            foreach (var entity in Entities(message).Where(e => e.Type == MessageEntityType.Spoiler))
                for (int i = entity.Offset; i < Math.Min(entity.Offset + entity.Length, chars.Length); i++)
                    chars[i] = '░';

            string text = new string(chars).Trim();
            return text.Length == 0 ? null : text;
        }

        private static MessageEntity[] Entities(Message message) =>
            (message.Text != null ? message.Entities : message.CaptionEntities) ?? [];

        internal static long? AuthorChatId(Message message) => message.ForwardOrigin switch
        {
            MessageOriginChat origin => origin.SenderChat.Id,
            MessageOriginChannel origin => origin.Chat.Id,
            MessageOriginUser or MessageOriginHiddenUser => null,
            _ => message.SenderChat?.Id,
        };

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
                return StickerImage(sticker);

            return (message.Video?.Thumbnail ?? message.Animation?.Thumbnail ?? message.VideoNote?.Thumbnail ?? message.Document?.Thumbnail)?.FileId;
        }

        private static string? StickerImage(Sticker sticker) =>
            sticker.IsAnimated || sticker.IsVideo ? sticker.Thumbnail?.FileId : sticker.FileId;

        private static async Task LoadCustomEmojiAsync(ITelegramBotClient bot, Message message, Dictionary<string, byte[]> known, CancellationToken ct)
        {
            var ids = Entities(message)
                .Where(e => e.Type == MessageEntityType.CustomEmoji && e.CustomEmojiId != null && !known.ContainsKey(e.CustomEmojiId))
                .Select(e => e.CustomEmojiId!)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
                return;

            try
            {
                foreach (var sticker in await bot.GetCustomEmojiStickers(ids, ct))
                    if (sticker.CustomEmojiId != null && StickerImage(sticker) is { } fileId && await DownloadAsync(bot, fileId, ct) is { } image)
                        known[sticker.CustomEmojiId] = image;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Quote] Не удалось получить премиум-эмодзи: {ex.GetType().Name}");
            }
        }

        private static async Task<byte[]?> LoadChatAvatarAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
        {
            try
            {
                var chat = await bot.GetChat(chatId, ct);
                return chat.Photo is { } photo ? await DownloadAsync(bot, photo.SmallFileId, ct) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Quote] Не удалось получить аватар чата: {ex.GetType().Name}");
                return null;
            }
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
