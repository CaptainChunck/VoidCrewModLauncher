using System.IO.Compression;
using System.Text.Json;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Core.Mods;

/// <summary>A mod found in another BepInEx folder (Rai Pal, a manual install, r2modman), offered for import.</summary>
public sealed class ImportCandidate
{
    public string Name { get; init; } = "";

    /// <summary>Files to copy, with their place in a package (plugins\..., patchers\...).</summary>
    public List<(string Source, string RelativeTarget)> Files { get; init; } = new();

    public List<PluginInfo> Plugins { get; init; } = new();

    /// <summary>The Thunderstore package this seems to be, if any.</summary>
    public TsPackage? Match { get; init; }

    public string Version => Plugins.Select(p => p.Version).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "1.0.0";

    /// <summary>The local copy is newer than Thunderstore's (a build of your own, say), so it should stay local.</summary>
    public bool LocalIsNewer => Match?.Latest is { } latest && VersionNumber.Compare(Version, latest.VersionNumber) > 0;
}

/// <summary>Adding mods that aren't on Thunderstore: a DLL, a folder, a zip, or everything in another BepInEx folder.</summary>
public static class LocalMods
{
    /// <summary>Adds a DLL, a mod folder or a mod zip from your PC as a local mod, and turns it on.</summary>
    public static InstalledMod Add(ModManager manager, string path)
    {
        string name;
        var files = new List<(string, string)>();
        string? temp = null;
        try
        {
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                temp = Path.Combine(Path.GetTempPath(), "vcml-import-" + Guid.NewGuid().ToString("N"));
                PackageStore.Unpack(path, temp);
                name = Path.GetFileNameWithoutExtension(path);
                AddFolder(temp, files);
            }
            else if (File.Exists(path))
            {
                name = Path.GetFileNameWithoutExtension(path);
                files.Add((path, Path.GetFileName(path)));
            }
            else if (Directory.Exists(path))
            {
                name = Path.GetFileName(path.TrimEnd('\\', '/'));
                AddFolder(path, files);
            }
            else
            {
                throw new FileNotFoundException(Loc.F("Nothing to add at {0}", path));
            }

            // A Thunderstore-style manifest names the mod better than its file name.
            string? manifestName = null;
            string? manifestVersion = null;
            string? description = null;
            List<string> dependencies = new();
            (string Source, string Relative) manifest = files.FirstOrDefault(f => string.Equals(f.Item2, "manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifest.Source != null)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest.Source));
                    JsonElement root = document.RootElement;
                    manifestName = root.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    manifestVersion = root.TryGetProperty("version_number", out JsonElement v) ? v.GetString() : null;
                    description = root.TryGetProperty("description", out JsonElement d) ? d.GetString() : null;
                    if (root.TryGetProperty("dependencies", out JsonElement deps) && deps.ValueKind == JsonValueKind.Array)
                    {
                        dependencies = deps.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList();
                    }
                }
                catch (JsonException)
                {
                    // Not a manifest after all.
                }
            }

            List<PluginInfo> plugins = files.Where(f => f.Item1.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).SelectMany(f => PluginScanner.ScanFile(f.Item1)).ToList();
            string version = manifestVersion ?? plugins.Select(p => p.Version).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "1.0.0";
            string displayName = manifestName ?? plugins.FirstOrDefault()?.Name ?? name;
            return Store(manager, displayName, version, files, plugins, description, dependencies);
        }
        finally
        {
            if (temp != null && Directory.Exists(temp))
            {
                try { Directory.Delete(temp, true); } catch { /* temp folder */ }
            }
        }
    }

    private static void AddFolder(string folder, List<(string, string)> files)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            files.Add((file, Path.GetRelativePath(folder, file)));
        }
    }

    /// <summary>
    /// The mods in another BepInEx folder: one per entry of its plugins folder, together with the patchers entry of the
    /// same name (VR mods such as UUVR have both). The launcher's own bridge and BepInEx itself are left out.
    /// </summary>
    public static List<ImportCandidate> Scan(string bepInExFolder, PackageIndex index)
    {
        string folder = Path.GetFullPath(bepInExFolder);
        if (!Directory.Exists(Path.Combine(folder, "plugins")) && Directory.Exists(Path.Combine(folder, "BepInEx", "plugins")))
        {
            folder = Path.Combine(folder, "BepInEx");
        }
        string plugins = Path.Combine(folder, "plugins");
        string patchers = Path.Combine(folder, "patchers");
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] { plugins, patchers })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                string entryName = Path.GetFileName(entry);
                if (File.Exists(entry) && !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(entryName);
            }
        }

        var candidates = new List<ImportCandidate>();
        foreach (string entryName in names)
        {
            if (string.Equals(entryName, Protocol.BridgeDefaults.PluginFolder, StringComparison.OrdinalIgnoreCase)) continue;
            var files = new List<(string, string)>();
            foreach ((string dir, string kind) in new[] { (plugins, "plugins"), (patchers, "patchers") })
            {
                string entry = Path.Combine(dir, entryName);
                if (File.Exists(entry)) files.Add((entry, Path.Combine(kind, entryName)));
                else if (Directory.Exists(entry))
                {
                    foreach (string file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                    {
                        files.Add((file, Path.Combine(kind, entryName, Path.GetRelativePath(entry, file))));
                    }
                }
            }
            if (files.Count == 0) continue;
            List<PluginInfo> found = files.Where(f => f.Item1.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).SelectMany(f => PluginScanner.ScanFile(f.Item1)).ToList();
            if (found.Any(p => p.Guid == Protocol.BridgeDefaults.PluginGuid)) continue;
            string baseName = Path.GetFileNameWithoutExtension(entryName);
            TsPackage? match = index.FindByName(found.FirstOrDefault()?.Name) ?? index.FindByName(baseName);
            candidates.Add(new ImportCandidate { Name = found.FirstOrDefault()?.Name ?? baseName, Files = files, Plugins = found, Match = match });
        }
        return candidates;
    }

    /// <summary>Copies an import candidate into the library as a local mod and turns it on.</summary>
    public static InstalledMod Import(ModManager manager, ImportCandidate candidate) =>
        Store(manager, candidate.Name, candidate.Version, candidate.Files, candidate.Plugins, null, new List<string>());

    /// <summary>Settings files (BepInEx\config\*.cfg) of another BepInEx folder that the runtime doesn't have yet.</summary>
    public static int CopyMissingConfigs(string bepInExFolder, string runtimeBepInEx)
    {
        string source = Path.Combine(bepInExFolder, "config");
        if (!Directory.Exists(source) && Directory.Exists(Path.Combine(bepInExFolder, "BepInEx", "config"))) source = Path.Combine(bepInExFolder, "BepInEx", "config");
        if (!Directory.Exists(source)) return 0;
        string target = Path.Combine(runtimeBepInEx, "config");
        Directory.CreateDirectory(target);
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*.cfg"))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, "BepInEx.cfg", StringComparison.OrdinalIgnoreCase)) continue;
            string destination = Path.Combine(target, name);
            if (File.Exists(destination)) continue;
            File.Copy(file, destination);
            copied++;
        }
        return copied;
    }

    private static InstalledMod Store(ModManager manager, string name, string version, List<(string, string)> files, List<PluginInfo> plugins, string? description, List<string> dependencies)
    {
        string id = "local-" + (PackageIndex.Simplify(name) is { Length: > 0 } simple ? simple : "mod");
        manager.Store.AddLocal(id, version, files);
        InstalledMod? existing = manager.Library.Find(id);
        var mod = new InstalledMod
        {
            Id = id,
            Source = ModSource.Local,
            Name = name,
            Author = "Your PC",
            Version = version,
            Description = description ?? (plugins.Count > 0 ? string.Join(", ", plugins.Select(p => $"{p.Name} {p.Version}").Distinct()) : "Added from your PC."),
            Dependencies = dependencies,
            PluginGuids = plugins.Select(p => p.Guid).Distinct().ToList(),
            VoidManagerType = PluginScanner.StrictestType(plugins),
            Enabled = existing?.Enabled ?? true,
            WhenJoining = existing?.WhenJoining ?? JoinBehaviour.Auto,
            InstalledUtc = DateTime.UtcNow,
        };
        manager.Library.AddOrReplace(mod);
        AppLog.Info($"Added local mod {mod.Name} {mod.Version} ({files.Count} files).");
        return mod;
    }
}
