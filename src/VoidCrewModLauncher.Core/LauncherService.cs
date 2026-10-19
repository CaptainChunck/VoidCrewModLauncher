using VoidCrewModLauncher.Core.Bridge;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Steam;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core;

public enum LaunchMode
{
    /// <summary>Play with the mods turned on in the INSTALLED tab.</summary>
    Play,

    /// <summary>Same mods as Play, started to fill the server browser (minimised if the player wants).</summary>
    Browse,

    /// <summary>Play's mods, then host a lobby with the HOST tab's settings.</summary>
    Host,

    /// <summary>A lobby's mods, then join it.</summary>
    Join,
}

/// <summary>The player has to agree before the launcher adds BepInEx's loader to the game folder.</summary>
public sealed class DoorstopConsentNeededException(string gameDirectory)
    : Exception($"The launcher needs to add BepInEx's loader (winhttp.dll and doorstop_config.ini) to {gameDirectory}.")
{
    public string GameDirectory { get; } = gameDirectory;
}

/// <summary>
/// Everything the launcher does, for the UI to call: the mod library and Thunderstore, building and starting launches,
/// and the bridge to the running game. UI-free and long-lived; events may come from background threads.
/// </summary>
public sealed class LauncherService : IDisposable
{
    public LauncherService(AppPaths paths)
    {
        Paths = paths;
        paths.EnsureCreated();
        Settings = LauncherSettings.Load(paths);
        Thunderstore = new ThunderstoreClient(paths);
        Library = new ModLibrary(paths);
        Store = new PackageStore(paths, Thunderstore);
        Mods = new ModManager(Library, Store);
        Planner = new JoinPlanner(Mods);
        Runtime = new RuntimeBuilder(paths);
        Bridge = new BridgeServer(Settings.BridgeToken);
        Bridge.Connected += OnBridgeConnected;
    }

    public AppPaths Paths { get; }
    public LauncherSettings Settings { get; }
    public ThunderstoreClient Thunderstore { get; }
    public ModLibrary Library { get; }
    public PackageStore Store { get; }
    public ModManager Mods { get; }
    public JoinPlanner Planner { get; }
    public RuntimeBuilder Runtime { get; }
    public BridgeServer Bridge { get; }

    public PackageIndex Index => Mods.Index;

    /// <summary>What the running game was started with (when the launcher started it).</summary>
    public LaunchFile? CurrentLaunch { get; private set; }

    /// <summary>When the last launch was started, to tell "starting" from "running without the launcher".</summary>
    public DateTime? LastLaunchUtc { get; private set; }

    public LaunchMode? LastLaunchMode { get; private set; }

    public event Action? PackagesChanged;
    public event Action<InstallStatus>? Status;

    /// <summary>The Void Crew folder: from Settings, else found through Steam.</summary>
    public string? GameDirectory
    {
        get
        {
            if (SteamLocator.IsGameDirectory(Settings.GameDirectory)) return Settings.GameDirectory;
            return _foundGame ??= SteamLocator.FindGame();
        }
    }

    private string? _foundGame;

    public void Start()
    {
        Bridge.Start();
        if (Settings.GameDirectory == null && GameDirectory != null) AppLog.Info($"Found Void Crew at {GameDirectory}.");
    }

    public void SaveSettings() => Settings.Save(Paths);

    public async Task RefreshPackagesAsync(bool force, CancellationToken cancel = default)
    {
        List<TsPackage> packages = await Thunderstore.GetPackagesAsync(force, cancel);
        Mods.Index = new PackageIndex(packages);
        PackagesChanged?.Invoke();
    }

    private void Report(string message, double? fraction = null) => Status?.Invoke(new InstallStatus(message, fraction));

    private IProgress<InstallStatus> StatusProgress() => new SyncProgress<InstallStatus>(s => Status?.Invoke(s));

    // ---------------------------------------------------------------- mod sets

