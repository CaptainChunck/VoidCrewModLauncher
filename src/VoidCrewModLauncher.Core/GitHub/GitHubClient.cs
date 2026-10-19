using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Core.GitHub;

/// <summary>A GitHub repository, from a mod's website address.</summary>
public sealed record GitHubRepo(string Owner, string Name)
{
    /// <summary>"https://github.com/Owner/Repo" (anything after it, or ".git", is fine) as a repo; null for anything else.</summary>
    public static GitHubRepo? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return Valid(parts[0]) && Valid(name) ? new GitHubRepo(parts[0], name) : null;
    }

    private static bool Valid(string part) => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public string Url => $"https://github.com/{Owner}/{Name}";

    public override string ToString() => $"{Owner}/{Name}";
}

/// <summary>One file of a GitHub release.</summary>
public sealed record GitHubAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>
/// Where a mod version from a GitHub release comes from: the release's package zip (Thunderstore's format, with its
/// manifest.json), or, when it has none, plugin DLLs that replace the files of the same names in <see cref="Base"/>.
/// </summary>
public sealed class GitHubSource
{
    public required GitHubRepo Repo { get; init; }
    public required string ReleaseUrl { get; init; }
    public GitHubAsset? Package { get; init; }
    public List<GitHubAsset> Plugins { get; init; } = new();

    /// <summary>The Thunderstore version the plugin DLLs go into.</summary>
    public TsVersion? Base { get; init; }
}

/// <summary>
/// GitHub's public API for mods whose Thunderstore page names a GitHub repo: their releases, as versions the launcher
/// can install. Answers are kept on disk (cache\github.json) and asked for again with their ETag, which GitHub doesn't
/// count against the 60 requests an hour it allows without an account.
/// </summary>
public sealed class GitHubClient : IDisposable
{
    public const string Api = "https://api.github.com";
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KeptFor = TimeSpan.FromDays(14);

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, CachedAnswer>? _cache;
    private DateTime _limitedUntil;

