using Microsoft.Win32;

namespace VoidCrewModLauncher.Core.Steam;

/// <summary>Finds Steam and the Void Crew install through Steam's registry key and library folders.</summary>
public static class SteamLocator
{
    public static string? SteamDirectory()
    {
        string? path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(path)) path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrEmpty(path))
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            path = Directory.Exists(fallback) ? fallback : null;
        }
        return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path.Replace('/', '\\'));
    }

    public static string? SteamExe()
    {
        string? exe = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return Path.GetFullPath(exe.Replace('/', '\\'));
        string? dir = SteamDirectory();
        if (dir == null) return null;
        exe = Path.Combine(dir, "steam.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Every Steam library folder (the Steam folder itself first).</summary>
    public static List<string> LibraryFolders(string steamDirectory)
    {
        var folders = new List<string> { steamDirectory };
        VdfNode? root = Vdf.Load(Path.Combine(steamDirectory, "steamapps", "libraryfolders.vdf"));
        VdfNode? libraries = root?["libraryfolders"];
        if (libraries != null)
        {
            foreach (VdfNode library in libraries.Children.Values)
            {
                string? path = library.Get("path");
                if (string.IsNullOrEmpty(path)) continue;
                path = Path.GetFullPath(path);
                if (!folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path);
            }
        }
        return folders;
    }

    /// <summary>The Void Crew folder from Steam's app manifest, or null when it isn't installed through Steam.</summary>
    public static string? FindGame()
    {
        string? steam = SteamDirectory();
        if (steam == null) return null;
        foreach (string library in LibraryFolders(steam))
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppInfo.SteamAppId}.acf");
            VdfNode? app = Vdf.Load(manifest)?["AppState"];
            string? installDir = app?.Get("installdir");
            if (string.IsNullOrEmpty(installDir)) continue;
            string game = Path.Combine(library, "steamapps", "common", installDir);
            if (IsGameDirectory(game)) return game;
        }
        return null;
    }

    public static bool IsGameDirectory(string? directory) =>
        !string.IsNullOrEmpty(directory)
        && File.Exists(Path.Combine(directory, AppInfo.GameExeName))
        && Directory.Exists(Path.Combine(directory, AppInfo.GameDataFolder));
}
