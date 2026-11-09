using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class RepeaterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"repeater_test_{Guid.NewGuid():N}");
        private string DbPath => Path.Combine(_dir, "replies.txt");

        public RepeaterTests() => Directory.CreateDirectory(_dir);

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        [Theory]
        [InlineData("  привет  ", "привет")]
        [InlineData("строка 1\r\nстрока 2", "строка 1 строка 2")]
        [InlineData("a\nb", "a b")]
        public void Normalizes_text_to_single_line(string input, string expected)
        {
            Assert.Equal(expected, RepeaterModule.Normalize(input));
        }

        [Theory]
        [InlineData("Привет!!!", "привет")]
        [InlineData("привет 😂", "привет")]
        [InlineData("...ну   и   что?", "ну и что")]
        [InlineData("«Кто там?»", "кто там")]
        [InlineData("😂", "😂")]
        [InlineData("???", "???")]
        public void Match_key_ignores_case_spaces_and_edge_punctuation(string input, string expected)
        {
            Assert.Equal(expected, RepeaterModule.MatchKey(input));
        }

        [Theory]
        [InlineData("привет", "здарова", true)]
        [InlineData("/grow", "ок", false)]
        [InlineData("привет", "/top", false)]
        [InlineData("глянь https://x.com/a/status/1", "огонь", false)]
        [InlineData("канал t.me/something", "ок", false)]
        [InlineData("привет", "Привет!", false)]
        [InlineData("a:::b", "c", false)]
        [InlineData("@vasya", "чё надо", false)]
        [InlineData("@vasya иди сюда", "не", false)]
        [InlineData("пиши на test@mail.ru", "ок", true)]
        [InlineData("привет", "заходи https://spam.com", false)]
        [InlineData("привет", "заходи t.me/spam", false)]
        [InlineData("привет", "спроси @vasya", false)]
        [InlineData("привет", "пиши на test@mail.ru", true)]
        public void Decides_what_to_learn(string trigger, string answer, bool expected)
        {
            Assert.Equal(expected, RepeaterModule.ShouldLearn(trigger, answer));
        }

        [Fact]
        public void Detects_mentions_in_messages()
        {
            Assert.True(RepeaterModule.HasMention(new Telegram.Bot.Types.Message { Text = "@vasya привет" }));
            Assert.False(RepeaterModule.HasMention(new Telegram.Bot.Types.Message { Text = "привет" }));

            var textMention = new Telegram.Bot.Types.Message
            {
                Text = "Вася привет",
                Entities = [new Telegram.Bot.Types.MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.TextMention, Offset = 0, Length = 4 }]
            };
            Assert.True(RepeaterModule.HasMention(textMention));
        }

        private RepeaterModule NewModule() => new(filePath: DbPath, optOutPath: Path.Combine(_dir, "noreply.json"));

        private static Telegram.Bot.Types.Update Said(long userId, string text, Telegram.Bot.Types.Message? replyTo = null) => new()
        {
            Message = new Telegram.Bot.Types.Message
            {
                Text = text,
                From = new Telegram.Bot.Types.User { Id = userId, FirstName = "Имя" },
                ReplyToMessage = replyTo,
            }
        };

        [Fact]
        public void Opt_out_toggles_and_survives_restart()
        {
            var module = NewModule();

            Assert.True(module.ToggleOptOut(7));
            Assert.True(module.ToggleOptOut(8));
            Assert.False(module.ToggleOptOut(8));

            var reloaded = NewModule();
            Assert.False(reloaded.ToggleOptOut(7));
            Assert.True(reloaded.ToggleOptOut(8));
        }

        [Fact]
        public async Task Opted_out_user_does_not_teach_with_answers_or_triggers()
        {
            var module = NewModule();
            module.ToggleOptOut(7);

            var fromOptedOut = Said(7, "вопрос").Message;
            var fromOther = Said(8, "другой вопрос").Message;

            await module.HandleRepeaterCommand(null!, Said(7, "ответ", fromOther), default);
            await module.HandleRepeaterCommand(null!, Said(8, "ответ", fromOptedOut), default);
            Assert.Equal(0, module.Count);

            await module.HandleRepeaterCommand(null!, Said(8, "ответ", fromOther), default);
            Assert.Equal(1, module.Count);
        }

        [Fact]
        public async Task Opted_out_user_gets_no_auto_reply_and_returning_user_does()
        {
            File.WriteAllLines(DbPath, ["привет:::здарова"]);
            var module = NewModule();
            module.ToggleOptOut(7);

            await module.HandleRepeaterCommand(null!, Said(7, "привет"), default);

            module.ToggleOptOut(7);
            await Assert.ThrowsAnyAsync<Exception>(() => module.HandleRepeaterCommand(null!, Said(7, "привет"), default));
        }

        [Fact]
        public void Rate_limit_allows_four_per_window_per_chat()
        {
            var limit = new ChatRateLimit(4, TimeSpan.FromSeconds(30));
            var t = new DateTime(2026, 1, 1);

            for (int i = 0; i < 4; i++)
                Assert.True(limit.TryTake(1, t.AddSeconds(i)));
            Assert.False(limit.TryTake(1, t.AddSeconds(10)));
            Assert.True(limit.TryTake(2, t.AddSeconds(10)));
            Assert.True(limit.TryTake(1, t.AddSeconds(30)));
            Assert.False(limit.TryTake(1, t.AddSeconds(30.5)));
        }

        [Fact]
        public void Does_not_learn_too_long_texts()
        {
            Assert.False(RepeaterModule.ShouldLearn(new string('а', RepeaterModule.MaxTriggerLength + 1), "ок"));
            Assert.False(RepeaterModule.ShouldLearn("привет", new string('а', RepeaterModule.MaxAnswerLength + 1)));
            Assert.True(RepeaterModule.ShouldLearn(new string('а', RepeaterModule.MaxTriggerLength), "ок"));
        }

        [Fact]
        public void Cleans_junk_and_merges_duplicates_on_load_with_backup()
        {
            File.WriteAllLines(DbPath,
            [
                "привет:::здарова",
                "Привет!:::хай",
                "/grow:::ок",
                "https://spam.com:::круто",
                $"{new string('а', 300)}:::ок",
                "как дела:::норм",
            ]);

            var module = new RepeaterModule(filePath: DbPath);

            Assert.Equal(2, module.Count);
            var saved = File.ReadAllLines(DbPath);
            Assert.Contains("Привет!:::хай", saved);
            Assert.Contains("как дела:::норм", saved);
            Assert.Single(Directory.GetFiles(_dir, "replies.backup-*.txt"));
        }

        [Fact]
        public void Clean_database_is_not_rewritten_and_no_backup_is_made()
        {
            File.WriteAllLines(DbPath, ["привет:::здарова", "как дела:::норм"]);

            var module = new RepeaterModule(filePath: DbPath);

            Assert.Equal(2, module.Count);
            Assert.Empty(Directory.GetFiles(_dir, "replies.backup-*.txt"));
        }
    }
}
