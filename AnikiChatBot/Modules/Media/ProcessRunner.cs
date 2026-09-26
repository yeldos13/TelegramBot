using System.Diagnostics;
using System.Text;

namespace AnikiChatBot.Modules.Media
{
    public static class ProcessRunner
    {
        public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
            string exePath, IEnumerable<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Не удалось запустить {exePath}");

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

        public static string LastLine(string text)
        {
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        }
    }
}
