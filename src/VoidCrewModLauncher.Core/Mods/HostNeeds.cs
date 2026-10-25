using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Mods;

/// <summary>
/// What players who join your lobby need of each of your mods: your choice on the HOST tab, else the launcher's guess;
/// but always everyone for a mod joining players are checked for, since they'd be turned away without it. Checked are:
/// VoidManager "All" mods (from their DLLs), VoidManager itself while one of those is on, and mods the game's own mod
/// check turned out to require when you last hosted with them (the bridge reports those, see
/// <see cref="HostSettings.CheckedInGame"/>). The bridge applies the same rule in the game (LobbyAdvert.AdvertisedNeed).
/// </summary>
public sealed class HostNeeds
{
    private readonly IReadOnlyDictionary<string, ModNeed> _chosen;
    private readonly HashSet<string> _checkedInGame;
    private readonly bool _voidManagerChecks;

    /// <param name="enabled">Your turned-on mods: one VoidManager "All" mod among them makes VoidManager itself needed.</param>
    public HostNeeds(IEnumerable<InstalledMod> enabled, IReadOnlyDictionary<string, ModNeed> chosen, IEnumerable<string>? checkedInGame)
    {
        _chosen = chosen;
        _checkedInGame = new HashSet<string>(checkedInGame ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _voidManagerChecks = enabled.Any(m => m.CheckedByVoidManager);
    }

    /// <summary>Joining players are turned away without the mod.</summary>
    public bool Checked(InstalledMod mod) =>
        mod.CheckedByVoidManager || (_voidManagerChecks && mod.IsVoidManager) || _checkedInGame.Contains(mod.Id);

    /// <summary>What you picked on the HOST tab, or null for Auto.</summary>
    public ModNeed? Chosen(string id) => _chosen.TryGetValue(id, out ModNeed need) ? need : null;

    /// <summary>What Auto means for the mod: everyone when joining players are checked for it, else the launcher's guess.</summary>
    public ModNeed Auto(InstalledMod mod) => Checked(mod) ? ModNeed.Everyone : mod.GuessedNeed;

    /// <summary>What the lobby lists: everyone when joining players are checked for it, else your choice, else Auto.</summary>
    public ModNeed Effective(InstalledMod mod) => Checked(mod) ? ModNeed.Everyone : Chosen(mod.Id) ?? mod.GuessedNeed;

    /// <summary>You picked less than everyone for a mod joining players are checked for: the lobby lists it for everyone anyway.</summary>
    public bool Overridden(InstalledMod mod) => Checked(mod) && Chosen(mod.Id) is { } chosen && chosen != ModNeed.Everyone;
}
