using AnikiChatBot.Modules;
using Telegram.Bot.Types;

namespace AnikiChatBot.Tests
{
    public class CaptchaBackupTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "aniki_cb_" + Guid.NewGuid().ToString("N"));

        public CaptchaBackupTests() => Directory.CreateDirectory(_dir);

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        [Fact]
        public void Captcha_is_given_once_and_expires_after_timeout()
        {
            string path = Path.Combine(_dir, "captcha.json");
            var captcha = new CaptchaModule(null, (_, _) => false, path);
            var user = new User { Id = 5, FirstName = "Вася" };
            var now = DateTime.UtcNow;

            Assert.True(captcha.TryReserve(1, user, now));
            Assert.False(captcha.TryReserve(1, user, now));
            Assert.True(captcha.TryReserve(2, user, now));

            Assert.Empty(captcha.TakeExpired(now + CaptchaModule.Timeout - TimeSpan.FromSeconds(1)));
            Assert.NotNull(captcha.Take(2, 5));

            var restarted = new CaptchaModule(null, (_, _) => false, path);
            var expired = restarted.TakeExpired(now + CaptchaModule.Timeout);
            Assert.Equal([(1L, 5L)], expired.Select(p => (p.ChatId, p.UserId)));
            Assert.Null(restarted.Take(1, 5));
        }

        [Fact]
        public void Backup_copies_data_files_once_a_day_and_keeps_a_week()
        {
            string data = Path.Combine(_dir, "data");
            string backups = Path.Combine(data, "backups");
            Directory.CreateDirectory(data);
            foreach (string name in new[] { "replies.txt", "stats.json", "appsettings.json", "AnikiChatBot.deps.json", "replies.limit.json", "replies.backup-1.txt", "bot.dll" })
                System.IO.File.WriteAllText(Path.Combine(data, name), name);

            var day = new DateTime(2026, 11, 1);
            Assert.NotNull(Backup.Run(data, backups, day));
            Assert.Null(Backup.Run(data, backups, day));
            Assert.Equal(["replies.txt", "stats.json"],
                Directory.GetFiles(Path.Combine(backups, "2026-11-01")).Select(Path.GetFileName).Order());

            for (int i = 1; i <= 8; i++)
                Backup.Run(data, backups, day.AddDays(i));

            var kept = Directory.GetDirectories(backups).Select(Path.GetFileName).Order().ToList();
            Assert.Equal(Backup.KeepDays, kept.Count);
            Assert.Equal("2026-11-03", kept[0]);
        }
    }
}
