using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class RandomGifModule
    {
        private static readonly string[] Extensions = [".gif", ".mp4"];

        private readonly string _folder;
        private readonly double _chance;
        private readonly TimeSpan _cooldown;
        private readonly Random _random;
        private readonly ConcurrentDictionary<long, DateTime> _lastSent = new();
        private readonly ConcurrentDictionary<string, string> _fileIds = new();

        public RandomGifModule(string folder, double chance, TimeSpan cooldown, Random? random = null)
        {
            _folder = folder;
            _chance = Math.Clamp(chance, 0, 1);
            _cooldown = cooldown;
            _random = random ?? Random.Shared;

            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { Console.Error.WriteLine($"[Gif] Не удалось создать папку {folder}: {ex.Message}"); }
        }

        public static double ParseChance(string? value, double fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            string text = value.Trim().TrimEnd('%').Replace(',', '.');
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) || number < 0)
                return fallback;

            return value.Contains('%') || number > 1 ? number / 100 : number;
        }

        public List<string> ListGifs()
        {
            try
            {
                return Directory.Exists(_folder)
                    ? Directory.GetFiles(_folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList()
                    : [];
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Gif] Не удалось прочитать папку {_folder}: {ex.Message}");
                return [];
            }
        }

        public static bool IsCandidate(Message message) =>
            message.From is { IsBot: false }
            && !(message.Text ?? message.Caption ?? "").TrimStart().StartsWith('/')
            && !NewcomerLinksModule.HasLink(message);

        public bool ShouldReply(long chatId, DateTime nowUtc)
        {
            if (_chance <= 0 || _lastSent.TryGetValue(chatId, out var last) && nowUtc - last < _cooldown)
                return false;

            return _random.NextDouble() < _chance;
        }

        public async Task HandleMessage(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (!IsCandidate(message) || !ShouldReply(message.Chat.Id, DateTime.UtcNow))
                return;

            var gifs = ListGifs();
            if (gifs.Count == 0)
                return;

            string path = gifs[_random.Next(gifs.Count)];
            _lastSent[message.Chat.Id] = DateTime.UtcNow;

            if (_fileIds.TryGetValue(path, out string? fileId))
            {
                await bot.SendAnimation(message.Chat.Id, InputFile.FromFileId(fileId), replyParameters: message.MessageId, cancellationToken: ct);
                return;
            }

            await using var stream = File.OpenRead(path);
            var sent = await bot.SendAnimation(message.Chat.Id, InputFile.FromStream(stream, Path.GetFileName(path)),
                replyParameters: message.MessageId, cancellationToken: ct);

            if ((sent.Animation?.FileId ?? sent.Document?.FileId ?? sent.Video?.FileId) is { } id)
                _fileIds[path] = id;
        }
    }
}
