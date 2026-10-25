using System.Text.Json;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

/// <summary>
/// Which mods a PLAY or HOST launch loads: a mod that isn't on Thunderstore brings the dependencies its manifest names,
/// and a second copy of a plugin (both of VoidManager's packages turned on) isn't loaded, copied or advertised.
/// </summary>
public class LaunchSetTests
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Thunderstore's list with VoidManager (and its download) and BepInEx.</summary>
    private static FakeHttp Thunderstore()
    {
        TsPackage voidManager = Packages.Make("NihilityShift", "VoidManager", new[] { "1.2.11", "1.2.10" }, new[] { "Libraries" });
        TsPackage bepInEx = Packages.Make("BepInEx", "BepInExPack", new[] { "5.4.2305" });
        return new FakeHttp()
            .On(ThunderstoreClient.ListingIndexUrl, FakeHttp.Gzip("[\"https://cdn.test/chunk.blob\"]"))
            .On("https://cdn.test/chunk.blob", FakeHttp.Gzip(JsonSerializer.Serialize(new[] { voidManager, bepInEx }, Snake)))
            .On(voidManager.Versions[0].DownloadUrl, FakeHttp.Zip(("manifest.json", "{\"name\":\"VoidManager\",\"version_number\":\"1.2.11\"}"), ("VoidManager.dll", "vm")));
    }

    /// <summary>A mod in the library and the store, as if added from your PC (or installed, with <paramref name="source"/>).</summary>
    private static InstalledMod Add(LauncherService service, string id, string version, ModSource source = ModSource.Local, string[]? dependencies = null, string[]? guids = null)
    {
        Directory.CreateDirectory(service.Store.VersionDirectory(id, version));
        var mod = new InstalledMod
        {
            Id = id, Source = source, Name = id, Version = version, VoidManagerType = 8,
            Dependencies = (dependencies ?? Array.Empty<string>()).ToList(), PluginGuids = (guids ?? Array.Empty<string>()).ToList(),
        };
        service.Library.AddOrReplace(mod);
        return mod;
    }

    [Fact]
    public async Task A_local_mod_brings_the_dependencies_its_manifest_names()
    {
        using var temp = new TempFolder();
        using var service = new LauncherService(temp.AppPaths(), Thunderstore());
        await service.RefreshPackagesAsync(true);
        Add(service, "local-myhud", "1.0.0", dependencies: new[] { "BepInEx-BepInExPack-5.4.2100", "NihilityShift-VoidManager-1.2.10" });

        List<RuntimeMod> mods = await service.PlayModsAsync();
        Assert.Contains(mods, m => m.Id == "local-myhud" && m.Local);
        // Downloaded like the dependency of a Thunderstore mod: its newest version, into the store, not into your mods.
        RuntimeMod voidManager = Assert.Single(mods, m => m.Id == "NihilityShift-VoidManager");
        Assert.Equal("1.2.11", voidManager.Version);
        Assert.True(service.Store.Has("NihilityShift-VoidManager", "1.2.11"));
        Assert.Null(service.Library.Find("NihilityShift-VoidManager"));
        Assert.DoesNotContain(mods, m => PackageIndex.IsBepInExPack(m.Id));

        // The version you have turned on is used when it is new enough.
        Add(service, "NihilityShift-VoidManager", "1.2.10", ModSource.Thunderstore);
        Assert.Equal("1.2.10", (await service.PlayModsAsync()).Single(m => m.Id == "NihilityShift-VoidManager").Version);
    }

    [Fact]
    public async Task Both_of_VoidManagers_packages_turned_on_load_only_the_newer()
    {
        using var temp = new TempFolder();
        using var service = new LauncherService(temp.AppPaths(), new FakeHttp());
        string[] guid = { ModPolicy.VoidManagerGuid };
        Add(service, "NihilityShift-VoidManager", "1.2.11", ModSource.Thunderstore, guids: guid);
        Add(service, "VoidCrewModdingTeam-VoidManager", "1.2.6", ModSource.Thunderstore, guids: guid);
        Add(service, "local-myhud", "1.0.0", guids: new[] { "me.hud" });

        List<RuntimeMod> mods = await service.PlayModsAsync();
        Assert.Equal(new[] { "NihilityShift-VoidManager", "local-myhud" }, mods.Select(m => m.Id).Order(StringComparer.Ordinal));
        // Both stay on in your mods.
        Assert.True(service.Library.Find("VoidCrewModdingTeam-VoidManager")!.Enabled);

        // A mod with another plugin as well stays: that plugin would still load.
        Add(service, "VoidCrewModdingTeam-VoidManager", "1.2.6", ModSource.Thunderstore, guids: new[] { ModPolicy.VoidManagerGuid, "someone.extra" });
        Assert.Contains(await service.PlayModsAsync(), m => m.Id == "VoidCrewModdingTeam-VoidManager");
    }

    [Fact]
    public void The_copy_BepInEx_would_load_wins_by_the_version_of_its_plugin()
    {
        using var temp = new TempFolder();
        using var service = new LauncherService(temp.AppPaths(), new FakeHttp());
        // An older package that carries the newer plugin: BepInEx goes by the plugin's version in the DLL.
        InstalledMod oldPackage = Add(service, "Someone-Old_Package", "1.0.0", ModSource.Thunderstore, guids: new[] { "test.plugin" });
        InstalledMod newPackage = Add(service, "Someone-New_Package", "2.0.0", ModSource.Thunderstore, guids: new[] { "test.plugin" });
        PluginDlls.Write(service.Store.VersionDirectory(oldPackage.Id, oldPackage.Version), "test.plugin", false, version: "3.0.0");
        PluginDlls.Write(service.Store.VersionDirectory(newPackage.Id, newPackage.Version), "test.plugin", false, version: "2.0.0");
        List<RuntimeMod> Launch() => new()
        {
            new(newPackage.Id, newPackage.Name, newPackage.Version, service.Store.VersionDirectory(newPackage.Id, newPackage.Version), false, ModNeed.Everyone),
            new(oldPackage.Id, oldPackage.Name, oldPackage.Version, service.Store.VersionDirectory(oldPackage.Id, oldPackage.Version), false, ModNeed.Everyone),
        };
        Assert.Equal(new[] { oldPackage.Id }, service.Mods.WithoutDuplicatePlugins(Launch()).Select(m => m.Id));

        // A DLL that can't be read: the package's own version stands in for its plugin's.
        File.Delete(Directory.GetFiles(service.Store.VersionDirectory(oldPackage.Id, oldPackage.Version), "*.dll").Single());
        Assert.Equal(new[] { newPackage.Id }, service.Mods.WithoutDuplicatePlugins(Launch()).Select(m => m.Id));

        // Mods the library doesn't know the plugins of are all kept.
        var unknown = new List<RuntimeMod> { new("A-One", "One", "1.0.0", "x", false, ModNeed.Everyone), new("A-Two", "Two", "1.0.0", "y", false, ModNeed.Everyone) };
        Assert.Equal(2, service.Mods.WithoutDuplicatePlugins(unknown).Count);
    }
}
