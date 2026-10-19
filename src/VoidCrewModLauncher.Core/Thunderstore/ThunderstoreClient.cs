using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

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
               ?? throw new InvalidDataException("Thunderstore sent a package list the launcher couldn't read.");
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

    private async Task<TsPackage?> GetCurrentAsync(string id, CancellationToken cancel)
    {
        if (_current.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < CurrentAge) return cached.Package;
        if (PackageReference.Parse(id) is not { } reference) return null;
        TsPackage? package = null;
        await _lookups.WaitAsync(cancel);
        try
        {
            string url = $"{Site}/api/experimental/package/{Uri.EscapeDataString(reference.Namespace)}/{Uri.EscapeDataString(reference.Name)}/";
            using HttpResponseMessage response = await _http.GetAsync(url, cancel);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(cancel);
                package = (await JsonSerializer.DeserializeAsync<CurrentPackage>(body, Json, cancel))?.ToPackage();
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
        _current[id] = (DateTime.UtcNow, package);
        return package;
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
            string url = $"{Site}/api/experimental/package/{Uri.EscapeDataString(reference.Namespace)}/{Uri.EscapeDataString(reference.Name)}/{Uri.EscapeDataString(version)}/";
            using HttpResponseMessage response = await _http.GetAsync(url, cancel);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(cancel);
                found = await JsonSerializer.DeserializeAsync<TsVersion>(body, Json, cancel);
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
        _versions[key] = (DateTime.UtcNow, found);
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
        public TsPackage? ToPackage()
        {
            CommunityListing? listing = CommunityListings.FirstOrDefault(l => string.Equals(l.Community, AppInfo.ThunderstoreCommunity, StringComparison.OrdinalIgnoreCase));
            if (Latest == null || listing == null || string.Equals(listing.ReviewStatus, "rejected", StringComparison.OrdinalIgnoreCase)) return null;
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
        string target = Path.Combine(_paths.Downloads, $"{version.FullName}.zip");
        string temp = target + ".part";
        using (HttpResponseMessage response = await _http.GetAsync(version.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength ?? (version.FileSize > 0 ? version.FileSize : null);
            await using Stream source = await response.Content.ReadAsStreamAsync(cancel);
            await using FileStream file = File.Create(temp);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (length > 0) progress?.Report(Math.Min(1.0, done / (double)length));
            }
        }
        File.Move(temp, target, true);
        progress?.Report(1.0);
        return target;
    }

    /// <summary>The package's README (markdown) for one version, or null.</summary>
    public Task<string?> GetReadmeAsync(string owner, string name, string version)
    {
        string key = $"{owner}/{name}/{version}";
        return _readmes.GetOrAdd(key, async _ =>
        {
            try
            {
                string url = $"{Site}/api/experimental/package/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}/readme/";
                using JsonDocument document = JsonDocument.Parse(await _http.GetStringAsync(url));
                return document.RootElement.TryGetProperty("markdown", out JsonElement markdown) ? markdown.GetString() : null;
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
