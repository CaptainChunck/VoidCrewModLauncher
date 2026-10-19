using System.IO.Compression;
using System.Text.Json;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

public class InstallTests
{
    [Fact]
    public void Install_rules_follow_thunderstore_layouts()
    {
        using var temp = new TempFolder();
        temp.File("pkg/manifest.json", "{}");
        temp.File("pkg/icon.png");
        temp.File("pkg/README.md");
        temp.File("pkg/RootPlugin.dll");
        temp.File("pkg/Assets/bundle.metem");
        temp.File("pkg/BepInEx/plugins/Sub/Nested.dll");
        temp.File("pkg/BepInEx/config/mod.cfg");
        temp.File("pkg/patchers/Patcher.dll");
        temp.File("pkg/BepInEx/monomod/Hook.mm.dll");

        List<PackageFile> files = InstallRules.Map(Path.Combine(temp.Path, "pkg"));
        PackageFile Find(string name) => files.Single(f => f.SourcePath.EndsWith(name, StringComparison.Ordinal));

        Assert.Equal(6, files.Count);
        Assert.Equal((FileTarget.Plugins, "RootPlugin.dll"), (Find("RootPlugin.dll").Target, Find("RootPlugin.dll").RelativePath));
        Assert.Equal((FileTarget.Plugins, Path.Combine("Assets", "bundle.metem")), (Find("bundle.metem").Target, Find("bundle.metem").RelativePath));
        Assert.Equal((FileTarget.Plugins, Path.Combine("Sub", "Nested.dll")), (Find("Nested.dll").Target, Find("Nested.dll").RelativePath));
        Assert.Equal(FileTarget.Config, Find("mod.cfg").Target);
        Assert.Equal(FileTarget.Patchers, Find("Patcher.dll").Target);
        Assert.Equal(FileTarget.MonoMod, Find("Hook.mm.dll").Target);
    }

    [Fact]
    public void Install_rules_split_the_bepinex_pack()
    {
        using var temp = new TempFolder();
        temp.File("pack/BepInExPack/winhttp.dll");
        temp.File("pack/BepInExPack/doorstop_config.ini");
        temp.File("pack/BepInExPack/BepInEx/core/BepInEx.Preloader.dll");
        temp.File("pack/BepInExPack/BepInEx/config/BepInEx.cfg");

        List<PackageFile> files = InstallRules.Map(Path.Combine(temp.Path, "pack"));
        Assert.Contains(files, f => f.Target == FileTarget.GameRoot && f.RelativePath == "winhttp.dll");
        Assert.Contains(files, f => f.Target == FileTarget.GameRoot && f.RelativePath == "doorstop_config.ini");
        Assert.Contains(files, f => f.Target == FileTarget.Core && f.RelativePath == "BepInEx.Preloader.dll");
        Assert.Contains(files, f => f.Target == FileTarget.Config && f.RelativePath == "BepInEx.cfg");
    }

