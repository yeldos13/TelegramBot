using System.Globalization;

namespace AnikiChatBot.Modules.Media
{
    public class FFmpeg
    {
        private const int AudioKbps = 96;

        private const int MinVideoKbps = 150;

        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;

        public FFmpeg(string ffmpegPath)
        {
            _ffmpegPath = ffmpegPath;
            _ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        }

        public async Task<double?> GetDurationAsync(string path, CancellationToken ct)
        {
            var result = await ProcessRunner.RunAsync(_ffprobePath,
                ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path], ct);

            return double.TryParse(result.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
                ? duration
                : null;
        }

        public async Task<string?> CompressAsync(string inputPath, long targetBytes, CancellationToken ct)
        {
            double? duration = await GetDurationAsync(inputPath, ct);
            if (duration is not > 0)
                return null;

            int videoKbps = CalculateVideoKbps(targetBytes, duration.Value);
            if (videoKbps < MinVideoKbps)
                return null;

            int shortSide = videoKbps >= 1200 ? 720 : 480;
            string scale = $"scale='if(gt(iw,ih),-2,min({shortSide},iw))':'if(gt(iw,ih),min({shortSide},ih),-2)'";

            string outputPath = Path.Combine(
                Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + "_small.mp4");

            var result = await ProcessRunner.RunAsync(_ffmpegPath,
            [
                "-y", "-v", "error",
                "-i", inputPath,
                "-vf", scale,
                "-c:v", "libx264", "-preset", "veryfast",
                "-b:v", $"{videoKbps}k", "-maxrate", $"{videoKbps * 12 / 10}k", "-bufsize", $"{videoKbps * 2}k",
                "-c:a", "aac", "-b:a", $"{AudioKbps}k",
                "-movflags", "+faststart",
                outputPath
            ], ct);

            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                Console.Error.WriteLine($"[ffmpeg] Не удалось сжать {inputPath}: {ProcessRunner.LastLine(result.StdErr)}");
                return null;
            }

            if (new FileInfo(outputPath).Length > targetBytes)
            {
                File.Delete(outputPath);
                return null;
            }

            return outputPath;
        }

        public static int CalculateVideoKbps(long targetBytes, double durationSeconds)
        {
            double totalKbps = targetBytes * 8 / 1000.0 / durationSeconds * 0.9;
            return (int)(totalKbps - AudioKbps);
        }
    }
}
