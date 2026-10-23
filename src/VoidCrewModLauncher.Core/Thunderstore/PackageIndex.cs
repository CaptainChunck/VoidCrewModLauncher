using System.Text;

namespace VoidCrewModLauncher.Core.Thunderstore;

/// <summary>Lookups over Thunderstore's package list: by id, by VoidManager's download id, and by a mod's name.</summary>
public sealed class PackageIndex
{
    /// <summary>Mod managers and other things in the community list that aren't mods.</summary>
    private static readonly HashSet<string> NotMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ebkr-r2modman",
        "Kesomannen-GaleModManager",
        "Thunderstore-Thunderstore_Mod_Manager",
    };

    public const string BepInExPackId = "BepInEx-BepInExPack";

    private readonly Dictionary<string, TsPackage> _byId;

    public PackageIndex(IEnumerable<TsPackage> packages)
    {
        All = packages.Where(p => p.Versions.Count > 0).ToList();
        _byId = new Dictionary<string, TsPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (TsPackage package in All) _byId[package.FullName] = package;
    }

    public static PackageIndex Empty { get; } = new(Array.Empty<TsPackage>());

    /// <summary>
    /// This index with <paramref name="preferred"/> packages (voidcrewmod.com's) put first: a package both have takes the
    /// preferred one's details and versions, keeping only versions the preferred source doesn't have; new ones are added.
    /// </summary>
    public PackageIndex Prefer(IReadOnlyCollection<TsPackage> preferred)
    {
        if (preferred.Count == 0) return this;
        var byId = preferred.Where(p => p.Versions.Count > 0).GroupBy(p => p.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        TsPackage Combine(TsPackage mine, TsPackage? other) => other == null
            ? mine
            : mine.WithVersions(other.Versions.Where(v => mine.Version(v.VersionNumber) == null));
        var result = All.Select(p => byId.TryGetValue(p.FullName, out TsPackage? site) ? Combine(site, p) : p).ToList();
        result.AddRange(byId.Values.Where(p => !_byId.ContainsKey(p.FullName)));
        return new PackageIndex(result);
    }

    /// <summary>
    /// This index with up-to-date packages merged in (from Thunderstore's per-package API): versions it doesn't have are
    /// added to their package, and packages it doesn't have are added. Returns this same index when nothing is new.
    /// </summary>
    public PackageIndex Merge(IEnumerable<TsPackage> fresh)
    {
        var replaced = new Dictionary<string, TsPackage>(StringComparer.OrdinalIgnoreCase);
        var added = new List<TsPackage>();
        foreach (TsPackage update in fresh)
        {
            if (update.Versions.Count == 0) continue;
            TsPackage? current = replaced.TryGetValue(update.FullName, out TsPackage? done) ? done
                : _byId.TryGetValue(update.FullName, out TsPackage? known) ? known
                : added.FirstOrDefault(p => string.Equals(p.FullName, update.FullName, StringComparison.OrdinalIgnoreCase));
            if (current == null)
            {
                added.Add(update.WithVersions(Array.Empty<TsVersion>()));
                continue;
            }
            if (update.Versions.All(v => current.Version(v.VersionNumber) != null)) continue;
            TsPackage merged = current.WithVersions(update.Versions);
            int addedAt = added.IndexOf(current);
            if (addedAt >= 0) added[addedAt] = merged;
            else replaced[update.FullName] = merged;
        }
        if (replaced.Count == 0 && added.Count == 0) return this;
        return new PackageIndex(All.Select(p => replaced.TryGetValue(p.FullName, out TsPackage? merged) ? merged : p).Concat(added));
    }

    public IReadOnlyList<TsPackage> All { get; }

    /// <summary>What the Mods tab lists: Thunderstore's mods, not BepInEx itself, not mod managers, not NSFW.</summary>
    public IEnumerable<TsPackage> Mods => All.Where(p => !p.IsGitHubOnly && !NotMods.Contains(p.FullName) && !p.HasNsfwContent && !IsBepInExPack(p.FullName));

    public static bool IsBepInExPack(string id) => string.Equals(id, BepInExPackId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds a package from "Namespace-Name", "Namespace/Name", "Namespace-Name-1.2.3" or a Thunderstore package URL
    /// (VoidManager's download ids use the "Namespace/Name" form).
    /// </summary>
    public TsPackage? Find(string? reference)
    {
        string? id = NormalizeId(reference);
        if (id == null) return null;
        if (_byId.TryGetValue(id, out TsPackage? package)) return package;
        PackageReference? parsed = PackageReference.Parse(id);
        return parsed != null && _byId.TryGetValue(parsed.Value.Id, out package) ? package : null;
    }

    public static string? NormalizeId(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string text = reference.Trim();
        int marker = text.IndexOf("/p/", StringComparison.OrdinalIgnoreCase);
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase) && marker >= 0) text = text[(marker + 3)..];
        text = text.Trim('/');
        string[] parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) text = $"{parts[0]}-{parts[1]}";
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Best guess at the package of a mod known only by its plugin name (e.g. from VoidManager's list, or a DLL found
    /// in another BepInEx folder). Exact matches of the name win; then the name without "Void Crew"; then containment.
    /// Ties go to packages that aren't deprecated, then to the most downloaded.
    /// </summary>
    public TsPackage? FindByName(string? modName)
    {
        string wanted = Simplify(modName);
        if (wanted.Length < 3) return null;
        string withoutGame = wanted.StartsWith("voidcrew", StringComparison.Ordinal) && wanted.Length > 10 ? wanted[8..] : wanted;

        TsPackage? best = null;
        int bestScore = 0;
        foreach (TsPackage package in Mods)
        {
            string name = Simplify(package.Name);
            int score = 0;
            if (name == wanted) score = 4;
            else if (name == withoutGame || Simplify("VoidCrew" + package.Name) == wanted) score = 3;
            else if (name.Length >= 6 && withoutGame.Length >= 6 && (name.Contains(withoutGame) || withoutGame.Contains(name))) score = 1;
            if (score == 0) continue;
            if (!package.IsDeprecated) score = score * 2 + 1;
            else score *= 2;
            if (score > bestScore || (score == bestScore && best != null && package.TotalDownloads > best.TotalDownloads))
            {
                best = package;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>Lower case letters and digits only: "Void Crew VR UI Fix" and "Void_Crew_VR_UI_Fix" are the same.</summary>
    public static string Simplify(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var simple = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) simple.Append(char.ToLowerInvariant(c));
        }
        return simple.ToString();
    }
}
