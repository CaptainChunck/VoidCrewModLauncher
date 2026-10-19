using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Infrastructure;
using VoidCrewModLauncher.Protocol;

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
    }

    public LauncherService Service { get; }
    public Dialogs Dialogs { get; }
    public Icons Icons { get; }
    public BrowserViewModel Browser { get; }
    public ModsViewModel Mods { get; }
    public HostViewModel Host { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private object currentPage;
    [ObservableProperty] private string currentPageName = "browser";
    [ObservableProperty] private string statusText = "Ready.";
    [ObservableProperty] private double statusProgress;
    [ObservableProperty] private bool statusProgressVisible;
    [ObservableProperty] private GameState gameState;
    [ObservableProperty] private string gameStateText = "NOT RUNNING";
    [ObservableProperty] private bool isBusy;

    public bool GameRunning => GameState != GameState.NotRunning && GameState != GameState.Starting;

    public string ThunderstoreText
    {
        get
        {
            int count = Service.Index.All.Count;
            if (count == 0) return "THUNDERSTORE: NOT LOADED";
            return Service.Thunderstore.IsOffline ? $"THUNDERSTORE: OFFLINE ({count} MODS, CACHED)" : $"THUNDERSTORE: {count} MODS";
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

    /// <summary>The launch whose game process has been seen; once it has, the process going away means the game closed.</summary>
    private DateTime? _seenLaunch;

    public void UpdateGameState()
    {
        bool running = GameProcess.IsRunning();
        bool recentlyLaunched = Service.LastLaunchUtc is { } launched && DateTime.UtcNow - launched < TimeSpan.FromMinutes(3);
        if (running) _seenLaunch = Service.LastLaunchUtc;
        bool waitingForProcess = _seenLaunch != Service.LastLaunchUtc && recentlyLaunched && DateTime.UtcNow - Service.LastLaunchUtc!.Value < TimeSpan.FromSeconds(150);
        StateMessage? state = Service.Bridge.State;
        GameState next;
        // Steam may need a while to start (and log in) before the game's process appears.
        if (!running) next = waitingForProcess ? GameState.Starting : GameState.NotRunning;
        else if (!Service.Bridge.IsConnected) next = recentlyLaunched ? GameState.Starting : GameState.RunningOutside;
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
        GameState = next;
        GameStateText = next switch
        {
            GameState.NotRunning => "NOT RUNNING",
            GameState.Starting => "STARTING",
            GameState.RunningOutside => "RUNNING WITHOUT THE LAUNCHER",
            GameState.Booting => "LOADING",
            GameState.Menu => state?.InLobby == true ? $"MAIN MENU · {RegionName(state.Region)}" : "MAIN MENU · CONNECTING",
            GameState.Hub => state?.IsHost == true ? $"HOSTING · {state.Players}/{state.MaxPlayers}" : $"IN A LOBBY · {state?.Players}/{state?.MaxPlayers}",
            GameState.Mission => "IN A MISSION",
            _ => "LOADING",
        };
    }

    public static string RegionName(string? code) => code?.ToLowerInvariant() switch
    {
        "eu" => "EUROPE",
        "us" => "USA EAST",
        "usw" => "USA WEST",
        "asia" => "ASIA",
        "jp" => "JAPAN",
        "au" => "AUSTRALIA",
        "sa" => "SOUTH AMERICA",
        "kr" => "KOREA",
        "in" => "INDIA",
        "cae" => "CANADA",
        "za" => "SOUTH AFRICA",
        "tr" => "TURKEY",
        "uae" => "UAE",
        "ussc" => "USA SOUTH",
        "hk" => "HONG KONG",
        "cn" => "CHINA",
        "ru" => "RUSSIA",
        "rue" => "RUSSIA EAST",
        null or "" => "NO REGION",
        _ => code.ToUpperInvariant(),
    };

    private void OnGameConnected(HelloMessage hello)
    {
        UpdateGameState();
        if (Service.LastLaunchMode == LaunchMode.Browse && Service.Settings.MinimizeGameWhenBrowsing) GameProcess.Minimize(hello.ProcessId);
        if (Service.CurrentLaunch == null)
        {
            SetStatus("Void Crew is connected, but it wasn't started by this launcher session; its mod list is unknown.");
        }
    }

    // ---------------------------------------------------------------- PLAY / CLOSE / FOCUS

    private bool CanPlay() => !IsBusy && GameState == GameState.NotRunning;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task Play() => RunAsync("Starting Void Crew", () => Service.LaunchAsync(LaunchMode.Play));

    private bool CanCloseGame() => !IsBusy && GameRunning;

    [RelayCommand(CanExecute = nameof(CanCloseGame))]
    private async Task CloseGame()
    {
        if (!Dialogs.Confirm("Close Void Crew", "Close the game now? Anything you haven't saved in the game is lost.", "CLOSE GAME")) return;
        await RunAsync("Closing Void Crew", async () =>
        {
            if (!await CloseGameOrAskAsync()) SetStatus("Void Crew is still running.");
        });
    }

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
        int choice = Dialogs.Ask("Void Crew is still running",
            "Void Crew didn't close. It may be showing a question or still saving.\n\nForce it to close? Anything not saved in the game is lost.",
            new DialogButton("FORCE CLOSE", ButtonKind.Cancel), new DialogButton("WAIT"));
        if (choice != 0) return await GameProcess.WaitForExitAsync(TimeSpan.FromSeconds(30));
        GameProcess.Kill();
        return await GameProcess.WaitForExitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>False, with a note in the status bar, while a long task runs (quick actions that change files check it).</summary>
    public bool CheckIdle(string what)
    {
        if (!IsBusy) return true;
        SetStatus($"{what}: wait until the launcher has finished what it's doing, then try again.");
        return false;
    }

    /// <summary>
    /// Runs a long task with the busy flag set, reporting errors in a pop-up. Asks once for permission to add BepInEx's
    /// loader to the game folder when a launch needs it, then tries again.
    /// </summary>
    public async Task<bool> RunAsync(string what, Func<Task> work)
    {
        if (!CheckIdle(what) || Interlocked.Exchange(ref _busy, 1) == 1) return false;
        IsBusy = true;
        try
        {
            SetStatus(what + "...");
            try
            {
                await work();
            }
            catch (DoorstopConsentNeededException needed)
            {
                if (!Dialogs.Confirm("Add BepInEx's loader",
                        "To load mods, Void Crew needs BepInEx's loader next to its exe: two small files (winhttp.dll and doorstop_config.ini) " +
                        $"in\n{needed.GameDirectory}\n\nThe launcher adds them switched off, so starting Void Crew from Steam stays unmodded. " +
                        "Everything else (BepInEx and your mods) stays in the launcher's own folder. You can remove them on the SETTINGS tab.",
                        "ADD AND CONTINUE"))
                {
                    SetStatus("Cancelled: the game folder was left as it is.");
                    return false;
                }
                Service.Settings.DoorstopConsent = true;
                Service.SaveSettings();
                await work();
            }
            if (StatusText == what + "...") SetStatus("Done.");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
            return false;
        }
        catch (Exception e)
        {
            AppLog.Error(what + " failed", e);
            SetStatus(what + " failed.");
            Dialogs.Error(what + " failed", e.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _busy, 0);
            UpdateGameState();
        }
    }

    /// <summary>Loads Thunderstore's list at start-up (from the cache when it's fresh).</summary>
    public async Task StartAsync()
    {
        try
        {
            SetStatus("Loading Thunderstore's Void Crew mods...");
            await Service.RefreshPackagesAsync(false);
            int updates = Service.Mods.Updates().Count;
            SetStatus(updates > 0 ? $"{updates} of your mods can be updated (MOD MANAGER)." : "Ready.");
        }
        catch (Exception e)
        {
            AppLog.Error("Loading Thunderstore's mod list failed", e);
            SetStatus("Thunderstore couldn't be reached. Mods you have still work; try REFRESH on the MODS tab later.");
        }
        if (Service.GameDirectory == null)
        {
            Navigate("settings");
            Dialogs.Info("Void Crew not found", "The launcher couldn't find Void Crew through Steam. Choose its folder on the SETTINGS tab (the one with Void Crew.exe).");
        }
    }

    public static void Ui(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
