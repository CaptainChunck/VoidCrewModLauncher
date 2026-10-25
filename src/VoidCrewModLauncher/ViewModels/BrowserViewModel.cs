using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Infrastructure;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.ViewModels;

public sealed record RegionOption(string Code, string Name, int Ping)
{
    /// <summary>Photon reports 4000 ms while it measures again; show no number then.</summary>
    public string Label => Ping is > 0 and < 4000 ? Loc.F("{0}  ·  {1} ms", Name, Ping) : Name;

    public override string ToString() => Label;
}

/// <summary>
/// The server browser. The lobby list comes from the game itself (through the bridge), so it is live while Void Crew
/// is in its main menu. JOIN works out the lobby's mods, downloads what's missing, restarts the game with them if
/// needed, and joins.
/// </summary>
public partial class BrowserViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ObservableCollection<LobbyViewModel> _lobbies = new();
    private RoomsMessage? _rooms;
    private bool _settingRegion;

    /// <summary>The last join, to try again after the game names missing mods.</summary>
    private (Lobby Lobby, ResolvedJoin Join)? _lastJoin;

    public BrowserViewModel(MainViewModel main)
    {
        _main = main;
        LobbiesView = CollectionViewSource.GetDefaultView(_lobbies);
        LobbiesView.Filter = Filter;
        LobbiesView.SortDescriptions.Add(new SortDescription(nameof(LobbyViewModel.IsFull), ListSortDirection.Ascending));
        LobbiesView.SortDescriptions.Add(new SortDescription(nameof(LobbyViewModel.InHub), ListSortDirection.Descending));
        LobbiesView.SortDescriptions.Add(new SortDescription(nameof(LobbyViewModel.Name), ListSortDirection.Ascending));
        BrowserSettings settings = main.Service.Settings.Browser;
        hideFull = settings.HideFull;
        hideVanilla = settings.HideVanilla;
        hideModded = settings.HideModded;
        onlyInHub = settings.OnlyInHub;
    }

    public ICollectionView LobbiesView { get; }
    public ObservableCollection<RegionOption> Regions { get; } = new();

    [ObservableProperty] private LobbyViewModel? selected;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool hideFull;
    [ObservableProperty] private bool hideVanilla;
    [ObservableProperty] private bool hideModded;
    [ObservableProperty] private bool onlyInHub;
    [ObservableProperty] private RegionOption? selectedRegion;
    [ObservableProperty] private int lobbyCount;
    [ObservableProperty] private int shownCount;

    public bool MinimizeWhenBrowsing
    {
        get => _main.Service.Settings.MinimizeGameWhenBrowsing;
        set
        {
            _main.Service.Settings.MinimizeGameWhenBrowsing = value;
            _main.Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>The panel over the list when there's no live list: null when the list shows.</summary>
    public string? OverlayTitle => _main.GameState switch
    {
        GameState.NotRunning => Loc.T("START VOID CREW TO SEE LOBBIES"),
        GameState.Starting => Loc.T("STARTING VOID CREW"),
        GameState.RunningOutside => Loc.T("VOID CREW IS RUNNING WITHOUT THE LAUNCHER"),
        GameState.Closing => Loc.T("VOID CREW IS CLOSING"),
        GameState.Booting or GameState.Loading when _rooms == null => Loc.T("VOID CREW IS LOADING"),
        GameState.Menu when _main.Service.Bridge.State?.InLobby != true && _rooms == null => Loc.T("CONNECTING TO VOID CREW'S SERVERS"),
        _ => null,
    };

    public string OverlayText => _main.GameState switch
    {
        GameState.NotRunning => Loc.T("The lobby list comes from the game."),
        GameState.Starting => Loc.T("Lobbies appear when the main menu is up."),
        GameState.RunningOutside => Loc.T("Restart it from the launcher to see lobbies."),
        _ => "",
    };

    public bool ShowStartButton => _main.GameState == GameState.NotRunning;
    public bool ShowRestartButton => _main.GameState == GameState.RunningOutside;

    /// <summary>A line over the list: what the list is, or why it may be stale.</summary>
    public string Banner
    {
        get
        {
            StateMessage? state = _main.Service.Bridge.State;
            if (_main.GameState is GameState.Hub or GameState.Mission)
            {
                return Loc.T("You're in a game, so Void Crew isn't refreshing its lobby list. It updates again in the main menu.");
            }
            if (_rooms == null) return "";
            string region = MainViewModel.RegionName(_rooms.Region ?? state?.Region);
            string? online = state is { ServerPlayers: > 0 } ? Loc.F("{0} players online there.", state.ServerPlayers) : null;
            if (LobbyCount == 0)
            {
                return online != null
                    ? Loc.F("No public lobbies in {0} right now. {1} Try another region, or host one.", region, online)
                    : Loc.F("No public lobbies in {0} right now. Try another region, or host one.", region);
            }
            string lobbies = ShownCount != LobbyCount
                ? Loc.F("{0} public lobbies in {1}, {2} shown.", LobbyCount, region, ShownCount)
                : Loc.F("{0} public lobbies in {1}.", LobbyCount, region);
            return online != null ? Loc.F("{0} {1}", lobbies, online) : lobbies;
        }
    }

    public bool HasBanner => !string.IsNullOrEmpty(Banner) && OverlayTitle == null;

    public void OnGameStateChanged()
    {
        if (_main.GameState is GameState.NotRunning or GameState.RunningOutside or GameState.Closing)
        {
            _rooms = null;
            _lobbies.Clear();
            Regions.Clear();
            LobbyCount = 0;
        }
        RaiseOverlay();
        NotifyCommands();
    }

    private void RaiseOverlay()
    {
        OnPropertyChanged(nameof(OverlayTitle));
        OnPropertyChanged(nameof(OverlayText));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowRestartButton));
        OnPropertyChanged(nameof(Banner));
        OnPropertyChanged(nameof(HasBanner));
    }

    public void NotifyCommands()
    {
        JoinCommand.NotifyCanExecuteChanged();
        StartGameCommand.NotifyCanExecuteChanged();
        RestartFromLauncherCommand.NotifyCanExecuteChanged();
    }

    public void OnState(StateMessage state)
    {
        _settingRegion = true;
        try
        {
            var regions = (state.Regions ?? new List<RegionDto>()).Select(r => new RegionOption(r.Code, MainViewModel.RegionName(r.Code), r.Ping)).ToList();
            if (!regions.Select(r => r.Code).SequenceEqual(Regions.Select(r => r.Code)))
            {
                Regions.Clear();
                foreach (RegionOption region in regions) Regions.Add(region);
            }
            else
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    if (Regions[i].Ping != regions[i].Ping) Regions[i] = regions[i];
                }
            }
            SelectedRegion = Regions.FirstOrDefault(r => r.Code == state.Region);
        }
        finally
        {
            _settingRegion = false;
        }
        RaiseOverlay();
    }

    public void OnRooms(RoomsMessage rooms)
    {
        _rooms = rooms;
        RefreshLobbies();
    }

    /// <summary>Rebuilds the rows (mod status depends on the library too, so this also runs after installs).</summary>
    public void RefreshLobbies()
    {
        if (_rooms == null) return;
        string? selectedId = Selected?.Id;
        PackageIndex index = _main.Service.Index;
        _lobbies.Clear();
        foreach (RoomDto room in _rooms.Rooms ?? new List<RoomDto>())
        {
            Lobby lobby = LobbyReader.Read(room, _rooms.Region, index);
            _lobbies.Add(new LobbyViewModel(lobby, _main.Service.Planner.Plan(lobby)));
        }
        LobbyCount = _lobbies.Count;
        LobbiesView.Refresh();
        ShownCount = LobbiesView.Cast<object>().Count();
        Selected = _lobbies.FirstOrDefault(l => l.Id == selectedId);
        RaiseOverlay();
        LookUpMissingVersions();
    }

    /// <summary>Mod versions lobbies run that the launcher has looked up on Thunderstore this session.</summary>
    private readonly HashSet<string> _lookedUp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Thunderstore's list can be behind the versions lobbies run (or miss a package); looks those up so the rows show
    /// them as downloadable. The rows are drawn again when something is found.
    /// </summary>
    private void LookUpMissingVersions()
    {
        foreach (LobbyViewModel row in _lobbies)
        {
            List<string> missing = row.Lobby.Mods.Where(m => !m.Local && m.PackageVersion == null && m.PackageId != null)
                .Select(m => $"{m.PackageId}@{m.Version}").Where(_lookedUp.Add).ToList();
            if (missing.Count > 0) _ = FreshenLobbyAsync(row.Lobby);
        }
    }

    private async Task<bool> FreshenLobbyAsync(Lobby lobby)
    {
        try
        {
            return await _main.Service.FreshenLobbyAsync(lobby);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Warn($"Looking up {lobby.Name}'s mods on Thunderstore failed: {e.Message}");
            return false;
        }
    }

    private bool Filter(object item)
    {
        if (item is not LobbyViewModel lobby) return false;
        if (HideFull && lobby.IsFull) return false;
        if (HideVanilla && !lobby.IsModded) return false;
        if (HideModded && lobby.IsModded) return false;
        if (OnlyInHub && !lobby.InHub) return false;
        if (!string.IsNullOrWhiteSpace(Search) && !lobby.SearchText.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnHideFullChanged(bool value) => ApplyFilter();
    partial void OnOnlyInHubChanged(bool value) => ApplyFilter();

    partial void OnHideVanillaChanged(bool value)
    {
        if (value) HideModded = false;
        ApplyFilter();
    }

    partial void OnHideModdedChanged(bool value)
    {
        if (value) HideVanilla = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        BrowserSettings settings = _main.Service.Settings.Browser;
        settings.HideFull = HideFull;
        settings.HideVanilla = HideVanilla;
        settings.HideModded = HideModded;
        settings.OnlyInHub = OnlyInHub;
        _main.Service.SaveSettings();
        LobbiesView.Refresh();
        ShownCount = LobbiesView.Cast<object>().Count();
        OnPropertyChanged(nameof(Banner));
    }

    partial void OnSelectedChanged(LobbyViewModel? value) => JoinCommand.NotifyCanExecuteChanged();

    partial void OnSelectedRegionChanged(RegionOption? value)
    {
        if (_settingRegion || value == null) return;
        if (_main.Service.Bridge.State?.Region == value.Code) return;
        _main.SetStatus(Loc.F("Switching Void Crew to {0}...", value.Name));
        _ = _main.Service.Bridge.SendAsync(new SetRegionCommand { Region = value.Code });
    }

    [RelayCommand]
    private void Refresh()
    {
        _ = _main.Service.Bridge.SendAsync(new Message { Type = MessageTypes.Refresh });
        RefreshLobbies();
    }

    private bool CanStartGame() => !_main.IsBusy && _main.GameState == GameState.NotRunning;

    [RelayCommand(CanExecute = nameof(CanStartGame))]
    private Task StartGame() => _main.RunAsync(Loc.T("Starting Void Crew"), () => _main.Service.LaunchAsync(LaunchMode.Browse));

    private bool CanRestartFromLauncher() => !_main.IsBusy && _main.GameState == GameState.RunningOutside;

    [RelayCommand(CanExecute = nameof(CanRestartFromLauncher))]
    private async Task RestartFromLauncher()
    {
        if (!_main.Dialogs.Confirm(Loc.T("Restart Void Crew"), Loc.T("Close the running Void Crew and start it again from the launcher, with your enabled mods?"), Loc.T("RESTART"))) return;
        await _main.RunAsync(Loc.T("Restarting Void Crew"), async () =>
        {
            if (!await _main.CloseGameOrAskAsync()) throw new InvalidOperationException(Loc.T("Void Crew is still running."));
            await _main.Service.LaunchAsync(LaunchMode.Browse);
        });
    }

    private bool CanJoin(LobbyViewModel? lobby) =>
        !_main.IsBusy && (lobby ?? Selected) != null && _main.Service.Bridge.State?.Busy == null;

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task Join(LobbyViewModel? lobby)
    {
        lobby ??= Selected;
        if (lobby == null) return;
        LauncherService service = _main.Service;
        if (lobby.IsFull && !_main.Dialogs.Confirm(Loc.T("Lobby is full"), Loc.F("{0} is full ({1}). Try anyway?", lobby.Name, lobby.Players), Loc.T("TRY ANYWAY"))) return;

        // The lobby's exact mod versions, looked up on Thunderstore when its list doesn't have them yet.
        Lobby current = lobby.Lobby;
        if (current.Mods.Any(m => !m.Local && m.PackageVersion == null && m.PackageId != null))
        {
            _main.SetStatus(Loc.F("Looking up {0}'s mods on Thunderstore...", current.Name));
            await FreshenLobbyAsync(current);
            current = LobbyReader.Read(current.Room, current.Region, service.Index);
            _main.SetStatus(Loc.T("Ready."));
        }

        // Re-plan against the current library, then let the player check it.
        JoinPlan plan = service.Planner.Plan(current);
        if (!_main.Dialogs.ConfirmJoin(plan, service.Planner.Resolve, j => !service.CanJoinWithoutRestart(j), _main.GameRunning)) return;
        ResolvedJoin join = service.Planner.Resolve(plan);
        await JoinWith(current, join);
    }

    /// <param name="restart">Start the game again even when its mods look right (it was turned away for a mod it has loaded).</param>
    private async Task JoinWith(Lobby lobby, ResolvedJoin join, bool restart = false)
    {
        LauncherService service = _main.Service;
        _lastJoin = (lobby, join);
        await _main.RunAsync(Loc.F("Joining {0}", lobby.Name), async () =>
        {
            if (!restart && service.CanJoinWithoutRestart(join))
            {
                _main.SetStatus(Loc.F("Joining {0}...", lobby.Name));
                await service.Bridge.SendAsync(new JoinCommand { RoomId = lobby.Id, Region = lobby.Region, RoomName = lobby.Name, Modded = lobby.IsSessionModded });
                return;
            }
            if (GameProcess.IsRunning() && !await _main.CloseGameOrAskAsync()) throw new InvalidOperationException(Loc.T("Void Crew is still running, so its mods can't be changed."));
            await service.JoinAsync(lobby, join);
            _main.SetStatus(Loc.F("Void Crew is starting with {0}'s mods; it joins as soon as the main menu is ready.", lobby.Name));
        });
    }

    public void OnJoinResult(JoinResultMessage result)
    {
        if (result.Ok)
        {
            _main.SetStatus(Loc.T("Joined the lobby. Have fun!"));
            using var game = GameProcess.Find();
            if (game != null) GameProcess.Focus(game.Id);
            return;
        }
        _main.SetStatus(Loc.T("Joining failed."));
        // Turned away for its mods: the game's report naming them comes right after, and OnGameLeftRoom explains it on
        // its own (with FIX AND REJOIN) instead of under a second dialog. Bridges before 0.9.1 don't give the reason.
        if (result.Reason == "mods" && IsLastJoin(result.RoomId)) return;
        _main.Dialogs.Error(Loc.T("Couldn't join"), result.Error ?? Loc.T("Void Crew couldn't join the lobby."));
    }

    /// <summary>The lobby the launcher last joined, which FIX AND REJOIN can try again.</summary>
    private bool IsLastJoin(string? roomId) => _lastJoin is { } last && string.Equals(roomId, last.Lobby.Id, StringComparison.Ordinal);

    /// <summary>
    /// The game left a room on its own. When it was the lobby the launcher last joined and the game names mods the
    /// room wanted (or didn't allow), offer to fix the mod set and join again.
    /// </summary>
    public void OnGameLeftRoom(DisconnectedMessage left)
    {
        if (left.Reason != "mods" || !IsLastJoin(left.RoomId) || _lastJoin is not { } last) return;
        (Lobby lobby, ResolvedJoin join) = last;
        _lastJoin = null;
        PackageIndex index = _main.Service.Index;

        var found = new List<(string Name, TsPackage Package)>();
        var notFound = new List<string>();
        foreach (ModNameDto missing in left.MissingMods ?? new List<ModNameDto>())
        {
            string name = string.IsNullOrEmpty(missing.DisplayName) ? missing.Name : missing.DisplayName;
            TsPackage? package = index.FindByName(missing.DisplayName) ?? index.FindByName(StripVersion(missing.Name));
            if (package != null && join.Mods.All(m => !string.Equals(m.Id, package.FullName, StringComparison.OrdinalIgnoreCase))) found.Add((name, package));
            else notFound.Add(name);
        }
        var extra = new List<RuntimeMod>();
        foreach (ModNameDto invalid in left.InvalidMods ?? new List<ModNameDto>())
        {
            string simple = PackageIndex.Simplify(StripVersion(string.IsNullOrEmpty(invalid.DisplayName) ? invalid.Name : invalid.DisplayName));
            extra.AddRange(join.Mods.Where(m => PackageIndex.Simplify(m.Name) == simple || PackageIndex.Simplify(m.Id).EndsWith(simple, StringComparison.Ordinal)));
        }
        // The game often names what's missing by a mod's assets (a mission, an item) rather than the mod. The host's own
        // mods this join left out (listed as only the host's) are then the likely owners: a mod the game requires that the
        // host didn't list for everyone (hosts before launcher 0.8.1 couldn't tell).
        List<LobbyMod> leftOut = notFound.Count == 0 ? new List<LobbyMod>()
            : lobby.Mods.Where(m => m is { Downloadable: true, JoinerNeeds: false, Need: not ModNeed.ClientOnly }
                                    && (m.PackageVersion ?? m.Package!.Latest) != null
                                    && join.Mods.All(r => !string.Equals(r.Id, m.Package!.FullName, StringComparison.OrdinalIgnoreCase))).ToList();

        var message = new System.Text.StringBuilder(Loc.F("{0} turned Void Crew away because the mods don't match.", lobby.Name)).Append('\n');
        if (found.Count > 0) message.Append('\n').Append(Loc.T("It needs (found on Thunderstore):")).Append('\n').AppendJoin("\n", found.Select(f => "  • " + Loc.F("{0} → {1} by {2}", f.Name, f.Package.DisplayName, f.Package.Author))).Append('\n');
        if (notFound.Count > 0) message.Append('\n').Append(Loc.T("It needs (not found on Thunderstore):")).Append('\n').AppendJoin("\n", notFound.Select(n => "  • " + n)).Append('\n');
        if (leftOut.Count > 0) message.Append('\n').Append(Loc.T("Also loading the host's other mods:")).Append('\n').AppendJoin("\n", leftOut.Select(m => "  • " + m.DisplayName)).Append('\n');
        if (extra.Count > 0) message.Append('\n').Append(Loc.T("It doesn't allow:")).Append('\n').AppendJoin("\n", extra.Select(m => "  • " + m.Name.Replace('_', ' '))).Append('\n');

        if (found.Count == 0 && extra.Count == 0 && leftOut.Count == 0)
        {
            _main.Dialogs.Error(Loc.T("Mods don't match"), message.ToString());
            return;
        }
        if (!_main.Dialogs.Confirm(Loc.T("Mods don't match"), Loc.F("{0}\nFix the mods and join again?", message), Loc.T("FIX AND REJOIN"), Loc.T("NOT NOW"))) return;

        var fixedJoin = new ResolvedJoin();
        fixedJoin.Mods.AddRange(join.Mods.Except(extra));
        var roots = found.Select(f => (f.Package, f.Package.Latest!)).ToList();
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (LobbyMod mod in leftOut)
        {
            TsVersion version = (mod.PackageVersion ?? mod.Package!.Latest)!;
            roots.Add((mod.Package!, version));
            pinned[mod.Package!.FullName] = version.VersionNumber;
        }
        foreach ((TsPackage package, TsVersion version) in _main.Service.Mods.Resolve(roots, pinned))
        {
            if (fixedJoin.Mods.Any(m => string.Equals(m.Id, package.FullName, StringComparison.OrdinalIgnoreCase))) continue;
            string folder = _main.Service.Store.VersionDirectory(package.FullName, version.VersionNumber);
            fixedJoin.Mods.Add(new RuntimeMod(package.FullName, package.Name, version.VersionNumber, folder, false, ModNeed.Everyone));
            if (!_main.Service.Store.Has(package.FullName, version.VersionNumber)) fixedJoin.Downloads.Add((package, version));
        }
        _ = JoinWith(lobby, fixedJoin, restart: extra.Count > 0);
    }

    private static string StripVersion(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Where(w => !(char.IsDigit(w[0]) && w.Contains('.')) && !w.StartsWith('(')));
    }
}
