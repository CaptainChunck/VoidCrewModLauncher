using System.Diagnostics;
using Microsoft.Win32;
using VoidCrewModLauncher.Core.Game;

namespace VoidCrewModLauncher.Core.Setup;

/// <summary>Where an install goes. <see cref="ForThisUser"/> is the real one; tests pass their own folders.</summary>
public sealed record InstallLocations(string InstallDirectory, string StartMenuDirectory, string DesktopDirectory, string? UninstallKey)
{
    public const string ExeName = "VoidCrewModLauncher.exe";
    public const string ShortcutName = "Void Crew Mod Launcher.lnk";

    /// <summary>The release download: the same exe, which installs itself when started from anywhere else.</summary>
    public const string SetupName = "VoidCrewModLauncher-Setup.exe";

    public string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    /// <summary>Where the voidcrewmod:// link type is registered (HKCU\Software\Classes, or under the test key); null for none.</summary>
    public string? ClassesKey { get; init; }

    /// <summary>Set to a folder to try the install there (Programs, StartMenu and Desktop inside it, a test registry key).</summary>
    public const string TestRootVariable = "VCML_INSTALL_ROOT";

    /// <summary>Per user, no admin rights: %LOCALAPPDATA%\Programs, the user's Start menu and desktop, HKCU.</summary>
    public static InstallLocations ForThisUser()
    {
        string? testRoot = Environment.GetEnvironmentVariable(TestRootVariable);
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            return new InstallLocations(Path.Combine(testRoot, "Programs", "VoidCrewModLauncher"), Path.Combine(testRoot, "StartMenu"),
                Path.Combine(testRoot, "Desktop"), @"Software\VoidCrewModLauncherInstallTest")
            {
                ClassesKey = @"Software\VoidCrewModLauncherInstallTest\Classes",
            };
        }
        return new InstallLocations(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VoidCrewModLauncher"),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VoidCrewModLauncher")
        {
            ClassesKey = @"Software\Classes",
        };
    }

    public static bool IsTestInstall => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestRootVariable));
}

/// <summary>
/// The one-click install: the release exe installs itself when it's started from anywhere else (a download), then the
/// installed copy opens. It goes in the user's own Programs folder (no admin rights), with Start menu and desktop
/// shortcuts and an entry in Windows' installed apps whose uninstall runs this exe with --uninstall. Running a newer
/// release exe the same way updates the install. Mods and settings live in the launcher's data folder, not here.
/// </summary>
public static class Installer
{
    /// <summary>The alternate data stream where Windows marks a file as downloaded.</summary>
    public const string ZoneStream = ":Zone.Identifier";

