using System.IO.Compression;
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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PackageStore(AppPaths paths, ThunderstoreClient thunderstore)
    {
        _paths = paths;
        _thunderstore = thunderstore;
    }

    public string VersionDirectory(string id, string version) => Path.Combine(_paths.Store, SafeName(id), SafeName(version));

    public bool Has(string id, string version) => Directory.Exists(VersionDirectory(id, version));

    public IEnumerable<string> Versions(string id)
    {
        string dir = Path.Combine(_paths.Store, SafeName(id));
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().Where(v => !v.StartsWith('.')) : Enumerable.Empty<string>();
    }

    /// <summary>Downloads and unpacks one Thunderstore version unless it's already in the store; returns its folder.</summary>
    public async Task<string> EnsureAsync(TsVersion version, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        PackageReference reference = PackageReference.Parse(version.FullName) ?? throw new InvalidDataException($"Not a package version: {version.FullName}");
        string target = VersionDirectory(reference.Id, version.VersionNumber);
        if (Directory.Exists(target)) return target;

        await _gate.WaitAsync(cancel);
        try
        {
            if (Directory.Exists(target)) return target;
            AppLog.Info($"Downloading {version.FullName}.");
            string zip = await _thunderstore.DownloadAsync(version, progress, cancel);
            try
            {
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

    /// <summary>Unpacks a zip into <paramref name="target"/>, refusing entries that would land outside it.</summary>
    public static void Unpack(string zip, string target)
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
