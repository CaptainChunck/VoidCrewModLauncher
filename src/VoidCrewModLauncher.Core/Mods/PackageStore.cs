using System.IO.Compression;
using System.Text.Json;
using VoidCrewModLauncher.Core.GitHub;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Core.Mods;

/// <summary>
/// Every mod version the launcher has downloaded, unpacked as the package ships it: store\&lt;id&gt;\&lt;version&gt;\.
/// Lobbies can use other versions than the one you play with, so several versions of a mod can sit side by side.
/// Downloads are unpacked into a temporary folder first and only moved into place when complete.
/// </summary>
public sealed class PackageStore
{
    private readonly AppPaths _paths;
    private readonly ThunderstoreClient _thunderstore;
    private readonly GitHubClient? _github;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PackageStore(AppPaths paths, ThunderstoreClient thunderstore, GitHubClient? github = null)
    {
        _paths = paths;
        _thunderstore = thunderstore;
        _github = github;
    }

    public string VersionDirectory(string id, string version) => Path.Combine(_paths.Store, SafeName(id), SafeName(version));

    public bool Has(string id, string version) => Directory.Exists(VersionDirectory(id, version));

    public IEnumerable<string> Versions(string id)
    {
        string dir = Path.Combine(_paths.Store, SafeName(id));
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().Where(v => !v.StartsWith('.')) : Enumerable.Empty<string>();
    }

