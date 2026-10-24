using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.ViewModels;

/// <summary>How ready the player is to join a lobby.</summary>
public enum LobbyReadiness
{
    /// <summary>No mods needed beyond what's in the store.</summary>
    Ready,

    /// <summary>Mods will be downloaded.</summary>
    Download,

    /// <summary>Some needed mods can't be downloaded.</summary>
    Missing,

    /// <summary>Modded without a mod list.</summary>
    Unknown,
}

/// <summary>One row of the server browser, and the details panel when it's selected.</summary>
public sealed class LobbyViewModel
{
    public LobbyViewModel(Lobby lobby, JoinPlan plan)
    {
        Lobby = lobby;
        Plan = plan;
        Mods = plan.Lines
            .OrderBy(l => l.Status == LobbyModStatus.NotNeeded)
            .ThenBy(l => l.Mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(l => new LobbyModViewModel(l))
            .ToList();

        int downloads = plan.Lines.Count(l => l.Include && l.Status == LobbyModStatus.Download);
        int missing = plan.Lines.Count(l => l.Mod.JoinerNeeds && l.Status == LobbyModStatus.Unavailable);
        Readiness = lobby.ModsUnknown ? LobbyReadiness.Unknown : missing > 0 ? LobbyReadiness.Missing : downloads > 0 ? LobbyReadiness.Download : LobbyReadiness.Ready;
        ReadyText = Readiness switch
        {
            LobbyReadiness.Ready => Loc.T("READY"),
            LobbyReadiness.Download => Loc.F("GET {0}", downloads),
            LobbyReadiness.Missing => Loc.F("{0} MISSING", missing),
            _ => Loc.T("UNKNOWN"),
        };
    }

    public Lobby Lobby { get; }
    public JoinPlan Plan { get; }
    public RoomDto Room => Lobby.Room;
    public string Id => Lobby.Id;
    public string Name => Lobby.Name;
    public string Host => string.IsNullOrEmpty(Room.Host) ? "—" : Room.Host;
    public string Players => $"{Room.Players}/{Room.MaxPlayers}";
    public bool IsFull => Lobby.IsFull;
    public bool InHub => Room.InHub;

    public string Where => Room.InHub ? Loc.T("HUB") : Quest(Room.Quest);

    public string Ship => string.IsNullOrEmpty(Room.Ship) || Room.Ship == "--" ? "—" : Room.Ship;
    public string Sector => string.IsNullOrEmpty(Room.System) || Room.System == "--" ? "—" : Room.System;
    public string Region => MainViewModel.RegionName(Lobby.Region);
    public string Rank => Room.AverageRank > 0 ? Room.AverageRank.ToString() : "—";
    public string Roles => Room.Roles is { Count: > 0 } roles ? string.Join(", ", roles) : "—";
    public string Mutators => Room.Mutators is { Count: > 0 } mutators ? string.Join(", ", mutators) : Loc.T("None");
    public bool CrossPlay => Room.CrossPlay;
    public string Platform => Room.SamePlatform ? Room.Platform ?? "" : Loc.F("{0} (crossplay)", Room.Platform);

    /// <summary>The game's own tag.</summary>
    public string ModdingTag => Room.Modding switch { "session" => "MOD_SESSION", "local" => "MOD_LOCAL", _ => "" };

    public bool IsSessionModded => Lobby.IsSessionModded;
    public bool IsModded => Lobby.IsModded;

    public string ModsSummary
    {
        get
        {
            int needed = Lobby.NeededMods.Count();
            if (Lobby.ModsUnknown) return Loc.T("MODDED");
            if (Lobby.Mods.Count == 0) return Room.Modding == "local" ? Loc.T("CLIENT MODS") : Loc.T("VANILLA");
            if (needed == 0) return Lobby.Mods.Count == 1 ? Loc.F("{0} HOST MOD", Lobby.Mods.Count) : Loc.F("{0} HOST MODS", Lobby.Mods.Count);
            return needed == 1 ? Loc.F("{0} MOD NEEDED", needed) : Loc.F("{0} MODS NEEDED", needed);
        }
    }

    public string ModsTooltip => Lobby.Mods.Count == 0 ? ModsSummary : string.Join("\n", Lobby.Mods.Select(m => m.JoinerNeeds ? $"{m.DisplayName} {m.Version}" : Loc.F("{0} {1} (not needed)", m.DisplayName, m.Version)));

    public List<LobbyModViewModel> Mods { get; }
    public bool HasMods => Mods.Count > 0;
    public LobbyReadiness Readiness { get; }
    public string ReadyText { get; }

    public string KeptText => Plan.Kept.Count == 0 ? "" : Loc.F("Stay on: {0}", string.Join(", ", Plan.Kept.Select(m => m.DisplayName)));
    public string TurnedOffText => Plan.TurnedOff.Count == 0 ? "" : Loc.F("Off for this game: {0}", string.Join(", ", Plan.TurnedOff.Select(m => m.DisplayName)));
    public string Warnings => string.Join("\n\n", Plan.Warnings);

    /// <summary>Text the search box matches against.</summary>
    public string SearchText => $"{Name} {Room.Host} {Ship} {Sector} {string.Join(' ', Lobby.Mods.Select(m => m.DisplayName))}";

    private static string Quest(string? quest) => quest switch
    {
        "Survivor" => Loc.T("MISSION"),
        "Pilgrimage" => Loc.T("PILGRIMAGE"),
        "Endless" => Loc.T("ENDLESS"),
        "Tutorial" => Loc.T("TUTORIAL"),
        "Special" => Loc.T("SPECIAL"),
        _ => Loc.T("MISSION"),
    };
}

public sealed class LobbyModViewModel
{
    public LobbyModViewModel(JoinPlanLine line)
    {
        Line = line;
    }

    public JoinPlanLine Line { get; }
    public string Name => Line.Mod.DisplayName;
    public string Version => string.IsNullOrEmpty(Line.Mod.Version) ? "" : "v" + Line.Mod.Version;
    public string Author => Line.Mod.Package?.Author ?? (Line.Mod.Local ? Loc.T("host's own mod") : "");
    public bool Needed => Line.Mod.JoinerNeeds;

    public string NeedText => Line.Mod.Need switch
    {
        ModNeed.Everyone => Loc.T("EVERYONE"),
        ModNeed.Session => Loc.T("HOST"),
        ModNeed.HostOnly => Loc.T("HOST"),
        ModNeed.ClientOnly => Loc.T("HOST'S CLIENT"),
        _ => Loc.T("PROBABLY"),
    };

    public string StatusText => Line.Status switch
    {
        LobbyModStatus.Ready => Loc.T("INSTALLED"),
        LobbyModStatus.Download => Loc.T("DOWNLOAD"),
        LobbyModStatus.Unavailable => Loc.T("UNAVAILABLE"),
        _ => Loc.T("NOT NEEDED"),
    };

    public string StatusKey => Line.Status.ToString();
    public string? Note => Line.Note;
}
