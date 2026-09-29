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
