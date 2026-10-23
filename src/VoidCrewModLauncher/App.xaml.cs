using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Setup;
using VoidCrewModLauncher.Infrastructure;
using VoidCrewModLauncher.ViewModels;

namespace VoidCrewModLauncher;

/// <summary>
/// Start-up: one launcher at a time, the data folder (--data-dir or VCML_DATA_DIR), logging, then the main window.
/// --render-previews &lt;folder&gt; draws every page with sample data into PNGs and exits (for checking the look).
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private LauncherService? _service;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? dataDir = Argument(e.Args, "--data-dir");
        string? previews = Argument(e.Args, "--render-previews");

        if (previews != null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Previews.Render(previews);
            Shutdown();
            return;
        }

        // A downloaded update installing itself over the launcher that downloaded it (see Installer.StartUpdate).
        var locations = InstallLocations.ForThisUser();
        int apply = Array.FindIndex(e.Args, a => a.Equals(Installer.ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase));
        if (apply >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunApplyUpdate(e.Args[(apply + 1)..], locations);
            Shutdown();
            return;
        }

        // The release exe installs itself on its first start (the one-click install), and uninstalls from Windows' apps list.
        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunUninstall(locations, dataDir);
            Shutdown();
            return;
        }
        if (Installer.ShouldInstall(e.Args, locations) && RunInstall(locations))
        {
            Shutdown();
            return;
        }

        // A voidcrewmod:// link (a website's "download with the Mod Launcher") starts the launcher with the link.
        string? link = LauncherLinks.FindIn(e.Args);
        _single = new Mutex(true, "VoidCrewModLauncher-" + Environment.UserName, out bool first);
        if (!first)
        {
            // The launcher is open: it handles the link.
            if (link != null && LinkPipe.SendAsync(link).GetAwaiter().GetResult())
            {
                Shutdown();
                return;
            }
            MessageBox.Show("The Void Crew Mod Launcher is already open.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths paths = AppPaths.FromEnvironment(dataDir);
        paths.EnsureCreated();
        AppLog.Init(paths.LauncherLog);
        AppLog.Info($"Data folder: {paths.Root}");
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task error", args.Exception);
            args.SetObserved();
        };

        _service = new LauncherService(paths);
        _service.Start();
        var dialogs = new Dialogs();
        var model = new MainViewModel(_service, dialogs) { LauncherArguments = LauncherArguments(e.Args) };
        var window = new MainWindow { DataContext = model };
        dialogs.Owner = window;
        MainWindow = window;
        window.Show();
        _links = new LinkPipe();
        _links.Start(received => Dispatcher.BeginInvoke(() => _ = model.HandleLinkAsync(received)));
        RegisterLinks(locations);
        _ = model.StartAsync(Argument(e.Args, Installer.UpdatedArgument), link);
    }

    private LinkPipe? _links;

    /// <summary>
    /// voidcrewmod:// links open this launcher: the setup registers them; a copy that was never set up (portable)
    /// registers itself when nothing else has.
    /// </summary>
    private static void RegisterLinks(InstallLocations locations)
    {
        if (!Installer.IsPublishedBuild || locations.ClassesKey == null || Environment.ProcessPath is not { } exe) return;
        try
        {
            bool installed = string.Equals(Path.GetFullPath(exe), Path.GetFullPath(locations.InstalledExe), StringComparison.OrdinalIgnoreCase);
            using Microsoft.Win32.RegistryKey? existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{locations.ClassesKey}\{LauncherLinks.Scheme}");
            if ((installed || existing == null) && !LauncherLinks.IsRegistered(exe, locations.ClassesKey)) LauncherLinks.Register(exe, locations.ClassesKey);
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not register voidcrewmod:// links: {e.Message}");
        }
    }

    /// <summary>The arguments a restarted launcher keeps (after a self-update).</summary>
    private static List<string> LauncherArguments(string[] args)
    {
        var kept = new List<string>();
        if (args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase))) kept.Add("--portable");
        if (Argument(args, "--data-dir") is { } dataDir) kept.AddRange(new[] { "--data-dir", dataDir });
        // A link that started this launcher is handled after the update instead.
        if (LauncherLinks.FindIn(args) is { } link) kept.Add(link);
        return kept;
    }

    /// <summary>
    /// The new version, started by the old one: waits for it to close, takes its place and starts it again. If that
    /// fails, the old version starts as it was.
    /// </summary>
    private static void RunApplyUpdate(string[] args, InstallLocations locations)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out int oldProcess)) return;
        string target = args[1];
        List<string> restart = args[2..].ToList();
        try
        {
            Installer.ApplyUpdate(Environment.ProcessPath!, oldProcess, target, locations, AppInfo.Version);
        }
        catch (Exception error)
        {
            MessageBox.Show($"The launcher couldn't update itself ({error.Message}).\n\nIt opens as it was. The new version is on {AppInfo.RepositoryUrl}/releases/latest.",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            int updated = restart.FindIndex(a => a.Equals(Installer.UpdatedArgument, StringComparison.OrdinalIgnoreCase));
            if (updated >= 0) restart.RemoveRange(updated, Math.Min(2, restart.Count - updated));
        }
        if (InstallLocations.IsTestInstall || !File.Exists(target)) return;
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        foreach (string argument in restart) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _links?.Dispose();
        _service?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unexpected error", e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in the launcher's log (SETTINGS > Launcher log).", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Installs this exe and starts the installed copy. False when it should run from here instead.</summary>
    private static bool RunInstall(InstallLocations locations)
    {
        if (Installer.InstalledCopyIsRunning(locations))
        {
            MessageBox.Show("The Void Crew Mod Launcher is open. Close it, then run this again to update it.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        try
        {
            Installer.Install(Environment.ProcessPath!, locations, AppInfo.Version);
            if (!InstallLocations.IsTestInstall)
            {
                Process.Start(new ProcessStartInfo(locations.InstalledExe) { UseShellExecute = true, WorkingDirectory = locations.InstallDirectory })?.Dispose();
            }
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show($"The launcher couldn't install itself ({error.Message}). It will run from where it is instead.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static void RunUninstall(InstallLocations locations, string? dataDir)
    {
        var dialogs = new Dialogs();
        if (!dialogs.Confirm("Uninstall", "Remove the Void Crew Mod Launcher from this PC?", "UNINSTALL")) return;
        AppPaths paths = AppPaths.FromEnvironment(dataDir);
        bool removeData = Directory.Exists(paths.Root) && dialogs.Ask("Downloaded mods and settings",
            "Delete the launcher's downloaded mods and settings too?",
            new DialogButton("DELETE THEM", ButtonKind.Cancel), new DialogButton("KEEP THEM")) == 0;
        string? game = null;
        try
        {
            string? chosen = JsonFiles.Load<LauncherSettings>(paths.SettingsFile)?.GameDirectory;
            game = Core.Steam.SteamLocator.IsGameDirectory(chosen) ? chosen : Core.Steam.SteamLocator.FindGame();
        }
        catch (Exception error)
        {
            AppLog.Warn("Uninstall could not find the game folder: " + error.Message);
        }
        Installer.Uninstall(locations, game, removeData ? paths.Root : null);
        dialogs.Info("Uninstalled", "The Void Crew Mod Launcher is removed." + (removeData ? "" : " Your mods and settings are still in " + paths.Root + "."));
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