    /// <summary>Downloads and unpacks one version (from Thunderstore or GitHub) unless it's already in the store; returns its folder.</summary>
    public async Task<string> EnsureAsync(TsVersion version, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        PackageReference reference = PackageReference.Parse(version.FullName) ?? throw new InvalidDataException($"Not a package version: {version.FullName}");
        string target = VersionDirectory(reference.Id, version.VersionNumber);
        if (Directory.Exists(target)) return target;
        // Plugin DLLs from a GitHub release go into the files of a Thunderstore version, which is needed first.
        if (version.GitHub is { Package: null, Base: { } basis }) await EnsureAsync(basis, null, cancel);

        await _gate.WaitAsync(cancel);
        try
        {
            if (Directory.Exists(target)) return target;
            if (version.GitHub is { } github)
            {
                await AddFromGitHubAsync(reference, version, github, target, progress, cancel);
                return target;
            }
            AppLog.Info($"Downloading {version.FullName}.");
            string zip = await _thunderstore.DownloadAsync(version, progress, cancel);
            try
            {
                if (version.Sha256 != null) await CheckSha256Async(zip, version, cancel);
                await Task.Run(() => Unpack(zip, target), cancel);
            }
            finally
            {
                TryDelete(zip);
            }
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A version from a mod's GitHub release: its package zip, which must be this package at this version (its manifest
    /// says so), or plugin DLLs that replace the files of the same names in a copy of the Thunderstore version.
    /// </summary>
    private async Task AddFromGitHubAsync(PackageReference reference, TsVersion version, GitHubSource github, string target, IProgress<double>? progress, CancellationToken cancel)
    {
        if (_github == null) throw new InvalidOperationException("GitHub downloads aren't available here.");
        AppLog.Info($"Downloading {version.FullName} from GitHub ({github.ReleaseUrl}).");
        if (github.Package != null)
        {
            string zip = await _github.DownloadAsync(github.Package, progress, cancel);
            try
            {
                Action<string> check = github.PluginGuidPrefix is { } prefix
                    ? folder => CheckPlugin(folder, prefix, reference, version)
                    : folder => CheckManifest(folder, reference, version);
                await Task.Run(() => Unpack(zip, target, check), cancel);
            }
            finally
            {
                TryDelete(zip);
            }
            return;
        }

        string basis = VersionDirectory(reference.Id, github.Base!.VersionNumber);
        string temp = target + ".patching";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        CopyDirectory(basis, temp);
        foreach (GitHubAsset plugin in github.Plugins)
        {
            string file = await _github.DownloadAsync(plugin, progress, cancel);
            try
            {
                List<string> same = Directory.GetFiles(temp, "*", SearchOption.AllDirectories)
                    .Where(f => string.Equals(Path.GetFileName(f), plugin.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count == 0)
                {
                    Directory.Delete(temp, true);
                    throw new InvalidDataException($"{plugin.Name} in {github.Repo}'s release isn't a file of {reference.Id}; not using that release.");
                }
                foreach (string existing in same) File.Copy(file, existing, true);
            }
            finally
            {
                TryDelete(file);
            }
        }
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(temp, target);
    }

    /// <summary>A package from GitHub must say it is this package at this version; its dependencies replace the guessed ones.</summary>
    private static void CheckManifest(string folder, PackageReference reference, TsVersion version)
    {
        string manifest = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifest)) throw new InvalidDataException($"The GitHub release of {reference.Id} {version.VersionNumber} has no manifest.json; not using it.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        JsonElement root = document.RootElement;
        string? name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        string? number = root.TryGetProperty("version_number", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (!string.Equals(name, reference.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The GitHub release's package is {name}, not {reference.Name}; not using it.");
        }
        if (!VersionNumber.Same(number, version.VersionNumber))
        {
            throw new InvalidDataException($"The GitHub release {version.VersionNumber} of {reference.Name} has a package saying {number}; not using it.");
        }
        if (root.TryGetProperty("dependencies", out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Array)
        {
            version.Dependencies = dependencies.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString()!).ToList();
        }
    }

    /// <summary>A download whose source lists its SHA-256 (voidcrewmod.com) must match it.</summary>
    private static async Task CheckSha256Async(string file, TsVersion version, CancellationToken cancel)
    {
        await using FileStream stream = File.OpenRead(file);
        string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancel)).ToLowerInvariant();
        if (actual != version.Sha256)
        {
            throw new InvalidDataException($"{version.FullName} doesn't match the checksum its site lists; not installing it.");
        }
    }

    /// <summary>A GitHub-only mod's download must hold its plugin (a GUID starting with <paramref name="prefix"/>).</summary>
    private static void CheckPlugin(string folder, string prefix, PackageReference reference, TsVersion version)
    {
        if (!PluginScanner.ScanDirectory(folder).Any(p => p.Guid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"The GitHub release {version.VersionNumber} of {reference.Name} has no {prefix} plugin; not using it.");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
        }
    }

    /// <summary>
    /// Unpacks a zip into <paramref name="target"/>, refusing entries that would land outside it. <paramref name="check"/>
    /// looks at the unpacked files before they're moved into place (and can refuse them by throwing).
    /// </summary>
    public static void Unpack(string zip, string target, Action<string>? check = null)
    {
        string temp = target + ".unpacking";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        string root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.Length == 0) continue;
                string destination = Path.GetFullPath(Path.Combine(temp, name));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{Path.GetFileName(zip)} has a file that would be written outside its folder ({entry.FullName}); not installing it.");
                }
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }
        try
        {
            check?.Invoke(temp);
        }
        catch
        {
            Directory.Delete(temp, true);
            throw;
        }
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(temp, target);
    }

    /// <summary>Copies a local mod (a folder, or a single DLL) into the store.</summary>
    public string AddLocal(string id, string version, IEnumerable<(string Source, string RelativeTarget)> files)
    {
        string target = VersionDirectory(id, version);
        string temp = target + ".adding";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        foreach ((string source, string relative) in files)
        {
            string destination = Path.Combine(temp, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }
        Directory.CreateDirectory(temp);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(temp, target);
        return target;
    }

    public void RemoveAll(string id)
    {
        string dir = Path.Combine(_paths.Store, SafeName(id));
        if (Directory.Exists(dir)) TryDeleteDirectory(dir);
    }

    public static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Trim().TrimEnd('.') is { Length: > 0 } trimmed && trimmed != ".." ? trimmed : "_";
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

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not delete {dir}: {e.Message}");
        }
    }
}
