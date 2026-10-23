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
        string[] arguments = Doorstop.Arguments(preloader);
        string? steam = throughSteam ? SteamLocator.SteamExe() : null;
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
}
