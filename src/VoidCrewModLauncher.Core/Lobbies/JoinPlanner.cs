using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Lobbies;

public enum LobbyModStatus
{
    /// <summary>In the store at the version the lobby uses.</summary>
    Ready,

    /// <summary>Will be downloaded from Thunderstore.</summary>
    Download,

    /// <summary>The host's own mod or not on Thunderstore: the launcher can't get it.</summary>
    Unavailable,

    /// <summary>Joining players don't need it (host-side or the host's own client mod).</summary>
    NotNeeded,
}

/// <summary>One of the lobby's mods in the join dialog.</summary>
public sealed class JoinPlanLine
{
    public required LobbyMod Mod { get; init; }

    /// <summary>Loaded for this join. Players can untick mods the launcher only guessed are needed.</summary>
    public bool Include { get; set; }

    /// <summary>The launcher isn't sure joiners need it (nothing from the host says so).</summary>
    public bool Optional { get; init; }

    public LobbyModStatus Status { get; set; }

    public TsVersion? Version { get; init; }

    public string? Note { get; init; }
}

/// <summary>What joining a lobby takes: which of its mods to load, which of yours stay on, what to download.</summary>
public sealed class JoinPlan
{
    public required Lobby Lobby { get; init; }
    public List<JoinPlanLine> Lines { get; } = new();

    /// <summary>Your enabled mods that stay on (client-side mods such as VR).</summary>
    public List<InstalledMod> Kept { get; } = new();

