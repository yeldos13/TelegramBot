using AnikiChatBot.Modules;
using AnikiChatBot.Modules.Quote;
using SkiaSharp;
using Telegram.Bot.Types;

namespace AnikiChatBot.Tests
{
    public class QuoteTests : IDisposable
    {
        private readonly string _file = $"quotes_{Guid.NewGuid():N}.json";

        public void Dispose() => System.IO.File.Delete(_file);

        private static SavedQuote Quote(int id, string author = "Вася") =>
            new() { Id = id, FileId = $"file{id}", MessageId = 100 + id, Author = author, Preview = "текст", At = DateTime.UtcNow };

        [Fact]
        public void Parses_all_flags_in_any_order()
        {
            Assert.True(QuoteModule.TryParseFlags("s1,5 M #FF8800 r P", out var flags, out _));

            Assert.True(flags.Png);
            Assert.True(flags.WithReply);
            Assert.True(flags.WithMedia);
            Assert.Equal(1.5f, flags.Scale);
            Assert.Equal(new SKColor(0xFF, 0x88, 0x00), flags.Background);
        }

        [Theory]
        [InlineData("", 1)]
        [InlineData("3", 3)]
        [InlineData("0", 1)]
        [InlineData("50", 10)]
        [InlineData("99999999999999", 10)]
        [InlineData("p 4 r", 4)]
        public void Message_count_is_limited(string args, int expected)
        {
            Assert.True(QuoteModule.TryParseFlags(args, out var flags, out _));
            Assert.Equal(expected, flags.Count);
        }

        private static Message Msg(int id, string? text, long chat = -1) =>
            new() { Id = id, Chat = new Chat { Id = chat }, Text = text };

        [Fact]
        public void Range_takes_following_messages_of_this_chat_without_commands()
        {
            var module = new QuoteModule(new QuoteStore(_file));
            module.Remember(Msg(1, "раз"));
            module.Remember(Msg(2, "/roll"));
            module.Remember(Msg(3, "два"));
            module.Remember(Msg(4, null));
            module.Remember(Msg(5, "чужой", chat: -2));
            module.Remember(Msg(6, "три"));
            module.Remember(Msg(7, "четыре"));

            Assert.Equal([1, 3, 6], module.Range(Msg(1, "раз"), 3).Select(m => m.Id));
            Assert.Equal([6, 7], module.Range(Msg(6, "три"), 5).Select(m => m.Id));
        }

        [Fact]
        public void Range_does_not_glue_unseen_message_to_later_ones()
        {
            var module = new QuoteModule(new QuoteStore(_file));
            module.Remember(Msg(10, "новое"));

            Assert.Equal([5], module.Range(Msg(5, "старое"), 3).Select(m => m.Id));
            Assert.Equal([1], module.Range(Msg(1, "из другого чата", chat: -9), 3).Select(m => m.Id));
        }

        [Fact]
        public void Old_messages_leave_memory()
        {
            var module = new QuoteModule(new QuoteStore(_file));
            for (int id = 1; id <= 1001; id++)
                module.Remember(Msg(id, "текст"));

            Assert.Equal([1], module.Range(Msg(1, "текст"), 2).Select(m => m.Id));
            Assert.Equal([2, 3], module.Range(Msg(2, "текст"), 2).Select(m => m.Id));
        }

        [Fact]
        public void Parses_file_and_crop_flags()
        {
            Assert.True(QuoteModule.TryParseFlags("i c", out var flags, out _));
            Assert.True(flags.File);
            Assert.True(flags.Crop);
            Assert.False(flags.Png);
        }

