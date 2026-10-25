using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

/// <summary>
/// Who needs which mod: the advert's plugin GUIDs for local mods, the VoidManager types read from DLLs, the HOST tab's
/// choices against what joining players are checked for, and the rule the bridge applies when it lists a lobby's mods.
/// </summary>
public class ModNeedTests
{
    // ---------------------------------------------------------------- the advert

    [Fact]
    public void Advert_carries_a_local_mods_plugin_guids_and_older_launchers_still_read_it()
    {
        string advert = LobbyAdvert.Encode(new[]
        {
            new AdvertMod { Id = "Jack_Modding-Ectype_Framework", Version = "0.20.0", Need = ModNeed.Everyone },
            new AdvertMod
            {
                Id = "Ectype Role: Salvager", Version = "0.1.14", Need = ModNeed.Everyone, Local = true,
                PluginGuids = new List<string> { "jack.voidcrew.ectype.salvager", "odd;guid,with: separators!" },
            },
        });
        Assert.Equal("vcml1;Jack_Modding-Ectype_Framework@0.20.0!R;#Ectype Role: Salvager@0.1.14!R:jack.voidcrew.ectype.salvager,oddguidwithseparators", advert);

        List<AdvertMod> mods = LobbyAdvert.Decode(advert)!;
        Assert.Null(mods[0].PluginGuids);
        Assert.Equal("Ectype Role: Salvager", mods[1].Id);
        Assert.Equal("0.1.14", mods[1].Version);
        Assert.Equal(ModNeed.Everyone, mods[1].Need);
        Assert.True(mods[1].Local);
        Assert.Equal(new[] { "jack.voidcrew.ectype.salvager", "oddguidwithseparators" }, mods[1].PluginGuids);

        // Launcher 0.8.0 read each entry like this: the GUIDs after the need letter don't get in its way.
        string entry = advert.Split(';')[2][1..];
        int bang = entry.LastIndexOf('!');
        Assert.Equal(ModNeed.Everyone, LobbyAdvert.ParseNeed(entry[(bang + 1)..]));
        Assert.Equal("Ectype Role: Salvager@0.1.14", entry[..bang]);
    }

    [Fact]
    public void Advert_plugin_guids_are_bounded()
    {
        var many = Enumerable.Range(1, 20).Select(i => "guid." + i).ToList();
        string advert = LobbyAdvert.Encode(new[] { new AdvertMod { Id = "Big", Version = "1.0", Need = ModNeed.Everyone, Local = true, PluginGuids = many } });
        Assert.Equal(LobbyAdvert.MaxGuids, LobbyAdvert.Decode(advert)![0].PluginGuids!.Count);

        string crafted = "vcml1;#Big@1.0!R:" + string.Join(",", many) + "," + new string('x', 500);
        AdvertMod mod = Assert.Single(LobbyAdvert.Decode(crafted)!);
        Assert.Equal(LobbyAdvert.MaxGuids, mod.PluginGuids!.Count);
        Assert.All(mod.PluginGuids, g => Assert.True(g.Length <= LobbyAdvert.MaxGuidLength));
    }

    [Theory]
    // Joining players are checked for it: everyone, whatever the HOST tab or the guess says.
    [InlineData(ModNeed.HostOnly, true, ModNeed.Everyone, false, ModNeed.Everyone)]
    [InlineData(ModNeed.ClientOnly, false, ModNeed.Everyone, false, ModNeed.Everyone)]
    [InlineData(ModNeed.HostOnly, true, ModNeed.Session, true, ModNeed.Everyone)]
    // Otherwise the host's own choice wins (giving everyone a mod is always safe)...
    [InlineData(ModNeed.HostOnly, true, ModNeed.Unknown, false, ModNeed.HostOnly)]
    [InlineData(ModNeed.Everyone, true, ModNeed.ClientOnly, false, ModNeed.Everyone)]
    // ...then the mod's VoidManager type, then the launcher's guess.
    [InlineData(ModNeed.Everyone, false, ModNeed.ClientOnly, false, ModNeed.ClientOnly)]
    [InlineData(ModNeed.ClientOnly, false, ModNeed.Session, false, ModNeed.Session)]
    [InlineData(ModNeed.Unknown, false, ModNeed.Unknown, false, ModNeed.Unknown)]
    public void Advertised_need_follows_what_joining_players_are_checked_for(ModNeed launcher, bool chosen, ModNeed voidManager, bool checkedOnJoiners, ModNeed expected)
    {
        Assert.Equal(expected, LobbyAdvert.AdvertisedNeed(launcher, chosen, voidManager, checkedOnJoiners));
    }

