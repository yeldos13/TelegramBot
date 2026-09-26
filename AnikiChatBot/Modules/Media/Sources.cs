using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace AnikiChatBot.Modules.Media
{
    public class YouTubeSource : IMediaSource
    {
        private static readonly Regex ShortsRegex = new(@"^/shorts/([A-Za-z0-9_-]{11})", RegexOptions.Compiled);
        private static readonly Regex PostRegex = new(@"^/post/([A-Za-z0-9_-]+)", RegexOptions.Compiled);
        private static readonly Regex InitialDataRegex = new(
            @"var ytInitialData\s*=\s*(\{.+?\});\s*</script>", RegexOptions.Compiled | RegexOptions.Singleline);

        private readonly YtDlp _ytDlp;

        public YouTubeSource(YtDlp ytDlp) => _ytDlp = ytDlp;

        public bool CanHandle(Uri uri)
        {
            return MediaHttp.IsHost(uri, "youtube.com")
                && (ShortsRegex.IsMatch(uri.AbsolutePath) || GetPostId(uri) != null);
        }

        public async Task<List<MediaItem>> ExtractAsync(Uri uri, string workDir, CancellationToken ct)
        {
            var shorts = ShortsRegex.Match(uri.AbsolutePath);
            if (shorts.Success)
                return await _ytDlp.ExtractAsync($"https://www.youtube.com/shorts/{shorts.Groups[1].Value}", workDir, ct);

            string postId = GetPostId(uri)!;
            string html = await MediaHttp.GetStringAsync($"https://www.youtube.com/post/{postId}", ct, cookie: "SOCS=CAI");

            var match = InitialDataRegex.Match(html);
            if (!match.Success)
                return [];

            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var post = MediaHttp.FindAll(doc.RootElement, "backstagePostRenderer").FirstOrDefault();
            if (post.ValueKind != JsonValueKind.Object)
                return [];

            var items = new List<MediaItem>();
            foreach (var image in MediaHttp.FindAll(post, "backstageImageRenderer"))
            {
                if (!image.TryGetProperty("image", out var img) || !img.TryGetProperty("thumbnails", out var thumbnails))
                    continue;

                string? url = thumbnails.EnumerateArray().LastOrDefault().GetStringOrNull("url");
                if (url == null)
                    continue;

                items.Add(new MediaItem { Kind = MediaKind.Photo, Url = ToFullSize(url) });
            }

            return items;
        }

        private static string? GetPostId(Uri uri)
        {
            var post = PostRegex.Match(uri.AbsolutePath);
            if (post.Success)
                return post.Groups[1].Value;

            return HttpUtility.ParseQueryString(uri.Query)["lb"];
        }

        internal static string ToFullSize(string url)
        {
            if (url.StartsWith("//"))
                url = "https:" + url;

            int slash = url.LastIndexOf('/');
            int eq = url.IndexOf('=', slash + 1);
            return (eq > 0 ? url[..eq] : url) + "=s2048-rj";
        }
    }

    public class InstagramSource : IMediaSource
    {
        private static readonly Regex ShortcodeRegex = new(@"/(?:p|reels?|tv)/([A-Za-z0-9_-]+)", RegexOptions.Compiled);

        private readonly YtDlp _ytDlp;

        public InstagramSource(YtDlp ytDlp) => _ytDlp = ytDlp;

        public bool CanHandle(Uri uri)
        {
            return MediaHttp.IsHost(uri, "instagram.com")
                && (ShortcodeRegex.IsMatch(uri.AbsolutePath) || uri.AbsolutePath.StartsWith("/share/"));
        }

        public async Task<List<MediaItem>> ExtractAsync(Uri uri, string workDir, CancellationToken ct)
        {
            if (uri.AbsolutePath.StartsWith("/share/"))
                uri = await MediaHttp.ResolveRedirectsAsync(uri, ct);

            var match = ShortcodeRegex.Match(uri.AbsolutePath);
            if (!match.Success)
                return [];

            return await _ytDlp.ExtractAsync($"https://www.instagram.com/p/{match.Groups[1].Value}/", workDir, ct);
        }
    }

    public class TikTokSource : IMediaSource
    {
        private static readonly Regex IdRegex = new(@"/(?:video|photo)/(\d+)", RegexOptions.Compiled);
        private static readonly Regex DataRegex = new(
            @"<script id=""__UNIVERSAL_DATA_FOR_REHYDRATION__"" type=""application/json"">(.+?)</script>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private readonly YtDlp _ytDlp;

        public TikTokSource(YtDlp ytDlp) => _ytDlp = ytDlp;

        public bool CanHandle(Uri uri) => MediaHttp.IsHost(uri, "tiktok.com");

        public async Task<List<MediaItem>> ExtractAsync(Uri uri, string workDir, CancellationToken ct)
        {
            if (!IdRegex.IsMatch(uri.AbsolutePath))
                uri = await MediaHttp.ResolveRedirectsAsync(uri, ct);

            var idMatch = IdRegex.Match(uri.AbsolutePath);
            if (!idMatch.Success)
                return [];

            string id = idMatch.Groups[1].Value;
            string author = "_";

            try
            {
                string pageUrl = $"https://www.tiktok.com/@_/video/{id}";
                string html = await MediaHttp.GetStringAsync(pageUrl, ct);
                var match = DataRegex.Match(html);

                if (!match.Success)
                    match = DataRegex.Match(await _ytDlp.DumpPageAsync(pageUrl, ct));

                if (match.Success)
                {
                    using var doc = JsonDocument.Parse(match.Groups[1].Value);
                    if (doc.RootElement.TryGetProperty("__DEFAULT_SCOPE__", out var scope)
                        && scope.TryGetProperty("webapp.video-detail", out var detail)
                        && detail.TryGetProperty("itemInfo", out var itemInfo)
                        && itemInfo.TryGetProperty("itemStruct", out var item))
                    {
                        if (item.TryGetProperty("author", out var authorProp))
                            author = authorProp.GetStringOrNull("uniqueId") ?? author;

                        var photos = GetPhotos(item);
                        if (photos.Count > 0)
                            return photos;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[TikTok] Не удалось разобрать страницу {id}: {ex.Message}");
            }

            return await _ytDlp.ExtractAsync($"https://www.tiktok.com/@{author}/video/{id}", workDir, ct);
        }

        private static List<MediaItem> GetPhotos(JsonElement item)
        {
            var result = new List<MediaItem>();

            if (!item.TryGetProperty("imagePost", out var imagePost)
                || !imagePost.TryGetProperty("images", out var images)
                || images.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var image in images.EnumerateArray().Take(YtDlp.MaxItems))
            {
                if (!image.TryGetProperty("imageURL", out var imageUrl)
                    || !imageUrl.TryGetProperty("urlList", out var urlList)
                    || urlList.ValueKind != JsonValueKind.Array)
                    continue;

                string? url = urlList.EnumerateArray().Select(u => u.GetString()).FirstOrDefault(u => !string.IsNullOrEmpty(u));
                if (url != null)
                    result.Add(new MediaItem { Kind = MediaKind.Photo, Url = url, Width = image.GetInt("imageWidth"), Height = image.GetInt("imageHeight") });
            }

            return result;
        }
    }

    public class XSource : IMediaSource
    {
        private static readonly Regex StatusRegex = new(@"/status(?:es)?/(\d+)", RegexOptions.Compiled);

        public bool CanHandle(Uri uri)
        {
            return MediaHttp.IsHost(uri, "x.com", "twitter.com", "fxtwitter.com", "vxtwitter.com", "fixupx.com", "fixvx.com")
                && StatusRegex.IsMatch(uri.AbsolutePath);
        }

        public async Task<List<MediaItem>> ExtractAsync(Uri uri, string workDir, CancellationToken ct)
        {
            string id = StatusRegex.Match(uri.AbsolutePath).Groups[1].Value;

            using var doc = await MediaHttp.GetJsonAsync($"https://api.fxtwitter.com/status/{id}", ct);
            if (!doc.RootElement.TryGetProperty("tweet", out var tweet)
                || !tweet.TryGetProperty("media", out var media)
                || !media.TryGetProperty("all", out var all)
                || all.ValueKind != JsonValueKind.Array)
                return [];

            var items = new List<MediaItem>();
            int index = 0;

            foreach (var entry in all.EnumerateArray().Take(YtDlp.MaxItems))
            {
                index++;
                string? type = entry.GetStringOrNull("type");
                string? url = entry.GetStringOrNull("url");
                if (url == null)
                    continue;

                if (type == "photo")
                {
                    items.Add(new MediaItem { Kind = MediaKind.Photo, Url = url, Index = index });
                    continue;
                }

                string path = Path.Combine(workDir, $"x_{index}.mp4");
                var candidates = GetVideoCandidates(entry, url).ToList();

                bool downloaded = false;
                foreach (string candidate in candidates)
                {
                    if (await MediaHttp.DownloadAsync(candidate, path, MediaModule.MaxVideoBytes, ct))
                    {
                        downloaded = true;
                        break;
                    }
                }

                if (!downloaded)
                    downloaded = await MediaHttp.DownloadAsync(candidates[^1], path, MediaModule.MaxDownloadBytes, ct);

                if (downloaded)
                {
                    items.Add(new MediaItem
                    {
                        Kind = type == "gif" ? MediaKind.Animation : MediaKind.Video,
                        FilePath = path,
                        Index = index,
                        Width = entry.GetInt("width"),
                        Height = entry.GetInt("height"),
                        Duration = entry.GetInt("duration")
                    });
                }
            }

            return items;
        }

        private static IEnumerable<string> GetVideoCandidates(JsonElement entry, string bestUrl)
        {
            var candidates = new List<string> { bestUrl };

            if (entry.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                candidates.AddRange(formats.EnumerateArray()
                    .Where(f => f.GetStringOrNull("container") == "mp4" && f.GetStringOrNull("url") != null)
                    .OrderByDescending(f => f.GetInt("bitrate") ?? 0)
                    .Select(f => f.GetStringOrNull("url")!));
            }

            return candidates.Distinct();
        }
    }
}
