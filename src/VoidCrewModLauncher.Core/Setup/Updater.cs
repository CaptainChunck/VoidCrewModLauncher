using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Core.Setup;

/// <summary>A launcher release newer than this one, and its setup exe.</summary>
public sealed record LauncherRelease(string Version, string DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// The launcher's self-update: the latest GitHub release, its setup exe downloaded and checked (size, the SHA-256 GitHub
/// publishes, and that it is this launcher at that version). <see cref="Installer.StartUpdate"/> then swaps it in.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string LatestReleaseApi = "https://api.github.com/repos/CaptainChunck/VoidCrewModLauncher/releases/latest";

    /// <summary>Set to another release feed (a URL with the same JSON as GitHub's) to try an update locally.</summary>
    public const string FeedVariable = "VCML_UPDATE_FEED";

    private readonly HttpClient _http;

    public Updater(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Where downloaded setups wait to be installed.</summary>
    public static string DownloadFolder => Path.Combine(Path.GetTempPath(), "VoidCrewModLauncher-update");

    /// <summary>The latest release when it is newer than <paramref name="currentVersion"/>, else null. Network errors throw.</summary>
    public async Task<LauncherRelease?> CheckAsync(string currentVersion, CancellationToken cancel = default)
    {
        string feed = Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } custom ? custom : LatestReleaseApi;
        using JsonDocument release = JsonDocument.Parse(await _http.GetStringAsync(feed, cancel));
        return Parse(release.RootElement, currentVersion);
    }

    /// <summary>Reads a GitHub release: null unless it's a published release newer than <paramref name="currentVersion"/> with the setup exe.</summary>
    public static LauncherRelease? Parse(JsonElement release, string currentVersion)
    {
        if (Bool(release, "draft") || Bool(release, "prerelease")) return null;
        string version = Text(release, "tag_name")?.Trim().TrimStart('v', 'V') ?? "";
        if (version.Length == 0 || VersionNumber.Compare(version, currentVersion) <= 0) return null;
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!string.Equals(Text(asset, "name"), InstallLocations.SetupName, StringComparison.OrdinalIgnoreCase)) continue;
            string? url = Text(asset, "browser_download_url");
            if (string.IsNullOrEmpty(url)) return null;
            long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long bytes) ? bytes : 0;
            string? digest = Text(asset, "digest");
            string? sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
            return new LauncherRelease(version, url, size, sha256, Text(release, "html_url") ?? AppInfo.RepositoryUrl + "/releases/latest");
        }
        AppLog.Warn($"Launcher release {version} has no {InstallLocations.SetupName}; not updating.");
        return null;
    }

    /// <summary>
    /// Downloads the release's setup into <see cref="DownloadFolder"/> and checks its size and SHA-256 (when GitHub gives
    /// one). Returns the file; throws when it doesn't match.
    /// </summary>
    public async Task<string> DownloadAsync(LauncherRelease release, IProgress<(long Done, long Total)>? progress, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(DownloadFolder);
        string target = Path.Combine(DownloadFolder, $"VoidCrewModLauncher-{release.Version}.exe");
        string part = target + ".part";
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            long done = 0;
            using (HttpResponseMessage response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.Size;
                await using Stream source = await response.Content.ReadAsStreamAsync(cancel);
                await using FileStream file = File.Create(part);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    progress?.Report((done, total));
                }
            }
            if (release.Size > 0 && done != release.Size)
            {
                File.Delete(part);
                throw new InvalidDataException($"The download is {done:N0} bytes; GitHub says {release.Size:N0}.");
            }
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (release.Sha256 != null && actual != release.Sha256)
            {
                File.Delete(part);
                throw new InvalidDataException("The download doesn't match the checksum GitHub lists for it.");
            }
        }
        File.Move(part, target, true);
        return target;
    }

    /// <summary>Throws unless <paramref name="exe"/> is this launcher at <paramref name="version"/> (so a mislabelled release can't loop).</summary>
    public static void CheckSetup(string exe, string version)
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
        string found = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        if (info.ProductName != AppInfo.Name) throw new InvalidDataException($"The download isn't the {AppInfo.Name}.");
        if (!VersionNumber.Same(found, version)) throw new InvalidDataException($"The download is version {found}, not {version}.");
    }

    /// <summary>Deletes setups left from earlier updates (one still running is skipped and goes next time).</summary>
    public static void CleanUp()
    {
        if (!Directory.Exists(DownloadFolder)) return;
        foreach (string file in Directory.GetFiles(DownloadFolder))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still running (the setup that installed this update exits a moment after starting it).
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public void Dispose() => _http.Dispose();
}
