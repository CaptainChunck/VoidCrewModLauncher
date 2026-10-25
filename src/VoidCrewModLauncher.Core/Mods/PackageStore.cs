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

    /// <summary>
    /// Downloads and unpacks one version (from Thunderstore or GitHub) unless it's already in the store; returns its folder.
    /// The download and the file work run off the caller's thread (the UI's when a player installs).
    /// </summary>
    public async Task<string> EnsureAsync(TsVersion version, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        PackageReference reference = PackageReference.Parse(version.FullName) ?? throw new InvalidDataException(Loc.F("Not a package version: {0}", version.FullName)).InEnglish();
        string target = VersionDirectory(reference.Id, version.VersionNumber);
        if (Directory.Exists(target)) return target;
        // Plugin DLLs from a GitHub release go into the files of a Thunderstore version, which is needed first.
        if (version.GitHub is { Package: null, Base: { } basis }) await EnsureAsync(basis, null, cancel).ConfigureAwait(false);

        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(target)) return target;
            if (version.GitHub is { } github)
            {
                await AddFromGitHubAsync(reference, version, github, target, progress, cancel).ConfigureAwait(false);
                return target;
            }
            AppLog.Info($"Downloading {version.FullName}.");
            string zip = await _thunderstore.DownloadAsync(version, progress, cancel).ConfigureAwait(false);
            try
            {
                if (version.Sha256 != null) await CheckSha256Async(zip, version, cancel).ConfigureAwait(false);
                await Task.Run(() => Unpack(zip, target), cancel).ConfigureAwait(false);
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
        if (_github == null) throw new InvalidOperationException(Loc.T("GitHub downloads aren't available here.")).InEnglish();
        AppLog.Info($"Downloading {version.FullName} from GitHub ({github.ReleaseUrl}).");
        if (github.Package != null)
        {
            string zip = await _github.DownloadAsync(github.Package, progress, cancel).ConfigureAwait(false);
            try
            {
                Action<string> check = github.PluginGuidPrefix is { } prefix
                    ? folder => CheckPlugin(folder, prefix, reference, version)
                    : folder => CheckManifest(folder, reference, version);
                await Task.Run(() => Unpack(zip, target, check), cancel).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(zip);
            }
            return;
        }

        string basis = VersionDirectory(reference.Id, github.Base!.VersionNumber);
        string temp = target + ".patching";
        // The file work runs off the caller's thread, which is the UI's when a player installs: a mod can have many files.
        await Task.Run(() =>
        {
            FileTools.DeleteDirectory(temp);
            CopyDirectory(basis, temp);
        }, cancel).ConfigureAwait(false);
        foreach (GitHubAsset plugin in github.Plugins)
        {
            string file = await _github.DownloadAsync(plugin, progress, cancel).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    List<string> same = Directory.GetFiles(temp, "*", SearchOption.AllDirectories)
                        .Where(f => string.Equals(Path.GetFileName(f), plugin.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (same.Count == 0)
                    {
                        FileTools.DeleteDirectory(temp);
                        throw new InvalidDataException(Loc.F("{0} in {1}'s release isn't a file of {2}; not using that release.", plugin.Name, github.Repo, reference.Id)).InEnglish();
                    }
                    foreach (string existing in same) FileTools.CopyFile(file, existing);
                }, cancel).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(file);
            }
        }
        FileTools.DeleteDirectory(target);
        Directory.Move(temp, target);
    }

    /// <summary>A package from GitHub must say it is this package at this version; its dependencies replace the guessed ones.</summary>
    private static void CheckManifest(string folder, PackageReference reference, TsVersion version)
    {
        string manifest = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifest)) throw new InvalidDataException(Loc.F("The GitHub release of {0} {1} has no manifest.json; not using it.", reference.Id, version.VersionNumber)).InEnglish();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        JsonElement root = document.RootElement;
        string? name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        string? number = root.TryGetProperty("version_number", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (!string.Equals(name, reference.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Loc.F("The GitHub release's package is {0}, not {1}; not using it.", name, reference.Name)).InEnglish();
        }
        if (!VersionNumber.Same(number, version.VersionNumber))
        {
            throw new InvalidDataException(Loc.F("The GitHub release {0} of {1} has a package saying {2}; not using it.", version.VersionNumber, reference.Name, number)).InEnglish();
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
        string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != version.Sha256)
        {
            throw new InvalidDataException(Loc.F("{0} doesn't match the checksum its site lists; not installing it.", version.FullName)).InEnglish();
        }
    }

    /// <summary>A GitHub-only mod's download must hold its plugin (a GUID starting with <paramref name="prefix"/>).</summary>
    private static void CheckPlugin(string folder, string prefix, PackageReference reference, TsVersion version)
    {
        if (!PluginScanner.ScanDirectory(folder).Any(p => p.Guid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(Loc.F("The GitHub release {0} of {1} has no {2} plugin; not using it.", version.VersionNumber, reference.Name, prefix)).InEnglish();
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
            FileTools.CopyFile(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>The most a package may unpack to, and the most files it may have; anything bigger isn't a Void Crew mod.</summary>
    public const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;

    public const int MaxEntries = 20_000;

    /// <summary>
    /// Unpacks a zip into <paramref name="target"/>, refusing entries that would land outside it. <paramref name="check"/>
    /// looks at the unpacked files before they're moved into place (and can refuse them by throwing).
    /// </summary>
    public static void Unpack(string zip, string target, Action<string>? check = null) => Unpack(zip, target, check, MaxUnpackedBytes, MaxEntries);

    /// <summary><see cref="Unpack(string, string, Action{string}?)"/> with other size limits (tests).</summary>
    internal static void Unpack(string zip, string target, Action<string>? check, long maxBytes, int maxEntries)
    {
        string temp = target + ".unpacking";
        FileTools.DeleteDirectory(temp);
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            // Refused before anything is written: a zip bomb, or something that isn't a mod.
            long declared = 0;
            foreach (ZipArchiveEntry entry in archive.Entries) declared += entry.Length;
            if (archive.Entries.Count > maxEntries || declared > maxBytes)
            {
                throw new InvalidDataException(Loc.F("{0} is too big to be a mod ({1} files, {2} MB); not installing it.", Path.GetFileName(zip), archive.Entries.Count, declared / (1024 * 1024))).InEnglish();
            }

            Directory.CreateDirectory(temp);
            string root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
            try
            {
                long written = 0;
                byte[] buffer = new byte[81920];
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.Length == 0) continue;
                    string destination = Path.GetFullPath(Path.Combine(temp, name));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(Loc.F("{0} has a file that would be written outside its folder ({1}); not installing it.", Path.GetFileName(zip), entry.FullName)).InEnglish();
                    }
                    if (name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    // Counted while writing too: an entry can unpack to more than its header says.
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            written += read;
                            if (written > maxBytes) throw new InvalidDataException(Loc.F("{0} unpacks to more than {1} MB; not installing it.", Path.GetFileName(zip), maxBytes / (1024 * 1024))).InEnglish();
                            output.Write(buffer, 0, read);
                        }
                    }
                    try
                    {
                        File.SetLastWriteTime(destination, entry.LastWriteTime.DateTime);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // A zip with no usable date; the file keeps today's.
                    }
                }
            }
            catch
            {
                FileTools.TryDeleteDirectory(temp);
                throw;
            }
        }
        try
        {
            check?.Invoke(temp);
        }
        catch
        {
            FileTools.TryDeleteDirectory(temp);
            throw;
        }
        FileTools.DeleteDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(temp, target);
    }

    /// <summary>Copies a local mod (a folder, or a single DLL) into the store.</summary>
    public string AddLocal(string id, string version, IEnumerable<(string Source, string RelativeTarget)> files)
    {
        string target = VersionDirectory(id, version);
        string temp = target + ".adding";
        FileTools.DeleteDirectory(temp);
        foreach ((string source, string relative) in files)
        {
            string destination = Path.Combine(temp, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Without the source's read-only mark (a git clone's), which would block later updates and deletes.
            FileTools.CopyFile(source, destination);
        }
        Directory.CreateDirectory(temp);
        FileTools.DeleteDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(temp, target);
        return target;
    }

    public void RemoveAll(string id)
    {
        string dir = Path.Combine(_paths.Store, SafeName(id));
        FileTools.TryDeleteDirectory(dir);
    }

    /// <summary>A name that is safe as one file or folder name (no path separators, never "." or "..").</summary>
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
}
