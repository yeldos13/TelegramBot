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
