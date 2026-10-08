using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnikiChatBot.Modules.Media;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace AnikiChatBot.Modules
{
    public class SteamGiveawaysModule
    {
        private const string SearchUrl =
            "https://store.steampowered.com/search/results/?force_infinite=1&maxprice=free&specials=1&json=1";
        private const string DetailsUrl = "https://store.steampowered.com/api/appdetails?cc=us&l=russian&appids=";
        private const string SeenFile = "steam_seen.json";
        private static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(30);

        private static readonly Regex AppIdRegex = new(@"/apps/(\d+)/", RegexOptions.Compiled);

        public record SteamApp(long Id, string Name, string Type, int DiscountPercent, string? InitialPrice, string? HeaderImage)
        {
            public string Link => $"https://store.steampowered.com/app/{Id}/";
            public bool IsGiveaway => Type == "game" && DiscountPercent == 100;
        }

        private readonly Dictionary<long, DateTime> _seen;
        private readonly string _seenFile;

        public SteamGiveawaysModule(string seenFile = SeenFile)
        {
            _seenFile = seenFile;
            _seen = JsonFile.Load<Dictionary<long, DateTime>>(seenFile, "Steam") ?? new();
        }

        public async Task<List<SteamApp>> FindNewGiveawaysAsync(CancellationToken ct)
        {
            string search = await MediaHttp.GetStringAsync(SearchUrl, ct);
            var ids = ParseSearchAppIds(search);
            var now = DateTime.UtcNow;
            var found = new List<SteamApp>();

            foreach (long id in ids)
            {
                if (_seen.ContainsKey(id))
                {
                    _seen[id] = now;
                    continue;
                }

                string details = await MediaHttp.GetStringAsync(DetailsUrl + id, ct);
                if (ParseAppDetails(details, id) is not { } app)
                    continue;

                _seen[id] = now;
                if (app.IsGiveaway)
                    found.Add(app);
            }

            foreach (long old in _seen.Where(p => now - p.Value > ForgetAfter).Select(p => p.Key).ToList())
                _seen.Remove(old);

            JsonFile.Save(_seenFile, _seen, "Steam");
            return found;
        }

        public async Task AnnounceAsync(ITelegramBotClient bot, IEnumerable<long> chatIds, SteamApp app, string? ownerUsername, CancellationToken ct)
        {
            string caption = BuildAnnouncement(app, ownerUsername);

            foreach (long chatId in chatIds)
            {
                try
                {
                    if (app.HeaderImage != null)
                        await bot.SendPhoto(chatId, app.HeaderImage, caption: caption, parseMode: ParseMode.Html, cancellationToken: ct);
                    else
                        await bot.SendMessage(chatId, caption, parseMode: ParseMode.Html, cancellationToken: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[Steam] Не удалось отправить раздачу {app.Name}: {ex.Message}");
                    await bot.SendMessage(chatId, caption, parseMode: ParseMode.Html, cancellationToken: ct);
                }
            }
        }

        public static List<long> ParseSearchAppIds(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            return items.EnumerateArray()
                .Select(item => item.GetStringOrNull("logo"))
                .Select(logo => logo == null ? null : AppIdRegex.Match(logo))
                .Where(m => m is { Success: true })
                .Select(m => long.Parse(m!.Groups[1].Value))
                .Distinct()
                .ToList();
        }

        public static SteamApp? ParseAppDetails(string json, long id)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(id.ToString(), out var entry)
                || entry.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
                || !entry.TryGetProperty("data", out var data))
                return null;

            int discount = 0;
            string? initial = null;
            if (data.TryGetProperty("price_overview", out var price) && price.ValueKind == JsonValueKind.Object)
            {
                discount = price.GetInt("discount_percent") ?? 0;
                initial = price.GetStringOrNull("initial_formatted");
            }

            return new SteamApp(id, data.GetStringOrNull("name") ?? $"App {id}", data.GetStringOrNull("type") ?? "",
                discount, string.IsNullOrEmpty(initial) ? null : initial, data.GetStringOrNull("header_image"));
        }

        public static string BuildAnnouncement(SteamApp app, string? ownerUsername)
        {
            string text = $"🎁 В Steam бесплатно раздают <b>{WebUtility.HtmlEncode(app.Name)}</b>";
            if (app.InitialPrice != null)
                text += $" (обычно {WebUtility.HtmlEncode(app.InitialPrice)})";

            text += $"\nЗабрать: <a href=\"{app.Link}\">{app.Link}</a>\nПосле получения игра остаётся в библиотеке навсегда.";

            string? tag = ownerUsername?.TrimStart('@');
            if (!string.IsNullOrEmpty(tag))
                text += $"\n\n@{tag}";

            return text;
        }
    }
}
