using AnikiChatBot.Modules;
using AnikiChatBot.Modules.Pig;
using Telegram.Bot.Types;

namespace AnikiChatBot.Tests
{
    public class PigGameTests : IDisposable
    {
        private readonly string _file = $"pig_test_{Guid.NewGuid():N}.json";
        private static readonly User Vasya = new User { Id = 1, FirstName = "Вася" };
        private static readonly User Petya = new User { Id = 2, FirstName = "Петя", LastName = "Иванов" };
        private static readonly DateOnly Day = new DateOnly(2026, 9, 21);

        public void Dispose() => System.IO.File.Delete(_file);

        private static Player PlayerWith(params int[] deltas)
        {
            var player = new Player { UserId = 1, Size = 50 };
            for (int i = 0; i < deltas.Length; i++)
                PigGame.ApplyGrow(player, Day.AddDays(i), deltas[i]);
            return player;
        }

        private static List<string> Check(Player player, DateTime? now = null) =>
            PigAchievements.CheckAfterGrow(player, now ?? new DateTime(2026, 9, 21, 15, 0, 0)).ToList();

        [Fact]
        public void Deltas_stay_within_range_and_all_outcomes_happen()
        {
            var random = new Random(1);
            var deltas = Enumerable.Range(0, 5000).Select(_ => PigGame.RollDelta(random)).ToList();

            Assert.All(deltas, d => Assert.InRange(d, -20, 20));
            Assert.Contains(20, deltas);
            Assert.Contains(-20, deltas);
            Assert.Contains(0, deltas);
            Assert.True(deltas.Average() > 0, "В среднем свинья должна набирать вес");
        }

        [Fact]
        public void Size_never_drops_below_one()
        {
            var player = new Player { Size = 5 };
            PigGame.ApplyGrow(player, Day, -20);

            Assert.Equal(1, player.Size);
            Assert.Equal(-20, player.History[^1].Delta);
        }

        [Fact]
        public void Streak_counts_consecutive_days_and_resets_after_gap()
        {
            var player = new Player { Size = 10 };
            PigGame.ApplyGrow(player, Day, 1);
            PigGame.ApplyGrow(player, Day.AddDays(1), 1);
            Assert.Equal(2, player.Streak);

            PigGame.ApplyGrow(player, Day.AddDays(3), 1);
            Assert.Equal(1, player.Streak);
        }

        [Fact]
        public void Grow_is_allowed_once_per_day()
        {
            var player = new Player { Size = 10 };
            Assert.True(PigGame.CanGrow(player, Day));
            PigGame.ApplyGrow(player, Day, 3);
            Assert.False(PigGame.CanGrow(player, Day));
            Assert.True(PigGame.CanGrow(player, Day.AddDays(1)));
        }

        [Fact]
        public void Duel_transfer_is_within_limits_and_loser_keeps_at_least_one_cm()
        {
            var random = new Random(7);
            for (int i = 0; i < 2000; i++)
            {
                var result = PigGame.ResolveDuel(40, 10, random);
                int loser = result.ChallengerWins ? 10 : 40;

                Assert.InRange(result.Transfer, 0, loser - 1);
                Assert.True(result.Transfer <= loser / 2 || loser <= 2);
            }

            Assert.Equal(0, PigGame.ResolveDuel(100, 1, new Random(1)) is { ChallengerWins: true } r ? r.Transfer : 0);
        }

        [Fact]
        public void Heavier_pig_wins_more_often_but_not_always()
        {
            var random = new Random(3);
            int wins = Enumerable.Range(0, 5000).Count(_ => PigGame.ResolveDuel(90, 10, random).ChallengerWins);

            Assert.InRange(wins, 3000, 4000);
        }

        [Theory]
        [InlineData(19, "kamasutra")]
        [InlineData(-32, "eighteen")]
        [InlineData(20, "monster")]
        [InlineData(-1, "oops")]
        public void Size_and_delta_achievements(int delta, string expected)
        {
            Assert.Contains(expected, Check(PlayerWith(delta)));
        }