    /// <summary>The mods of a Play or Host launch: every enabled mod and its dependencies, downloading what's missing.</summary>
    public async Task<List<RuntimeMod>> PlayModsAsync(CancellationToken cancel = default)
    {
        var mods = new List<RuntimeMod>();
        var roots = new List<(TsPackage, TsVersion)>();
        foreach (InstalledMod mod in Library.Enabled)
        {
            ModNeed need = Settings.Host.ModNeeds.TryGetValue(mod.Id, out ModNeed chosen) ? chosen : mod.GuessedNeed;
            bool byUser = Settings.Host.ModNeeds.ContainsKey(mod.Id);
            TsPackage? package = mod.IsLocal ? null : Index.Find(mod.Id);
            TsVersion? version = package?.Version(mod.Version);
            if (package != null && version != null)
            {
                roots.Add((package, version));
                continue;
            }
            string folder = Store.VersionDirectory(mod.Id, mod.Version);
            if (Directory.Exists(folder)) mods.Add(new RuntimeMod(mod.Id, mod.Name, mod.Version, folder, mod.IsLocal, need, byUser));
            else AppLog.Warn($"{mod.Id} {mod.Version} is turned on but isn't in the store any more; leaving it out.");
        }

        var enabledIds = new HashSet<string>(Library.Enabled.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        foreach ((TsPackage package, TsVersion version) in Mods.Resolve(roots))
        {
            // An enabled copy Thunderstore no longer lists is already in; don't load a second version next to it.
            if (mods.Any(m => string.Equals(m.Id, package.FullName, StringComparison.OrdinalIgnoreCase))) continue;
            if (!enabledIds.Contains(package.FullName)) AppLog.Info($"Also loading {package.FullName} {version.VersionNumber}: another enabled mod needs it.");
            string folder = await Store.EnsureAsync(version, new SyncProgress<double>(f => Report($"Downloading {package.DisplayName} {version.VersionNumber}", f)), cancel);
            bool byUser = Settings.Host.ModNeeds.TryGetValue(package.FullName, out ModNeed chosen);
            ModNeed need = byUser ? chosen : ModPolicy.GuessNeed(package.FullName, package.Categories);
            mods.Add(new RuntimeMod(package.FullName, package.Name, version.VersionNumber, folder, false, need, byUser));
        }
        return mods;
    }

    // ---------------------------------------------------------------- launching

    /// <summary>Starts Void Crew for Play, Browse or Host with your enabled mods. The game must not be running.</summary>
    public async Task LaunchAsync(LaunchMode mode, HostCommand? host = null, CancellationToken cancel = default)
    {
        if (mode == LaunchMode.Join) throw new ArgumentException("Use JoinAsync to join a lobby.", nameof(mode));
        List<RuntimeMod> mods = await PlayModsAsync(cancel);
        await LaunchWithAsync(mode, mods, host, null, cancel);
    }

    /// <summary>Downloads what the join needs, then starts Void Crew with the lobby's mods to join it.</summary>
    public async Task JoinAsync(Lobby lobby, ResolvedJoin join, CancellationToken cancel = default)
    {
        int index = 0;
        foreach ((TsPackage package, TsVersion version) in join.Downloads)
        {
            index++;
            string label = $"Downloading {package.DisplayName} {version.VersionNumber} ({index}/{join.Downloads.Count})";
            Report(label, 0);
            await Store.EnsureAsync(version, new SyncProgress<double>(f => Report(label, f)), cancel);
        }
        var command = new JoinCommand
        {
            RoomId = lobby.Id,
            Region = lobby.Region,
            RoomName = lobby.Name,
            Modded = lobby.IsSessionModded,
        };
        await LaunchWithAsync(LaunchMode.Join, join.Mods, null, command, cancel);
    }

    private async Task LaunchWithAsync(LaunchMode mode, List<RuntimeMod> mods, HostCommand? host, JoinCommand? join, CancellationToken cancel)
    {
        string game = GameDirectory ?? throw new InvalidOperationException("Void Crew wasn't found. Choose its folder on the SETTINGS tab.");
        if (GameProcess.IsRunning()) throw new InvalidOperationException("Void Crew is already running. Close it first.");
        mods = mods.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        string bepInExPack = await Mods.EnsureBepInExAsync(StatusProgress(), cancel);
        DoorstopState doorstop = Doorstop.Inspect(game);
        if (!doorstop.HasLoader)
        {
            if (!Settings.DoorstopConsent) throw new DoorstopConsentNeededException(game);
            Doorstop.InstallIfMissing(game, bepInExPack);
        }

        var launch = new LaunchFile
        {
            LaunchId = Guid.NewGuid().ToString("N"),
            LauncherVersion = AppInfo.Version,
            Mode = mode.ToString().ToLowerInvariant(),
            Ports = BridgeDefaults.Ports.ToList(),
            Token = Settings.BridgeToken,
            Mods = mods.Select(m => new LaunchMod
            {
                Id = m.Id,
                Name = m.Name.Replace('_', ' '),
                Version = m.Version,
                Need = LobbyAdvert.NeedLetter(m.Need),
                NeedSetByUser = m.NeedSetByUser,
                Local = m.Local,
            }).ToList(),
            Host = host,
            Join = join,
        };

        Report("Preparing BepInEx and the mods");
        await Task.Run(() => Runtime.Build(bepInExPack, mods, launch, Settings.ShowBepInExConsole), cancel);
        GameProcess.Start(game, Paths.Preloader, Settings.LaunchThroughSteam);
        CurrentLaunch = launch;
        LastLaunchUtc = DateTime.UtcNow;
        LastLaunchMode = mode;
        Report(mode switch
        {
            LaunchMode.Join => $"Starting Void Crew to join {join?.RoomName}",
            LaunchMode.Host => "Starting Void Crew to host your lobby",
            LaunchMode.Browse => "Starting Void Crew for the server browser",
            _ => "Starting Void Crew",
        });
    }

    /// <summary>Closes the game (through the bridge when connected, else like closing its window) and waits for it.</summary>
    public async Task<bool> CloseGameAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        if (!GameProcess.IsRunning()) return true;
        Report("Closing Void Crew");
        if (!await Bridge.SendAsync(new Message { Type = MessageTypes.Quit })) GameProcess.RequestClose();
        return await GameProcess.WaitForExitAsync(timeout, cancel);
    }

