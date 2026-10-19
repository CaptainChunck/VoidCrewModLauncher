using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Tests;

/// <summary>A temporary launcher data folder, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vcml-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content = "x")
    {
        string path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public AppPaths AppPaths()
    {
        var paths = new AppPaths(System.IO.Path.Combine(Path, "data"));
        paths.EnsureCreated();
        return paths;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // A file still open on a slow machine; the OS cleans temp eventually.
        }
    }
}

public static class Packages
{
    public static TsPackage Make(string owner, string name, string[] versions, string[]? categories = null, string[]? dependencies = null, bool deprecated = false, int downloads = 100)
    {
        return new TsPackage
        {
            Name = name,
            FullName = $"{owner}-{name}",
            Owner = owner,
            PackageUrl = $"https://thunderstore.io/c/void-crew/p/{owner}/{name}/",
            IsDeprecated = deprecated,
            Categories = (categories ?? Array.Empty<string>()).ToList(),
            Versions = versions.Select(v => new TsVersion
            {
                Name = name,
                FullName = $"{owner}-{name}-{v}",
                VersionNumber = v,
                Description = $"{name} {v}",
                Dependencies = (dependencies ?? Array.Empty<string>()).ToList(),
                DownloadUrl = $"https://thunderstore.io/package/download/{owner}/{name}/{v}/",
                Downloads = downloads,
                FileSize = 1000,
            }).ToList(),
        };
    }

    /// <summary>A small Void Crew community: BepInEx, VoidManager, a few mods.</summary>
    public static PackageIndex Index() => new(new[]
    {
        Make("BepInEx", "BepInExPack", new[] { "5.4.2305" }, downloads: 1_000_000),
        Make("NihilityShift", "VoidManager", new[] { "1.2.11", "1.2.10" }, new[] { "Libraries", "Mods" }, new[] { "BepInEx-BepInExPack-5.4.2100" }),
        Make("VoidCrewModdingTeam", "VoidManager", new[] { "1.2.6" }, new[] { "Libraries" }, deprecated: true, downloads: 50_000),
        Make("NihilityShift", "Improved_Scoop", new[] { "1.1.6", "1.1.5" }, new[] { "Mod_Session Required", "Mods", "Host-Side", "Quality of Life" }, new[] { "NihilityShift-VoidManager-1.2.10" }),
        Make("VoidCrewModdingTeam", "Max_Players", new[] { "1.1.2" }, new[] { "All Required", "Mods" }, new[] { "NihilityShift-VoidManager-1.2.0" }),
        Make("VoidCrewModdingTeam", "Pilot_HUD", new[] { "0.0.4" }, new[] { "Client-Side", "Mods" }),
        Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { "0.3.0", "0.2.1" }, new[] { "Client-Side", "Mods", "Host-Side" }),
        Make("Captain_Chuncks_Mods", "Void_Crew_VR_UI_Fix", new[] { "3.14.1" }, new[] { "Client-Side", "Mods" }),
        Make("Captain_Chuncks_Mods", "Ship_Customizer", new[] { "0.7.0" }, new[] { "Client-Side", "Mods", "Host-Side" }),
        Make("ebkr", "r2modman", new[] { "3.2.20" }, new[] { "Tools" }),
    });
}
