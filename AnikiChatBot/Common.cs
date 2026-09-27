using System.Text.Json;
using Telegram.Bot.Types;

namespace AnikiChatBot
{
    public record ParsedCommand(string Command, string Args);

    public static class BotCommands
    {
        public static ParsedCommand? Parse(string? text, string botUsername)
        {
            if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
                return null;

            int space = text.IndexOfAny([' ', '\n']);
            string head = space < 0 ? text[1..] : text[1..space];
            string args = space < 0 ? "" : text[(space + 1)..].Trim();

            int at = head.IndexOf('@');
            if (at >= 0)
            {
                if (!string.Equals(head[(at + 1)..], botUsername, StringComparison.OrdinalIgnoreCase))
                    return null;
                head = head[..at];
            }

            return new ParsedCommand(head.ToLowerInvariant(), args);
        }
    }

    public static class Users
    {
        public static string DisplayName(User user)
        {
            string name = string.Join(" ", new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(name) ? user.Username ?? "Аноним" : name;
        }

        public static string NameWithUsername(User user) =>
            DisplayName(user) + (user.Username != null ? $" (@{user.Username})" : "");
    }

    public static class JsonFile
    {
        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        public static T? Load<T>(string path, string tag, JsonSerializerOptions? options = null) where T : class
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), options) : null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{tag}] Не удалось прочитать {path}: {ex.Message}");

                try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
                return null;
            }
        }

        public static void Save<T>(string path, T value, string tag, JsonSerializerOptions? options = null)
        {
            try
            {
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(value, options ?? Indented));
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{tag}] Не удалось сохранить {path}: {ex.Message}");
            }
        }
    }

    public sealed class PeriodicSaver
    {
        private readonly Action _save;
        private readonly object _lock;
        private readonly Timer _timer;
        private bool _dirty;

        public PeriodicSaver(Action save, object lockObject, TimeSpan interval)
        {
            _save = save;
            _lock = lockObject;
            _timer = new Timer(_ => Flush(), null, interval, interval);
        }

        public void MarkDirty()
        {
            lock (_lock)
                _dirty = true;
        }

        public void Flush()
        {
            lock (_lock)
            {
                if (!_dirty)
                    return;
                _dirty = false;
                _save();
            }
        }

        public void SaveNow()
        {
            lock (_lock)
            {
                _dirty = false;
                _save();
            }
        }
    }
}
