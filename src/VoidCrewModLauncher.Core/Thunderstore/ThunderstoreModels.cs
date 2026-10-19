using System.Text.Json.Serialization;

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

    [JsonIgnore]
    public TsVersion? Latest => Versions.Count == 0 ? null : Versions.MaxBy(v => v.VersionNumber, VersionNumber.Comparer);

    [JsonIgnore]
    public long TotalDownloads => Versions.Sum(v => (long)v.Downloads);

    [JsonIgnore]
    public string DisplayName => Name.Replace('_', ' ');

    [JsonIgnore]
    public string Author => Owner.Replace('_', ' ');

    public TsVersion? Version(string? version) =>
        string.IsNullOrEmpty(version) ? null : Versions.FirstOrDefault(v => VersionNumber.Same(v.VersionNumber, version));
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