    /// <summary>Your enabled mods that are off for this game.</summary>
    public List<InstalledMod> TurnedOff { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>The final mod set of a join.</summary>
public sealed class ResolvedJoin
{
    public List<RuntimeMod> Mods { get; } = new();

    /// <summary>Thunderstore versions not in the store yet.</summary>
    public List<(TsPackage Package, TsVersion Version)> Downloads { get; } = new();

    public long DownloadBytes => Downloads.Sum(d => d.Version.FileSize);
}

/// <summary>
/// Works out how to join a lobby: its mods that joining players need (exact versions where Thunderstore has them),
/// plus your client-side mods, plus every dependency; everything else you have turned on stays off for that game.
/// </summary>
public sealed class JoinPlanner
{
    private readonly ModManager _manager;

    public JoinPlanner(ModManager manager)
    {
        _manager = manager;
    }

    public JoinPlan Plan(Lobby lobby)
    {
        var plan = new JoinPlan { Lobby = lobby };
        if (lobby.ModsUnknown)
        {
            plan.Warnings.Add("This lobby is modded but doesn't list its mods. If the game turns you away, the launcher looks up the mods it names.");
        }

        foreach (LobbyMod mod in lobby.Mods)
        {
            bool needed = mod.JoinerNeeds;
            TsVersion? version = mod.PackageVersion ?? mod.Package?.Latest;
            string? note = null;
            if (needed && mod.Package != null && mod.PackageVersion == null && version != null)
            {
                note = $"The host has {mod.Version}; Thunderstore doesn't have that version, so {version.VersionNumber} is used.";
            }

            LobbyModStatus status;
            if (!needed) status = LobbyModStatus.NotNeeded;
            else if (!mod.Downloadable || version == null) status = LobbyModStatus.Unavailable;
            else status = _manager.Store.Has(mod.Package!.FullName, version.VersionNumber) ? LobbyModStatus.Ready : LobbyModStatus.Download;

            plan.Lines.Add(new JoinPlanLine
            {
                Mod = mod,
                Include = needed && status != LobbyModStatus.Unavailable,
                Optional = needed && mod.Need == ModNeed.Unknown,
                Status = status,
                Version = version,
                Note = note,
            });
            if (needed && status == LobbyModStatus.Unavailable)
            {
                plan.Warnings.Add(mod.Local
                    ? $"{mod.DisplayName} is the host's own mod (not on Thunderstore). Ask the host for it, or the game may turn you away."
                    : $"{mod.DisplayName} {mod.Version} isn't on Thunderstore. You'll need to get it yourself.");
            }
        }

        var loaded = plan.Lines.Where(l => l.Include).Select(l => l.Mod).ToList();
        var neededNames = new HashSet<string>(loaded.Select(m => PackageIndex.Simplify(m.Package?.Name ?? m.Name)));
        foreach (InstalledMod mod in _manager.Library.Enabled)
        {
            // The lobby's own version of it is loaded instead.
            if (loaded.Any(m => m.Package != null && string.Equals(m.Package.FullName, mod.Id, StringComparison.OrdinalIgnoreCase))) continue;
            // A local copy of a mod the lobby runs from Thunderstore would load the same plugin twice.
            if (mod.IsLocal && neededNames.Contains(PackageIndex.Simplify(mod.Name))) plan.TurnedOff.Add(mod);
            else if (ModPolicy.KeepWhenJoining(mod)) plan.Kept.Add(mod);
            else plan.TurnedOff.Add(mod);
        }
        return plan;
    }

    /// <summary>The mods to launch with for <paramref name="plan"/>, as the player left the ticks.</summary>
    public ResolvedJoin Resolve(JoinPlan plan)
    {
        var resolved = new ResolvedJoin();
        var roots = new List<(TsPackage, TsVersion)>();
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var needs = new Dictionary<string, ModNeed>(StringComparer.OrdinalIgnoreCase);

        foreach (JoinPlanLine line in plan.Lines.Where(l => l.Include && l.Mod.Package != null && l.Version != null))
        {
            roots.Add((line.Mod.Package!, line.Version!));
            pinned[line.Mod.Package!.FullName] = line.Version!.VersionNumber;
            needs[line.Mod.Package!.FullName] = line.Mod.Need;
        }
        foreach (InstalledMod mod in plan.Kept)
        {
            if (mod.IsLocal)
            {
                string folder = _manager.Store.VersionDirectory(mod.Id, mod.Version);
                if (Directory.Exists(folder)) resolved.Mods.Add(new RuntimeMod(mod.Id, mod.Name, mod.Version, folder, true, ModNeed.ClientOnly));
                continue;
            }
            TsPackage? package = _manager.Index.Find(mod.Id);
            TsVersion? version = package?.Version(mod.Version);
            if (package == null || version == null || pinned.ContainsKey(package.FullName))
            {
                if (package == null || version == null) AddInstalled(resolved, mod);
                continue;
            }
            roots.Add((package, version));
            needs[package.FullName] = mod.GuessedNeed;
        }

        foreach ((TsPackage package, TsVersion version) in _manager.Resolve(roots, pinned))
        {
            string folder = _manager.Store.VersionDirectory(package.FullName, version.VersionNumber);
            if (!_manager.Store.Has(package.FullName, version.VersionNumber)) resolved.Downloads.Add((package, version));
            ModNeed need = needs.TryGetValue(package.FullName, out ModNeed known) ? known : ModPolicy.GuessNeed(package.FullName, package.Categories);
            resolved.Mods.Add(new RuntimeMod(package.FullName, package.Name, version.VersionNumber, folder, false, need));
        }
        return resolved;
    }

    /// <summary>An installed mod Thunderstore no longer lists: use the copy in the store.</summary>
    private void AddInstalled(ResolvedJoin resolved, InstalledMod mod)
    {
        string folder = _manager.Store.VersionDirectory(mod.Id, mod.Version);
        if (Directory.Exists(folder)) resolved.Mods.Add(new RuntimeMod(mod.Id, mod.Name, mod.Version, folder, mod.IsLocal, mod.GuessedNeed));
    }

    /// <summary>
    /// Whether the game as it runs now can join without a restart: it has every mod the join needs at the same
    /// version, and nothing else that could make the host's game or VoidManager turn the player away.
    /// </summary>
    public static bool RunningGameFits(ResolvedJoin join, LaunchFile? running)
    {
        if (running?.Mods == null) return false;
        var have = new Dictionary<string, LaunchMod>(StringComparer.OrdinalIgnoreCase);
        foreach (LaunchMod loaded in running.Mods) have.TryAdd(loaded.Id, loaded);
        foreach (RuntimeMod mod in join.Mods)
        {
            if (!have.TryGetValue(mod.Id, out LaunchMod? loaded) || !VersionNumber.Same(loaded.Version, mod.Version)) return false;
        }
        var wanted = new HashSet<string>(join.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        foreach (LaunchMod extra in running.Mods.Where(m => !wanted.Contains(m.Id)))
        {
            ModNeed need = LobbyAdvert.ParseNeed(extra.Need);
            if (need is ModNeed.Everyone or ModNeed.Unknown) return false;
        }
        return true;
    }
}
