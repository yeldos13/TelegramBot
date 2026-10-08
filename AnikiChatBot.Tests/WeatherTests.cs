using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class WeatherTests
    {
        [Theory]
        [InlineData("шма погода астана", "астана")]
        [InlineData("Шма погода Астана", "Астана")]
        [InlineData("полбот погода алматы", "алматы")]
        [InlineData("Полбот, погода Нур-Султан?", "Нур-Султан")]
        [InlineData("шма погода в москве", "москве")]
        [InlineData("полбот погода санкт петербург", "санкт петербург")]
        [InlineData("/weather Астана", "Астана")]
        [InlineData("/weather@AnikiChatBot Караганда", "Караганда")]
        [InlineData("шма погода", "")]
        [InlineData("/weather", "")]
        public void Parses_weather_requests(string text, string city)
        {
            Assert.Equal(city, WeatherModule.ParseRequest(text, "AnikiChatBot"));
        }

        [Theory]
        [InlineData("погода астана")]
        [InlineData("шма какая погода")]
        [InlineData("кто сказал шма погода астана")]
        [InlineData("шмапогода астана")]
        [InlineData("/weather@OtherBot Астана")]
        [InlineData(null)]
        public void Ignores_other_messages(string? text)
        {
            Assert.Null(WeatherModule.ParseRequest(text, "AnikiChatBot"));
        }

        [Theory]
        [InlineData("Москве", new[] { "Москве", "Москв" })]
        [InlineData("Казани", new[] { "Казани", "Казан" })]
        [InlineData("Астана", new[] { "Астана" })]
        [InlineData("Омск", new[] { "Омск" })]
        [InlineData("Уфе", new[] { "Уфе" })]
        public void Tries_city_without_case_ending(string city, string[] expected)
        {
            Assert.Equal(expected, WeatherModule.CityCandidates(city));
        }

        [Fact]
        public void Parses_geocoding_response()
        {
            var place = WeatherModule.ParsePlace(
                """{"results":[{"id":1526273,"name":"Астана","latitude":51.1801,"longitude":71.44598,"country":"Казахстан"}]}""");

            Assert.Equal(new WeatherModule.Place("Астана", "Казахстан", 51.1801, 71.44598), place);
            Assert.Null(WeatherModule.ParsePlace("""{"generationtime_ms":0.5}"""));
        }

        private const string ForecastJson = """
            {"current":{"time":"2026-10-09T00:00","temperature_2m":7.0,"apparent_temperature":3.4,"weather_code":0,"wind_speed_10m":4.41,"relative_humidity_2m":84},
             "daily":{"time":["2026-10-09","2026-10-10"],"weather_code":[1,3],"temperature_2m_max":[11.0,-0.4],"temperature_2m_min":[4.0,-5.6],"precipitation_probability_max":[23,0]}}
            """;

        [Fact]
        public void Parses_forecast()
        {
            var forecast = WeatherModule.ParseForecast(ForecastJson);

            Assert.Equal(new WeatherModule.Current(7.0, 3.4, 0, 4.41, 84), forecast.Now);
            Assert.Equal(2, forecast.Days.Count);
            Assert.Equal(new WeatherModule.Day(new DateOnly(2026, 10, 10), 3, -5.6, -0.4, 0), forecast.Days[1]);
        }

        [Fact]
        public void Builds_weather_text()
        {
            var place = new WeatherModule.Place("Астана", "Казахстан", 51.18, 71.45);

            string text = WeatherModule.BuildText(place, WeatherModule.ParseForecast(ForecastJson)).Replace("\r\n", "\n");

            Assert.Equal(
                "☀️ Погода: Астана, Казахстан\n" +
                "Сейчас +7°, ощущается как +3°, ясно\n" +
                "Ветер 4 м/с, влажность 84%\n" +
                "\n" +
                "Сегодня: +4°…+11°, 🌤 малооблачно, осадки 23%\n" +
                "Завтра: −6°…0°, ☁️ пасмурно",
                text);
        }

        [Theory]
        [InlineData(0.4, "0°")]
        [InlineData(-0.4, "0°")]
        [InlineData(2.5, "+3°")]
        [InlineData(-2.5, "−3°")]
        public void Formats_temperature(double value, string expected)
        {
            Assert.Equal(expected, WeatherModule.Temp(value));
        }
    }
}