    // ---------------------------------------------------------------- Auto

    [Theory]
    // VoidManager turns away joining players without an "All" mod, even one the launcher knows as client-side.
    [InlineData("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", "Client-Side", false, 30, ModNeed.Everyone)]
    [InlineData("local-ppc", "", true, 30, ModNeed.Everyone)]
    // What the mod tells VoidManager comes before its Thunderstore categories.
    [InlineData("A-Grapple", "Client-Side", false, 20, ModNeed.Session)]
    [InlineData("A-Speedometer", "All Required", false, 8, ModNeed.ClientOnly)]
    [InlineData("A-Scoop", "Mods", false, 14, ModNeed.HostOnly)]
    // Unmanaged (no VoidManager plugin) and unknown say nothing: the categories, or client-side for a local mod.
    [InlineData("A-Doors", "Host-Side", false, 4, ModNeed.HostOnly)]
    [InlineData("local-uuvr", "", true, 4, ModNeed.ClientOnly)]
    [InlineData("A-Mystery", "", false, null, ModNeed.Unknown)]
    [InlineData("Captain_Chuncks_Mods-Void_Crew_Fleet", "Client-Side", false, 4, ModNeed.Everyone)]
    public void Auto_follows_the_VoidManager_type_the_mod_declares(string id, string categories, bool local, int? voidManagerType, ModNeed expected)
    {
        string[] list = categories.Length == 0 ? Array.Empty<string>() : categories.Split(',');
        Assert.Equal(expected, ModPolicy.GuessNeed(id, list, local, voidManagerType));
    }

    private static InstalledMod Mod(string id, int? voidManagerType, ModSource source = ModSource.Thunderstore, params string[] guids) => new()
    {
        Id = id, Name = id, Version = "1.0.0", Source = source, VoidManagerType = voidManagerType, PluginGuids = guids.ToList(),
    };

    [Fact]
    public void Host_choices_give_way_to_what_joining_players_are_checked_for()
    {
        InstalledMod cannon = Mod("local-penitenceparticlecannon", 30, ModSource.Local, "jack.voidcrew.ppc");
        InstalledMod voidManager = Mod("NihilityShift-VoidManager", 4, ModSource.Thunderstore, ModPolicy.VoidManagerGuid);
        InstalledMod speedometer = Mod("Jack_Modding-Speedometer", 8);
        InstalledMod warzone = Mod("Jack_Modding-Warzone_Mission_Type", 20);
        InstalledMod scoop = Mod("NihilityShift-Improved_Scoop", 14);
        var chosen = new Dictionary<string, ModNeed>
        {
            [cannon.Id] = ModNeed.HostOnly,
            [speedometer.Id] = ModNeed.Everyone,
            [scoop.Id] = ModNeed.ClientOnly,
        };
        var needs = new HostNeeds(new[] { cannon, voidManager, speedometer, warzone, scoop }, chosen, new[] { warzone.Id });

        // VoidManager "All": everyone, and the host's "Only the host runs it" is overridden.
        Assert.Equal(ModNeed.Everyone, needs.Effective(cannon));
        Assert.Equal(ModNeed.Everyone, needs.Auto(cannon));
        Assert.True(needs.Overridden(cannon));
        // VoidManager itself while an "All" mod is on.
        Assert.True(needs.Checked(voidManager));
        Assert.Equal(ModNeed.Everyone, needs.Auto(voidManager));
        // The game required it last time (the bridge said so), though VoidManager calls it Session.
        Assert.Equal(ModNeed.Everyone, needs.Auto(warzone));
        // Safe choices win: everyone for a client-side mod, client-side for a host-side one.
        Assert.Equal(ModNeed.Everyone, needs.Effective(speedometer));
        Assert.False(needs.Overridden(speedometer));
        Assert.Equal(ModNeed.ClientOnly, needs.Effective(scoop));
        Assert.False(needs.Overridden(scoop));
        Assert.Equal(ModNeed.HostOnly, needs.Auto(scoop));

        // Without an "All" mod, VoidManager is a library again.
        var relaxed = new HostNeeds(new[] { voidManager, speedometer }, chosen, null);
        Assert.False(relaxed.Checked(voidManager));
        Assert.Equal(ModNeed.ClientOnly, relaxed.Effective(voidManager));
    }

