namespace VoidCrewModLauncher.Core;

/// <summary>The launcher's log file (logs\launcher.log; the previous run's is launcher.prev.log).</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _file;

    /// <summary>Every line written, for the UI's status bar.</summary>
    public static event Action<string>? Line;

    public static void Init(string file)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (File.Exists(file))
                {
                    File.Copy(file, Path.ChangeExtension(file, ".prev.log"), true);
                    File.Delete(file);
                }
            }
            catch
            {
                // Another copy of the launcher may hold the file; keep appending.
            }
            _file = file;
        }
        Info($"{AppInfo.Name} {AppInfo.Version} started.");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}";
        lock (Gate)
        {
            if (_file != null)
            {
                try
                {
                    File.AppendAllText(_file, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never take the launcher down.
                }
            }
        }
        Line?.Invoke(line);
    }
}
