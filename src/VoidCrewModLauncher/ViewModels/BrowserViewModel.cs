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
    public string Label => Ping is > 0 and < 4000 ? $"{Name}  ·  {Ping} ms" : Name;

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
        GameState.NotRunning => "START VOID CREW TO SEE LOBBIES",
        GameState.Starting => "STARTING VOID CREW",
        GameState.RunningOutside => "VOID CREW IS RUNNING WITHOUT THE LAUNCHER",
        GameState.Booting or GameState.Loading when _rooms == null => "VOID CREW IS LOADING",
        GameState.Menu when _main.Service.Bridge.State?.InLobby != true && _rooms == null => "CONNECTING TO VOID CREW'S SERVERS",
        _ => null,
    };

    public string OverlayText => _main.GameState switch
    {
        GameState.NotRunning => "The lobby list comes from the game.",
        GameState.Starting => "Lobbies appear when the main menu is up.",
        GameState.RunningOutside => "Restart it from the launcher to see lobbies.",
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
                return "You're in a game, so Void Crew isn't refreshing its lobby list. It updates again in the main menu.";
            }
            if (_rooms == null) return "";
            string region = MainViewModel.RegionName(_rooms.Region ?? state?.Region);
            string online = state is { ServerPlayers: > 0 } ? $" {state.ServerPlayers} players online there." : "";
            return LobbyCount == 0
                ? $"No public lobbies in {region} right now.{online} Try another region, or host one."
                : $"{LobbyCount} public lobbies in {region}" + (ShownCount != LobbyCount ? $", {ShownCount} shown." : ".") + online;
        }
    }

    public bool HasBanner => !string.IsNullOrEmpty(Banner) && OverlayTitle == null;

    public void OnGameStateChanged()
    {
        if (_main.GameState is GameState.NotRunning or GameState.RunningOutside)
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
        _main.SetStatus($"Switching Void Crew to {value.Name}...");
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
    private Task StartGame() => _main.RunAsync("Starting Void Crew", () => _main.Service.LaunchAsync(LaunchMode.Browse));

    private bool CanRestartFromLauncher() => !_main.IsBusy && _main.GameState == GameState.RunningOutside;

    [RelayCommand(CanExecute = nameof(CanRestartFromLauncher))]
    private async Task RestartFromLauncher()
    {
        if (!_main.Dialogs.Confirm("Restart Void Crew", "Close the running Void Crew and start it again from the launcher, with your enabled mods?", "RESTART")) return;
        await _main.RunAsync("Restarting Void Crew", async () =>
        {
            if (!await _main.CloseGameOrAskAsync()) throw new InvalidOperationException("Void Crew is still running.");
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
        if (lobby.IsFull && !_main.Dialogs.Confirm("Lobby is full", $"{lobby.Name} is full ({lobby.Players}). Try anyway?", "TRY ANYWAY")) return;

        // The lobby's exact mod versions, looked up on Thunderstore when its list doesn't have them yet.
        Lobby current = lobby.Lobby;
        if (current.Mods.Any(m => !m.Local && m.PackageVersion == null && m.PackageId != null))
        {
            _main.SetStatus($"Looking up {current.Name}'s mods on Thunderstore...");
            await FreshenLobbyAsync(current);
            current = LobbyReader.Read(current.Room, current.Region, service.Index);
            _main.SetStatus("Ready.");
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
        await _main.RunAsync($"Joining {lobby.Name}", async () =>
        {
            if (!restart && service.CanJoinWithoutRestart(join))
            {
                _main.SetStatus($"Joining {lobby.Name}...");
                await service.Bridge.SendAsync(new JoinCommand { RoomId = lobby.Id, Region = lobby.Region, RoomName = lobby.Name, Modded = lobby.IsSessionModded });
                return;
            }
            if (GameProcess.IsRunning() && !await _main.CloseGameOrAskAsync()) throw new InvalidOperationException("Void Crew is still running, so its mods can't be changed.");
            await service.JoinAsync(lobby, join);
            _main.SetStatus($"Void Crew is starting with {lobby.Name}'s mods; it joins as soon as the main menu is ready.");
        });
    }

    public void OnJoinResult(JoinResultMessage result)
    {
        if (result.Ok)
        {
            _main.SetStatus("Joined the lobby. Have fun!");
            using var game = GameProcess.Find();
            if (game != null) GameProcess.Focus(game.Id);
            return;
        }
        _main.SetStatus("Joining failed.");
        _main.Dialogs.Error("Couldn't join", result.Error ?? "Void Crew couldn't join the lobby.");
    }

    /// <summary>
    /// The game left a room on its own. When it was the lobby the launcher last joined and the game names mods the
    /// room wanted (or didn't allow), offer to fix the mod set and join again.
    /// </summary>
    public void OnGameLeftRoom(DisconnectedMessage left)
    {
        if (left.Reason != "mods" || _lastJoin == null) return;
        (Lobby lobby, ResolvedJoin join) = _lastJoin.Value;
        if (!string.Equals(left.RoomId, lobby.Id, StringComparison.Ordinal)) return;
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

        var message = new System.Text.StringBuilder($"{lobby.Name} turned Void Crew away because the mods don't match.\n");
        if (found.Count > 0) message.Append("\nIt needs (found on Thunderstore):\n").AppendJoin("\n", found.Select(f => $"  • {f.Name} → {f.Package.DisplayName} by {f.Package.Author}")).Append('\n');
        if (notFound.Count > 0) message.Append("\nIt needs (not found on Thunderstore):\n").AppendJoin("\n", notFound.Select(n => "  • " + n)).Append('\n');
        if (extra.Count > 0) message.Append("\nIt doesn't allow:\n").AppendJoin("\n", extra.Select(m => "  • " + m.Name.Replace('_', ' '))).Append('\n');

        if (found.Count == 0 && extra.Count == 0)
        {
            _main.Dialogs.Error("Mods don't match", message.ToString());
            return;
        }
        if (!_main.Dialogs.Confirm("Mods don't match", message + "\nFix the mods and join again?", "FIX AND REJOIN", "NOT NOW")) return;

        var fixedJoin = new ResolvedJoin();
        fixedJoin.Mods.AddRange(join.Mods.Except(extra));
        var roots = found.Select(f => (f.Package, f.Package.Latest!)).ToList();
        foreach ((TsPackage package, TsVersion version) in _main.Service.Mods.Resolve(roots))
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
