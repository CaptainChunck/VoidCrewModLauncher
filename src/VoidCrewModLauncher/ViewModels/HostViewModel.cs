using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Infrastructure;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.ViewModels;

public sealed record NeedOption(ModNeed? Need, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A mod of your lobby, with what joining players need of it.</summary>
public partial class HostModViewModel : ObservableObject
{
    private readonly HostViewModel _owner;

    /// <summary>The same rules the launch lists the mods by (it reads your choices from the settings this row changes).</summary>
    private readonly HostNeeds _needs;

    /// <param name="needs">Your choices, and the mods joining players are checked for (everyone gets those).</param>
    public HostModViewModel(HostViewModel owner, InstalledMod mod, HostNeeds needs)
    {
        _owner = owner;
        _needs = needs;
        Mod = mod;
        Checked = needs.Checked(mod);
        ModNeed auto = needs.Auto(mod);
        Options = new List<NeedOption>
        {
            new(null, Loc.F("Auto: {0}", Describe(auto))),
            new(ModNeed.Everyone, Loc.T("Everyone needs it")),
            new(ModNeed.HostOnly, Loc.T("Only the host runs it")),
            new(ModNeed.ClientOnly, Loc.T("Client-side (my own game only)")),
        };
        ModNeed? chosen = needs.Chosen(mod.Id);
        selected = Options.FirstOrDefault(o => o.Need == chosen) ?? Options[0];
        Key = string.Join("\u001f", mod.Id, mod.Version, Name, Author, Checked, auto, chosen);
    }

    /// <summary>
    /// What the row shows, as it was made (the library changes mods in place): when the list is drawn again, a row whose
    /// key hasn't changed stays as it is, so a WHO NEEDS IT list that is open stays open.
    /// </summary>
    public string Key { get; }

    public InstalledMod Mod { get; }
    public string Name => Mod.DisplayName;
    public string Version => "v" + Mod.Version;
    public string Author => Mod.IsLocal ? Loc.T("your PC") : Mod.Author.Replace('_', ' ');
    public IReadOnlyList<NeedOption> Options { get; }

    /// <summary>Joining players are turned away without it (VoidManager or the game checks it), so the lobby lists it for everyone.</summary>
    public bool Checked { get; }

    [ObservableProperty] private NeedOption selected;

    /// <summary>What the lobby lists for joining players.</summary>
    public ModNeed Effective => _needs.Effective(Mod);
    public bool JoinersGetIt => Effective is ModNeed.Everyone or ModNeed.Unknown;

    /// <summary>You picked less than everyone for a mod joining players are checked for: everyone gets it anyway.</summary>
    public bool Overridden => _needs.Overridden(Mod);

    partial void OnSelectedChanged(NeedOption value)
    {
        _owner.SetNeed(Mod.Id, value.Need);
        OnPropertyChanged(nameof(Effective));
        OnPropertyChanged(nameof(JoinersGetIt));
        OnPropertyChanged(nameof(Overridden));
    }

    private static string Describe(ModNeed need) => need switch
    {
        ModNeed.Everyone => Loc.T("everyone needs it"),
        ModNeed.HostOnly or ModNeed.Session => Loc.T("only the host"),
        ModNeed.ClientOnly => Loc.T("client-side"),
        _ => Loc.T("probably everyone"),
    };
}

/// <summary>The HOST LOBBY page: the lobby's settings and its mods; HOST LOBBY starts the game straight into the hub.</summary>
public partial class HostViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public HostViewModel(MainViewModel main)
    {
        _main = main;
        HostSettings settings = main.Service.Settings.Host;
        lobbyName = settings.Name;
        isPublic = settings.IsPublic;
        crossPlay = settings.CrossPlay;
        _wantedMaxPlayers = Math.Max(1, settings.MaxPlayers);
        maxPlayers = _wantedMaxPlayers;
        RefreshMods();
    }

    public ObservableCollection<HostModViewModel> Mods { get; } = new();

    [ObservableProperty] private string lobbyName;
    [ObservableProperty] private bool isPublic;
    [ObservableProperty] private bool crossPlay;
    [ObservableProperty] private int maxPlayers;
    [ObservableProperty] private int playerLimit = 6;

    /// <summary>The player count you chose (saved); the lobby gets at most what its mods allow.</summary>
    private int _wantedMaxPlayers;

    /// <summary>Set while the limit changes, so the shown count following it isn't saved as your choice.</summary>
    private bool _limiting;

    public bool IsPrivate
    {
        get => !IsPublic;
        set => IsPublic = !value;
    }

    /// <summary>Draws the mods again: rows that show the same as before stay (with an open list), the rest are replaced.</summary>
    public void RefreshMods()
    {
        HostNeeds needs = _main.Service.CurrentHostNeeds();
        var rows = new List<HostModViewModel>();
        foreach (InstalledMod mod in _main.Service.Library.Enabled.OrderBy(m => m.InstalledAsDependency).ThenBy(m => m.DisplayName))
        {
            var row = new HostModViewModel(this, mod, needs);
            rows.Add(Mods.FirstOrDefault(old => old.Key == row.Key) ?? row);
        }
        Mods.SyncTo(rows);
        // The game's 6, or more with a mod that raises it: the bridge puts the size on the game's host panel.
        int limit = ModPolicy.RoomLimit(Mods.Select(m => m.Mod.Id));
        _limiting = true;
        try
        {
            // The slider's maximum and value change in the order that keeps the value inside it.
            if (limit > PlayerLimit) PlayerLimit = limit;
            MaxPlayers = Math.Min(_wantedMaxPlayers, limit);
            PlayerLimit = limit;
        }
        finally
        {
            _limiting = false;
        }
    }

    public void SetNeed(string id, ModNeed? need)
    {
        Dictionary<string, ModNeed> chosen = _main.Service.Settings.Host.ModNeeds;
        if (need == null) chosen.Remove(id);
        else chosen[id] = need.Value;
        _main.SaveSettings();
    }

    // Typing the name or dragging the crew size changes these many times a second: they are saved a moment after the last
    // change (and right away when you host), only when what's saved changes.
    partial void OnLobbyNameChanged(string value) => Remember();
    partial void OnIsPublicChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPrivate));
        Remember();
    }
    partial void OnCrossPlayChanged(bool value) => Remember();
    partial void OnMaxPlayersChanged(int value)
    {
        if (_limiting) return;
        _wantedMaxPlayers = value;
        Remember();
    }

    /// <summary>Puts the page's values into the settings; true when that changed them.</summary>
    private bool Apply()
    {
        HostSettings settings = _main.Service.Settings.Host;
        string name = LobbyName.Trim();
        if (settings.Name == name && settings.IsPublic == IsPublic && settings.CrossPlay == CrossPlay && settings.MaxPlayers == _wantedMaxPlayers) return false;
        settings.Name = name;
        settings.IsPublic = IsPublic;
        settings.CrossPlay = CrossPlay;
        settings.MaxPlayers = _wantedMaxPlayers;
        return true;
    }

    private void Remember()
    {
        if (Apply()) _main.SaveSettingsSoon();
    }

    public void NotifyCommands() => HostLobbyCommand.NotifyCanExecuteChanged();

    private bool CanHostLobby() => !_main.IsBusy;

    [RelayCommand(CanExecute = nameof(CanHostLobby))]
    private async Task HostLobby()
    {
        // Saved now rather than in a moment.
        if (Apply()) _main.SaveSettings();
        else _main.SaveWaitingSettings();
        var command = new HostCommand { Name = LobbyName.Trim(), IsPublic = IsPublic, MaxPlayers = MaxPlayers, CrossPlay = CrossPlay };
        LauncherService service = _main.Service;
        await _main.RunAsync(Loc.T("Hosting your lobby"), async () =>
        {
            StateMessage? state = service.Bridge.State;
            if (state is { Phase: "menu", InLobby: true } && await service.RunningGameHasPlayModsAsync())
            {
                await service.Bridge.SendAsync(command);
                _main.SetStatus(Loc.T("Opening your lobby..."));
                return;
            }
            if (GameProcess.IsRunning())
            {
                string question = state?.Phase is "hub" or "mission"
                    ? Loc.T("You're in a game right now. Void Crew restarts with your enabled mods and opens your lobby. Continue?")
                    : Loc.T("Void Crew restarts with your enabled mods and opens your lobby. Continue?");
                if (!_main.Dialogs.Confirm(Loc.T("Restart Void Crew"), question, Loc.T("RESTART AND HOST")))
                {
                    _main.SetStatus(Loc.T("Cancelled."));
                    return;
                }
                if (!await _main.CloseGameOrAskAsync()) throw new InvalidOperationException(Loc.T("Void Crew is still running."));
            }
            await service.LaunchAsync(LaunchMode.Host, command);
            _main.SetStatus(Loc.T("Void Crew is starting; it opens your lobby as soon as the main menu is ready."));
        });
    }

    public void OnHostResult(HostResultMessage result)
    {
        if (result.Ok)
        {
            _main.SetStatus(Loc.T("Your lobby is open. Other launcher users see your mods in their server browser."));
            using var game = GameProcess.Find();
            if (game != null) GameProcess.Focus(game.Id);
            return;
        }
        _main.SetStatus(Loc.T("Hosting failed."));
        _main.Dialogs.Error(Loc.T("Couldn't host"), result.Error ?? Loc.T("Void Crew couldn't open the lobby."));
    }
}
