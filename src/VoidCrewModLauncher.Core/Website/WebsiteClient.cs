using System.Text.Json;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Core.Website;

/// <summary>
/// voidcrewmod.com, the user's own Void Crew mod site, which the launcher prefers over Thunderstore. Its package list
/// (<see cref="PackageListUrl"/>) has the same JSON as Thunderstore's v1 list (snake_case: name, full_name, owner,
/// package_url, categories, versions with version_number, download_url, dependencies, ...), and each version may add
/// "sha256", which is checked after download. While the site has no list (or can't be reached), the last list read is
/// used, else none: Thunderstore covers everything then.
/// </summary>
public sealed class WebsiteClient : IDisposable
{
    /// <summary>
    /// Points the launcher at a test copy of the site (README); in every build, like VCML_UPDATE_FEED (whatever can set it
    /// can already change the launcher's files), and the log says when it is in use.
    /// </summary>
    public const string SiteVariable = "VCML_SITE_URL";
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private bool _toldUnavailable;

    public WebsiteClient(AppPaths paths, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        if (Environment.GetEnvironmentVariable(SiteVariable) is { Length: > 0 }) AppLog.Warn($"{SiteVariable} is set: reading mods from {Site} instead of {AppInfo.WebsiteUrl}.");
    }

    /// <summary>The site's address (VCML_SITE_URL can point at a test copy).</summary>
    public static string Site => (Environment.GetEnvironmentVariable(SiteVariable) is { Length: > 0 } custom ? custom : AppInfo.WebsiteUrl).TrimEnd('/');

    public static string PackageListUrl => $"{Site}/api/v1/package/";

    private string CacheFile => Path.Combine(_paths.Cache, "website-packages.json");

    /// <summary>The site's packages, every version marked as from the website. Never throws for a site that's down.</summary>
    public async Task<List<TsPackage>> GetPackagesAsync(bool refresh, CancellationToken cancel = default)
    {
        if (!refresh && File.Exists(CacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < CacheAge && Read(await File.ReadAllBytesAsync(CacheFile, cancel)) is { } cached)
        {
            return cached;
        }
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(PackageListUrl, cancel);
            response.EnsureSuccessStatusCode();
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancel);
            List<TsPackage> packages = Read(body) ?? throw new InvalidDataException("the package list isn't JSON the launcher can read");
            Directory.CreateDirectory(_paths.Cache);
            await File.WriteAllBytesAsync(CacheFile, body, cancel);
            AppLog.Info($"voidcrewmod.com: {packages.Count} packages.");
            _toldUnavailable = false;
            return packages;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            if (!_toldUnavailable) AppLog.Info($"voidcrewmod.com's package list isn't available ({e.Message}); using Thunderstore.");
            _toldUnavailable = true;
            return File.Exists(CacheFile) && Read(await File.ReadAllBytesAsync(CacheFile, cancel)) is { } stale ? stale : new List<TsPackage>();
        }
    }

    private static List<TsPackage>? Read(byte[] body)
    {
        try
        {
            List<TsPackage>? packages = JsonSerializer.Deserialize<List<TsPackage>>(body, Json);
            if (packages == null) return null;
            foreach (TsVersion version in packages.SelectMany(p => p.Versions))
            {
                version.FromWebsite = true;
                version.Sha256 = string.IsNullOrWhiteSpace(version.Sha256) ? null : version.Sha256.Trim().ToLowerInvariant();
            }
            return packages.Where(p => p.Versions.Count > 0 && PackageReference.Parse(p.FullName) != null).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
