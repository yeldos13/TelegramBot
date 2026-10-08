namespace AnikiChatBot.Modules.Pig
{
    public record Achievement(string Id, string Title, string Description);

    public static class PigAchievements
    {
        public const string Named = "named";
        public const string PigOfDay = "pig_of_day";
        public const string TwoChats = "chats_2";
        public const string FiveChats = "chats_5";
        public const string TenChats = "chats_10";

        public static readonly IReadOnlyList<Achievement> All =
        [
            new("oops", "Ой... 😳", "Впервые похудеть"),
            new("kamasutra", "Камасутра 🧘‍♂️❤️", "Ровно 69 т"),
            new("rollercoaster", "Американские горки 🎢", "За неделю: набор, похудение и без изменений"),
            new("monster", "MONSTER GROW 🦖🌱", "Набрать максимальные +20 т"),
            new("year", "Набитый год 🍻🎉", "Вес равен текущему году"),
            new("s100", "Соточка 💯", "100+ т"),
            new("s500", "Полтысячи тонн ⚖️", "500+ т"),
            new("s1000", "Килотонна 🏗️", "1000+ т"),
            new("jackpot", "Джекпот 🎰💎", "Ровно 777 т"),
            new("beast", "Число зверя 😈", "Ровно 666 т"),
            new("eighteen", "Наконец 18! 🎂🔞", "Ровно 18 т"),
            new("s5000", "Мать-Земля 🌍", "5000+ т"),
            new("gymrat", "Качок года 🏋️🏆", "5 раз подряд по +20 т"),
            new("schrodinger", "Свинья Шрёдингера 📦❓", "3 дня подряд: набор, похудение и без изменений"),
            new("streak30", "Работник месяца 👔⭐", "30 дней подряд /grow"),
            new("fridays", "7 пятниц на неделю 🍺📅", "7 дней подряд только набор"),
            new("pendulum", "Маятник ⏰↔️", "+20 т и −20 т за 2 дня подряд"),
            new("groundhog", "День сурка 🦫🔁", "3 дня подряд похудение"),
            new("nohelp", "Старался, не помогло 💀", "3 дня подряд без изменений"),
            new("streak7", "Семь самураев ⚔️7️⃣", "7 дней подряд /grow"),
            new("streak14", "Карантин 🦠📅", "14 дней подряд /grow"),
            new("lucky", "Полоса везения 🍀📈", "5 раз подряд набор"),
            new("infinity", "Война бесконечности ♾️", "Похудеть до 1 т на −20 т"),
            new("genin", "Вечный генин 🥷📉", "7 дней подряд вес не больше 10 т"),
            new("lastyear", "Прошлогодний 📆", "/grow 31 декабря и 1 января"),
            new(PigOfDay, "Свинья дня 🌞", "Стать свиньёй дня"),
            new(Named, "Окрестили 🏷️", "Дать свинье имя"),
            new("midnight", "Тут как тут 👀", "/grow ровно в 00:00"),
            new("agent007", "Агент 007 🕵️‍♂️🍸", "/grow 7 июля в 7 утра"),
            new("newhope", "Новая надежда 🌌✨", "/grow 1-го числа"),
            new("valentine", "Валентинка 💕", "/grow 14 февраля"),
            new("halloween", "Нечисть 🎃👹", "/grow 31 октября"),
            new(TwoChats, "Засветилась 👀🐷", "Свинья в 2 чатах"),
            new(FiveChats, "Посол 🎖️", "Свинья в 5 чатах"),
            new(TenChats, "Эпидемия 🦠", "Свинья в 10 чатах"),
            new("duel_first", "Первая кровь 🩸", "Выиграть первую дуэль"),
            new("duel_10", "Гладиатор ⚔️", "10 побед в дуэлях"),
            new("duel_50", "Чемпион арены 🏟️", "50 побед в дуэлях"),
            new("duel_goliath", "Давид и Голиаф 🪨", "Победить соперника, который минимум вдвое тяжелее"),
            new("duel_knockout", "Нокаутёр 🥊", "Победить нокаутом"),
        ];

        public static Achievement Get(string id) => All.First(a => a.Id == id);

        public static IEnumerable<string> CheckAfterGrow(Player player, DateTime now)
        {
            var history = player.History;
            var last = history[^1];
            int size = player.Size;

            List<GrowEntry>? LastDays(int n) =>
                history.Count >= n && player.Streak >= n ? history.GetRange(history.Count - n, n) : null;

            if (last.Delta < 0) yield return "oops";
            if (size == 69) yield return "kamasutra";
            if (last.Delta == PigGame.MaxDelta) yield return "monster";
            if (size == now.Year) yield return "year";
            if (size >= 100) yield return "s100";
            if (size >= 500) yield return "s500";
            if (size >= 1000) yield return "s1000";
            if (size >= 5000) yield return "s5000";
            if (size == 777) yield return "jackpot";
            if (size == 666) yield return "beast";
            if (size == 18) yield return "eighteen";

            var week = history.Where(e => e.Date > last.Date.AddDays(-7)).ToList();
            if (week.Any(e => e.Delta > 0) && week.Any(e => e.Delta < 0) && week.Any(e => e.Delta == 0))
                yield return "rollercoaster";

            if (history.Count >= 5 && history.TakeLast(5).All(e => e.Delta == PigGame.MaxDelta))
                yield return "gymrat";

            if (LastDays(3) is { } three)
            {
                if (three.Any(e => e.Delta > 0) && three.Any(e => e.Delta < 0) && three.Any(e => e.Delta == 0))
                    yield return "schrodinger";
                if (three.All(e => e.Delta < 0)) yield return "groundhog";
                if (three.All(e => e.Delta == 0)) yield return "nohelp";
            }

            if (LastDays(2) is { } two && two.Any(e => e.Delta == PigGame.MaxDelta) && two.Any(e => e.Delta == -PigGame.MaxDelta))
                yield return "pendulum";

            if (LastDays(7) is { } seven)
            {
                if (seven.All(e => e.Delta > 0)) yield return "fridays";
                if (seven.All(e => e.SizeAfter <= 10)) yield return "genin";
            }

            if (history.Count >= 5 && history.TakeLast(5).All(e => e.Delta > 0)) yield return "lucky";

            if (player.Streak >= 7) yield return "streak7";
            if (player.Streak >= 14) yield return "streak14";
            if (player.Streak >= 30) yield return "streak30";

            if (last.Delta == -PigGame.MaxDelta && size == PigGame.MinSize) yield return "infinity";

            if (last.Date.Month == 1 && last.Date.Day == 1 && history.Count >= 2
                && history[^2].Date == last.Date.AddDays(-1))
                yield return "lastyear";

            if (now.Hour == 0 && now.Minute == 0) yield return "midnight";
            if (now.Month == 7 && now.Day == 7 && now.Hour == 7) yield return "agent007";
            if (now.Day == 1) yield return "newhope";
            if (now.Month == 2 && now.Day == 14) yield return "valentine";
            if (now.Month == 10 && now.Day == 31) yield return "halloween";
        }

        public static IEnumerable<string> CheckAfterDuelWin(Player winner, int winnerSizeBefore, int loserSizeBefore, DuelHit hit)
        {
            if (winner.DuelWins >= 1) yield return "duel_first";
            if (winner.DuelWins >= 10) yield return "duel_10";
            if (winner.DuelWins >= 50) yield return "duel_50";
            if (loserSizeBefore >= winnerSizeBefore * 2) yield return "duel_goliath";
            if (hit == DuelHit.Knockout) yield return "duel_knockout";
        }

        public static IEnumerable<string> CheckChats(int chatCount)
        {
            if (chatCount >= 2) yield return TwoChats;
            if (chatCount >= 5) yield return FiveChats;
            if (chatCount >= 10) yield return TenChats;
        }
    }
}