        [Fact]
        public void Maps_entities_to_styles_and_skips_unknown_custom_emoji()
        {
            var message = new Message
            {
                Text = "жир код ссылка 😀",
                Entities =
                [
                    new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold, Offset = 0, Length = 3 },
                    new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.Code, Offset = 4, Length = 3 },
                    new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.Url, Offset = 8, Length = 6 },
                    new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.CustomEmoji, Offset = 15, Length = 2, CustomEmojiId = "known" },
                    new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.CustomEmoji, Offset = 15, Length = 2, CustomEmojiId = "unknown" },
                ]
            };
            byte[] image = [1];

            var spans = QuoteModule.Spans(message, new Dictionary<string, byte[]> { ["known"] = image });

            Assert.Equal(
                [QuoteStyle.Bold, QuoteStyle.Mono, QuoteStyle.Link, QuoteStyle.None],
                spans.Select(s => s.Style));
            Assert.Same(image, spans[3].Image);
        }

        [Fact]
        public void Uses_caption_entities_for_caption()
        {
            var message = new Message
            {
                Caption = "подпись",
                CaptionEntities = [new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.Italic, Offset = 0, Length = 7 }]
            };

            Assert.Equal(QuoteStyle.Italic, Assert.Single(QuoteModule.Spans(message, new Dictionary<string, byte[]>())).Style);
        }

        [Fact]
        public void Spoiler_text_is_hidden_in_previews()
        {
            var message = new Message
            {
                Text = "убийца дворецкий",
                Entities = [new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.Spoiler, Offset = 7, Length = 9 }]
            };

            Assert.Equal("убийца ░░░░░░░░░", QuoteModule.VisibleText(message));
            Assert.Null(QuoteModule.VisibleText(new Message { Text = "   " }));
        }

        [Fact]
        public void Spoiler_text_is_not_drawn()
        {
            var spans = new[] { new QuoteSpan(0, 3, QuoteStyle.Spoiler | QuoteStyle.Mono) };

            byte[] first = QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "abc", Spans: spans));
            byte[] second = QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "xyz", Spans: spans));

            Assert.Equal(first, second);
        }

        [Fact]
        public void Style_offsets_account_for_leading_spaces()
        {
            var plainSpans = new[] { new QuoteSpan(2, 3, QuoteStyle.Spoiler | QuoteStyle.Mono) };

            byte[] spaced = QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "  abc", Spans: plainSpans));
            byte[] other = QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "  xyz", Spans: plainSpans));

            Assert.Equal(spaced, other);
        }

        [Fact]
        public void Custom_emoji_replaces_its_fallback_and_broken_image_is_ignored()
        {
            using var surface = SKSurface.Create(new SKImageInfo(16, 16));
            surface.Canvas.Clear(SKColors.Red);
            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);

            var good = new[] { new QuoteSpan(0, 2, QuoteStyle.None, encoded.ToArray()) };
            var broken = new[] { new QuoteSpan(0, 2, QuoteStyle.None, [1, 2, 3]) };

            Assert.NotEqual(
                QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "😀", Spans: good)),
                QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "😀")));
            Assert.Equal(
                QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "😀", Spans: broken)),
                QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, "😀")));
        }

        [Fact]
        public void Photo_has_no_transparency()
        {
            using var bitmap = SKBitmap.Decode(QuoteRenderer.RenderPhoto(new QuoteCard("Вася", 1, "текст")));
            Assert.Equal(SKColors.White, bitmap.GetPixel(bitmap.Width - 1, 0));
        }

        [Theory]
        [InlineData(1600, 900, 0, 0, 1600, 900)]
        [InlineData(1000, 1000, 0, 218.75f, 1000, 781.25f)]
        [InlineData(1600, 450, 400, 0, 1200, 450)]
        public void Crop_takes_the_center(int width, int height, float left, float top, float right, float bottom)
        {
            var rect = QuoteRenderer.CropSource(width, height, 9f / 16);

            Assert.Equal(left, rect.Left, 0.01f);
            Assert.Equal(top, rect.Top, 0.01f);
            Assert.Equal(right, rect.Right, 0.01f);
            Assert.Equal(bottom, rect.Bottom, 0.01f);
        }

        [Fact]
        public void Author_chat_gives_avatar_of_channel_but_not_of_user()
        {
            var sender = new User { Id = 1, FirstName = "Вася" };
            var channel = new Chat { Id = -100, Title = "Канал" };

            Assert.Equal(-100, QuoteModule.AuthorChatId(new Message { From = sender, ForwardOrigin = new MessageOriginChannel { Chat = channel } }));
            Assert.Equal(-100, QuoteModule.AuthorChatId(new Message { SenderChat = channel }));
            Assert.Null(QuoteModule.AuthorChatId(new Message { From = sender }));
            Assert.Null(QuoteModule.AuthorChatId(new Message { SenderChat = channel, ForwardOrigin = new MessageOriginUser { SenderUser = sender } }));
        }

        [Fact]
        public void Chat_settings_survive_restart_and_color_can_be_restored()
        {
            string path = _file + ".settings";
            try
            {
                var settings = new QuoteSettingsStore(path);
                settings.Update(-1, s => { s.Background = QuoteModule.ParseColor("красный").ToString(); s.RatingOff = true; });

                var loaded = new QuoteSettingsStore(path).Get(-1);

                Assert.True(loaded.RatingOff);
                Assert.True(SKColor.TryParse(loaded.Background, out var color));
                Assert.Equal(QuoteModule.ParseColor("красный"), color);
                Assert.False(new QuoteSettingsStore(path).Get(-2).RatingOff);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void Several_messages_fit_one_sticker()
        {
            var longText = new string('а', 2000);
            byte[] data = QuoteRenderer.RenderSticker(
                new QuoteCard("Вася", 1, longText),
                new QuoteCard("Вася", 1, "ещё"),
                new QuoteCard("Петя", 2, longText, ReplyName: "Вася", ReplyText: "ещё"));

            using var bitmap = SKBitmap.Decode(data);
            Assert.Equal(QuoteRenderer.StickerSide, Math.Max(bitmap.Width, bitmap.Height));
        }

        [Fact]
        public void Second_message_of_same_author_has_no_avatar_or_name()
        {
            using var single = SKBitmap.Decode(QuoteRenderer.RenderPng(
                new QuoteCard("Вася", 1, "а"), new QuoteCard("Вася", 1, "б")));
            int x = 10, y = single.Height - 10;

            Assert.Equal(0, single.GetPixel(x, y).Alpha);

            using var two = SKBitmap.Decode(QuoteRenderer.RenderPng(
                new QuoteCard("Вася", 1, "а"), new QuoteCard("Петя", 1, "б")));
            Assert.True(two.Height > single.Height);
        }

        [Theory]
        [InlineData("s9", 2f)]
        [InlineData("s0.1", 0.5f)]
        public void Scale_is_limited(string args, float expected)
        {
            Assert.True(QuoteModule.TryParseFlags(args, out var flags, out _));
            Assert.Equal(expected, flags.Scale);
        }

        [Theory]
        [InlineData("привет")]
        [InlineData("sabc")]
        [InlineData("s")]
        [InlineData("#zzz")]
        [InlineData("sInfinity")]
        public void Unknown_flag_is_reported(string token)
        {
            Assert.False(QuoteModule.TryParseFlags($"p {token}", out _, out string? bad));
            Assert.Equal(token, bad);
        }

        [Fact]
        public void Named_color_works_without_flags()
        {
            Assert.True(QuoteModule.TryParseFlags("Красный", out var flags, out _));
            Assert.NotNull(flags.Background);
            Assert.True(QuoteModule.TryParseFlags("", out var empty, out _));
            Assert.Null(empty.Background);
        }

        [Fact]
        public void Vote_toggles_and_switches_side()
        {
            var store = new QuoteStore(_file);
            store.Add(-1, Quote(1));

            Assert.Equal((1, 0), store.Vote(-1, 1, 7, up: true));
            Assert.Equal((0, 1), store.Vote(-1, 1, 7, up: false));
            Assert.Equal((0, 0), store.Vote(-1, 1, 7, up: false));
            Assert.Null(store.Vote(-1, 2, 7, up: true));
            Assert.Null(store.Vote(-2, 1, 7, up: true));
        }

        [Fact]
        public void Top_keeps_only_positive_quotes_of_this_chat_best_first()
        {
            var store = new QuoteStore(_file);
            store.Add(-1, Quote(1));
            store.Add(-1, Quote(2));
            store.Add(-1, Quote(3));
            store.Add(-2, Quote(1));

            store.Vote(-1, 1, 7, up: true);
            store.Vote(-1, 2, 7, up: true);
            store.Vote(-1, 2, 8, up: true);
            store.Vote(-1, 3, 7, up: false);
            store.Vote(-2, 1, 7, up: true);

            Assert.Equal([2, 1], store.Top(-1, 10).Select(q => q.Id));
            Assert.Equal([2], store.Top(-1, 1).Select(q => q.Id));
            Assert.NotEqual(3, store.RandomTop(-1, new Random(1))!.Id);
            Assert.Null(store.RandomTop(-3, new Random(1)));
        }

        [Fact]
        public void Quotes_and_votes_survive_restart()
        {
            var store = new QuoteStore(_file);
            store.Add(-1, Quote(1));
            store.Vote(-1, 1, 7, up: true);

            var reloaded = new QuoteStore(_file);

            Assert.Equal(2, reloaded.NextId(-1));
            Assert.Equal(1, reloaded.NextId(-2));
            Assert.Equal((0, 0), reloaded.Vote(-1, 1, 7, up: true));
        }

        [Fact]
        public void Top_text_escapes_html_and_links_only_supergroups()
        {
            var quote = Quote(1, "<Вася>");
            quote.Up.Add(7);

            string super = QuoteModule.BuildTop(-1001234567890, [quote]);
            Assert.Contains("https://t.me/c/1234567890/101", super);
            Assert.Contains("&lt;Вася&gt;", super);
            Assert.DoesNotContain("<Вася>", super);

            Assert.DoesNotContain("https://", QuoteModule.BuildTop(-12345, [quote]));
            Assert.Contains("Пока нет цитат", QuoteModule.BuildTop(-1, []));
        }

        [Fact]
        public void Author_is_the_original_sender_of_a_forward()
        {
            var sender = new User { Id = 1, FirstName = "Вася" };
            var original = new User { Id = 2, FirstName = "Петя" };

            Assert.Equal((1L, "Вася"), QuoteModule.Author(new Message { From = sender }));
            Assert.Equal((2L, "Петя"), QuoteModule.Author(new Message { From = sender, ForwardOrigin = new MessageOriginUser { SenderUser = original } }));
            Assert.Equal(((long?)null, "Скрытый"), QuoteModule.Author(new Message { From = sender, ForwardOrigin = new MessageOriginHiddenUser { SenderUserName = "Скрытый" } }));
            Assert.Equal(((long?)null, "Группа"), QuoteModule.Author(new Message { From = sender, SenderChat = new Chat { Title = "Группа" } }));
        }

        [Fact]
        public void Media_uses_largest_photo_and_thumbnail_of_animated_sticker()
        {
            var photo = new Message { Photo = [new PhotoSize { FileId = "small" }, new PhotoSize { FileId = "big" }] };
            Assert.Equal("big", QuoteModule.MediaFileId(photo));

            var animated = new Message { Sticker = new Sticker { FileId = "tgs", IsAnimated = true, Thumbnail = new PhotoSize { FileId = "thumb" } } };
            Assert.Equal("thumb", QuoteModule.MediaFileId(animated));

            var still = new Message { Sticker = new Sticker { FileId = "webp" } };
            Assert.Equal("webp", QuoteModule.MediaFileId(still));

            Assert.Null(QuoteModule.MediaFileId(new Message { Text = "текст" }));
        }

        [Fact]
        public void Preview_is_one_short_line_and_keeps_emoji_whole()
        {
            Assert.Equal("а б", QuoteModule.Preview("а\n  б"));

            string preview = QuoteModule.Preview(string.Concat(Enumerable.Repeat("😂", 60)));
            Assert.Equal(string.Concat(Enumerable.Repeat("😂", 50)) + "…", preview);
        }

        [Theory]
        [InlineData("короткий текст", 1f)]
        [InlineData("короткий текст", 2f)]
        [InlineData("🤦‍♂️👍🏽 эмодзи и العربية", 0.5f)]
        [InlineData("", 1f)]
        public void Sticker_fits_telegram_limit(string text, float scale)
        {
            byte[] data = QuoteRenderer.RenderSticker(new QuoteCard("Вася", 1, text, Scale: scale));

            using var bitmap = SKBitmap.Decode(data);
            Assert.NotNull(bitmap);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(data, 0, 4));
            Assert.InRange(Math.Max(bitmap.Width, bitmap.Height), 1, QuoteRenderer.StickerSide);
        }

        [Fact]
        public void Very_long_text_and_name_do_not_break_the_sticker()
        {
            string longText = string.Join("\n", Enumerable.Repeat(new string('а', 300), 200));
            byte[] data = QuoteRenderer.RenderSticker(new QuoteCard(new string('я', 300), -5, longText, ReplyName: "Петя", ReplyText: longText));

            using var bitmap = SKBitmap.Decode(data);
            Assert.Equal(QuoteRenderer.StickerSide, Math.Max(bitmap.Width, bitmap.Height));
        }

        [Fact]
        public void Broken_avatar_and_media_are_ignored()
        {
            byte[] junk = [1, 2, 3];
            byte[] data = QuoteRenderer.RenderSticker(new QuoteCard("Вася", 1, "текст", junk, junk));

            Assert.NotNull(SKBitmap.Decode(data));
        }

        [Fact]
        public void Png_is_larger_and_keeps_transparent_corners()
        {
            byte[] data = QuoteRenderer.RenderPng(new QuoteCard("Вася", 1, new string('а', 200), Background: SKColors.White));

            using var bitmap = SKBitmap.Decode(data);
            Assert.True(bitmap.Width > QuoteRenderer.StickerSide);
            Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, 0).Alpha);
        }

        [Theory]
        [InlineData("😂", true)]
        [InlineData("❤️", true)]
        [InlineData("1️⃣", true)]
        [InlineData("а", false)]
        [InlineData("!", false)]
        public void Detects_emoji(string grapheme, bool expected)
        {
            Assert.Equal(expected, QuoteRenderer.IsEmoji(grapheme));
        }
    }
}
