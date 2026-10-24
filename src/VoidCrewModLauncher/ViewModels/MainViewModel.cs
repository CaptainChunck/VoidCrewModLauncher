using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Setup;
using VoidCrewModLauncher.Infrastructure;
using VoidCrewModLauncher.Protocol;
using VoidCrewModLauncher.Views;

namespace VoidCrewModLauncher.ViewModels;

/// <summary>Where Void Crew is, as the top bar and the server browser show it.</summary>
public enum GameState
{
    NotRunning,
    Starting,

    /// <summary>Running, but not started by the launcher (or its bridge didn't load).</summary>
    RunningOutside,

    Booting,
    Menu,
    Loading,
    Hub,
    Mission,
}

/// <summary>The window: pages, the game's state, the status bar, and the PLAY / CLOSE GAME buttons.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer? _timer;
    private readonly Dispatcher _dispatcher;
    private int _busy;

    public MainViewModel(LauncherService service, Dialogs dialogs, bool preview = false)
    {
        Service = service;
        Dialogs = dialogs;
        Icons = new Icons(service.Thunderstore);
        _dispatcher = Dispatcher.CurrentDispatcher;
        Browser = new BrowserViewModel(this);
        Mods = new ModsViewModel(this);
        Host = new HostViewModel(this);
        Settings = new SettingsViewModel(this);
        currentPage = Browser;

        if (preview) return;
        service.Status += s => OnUi(() => SetStatus(s.Message, s.Fraction));
        service.PackagesChanged += () => OnUi(() =>
        {
            Mods.RefreshOnline();
            Mods.RefreshInstalled();
            Browser.RefreshLobbies();
            OnPropertyChanged(nameof(ThunderstoreText));
        });
        service.PackagesFreshened += ids => OnUi(() => OnPackagesFreshened(ids));
        service.Library.Changed += () => OnUi(() =>
        {
            Mods.RefreshInstalled();
            Host.RefreshMods();
            Browser.RefreshLobbies();
        });
        service.Bridge.Connected += hello => OnUi(() => OnGameConnected(hello));
        service.Bridge.Disconnected += () => OnUi(UpdateGameState);
        service.Bridge.StateChanged += state => OnUi(() =>
        {
            Browser.OnState(state);
            UpdateGameState();
            Browser.NotifyCommands();
        });
        service.Bridge.RoomsChanged += rooms => OnUi(() => Browser.OnRooms(rooms));
        service.Bridge.JoinResult += result => OnUi(() => Browser.OnJoinResult(result));
        service.Bridge.HostResult += result => OnUi(() => Host.OnHostResult(result));
        service.Bridge.GameLeftRoom += left => OnUi(() => Browser.OnGameLeftRoom(left));

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateGameState(), _dispatcher);
        _timer.Start();
        _playingTimer = new DispatcherTimer(PlayingCheckInterval, DispatcherPriority.Background, async (_, _) => await CheckWhilePlayingAsync(), _dispatcher);
        _playingTimer.Start();
    }

    /// <summary>How often the mods of a running game are checked for updates.</summary>
    private static readonly TimeSpan PlayingCheckInterval = TimeSpan.FromMinutes(15);

    private readonly DispatcherTimer? _playingTimer;
    private bool _checkingWhilePlaying;

    /// <summary>Packages freshened since the lists were last drawn again.</summary>
    private readonly HashSet<string> _freshened = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _freshenedTimer;

    /// <summary>
    /// Newer data for some packages: the lists are drawn again a moment later, once for everything that arrived meanwhile
    /// (the server browser looks up many lobbies' mods at once).
    /// </summary>
    private void OnPackagesFreshened(IReadOnlyCollection<string> ids)
    {
        _freshened.UnionWith(ids);
        if (_freshenedTimer == null)
        {
            _freshenedTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            _freshenedTimer.Tick += (_, _) =>
            {
                _freshenedTimer?.Stop();
                List<string> all = _freshened.ToList();
                _freshened.Clear();
                Mods.OnPackagesFreshened(all);
                Browser.RefreshLobbies();
            };
        }
        if (!_freshenedTimer.IsEnabled) _freshenedTimer.Start();
    }

    public LauncherService Service { get; }
    public Dialogs Dialogs { get; }

    /// <summary>The launcher's own command-line options, kept when it restarts after a self-update.</summary>
    public IReadOnlyList<string> LauncherArguments { get; init; } = Array.Empty<string>();
    public Icons Icons { get; }
    public BrowserViewModel Browser { get; }
    public ModsViewModel Mods { get; }
    public HostViewModel Host { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private object currentPage;
    [ObservableProperty] private string currentPageName = "browser";
    [ObservableProperty] private string statusText = Loc.T("Ready.");
    [ObservableProperty] private double statusProgress;
    [ObservableProperty] private bool statusProgressVisible;
    [ObservableProperty] private GameState gameState;
    [ObservableProperty] private string gameStateText = Loc.T("NOT RUNNING");
    [ObservableProperty] private bool isBusy;

    public bool GameRunning => GameState != GameState.NotRunning && GameState != GameState.Starting;

    public string ThunderstoreText
    {
        get
        {
            int count = Service.Index.All.Count;
            if (count == 0) return Loc.T("THUNDERSTORE: NOT LOADED");
            return Service.Thunderstore.IsOffline ? Loc.F("THUNDERSTORE: OFFLINE ({0} MODS, CACHED)", count) : Loc.F("THUNDERSTORE: {0} MODS", count);
        }
    }

    public void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void SetStatus(string text, double? fraction = null)
    {
        StatusText = text;
        StatusProgressVisible = fraction != null && fraction < 1;
        StatusProgress = fraction ?? 0;
    }

    [RelayCommand]
    public void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "mods" => Mods,
            "host" => Host,
            "settings" => Settings,
            _ => Browser,
        };
        CurrentPageName = page is "mods" or "host" or "settings" ? page : "browser";
        if (CurrentPage == Settings) Settings.Refresh();
        if (CurrentPage == Host) Host.RefreshMods();
    }

    partial void OnGameStateChanged(GameState value)
    {
        OnPropertyChanged(nameof(GameRunning));
        Browser.OnGameStateChanged();
        PlayCommand.NotifyCanExecuteChanged();
        CloseGameCommand.NotifyCanExecuteChanged();
        FocusGameCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        CloseGameCommand.NotifyCanExecuteChanged();
        Browser.NotifyCommands();
        Host.NotifyCommands();
    }

    /// <summary>The launch for which the launcher was put in front of the game's main menu.</summary>
    private DateTime? _frontedLaunch;

    /// <summary>The launch whose game process has been seen; once it has, the process going away means the game closed.</summary>
    private DateTime? _seenLaunch;

    /// <summary>The game's process once found: watched with HasExited instead of looking it up by name every second.</summary>
    private System.Diagnostics.Process? _gameProcess;

    private DateTime _gameScanUtc;

    /// <summary>How often the processes are searched for the game while it isn't known to run.</summary>
    private static readonly TimeSpan GameScanInterval = TimeSpan.FromSeconds(3);

    private bool IsGameRunning()
    {
        if (_gameProcess != null)
        {
            try
            {
                if (!_gameProcess.HasExited) return true;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It can't be watched (it runs as administrator): look it up by name instead.
                _gameProcess.Dispose();
                _gameProcess = null;
                return GameProcess.IsRunning();
            }
            _gameProcess.Dispose();
            _gameProcess = null;
        }
        // A connected bridge means the game runs: look now rather than show it closed for a moment.
        if (DateTime.UtcNow - _gameScanUtc < GameScanInterval && !Service.Bridge.IsConnected) return false;
        _gameScanUtc = DateTime.UtcNow;
        _gameProcess = GameProcess.Find();
        return _gameProcess != null;
    }

    private DateTime? _checkedLaunch;

    /// <summary>
    /// The game the launcher started runs but never connected: writes what the launcher can see about why to its log
    /// (once per launch, off the UI thread), so a player can send it.
    /// </summary>
    private void CheckLaunch(DateTime launch)
    {
        _checkedLaunch = launch;
        SetStatus(Loc.T("Void Crew started without the launcher's mods. Details are in the launcher log (SETTINGS, LAUNCHER LOG)."));
        string? game = Service.GameDirectory;
        _ = Task.Run(() =>
        {
            try
            {
                AppLog.Warn("Void Crew runs but never connected to the launcher. What the launcher sees:");
                foreach (string line in LaunchCheck.Run(Service.Paths, game, launch)) AppLog.Warn("  " + line);
            }
            catch (Exception e)
            {
                AppLog.Error("The launch check failed", e);
            }
        });
    }

    public void UpdateGameState()
    {
        bool running = IsGameRunning();
        bool recentlyLaunched = Service.LastLaunchUtc is { } launched && DateTime.UtcNow - launched < TimeSpan.FromMinutes(3);
        if (running) _seenLaunch = Service.LastLaunchUtc;
        bool waitingForProcess = _seenLaunch != Service.LastLaunchUtc && recentlyLaunched && DateTime.UtcNow - Service.LastLaunchUtc!.Value < TimeSpan.FromSeconds(150);
        StateMessage? state = Service.Bridge.State;
        GameState next;
        // Steam may need a while to start (and log in) before the game's process appears.
        if (!running) next = waitingForProcess ? GameState.Starting : GameState.NotRunning;
        else if (!Service.Bridge.IsConnected)
        {
            next = recentlyLaunched ? GameState.Starting : GameState.RunningOutside;
            if (next == GameState.RunningOutside && Service.LastLaunchUtc is { } launch && _checkedLaunch != launch && _seenLaunch == launch) CheckLaunch(launch);
        }
        else
        {
            next = state?.Phase switch
            {
                "menu" => GameState.Menu,
                "hub" => GameState.Hub,
                "mission" => GameState.Mission,
                "boot" => GameState.Booting,
                null => GameState.Booting,
                _ => GameState.Loading,
            };
        }
        // The game takes the front again when its main menu comes up: put the launcher back on top once per launch.
        if (next == GameState.Menu && GameState != GameState.Menu && Service.LastLaunchMode == LaunchMode.Browse
            && Service.Settings.MinimizeGameWhenBrowsing && _frontedLaunch != Service.LastLaunchUtc)
        {
            _frontedLaunch = Service.LastLaunchUtc;
            ShowWindow();
        }
        GameState = next;
        GameStateText = next switch
        {
            GameState.NotRunning => Loc.T("NOT RUNNING"),
            GameState.Starting => Loc.T("STARTING"),
            GameState.RunningOutside => Loc.T("RUNNING WITHOUT THE LAUNCHER"),
            GameState.Booting => Loc.T("LOADING"),
            GameState.Menu => state?.InLobby == true ? Loc.F("MAIN MENU · {0}", RegionName(state.Region)) : Loc.T("MAIN MENU · CONNECTING"),
            GameState.Hub => state?.IsHost == true ? Loc.F("HOSTING · {0}/{1}", state.Players, state.MaxPlayers) : Loc.F("IN A LOBBY · {0}/{1}", state?.Players, state?.MaxPlayers),
            GameState.Mission => Loc.T("IN A MISSION"),
            _ => Loc.T("LOADING"),
        };
    }

    public static string RegionName(string? code) => code?.ToLowerInvariant() switch
    {
        "eu" => Loc.T("EUROPE"),
        "us" => Loc.T("USA EAST"),
        "usw" => Loc.T("USA WEST"),
        "asia" => Loc.T("ASIA"),
        "jp" => Loc.T("JAPAN"),
        "au" => Loc.T("AUSTRALIA"),
        "sa" => Loc.T("SOUTH AMERICA"),
        "kr" => Loc.T("KOREA"),
        "in" => Loc.T("INDIA"),
        "cae" => Loc.T("CANADA"),
        "za" => Loc.T("SOUTH AFRICA"),
        "tr" => Loc.T("TURKEY"),
        "uae" => Loc.T("UAE"),
        "ussc" => Loc.T("USA SOUTH"),
        "hk" => Loc.T("HONG KONG"),
        "cn" => Loc.T("CHINA"),
        "ru" => Loc.T("RUSSIA"),
        "rue" => Loc.T("RUSSIA EAST"),
        null or "" => Loc.T("NO REGION"),
        _ => code.ToUpperInvariant(),
    };

    private void OnGameConnected(HelloMessage hello)
    {
        UpdateGameState();
        // Never minimise the game: Void Crew loses its graphics device (and crashes) when minimised while it loads.
        if (Service.LastLaunchMode == LaunchMode.Browse && Service.Settings.MinimizeGameWhenBrowsing) ShowWindow();
        if (Service.CurrentLaunch == null)
        {
            SetStatus(Loc.T("Void Crew is connected, but it wasn't started by this launcher session; its mod list is unknown."));
        }
    }

    // ---------------------------------------------------------------- PLAY / CLOSE / FOCUS

    private bool CanPlay() => !IsBusy && GameState == GameState.NotRunning;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task Play() => RunAsync(Loc.T("Starting Void Crew"), () => Service.LaunchAsync(LaunchMode.Play));

    private bool CanCloseGame() => !IsBusy && GameRunning;

    [RelayCommand(CanExecute = nameof(CanCloseGame))]
    private async Task CloseGame()
    {
        if (!Dialogs.Confirm(Loc.T("Close Void Crew"), Loc.T("Close the game now? Anything you haven't saved in the game is lost."), Loc.T("CLOSE GAME"))) return;
        await RunAsync(Loc.T("Closing Void Crew"), async () =>
        {
            if (!await CloseGameOrAskAsync()) SetStatus(Loc.T("Void Crew is still running."));
        });
    }

    /// <summary>voidcrewmod.com in the browser.</summary>
    [RelayCommand]
    private void OpenWebsite() => Format.Open(AppInfo.WebsiteUrl);

    [RelayCommand(CanExecute = nameof(GameRunning))]
    private void FocusGame()
    {
        using var game = GameProcess.Find();
        if (game != null) GameProcess.Focus(game.Id);
    }

    /// <summary>Closes the game and waits; if it won't close, offers to end it. True when it's gone.</summary>
    public async Task<bool> CloseGameOrAskAsync()
    {
        if (await Service.CloseGameAsync(TimeSpan.FromSeconds(25))) return true;
        int choice = Dialogs.Ask(Loc.T("Void Crew is still running"),
            Loc.T("Void Crew didn't close. It may be showing a question or still saving.\n\nForce it to close? Anything not saved in the game is lost."),
            new DialogButton(Loc.T("FORCE CLOSE"), ButtonKind.Cancel), new DialogButton(Loc.T("WAIT")));
        if (choice != 0) return await GameProcess.WaitForExitAsync(TimeSpan.FromSeconds(30));
        GameProcess.Kill();
        return await GameProcess.WaitForExitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>False, with a note in the status bar, while a long task runs (quick actions that change files check it).</summary>
    public bool CheckIdle(string what)
    {
        if (!IsBusy) return true;
        SetStatus(Loc.F("{0}: wait until the launcher has finished what it's doing, then try again.", what));
        return false;
    }

    /// <summary>
    /// Runs a long task with the busy flag set, reporting errors in a pop-up. Asks once for permission to add BepInEx's
    /// loader to the game folder when a launch needs it, then tries again.
    /// </summary>
    public async Task<bool> RunAsync(string what, Func<Task> work)
    {
        if (!CheckIdle(what) || !TryClaimBusy()) return false;
        string working = Loc.F("{0}...", what);
        try
        {
            SetStatus(working);
            try
            {
                await work();
            }
            catch (DoorstopConsentNeededException needed)
            {
                if (!Dialogs.Confirm(Loc.T("Add BepInEx's loader"),
                        Loc.F("To load mods, Void Crew needs BepInEx's loader next to its exe: two small files (winhttp.dll and doorstop_config.ini) in\n{0}\n\nThe launcher adds them switched off, so starting Void Crew from Steam stays unmodded. Everything else (BepInEx and your mods) stays in the launcher's own folder. You can remove them on the SETTINGS tab.",
                            needed.GameDirectory),
                        Loc.T("ADD AND CONTINUE")))
                {
                    SetStatus(Loc.T("Cancelled: the game folder was left as it is."));
                    return false;
                }
                Service.Settings.DoorstopConsent = true;
                Service.SaveSettings();
                await work();
            }
            if (StatusText == working) SetStatus(Loc.T("Done."));
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus(Loc.T("Cancelled."));
            return false;
        }
        catch (Exception e)
        {
            AppLog.Error(what + " failed", e);
            SetStatus(Loc.F("{0} failed.", what));
            Dialogs.Error(Loc.F("{0} failed", what), e.Message);
            return false;
        }
        finally
        {
            ReleaseBusy();
            UpdateGameState();
        }
    }

    private bool TryClaimBusy()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        IsBusy = true;
        return true;
    }

    private void ReleaseBusy()
    {
        IsBusy = false;
        Interlocked.Exchange(ref _busy, 0);
    }

    /// <summary>Waits, however long it takes, until no long task runs.</summary>
    private async Task WaitUntilIdleAsync()
    {
        while (IsBusy) await Task.Delay(250);
    }

    /// <summary>Set when the launcher closes for its self-update (the window closes without asking).</summary>
    public bool ClosingForUpdate { get; private set; }

    /// <summary>
    /// Start-up: a new launcher from GitHub first (it restarts the launcher), then Thunderstore's list (from the cache when
    /// it's fresh) and updates for the installed mods.
    /// </summary>
    /// <param name="updatedFrom">The version this launcher was updated from, when it just updated itself.</param>
    /// <param name="link">A voidcrewmod:// link the launcher was started with, handled once the mod list is loaded.</param>
    public async Task StartAsync(string? updatedFrom = null, string? link = null)
    {
        // Links wait for this; whatever fails here, they mustn't wait forever.
        try
        {
            try
            {
                if (updatedFrom != null)
                {
                    Updater.CleanUp();
                    AppLog.Info($"The launcher updated itself from {updatedFrom} to {AppInfo.Version}.");
                }
                else if (Service.Settings.AutoUpdateLauncher && await UpdateLauncherAsync(manual: false))
                {
                    return;
                }
            }
            catch (Exception e)
            {
                AppLog.Error("Updating the launcher failed", e);
            }

            string? updatedText = updatedFrom != null ? Loc.F("Updated the launcher to {0}.", AppInfo.Version) : null;
            try
            {
                SetStatus(Loc.T("Loading Thunderstore's Void Crew mods..."));
                await Service.RefreshPackagesAsync(false);
                string? modsText = Service.Settings.AutoUpdateMods ? await UpdateModsAsync() : null;
                string? githubText = await AddMissingGitHubModsAsync();
                if (githubText != null) modsText = modsText == null ? githubText : modsText + " " + githubText;
                int updates = Service.Mods.Updates().Count;
                string text = modsText ?? (updates > 0 ? Loc.F("{0} of your mods can be updated (MOD MANAGER).", updates) : Loc.T("Ready."));
                SetStatus(updatedText != null ? Loc.F("{0} {1}", updatedText, text) : text);
            }
            catch (Exception e)
            {
                AppLog.Error("Loading Thunderstore's mod list failed", e);
                string text = Loc.T("Thunderstore couldn't be reached. Mods you have still work; try REFRESH on the MODS tab later.");
                SetStatus(updatedText != null ? Loc.F("{0} {1}", updatedText, text) : text);
            }
        }
        finally
        {
            _started.TrySetResult();
        }
        if (link != null) await HandleLinkAsync(link);
        if (Service.GameDirectory == null)
        {
            Navigate("settings");
            Dialogs.Info(Loc.T("Void Crew not found"), Loc.T("The launcher couldn't find Void Crew through Steam. Choose its folder on the SETTINGS tab (the one with Void Crew.exe)."));
        }
    }

    /// <summary>Set once start-up has loaded the mod list.</summary>
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A voidcrewmod:// link from a website: brings the launcher up and, for an install, shows the mod and installs it
    /// (with what it needs) once the player agrees. Nothing is installed without that.
    /// </summary>
    public async Task HandleLinkAsync(string text)
    {
        AppLog.Info($"Link: {text}");
        ShowWindow();
        // One link at a time: a second one waits until the first is installed or declined.
        await _links.WaitAsync();
        try
        {
            await HandleOneLinkAsync(text);
        }
        finally
        {
            _links.Release();
        }
    }

    private readonly SemaphoreSlim _links = new(1, 1);

    private async Task HandleOneLinkAsync(string text)
    {
        LauncherLink? link = LauncherLinks.Parse(text);
        if (link == null)
        {
            Dialogs.Error(Loc.T("Link not understood"), Loc.T("This link isn't one the Void Crew Mod Launcher knows. Update the launcher (SETTINGS > CHECK FOR UPDATES) and try again."));
            return;
        }
        if (link.Action != LinkAction.Install || link.ModId == null) return;

        await _started.Task;
        await WaitUntilIdleAsync();
        SetStatus(Loc.F("Looking up {0}...", link.ModId));
        try
        {
            await Service.FreshenAsync(new[] { link.ModId });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Warn($"Looking up {link.ModId} failed: {e.Message}");
        }
        Core.Thunderstore.TsPackage? package = Service.Index.Find(link.ModId);
        if (package == null)
        {
            SetStatus(Loc.T("Ready."));
            Dialogs.Error(Loc.T("Mod not found"), Loc.F("{0} isn't on Thunderstore's Void Crew list or GitHub, so the launcher can't install it.", link.ModId));
            return;
        }
        string version = link.Version ?? package.Latest?.VersionNumber ?? "";
        if (link.Version != null && package.Version(link.Version) == null)
        {
            SetStatus(Loc.T("Ready."));
            Dialogs.Error(Loc.T("Version not found"), Loc.F("{0} {1} isn't on Thunderstore.", package.DisplayName, link.Version));
            return;
        }
        InstalledMod? mine = Service.Library.Find(package.FullName);
        Navigate("mods");
        Mods.ShowBrowse = false;
        if (mine != null && Core.Thunderstore.VersionNumber.Same(mine.Version, version))
        {
            if (mine.Enabled)
            {
                SetStatus(Loc.F("{0} {1} is installed and on.", package.DisplayName, version));
                return;
            }
            if (!Dialogs.Confirm(Loc.T("Turn on mod"), Loc.F("A link asked the launcher to turn on {0} {1} by {2}, which you have switched off.", package.DisplayName, version, package.Author), Loc.T("TURN ON")))
            {
                SetStatus(Loc.T("Ready."));
                return;
            }
            await WaitUntilIdleAsync();
            if (await RunAsync(Loc.F("Turning on {0}", mine.DisplayName), () => Service.SetModEnabledAsync(mine.Id, true, null)))
            {
                SetStatus(Loc.F("{0} {1} is installed and on.", package.DisplayName, version));
            }
            return;
        }

        string needs = string.Join(", ", Service.GitHubMods.NeededBy(package.FullName).Where(m => Service.CopyOf(m) == null).Select(m => m.Name));
        string message = (needs.Length > 0, mine != null) switch
        {
            (true, true) => Loc.F("A link asked the launcher to install {0} {1} by {2}, with {3} which it needs (you have {4}).", package.DisplayName, version, package.Author, needs, mine!.Version),
            (true, false) => Loc.F("A link asked the launcher to install {0} {1} by {2}, with {3} which it needs.", package.DisplayName, version, package.Author, needs),
            (false, true) => Loc.F("A link asked the launcher to install {0} {1} by {2} (you have {3}).", package.DisplayName, version, package.Author, mine!.Version),
            (false, false) => Loc.F("A link asked the launcher to install {0} {1} by {2}.", package.DisplayName, version, package.Author),
        };
        if (!Dialogs.Confirm(mine != null ? Loc.T("Update mod") : Loc.T("Install mod"), message, mine != null ? Loc.T("UPDATE") : Loc.T("INSTALL")))
        {
            SetStatus(Loc.T("Ready."));
            return;
        }
        // Something may have started while the question was up.
        await WaitUntilIdleAsync();
        var progress = new SyncProgress<InstallStatus>(s => OnUi(() => SetStatus(s.Message, s.Fraction)));
        bool ok = await RunAsync(Loc.F("Installing {0}", package.DisplayName), async () =>
        {
            if (link.Version != null)
            {
                await Service.Mods.InstallAsync(package.FullName, link.Version, progress);
                await Service.AddNeededGitHubModsAsync(package.FullName, progress);
            }
            else
            {
                await Service.InstallModAsync(package.FullName, progress);
            }
        });
        if (ok) SetStatus(Loc.F("{0} {1} is installed and on. It loads the next time you play or host.", package.DisplayName, Service.Library.Find(package.FullName)?.Version ?? version));
    }

    /// <summary>Brings the launcher's window to the front (a link was clicked in a browser).</summary>
    private static void ShowWindow()
    {
        if (Application.Current?.MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>
    /// Updates every installed Thunderstore mod that has a newer version (checked with Thunderstore first). Returns the
    /// status line for what changed, or null when every mod was up to date.
    /// </summary>
    public async Task<string?> UpdateModsAsync()
    {
        try
        {
            await Service.FreshenAsync(Service.Library.Mods.Where(m => m.Source is ModSource.Thunderstore or ModSource.GitHub).Select(m => m.Id));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Warn($"Checking the installed mods on Thunderstore failed: {e.Message}");
        }
        if (Service.Mods.Updates().Count == 0)
        {
            await TellGameAboutUpdatesAsync();
            return null;
        }

        (List<string> Updated, List<string> Failed) result = (new List<string>(), new List<string>());
        await RunAsync(Loc.T("Updating mods"), async () =>
        {
            result = await Service.UpdateModsAsync(new SyncProgress<InstallStatus>(s => OnUi(() => SetStatus(s.Message, s.Fraction))));
        });
        string text = "";
        if (result.Updated.Count > 0)
        {
            text = result.Updated.Count == 1
                ? Loc.F("Updated {0} mod: {1}.", result.Updated.Count, string.Join(", ", result.Updated))
                : Loc.F("Updated {0} mods: {1}.", result.Updated.Count, string.Join(", ", result.Updated));
        }
        if (result.Failed.Count > 0)
        {
            string failed = Loc.F("Couldn't update {0} (see the launcher log).", string.Join(", ", result.Failed));
            text = text.Length > 0 ? Loc.F("{0} {1}", text, failed) : failed;
        }
        if (text.Length > 0) SetStatus(text);
        await TellGameAboutUpdatesAsync();
        return text.Length > 0 ? text : null;
    }

    /// <summary>Installs GitHub-only mods your turned-on mods need and don't have yet (UUVR for the VR UI Fix).</summary>
    private async Task<string?> AddMissingGitHubModsAsync()
    {
        List<string> added = new();
        await RunAsync(Loc.T("Installing what your mods need"), async () =>
        {
            added = await Service.AddMissingGitHubModsAsync(new SyncProgress<InstallStatus>(s => OnUi(() => SetStatus(s.Message, s.Fraction))));
        });
        return added.Count > 0 ? Loc.F("Installed from GitHub: {0}.", string.Join(", ", added)) : null;
    }

    /// <summary>
    /// While Void Crew runs from the launcher: looks for updates of the mods it was started with, installs them when mods
    /// update automatically, and tells the player in the game. Every <see cref="PlayingCheckInterval"/>.
    /// </summary>
    private async Task CheckWhilePlayingAsync()
    {
        if (_checkingWhilePlaying || IsBusy || !Service.Bridge.IsConnected || Service.CurrentLaunch?.Mods is not { Count: > 0 } mods) return;
        _checkingWhilePlaying = true;
        try
        {
            if (Service.Settings.AutoUpdateMods)
            {
                await UpdateModsAsync();
            }
            else
            {
                await Service.FreshenAsync(mods.Where(m => !m.Local).Select(m => m.Id));
                await TellGameAboutUpdatesAsync();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Warn($"Checking the running game's mods for updates failed: {e.Message}");
        }
        finally
        {
            _checkingWhilePlaying = false;
        }
    }

    /// <summary>
    /// Tells the player in the game (one quiet line in their own chat) about updates that came out for its mods since it
    /// started, so they know to restart it from the launcher. Each update is told once.
    /// </summary>
    private async Task TellGameAboutUpdatesAsync()
    {
        if (!Service.Bridge.IsConnected) return;
        List<(string Name, string Version, bool Ready)> news = Service.NewUpdatesForRunningGame();
        if (news.Count == 0) return;
        string names = string.Join(", ", news.Select(n => $"{n.Name} {n.Version}"));
        bool one = news.Count == 1;
        string text = (news.All(n => n.Ready), one) switch
        {
            (true, true) => Loc.F("Update ready: {0}. Restart Void Crew from the Mod Launcher to play with it.", names),
            (true, false) => Loc.F("Update ready: {0}. Restart Void Crew from the Mod Launcher to play with them.", names),
            (false, true) => Loc.F("Update out: {0}. Update it in the Mod Launcher, then restart Void Crew from there.", names),
            (false, false) => Loc.F("Update out: {0}. Update them in the Mod Launcher, then restart Void Crew from there.", names),
        };
        if (await Service.Bridge.SendAsync(new NotifyCommand { Text = text })) AppLog.Info("Told the game: " + text);
    }

    /// <summary>
    /// The launcher's self-update: when GitHub has a newer release, downloads it with a window saying so, starts it to
    /// install itself and closes this launcher. True when the launcher is closing for the update.
    /// </summary>
    /// <param name="manual">From CHECK FOR UPDATES: says when there's nothing new, and tries again even right after a failed attempt.</param>
    public async Task<bool> UpdateLauncherAsync(bool manual)
    {
        if (!Installer.IsPublishedBuild)
        {
            if (manual) SetStatus(Loc.T("This is a developer build; it doesn't update itself."));
            return false;
        }
        if (manual && !CheckIdle(Loc.T("Checking for updates"))) return false;
        using var updater = new Updater();
        LauncherRelease? release;
        try
        {
            if (manual) SetStatus(Loc.T("Checking GitHub for a new launcher..."));
            release = await updater.CheckAsync(AppInfo.Version);
        }
        catch (Exception e)
        {
            AppLog.Warn($"Checking GitHub for a new launcher failed: {e.Message}");
            if (manual) SetStatus(Loc.T("GitHub couldn't be reached to check for a new launcher."));
            return false;
        }
        if (release == null)
        {
            if (manual) SetStatus(Loc.F("The launcher is up to date ({0}).", AppInfo.Version));
            return false;
        }

        LauncherSettings settings = Service.Settings;
        if (!manual && settings.UpdateTriedVersion == release.Version && settings.UpdateTriedUtc is { } tried && DateTime.UtcNow - tried < TimeSpan.FromHours(6))
        {
            AppLog.Warn($"Launcher {release.Version} was installed at {tried:u} but this is still {AppInfo.Version}; not trying again for a while.");
            SetStatus(Loc.F("Launcher {0} is out but didn't install. Try CHECK FOR UPDATES on the SETTINGS tab, or get it from GitHub.", release.Version));
            return false;
        }

        // Busy from the download window until the launcher closes: nothing else starts meanwhile.
        if (!TryClaimBusy())
        {
            if (manual) CheckIdle(Loc.T("Checking for updates"));
            return false;
        }
        try
        {
            AppLog.Info($"Updating the launcher from {AppInfo.Version} to {release.Version}.");
            string? setup = UpdateWindow.Download(Application.Current?.MainWindow, updater, release, out Exception? error);
            if (setup == null)
            {
                if (error == null || error is OperationCanceledException)
                {
                    SetStatus(Loc.F("Update to {0} cancelled. The launcher tries again next time it starts.", release.Version));
                }
                else
                {
                    AppLog.Error($"Downloading launcher {release.Version} failed", error);
                    SetStatus(Loc.F("The launcher couldn't update to {0}: {1}", release.Version, error.Message));
                }
                return false;
            }

            settings.UpdateTriedVersion = release.Version;
            settings.UpdateTriedUtc = DateTime.UtcNow;
            Service.SaveSettings();
            try
            {
                Installer.StartUpdate(setup, Environment.ProcessPath!, LauncherArguments.Concat(new[] { Installer.UpdatedArgument, AppInfo.Version }));
            }
            catch (Exception e)
            {
                AppLog.Error("Starting the launcher update failed", e);
                SetStatus(Loc.F("The launcher couldn't update to {0}: {1}", release.Version, e.Message));
                return false;
            }
            ClosingForUpdate = true;
            Application.Current?.Shutdown();
            return true;
        }
        finally
        {
            if (!ClosingForUpdate) ReleaseBusy();
        }
    }

    public static void Ui(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
