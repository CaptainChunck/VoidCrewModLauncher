using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VoidCrewModLauncher.Core.Thunderstore;

/// <summary>
/// Thunderstore's public API for the Void Crew community: the package list (cached on disk for a few minutes and used
/// as-is when offline), package downloads, READMEs and icons. No account or key is needed.
/// </summary>
public sealed class ThunderstoreClient : IDisposable
{
    public const string Site = "https://thunderstore.io";
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<string?>> _readmes = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _icons = new();

    public ThunderstoreClient(AppPaths paths, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    /// <summary>When the list came from the disk cache because Thunderstore couldn't be reached.</summary>
    public bool IsOffline { get; private set; }

    public DateTime? ListTime { get; private set; }

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
            string temp = cache + ".download";
            using (HttpResponseMessage response = await _http.GetAsync(PackageListUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using FileStream file = File.Create(temp);
                await response.Content.CopyToAsync(file, cancel);
            }
            List<TsPackage> packages = ReadCache(temp) ?? throw new InvalidDataException("Thunderstore sent a package list the launcher couldn't read.");
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
