using AnikiChatBot.Modules;
using AnikiChatBot.Modules.Media;

namespace AnikiChatBot.Tests
{
    public class MediaLinkTests
    {
        private readonly MediaModule _module = new MediaModule("yt-dlp.exe", "ffmpeg.exe", null);

        [Theory]
        [InlineData("https://www.youtube.com/shorts/Mr8EzFVaWA4", typeof(YouTubeSource))]
        [InlineData("https://youtube.com/shorts/Mr8EzFVaWA4?si=abc", typeof(YouTubeSource))]
        [InlineData("https://m.youtube.com/shorts/Mr8EzFVaWA4", typeof(YouTubeSource))]
        [InlineData("https://www.youtube.com/post/Ugkx5zjY-EF1Nnx3mEo2eYqtI74IFq7JBHzA", typeof(YouTubeSource))]
        [InlineData("https://www.youtube.com/channel/UCBR8-60-B28hp2BmDPdntcQ/community?lb=Ugkx5zjY", typeof(YouTubeSource))]
        [InlineData("https://www.instagram.com/reel/Chunk8-jurw/", typeof(InstagramSource))]
        [InlineData("https://www.instagram.com/reels/Chunk8-jurw/", typeof(InstagramSource))]
        [InlineData("https://www.instagram.com/p/BQ0eAlwhDrw/?img_index=1", typeof(InstagramSource))]
        [InlineData("https://www.instagram.com/someuser/p/BQ0eAlwhDrw/", typeof(InstagramSource))]
        [InlineData("https://www.instagram.com/share/abc123", typeof(InstagramSource))]
        [InlineData("https://www.tiktok.com/@user/video/6984138651336838402", typeof(TikTokSource))]
        [InlineData("https://www.tiktok.com/@user/photo/7240568259186019630", typeof(TikTokSource))]
        [InlineData("https://vm.tiktok.com/ZMabcdef/", typeof(TikTokSource))]
        [InlineData("https://x.com/user/status/1790637656616943991", typeof(XSource))]
        [InlineData("https://twitter.com/user/status/1790637656616943991?s=20", typeof(XSource))]
        [InlineData("https://mobile.twitter.com/user/status/1790637656616943991", typeof(XSource))]
        [InlineData("https://fxtwitter.com/user/status/1790637656616943991", typeof(XSource))]
        public void Routes_link_to_source(string url, Type expected)
        {
            var source = _module.FindSource(new Uri(url));
            Assert.NotNull(source);
            Assert.IsType(expected, source);
        }

        [Theory]
        [InlineData("https://box.com/status/123")]
        [InlineData("https://notx.com/user/status/123")]
        [InlineData("https://www.youtube.com/watch?v=Mr8EzFVaWA4")]
        [InlineData("https://www.youtube.com/@YouTube")]
        [InlineData("https://www.instagram.com/someuser/")]
        [InlineData("https://x.com/user")]
        [InlineData("https://example.com/p/abc")]
        public void Ignores_unsupported_links(string url)
        {
            Assert.Null(_module.FindSource(new Uri(url)));
        }

        [Fact]
        public void Extracts_links_and_trims_punctuation()
        {
            var links = MediaModule.ExtractLinks("глянь (https://x.com/a/status/1). И ещё https://www.tiktok.com/@a/video/2, круто!")
                .Select(u => u.ToString())
                .ToList();

            Assert.Equal(["https://x.com/a/status/1", "https://www.tiktok.com/@a/video/2"], links);
        }

        [Fact]
        public void Takes_at_most_three_distinct_links()
        {
            string text = "https://a.com/1 https://a.com/1 https://a.com/2 https://a.com/3 https://a.com/4";
            Assert.Equal(3, MediaModule.ExtractLinks(text).Count());
        }

        [Theory]
        [InlineData(
            "https://yt3.ggpht.com/ADvaP1irX6wM8AY3=s288-c-fcrop64=1,00000000ffffffff-rw-nd-v1",
            "https://yt3.ggpht.com/ADvaP1irX6wM8AY3=s2048-rj")]
        [InlineData(
            "//yt3.ggpht.com/svVdB6UKoHiZ=s288-nd-v1-rwa",
            "https://yt3.ggpht.com/svVdB6UKoHiZ=s2048-rj")]
        public void YouTube_post_image_is_requested_in_full_size_jpeg(string thumbnail, string expected)
        {
            Assert.Equal(expected, YouTubeSource.ToFullSize(thumbnail));
        }

        [Fact]
        public void Compression_bitrate_fits_target_size()
        {
            long target = 48L * 1024 * 1024;

            int kbps = FFmpeg.CalculateVideoKbps(target, 600);
            Assert.InRange(kbps, 450, 550);

            double bytes = (kbps + 96) * 1000 / 8.0 * 600;
            Assert.True(bytes < target);
        }
    }
}