    [Fact]
    public void Unpack_refuses_files_outside_the_folder()
    {
        using var temp = new TempFolder();
        string zip = Path.Combine(temp.Path, "evil.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("ok.txt");
            archive.CreateEntry("../../escaped.txt");
        }
        string target = Path.Combine(temp.Path, "out");
        Assert.Throws<InvalidDataException>(() => PackageStore.Unpack(zip, target));
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(temp.Path, "..", "escaped.txt")));
    }

    [Fact]
    public void Plugin_scanner_reads_the_bridge()
    {
        using var temp = new TempFolder();
        string dll = Path.Combine(temp.Path, EmbeddedBridge.FileName);
        File.WriteAllBytes(dll, EmbeddedBridge.Read());
        List<PluginInfo> plugins = PluginScanner.ScanFile(dll);
        PluginInfo bridge = Assert.Single(plugins);
        Assert.Equal(BridgeDefaults.PluginGuid, bridge.Guid);
        Assert.Equal("Void Crew Mod Launcher Bridge", bridge.Name);
    }

    [Fact]
    public void Runtime_holds_exactly_the_launch_mods()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        temp.File("pack/BepInExPack/BepInEx/core/BepInEx.Preloader.dll", "preloader");
        temp.File("pack/BepInExPack/BepInEx/core/BepInEx.dll", "bepinex");
        temp.File("pack/BepInExPack/BepInEx/config/BepInEx.cfg", "default");
        string fleet = Path.GetDirectoryName(temp.File("store/Fleet/0.3.0/VoidCrewFleet.dll", "fleet"))!;
        string uuvr = Path.GetDirectoryName(temp.File("store/local-uuvr/1.0.0/plugins/uuvr/Uuvr.dll", "uuvr"))!;
        temp.File("store/local-uuvr/1.0.0/patchers/uuvr/UuvrPatcher.dll", "patcher");
        uuvr = Path.GetDirectoryName(Path.GetDirectoryName(uuvr))!;
        var builder = new RuntimeBuilder(paths);
        var launch = new LaunchFile { LaunchId = "one", Mode = "play", Token = "t", Mods = new List<LaunchMod>() };

        builder.Build(Path.Combine(temp.Path, "pack"), new[]
        {
            new RuntimeMod("Captain_Chuncks_Mods-Void_Crew_Fleet", "Void_Crew_Fleet", "0.3.0", fleet, false, ModNeed.Everyone),
            new RuntimeMod("local-uuvr", "UUVR", "1.0.0", uuvr, true, ModNeed.ClientOnly),
        }, launch);

        string bepInEx = paths.RuntimeBepInEx;
        Assert.True(File.Exists(Path.Combine(bepInEx, "core", "BepInEx.Preloader.dll")));
        Assert.True(File.Exists(Path.Combine(bepInEx, "config", "BepInEx.cfg")));
        Assert.True(File.Exists(Path.Combine(bepInEx, "plugins", "Captain_Chuncks_Mods-Void_Crew_Fleet", "VoidCrewFleet.dll")));
        Assert.True(File.Exists(Path.Combine(bepInEx, "plugins", "local-uuvr", "uuvr", "Uuvr.dll")));
        Assert.True(File.Exists(Path.Combine(bepInEx, "patchers", "local-uuvr", "uuvr", "UuvrPatcher.dll")));
        Assert.True(File.Exists(Path.Combine(bepInEx, "plugins", BridgeDefaults.PluginFolder, EmbeddedBridge.FileName)));
        using (JsonDocument written = JsonDocument.Parse(File.ReadAllText(paths.RuntimeLaunchFile)))
        {
            Assert.Equal("one", written.RootElement.GetProperty("launchId").GetString());
        }

        // Settings the player changed survive; a mod that isn't in the next launch is removed; stray files go.
        File.WriteAllText(Path.Combine(bepInEx, "config", "BepInEx.cfg"), "changed by the player");
        temp.File(Path.Combine("data", "runtime", "BepInEx", "plugins", "stray.dll"));
        builder.Build(Path.Combine(temp.Path, "pack"), new[]
        {
            new RuntimeMod("local-uuvr", "UUVR", "1.0.0", uuvr, true, ModNeed.ClientOnly),
        }, new LaunchFile { LaunchId = "two", Mode = "join", Token = "t", Mods = new List<LaunchMod>() });

        Assert.Equal("changed by the player", File.ReadAllText(Path.Combine(bepInEx, "config", "BepInEx.cfg")));
        Assert.False(Directory.Exists(Path.Combine(bepInEx, "plugins", "Captain_Chuncks_Mods-Void_Crew_Fleet")));
        Assert.False(File.Exists(Path.Combine(bepInEx, "plugins", "stray.dll")));
        Assert.True(File.Exists(Path.Combine(bepInEx, "plugins", "local-uuvr", "uuvr", "Uuvr.dll")));
        Assert.True(File.Exists(Path.Combine(bepInEx, "plugins", BridgeDefaults.PluginFolder, EmbeddedBridge.FileName)));
    }

    [Fact]
    public void Doorstop_is_added_switched_off_and_never_replaced()
    {
        using var temp = new TempFolder();
        string game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(game);
        temp.File("pack/BepInExPack/winhttp.dll", "proxy");
        temp.File("pack/BepInExPack/.doorstop_version", "4.5.0");

        Assert.False(Doorstop.Inspect(game).HasLoader);
        Doorstop.InstallIfMissing(game, Path.Combine(temp.Path, "pack"));
        DoorstopState state = Doorstop.Inspect(game);
        Assert.True(state.HasLoader);
        Assert.True(state.InstalledByLauncher);
        Assert.False(state.EnabledInConfig);
        Assert.Equal("4.5.0", state.Version);

        // Someone else's loader stays exactly as it is.
        string other = Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "winhttp.dll"), "rai pal");
        File.WriteAllText(Path.Combine(other, "doorstop_config.ini"), "[General]\nenabled=true\ntarget_assembly=C:\\raipal\\BepInEx\\core\\BepInEx.Preloader.dll\n");
        Doorstop.InstallIfMissing(other, Path.Combine(temp.Path, "pack"));
        Assert.Equal("rai pal", File.ReadAllText(Path.Combine(other, "winhttp.dll")));
        DoorstopState theirs = Doorstop.Inspect(other);
        Assert.False(theirs.InstalledByLauncher);
        Assert.True(theirs.EnabledInConfig);
        Assert.Equal(@"C:\raipal\BepInEx\core\BepInEx.Preloader.dll", theirs.ConfigTarget);
    }

    [Fact]
    public void Doorstop_under_another_name_or_a_leftover_config_is_never_overwritten()
    {
        using var temp = new TempFolder();
        temp.File("pack/BepInExPack/winhttp.dll", "proxy");
        temp.File("pack/BepInExPack/.doorstop_version", "4.5.0");
        string pack = Path.Combine(temp.Path, "pack");

        // Doorstop installed as version.dll: that is the loader, nothing is added.
        string renamed = Path.Combine(temp.Path, "renamed");
        temp.File("renamed/version.dll", "doorstop");
        temp.File("renamed/doorstop_config.ini", "[General]\nenabled=true\n");
        Assert.True(Doorstop.Inspect(renamed).HasLoader);
        Doorstop.InstallIfMissing(renamed, pack);
        Assert.False(File.Exists(Path.Combine(renamed, "winhttp.dll")));

        // A config left behind without its loader: the launcher stops and explains instead of replacing it.
        string leftover = Path.Combine(temp.Path, "leftover");
        temp.File("leftover/doorstop_config.ini", "[General]\nenabled=true\n");
        Assert.False(Doorstop.Inspect(leftover).HasLoader);
        Assert.Throws<InvalidOperationException>(() => Doorstop.InstallIfMissing(leftover, pack));
        Assert.Equal("[General]\nenabled=true\n", File.ReadAllText(Path.Combine(leftover, "doorstop_config.ini")));
        Assert.False(File.Exists(Path.Combine(leftover, "winhttp.dll")));

        // Someone else's version file stays.
        string versioned = Path.Combine(temp.Path, "versioned");
        temp.File("versioned/.doorstop_version", "3.4.0");
        Doorstop.InstallIfMissing(versioned, pack);
        Assert.Equal("3.4.0", File.ReadAllText(Path.Combine(versioned, ".doorstop_version")));
        Assert.True(Doorstop.Inspect(versioned).InstalledByLauncher);
    }
}
