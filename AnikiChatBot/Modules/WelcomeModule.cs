using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AnikiChatBot.Modules.Media;
using SkiaSharp;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class WelcomeModule(string videoPath, string ffmpegPath, string fontPath, Func<User, bool>? skip = null)
    {
        private const float FontSize = 26;
        private const float MaxTextWidth = 570;
        private const int TextTop = 290;
        private const int LineHeight = 33;
        private const int TextFromFrame = 36;
        private const int MaxNameLength = 32;
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<(long ChatId, long UserId), DateTime> _recent = new();
        private readonly ChatRateLimit _limit = new(3, TimeSpan.FromMinutes(1));

        public bool Enabled => File.Exists(videoPath) && File.Exists(fontPath) && File.Exists(ffmpegPath);

        public void HandleJoins(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (!Enabled)
                return;

            var now = DateTime.UtcNow;
            foreach (var old in _recent.Where(x => now - x.Value > DuplicateWindow).ToList())
                _recent.TryRemove(old.Key, out _);

            foreach (var (chat, user) in MembersModule.GetJoinedMembers(update))
            {
                if (skip?.Invoke(user) == true || !_recent.TryAdd((chat.Id, user.Id), now) || !_limit.TryTake(chat.Id, now))
                    continue;

                _ = Task.Run(() => SendAsync(bot, chat.Id, user, ct), ct);
            }
        }

        internal (string[] Lines, float Size) BuildText(User user)
        {
            using var typeface = SKTypeface.FromFile(fontPath);
            string name = string.Concat((user.Username ?? Users.DisplayName(user)).EnumerateRunes()
                .Where(r => typeface.ContainsGlyph(r.Value)).Take(MaxNameLength)).Trim();
            if (name.Length == 0)
                name = "newbie";

            using var font = new SKFont(typeface, FontSize);
            string oneLine = $"Welcome to the group! {name}-kun!";
            if (font.MeasureText(oneLine) <= MaxTextWidth)
                return ([oneLine], FontSize);

            string[] lines = ["Welcome to the group!", $"{name}-kun!"];
            float widest = lines.Max(line => font.MeasureText(line));
            return (lines, widest > MaxTextWidth ? FontSize * MaxTextWidth / widest : FontSize);
        }

        private static string FilterPath(string path) => "'" + Path.GetFullPath(path).Replace('\\', '/').Replace(":", "\\:") + "'";

        internal async Task<string?> RenderAsync(User user, string dir, CancellationToken ct)
        {
            Directory.CreateDirectory(dir);
            var (lines, size) = BuildText(user);
            string output = Path.Combine(dir, "welcome.mp4");
            var filters = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string textFile = Path.Combine(dir, $"text{i}.txt");
                await File.WriteAllTextAsync(textFile, lines[i], new UTF8Encoding(false), ct);

                float y = TextTop + (FontSize - size) / 2 - (lines.Length - 1 - i) * LineHeight;
                filters.Add(string.Create(CultureInfo.InvariantCulture,
                    $"drawtext=fontfile={FilterPath(fontPath)}:textfile={FilterPath(textFile)}:expansion=none:fontsize={size:0.#}:fontcolor=white:borderw=2:bordercolor=black:x=(w-tw)/2:y={y:0}:enable='gte(n,{TextFromFrame})'"));
            }

            string drawtext = string.Join(",", filters);

            var result = await ProcessRunner.RunAsync(ffmpegPath,
                ["-y", "-v", "error", "-i", videoPath, "-vf", drawtext, "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                 "-pix_fmt", "yuv420p", "-movflags", "+faststart", output], ct);

            if (result.ExitCode == 0 && File.Exists(output))
                return output;

            Console.Error.WriteLine($"[Welcome] ffmpeg: {ProcessRunner.LastLine(result.StdErr)}");
            return null;
        }

        private async Task SendAsync(ITelegramBotClient bot, long chatId, User user, CancellationToken ct)
        {
            string dir = Path.Combine(Path.GetTempPath(), "AnikiChatBot", Guid.NewGuid().ToString("N"));
            try
            {
                if (await RenderAsync(user, dir, ct) is not { } output)
                    return;

                await using var stream = File.OpenRead(output);
                await bot.SendAnimation(chatId, InputFile.FromStream(stream, "welcome.mp4"), cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Welcome] {ex.Message}");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