    /// <summary>The running game's mods match the join (no restart needed) and it is connected to the launcher.</summary>
    public bool CanJoinWithoutRestart(ResolvedJoin join) =>
        Bridge.IsConnected && join.Downloads.Count == 0 && JoinPlanner.RunningGameFits(join, CurrentLaunch);

    /// <summary>The running game has exactly the Play mods (so hosting needs no restart).</summary>
    public async Task<bool> RunningGameHasPlayModsAsync()
    {
        if (!Bridge.IsConnected || CurrentLaunch?.Mods == null) return false;
        List<RuntimeMod> play = await PlayModsAsync();
        var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (LaunchMod mod in CurrentLaunch.Mods) running.TryAdd(mod.Id, mod.Version);
        if (play.Count != running.Count) return false;
        return play.All(m => running.TryGetValue(m.Id, out string? version) && VersionNumber.Same(version, m.Version));
    }

    private void OnBridgeConnected(HelloMessage hello)
    {
        // After a launcher restart, learn what the running game was started with from launch.json.
        if (CurrentLaunch?.LaunchId == hello.LaunchId) return;
        LaunchFile? file = null;
        try
        {
            if (File.Exists(Paths.RuntimeLaunchFile))
            {
                file = System.Text.Json.JsonSerializer.Deserialize<LaunchFile>(File.ReadAllText(Paths.RuntimeLaunchFile), JsonFiles.Protocol);
            }
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not read {Paths.RuntimeLaunchFile}: {e.Message}");
        }
        CurrentLaunch = file?.LaunchId == hello.LaunchId ? file : null;
    }

    public void Dispose()
    {
        Bridge.Dispose();
        Thunderstore.Dispose();
    }
}

/// <summary>An IProgress that reports on the calling thread (no SynchronizationContext capture).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