    public GitHubClient(AppPaths paths, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private string CacheFile => Path.Combine(_paths.Cache, "github.json");

    private sealed class CachedAnswer
    {
        public string? ETag { get; set; }
        public string Body { get; set; } = "";
        public bool Missing { get; set; }
        public DateTime FetchedUtc { get; set; }
    }

    /// <summary>
    /// What GitHub's API has at <paramref name="path"/> ("/repos/owner/repo/releases/latest"): fresh, or from the cache
    /// when it was asked for a moment ago, GitHub says it hasn't changed, or GitHub can't be reached. Null when there is
    /// nothing there.
    /// </summary>
    public async Task<JsonDocument?> GetAsync(string path, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel);
        CachedAnswer? cached = null;
        try
        {
            _cache ??= LoadCache();
            _cache.TryGetValue(path, out cached);
            DateTime now = DateTime.UtcNow;
            if (cached != null && (now - cached.FetchedUtc < FreshFor || now < _limitedUntil)) return Parse(cached);
            if (now < _limitedUntil) return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
            if (cached?.ETag != null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            using HttpResponseMessage response = await _http.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
            {
                cached.FetchedUtc = now;
                SaveCache();
                return Parse(cached);
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _limitedUntil = ResetTime(response) ?? now.AddMinutes(15);
                AppLog.Warn($"GitHub's limit for requests without an account is used up until {_limitedUntil.ToLocalTime():t}; using what the launcher already knows.");
                return cached != null ? Parse(cached) : null;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache[path] = new CachedAnswer { Missing = true, FetchedUtc = now };
                SaveCache();
                return null;
            }
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(cancel);
            _cache[path] = new CachedAnswer { ETag = response.Headers.ETag?.ToString(), Body = body, FetchedUtc = now };
            SaveCache();
            return JsonDocument.Parse(body);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            AppLog.Warn($"GitHub {path}: {e.Message}");
            return cached != null ? Parse(cached) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The repo's newest published release as a version of <paramref name="package"/>, or null.</summary>
    public async Task<TsVersion?> GetLatestReleaseAsync(GitHubRepo repo, TsPackage package, CancellationToken cancel = default)
    {
        using JsonDocument? release = await GetAsync($"/repos/{repo.Owner}/{repo.Name}/releases/latest", cancel);
        return release == null ? null : ToVersion(release.RootElement, repo, package);
    }

    /// <summary>The release of one version (tag "v1.2.3" or "1.2.3") as a version of <paramref name="package"/>, or null.</summary>
    public async Task<TsVersion?> GetReleaseAsync(GitHubRepo repo, TsPackage package, string version, CancellationToken cancel = default)
    {
        foreach (string tag in new[] { "v" + version, version })
        {
            using JsonDocument? release = await GetAsync($"/repos/{repo.Owner}/{repo.Name}/releases/tags/{Uri.EscapeDataString(tag)}", cancel);
            if (release != null && ToVersion(release.RootElement, repo, package) is { } found && VersionNumber.Same(found.VersionNumber, version)) return found;
        }
        return null;
    }

    /// <summary>
    /// A release as a version of <paramref name="package"/>: a published release whose tag is a version ("v1.2.3"), with
    /// the package's zip, or else plugin DLLs to put into the newest Thunderstore version's files. Description, icon and
    /// dependencies come from that Thunderstore version until the zip's manifest is read. Null for anything else.
    /// </summary>
    public static TsVersion? ToVersion(JsonElement release, GitHubRepo repo, TsPackage package)
    {
        if (Bool(release, "draft") || Bool(release, "prerelease")) return null;
        string version = (Text(release, "tag_name") ?? "").Trim().TrimStart('v', 'V');
        if (!IsVersion(version)) return null;

        var assets = new List<GitHubAsset>();
        if (release.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in list.EnumerateArray())
            {
                string? name = Text(asset, "name");
                string? url = Text(asset, "browser_download_url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url) || Text(asset, "state") is { } state && state != "uploaded") continue;
                long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long bytes) ? bytes : 0;
                string? digest = Text(asset, "digest");
                string? sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new GitHubAsset(name, url, size, sha256));
            }
        }

        TsVersion? basis = package.LatestOnThunderstore;
        GitHubAsset? zip = PickPackage(assets, package, repo);
        List<GitHubAsset> plugins = zip == null ? assets.Where(a => a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList() : new List<GitHubAsset>();
        if (zip == null && (plugins.Count == 0 || basis == null)) return null;
        DateTimeOffset published = DateTimeOffset.TryParse(Text(release, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) ? at : DateTimeOffset.UtcNow;

        return new TsVersion
        {
            Name = package.Name,
            FullName = $"{package.FullName}-{version}",
            Description = basis?.Description ?? "",
            Icon = basis?.Icon ?? "",
            VersionNumber = version,
            Dependencies = basis?.Dependencies.ToList() ?? new List<string>(),
            DownloadUrl = zip?.Url ?? plugins[0].Url,
            DateCreated = published,
            WebsiteUrl = repo.Url,
            IsActive = true,
            FileSize = zip?.Size ?? plugins.Sum(p => p.Size),
            GitHub = new GitHubSource
            {
                Repo = repo,
                ReleaseUrl = Text(release, "html_url") ?? repo.Url + "/releases",
                Package = zip,
                Plugins = plugins,
                Base = zip == null ? basis : null,
            },
        };
    }

    /// <summary>The release's package zip: one named like the package, else like the repo, else the only zip.</summary>
    private static GitHubAsset? PickPackage(List<GitHubAsset> assets, TsPackage package, GitHubRepo repo)
    {
        List<GitHubAsset> zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        string packageName = PackageIndex.Simplify(package.Name);
        string repoName = PackageIndex.Simplify(repo.Name);
        return zips.FirstOrDefault(a => packageName.Length > 0 && PackageIndex.Simplify(a.Name).Contains(packageName, StringComparison.Ordinal))
               ?? zips.FirstOrDefault(a => repoName.Length > 0 && PackageIndex.Simplify(a.Name).Contains(repoName, StringComparison.Ordinal))
               ?? (zips.Count == 1 ? zips[0] : null);
    }

    /// <summary>Downloads a release file into cache\downloads and checks its size and its SHA-256 (when GitHub lists one).</summary>
    public async Task<string> DownloadAsync(GitHubAsset asset, IProgress<double>? progress, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(_paths.Downloads);
        string target = Path.Combine(_paths.Downloads, $"github-{Guid.NewGuid():N}-{PackageStore.SafeName(asset.Name)}");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        using (HttpResponseMessage response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancel);
            await using FileStream file = File.Create(target);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
            }
        }
        string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if ((asset.Size > 0 && done != asset.Size) || (asset.Sha256 != null && actual != asset.Sha256))
        {
            File.Delete(target);
            throw new InvalidDataException($"{asset.Name} from GitHub doesn't match the size or checksum GitHub lists for it.");
        }
        progress?.Report(1.0);
        return target;
    }

    private static JsonDocument? Parse(CachedAnswer answer) => answer.Missing ? null : JsonDocument.Parse(answer.Body);

    private Dictionary<string, CachedAnswer> LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, CachedAnswer>>(File.ReadAllText(CacheFile));
                if (loaded != null)
                {
                    DateTime cutoff = DateTime.UtcNow - KeptFor;
                    return loaded.Where(p => p.Value.FetchedUtc > cutoff).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not read {CacheFile}: {e.Message}");
        }
        return new Dictionary<string, CachedAnswer>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(_paths.Cache);
            string temp = CacheFile + ".saving";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
            File.Move(temp, CacheFile, true);
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not save {CacheFile}: {e.Message}");
        }
    }

    private static DateTime? ResetTime(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-reset", out IEnumerable<string>? values) && long.TryParse(values.FirstOrDefault(), out long seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        if (response.Headers.RetryAfter?.Delta is { } delay) return DateTime.UtcNow + delay;
        return null;
    }

    private static bool IsVersion(string text)
    {
        string[] parts = text.Split('.');
        return parts.Length is >= 2 and <= 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public void Dispose() => _http.Dispose();
}
