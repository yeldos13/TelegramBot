using System.Text.RegularExpressions;

namespace AnikiChatBot.Modules.Media
{
    public class YtDlpSource(YtDlp ytDlp, string[] hosts, string pathPattern) : IMediaSource
    {
        private readonly Regex _path = new(pathPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public bool CanHandle(Uri uri) => MediaHttp.IsHost(uri, hosts) && _path.IsMatch(uri.PathAndQuery);

        public Task<List<MediaItem>> ExtractAsync(Uri uri, string workDir, CancellationToken ct) =>
            ytDlp.ExtractAsync(uri.ToString(), workDir, ct);

        public static YtDlpSource[] Others(YtDlp ytDlp) =>
        [
            new(ytDlp, ["reddit.com"], @"/comments/\w+|^/r/\w+/s/\w+"),
            new(ytDlp, ["redd.it"], @"^/\w+"),
            new(ytDlp, ["vk.com", "vk.ru", "vkvideo.ru"], @"(video|clip)-?\d+_\d+"),
            new(ytDlp, ["clips.twitch.tv"], @"^/[\w-]+"),
            new(ytDlp, ["twitch.tv"], @"/clip/[\w-]+"),
            new(ytDlp, ["pinterest.com", "pinterest.ru"], @"^/pin/[\w-]+"),
            new(ytDlp, ["pin.it"], @"^/\w+"),
        ];
    }
}