    [Fact]
    public async Task Hosting_lists_a_mod_VoidManager_checks_for_everyone_whatever_the_HOST_tab_says()
    {
        using var temp = new TempFolder();
        using var service = new LauncherService(temp.AppPaths(), new FakeHttp());
        void Add(string id, string name, int type)
        {
            Directory.CreateDirectory(service.Store.VersionDirectory(id, "1.0.0"));
            service.Library.AddOrReplace(new InstalledMod { Id = id, Source = ModSource.Local, Name = name, Version = "1.0.0", VoidManagerType = type });
        }
        Add("local-ppc", "Penitence Particle Cannon", 30);
        Add("local-hud", "My HUD", 8);
        service.Settings.Host.ModNeeds["local-ppc"] = ModNeed.HostOnly;
        service.Settings.Host.ModNeeds["local-hud"] = ModNeed.Everyone;

        List<RuntimeMod> mods = await service.PlayModsAsync();
        Assert.Equal(ModNeed.Everyone, mods.Single(m => m.Id == "local-ppc").Need);
        Assert.Equal(ModNeed.Everyone, mods.Single(m => m.Id == "local-hud").Need);
        Assert.True(mods.Single(m => m.Id == "local-hud").NeedSetByUser);
    }

    [Fact]
    public void HOST_tab_choices_find_their_mod_in_any_case_after_a_restart()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        var settings = new LauncherSettings { BridgeToken = "t" };
        settings.Host.ModNeeds["Jack_Modding-Speedometer"] = ModNeed.Everyone;
        settings.Save(paths);

        // As the next start reads settings.json: still any case, as mod ids are everywhere else.
        LauncherSettings loaded = LauncherSettings.Load(paths);
        Assert.Equal(ModNeed.Everyone, loaded.Host.ModNeeds["jack_modding-speedometer"]);
        Assert.Equal(ModNeed.Everyone, new HostNeeds(Array.Empty<InstalledMod>(), loaded.Host.ModNeeds, null).Chosen("JACK_MODDING-SPEEDOMETER"));