        [Fact]
        public void Pattern_achievements()
        {
            Assert.Contains("groundhog", Check(PlayerWith(-1, -2, -3)));
            Assert.Contains("nohelp", Check(PlayerWith(0, 0, 0)));
            Assert.Contains("schrodinger", Check(PlayerWith(5, -3, 0)));
            Assert.Contains("pendulum", Check(PlayerWith(20, -20)));
            Assert.Contains("lucky", Check(PlayerWith(1, 2, 3, 4, 5)));
            Assert.Contains("gymrat", Check(PlayerWith(20, 20, 20, 20, 20)));
            Assert.Contains("fridays", Check(PlayerWith(1, 1, 1, 1, 1, 1, 1)));
            Assert.Contains("streak7", Check(PlayerWith(0, 1, -1, 0, 1, -1, 0)));
            Assert.Contains("rollercoaster", Check(PlayerWith(1, -1, 0)));

            Assert.DoesNotContain("groundhog", Check(PlayerWith(-1, 1, -3)));
            Assert.DoesNotContain("fridays", Check(PlayerWith(1, 1, 1, -1, 1, 1, 1)));
        }

        [Fact]
        public void Infinity_war_needs_minus_twenty_to_one_cm()
        {
            var player = new Player { Size = 15 };
            PigGame.ApplyGrow(player, Day, -20);

            Assert.Contains("infinity", Check(player));
        }

        [Fact]
        public void Date_achievements()
        {
            var player = PlayerWith(1);
            Assert.Contains("midnight", Check(player, new DateTime(2026, 9, 21, 0, 0, 30)));
            Assert.Contains("agent007", Check(player, new DateTime(2026, 7, 7, 7, 15, 0)));
            Assert.Contains("newhope", Check(player, new DateTime(2026, 10, 1, 12, 0, 0)));
            Assert.Contains("valentine", Check(player, new DateTime(2027, 2, 14, 12, 0, 0)));
            Assert.Contains("halloween", Check(player, new DateTime(2026, 10, 31, 12, 0, 0)));
            Assert.DoesNotContain("midnight", Check(player, new DateTime(2026, 9, 21, 0, 1, 0)));
        }

        [Fact]
        public void Last_year_achievement_needs_dec_31_and_jan_1()
        {
            var player = new Player { Size = 10 };
            PigGame.ApplyGrow(player, new DateOnly(2026, 12, 31), 1);
            PigGame.ApplyGrow(player, new DateOnly(2027, 1, 1), 1);

            Assert.Contains("lastyear", Check(player, new DateTime(2027, 1, 1, 12, 0, 0)));
        }

        [Fact]
        public void All_achievement_ids_are_unique_and_known()
        {
            var ids = PigAchievements.All.Select(a => a.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());

            var player = PlayerWith(20, -20, 0, 20, 20, 20, 20);
            Assert.All(Check(player, new DateTime(2026, 7, 7, 7, 0, 0)), id => Assert.Contains(id, ids));
        }

        [Theory]
        [InlineData("/grow", "AnikiChatBot", "grow", "")]
        [InlineData("/GROW@AnikiChatBot", "AnikiChatBot", "grow", "")]
        [InlineData("/name Большой Бро", "AnikiChatBot", "name", "Большой Бро")]
        [InlineData("/name@anikichatbot  Бро ", "AnikiChatBot", "name", "Бро")]
        public void Parses_commands(string text, string bot, string command, string args)
        {
            var parsed = BotCommands.Parse(text, bot);
            Assert.NotNull(parsed);
            Assert.Equal(command, parsed.Command);
            Assert.Equal(args, parsed.Args);
        }

        [Theory]
        [InlineData("/grow@OtherBot")]
        [InlineData("grow")]
        [InlineData("")]
        [InlineData(null)]
        public void Ignores_non_commands_and_commands_for_other_bots(string? text)
        {
            Assert.Null(BotCommands.Parse(text, "AnikiChatBot"));
        }

        [Fact]
        public void Full_game_flow_and_persistence()
        {
            var store = new PigStore(_file);
            var module = new PigModule(store, new Random(42));

            Assert.Contains("нет свиньи", module.My(-1, Vasya));
            Assert.Contains("никто не завёл свинью", module.Top(-1));

            string first = module.Grow(-1, Vasya);
            Assert.Contains("у тебя появилась свинья", first);
            Assert.Contains("уже кормил свинью сегодня", module.Grow(-1, Vasya));

            module.Grow(-1, Petya);
            Assert.Contains("🏷️", module.SetName(-1, Vasya, "Малыш"));
            Assert.Contains("Окрестили", module.Achievements(Vasya).Split('\n').First(l => l.Contains("Окрестили")));
            Assert.StartsWith("✅", module.Achievements(Vasya).Split('\n').First(l => l.Contains("Окрестили")));

            string top = module.Top(-1);
            Assert.Contains("Вася «Малыш»", top);
            Assert.Contains("Петя Иванов", top);

            string day = module.PigOfDay(-1);
            Assert.Contains("Свинья дня сегодня", day);
            Assert.Contains("уже выбрана", module.PigOfDay(-1));

            Assert.Contains("Засветилась", module.Grow(-2, Vasya));

            Assert.False(System.IO.File.Exists(_file));
            store.Flush();
            var restarted = new PigModule(new PigStore(_file));
            Assert.Contains("Малыш", restarted.My(-1, Vasya));
            Assert.Contains("уже кормил свинью сегодня", restarted.Grow(-1, Vasya));
        }

