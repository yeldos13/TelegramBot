namespace AnikiChatBot.Modules.Penis
{
    public enum DuelHit { Normal, Critical, Knockout }

    public record DuelResult(bool ChallengerWins, int Transfer, DuelHit Hit);

    public static class PenisGame
    {
        public const int MinSize = 1;
        public const int MaxDelta = 20;
        public const int MaxHistory = 30;

        public static int RollInitialSize(Random random) => random.Next(1, 21);

        public static int RollDelta(Random random)
        {
            int roll = random.Next(100);
            if (roll < 70) return random.Next(1, MaxDelta + 1);
            if (roll < 90) return -random.Next(1, MaxDelta + 1);
            return 0;
        }

        public static bool CanGrow(Player player, DateOnly today) => player.LastGrow != today;

        public static TimeSpan TimeUntilNextGrow(DateTime now) => now.Date.AddDays(1) - now;

        public static GrowEntry ApplyGrow(Player player, DateOnly today, int delta)
        {
            player.Streak = player.LastGrow == today.AddDays(-1) ? player.Streak + 1 : 1;
            player.LastGrow = today;
            player.Size = Math.Max(MinSize, player.Size + delta);

            var entry = new GrowEntry { Date = today, Delta = delta, SizeAfter = player.Size };
            player.History.Add(entry);

            if (player.History.Count > MaxHistory)
                player.History.RemoveRange(0, player.History.Count - MaxHistory);

            return entry;
        }

        public static DuelResult ResolveDuel(int challengerSize, int opponentSize, Random random)
        {
            double share = (double)challengerSize / Math.Max(1, challengerSize + opponentSize);
            bool challengerWins = random.NextDouble() < 0.25 + 0.5 * share;

            int hitRoll = random.Next(100);
            DuelHit hit = hitRoll < 70 ? DuelHit.Normal : hitRoll < 90 ? DuelHit.Critical : DuelHit.Knockout;
            int percent = hit switch
            {
                DuelHit.Normal => random.Next(10, 26),
                DuelHit.Critical => random.Next(26, 41),
                _ => 50
            };

            int loserSize = challengerWins ? opponentSize : challengerSize;
            int transfer = Math.Max(1, loserSize * percent / 100);

            transfer = Math.Min(transfer, loserSize - MinSize);

            return new DuelResult(challengerWins, Math.Max(0, transfer), hit);
        }
    }
}
