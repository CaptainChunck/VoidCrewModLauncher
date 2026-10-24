using System.Diagnostics;

namespace VoidCrewModLauncher.Infrastructure;

public static class Format
{
    public static string Count(long value) => value switch
    {
        >= 1_000_000 => Loc.F("{0:0.#}M", value / 1_000_000.0),
        >= 10_000 => Loc.F("{0:0}K", value / 1000.0),
        >= 1000 => Loc.F("{0:0.#}K", value / 1000.0),
        _ => value.ToString(),
    };

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => Loc.F("{0:0.0} GB", bytes / (1024.0 * 1024 * 1024)),
        >= 1024L * 1024 => Loc.F("{0:0.0} MB", bytes / (1024.0 * 1024)),
        >= 1024 => Loc.F("{0:0} KB", bytes / 1024.0),
        _ => Loc.F("{0} B", bytes),
    };

    public static string Ago(DateTimeOffset time)
    {
        TimeSpan age = DateTimeOffset.Now - time;
        if (age.TotalDays >= 365) return Loc.F("{0}y ago", (int)(age.TotalDays / 365));
        if (age.TotalDays >= 30) return Loc.F("{0}mo ago", (int)(age.TotalDays / 30));
        if (age.TotalDays >= 1) return Loc.F("{0}d ago", (int)age.TotalDays);
        if (age.TotalHours >= 1) return Loc.F("{0}h ago", (int)age.TotalHours);
        return Loc.T("just now");
    }

    /// <summary>Opens a web page or folder with the shell. Only http(s) links and existing paths.</summary>
    public static void Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        bool web = Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        if (!web && !File.Exists(target) && !Directory.Exists(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            Core.AppLog.Warn($"Could not open {target}: {e.Message}");
        }
    }

    /// <summary>Shows a file selected in Explorer.</summary>
    public static void Reveal(string path)
    {
        if (!File.Exists(path))
        {
            Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e)
        {
            Core.AppLog.Warn($"Could not show {path}: {e.Message}");
        }
    }
}
