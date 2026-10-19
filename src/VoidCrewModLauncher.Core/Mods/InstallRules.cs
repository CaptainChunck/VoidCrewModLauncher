namespace VoidCrewModLauncher.Core.Mods;

/// <summary>Where a file of an unpacked package goes in the runtime's BepInEx folder.</summary>
public enum FileTarget
{
    /// <summary>BepInEx\plugins\&lt;id&gt;\...</summary>
    Plugins,

    /// <summary>BepInEx\patchers\&lt;id&gt;\...</summary>
    Patchers,

    /// <summary>BepInEx\monomod\&lt;id&gt;\...</summary>
    MonoMod,

    /// <summary>BepInEx\core\... (BepInEx itself).</summary>
    Core,

    /// <summary>BepInEx\config\..., copied only when the file isn't there yet (a mod's default settings).</summary>
    Config,

    /// <summary>Next to the game's exe (BepInEx's Doorstop loader); never copied into the runtime.</summary>
    GameRoot,
}

public readonly record struct PackageFile(string SourcePath, FileTarget Target, string RelativePath);

/// <summary>
/// Thunderstore's BepInEx layout rules, as r2modman applies them, so packages land where their authors expect:
/// <list type="bullet">
/// <item>BepInEx\plugins, patchers, monomod, core, config (or the same folders at the package's root) go to the
/// matching BepInEx folder, plugins/patchers/monomod inside a folder named after the mod;</item>
/// <item>BepInExPack\ (BepInEx itself): its BepInEx folder as above, the Doorstop files to the game root;</item>
/// <item>everything else goes into plugins\&lt;id&gt;\ as it is;</item>
/// <item>the package's own manifest.json, icon.png, README and CHANGELOG stay in the store.</item>
/// </list>
/// </summary>
public static class InstallRules
{
    private static readonly HashSet<string> PackageInfoFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "LICENSE.md", "LICENSE.txt",
    };

    public static List<PackageFile> Map(string packageDirectory)
    {
        var files = new List<PackageFile>();
        string root = Path.GetFullPath(packageDirectory);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            PackageFile? mapped = MapOne(file, relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (mapped != null) files.Add(mapped.Value);
        }
        return files;
    }

    private static PackageFile? MapOne(string source, string[] parts)
    {
        if (parts.Length == 1 && PackageInfoFiles.Contains(parts[0])) return null;

        // BepInExPack: BepInExPack\BepInEx\... and the loader files next to it.
        if (parts.Length >= 2 && Is(parts[0], "BepInExPack"))
        {
            if (parts.Length == 2) return new PackageFile(source, FileTarget.GameRoot, parts[1]);
            if (Is(parts[1], "BepInEx")) return MapBepInExFolder(source, parts[2..]);
            return new PackageFile(source, FileTarget.GameRoot, Join(parts[1..]));
        }

        if (parts.Length >= 2 && Is(parts[0], "BepInEx")) return MapBepInExFolder(source, parts[1..]);

        if (parts.Length >= 2)
        {
            FileTarget? folder = FolderTarget(parts[0]);
            if (folder != null) return new PackageFile(source, folder.Value, Join(parts[1..]));
        }

        return new PackageFile(source, FileTarget.Plugins, Join(parts));
    }

    /// <summary>A path inside a package's BepInEx folder.</summary>
    private static PackageFile MapBepInExFolder(string source, string[] parts)
    {
        if (parts.Length >= 2)
        {
            FileTarget? folder = FolderTarget(parts[0]);
            if (folder != null) return new PackageFile(source, folder.Value, Join(parts[1..]));
        }
        return new PackageFile(source, FileTarget.Plugins, Join(parts));
    }

    private static FileTarget? FolderTarget(string folder)
    {
        if (Is(folder, "plugins")) return FileTarget.Plugins;
        if (Is(folder, "patchers")) return FileTarget.Patchers;
        if (Is(folder, "monomod")) return FileTarget.MonoMod;
        if (Is(folder, "core")) return FileTarget.Core;
        if (Is(folder, "config")) return FileTarget.Config;
        return null;
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Join(string[] parts) => Path.Combine(parts);
}
