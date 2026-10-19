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

        // The release exe installs itself on its first start (the one-click install), and uninstalls from Windows' apps list.
        var locations = InstallLocations.ForThisUser();
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

        _single = new Mutex(true, "VoidCrewModLauncher-" + Environment.UserName, out bool first);
        if (!first)
        {
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
        var model = new MainViewModel(_service, dialogs);
        var window = new MainWindow { DataContext = model };
        dialogs.Owner = window;
        MainWindow = window;
        window.Show();
        _ = model.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
