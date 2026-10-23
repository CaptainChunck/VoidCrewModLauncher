using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Mods;

/// <summary>
/// The launcher's rules of thumb for mods it knows nothing else about: what joining players need (from Thunderstore's
/// community categories, which authors pick themselves), and whether one of your mods is safe to keep when you join
/// someone else's lobby. A lobby's own advert, VoidManager's multiplayer types and the host's choices on the HOST tab
/// all come before these guesses.
/// </summary>
public static class ModPolicy
{
    /// <summary>Mods whose categories don't say what they need, checked by hand.</summary>
    private static readonly Dictionary<string, ModNeed> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Everyone in a Fleet or Ship Customizer room needs the mod (both register with the game's own mod check).
        ["Captain_Chuncks_Mods-Void_Crew_Fleet"] = ModNeed.Everyone,
        ["Captain_Chuncks_Mods-Ship_Customizer"] = ModNeed.Everyone,
        ["Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix"] = ModNeed.ClientOnly,
        // VR for the player's own game only.
        ["github-Raicuparta-UUVR"] = ModNeed.ClientOnly,
        // Libraries: whoever needs them gets them as a dependency.
        ["NihilityShift-VoidManager"] = ModNeed.ClientOnly,
        ["VoidCrewModdingTeam-VoidManager"] = ModNeed.ClientOnly,
        [PackageIndex.BepInExPackId] = ModNeed.ClientOnly,
    };

    public static ModNeed GuessNeed(string id, IReadOnlyCollection<string>? categories, bool local = false)
    {
        if (Known.TryGetValue(id, out ModNeed known)) return known;
        if (categories == null || categories.Count == 0) return local ? ModNeed.ClientOnly : ModNeed.Unknown;

        bool Has(string category) => categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        bool client = Has("Client-Side");
        bool host = Has("Host-Side");
        if (Has("All Required")) return ModNeed.Everyone;
        if (client && !host) return ModNeed.ClientOnly;
        if (host && !client) return ModNeed.HostOnly;
        if (Has("Mod_Session Required") && !client) return ModNeed.Session;
        if (Has("Cosmetics") || Has("Decorations")) return ModNeed.ClientOnly;
        return ModNeed.Unknown;
    }

    /// <summary>Whether a mod a lobby runs is one a joining player has to load.</summary>
    public static bool JoinerNeeds(ModNeed need) => need is ModNeed.Everyone or ModNeed.Unknown;

    /// <summary>Whether one of your enabled mods stays on when you join a lobby that doesn't run it.</summary>
    public static bool KeepWhenJoining(InstalledMod mod) => mod.WhenJoining switch
    {
        JoinBehaviour.Keep => true,
        JoinBehaviour.OnlyIfLobbyHasIt => false,
        _ => mod.GuessedNeed == ModNeed.ClientOnly,
    };

    public static string Describe(ModNeed need) => need switch
    {
        ModNeed.Everyone => "Everyone needs it",
        ModNeed.Session => "Host's mod (modded lobby)",
        ModNeed.HostOnly => "Host's mod",
        ModNeed.ClientOnly => "Host's own client mod",
        _ => "Probably needed",
    };
}
