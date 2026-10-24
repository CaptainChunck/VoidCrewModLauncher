using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VoidCrewModLauncher.Core.Mods;

namespace VoidCrewModLauncher.Core.Thunderstore;

/// <summary>
/// Thunderstore's public API for the Void Crew community: the package list (cached on disk for a few minutes and used
/// as-is when offline), the current state of single packages and versions, package downloads, READMEs and icons. No
/// account or key is needed.
/// </summary>
/// <remarks>
/// Thunderstore caches its community list on its side, at times an hour or more behind, and a package can even be
/// missing from it for a while. Its per-package API is current, so installs, updates and joins check there.
/// </remarks>
public sealed class ThunderstoreClient : IDisposable
{
    public const string Site = "https://thunderstore.io";
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(15);

    /// <summary>How long an answer of the per-package API is reused.</summary>
    private static readonly TimeSpan CurrentAge = TimeSpan.FromMinutes(2);

    /// <summary>How long "Thunderstore doesn't have this version" is believed before asking again.</summary>
    private static readonly TimeSpan MissingAge = TimeSpan.FromMinutes(10);

    /// <summary>How long one call of the per-package API may take (downloads have the client's longer timeout).</summary>
    internal static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How many per-package answers are kept; lobbies (other players) name packages, so this mustn't grow forever.</summary>
    private const int MaxRemembered = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<string?>> _readmes = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _icons = new();
    private readonly ConcurrentDictionary<string, (DateTime At, TsPackage? Package)> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, TsVersion? Version)> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lookups = new(6, 6);

    public ThunderstoreClient(AppPaths paths, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    /// <summary>A download fails when nothing arrives for this long (the client's timeout doesn't cover reading the body).</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When the list came from the disk cache because Thunderstore couldn't be reached.</summary>
    public bool IsOffline { get; private set; }

    public DateTime? ListTime { get; private set; }

    /// <summary>The community's package list as gzip'd chunks (what current mod managers read; rebuilt more often).</summary>
    public static string ListingIndexUrl => $"{Site}/c/{AppInfo.ThunderstoreCommunity}/api/v1/package-listing-index/";

    /// <summary>The older single package list, used when the listing index can't be read.</summary>
    public static string PackageListUrl => $"{Site}/c/{AppInfo.ThunderstoreCommunity}/api/v1/package/";

    public static string PackagePage(string owner, string name) => $"{Site}/c/{AppInfo.ThunderstoreCommunity}/p/{owner}/{name}/";

    public async Task<List<TsPackage>> GetPackagesAsync(bool refresh, CancellationToken cancel = default)
    {
        string cache = _paths.PackageListCache;
        if (!refresh && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < CacheAge)
        {
            List<TsPackage>? cached = ReadCache(cache);
            if (cached != null)
            {
                IsOffline = false;
                ListTime = File.GetLastWriteTime(cache);
                return cached;
            }
        }

        try
        {
            List<TsPackage> packages = await DownloadListAsync(cancel);
            string temp = cache + ".download";
            await using (FileStream file = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(file, packages, Json, cancel);
            }
            File.Move(temp, cache, true);
            IsOffline = false;
            ListTime = DateTime.Now;
            AppLog.Info($"Thunderstore: {packages.Count} Void Crew packages.");
            return packages;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            List<TsPackage>? cached = File.Exists(cache) ? ReadCache(cache) : null;
            if (cached == null) throw;
            AppLog.Warn($"Thunderstore couldn't be reached ({e.Message}); using the package list from {File.GetLastWriteTime(cache):g}.");
            IsOffline = true;
            ListTime = File.GetLastWriteTime(cache);
            return cached;
        }
    }

    /// <summary>The community's package list: from the listing index, else from the older single list.</summary>
    private async Task<List<TsPackage>> DownloadListAsync(CancellationToken cancel)
    {
        try
        {
            List<string> chunks = JsonSerializer.Deserialize<List<string>>(await GetBodyAsync(ListingIndexUrl, cancel)) ?? new List<string>();
            var packages = new List<TsPackage>();
            foreach (string chunk in chunks)
            {
                packages.AddRange(JsonSerializer.Deserialize<List<TsPackage>>(await GetBodyAsync(chunk, cancel), Json) ?? new List<TsPackage>());
            }
            if (packages.Count > 0) return packages;
            AppLog.Warn("Thunderstore's listing index was empty; reading its package list instead.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            AppLog.Warn($"Thunderstore's listing index couldn't be read ({e.Message}); reading its package list instead.");
        }
        return JsonSerializer.Deserialize<List<TsPackage>>(await GetBodyAsync(PackageListUrl, cancel), Json)
               ?? throw new InvalidDataException(Loc.T("Thunderstore sent a package list the launcher couldn't read."));
    }

    /// <summary>A response body, unpacked when it is gzip data (the listing index stores its files compressed).</summary>
    private async Task<byte[]> GetBodyAsync(string url, CancellationToken cancel)
    {
        byte[] body = await _http.GetByteArrayAsync(url, cancel);
        if (body.Length >= 2 && body[0] == 0x1F && body[1] == 0x8B)
        {
            using var gzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            await gzip.CopyToAsync(unpacked, cancel);
            body = unpacked.ToArray();
        }
        // System.Text.Json reads UTF-8 without the byte order mark.
        return body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? body[3..] : body;
    }

    /// <summary>
    /// The current state of packages ("Namespace-Name"), from Thunderstore's per-package API, each with its newest version
    /// only. Packages Thunderstore doesn't have or doesn't list for Void Crew are left out, and so are failed lookups.
    /// </summary>
    public async Task<List<TsPackage>> GetCurrentAsync(IEnumerable<string> ids, CancellationToken cancel = default)
    {
        Task<TsPackage?>[] lookups = ids.Select(PackageIndex.NormalizeId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => GetCurrentAsync(id, cancel)).ToArray();
        return (await Task.WhenAll(lookups)).OfType<TsPackage>().ToList();
    }

    /// <summary>
    /// A package even when its Void Crew listing was rejected: only to find the mod's GitHub repo (its own releases),
    /// never to download its Thunderstore files.
    /// </summary>
    public Task<TsPackage?> GetForGitHubAsync(string id, CancellationToken cancel = default) => GetCurrentAsync(id, cancel, true);

    private async Task<TsPackage?> GetCurrentAsync(string id, CancellationToken cancel, bool includeRejected = false)
    {
        string key = includeRejected ? id + "|rejected" : id;
        if (_current.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CurrentAge) return cached.Package;
        if (PackageReference.Parse(id) is not { } reference) return null;
        TsPackage? package = null;
        await _lookups.WaitAsync(cancel);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ApiTimeout);
            string url = $"{Site}/api/experimental/package/{Uri.EscapeDataString(reference.Namespace)}/{Uri.EscapeDataString(reference.Name)}/";
            using HttpResponseMessage response = await _http.GetAsync(url, timeout.Token);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                package = (await JsonSerializer.DeserializeAsync<CurrentPackage>(body, Json, timeout.Token))?.ToPackage(includeRejected);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            AppLog.Warn($"Thunderstore lookup of {id} failed: {e.Message}");
            return null;
        }
        finally
        {
            _lookups.Release();
        }
        Remember<(DateTime At, TsPackage? Package)>(_current, key, (DateTime.UtcNow, package), a => DateTime.UtcNow - a.At < CurrentAge);
        return package;
    }

    /// <summary>Keeps an answer, first dropping stale ones (or, when that isn't enough, all of them) once there are too many.</summary>
    private static void Remember<T>(ConcurrentDictionary<string, T> answers, string key, T answer, Func<T, bool> stillGood)
    {
        if (answers.Count >= MaxRemembered)
        {
            foreach (KeyValuePair<string, T> old in answers.Where(p => !stillGood(p.Value)).ToList()) answers.TryRemove(old.Key, out _);
            if (answers.Count >= MaxRemembered) answers.Clear();
        }
        answers[key] = answer;
    }

    /// <summary>One exact version ("Namespace-Name", "1.2.3") from Thunderstore's per-version API, or null when it doesn't have it.</summary>
    public async Task<TsVersion?> GetVersionAsync(string id, string version, CancellationToken cancel = default)
    {
        string key = $"{PackageIndex.NormalizeId(id)}@{version}";
        if (_versions.TryGetValue(key, out var cached) && (cached.Version != null || DateTime.UtcNow - cached.At < MissingAge)) return cached.Version;
        if (PackageReference.Parse(PackageIndex.NormalizeId(id)) is not { } reference) return null;
        TsVersion? found = null;
        await _lookups.WaitAsync(cancel);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ApiTimeout);
            string url = $"{Site}/api/experimental/package/{Uri.EscapeDataString(reference.Namespace)}/{Uri.EscapeDataString(reference.Name)}/{Uri.EscapeDataString(version)}/";
            using HttpResponseMessage response = await _http.GetAsync(url, timeout.Token);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                found = await JsonSerializer.DeserializeAsync<TsVersion>(body, Json, timeout.Token);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            AppLog.Warn($"Thunderstore lookup of {key} failed: {e.Message}");
            return null;
        }
        finally
        {
            _lookups.Release();
        }
        Remember<(DateTime At, TsVersion? Version)>(_versions, key, (DateTime.UtcNow, found), a => a.Version != null || DateTime.UtcNow - a.At < MissingAge);
        return found;
    }

    /// <summary>The per-package API's answer (snake_case JSON).</summary>
    private sealed class CurrentPackage
    {
        public string Name { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string Owner { get; set; } = "";
        public DateTimeOffset DateCreated { get; set; }
        public DateTimeOffset DateUpdated { get; set; }
        public int RatingScore { get; set; }
        public bool IsPinned { get; set; }
        public bool IsDeprecated { get; set; }
        public TsVersion? Latest { get; set; }
        public List<CommunityListing> CommunityListings { get; set; } = new();

        /// <summary>As a list entry, when it is listed (and not rejected) in the Void Crew community.</summary>
        public TsPackage? ToPackage(bool includeRejected = false)
        {
            CommunityListing? listing = CommunityListings.FirstOrDefault(l => string.Equals(l.Community, AppInfo.ThunderstoreCommunity, StringComparison.OrdinalIgnoreCase));
            if (Latest == null || listing == null) return null;
            if (!includeRejected && string.Equals(listing.ReviewStatus, "rejected", StringComparison.OrdinalIgnoreCase)) return null;
            string owner = Owner.Length > 0 ? Owner : Namespace;
            return new TsPackage
            {
                Name = Name,
                FullName = FullName,
                Owner = owner,
                PackageUrl = PackagePage(owner, Name),
                DateCreated = DateCreated,
                DateUpdated = DateUpdated,
                RatingScore = RatingScore,
                IsPinned = IsPinned,
                IsDeprecated = IsDeprecated,
                HasNsfwContent = listing.HasNsfwContent,
                Categories = listing.Categories.ToList(),
                Versions = new List<TsVersion> { Latest },
            };
        }
    }

    private sealed class CommunityListing
    {
        public string Community { get; set; } = "";
        public List<string> Categories { get; set; } = new();
        public bool HasNsfwContent { get; set; }
        public string? ReviewStatus { get; set; }
    }

    private static List<TsPackage>? ReadCache(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<TsPackage>>(stream, Json);
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not read the package list {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>Downloads a package zip into cache\downloads and returns its path.</summary>
    public async Task<string> DownloadAsync(TsVersion version, IProgress<double>? progress, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(_paths.Downloads);
        // The name comes from the package list (Thunderstore's or voidcrewmod.com's): made safe, and unique per download.
        string target = Path.Combine(_paths.Downloads, $"{Guid.NewGuid():N}-{PackageStore.SafeName(version.FullName)}.zip");
        string temp = target + ".part";
        try
        {
            using (HttpResponseMessage response = await _http.GetAsync(version.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                long? length = response.Content.Headers.ContentLength ?? (version.FileSize > 0 ? version.FileSize : null);
                await using Stream source = await response.Content.ReadAsStreamAsync(cancel);
                await using FileStream file = File.Create(temp);
                await CopyAsync(source, file, StallTimeout, (_, _, done) =>
                {
                    if (length > 0) progress?.Report(Math.Min(1.0, done / (double)length));
                }, cancel);
            }
            File.Move(temp, target, true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        progress?.Report(1.0);
        return target;
    }

    /// <summary>
    /// Copies a download to <paramref name="target"/>, telling <paramref name="received"/> about each piece (the bytes,
    /// their count, the total so far). Throws an IOException when nothing arrives for <paramref name="stall"/>: a
    /// stalled server would otherwise keep the download (and the store's lock) waiting forever.
    /// </summary>
    internal static async Task<long> CopyAsync(Stream source, Stream target, TimeSpan stall, Action<byte[], int, long>? received, CancellationToken cancel)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var buffer = new byte[81920];
        long done = 0;
        while (true)
        {
            watchdog.CancelAfter(stall);
            int read;
            try
            {
                // WaitAsync too, for a stream that doesn't listen to its token.
                read = await source.ReadAsync(buffer, watchdog.Token).AsTask().WaitAsync(watchdog.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new IOException(Loc.F("The download stalled: nothing arrived for {0:0} seconds.", stall.TotalSeconds));
            }
            if (read == 0) return done;
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;
            received?.Invoke(buffer, read, done);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // Left in cache\downloads; harmless.
        }
    }

    /// <summary>
    /// The package's README (markdown) for one version, or null. <paramref name="site"/> is another site with
    /// Thunderstore's API (voidcrewmod.com for its own mods); Thunderstore when null.
    /// </summary>
    public Task<string?> GetReadmeAsync(string owner, string name, string version, string? site = null)
    {
        site ??= Site;
        string key = $"{site}/{owner}/{name}/{version}";
        return _readmes.GetOrAdd(key, async _ =>
        {
            try
            {
                string url = $"{site}/api/experimental/package/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}/readme/";
                using var timeout = new CancellationTokenSource(ApiTimeout);
                using JsonDocument document = JsonDocument.Parse(await _http.GetStringAsync(url, timeout.Token));
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("markdown", out JsonElement markdown)
                       && markdown.ValueKind == JsonValueKind.String ? markdown.GetString() : null;
            }
            catch (Exception e)
            {
                AppLog.Warn($"No README for {key}: {e.Message}");
                _readmes.TryRemove(key, out Task<string?>? _);
                return null;
            }
        });
    }

    /// <summary>The local path of a package icon (downloaded once into cache\icons), or null.</summary>
    public Task<string?> GetIconAsync(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return Task.FromResult<string?>(null);
        }
        return _icons.GetOrAdd(url, async _ =>
        {
            string file = Path.Combine(_paths.Icons, Path.GetFileName(uri.LocalPath));
            if (File.Exists(file)) return file;
            try
            {
                Directory.CreateDirectory(_paths.Icons);
                using HttpResponseMessage response = await _http.GetAsync(uri);
                response.EnsureSuccessStatusCode();
                MediaTypeHeaderValue? type = response.Content.Headers.ContentType;
                if (type?.MediaType is { } media && !media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
                string temp = file + ".part";
                await File.WriteAllBytesAsync(temp, await response.Content.ReadAsByteArrayAsync());
                File.Move(temp, file, true);
                return file;
            }
            catch (Exception e)
            {
                AppLog.Warn($"Icon {url}: {e.Message}");
                _icons.TryRemove(url, out Task<string?>? _);
                return null;
            }
        });
    }

    public void Dispose() => _http.Dispose();
}
