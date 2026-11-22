namespace AnikiChatBot
{
    public static class Backup
    {
        public const int KeepDays = 7;

        public static bool IsDataFile(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            return (name.EndsWith(".json") || name.EndsWith(".txt"))
                && !name.StartsWith("appsettings")
                && !name.EndsWith(".deps.json")
                && !name.EndsWith(".runtimeconfig.json")
                && !name.EndsWith("limit.json")
                && !name.Contains(".backup-");
        }

        public static string? Run(string dataDir, string backupRoot, DateTime today)
        {
            string target = Path.Combine(backupRoot, today.ToString("yyyy-MM-dd"));
            if (Directory.Exists(target))
                return null;

            string temp = target + ".tmp";
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);

            foreach (string file in Directory.EnumerateFiles(dataDir).Where(IsDataFile))
            {
                try
                {
                    File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"[Backup] Не удалось скопировать {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Directory.Move(temp, target);

            foreach (string old in Directory.GetDirectories(backupRoot)
                .Where(d => !d.EndsWith(".tmp"))
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Skip(KeepDays))
                Directory.Delete(old, recursive: true);

            return target;
        }
    }
}
