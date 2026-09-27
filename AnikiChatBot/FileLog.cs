using System.Text;

namespace AnikiChatBot
{
    public sealed class FileLog : TextWriter
    {
        private const int KeepDays = 14;
        private static readonly object FileLock = new object();

        private readonly string _directory;
        private readonly string _prefix;
        private readonly TextWriter? _echo;
        private readonly StringBuilder _line = new StringBuilder();

        private FileLog(string directory, string prefix, TextWriter? echo)
        {
            _directory = directory;
            _prefix = prefix;
            _echo = echo;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public static void Install(string directory, bool echoToConsole)
        {
            Directory.CreateDirectory(directory);
            DeleteOldLogs(directory);

            Console.SetOut(new FileLog(directory, "", echoToConsole ? Console.Out : null));
            Console.SetError(new FileLog(directory, "ERROR ", echoToConsole ? Console.Error : null));
        }

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                    FlushLine();
                else if (value != '\r')
                    _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;

            foreach (char c in value)
                Write(c);
        }

        public override void WriteLine(string? value)
        {
            lock (_line)
            {
                Write(value);
                Write('\n');
            }
        }

        private void FlushLine()
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {_prefix}{_line}";
            _line.Clear();

            _echo?.WriteLine(line);

            lock (FileLock)
            {
                try
                {
                    string path = Path.Combine(_directory, $"bot-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        public static string ReadTail(string directory, int lineCount, int maxChars = 4000)
        {
            var file = Directory.Exists(directory)
                ? new DirectoryInfo(directory).GetFiles("bot-*.log").OrderBy(f => f.LastWriteTime).LastOrDefault()
                : null;

            if (file == null)
                return "Лог пуст.";

            List<string> lines;
            lock (FileLock)
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.TrimEnd('\r'))
                    .TakeLast(lineCount)
                    .ToList();
            }

            var result = new List<string>();
            int length = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (length + lines[i].Length + 1 > maxChars)
                    break;
                result.Insert(0, lines[i]);
                length += lines[i].Length + 1;
            }

            return result.Count == 0 ? "Лог пуст." : string.Join("\n", result);
        }

        private static void DeleteOldLogs(string directory)
        {
            foreach (var file in Directory.GetFiles(directory, "bot-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepDays))
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }
    }
}
