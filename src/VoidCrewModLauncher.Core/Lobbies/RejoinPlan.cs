using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Lobbies;

/// <summary>
/// FIX AND REJOIN: what to change after the game was turned away from a lobby for its mods. The game names what the
/// lobby wanted that this game didn't have, and what it had that the lobby doesn't allow, by the names mods register
/// with it (a mod's name, or one of its missions or items). Missing mods found on Thunderstore are added and disallowed
/// ones left out. When the game names something the launcher can't find, the host's other mods (listed as only the
/// host's) are loaded too: a host on an older launcher may not have listed a mod the game requires.
/// </summary>
public sealed class RejoinPlan
{
    /// <summary>Names shorter than this (letters and digits) match too many mods to go by.</summary>
    public const int MinNameLength = 3;

    /// <summary>Missing mods found on Thunderstore that the join didn't load: they are added.</summary>
    public List<(string Name, TsPackage Package)> Found { get; } = new();

    /// <summary>Missing mods whose package the join loaded already (the host may have another version of it): nothing to add.</summary>
    public List<(string Name, TsPackage Package)> AlreadyLoaded { get; } = new();

    /// <summary>Missing mods the launcher can't find.</summary>
    public List<string> NotFound { get; } = new();

    /// <summary>Mods of the join the lobby doesn't allow: they are left out.</summary>
    public List<RuntimeMod> Disallowed { get; } = new();

    /// <summary>The host's mods the join left out, loaded too because the game named something the launcher can't find.</summary>
    public List<LobbyMod> HostsOtherMods { get; } = new();

    /// <summary>There is something to change: a mod to add or one to leave out.</summary>
    public bool CanFix => Found.Count > 0 || Disallowed.Count > 0 || HostsOtherMods.Count > 0;

    /// <summary>Works out the fix for <paramref name="join"/> from what the game named when <paramref name="lobby"/> turned it away.</summary>
    public static RejoinPlan For(Lobby lobby, ResolvedJoin join, IEnumerable<ModNameDto>? missing, IEnumerable<ModNameDto>? invalid, PackageIndex index)
    {
        var plan = new RejoinPlan();
        foreach (ModNameDto mod in missing ?? Enumerable.Empty<ModNameDto>())
        {
            string name = ShownName(mod);
            TsPackage? package = index.FindByName(mod.DisplayName) ?? index.FindByName(StripVersion(mod.Name));
            if (package == null) plan.NotFound.Add(name);
            else if (Loads(join, package.FullName)) plan.AlreadyLoaded.Add((name, package));
            else plan.Found.Add((name, package));
        }

        foreach (ModNameDto mod in invalid ?? Enumerable.Empty<ModNameDto>())
        {
            // The whole name, never a part of it: "HUD" isn't every mod whose name ends in HUD, and an empty name
            // isn't every mod at all.
            HashSet<string> names = NamesOf(mod);
            if (names.Count == 0) continue;
            foreach (RuntimeMod loaded in join.Mods)
            {
                if (plan.Disallowed.Contains(loaded)) continue;
                if (names.Contains(PackageIndex.Simplify(loaded.Name)) || names.Contains(PackageIndex.Simplify(loaded.Id))) plan.Disallowed.Add(loaded);
            }
        }

        // The game often names what's missing by a mod's assets (a mission, an item) rather than the mod. The host's own
        // mods this join left out (listed as only the host's) are then the likely owners: a mod the game requires that the
        // host didn't list for everyone (hosts before launcher 0.8.1 couldn't tell).
        if (plan.NotFound.Count > 0)
        {
            plan.HostsOtherMods.AddRange(lobby.Mods.Where(m => m is { Downloadable: true, JoinerNeeds: false, Need: not ModNeed.ClientOnly }
                                                               && (m.PackageVersion ?? m.Package!.Latest) != null
                                                               && !Loads(join, m.Package!.FullName)));
        }
        return plan;
    }

    /// <summary>
    /// The join with the fix: without the disallowed mods, with the missing ones (at their newest versions) and the host's
    /// other mods (at the host's versions where Thunderstore has them), and with what those need.
    /// </summary>
    public ResolvedJoin FixedJoin(ResolvedJoin join, ModManager mods)
    {
        var fixedJoin = new ResolvedJoin();
        fixedJoin.Mods.AddRange(join.Mods.Where(m => !Disallowed.Contains(m)));
        var roots = Found.Select(f => (f.Package, f.Package.Latest!)).ToList();
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (LobbyMod mod in HostsOtherMods)
        {
            TsVersion version = (mod.PackageVersion ?? mod.Package!.Latest)!;
            roots.Add((mod.Package!, version));
            pinned[mod.Package!.FullName] = version.VersionNumber;
        }
        foreach ((TsPackage package, TsVersion version) in mods.Resolve(roots, pinned))
        {
            if (Loads(fixedJoin, package.FullName)) continue;
            string folder = mods.Store.VersionDirectory(package.FullName, version.VersionNumber);
            fixedJoin.Mods.Add(new RuntimeMod(package.FullName, package.Name, version.VersionNumber, folder, false, ModNeed.Everyone));
            if (!mods.Store.Has(package.FullName, version.VersionNumber)) fixedJoin.Downloads.Add((package, version));
        }
        return fixedJoin;
    }

    /// <summary>"Mod Name 1.2.3 (beta)" as "Mod Name": the game's names can carry a version and notes after the name.</summary>
    public static string StripVersion(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Where(w => !(char.IsDigit(w[0]) && w.Contains('.')) && !w.StartsWith('(')));
    }

    /// <summary>The name to show for what the game named: its display name, else its name (an id when it had no name).</summary>
    private static string ShownName(ModNameDto mod) =>
        !string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.DisplayName : !string.IsNullOrWhiteSpace(mod.Name) ? mod.Name : "?";

    /// <summary>
    /// The simplified names a mod the game named goes by: its display name and its name, without a version, each also
    /// without "Void Crew" in front; names too short to tell mods apart are left out.
    /// </summary>
    private static HashSet<string> NamesOf(ModNameDto mod)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? text in new[] { mod.DisplayName, mod.Name })
        {
            string simple = PackageIndex.Simplify(StripVersion(text));
            if (simple.Length < MinNameLength) continue;
            names.Add(simple);
            if (simple.StartsWith("voidcrew", StringComparison.Ordinal) && simple.Length >= 8 + MinNameLength) names.Add(simple[8..]);
        }
        return names;
    }

    private static bool Loads(ResolvedJoin join, string id) => join.Mods.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
