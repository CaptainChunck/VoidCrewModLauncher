using System.Text.Json.Serialization;
using VoidCrewModLauncher.Core.GitHub;

namespace VoidCrewModLauncher.Core.Thunderstore;

/// <summary>A package from Thunderstore's v1 package list (snake_case JSON).</summary>
public sealed class TsPackage
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Owner { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string? DonationLink { get; set; }
    public DateTimeOffset DateCreated { get; set; }
    public DateTimeOffset DateUpdated { get; set; }
    public int RatingScore { get; set; }
    public bool IsPinned { get; set; }
    public bool IsDeprecated { get; set; }
    public bool HasNsfwContent { get; set; }
    public List<string> Categories { get; set; } = new();
    public List<TsVersion> Versions { get; set; } = new();

    /// <summary>The newest version, from Thunderstore or, when it is newer, from the mod's GitHub releases.</summary>
    [JsonIgnore]
    public TsVersion? Latest => Versions.Count == 0 ? null : Versions.MaxBy(v => v.VersionNumber, VersionNumber.Comparer);

    [JsonIgnore]
    public TsVersion? LatestOnThunderstore => Versions.Where(v => v.GitHub == null).MaxBy(v => v.VersionNumber, VersionNumber.Comparer);

    /// <summary>A mod that is only on GitHub (see GitHubModCatalog): found by id, but not in the MODS list.</summary>
    [JsonIgnore]
    public bool IsGitHubOnly { get; init; }

    /// <summary>
    /// Thunderstore rejected the package's Void Crew listing: a lobby can still bring it in from the mod's own GitHub
    /// releases (see LauncherService.FreshenLobbyAsync), never with Thunderstore's files, and it isn't in the MODS list.
    /// </summary>
    [JsonIgnore]
    public bool ListingRejected { get; init; }

    /// <summary>The newest version comes from voidcrewmod.com.</summary>
    [JsonIgnore]
    public bool LatestFromWebsite => Latest?.FromWebsite == true;

    [JsonIgnore]
    public long TotalDownloads => Versions.Sum(v => (long)v.Downloads);

    [JsonIgnore]
    public string DisplayName => Name.Replace('_', ' ');

    [JsonIgnore]
    public string Author => Owner.Replace('_', ' ');

    public TsVersion? Version(string? version) =>
        string.IsNullOrEmpty(version) ? null : Versions.FirstOrDefault(v => VersionNumber.Same(v.VersionNumber, version));

    /// <summary>A copy of this package with <paramref name="extra"/> versions added (newest first); this one isn't changed.</summary>
    public TsPackage WithVersions(IEnumerable<TsVersion> extra)
    {
        var copy = (TsPackage)MemberwiseClone();
        copy.Versions = extra.Where(v => Version(v.VersionNumber) == null).Concat(Versions)
            .OrderByDescending(v => v.VersionNumber, VersionNumber.Comparer).ToList();
        if (copy.Versions.Count > 0 && copy.Versions.Max(v => v.DateCreated) is var newest && newest > copy.DateUpdated) copy.DateUpdated = newest;
        return copy;
    }

    /// <summary>A copy of this package with <paramref name="version"/> as its only version (and none of its own).</summary>
    public TsPackage WithOnly(TsVersion version)
    {
        TsPackage copy = WithVersions(Array.Empty<TsVersion>());
        copy.Versions = new List<TsVersion> { version };
        if (version.DateCreated > copy.DateUpdated) copy.DateUpdated = version.DateCreated;
        return copy;
    }
}

public sealed class TsVersion
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public List<string> Dependencies { get; set; } = new();
    public string DownloadUrl { get; set; } = "";
    public int Downloads { get; set; }
    public DateTimeOffset DateCreated { get; set; }
    public string WebsiteUrl { get; set; } = "";
    public bool IsActive { get; set; }
    public long FileSize { get; set; }

    /// <summary>Set when this version comes from the mod's GitHub releases instead of Thunderstore.</summary>
    [JsonIgnore]
    public GitHubSource? GitHub { get; set; }

    /// <summary>The download's SHA-256 (lower-case hex), when the source lists it (voidcrewmod.com does); checked after download.</summary>
    public string? Sha256 { get; set; }

    /// <summary>This version comes from voidcrewmod.com, which the launcher prefers over Thunderstore.</summary>
    [JsonIgnore]
    public bool FromWebsite { get; set; }
}

/// <summary>A Thunderstore dependency string: Namespace-Name-1.2.3.</summary>
public readonly record struct PackageReference(string Namespace, string Name, string? Version)
{
    public string Id => $"{Namespace}-{Name}";

    public static PackageReference? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Trim().Split('-');
        if (parts.Length < 2) return null;
        if (parts.Length >= 3 && LooksLikeVersion(parts[^1]))
        {
            return new PackageReference(string.Join('-', parts[..^2]), parts[^2], parts[^1]);
        }
        return new PackageReference(string.Join('-', parts[..^1]), parts[^1], null);
    }

    private static bool LooksLikeVersion(string text) => text.Length > 0 && char.IsDigit(text[0]) && text.Contains('.');

    public override string ToString() => Version == null ? Id : $"{Id}-{Version}";
}
