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

    /// <summary>
    /// For a GitHub-only mod (no manifest.json in its zip): a plugin in the download must have a GUID starting with this.
    /// </summary>
    public string? PluginGuidPrefix { get; init; }
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

    /// <summary>How long one API request may take (release downloads have the client's longer timeout).</summary>
    internal static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

    /// <summary>github.json is written this long after a change, so a batch of lookups writes it once.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    /// <summary>How many answers are kept (lobbies name versions to look up, so this mustn't grow forever).</summary>
    private const int MaxAnswers = 500;

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();
    private readonly SemaphoreSlim _requests = new(4, 4);
    private Dictionary<string, CachedAnswer>? _cache;
    private DateTime _limitedUntil;
    private bool _saveQueued;
    private bool _disposed;

    public GitHubClient(AppPaths paths, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>A download fails when nothing arrives for this long (the client's timeout doesn't cover reading the body).</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
        CachedAnswer? cached;
        DateTime now = DateTime.UtcNow;
        lock (_cacheLock)
        {
            _cache ??= LoadCache();
            _cache.TryGetValue(path, out cached);
            if (cached != null && (now - cached.FetchedUtc < FreshFor || now < _limitedUntil)) return Parse(path, cached);
            if (now < _limitedUntil) return null;
        }

        // A few at a time: the launcher checks several mods at once.
        await _requests.WaitAsync(cancel);
        try
        {
            // Another lookup may have asked for the same while this one waited (lobbies run the same mods).
            lock (_cacheLock)
            {
                if (_cache != null && _cache.TryGetValue(path, out CachedAnswer? meanwhile) && DateTime.UtcNow - meanwhile.FetchedUtc < FreshFor)
                {
                    return Parse(path, meanwhile);
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ApiTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
            if (cached?.ETag != null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
            {
                lock (_cacheLock) cached.FetchedUtc = now;
                SaveSoon();
                return Parse(path, cached);
            }
            if (IsRateLimited(response))
            {
                DateTime until = ResetTime(response) ?? now.AddMinutes(15);
                lock (_cacheLock) _limitedUntil = until;
                AppLog.Warn($"GitHub's limit for requests without an account is used up until {until.ToLocalTime():t}; using what the launcher already knows.");
                return cached != null ? Parse(path, cached) : null;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Keep(path, new CachedAnswer { Missing = true, FetchedUtc = now });
                return null;
            }
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            // Read before it is kept: an answer that isn't JSON is never cached.
            JsonDocument document = JsonDocument.Parse(body);
            Keep(path, new CachedAnswer { ETag = response.Headers.ETag?.ToString(), Body = body, FetchedUtc = now });
            return document;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            AppLog.Warn($"GitHub {path}: {e.Message}");
            return cached != null ? Parse(path, cached) : null;
        }
        finally
        {
            _requests.Release();
        }
    }

    /// <summary>
    /// GitHub's limit for requests without an account: 429, or a 403 that says no requests are left (or when to try
    /// again). Other 403s (a blocked repo, say) are only that answer failing.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return true;
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;
        if (response.Headers.RetryAfter != null) return true;
        return response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? values) && values.FirstOrDefault()?.Trim() == "0";
    }

    /// <summary>Keeps an answer (dropping the oldest ones past <see cref="MaxAnswers"/>) and saves the cache soon.</summary>
    private void Keep(string path, CachedAnswer answer)
    {
        lock (_cacheLock)
        {
            _cache ??= LoadCache();
            _cache[path] = answer;
            if (_cache.Count > MaxAnswers)
            {
                foreach (string old in _cache.OrderBy(p => p.Value.FetchedUtc).Take(_cache.Count - MaxAnswers).Select(p => p.Key).ToList()) _cache.Remove(old);
            }
        }
        SaveSoon();
    }

    /// <summary>The repo's newest published release as a version of <paramref name="package"/>, or null.</summary>
    public async Task<TsVersion?> GetLatestReleaseAsync(GitHubRepo repo, TsPackage package, CancellationToken cancel = default)
    {
        using JsonDocument? release = await GetAsync($"/repos/{repo.Owner}/{repo.Name}/releases/latest", cancel);
        return release == null ? null : ToVersion(release.RootElement, repo, package);
    }

    /// <summary>A GitHub-only mod as a package holding its newest release, or null (no release, or no file to install).</summary>
    public async Task<TsPackage?> GetModAsync(GitHubMod mod, CancellationToken cancel = default)
    {
        using JsonDocument? release = await GetAsync($"/repos/{mod.Repo.Owner}/{mod.Repo.Name}/releases/latest", cancel);
        if (release == null) return null;
        var package = new TsPackage
        {
            Name = mod.Name,
            FullName = mod.Id,
            Owner = mod.Author,
            PackageUrl = mod.Repo.Url,
            Categories = new List<string> { "Client-Side" },
            IsGitHubOnly = true,
        };
        TsVersion? version = ToVersion(release.RootElement, mod.Repo, package, mod);
        if (version == null)
        {
            AppLog.Warn($"{mod.Name}'s newest release ({mod.Repo}) has no {mod.Asset}.");
            return null;
        }
        version.Description = mod.Description;
        package.DateCreated = package.DateUpdated = version.DateCreated;
        return package.WithVersions(new[] { version });
    }

    /// <summary>How many of a repo's newest releases <see cref="GetReleaseAsync"/> looks through (GitHub's most per request).</summary>
    internal const int ReleasesLookedUp = 100;

    /// <summary>
    /// The release of one version (tag "v1.2.3" or "1.2.3") as a version of <paramref name="package"/>, or null. Looked up
    /// in the list of the repo's newest releases: one request per repo, however many versions lobbies run, kept and asked
    /// again with its ETag (which GitHub doesn't count); asking for each version's tag cost two counted requests, and a
    /// version that isn't there counted too. The server browser looks up lobbies' versions without a click, and GitHub
    /// allows 60 requests an hour without an account.
    /// </summary>
    public async Task<TsVersion?> GetReleaseAsync(GitHubRepo repo, TsPackage package, string version, CancellationToken cancel = default)
    {
        // The version can come from another player's lobby: only real version numbers are looked for.
        if (!VersionNumber.IsValid(version)) return null;
        using JsonDocument? releases = await GetAsync($"/repos/{repo.Owner}/{repo.Name}/releases?per_page={ReleasesLookedUp}", cancel);
        if (releases == null || releases.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement release in releases.RootElement.EnumerateArray())
        {
            string tag = (Text(release, "tag_name") ?? "").Trim().TrimStart('v', 'V');
            if (!VersionNumber.IsValid(tag) || !VersionNumber.Same(tag, version)) continue;
            if (ToVersion(release, repo, package) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// A release as a version of <paramref name="package"/>: a published release whose tag is a version ("v1.2.3"), with
    /// the package's zip, or else plugin DLLs to put into the newest Thunderstore version's files. Description, icon and
    /// dependencies come from that Thunderstore version until the zip's manifest is read. For a GitHub-only
    /// <paramref name="mod"/>, its own release file. Null for anything else.
    /// </summary>
    public static TsVersion? ToVersion(JsonElement release, GitHubRepo repo, TsPackage package, GitHubMod? mod = null)
    {
        if (release.ValueKind != JsonValueKind.Object || Bool(release, "draft") || Bool(release, "prerelease")) return null;
        string version = (Text(release, "tag_name") ?? "").Trim().TrimStart('v', 'V');
        if (!IsVersion(version)) return null;

        var assets = new List<GitHubAsset>();
        if (release.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in list.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                string? name = Text(asset, "name");
                string? url = Text(asset, "browser_download_url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url) || Text(asset, "state") is { } state && state != "uploaded") continue;
                long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long bytes) ? bytes : 0;
                string? digest = Text(asset, "digest");
                string? sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new GitHubAsset(name, url, size, sha256));
            }
        }

        TsVersion? basis = mod == null ? package.LatestOnThunderstore : null;
        GitHubAsset? zip = mod != null ? PickAsset(assets, mod.Asset) : PickPackage(assets, package, repo);
        List<GitHubAsset> plugins = zip == null && mod == null ? PickPlugins(assets, package, repo) : new List<GitHubAsset>();
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
                PluginGuidPrefix = mod?.PluginGuidPrefix,
            },
        };
    }

    /// <summary>A GitHub-only mod's release file: by its name, or one whose name has it in (a version added, say).</summary>
    private static GitHubAsset? PickAsset(List<GitHubAsset> assets, string name)
    {
        string simple = PackageIndex.Simplify(Path.GetFileNameWithoutExtension(name));
        string extension = Path.GetExtension(name);
        return assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(a => a.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                                             && PackageIndex.Simplify(Path.GetFileNameWithoutExtension(a.Name)).Contains(simple, StringComparison.Ordinal));
    }

    /// <summary>
    /// The release's package zip: one named like the package; from the package's own repo (named like it), else one named
    /// like the repo, else the only zip. A mod's website can name a framework's repo (BepInEx) or a repo of many mods,
    /// whose releases aren't this package.
    /// </summary>
    private static GitHubAsset? PickPackage(List<GitHubAsset> assets, TsPackage package, GitHubRepo repo)
    {
        List<GitHubAsset> zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        string packageName = PackageIndex.Simplify(package.Name);
        string repoName = PackageIndex.Simplify(repo.Name);
        GitHubAsset? named = zips.FirstOrDefault(a => packageName.Length > 0 && PackageIndex.Simplify(a.Name).Contains(packageName, StringComparison.Ordinal));
        if (named != null || !IsPackagesOwnRepo(repo, package)) return named;
        return zips.FirstOrDefault(a => repoName.Length > 0 && PackageIndex.Simplify(a.Name).Contains(repoName, StringComparison.Ordinal))
               ?? (zips.Count == 1 ? zips[0] : null);
    }

    /// <summary>
    /// The plugin DLLs of a release without a package zip: all of them from the package's own repo, else only those named
    /// like the package. The store also checks each one replaces a file of the Thunderstore version.
    /// </summary>
    private static List<GitHubAsset> PickPlugins(List<GitHubAsset> assets, TsPackage package, GitHubRepo repo)
    {
        List<GitHubAsset> dlls = assets.Where(a => a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        if (IsPackagesOwnRepo(repo, package)) return dlls;
        string name = WithoutGame(PackageIndex.Simplify(package.Name));
        return name.Length < 4 ? new List<GitHubAsset>() : dlls.Where(a => PackageIndex.Simplify(Path.GetFileNameWithoutExtension(a.Name)).Contains(name, StringComparison.Ordinal)).ToList();
    }

    /// <summary>The repo is named like the package ("VoidCrewFleet" for Void_Crew_Fleet), not a framework's or a repo of many mods.</summary>
    internal static bool IsPackagesOwnRepo(GitHubRepo repo, TsPackage package)
    {
        string repoName = WithoutGame(PackageIndex.Simplify(repo.Name));
        string packageName = WithoutGame(PackageIndex.Simplify(package.Name));
        if (repoName.Length < 3 || packageName.Length < 3) return false;
        if (repoName == packageName) return true;
        (string shorter, string longer) = repoName.Length <= packageName.Length ? (repoName, packageName) : (packageName, repoName);
        return shorter.Length >= 6 && shorter != "voidcrew" && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static string WithoutGame(string simple) => simple.StartsWith("voidcrew", StringComparison.Ordinal) && simple.Length > 10 ? simple[8..] : simple;

    /// <summary>Downloads a release file into cache\downloads and checks its size and its SHA-256 (when GitHub lists one).</summary>
    public async Task<string> DownloadAsync(GitHubAsset asset, IProgress<double>? progress, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(_paths.Downloads);
        string target = Path.Combine(_paths.Downloads, $"github-{Guid.NewGuid():N}-{PackageStore.SafeName(asset.Name)}");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using FileStream file = File.Create(target);
            done = await ThunderstoreClient.CopyAsync(source, file, StallTimeout, (buffer, read, _) => hash.AppendData(buffer, 0, read), sofar =>
            {
                if (total > 0) progress?.Report(Math.Min(1.0, sofar / (double)total));
            }, cancel).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                File.Delete(target);
            }
            catch (IOException)
            {
                // Left in cache\downloads; harmless.
            }
            throw;
        }
        string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if ((asset.Size > 0 && done != asset.Size) || (asset.Sha256 != null && actual != asset.Sha256))
        {
            File.Delete(target);
            throw new InvalidDataException(Loc.F("{0} from GitHub doesn't match the size or checksum GitHub lists for it.", asset.Name)).InEnglish();
        }
        progress?.Report(1.0);
        return target;
    }

    /// <summary>A kept answer as JSON; one that can't be read (a damaged github.json) is dropped instead.</summary>
    private JsonDocument? Parse(string path, CachedAnswer answer)
    {
        if (answer.Missing) return null;
        try
        {
            return JsonDocument.Parse(answer.Body);
        }
        catch (JsonException)
        {
            lock (_cacheLock)
            {
                if (_cache != null && _cache.TryGetValue(path, out CachedAnswer? kept) && ReferenceEquals(kept, answer)) _cache.Remove(path);
            }
            return null;
        }
    }

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

    /// <summary>Saves github.json in a moment, once for every change made until then.</summary>
    private void SaveSoon()
    {
        lock (_cacheLock)
        {
            if (_saveQueued || _disposed) return;
            _saveQueued = true;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(SaveDelay);
            SaveNow();
        });
    }

    private void SaveNow()
    {
        lock (_cacheLock)
        {
            if (!_saveQueued || _cache == null) return;
            _saveQueued = false;
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
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        // What's waiting to be saved is saved now (and nothing is queued after this).
        lock (_cacheLock) _disposed = true;
        SaveNow();
        _http.Dispose();
    }
}
