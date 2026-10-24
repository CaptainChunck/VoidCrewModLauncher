using System.Diagnostics;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Game;

/// <summary>
/// When Void Crew runs after a launch but its bridge never connects: what the launcher can see about why, written to
/// the launcher's log so a player can send it. Each line is one fact: the command line the game really got, the loader
/// in the game folder, and whether BepInEx wrote its log for this launch.
/// </summary>
public static class LaunchCheck
{
    public static List<string> Run(AppPaths paths, string? gameDirectory, DateTime launchedUtc)
    {
        var lines = new List<string>();
        void Try(string what, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                lines.Add($"{what}: couldn't check ({e.GetType().Name}: {e.Message})");
            }
        }

        lines.Add($"Windows {Environment.OSVersion.Version}, launched {launchedUtc:HH:mm:ss} UTC, data folder {paths.Root}");
        Try("Game command line", () => lines.Add("Game command line: " + (GameCommandLine() ?? "(not readable)")));
        Try("Game folder", () =>
        {
            if (gameDirectory == null)
            {
                lines.Add("Game folder: not found");
                return;
            }
            lines.Add($"Game folder: {gameDirectory}");
            foreach (string name in new[] { Doorstop.ProxyFile, "version.dll", "winmm.dll", Doorstop.ConfigFile, Doorstop.VersionFile, "doorstop_config.ini.bak" })
            {
                var file = new FileInfo(Path.Combine(gameDirectory, name));
                if (file.Exists) lines.Add($"  {name}: {file.Length} bytes, {file.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
            string config = Path.Combine(gameDirectory, Doorstop.ConfigFile);
            if (File.Exists(config))
            {
                foreach (string line in File.ReadLines(config).Take(40).Where(l => l.Trim().Length > 0)) lines.Add($"  ini> {line.Trim()}");
            }
            DoorstopState state = Doorstop.Inspect(gameDirectory);
            lines.Add($"  Loader: {state.Describe()} Enabled in its config: {state.EnabledInConfig?.ToString() ?? "?"}, target: {state.ConfigTarget ?? "?"}");
        });
        Try("Launcher files", () =>
        {
            lines.Add($"Preloader: {(File.Exists(paths.Preloader) ? "there" : "MISSING")} ({paths.Preloader})");
            string bridge = Path.Combine(paths.RuntimeBepInEx, "plugins", BridgeDefaults.PluginFolder);
            lines.Add($"Bridge plugin: {(Directory.Exists(bridge) && Directory.EnumerateFiles(bridge, "*.dll").Any() ? "there" : "MISSING")}");
            lines.Add($"launch.json: {(File.Exists(paths.RuntimeLaunchFile) ? "there" : "MISSING")}");
        });
        Try("BepInEx log", () =>
        {
            var log = new FileInfo(paths.GameLog);
            if (!log.Exists)
            {
                lines.Add("BepInEx log: none (BepInEx has never run from the launcher's folder)");
                return;
            }
            bool fresh = log.LastWriteTimeUtc >= launchedUtc.AddSeconds(-5);
            lines.Add($"BepInEx log: last written {log.LastWriteTime:HH:mm:ss}, {(fresh ? "during this launch" : "BEFORE this launch, so BepInEx didn't run this time")}");
            if (!fresh) return;
            using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            List<string> all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            foreach (string line in all.Where(l => l.Contains("Mod Launcher Bridge", StringComparison.Ordinal) || l.Contains("[Error", StringComparison.Ordinal) || l.Contains("[Fatal", StringComparison.Ordinal)).Take(25))
            {
                lines.Add($"  log> {line}");
            }
            foreach (string line in all.TakeLast(8)) lines.Add($"  end> {line}");
        });
        return lines;
    }

    /// <summary>The command line Void Crew was started with, as Windows has it.</summary>
    private static string? GameCommandLine()
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     $"(Get-CimInstance Win32_Process -Filter \"Name='{AppInfo.GameProcessName}.exe'\" | Select-Object -First 1).CommandLine" })
        {
            start.ArgumentList.Add(argument);
        }
        using Process? process = Process.Start(start);
        if (process == null) return null;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(20000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return null;
        }
        string text = output.Result.Trim();
        return text.Length > 0 ? text : null;
    }
}
