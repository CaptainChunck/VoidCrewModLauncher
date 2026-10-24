using System.Diagnostics;
using System.Runtime.InteropServices;
using VoidCrewModLauncher.Core.Steam;

namespace VoidCrewModLauncher.Core.Game;

/// <summary>Starting, finding, focusing and closing Void Crew.</summary>
public static class GameProcess
{
    public static Process? Find()
    {
        Process[] found = Process.GetProcessesByName(AppInfo.GameProcessName);
        Process? game = found.FirstOrDefault();
        foreach (Process other in found.Skip(1)) other.Dispose();
        return game;
    }

    public static bool IsRunning()
    {
        using Process? game = Find();
        return game != null;
    }

    /// <summary>
    /// Starts the game with BepInEx loaded from <paramref name="preloader"/>: through Steam (steam.exe -applaunch, as
    /// r2modman does) or, when asked or when Steam isn't found, by starting the exe with Steam's app id set.
    /// </summary>
    public static void Start(string gameDirectory, string preloader, bool throughSteam)
    {
        string? steam = throughSteam ? SteamLocator.SteamExe() : null;
        (preloader, bool keepSteam) = PathForSteam(preloader, steam != null, ShortPath);
        if (steam != null && !keepSteam)
        {
            AppLog.Info("The launcher's folder has a space in its path and no short name, which Steam would cut in two; starting the game directly.");
            steam = null;
        }
        string[] arguments = Doorstop.Arguments(preloader);
        ProcessStartInfo start;
        if (steam != null)
        {
            start = new ProcessStartInfo(steam) { UseShellExecute = false };
            start.ArgumentList.Add("-applaunch");
            start.ArgumentList.Add(AppInfo.SteamAppId);
        }
        else
        {
            start = new ProcessStartInfo(Path.Combine(gameDirectory, AppInfo.GameExeName))
            {
                UseShellExecute = false,
                WorkingDirectory = gameDirectory,
            };
            start.Environment["SteamAppId"] = AppInfo.SteamAppId;
            start.Environment["SteamGameId"] = AppInfo.SteamAppId;
        }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        AppLog.Info($"Starting Void Crew {(steam != null ? "through Steam" : "directly")}: {string.Join(' ', arguments)}");
        using Process? process = Process.Start(start);
    }

    /// <summary>
    /// Steam passes the arguments on to the game without their quotes, so a path with a space (a Windows user name like
    /// "Jo Smith") arrives cut in two and BepInEx never loads. Then the path's short form (C:\Users\JOSMIT~1\...) is
    /// used; where the drive has no short names, the game is started directly instead, which keeps the quotes.
    /// </summary>
    internal static (string Path, bool ThroughSteam) PathForSteam(string preloader, bool throughSteam, Func<string, string?> shortPath)
    {
        if (!throughSteam || !preloader.Contains(' ')) return (preloader, throughSteam);
        string? shortForm = shortPath(preloader);
        return shortForm != null && !shortForm.Contains(' ') ? (shortForm, true) : (preloader, false);
    }

    internal static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 || length > buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    /// <summary>Asks the game to close as if its window were closed. False if it isn't running.</summary>
    public static bool RequestClose()
    {
        using Process? game = Find();
        if (game == null) return false;
        return game.CloseMainWindow();
    }

    public static async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!IsRunning()) return true;
            await Task.Delay(500, cancel);
        }
        return !IsRunning();
    }

    /// <summary>Ends the game's process. Only on the player's say-so: unsaved progress is lost.</summary>
    public static void Kill()
    {
        using Process? game = Find();
        game?.Kill();
    }

    public static void Focus(int processId)
    {
        IntPtr window = MainWindow(processId);
        if (window == IntPtr.Zero) return;
        if (IsIconic(window)) ShowWindow(window, SwRestore);
        SetForegroundWindow(window);
    }

    private static IntPtr MainWindow(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.MainWindowHandle;
        }
        catch (ArgumentException)
        {
            return IntPtr.Zero;
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint bufferLength);
}
