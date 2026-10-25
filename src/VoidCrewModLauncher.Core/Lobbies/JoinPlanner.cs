using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Lobbies;

public enum LobbyModStatus
{
    /// <summary>In the store at the version the lobby uses (for the host's local mod: your own copy of it).</summary>
    Ready,

    /// <summary>Will be downloaded from Thunderstore.</summary>
    Download,

    /// <summary>The host's local mod that you don't have at its version, or not on Thunderstore: the launcher can't get it.</summary>
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

    /// <summary>Your copy of the host's local mod (the same plugin at the same version), loaded for this game.</summary>
    public InstalledMod? Copy { get; init; }
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
/// Works out how to join a lobby: its mods that joining players need (exact versions where Thunderstore has them, and
/// your own copies of the host's local mods), plus your client-side mods, plus every dependency; everything else you
/// have turned on stays off for that game.
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
            plan.Warnings.Add(Loc.T("This lobby is modded but doesn't list its mods. If the game turns you away, the launcher looks up the mods it names."));
        }

        IReadOnlyList<InstalledMod> library = _manager.Library.Mods;
        foreach (LobbyMod mod in lobby.Mods)
        {
            bool needed = mod.JoinerNeeds;
            TsVersion? version = mod.Local ? null : mod.PackageVersion ?? mod.Package?.Latest;
            string? note = null;
            InstalledMod? copy = null;
            LobbyModStatus status;
            if (mod.Local)
            {
                // Nothing to download, but you may have the same plugin: added from your PC too, or from Thunderstore.
                InstalledMod? otherVersion = null;
                if (needed) copy = FindCopy(mod, library, m => _manager.Store.Has(m.Id, m.Version), out otherVersion);
                status = !needed ? LobbyModStatus.NotNeeded : copy != null ? LobbyModStatus.Ready : LobbyModStatus.Unavailable;
                if (status == LobbyModStatus.Unavailable) plan.Warnings.Add(MissingLocalMod(mod, otherVersion));
            }
            else
            {
                if (needed && mod.Package != null && mod.PackageVersion == null && version != null)
                {
                    note = Loc.F("The host has {0}; Thunderstore doesn't have that version, so {1} is used.", mod.Version, version.VersionNumber);
                }
                if (!needed) status = LobbyModStatus.NotNeeded;
                else if (!mod.Downloadable || version == null) status = LobbyModStatus.Unavailable;
                else status = _manager.Store.Has(mod.Package!.FullName, version.VersionNumber) ? LobbyModStatus.Ready : LobbyModStatus.Download;
                if (needed && status == LobbyModStatus.Unavailable)
                {
                    plan.Warnings.Add(Loc.F("{0} {1} isn't on Thunderstore. You'll need to get it yourself.", mod.DisplayName, mod.Version));
                }
            }

            plan.Lines.Add(new JoinPlanLine
            {
                Mod = mod,
                Include = needed && status != LobbyModStatus.Unavailable,
                Optional = needed && mod.Need == ModNeed.Unknown,
                Status = status,
                Version = version,
                Note = note,
                Copy = copy,
            });
        }

        List<JoinPlanLine> loaded = plan.Lines.Where(l => l.Include).ToList();
        var neededNames = new HashSet<string>(loaded.Select(l => PackageIndex.Simplify(l.Mod.Package?.Name ?? l.Mod.Name)));
        var copies = new HashSet<string>(loaded.Where(l => l.Copy != null).Select(l => l.Copy!.Id), StringComparer.OrdinalIgnoreCase);
        // The plugins this game loads for the lobby: another copy of one of them would load it twice.
        var loadedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JoinPlanLine line in loaded)
        {
            loadedGuids.UnionWith(line.Mod.PluginGuids);
            if (line.Copy != null) loadedGuids.UnionWith(line.Copy.PluginGuids);
            else if (line.Mod.Package != null && _manager.Library.Find(line.Mod.Package.FullName) is { } installed) loadedGuids.UnionWith(installed.PluginGuids);
        }
        foreach (InstalledMod mod in _manager.Library.Enabled)
        {
            // Loaded as the lobby's own mod: its version from Thunderstore, or your copy of the host's local mod.
            if (copies.Contains(mod.Id)) continue;
            if (loaded.Any(l => l.Mod.Package != null && string.Equals(l.Mod.Package.FullName, mod.Id, StringComparison.OrdinalIgnoreCase))) continue;
            if (mod.PluginGuids.Any(loadedGuids.Contains) || (mod.IsLocal && neededNames.Contains(PackageIndex.Simplify(mod.Name)))) plan.TurnedOff.Add(mod);
            else if (ModPolicy.KeepWhenJoining(mod))
            {
                plan.Kept.Add(mod);
                // Kept because you chose so: VoidManager turns away a player who has an "All" mod the host doesn't run.
                if (mod.CheckedByVoidManager) plan.Warnings.Add(Loc.F("{0} stays on, but this lobby doesn't run it: the game may turn you away.", mod.DisplayName));
            }
            else plan.TurnedOff.Add(mod);
        }
        return plan;
    }

    /// <summary>
    /// Your copy of the host's local mod: an installed mod (added from your PC, or from Thunderstore) that has one of
    /// its plugin GUIDs, at the same version; or, when the lobby doesn't say its GUIDs (a host before launcher 0.8.1
    /// without VoidManager), one with the same name. A local mod's id can't be compared: it comes from the file it was
    /// added from on each PC. <paramref name="otherVersion"/> is a copy at another version, when that's all there is.
    /// </summary>
    public static InstalledMod? FindCopy(LobbyMod mod, IEnumerable<InstalledMod> library, Func<InstalledMod, bool> inStore, out InstalledMod? otherVersion)
    {
        string name = PackageIndex.Simplify(mod.Name);
        List<InstalledMod> candidates = library
            .Where(m => mod.PluginGuids.Count > 0
                ? m.PluginGuids.Any(g => mod.PluginGuids.Contains(g, StringComparer.OrdinalIgnoreCase))
                : name.Length > 0 && PackageIndex.Simplify(m.Name) == name)
            .Where(inStore)
            .OrderByDescending(m => m.Enabled)
            .ThenByDescending(m => m.IsLocal)
            .ToList();
        otherVersion = null;
        bool anyVersion = string.IsNullOrEmpty(mod.Version) || mod.Version == "0";
        foreach (InstalledMod candidate in candidates)
        {
            if (anyVersion || VersionNumber.Same(candidate.Version, mod.Version)) return candidate;
            otherVersion ??= candidate;
        }
        return null;
    }

    /// <summary>Which of the host's local mods you're missing, by name and plugin GUID, and where to get it.</summary>
    private static string MissingLocalMod(LobbyMod mod, InstalledMod? otherVersion)
    {
        List<string> guids = mod.PluginGuids.Count > 0 ? mod.PluginGuids : otherVersion?.PluginGuids ?? new List<string>();
        string label = guids.Count > 0 ? $"{mod.DisplayName} ({string.Join(", ", guids)})" : mod.DisplayName;
        return otherVersion != null
            ? Loc.F("{0} is the host's local mod at version {1}, and you have {2}. It can't be downloaded: get the host's version from the host or its author.", label, mod.Version, otherVersion.Version)
            : Loc.F("{0} is the host's local mod: it can't be downloaded. Get it from the host or its author.", label);
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
        // Your copies of the host's local mods: from the store as they are (with their dependencies when from Thunderstore).
        foreach (JoinPlanLine line in plan.Lines.Where(l => l.Include && l.Copy != null))
        {
            InstalledMod copy = line.Copy!;
            TsPackage? package = copy.IsLocal ? null : _manager.Index.Find(copy.Id);
            TsVersion? version = package?.Version(copy.Version);
            if (package != null && version != null)
            {
                if (pinned.ContainsKey(package.FullName)) continue;
                roots.Add((package, version));
                pinned[package.FullName] = version.VersionNumber;
                needs[package.FullName] = line.Mod.Need;
                continue;
            }
            string folder = _manager.Store.VersionDirectory(copy.Id, copy.Version);
            if (Directory.Exists(folder)) resolved.Mods.Add(new RuntimeMod(copy.Id, copy.Name, copy.Version, folder, copy.IsLocal, line.Mod.Need));
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