    /// <summary>True for the published single-file build; developer builds never install themselves.</summary>
#pragma warning disable IL3000 // Location is empty inside a single-file app, which is exactly what this checks.
    public static bool IsPublishedBuild => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>
    /// Whether this start should install: a published build, not started from the install folder, and not asked to
    /// stay portable (--portable, or a portable.txt next to the exe).
    /// </summary>
    public static bool ShouldInstall(string[] args, InstallLocations locations)
    {
        if (!IsPublishedBuild) return false;
        if (args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase))) return false;
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        if (SamePath(exe, locations.InstalledExe)) return false;
        return !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "portable.txt"));
    }

    /// <summary>The installed launcher is open (its exe can't be replaced while it runs).</summary>
    public static bool InstalledCopyIsRunning(InstallLocations locations)
    {
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallLocations.ExeName)))
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainModule?.FileName is { } path && SamePath(path, locations.InstalledExe)) return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // A process we may not look into; not ours.
                }
            }
        }
        return false;
    }

    /// <summary>Copies <paramref name="sourceExe"/> into place and adds the shortcuts and the uninstall entry.</summary>
    public static void Install(string sourceExe, InstallLocations locations, string version)
    {
        Directory.CreateDirectory(locations.InstallDirectory);
        CopyExe(sourceExe, locations.InstalledExe);

        if (locations.ClassesKey != null) LauncherLinks.Register(locations.InstalledExe, locations.ClassesKey);
        CreateShortcut(Path.Combine(locations.StartMenuDirectory, InstallLocations.ShortcutName), locations.InstalledExe);
        CreateShortcut(Path.Combine(locations.DesktopDirectory, InstallLocations.ShortcutName), locations.InstalledExe);

        if (locations.UninstallKey != null)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(locations.UninstallKey);
            key.SetValue("DisplayName", AppInfo.Name);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "CaptainChunck");
            key.SetValue("DisplayIcon", locations.InstalledExe);
            key.SetValue("InstallLocation", locations.InstallDirectory);
            key.SetValue("UninstallString", $"\"{locations.InstalledExe}\" --uninstall");
            key.SetValue("URLInfoAbout", AppInfo.RepositoryUrl);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(locations.InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }
    }

    /// <summary>
    /// Puts <paramref name="sourceExe"/> at <paramref name="target"/>, through a temporary copy so a failed copy never leaves
    /// half an exe. Retries for a few seconds while the old exe is still closing.
    /// </summary>
    private static void CopyExe(string sourceExe, string target)
    {
        string temp = target + ".new";
        File.Copy(sourceExe, temp, true);
        // A downloaded setup carries Windows' "came from the internet" mark, and a copy keeps it. The player has already
        // chosen to run this file, so the installed copy shouldn't ask again each time it starts.
        TryDelete(temp + ZoneStream);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    // ---------------------------------------------------------------- self-update

    /// <summary>
    /// Starts a downloaded new version to replace <paramref name="target"/> (this launcher's exe) once this process has
    /// closed. The caller closes the launcher right after. <paramref name="arguments"/> are passed on to the restarted
    /// launcher.
    /// </summary>
    public static void StartUpdate(string setup, string target, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)! };
        start.ArgumentList.Add(ApplyUpdateArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(target);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    /// <summary>The argument a downloaded update starts with: --apply-update &lt;old process id&gt; &lt;exe to replace&gt; [launcher arguments].</summary>
    public const string ApplyUpdateArgument = "--apply-update";

    /// <summary>The argument the updated launcher starts with: --updated &lt;old version&gt;.</summary>
    public const string UpdatedArgument = "--updated";

    /// <summary>
    /// Run by the new version: waits for the old launcher to close, then installs this exe over it (the install, with its
    /// shortcuts and uninstall entry, or a portable copy in place). Throws when the old launcher won't close.
    /// </summary>
    public static void ApplyUpdate(string sourceExe, int oldProcessId, string target, InstallLocations locations, string version)
    {
        try
        {
            using Process old = Process.GetProcessById(oldProcessId);
            if (!old.WaitForExit(30_000)) throw new InvalidOperationException("The old launcher didn't close.");
        }
        catch (ArgumentException)
        {
            // Already closed.
        }
        if (SamePath(target, locations.InstalledExe)) Install(sourceExe, locations, version);
        else CopyExe(sourceExe, target);
        AppLog.Info($"Updated the launcher at {target} to {version}.");
    }

    /// <summary>
    /// Removes the shortcuts, the uninstall entry, the BepInEx loader the launcher added to the game folder (only if the
    /// launcher added it), and, when asked, the data folder (mods and settings). The install folder holds the running
    /// exe, so it is deleted a few seconds after this process ends.
    /// </summary>
    public static void Uninstall(InstallLocations locations, string? gameDirectory, string? dataDirectory)
    {
        TryDelete(Path.Combine(locations.StartMenuDirectory, InstallLocations.ShortcutName));
        TryDelete(Path.Combine(locations.DesktopDirectory, InstallLocations.ShortcutName));
        if (locations.UninstallKey != null) Registry.CurrentUser.DeleteSubKeyTree(locations.UninstallKey, false);
        if (locations.ClassesKey != null) LauncherLinks.Unregister(locations.ClassesKey);

        if (gameDirectory != null && Doorstop.Inspect(gameDirectory).InstalledByLauncher)
        {
            foreach (string file in new[] { Doorstop.ProxyFile, Doorstop.ConfigFile, Doorstop.VersionFile }) TryDelete(Path.Combine(gameDirectory, file));
        }
        if (dataDirectory != null && Directory.Exists(dataDirectory))
        {
            try
            {
                Directory.Delete(dataDirectory, true);
            }
            catch (IOException e)
            {
                AppLog.Warn($"Could not delete {dataDirectory}: {e.Message}");
            }
        }

        if (Directory.Exists(locations.InstallDirectory))
        {
            // The exe is running; a hidden cmd deletes the folder once it has exited.
            var cleanup = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            cleanup.ArgumentList.Add("/c");
            cleanup.ArgumentList.Add($"ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{locations.InstallDirectory}\"");
            Process.Start(cleanup)?.Dispose();
        }
    }

    /// <summary>A .lnk shortcut through Windows' own shell object.</summary>
    public static void CreateShortcut(string shortcut, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available to make shortcuts.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcut);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = AppInfo.Name;
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException e)
        {
            AppLog.Warn($"Could not delete {file}: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            AppLog.Warn($"Could not delete {file}: {e.Message}");
        }
    }
}