        // A file that has the same id twice in different cases (written before) still loads, with one of them.
        File.WriteAllText(paths.SettingsFile, """{"bridgeToken":"t","host":{"modNeeds":{"A-Mod":"HostOnly","a-mod":"Everyone"}}}""");
        Assert.Single(LauncherSettings.Load(paths).Host.ModNeeds);
    }

    [Fact]
    public void Mods_the_game_checked_when_you_hosted_are_remembered_for_the_HOST_tab()
    {
        using var temp = new TempFolder();
        using var service = new LauncherService(temp.AppPaths(), new FakeHttp());
        InstalledMod warzone = Mod("Jack_Modding-Warzone_Mission_Type", 20);
        InstalledMod cannon = Mod("local-ppc", 30, ModSource.Local);
        service.Library.AddOrReplace(warzone);
        service.Library.AddOrReplace(cannon);
        service.UseLaunch(new LaunchFile { LaunchId = "host", Mods = new List<LaunchMod> { new() { Id = warzone.Id }, new() { Id = cannon.Id } } });

        Assert.True(service.RememberCheckedMods(new AdvertMessage { Required = new List<string> { warzone.Id, cannon.Id } }));
        // The cannon's DLL already says VoidManager checks it; only what the launcher can't see is kept.
        Assert.Equal(new[] { warzone.Id }, service.Settings.Host.CheckedInGame);
        Assert.Equal(ModNeed.Everyone, service.CurrentHostNeeds().Auto(warzone));

        // A later lobby where the game didn't require it any more.
        Assert.True(service.RememberCheckedMods(new AdvertMessage { Required = new List<string>() }));
        Assert.Empty(service.Settings.Host.CheckedInGame);
        Assert.False(service.RememberCheckedMods(new AdvertMessage { Required = new List<string>() }));
        Assert.Equal(ModNeed.Session, service.CurrentHostNeeds().Auto(warzone));
    }

    [Fact]
    public async Task Hosting_from_the_running_game_restarts_it_when_a_HOST_tab_choice_changed_since_it_started()
    {
        using var temp = new TempFolder();
        using var service = new LauncherService(temp.AppPaths(), new FakeHttp());
        void Add(string id, string name, string version, int type)
        {
            Directory.CreateDirectory(service.Store.VersionDirectory(id, version));
            service.Library.AddOrReplace(new InstalledMod { Id = id, Source = ModSource.Local, Name = name, Version = version, VoidManagerType = type });
        }
        Add("local-hud", "My HUD", "1.0.0", 8);
        Add("local-scoop", "My Scoop", "1.0.0", 14);
        Add("local-warzone", "Warzone", "1.0.0", 20);
        // launch.json as LaunchWithAsync writes it: the bridge reads it once, when the game starts.
        static LaunchFile Launch(List<RuntimeMod> mods) => new()
        {
            LaunchId = "play",
            Mods = mods.Select(m => new LaunchMod { Id = m.Id, Name = m.Name, Version = m.Version, Need = LobbyAdvert.NeedLetter(m.Need), NeedSetByUser = m.NeedSetByUser, Local = m.Local }).ToList(),
        };
        service.UseLaunch(Launch(await service.PlayModsAsync()));
        Assert.True(service.RunningGameHosts(await service.PlayModsAsync()));

        // Changed on the HOST tab after the game started: its lobby would still list the old choice.
        service.Settings.Host.ModNeeds["local-hud"] = ModNeed.Everyone;
        Assert.False(service.RunningGameHosts(await service.PlayModsAsync()));
        // Picked as what Auto says: the bridge still reads it differently (the host's choice beats the mod's own type).
        service.Settings.Host.ModNeeds["local-hud"] = ModNeed.ClientOnly;
        Assert.False(service.RunningGameHosts(await service.PlayModsAsync()));
        service.Settings.Host.ModNeeds.Remove("local-hud");
        Assert.True(service.RunningGameHosts(await service.PlayModsAsync()));

        // The game turned out to require Warzone of joining players when you hosted: the running game lists it for
        // everyone already, so the HOST tab now saying so too needs no restart.
        Assert.True(service.RememberCheckedMods(new AdvertMessage { Required = new List<string> { "local-warzone" } }));
        Assert.Equal(ModNeed.Everyone, (await service.PlayModsAsync()).Single(m => m.Id == "local-warzone").Need);
        Assert.True(service.RunningGameHosts(await service.PlayModsAsync()));

        // Another version or another mod, as before.
        Add("local-scoop", "My Scoop", "1.1.0", 14);
        Assert.False(service.RunningGameHosts(await service.PlayModsAsync()));
        Add("local-scoop", "My Scoop", "1.0.0", 14);
        Add("local-camera", "My Camera", "1.0.0", 8);
        Assert.False(service.RunningGameHosts(await service.PlayModsAsync()));
    }

    // ---------------------------------------------------------------- reading the type from DLLs

    [Fact]
    public void The_VoidManager_type_is_read_from_the_plugin_DLL()
    {
        using var temp = new TempFolder();
        int? Read(bool voidPlugin, PluginDlls.Getter getter = PluginDlls.Getter.None, int type = 30, string? declared = null) =>
            Assert.Single(PluginScanner.ScanFile(PluginDlls.Write(temp.Path, "test.plugin", voidPlugin, getter, type, declared))).VoidManagerType;

        Assert.Equal(30, Read(true, PluginDlls.Getter.Constant, 30));
        Assert.Equal(8, Read(true, PluginDlls.Getter.ShortConstant, 8));
        Assert.Equal(14, Read(true, PluginDlls.Getter.DebugConstant, 14));
        // A VoidPlugin that doesn't override MPType is VoidPlugin's default, Session.
        Assert.Equal(20, Read(true));
        // Decided while the game runs: the DLL's own declaration, else unknown.
        Assert.Null(Read(true, PluginDlls.Getter.Runtime));
        Assert.Equal(20, Read(true, PluginDlls.Getter.Runtime, declared: "Session"));
        // No VoidPlugin: VoidManager doesn't manage the plugin.
        Assert.Equal(4, Read(false));
        Assert.Equal("test.plugin", PluginScanner.ScanFile(PluginDlls.Write(temp.Path, "test.plugin", false)).Single().Guid);
    }

    [Fact]
    public void A_mods_type_is_the_strictest_of_its_plugins()
    {
        PluginInfo Plugin(int? type) => new("g" + type, "n", "1.0", "x.dll", type);
        Assert.Equal(4, PluginScanner.StrictestType(Array.Empty<PluginInfo>()));
        Assert.Equal(30, PluginScanner.StrictestType(new[] { Plugin(4), Plugin(30), Plugin(8) }));
        Assert.Null(PluginScanner.StrictestType(new[] { Plugin(4), Plugin(null) }));
        Assert.Equal(30, PluginScanner.StrictestType(new[] { Plugin(null), Plugin(30) }));
    }

    [Fact]
    public void Mods_installed_before_get_their_VoidManager_type_read_at_start()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        string folder = Path.Combine(paths.Store, "local-ppc", "1.38.0");
        Directory.CreateDirectory(folder);
        PluginDlls.Write(folder, "jack.voidcrew.ppc", true, PluginDlls.Getter.Constant, 30);
        // library.json as launcher 0.8.0 wrote it: no VoidManager type.
        File.WriteAllText(paths.LibraryFile, """
            {"format":1,"mods":[{"id":"local-ppc","source":"Local","name":"Penitence Particle Cannon","version":"1.38.0",
             "enabled":true,"whenJoining":"Auto","pluginGuids":["jack.voidcrew.ppc"]}]}
            """);

        using var service = new LauncherService(paths, new FakeHttp());
        InstalledMod mod = service.Library.Find("local-ppc")!;
        Assert.Equal(30, mod.VoidManagerType);
        Assert.Equal(ModNeed.Everyone, mod.GuessedNeed);
        // Kept when joining only by a lobby that runs it: VoidManager turns away a player who has it when the host doesn't.
        Assert.False(ModPolicy.KeepWhenJoining(mod));
        Assert.Contains("\"voidManagerType\": 30", File.ReadAllText(paths.LibraryFile));
    }
}

