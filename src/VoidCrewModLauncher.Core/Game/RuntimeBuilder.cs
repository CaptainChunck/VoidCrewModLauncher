using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Game;

/// <summary>One mod of a launch: a version in the store, and what other players need of it.</summary>
public sealed record RuntimeMod(string Id, string Name, string Version, string Folder, bool Local, ModNeed Need, bool NeedSetByUser = false);

/// <summary>
/// Builds the BepInEx folder the game loads (runtime\BepInEx) for one launch: BepInEx itself from the BepInExPack, the
/// launch's mods from the store (each in its own folder named after the mod), the bridge plugin, and launch.json.
/// Mods that are already in place at the right version are left alone, so switching between lobbies is quick.
/// BepInEx\config is never cleared: your mods' settings carry over from launch to launch.
/// </summary>
public sealed class RuntimeBuilder
{
    private const string MarkerFile = ".vcml-mod";
    private static readonly FileTarget[] ModFolders = { FileTarget.Plugins, FileTarget.Patchers, FileTarget.MonoMod };

    private readonly AppPaths _paths;

    public RuntimeBuilder(AppPaths paths)
    {
        _paths = paths;
    }

    /// <param name="showConsole">BepInEx's log console window next to the game (off for players; the BepInExPack ships it on).</param>
    public void Build(string bepInExPackFolder, IReadOnlyList<RuntimeMod> mods, LaunchFile launch, bool showConsole = false)
    {
        string bepInEx = _paths.RuntimeBepInEx;
        Directory.CreateDirectory(bepInEx);

        // BepInEx itself.
        List<PackageFile> pack = InstallRules.Map(bepInExPackFolder);
        SyncFiles(Path.Combine(bepInEx, "core"), pack.Where(f => f.Target == FileTarget.Core).ToList());
        CopyMissingConfigs(bepInEx, pack);
        SetConsole(Path.Combine(bepInEx, "config", "BepInEx.cfg"), showConsole);

        // The mods, each in BepInEx\<plugins|patchers|monomod>\<mod id>\.
        var wanted = ModFolders.ToDictionary(t => t, _ => new Dictionary<string, (string Marker, List<PackageFile> Files)>(StringComparer.OrdinalIgnoreCase));
        foreach (RuntimeMod mod in mods)
        {
            List<PackageFile> files = InstallRules.Map(mod.Folder);
            string marker = $"{mod.Id}|{mod.Version}|{Directory.GetCreationTimeUtc(mod.Folder).Ticks}";
            string folderName = PackageStore.SafeName(mod.Id);
            foreach (FileTarget target in ModFolders)
            {
                List<PackageFile> part = files.Where(f => f.Target == target).ToList();
                if (part.Count > 0) wanted[target][folderName] = (marker, part);
            }
            if (files.Any(f => f.Target == FileTarget.Core)) AppLog.Warn($"{mod.Id} ships BepInEx core files; the launcher keeps its own BepInEx and leaves them out.");
            CopyMissingConfigs(bepInEx, files);
        }

        foreach (FileTarget target in ModFolders)
        {
            string folder = Path.Combine(bepInEx, target.ToString().ToLowerInvariant());
            Directory.CreateDirectory(folder);
            SyncModFolders(folder, wanted[target], target == FileTarget.Plugins ? BridgeDefaults.PluginFolder : null);
        }

        InstallBridge(Path.Combine(bepInEx, "plugins", BridgeDefaults.PluginFolder));
        JsonFiles.Save(_paths.RuntimeLaunchFile, launch, JsonFiles.Protocol);
        File.WriteAllText(Path.Combine(_paths.Runtime, "README.txt"),
            "This folder is managed by the Void Crew Mod Launcher and rebuilt before every launch.\r\n" +
            "Change mods in the launcher; files added here by hand are removed. BepInEx\\config (mod settings) is kept.\r\n");
        AppLog.Info($"Runtime ready for {launch.Mode}: {mods.Count} mods.");
    }

    private static void SyncModFolders(string folder, Dictionary<string, (string Marker, List<PackageFile> Files)> wanted, string? keep)
    {
        foreach (string file in Directory.GetFiles(folder)) TryDelete(file);
        foreach (string dir in Directory.GetDirectories(folder))
        {
            string name = Path.GetFileName(dir);
            if (keep != null && string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (!wanted.ContainsKey(name)) FileTools.DeleteDirectory(dir);
        }

        foreach ((string name, (string marker, List<PackageFile> files)) in wanted)
        {
            string target = Path.Combine(folder, name);
            string markerPath = Path.Combine(target, MarkerFile);
            if (File.Exists(markerPath) && File.ReadAllText(markerPath) == marker) continue;
            FileTools.DeleteDirectory(target);
            foreach (PackageFile file in files)
            {
                string destination = Path.Combine(target, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                FileTools.CopyFile(file.SourcePath, destination);
            }
            File.WriteAllText(markerPath, marker);
        }
    }

    /// <summary>Makes <paramref name="folder"/> hold exactly <paramref name="files"/> (copying only what changed).</summary>
    private static void SyncFiles(string folder, List<PackageFile> files)
    {
        Directory.CreateDirectory(folder);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageFile file in files)
        {
            string destination = Path.Combine(folder, file.RelativePath);
            wanted.Add(Path.GetFullPath(destination));
            var source = new FileInfo(file.SourcePath);
            var existing = new FileInfo(destination);
            if (existing.Exists && existing.Length == source.Length && existing.LastWriteTimeUtc == source.LastWriteTimeUtc) continue;
            Directory.CreateDirectory(existing.DirectoryName!);
            FileTools.CopyFile(source.FullName, destination);
        }
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            if (!wanted.Contains(Path.GetFullPath(file))) TryDelete(file);
        }
    }

    private static void CopyMissingConfigs(string bepInEx, List<PackageFile> files)
    {
        foreach (PackageFile file in files.Where(f => f.Target == FileTarget.Config))
        {
            string destination = Path.Combine(bepInEx, "config", file.RelativePath);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            FileTools.CopyFile(file.SourcePath, destination);
        }
    }

    /// <summary>Sets [Logging.Console] Enabled in BepInEx.cfg, leaving the rest of the file as it is.</summary>
    private static void SetConsole(string config, bool show)
    {
        if (!File.Exists(config)) return;
        string[] lines = File.ReadAllLines(config);
        bool inSection = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith('[')) inSection = line.Equals("[Logging.Console]", StringComparison.OrdinalIgnoreCase);
            else if (inSection && line.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
            {
                string wanted = "Enabled = " + (show ? "true" : "false");
                if (lines[i] == wanted) return;
                lines[i] = wanted;
                File.WriteAllLines(config, lines);
                return;
            }
        }
    }

    private static void InstallBridge(string folder)
    {
        Directory.CreateDirectory(folder);
        byte[] bridge = EmbeddedBridge.Read();
        string path = Path.Combine(folder, EmbeddedBridge.FileName);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bridge)) return;
        File.WriteAllBytes(path, bridge);
    }

    private static void TryDelete(string file)
    {
        try
        {
            FileTools.DeleteFile(file);
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not remove {file}: {e.Message}");
        }
    }
}

/// <summary>The bridge plugin, built with the launcher and carried inside it.</summary>
public static class EmbeddedBridge
{
    public const string FileName = "VoidCrewModLauncher.Bridge.dll";

    public static byte[] Read()
    {
        using Stream stream = typeof(EmbeddedBridge).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException(Loc.T("This build of the launcher doesn't contain its bridge plugin.")).InEnglish();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
