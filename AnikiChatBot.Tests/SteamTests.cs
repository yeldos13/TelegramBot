using AnikiChatBot.Modules;

namespace AnikiChatBot.Tests
{
    public class SteamTests
    {
        [Fact]
        public void Parses_app_ids_from_search()
        {
            string json = """
                {"desc":"","items":[
                  {"name":"Pony Island","logo":"https:\/\/shared.akamai.steamstatic.com\/store_item_assets\/steam\/apps\/405640\/capsule_sm_120.jpg?t=1"},
                  {"name":"Some DLC","logo":"https:\/\/shared.akamai.steamstatic.com\/store_item_assets\/steam\/apps\/2283211\/ecc70fa\/capsule_sm_120.jpg?t=2"},
                  {"name":"No logo"},
                  {"name":"Pony Island again","logo":"https:\/\/x\/steam\/apps\/405640\/capsule.jpg"}
                ]}
                """;

            Assert.Equal([405640L, 2283211L], SteamGiveawaysModule.ParseSearchAppIds(json));
        }

        [Fact]
        public void Search_without_items_gives_nothing()
        {
            Assert.Empty(SteamGiveawaysModule.ParseSearchAppIds("""{"desc":""}"""));
        }

        [Fact]
        public void Paid_game_with_full_discount_is_a_giveaway()
        {
            string json = """
                {"405640":{"success":true,"data":{"type":"game","name":"Pony Island","is_free":true,
                  "header_image":"https://cdn/405640/header.jpg",
                  "price_overview":{"currency":"USD","initial":499,"final":499,"discount_percent":100,"initial_formatted":"$4.99","final_formatted":"Free"}}}}
                """;

            var app = SteamGiveawaysModule.ParseAppDetails(json, 405640);

            Assert.NotNull(app);
            Assert.True(app.IsGiveaway);
            Assert.Equal("Pony Island", app.Name);
            Assert.Equal("$4.99", app.InitialPrice);
            Assert.Equal("https://store.steampowered.com/app/405640/", app.Link);
        }

        [Theory]
        [InlineData("dlc", 100)]
        [InlineData("game", 90)]
        public void Dlc_and_partial_discounts_are_skipped(string type, int discount)
        {
            string json = "{\"1\":{\"success\":true,\"data\":{\"type\":\"" + type + "\",\"name\":\"X\"," +
                "\"price_overview\":{\"discount_percent\":" + discount + ",\"initial_formatted\":\"$1.00\"}}}}";

            Assert.False(SteamGiveawaysModule.ParseAppDetails(json, 1)!.IsGiveaway);
        }

        [Fact]
        public void Free_to_play_game_without_price_is_skipped()
        {
            string json = """{"7":{"success":true,"data":{"type":"game","name":"F2P","is_free":true}}}""";

            Assert.False(SteamGiveawaysModule.ParseAppDetails(json, 7)!.IsGiveaway);
        }

        [Fact]
        public void Failed_details_give_null()
        {
            Assert.Null(SteamGiveawaysModule.ParseAppDetails("""{"5":{"success":false}}""", 5));
            Assert.Null(SteamGiveawaysModule.ParseAppDetails("""{}""", 5));
        }

        [Fact]
        public void Announcement_tags_owner_and_escapes_name()
        {
            var app = new SteamGiveawaysModule.SteamApp(405640, "Pony <Island>", "game", 100, "$4.99", null);

            string text = SteamGiveawaysModule.BuildAnnouncement(app, "@owner");

            Assert.StartsWith("🎁 В Steam бесплатно раздают <b>Pony &lt;Island&gt;</b> (обычно $4.99)", text);
            Assert.Contains("https://store.steampowered.com/app/405640/", text);
            Assert.EndsWith("\n\n@owner", text);
        }

        [Fact]
        public void Announcement_without_owner_has_no_tag()
        {
            var app = new SteamGiveawaysModule.SteamApp(1, "X", "game", 100, null, null);

            Assert.DoesNotContain("@", SteamGiveawaysModule.BuildAnnouncement(app, null));
        }
    }
}
