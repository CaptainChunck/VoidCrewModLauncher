namespace VoidCrewModLauncher.Core.GitHub;

/// <summary>
/// A mod that is only on GitHub (not on Thunderstore), installed from the newest release of its repo.
/// </summary>
/// <param name="Id">Its id in your mods and the store ("github-Owner-Name").</param>
/// <param name="Asset">The release file to install (a zip in BepInEx's plugins/patchers layout).</param>
/// <param name="PluginGuidPrefix">
/// A plugin in the download must have a GUID starting with this: it checks the download, and finds a copy you already
/// have (one imported from Rai Pal, say) so it isn't loaded twice.
/// </param>
public sealed record GitHubMod(string Id, string Name, string Author, GitHubRepo Repo, string Asset, string PluginGuidPrefix, string Description);

/// <summary>
/// The GitHub-only mods the launcher knows, and the mods that need them. Installing or turning on such a mod installs
/// the newest release of what it needs, which then updates like your other mods.
/// </summary>
public sealed class GitHubModCatalog
{
    private readonly List<GitHubMod> _mods;
    private readonly Dictionary<string, string[]> _neededBy;

    public GitHubModCatalog(IEnumerable<GitHubMod> mods, IReadOnlyDictionary<string, string[]> neededBy)
    {
        _mods = mods.ToList();
        _neededBy = new Dictionary<string, string[]>(neededBy, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Universal Unity VR, which the Void Crew VR UI Fix works with. Void Crew is a Mono game on a modern Unity.</summary>
    public static GitHubMod Uuvr { get; } = new(
        "github-Raicuparta-UUVR",
        "UUVR",
        "Raicuparta",
        new GitHubRepo("Raicuparta", "uuvr"),
        "uuvr-mono-modern.zip",
        "raicuparta.uuvr",
        "Universal Unity VR by Raicuparta: plays Void Crew in VR (OpenXR and OpenVR). The Void Crew VR UI Fix needs it.");

    public static GitHubModCatalog Default { get; } = new(
        new[] { Uuvr },
        new Dictionary<string, string[]> { ["Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix"] = new[] { Uuvr.Id } });

    public IReadOnlyList<GitHubMod> All => _mods;

    public GitHubMod? Find(string? id) => _mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The GitHub-only mods <paramref name="id"/> needs.</summary>
    public IReadOnlyList<GitHubMod> NeededBy(string id) =>
        _neededBy.TryGetValue(id, out string[]? ids) ? ids.Select(Find).OfType<GitHubMod>().ToList() : new List<GitHubMod>();
}
