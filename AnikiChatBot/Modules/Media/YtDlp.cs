using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AnikiChatBot.Modules.Media
{
    public class YtDlp
    {
        private const string VideoFormat = "bv*+ba/b";
        private const string VideoSort = "vcodec:h264,res:720,acodec:aac,size:48M";

        public const int MaxItems = 30;

        private readonly string _exePath;
        private readonly string _ffmpegPath;
        private readonly string? _cookiesFile;

        public YtDlp(string exePath, string ffmpegPath, string? cookiesFile)
        {
            _exePath = exePath;
            _ffmpegPath = ffmpegPath;
            _cookiesFile = cookiesFile;
        }

        public async Task<List<MediaItem>> ExtractAsync(string url, string workDir, CancellationToken ct)
        {
            var info = await RunAsync(["-J", "--ignore-no-formats-error", url], ct);
            if (info.ExitCode != 0 || string.IsNullOrWhiteSpace(info.StdOut))
            {
                Console.Error.WriteLine($"[yt-dlp] {url}: {LastLine(info.StdErr)}");
                return [];
            }

            string infoPath = Path.Combine(workDir, "info.json");
            await File.WriteAllTextAsync(infoPath, info.StdOut, ct);

            using var doc = JsonDocument.Parse(info.StdOut);
            var root = doc.RootElement;

            var entries = root.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Array
                ? entriesProp.EnumerateArray().ToList()
                : [root];

            var items = new List<MediaItem>();
            for (int i = 0; i < entries.Count && i < MaxItems; i++)
            {
                var entry = entries[i];
                if (HasVideo(entry))
                {
                    items.Add(new MediaItem
                    {
                        Kind = MediaKind.Video,
                        Index = i + 1,
                        Width = entry.GetInt("width"),
                        Height = entry.GetInt("height"),
                        Duration = entry.GetInt("duration")
                    });
                }
                else if (GetBestThumbnail(entry) is { } imageUrl)
                {
                    items.Add(new MediaItem { Kind = MediaKind.Photo, Index = i + 1, Url = imageUrl });
                }
            }

            if (items.Any(x => x.Kind == MediaKind.Video))
            {
                var download = await RunAsync(
                [
                    "--load-info-json", infoPath,
                    "-f", VideoFormat,
                    "-S", VideoSort,
                    "--merge-output-format", "mp4",
                    "--ignore-errors",
                    "--ignore-no-formats-error",
                    "-o", Path.Combine(workDir, "%(playlist_index|1)s.%(ext)s")
                ], ct);

                foreach (var item in items.Where(x => x.Kind == MediaKind.Video))
                {
                    item.FilePath = Directory.GetFiles(workDir, $"{item.Index}.*")
                        .FirstOrDefault(f => f.EndsWith(".mp4") || f.EndsWith(".webm") || f.EndsWith(".mkv") || f.EndsWith(".mov"));
                }

                if (items.Any(x => x.Kind == MediaKind.Video && x.FilePath == null))
                    Console.Error.WriteLine($"[yt-dlp] {url}: не все видео скачались. {LastLine(download.StdErr)}");
            }

            return items.Where(x => x.Kind == MediaKind.Photo || x.FilePath != null).ToList();
        }

        public async Task<string> DumpPageAsync(string url, CancellationToken ct)
        {
            var result = await RunAsync(["--dump-pages", "--simulate", "--ignore-no-formats-error", "--no-playlist", url], ct);

            var pages = new StringBuilder();
            foreach (string line in result.StdOut.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('['))
                    continue;

                try
                {
                    pages.AppendLine(Encoding.UTF8.GetString(Convert.FromBase64String(trimmed)));
                }
                catch (FormatException)
                {
                }
            }

            return pages.ToString();
        }

        public async Task UpdateAsync(CancellationToken ct)
        {
            try
            {
                var result = await RunAsync(["-U"], ct, addCommonArgs: false);
                Console.WriteLine($"[yt-dlp] {LastLine(result.StdOut)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[yt-dlp] Не удалось обновить: {ex.Message}");
            }
        }

        private static bool HasVideo(JsonElement entry)
        {
            if (!entry.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
                return false;

            return formats.EnumerateArray().Any(f => f.GetStringOrNull("vcodec") != "none");
        }

        private static string? GetBestThumbnail(JsonElement entry)
        {
            if (!entry.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
                return entry.GetStringOrNull("thumbnail");

            var list = thumbnails.EnumerateArray().Where(t => t.GetStringOrNull("url") != null).ToList();
            if (list.Count == 0)
                return null;

            var best = list.Any(t => t.GetInt("width") != null)
                ? list.MaxBy(t => (t.GetInt("width") ?? 0) * (t.GetInt("height") ?? 0))
                : list[^1];

            return best.GetStringOrNull("url");
        }

        private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
            IEnumerable<string> args, CancellationToken ct, bool addCommonArgs = true)
        {
            var psi = new ProcessStartInfo(_exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            if (addCommonArgs)
            {
                psi.ArgumentList.Add("--no-update");
                psi.ArgumentList.Add("--no-progress");
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(_ffmpegPath);

                if (!string.IsNullOrEmpty(_cookiesFile) && File.Exists(_cookiesFile))
                {
                    psi.ArgumentList.Add("--cookies");
                    psi.ArgumentList.Add(_cookiesFile);
                }
            }

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Не удалось запустить {_exePath}");

            var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }

        private static string LastLine(string text)
        {
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        }
    }
}