        [Fact]
        public void Name_is_limited_to_64_characters()
        {
            var module = new PigModule(new PigStore(_file));
            module.Grow(-1, Vasya);

            Assert.Contains("Слишком длинное", module.SetName(-1, Vasya, new string('a', 65)));
            Assert.Contains("🏷️", module.SetName(-1, Vasya, new string('a', 64)));
        }

        [Fact]
        public void Duel_updates_wins_losses_stats_and_achievements()
        {
            string statsFile = $"stats_duel_{Guid.NewGuid():N}.json";
            try
            {
                var stats = new StatsService(statsFile);
                var module = new PigModule(new PigStore(_file), new Random(5), stats);
                module.Grow(-1, Vasya);
                module.Grow(-1, Petya);

                string result = module.PlayDuel(-1, Vasya.Id, Petya);
                Assert.Contains("⚔️ Дуэль", result);
                Assert.Contains("Первая кровь", result);

                string my1 = module.My(-1, Vasya), my2 = module.My(-1, Petya);
                bool vasyaWon = my1.Contains("Дуэли: 1 побед");
                Assert.True(vasyaWon ? my2.Contains("0 побед, 1 поражений") : my1.Contains("0 побед, 1 поражений"));

                var week = stats.GetChat(-1);
                Assert.Equal(1, week.Players.Values.Sum(p => p.DuelWins));
                Assert.Equal(0, week.Players.Values.Sum(p => p.Growth));

                Assert.NotNull(module.GetLeader(-1));
                Assert.Null(module.GetLeader(-99));
            }
            finally
            {
                System.IO.File.Delete(statsFile);
            }
        }

        [Fact]
        public void Give_transfers_tons()
        {
            var module = new PigModule(new PigStore(_file), new Random(42));
            var stranger = new User { Id = 3, FirstName = "Коля" };

            Assert.Contains("Ответь /give", module.Give(-1, Vasya, null, "5"));
            Assert.Contains("Ответь /give", module.Give(-1, Vasya, Vasya, "5"));
            Assert.Contains("нет свиньи", module.Give(-1, Vasya, Petya, "5"));

            module.Grow(-1, Vasya);
            module.Grow(-1, Petya);
            Assert.Contains("ещё нет свиньи", module.Give(-1, Vasya, stranger, "1"));
            Assert.Contains("Укажи, сколько", module.Give(-1, Vasya, Petya, "-3"));

            int vasyaBefore = WeightOf(module.My(-1, Vasya));
            int petyaBefore = WeightOf(module.My(-1, Petya));

            Assert.Contains("Столько нет", module.Give(-1, Vasya, Petya, vasyaBefore.ToString()));

            string result = module.Give(-1, Vasya, Petya, "1");
            Assert.StartsWith("🎁 Вася дарит Петя Иванов 1 т.", result);
            Assert.Equal(vasyaBefore - 1, WeightOf(module.My(-1, Vasya)));
            Assert.Equal(petyaBefore + 1, WeightOf(module.My(-1, Petya)));
        }

        private static int WeightOf(string my) =>
            int.Parse(my.Split('\n').First(l => l.StartsWith("Вес:")).Split(' ')[1]);

        [Theory]
        [InlineData(1, 10, 20, DuelHit.Normal, new[] { "duel_first", "duel_goliath" })]
        [InlineData(10, 30, 10, DuelHit.Knockout, new[] { "duel_first", "duel_10", "duel_knockout" })]
        [InlineData(50, 30, 59, DuelHit.Critical, new[] { "duel_first", "duel_10", "duel_50" })]
        public void Duel_achievements(int wins, int winnerSize, int loserSize, DuelHit hit, string[] expected)
        {
            var winner = new Player { DuelWins = wins };
            Assert.Equal(expected, PigAchievements.CheckAfterDuelWin(winner, winnerSize, loserSize, hit).ToArray());
        }
    }
}
