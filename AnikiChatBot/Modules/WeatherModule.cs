using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnikiChatBot.Modules.Media;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnikiChatBot.Modules
{
    public class WeatherModule
    {
        private static readonly TimeSpan DeleteDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ForecastCacheLifetime = TimeSpan.FromMinutes(10);

        private static readonly Regex TriggerRegex = new(
            @"^\s*(?:полбот|шма)[\s,!.:]+погода(?:\s+(?:в\s+)?(?<city>.+?))?[\s!?.]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly BotCommand[] Commands =
        [
            new BotCommand { Command = "weather", Description = "Погода: /weather Астана" },
        ];

        public record Place(string Name, string? Country, double Latitude, double Longitude);

        public record Current(double Temperature, double FeelsLike, int Code, double Wind, int Humidity);

        public record Day(DateOnly Date, int Code, double Min, double Max, int? PrecipitationChance);

        public record Forecast(Current Now, List<Day> Days);

        private readonly ConcurrentDictionary<string, Place?> _places = new();
        private readonly ConcurrentDictionary<string, (DateTime At, Forecast Forecast)> _forecasts = new();
        private string _botUsername = "";

        public void SetBotUsername(string? username) => _botUsername = username ?? "";

        public async Task<bool> HandleMessage(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            if (ParseRequest(message.Text, _botUsername) is not { } request)
                return false;

            string reply = request.Length == 0
                ? "Напиши город: /weather Астана или «шма погода Астана»"
                : await BuildReplyAsync(request, ct);

            var sent = await bot.SendMessage(message.Chat.Id, reply, replyParameters: message.MessageId, cancellationToken: ct);
            Cleanup.DeleteLater(bot, message.Chat.Id, DeleteDelay, message.MessageId, sent.MessageId);
            return true;
        }

        public static string? ParseRequest(string? text, string botUsername)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (BotCommands.Parse(text, botUsername) is { Command: "weather" } command)
                return command.Args.Trim();

            var match = TriggerRegex.Match(text);
            return match.Success ? match.Groups["city"].Value.Trim() : null;
        }

        private async Task<string> BuildReplyAsync(string city, CancellationToken ct)
        {
            try
            {
                if (await FindPlaceAsync(city, ct) is not { } place)
                    return $"Не нашёл город «{city}».";

                return BuildText(place, await GetForecastAsync(place, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[Weather] {city}: {ex.Message}");
                return "Не удалось получить погоду, попробуй позже.";
            }
        }

        private async Task<Place?> FindPlaceAsync(string city, CancellationToken ct)
        {
            string key = city.ToLowerInvariant();
            if (_places.TryGetValue(key, out var cached))
                return cached;

            Place? place = null;
            foreach (string candidate in CityCandidates(city))
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=ru&format=json&name=" + Uri.EscapeDataString(candidate);
                place = ParsePlace(await MediaHttp.GetStringAsync(url, ct));
                if (place != null)
                    break;
            }

            _places[key] = place;
            return place;
        }

        public static IEnumerable<string> CityCandidates(string city)
        {
            yield return city;

            if (city.Length > 4 && "еиуюоы".Contains(char.ToLowerInvariant(city[^1])))
                yield return city[..^1];
        }

        private async Task<Forecast> GetForecastAsync(Place place, CancellationToken ct)
        {
            string key = $"{place.Latitude:F2},{place.Longitude:F2}";
            if (_forecasts.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ForecastCacheLifetime)
                return cached.Forecast;

            string url = "https://api.open-meteo.com/v1/forecast?" +
                $"latitude={place.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={place.Longitude.ToString(CultureInfo.InvariantCulture)}" +
                "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                "&timezone=auto&forecast_days=2&wind_speed_unit=ms";

            var forecast = ParseForecast(await MediaHttp.GetStringAsync(url, ct));
            _forecasts[key] = (DateTime.UtcNow, forecast);
            return forecast;
        }

        public static Place? ParsePlace(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("latitude", out var lat) && item.TryGetProperty("longitude", out var lon))
                    return new Place(item.GetStringOrNull("name") ?? "?", item.GetStringOrNull("country"), lat.GetDouble(), lon.GetDouble());
            }

            return null;
        }

        public static Forecast ParseForecast(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var current = root.GetProperty("current");

            var now = new Current(
                current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("apparent_temperature").GetDouble(),
                current.GetProperty("weather_code").GetInt32(),
                current.GetProperty("wind_speed_10m").GetDouble(),
                current.GetInt("relative_humidity_2m") ?? 0);

            var days = new List<Day>();
            if (root.TryGetProperty("daily", out var daily))
            {
                var dates = daily.GetProperty("time").EnumerateArray().ToList();
                var codes = daily.GetProperty("weather_code").EnumerateArray().ToList();
                var max = daily.GetProperty("temperature_2m_max").EnumerateArray().ToList();
                var min = daily.GetProperty("temperature_2m_min").EnumerateArray().ToList();
                var rain = daily.TryGetProperty("precipitation_probability_max", out var r) ? r.EnumerateArray().ToList() : [];

                for (int i = 0; i < dates.Count && i < codes.Count && i < max.Count && i < min.Count; i++)
                {
                    int? chance = i < rain.Count && rain[i].ValueKind == JsonValueKind.Number ? (int)Math.Round(rain[i].GetDouble()) : null;
                    days.Add(new Day(DateOnly.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture),
                        codes[i].GetInt32(), min[i].GetDouble(), max[i].GetDouble(), chance));
                }
            }

            return new Forecast(now, days);
        }

        public static string BuildText(Place place, Forecast forecast)
        {
            var now = forecast.Now;
            var (emoji, description) = Describe(now.Code);
            string where = place.Country != null ? $"{place.Name}, {place.Country}" : place.Name;

            var sb = new StringBuilder();
            sb.AppendLine($"{emoji} Погода: {where}");
            sb.AppendLine($"Сейчас {Temp(now.Temperature)}, ощущается как {Temp(now.FeelsLike)}, {description}");
            sb.AppendLine($"Ветер {Math.Round(now.Wind):0} м/с, влажность {now.Humidity}%");

            string[] labels = ["Сегодня", "Завтра"];
            for (int i = 0; i < forecast.Days.Count && i < labels.Length; i++)
            {
                var day = forecast.Days[i];
                var (dayEmoji, dayDescription) = Describe(day.Code);
                string line = $"{labels[i]}: {Temp(day.Min)}…{Temp(day.Max)}, {dayEmoji} {dayDescription}";
                if (day.PrecipitationChance is > 0 and var chance)
                    line += $", осадки {chance}%";

                if (i == 0)
                    sb.AppendLine();
                sb.AppendLine(line);
            }

            return sb.ToString().TrimEnd();
        }

        public static string Temp(double value)
        {
            int rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            return rounded > 0 ? $"+{rounded}°" : rounded < 0 ? $"−{-rounded}°" : "0°";
        }

        public static (string Emoji, string Text) Describe(int code) => code switch
        {
            0 => ("☀️", "ясно"),
            1 => ("🌤", "малооблачно"),
            2 => ("⛅", "переменная облачность"),
            3 => ("☁️", "пасмурно"),
            45 or 48 => ("🌫", "туман"),
            51 or 53 or 55 => ("🌦", "морось"),
            56 or 57 => ("🌧", "ледяная морось"),
            61 => ("🌧", "небольшой дождь"),
            63 => ("🌧", "дождь"),
            65 => ("🌧", "сильный дождь"),
            66 or 67 => ("🌧", "ледяной дождь"),
            71 => ("🌨", "небольшой снег"),
            73 => ("🌨", "снег"),
            75 => ("❄️", "сильный снег"),
            77 => ("🌨", "снежная крупа"),
            80 or 81 => ("🌦", "ливень"),
            82 => ("⛈", "сильный ливень"),
            85 or 86 => ("🌨", "снегопад"),
            95 => ("⛈", "гроза"),
            96 or 99 => ("⛈", "гроза с градом"),
            _ => ("🌡", "без осадков")
        };
    }
}
