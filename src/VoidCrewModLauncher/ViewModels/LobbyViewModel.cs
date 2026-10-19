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
            LobbyReadiness.Ready => "READY",
            LobbyReadiness.Download => $"GET {downloads}",
            LobbyReadiness.Missing => $"{missing} MISSING",
            _ => "UNKNOWN",
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

    public string Where => Room.InHub ? "HUB" : Quest(Room.Quest);

    public string Ship => string.IsNullOrEmpty(Room.Ship) || Room.Ship == "--" ? "—" : Room.Ship;
    public string Sector => string.IsNullOrEmpty(Room.System) || Room.System == "--" ? "—" : Room.System;
    public string Region => MainViewModel.RegionName(Lobby.Region);
    public string Rank => Room.AverageRank > 0 ? Room.AverageRank.ToString() : "—";
    public string Roles => Room.Roles is { Count: > 0 } roles ? string.Join(", ", roles) : "—";
    public string Mutators => Room.Mutators is { Count: > 0 } mutators ? string.Join(", ", mutators) : "None";
    public bool CrossPlay => Room.CrossPlay;
    public string Platform => Room.SamePlatform ? Room.Platform ?? "" : $"{Room.Platform} (crossplay)";

    /// <summary>The game's own tag.</summary>
    public string ModdingTag => Room.Modding switch { "session" => "MOD_SESSION", "local" => "MOD_LOCAL", _ => "" };

    public bool IsSessionModded => Lobby.IsSessionModded;
    public bool IsModded => Lobby.IsModded;

    public string ModsSummary
    {
        get
        {
            int needed = Lobby.NeededMods.Count();
            if (Lobby.ModsUnknown) return "MODDED";
            if (Lobby.Mods.Count == 0) return Room.Modding == "local" ? "CLIENT MODS" : "VANILLA";
            return needed == 0 ? $"{Lobby.Mods.Count} HOST MOD{(Lobby.Mods.Count == 1 ? "" : "S")}" : $"{needed} MOD{(needed == 1 ? "" : "S")} NEEDED";
        }
    }

    public string ModsTooltip => Lobby.Mods.Count == 0 ? ModsSummary : string.Join("\n", Lobby.Mods.Select(m => $"{m.DisplayName} {m.Version}{(m.JoinerNeeds ? "" : " (not needed)")}"));

    public List<LobbyModViewModel> Mods { get; }
    public bool HasMods => Mods.Count > 0;
    public LobbyReadiness Readiness { get; }
    public string ReadyText { get; }

    public string KeptText => Plan.Kept.Count == 0 ? "" : "Stay on: " + string.Join(", ", Plan.Kept.Select(m => m.DisplayName));
    public string TurnedOffText => Plan.TurnedOff.Count == 0 ? "" : "Off for this game: " + string.Join(", ", Plan.TurnedOff.Select(m => m.DisplayName));
    public string Warnings => string.Join("\n\n", Plan.Warnings);

    /// <summary>Text the search box matches against.</summary>
    public string SearchText => $"{Name} {Room.Host} {Ship} {Sector} {string.Join(' ', Lobby.Mods.Select(m => m.DisplayName))}";

    private static string Quest(string? quest) => quest switch
    {
        "Survivor" => "MISSION",
        "Pilgrimage" => "PILGRIMAGE",
        "Endless" => "ENDLESS",
        "Tutorial" => "TUTORIAL",
        "Special" => "SPECIAL",
        _ => "MISSION",
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
    public string Author => Line.Mod.Package?.Author ?? (Line.Mod.Local ? "host's own mod" : "");
    public bool Needed => Line.Mod.JoinerNeeds;

    public string NeedText => Line.Mod.Need switch
    {
        ModNeed.Everyone => "EVERYONE",
        ModNeed.Session => "HOST",
        ModNeed.HostOnly => "HOST",
        ModNeed.ClientOnly => "HOST'S CLIENT",
        _ => "PROBABLY",
    };

    public string StatusText => Line.Status switch
    {
        LobbyModStatus.Ready => "INSTALLED",
        LobbyModStatus.Download => "DOWNLOAD",
        LobbyModStatus.Unavailable => "UNAVAILABLE",
        _ => "NOT NEEDED",
    };

    public string StatusKey => Line.Status.ToString();
    public string? Note => Line.Note;
}
