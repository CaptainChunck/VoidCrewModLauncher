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
/// --render-previews &lt;folder&gt; [--preview-language &lt;code&gt;] [--preview-width &lt;pixels&gt;] draws every page with
/// sample data into PNGs and exits (for checking the look).
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private LauncherService? _service;
    private MainViewModel? _model;

    /// <summary>One launcher per Windows user: the launcher holds this while it runs (an uninstall too).</summary>
    private static string SingleInstanceName => "VoidCrewModLauncher-" + Environment.UserName;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = StartArguments(e.Args);
        string? dataDir = Argument(args, "--data-dir");
        string? previews = Argument(args, "--render-previews");
        WaitForRestartedLauncher(args);

        if (previews != null)
        {
            // English for the README's screenshots; --preview-language <code> to check how a translation fits, and
            // --preview-width <pixels> at another window width (the window's smallest is 1080).
            Loc.Use(Argument(args, "--preview-language") ?? Loc.English);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int width = int.TryParse(Argument(args, "--preview-width"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int w) ? w : 0;
            Previews.Render(previews, dataDir, width);
            Shutdown();
            return;
        }

        // The language first: every message from here on is shown in it.
        Loc.Use(LauncherSettings.ReadLanguage(AppPaths.FromEnvironment(dataDir)));

        // A downloaded update installing itself over the launcher that downloaded it (see Installer.StartUpdate).
        var locations = InstallLocations.ForThisUser();
        int apply = Array.FindIndex(args, a => a.Equals(Installer.ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase));
        if (apply >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunApplyUpdate(args[(apply + 1)..], locations);
            Shutdown();
            return;
        }

        // The release exe installs itself on its first start (the one-click install), and uninstalls from Windows' apps list.
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Held while uninstalling, so the launcher can't start meanwhile; when it is already open, it keeps its
            // files (and the loader in the game folder) in use, so the uninstall waits for it to be closed.
            _single = new Mutex(true, SingleInstanceName, out bool alone);
            RunUninstall(locations, dataDir, launcherOpen: !alone);
            Shutdown();
            return;
        }
        if (Installer.ShouldInstall(args, locations) && RunInstall(locations))
        {
            Shutdown();
            return;
        }

        // A voidcrewmod:// link (a website's "download with the Mod Launcher") starts the launcher with the link.
        string? link = LauncherLinks.FindIn(args);
        _single = new Mutex(true, SingleInstanceName, out bool first);
        if (!first)
        {
            // The launcher is open: it handles the link (sent off the UI thread, so a busy launcher can't hang this one).
            if (link != null && SendLink(link))
            {
                Shutdown();
                return;
            }
            MessageBox.Show(Loc.T("The Void Crew Mod Launcher is already open."), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths paths = AppPaths.FromEnvironment(dataDir);
        paths.EnsureCreated();
        AppLog.Init(paths.LauncherLog);
        AppLog.Info($"Data folder: {paths.Root}");
        // Again, now that there is a log: a broken translation is noted in it.
        Loc.Use(LauncherSettings.ReadLanguage(paths));
        AppLog.Info($"Language: {Loc.Current}");
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task error", args.Exception);
            args.SetObserved();
        };

        try
        {
            _service = new LauncherService(paths);
        }
        catch (IOException ex)
        {
            // settings.json or library.json can't be read right now (another program has it open). Starting anyway
            // would mean starting from empty files and saving them over the player's.
            AppLog.Error("Could not read the launcher's files", ex);
            MessageBox.Show(Loc.F("The launcher can't read its files right now:\n{0}\n\nClose any program that might have them open, then start the launcher again.", ex.Message),
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        _service.Start();
        var dialogs = new Dialogs();
        var model = new MainViewModel(_service, dialogs) { LauncherArguments = LauncherArguments(args) };
        _model = model;
        var window = new MainWindow { DataContext = model };
        dialogs.Owner = window;
        MainWindow = window;
        window.Show();
        _windowShown = true;
        _links = new LinkPipe();
        _links.Start(received => Dispatcher.BeginInvoke(() => _ = model.HandleLinkAsync(received)));
        RegisterLinks(locations);
        _ = model.StartAsync(Argument(args, Installer.UpdatedArgument), link);
    }

    /// <summary>The launcher restarting itself (for a new language) passes its process id after this: wait until it's gone.</summary>
    private const string WaitForArgument = "--wait-for";

    /// <summary>Starts the launcher again with <paramref name="arguments"/> and closes this one.</summary>
    public static void Restart(IEnumerable<string> arguments)
    {
        if (Environment.ProcessPath is not { } exe) return;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(WaitForArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            AppLog.Error("Restarting the launcher failed", e);
            return;
        }
        AppLog.Info("Restarting the launcher.");
        Current?.Shutdown();
    }

    /// <summary>A restart waits for the launcher it replaces to close (it holds the one-launcher lock until then).</summary>
    private static void WaitForRestartedLauncher(string[] args)
    {
        if (!int.TryParse(Argument(args, WaitForArgument), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id)) return;
        try
        {
            using Process old = Process.GetProcessById(id);
            old.WaitForExit(20_000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It has already closed.
        }
    }

    /// <summary>The marker before a link the launcher passes to itself when it restarts before handling it (see MainViewModel.RestartArguments).</summary>
    public const string LinkArgument = "--link";

    /// <summary>
    /// A start by a voidcrewmod:// link has the link and nothing else: a link handler that doesn't quote it can split one
    /// link into several arguments, so every switch is ignored then. Only the launcher's own restart (after a
    /// self-update) passes a link along with switches, after <see cref="LinkArgument"/>.
    /// </summary>
    private static string[] StartArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith(LauncherLinks.Scheme + ":", StringComparison.OrdinalIgnoreCase)) continue;
            if (i > 0 && args[i - 1].Equals(LinkArgument, StringComparison.OrdinalIgnoreCase)) continue;
            return new[] { args[i] };
        }
        return args;
    }

    /// <summary>Gives the link to the open launcher; false when it doesn't take it within a few seconds.</summary>
    private static bool SendLink(string link)
    {
        try
        {
            Task<bool> send = Task.Run(() => LinkPipe.SendAsync(link));
            return send.Wait(TimeSpan.FromSeconds(5)) && send.Result;
        }
        catch (AggregateException)
        {
            return false;
        }
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

    /// <summary>
    /// The options a restarted launcher keeps (after a self-update or a language change). A link it was started with goes
    /// along only while it waits to be handled: MainViewModel.RestartArguments adds it.
    /// </summary>
    private static List<string> LauncherArguments(string[] args)
    {
        var kept = new List<string>();
        if (args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase))) kept.Add("--portable");
        if (Argument(args, "--data-dir") is { } dataDir) kept.AddRange(new[] { "--data-dir", dataDir });
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
            MessageBox.Show(Loc.F("The launcher couldn't update itself ({0}).\n\nIt opens as it was. The new version is on {1}.", error.Message, AppInfo.RepositoryUrl + "/releases/latest"),
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
        // A lobby name typed a moment ago is still waiting to be saved.
        _model?.SaveWaitingSettings();
        _links?.Dispose();
        _service?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Set once the main window is up: an error before that leaves no window to close the launcher with.</summary>
    private bool _windowShown;

    /// <summary>An error message is open; errors meanwhile only go to the log.</summary>
    private bool _showingError;

    /// <summary>The last error shown, and when its message was closed: the same error again soon after is only logged.</summary>
    private string? _lastError;

    private DateTime _lastErrorClosedUtc;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unexpected error", e.Exception);
        e.Handled = true;
        // One message at a time: something that fails every second (a timer) would otherwise stack them up.
        bool repeat = e.Exception.Message == _lastError && DateTime.UtcNow - _lastErrorClosedUtc < TimeSpan.FromSeconds(30);
        if (!_showingError && !repeat)
        {
            _showingError = true;
            try
            {
                MessageBox.Show(Loc.F("Something went wrong:\n\n{0}\n\nDetails are in the launcher's log (SETTINGS > Launcher log).", e.Exception.Message), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _showingError = false;
                _lastError = e.Exception.Message;
                _lastErrorClosedUtc = DateTime.UtcNow;
            }
        }
        // Start-up stopped before the window came up: with no window to close, the launcher would stay running unseen
        // (and keep "already open" for every later start).
        if (!_windowShown) Shutdown();
    }

    /// <summary>Installs this exe and starts the installed copy. False when it should run from here instead.</summary>
    private static bool RunInstall(InstallLocations locations)
    {
        if (Installer.InstalledCopyIsRunning(locations))
        {
            MessageBox.Show(Loc.T("The Void Crew Mod Launcher is open. Close it, then run this again to update it."), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        // Say what changes on the PC before anything does (a test install, VCML_INSTALL_ROOT, goes ahead so it can run unattended).
        if (!InstallLocations.IsTestInstall && !new Dialogs().Confirm(Loc.T("Install the Void Crew Mod Launcher"),
                Loc.F("The launcher installs for your Windows user in\n{0}\n\nwith shortcuts on the desktop and in the Start menu, and opens voidcrewmod:// links from mod websites. Remove it any time from Windows' Installed apps.",
                    locations.InstallDirectory),
                Loc.T("INSTALL")))
        {
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
            MessageBox.Show(Loc.F("The launcher couldn't install itself ({0}). It will run from where it is instead.", error.Message), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <param name="launcherOpen">The launcher is running (it holds the one-launcher lock): nothing is removed until it's closed.</param>
    private static void RunUninstall(InstallLocations locations, string? dataDir, bool launcherOpen)
    {
        var dialogs = new Dialogs();
        // An open launcher or a running game has the launcher's files (and the loader in the game folder) in use: the
        // uninstall would leave half of them behind, so it asks for them to be closed first.
        if (launcherOpen)
        {
            dialogs.Info(Loc.T("Uninstall"), Loc.T("The Void Crew Mod Launcher is open. Close it, then uninstall it again."));
            return;
        }
        if (Core.Game.GameProcess.IsRunning())
        {
            dialogs.Info(Loc.T("Close Void Crew first"), Loc.T("Void Crew has the loader open while it runs."));
            return;
        }
        if (!dialogs.Confirm(Loc.T("Uninstall"), Loc.T("Remove the Void Crew Mod Launcher from this PC?"), Loc.T("UNINSTALL"))) return;
        AppPaths paths = AppPaths.FromEnvironment(dataDir);
        // KEEP THEM is the Enter key's, so pressing Enter through the questions never deletes the player's mods.
        bool removeData = Directory.Exists(paths.Root) && dialogs.Ask(Loc.T("Downloaded mods and settings"),
            Loc.T("Delete the launcher's downloaded mods and settings too?"),
            new DialogButton(Loc.T("DELETE THEM"), ButtonKind.Cancel), new DialogButton(Loc.T("KEEP THEM"), IsDefault: true)) == 0;
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
        try
        {
            Installer.Uninstall(locations, game, removeData ? paths.Root : null);
        }
        catch (Exception error)
        {
            dialogs.Error(Loc.T("Uninstall"), Loc.F("The launcher couldn't remove everything: {0}", error.Message));
            return;
        }
        // What the uninstall couldn't delete (a file another program has open) is said, not passed over.
        List<string> left = removeData ? LeftOver(paths) : new List<string>();
        if (left.Count > 0)
        {
            dialogs.Error(Loc.T("Uninstalled"), Loc.F("The Void Crew Mod Launcher is removed, but these couldn't be deleted. Close any program that might have them open and delete them yourself:\n{0}",
                string.Join("\n", left.Take(10))));
            return;
        }
        dialogs.Info(Loc.T("Uninstalled"), removeData
            ? Loc.T("The Void Crew Mod Launcher is removed.")
            : Loc.F("The Void Crew Mod Launcher is removed. Your mods and settings are still in {0}.", paths.Root));
    }

    /// <summary>The launcher's own files and folders still in its data folder (AppPaths.DeleteOwnFiles deletes these).</summary>
    private static List<string> LeftOver(AppPaths paths) =>
        new[] { paths.SettingsFile, paths.LibraryFile, paths.Store, paths.Cache, paths.Runtime, paths.Logs }
            .Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

    private static string? Argument(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