/// <summary>Small plugin DLLs for the scanner, written with PersistedAssemblyBuilder (never loaded or run).</summary>
internal static class PluginDlls
{
    public enum Getter
    {
        /// <summary>The VoidPlugin class doesn't override MPType.</summary>
        None,

        /// <summary>ldc.i4.s N; ret (a release build).</summary>
        Constant,

        /// <summary>ldc.i4.N; ret.</summary>
        ShortConstant,

        /// <summary>ldc.i4.s N; stloc.0; br.s; ldloc.0; ret (a debug build).</summary>
        DebugConstant,

        /// <summary>Computes the type (a config setting, say).</summary>
        Runtime,
    }

    public static string Write(string folder, string guid, bool voidPlugin, Getter getter = Getter.None, int type = 30, string? declared = null, string version = "1.0.0")
    {
        string name = "Plugin" + System.Guid.NewGuid().ToString("N");
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        if (declared != null)
        {
            assembly.SetCustomAttribute(typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!, Blob("MPType", declared));
        }
        ModuleBuilder module = assembly.DefineDynamicModule(name);

        TypeBuilder attribute = module.DefineType("BepInEx.BepInPlugin", TypeAttributes.Public | TypeAttributes.Class, typeof(Attribute));
        ConstructorBuilder constructor = attribute.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string), typeof(string), typeof(string) });
        constructor.GetILGenerator().Emit(OpCodes.Ret);
        attribute.CreateType();

        TypeBuilder plugin = module.DefineType("Mod.Plugin", TypeAttributes.Public | TypeAttributes.Class);
        plugin.SetCustomAttribute(constructor, Blob(guid, "Test Mod", version));
        plugin.CreateType();

        if (voidPlugin)
        {
            TypeBuilder basis = module.DefineType("VoidManager.VoidPlugin", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract);
            basis.CreateType();
            TypeBuilder mine = module.DefineType("Mod.ModVoidPlugin", TypeAttributes.Public | TypeAttributes.Class, basis);
            if (getter != Getter.None)
            {
                MethodBuilder method = mine.DefineMethod("get_MPType", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName, typeof(int), Type.EmptyTypes);
                ILGenerator il = method.GetILGenerator();
                switch (getter)
                {
                    case Getter.Constant:
                        il.Emit(OpCodes.Ldc_I4_S, (sbyte)type);
                        break;
                    case Getter.ShortConstant:
                        il.Emit(OpCodes.Ldc_I4_8);
                        break;
                    case Getter.DebugConstant:
                        il.DeclareLocal(typeof(int));
                        Label next = il.DefineLabel();
                        il.Emit(OpCodes.Ldc_I4_S, (sbyte)type);
                        il.Emit(OpCodes.Stloc_0);
                        il.Emit(OpCodes.Br_S, next);
                        il.MarkLabel(next);
                        il.Emit(OpCodes.Ldloc_0);
                        break;
                    case Getter.Runtime:
                        il.Emit(OpCodes.Ldc_I4_S, (sbyte)20);
                        il.Emit(OpCodes.Ldc_I4_8);
                        il.Emit(OpCodes.Add);
                        break;
                }
                il.Emit(OpCodes.Ret);
            }
            mine.CreateType();
        }

        string path = Path.Combine(folder, name + ".dll");
        using (FileStream stream = File.Create(path)) assembly.Save(stream);
        return path;
    }

    /// <summary>A custom attribute's value: prolog, the strings, no named arguments.</summary>
    private static byte[] Blob(params string[] values)
    {
        var bytes = new List<byte> { 1, 0 };
        foreach (string value in values)
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            bytes.Add((byte)text.Length);
            bytes.AddRange(text);
        }
        bytes.AddRange(new byte[] { 0, 0 });
        return bytes.ToArray();
    }
}
