using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

public class LobbyTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ModManager _manager;
    private readonly JoinPlanner _planner;

    public LobbyTests()
    {
        AppPaths paths = _temp.AppPaths();
        var library = new ModLibrary(paths);
        var store = new PackageStore(paths, new ThunderstoreClient(paths));
        _manager = new ModManager(library, store) { Index = Packages.Index() };
        _planner = new JoinPlanner(_manager);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Puts a mod in the library and the store, as if it had been installed.</summary>
    private void Have(string id, string version, bool enabled = true)
    {
        string folder = _manager.Store.VersionDirectory(id, version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Plugin.dll"), id);
        TsPackage? package = _manager.Index.Find(id);
        InstalledMod mod = package != null
            ? ModManager.Describe(package, package.Version(version)!, folder)
            : new InstalledMod { Id = id, Name = id, Version = version, Source = ModSource.Local };
        mod.Enabled = enabled;
        _manager.Library.AddOrReplace(mod);
    }

    private static RoomDto Room(string? advert = null, byte[]? voidManager = null, string modding = "session") => new()
    {
        Id = "ABC123",
        Name = "[Mods Required] Captain's Room",
        Host = "Captain",
        Players = 2,
        MaxPlayers = 6,
        Modding = modding,
        Advert = advert,
        VoidManagerMods = voidManager == null ? null : Convert.ToBase64String(voidManager),
    };

    [Fact]
    public void Launcher_lobby_needs_its_required_mods_and_keeps_your_client_mods()
    {
        Have("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", "3.14.1");
        Have("VoidCrewModdingTeam-Pilot_HUD", "0.0.4");
        Have("VoidCrewModdingTeam-Max_Players", "1.1.2");
        Have("NihilityShift-VoidManager", "1.2.11");
        Have("local-uuvr", "1.0.0");

        string advert = LobbyAdvert.Encode(new[]
        {
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Version = "0.2.1", Need = ModNeed.Everyone },
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", Version = "3.14.1", Need = ModNeed.ClientOnly },
            new AdvertMod { Id = "UUVR", Version = "1.0", Need = ModNeed.ClientOnly, Local = true },
        });
        Lobby lobby = LobbyReader.Read(Room(advert), "eu", _manager.Index);
        Assert.Equal("Captain's Room", lobby.Name);
        Assert.Equal(LobbyModSource.Advert, lobby.ModSource);

        JoinPlan plan = _planner.Plan(lobby);
        JoinPlanLine fleet = plan.Lines.Single(l => l.Mod.Id == "Captain_Chuncks_Mods-Void_Crew_Fleet");
        Assert.True(fleet.Include);
        Assert.Equal(LobbyModStatus.Download, fleet.Status);
        Assert.Equal("0.2.1", fleet.Version?.VersionNumber);
        Assert.All(plan.Lines.Where(l => l != fleet), l => Assert.Equal(LobbyModStatus.NotNeeded, l.Status));

        Assert.Contains(plan.Kept, m => m.Id == "VoidCrewModdingTeam-Pilot_HUD");
        Assert.Contains(plan.Kept, m => m.Id == "local-uuvr");
        Assert.Contains(plan.TurnedOff, m => m.Id == "VoidCrewModdingTeam-Max_Players");

        ResolvedJoin join = _planner.Resolve(plan);
        Assert.Contains(join.Mods, m => m.Id == "Captain_Chuncks_Mods-Void_Crew_Fleet" && m.Version == "0.2.1" && m.Need == ModNeed.Everyone);
        Assert.Contains(join.Mods, m => m.Id == "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix");
        Assert.Contains(join.Mods, m => m.Id == "local-uuvr" && m.Local);
        Assert.DoesNotContain(join.Mods, m => m.Id == "VoidCrewModdingTeam-Max_Players");
        var download = Assert.Single(join.Downloads);
        Assert.Equal("Captain_Chuncks_Mods-Void_Crew_Fleet", download.Package.FullName);
    }

    [Fact]
    public void VoidManager_lobby_needs_its_all_type_mods_and_their_dependencies()
    {
        byte[] list = VoidManagerModList.Encode("1.2.11", new[]
        {
            new VoidManagerMod("VoidManager", "VoidCrewModdingTeam.VoidManager", "1.2.11", 8, "NihilityShift/VoidManager"),
            new VoidManagerMod("Max Players", "VoidCrewModdingTeam.MaxPlayers", "1.1.2", 30, ""),
            new VoidManagerMod("Improved Scoop", "NihilityShift.ImprovedScoop", "1.1.6", 20, ""),
            new VoidManagerMod("Void Crew Mod Launcher Bridge", BridgeDefaults.PluginGuid, "0.1.0", 4, ""),
        });
        Lobby lobby = LobbyReader.Read(Room(voidManager: list), "us", _manager.Index);
        Assert.Equal(LobbyModSource.VoidManager, lobby.ModSource);
        Assert.Equal(3, lobby.Mods.Count);

        JoinPlan plan = _planner.Plan(lobby);
        Assert.True(plan.Lines.Single(l => l.Mod.Id == "VoidCrewModdingTeam-Max_Players").Include);
        Assert.Equal(LobbyModStatus.NotNeeded, plan.Lines.Single(l => l.Mod.Id == "NihilityShift-Improved_Scoop").Status);
        // With an "All" mod in the room, VoidManager turns away players who don't run it: everyone needs it too.
        JoinPlanLine voidManager = plan.Lines.Single(l => l.Mod.Id == "NihilityShift-VoidManager");
        Assert.Equal(ModNeed.Everyone, voidManager.Mod.Need);
        Assert.True(voidManager.Include);
        Assert.Equal("1.2.11", voidManager.Version?.VersionNumber);

        ResolvedJoin join = _planner.Resolve(plan);
        Assert.Contains(join.Mods, m => m.Id == "VoidCrewModdingTeam-Max_Players");
        Assert.Contains(join.Mods, m => m.Id == "NihilityShift-VoidManager");
        Assert.Equal(2, join.Downloads.Count);
        // Dependencies come before the mods that need them.
        Assert.Equal("NihilityShift-VoidManager", join.Downloads[0].Package.FullName);
    }

    [Fact]
    public void Advert_needs_are_refined_by_VoidManager_types()
    {
        string advert = LobbyAdvert.Encode(new[] { new AdvertMod { Id = "VoidCrewModdingTeam-Pilot_HUD", Version = "0.0.4", Need = ModNeed.Unknown } });
        byte[] list = VoidManagerModList.Encode("1.2.11", new[] { new VoidManagerMod("Pilot HUD", "pilot.hud", "0.0.4", 30, "") });
        Lobby lobby = LobbyReader.Read(Room(advert, list), "eu", _manager.Index);
        LobbyMod mod = Assert.Single(lobby.Mods, m => !ModPolicy.IsVoidManager(m.Id));
        Assert.Equal(ModNeed.Everyone, mod.Need);
        Assert.Equal(new[] { "pilot.hud" }, mod.PluginGuids);
    }

    [Fact]
    public void Modded_lobby_without_a_list_warns()
    {
        Lobby lobby = LobbyReader.Read(Room(), "eu", _manager.Index);
        Assert.True(lobby.ModsUnknown);
        Assert.NotEmpty(_planner.Plan(lobby).Warnings);
    }

    [Fact]
    public void Vanilla_lobby_turns_off_gameplay_mods_only()
    {
        Have("VoidCrewModdingTeam-Max_Players", "1.1.2");
        Have("VoidCrewModdingTeam-Pilot_HUD", "0.0.4");
        Lobby lobby = LobbyReader.Read(Room(modding: "none"), "eu", _manager.Index);
        Assert.False(lobby.IsModded);
        JoinPlan plan = _planner.Plan(lobby);
        Assert.Empty(plan.Lines);
        Assert.Contains(plan.TurnedOff, m => m.Id == "VoidCrewModdingTeam-Max_Players");
        Assert.Contains(plan.Kept, m => m.Id == "VoidCrewModdingTeam-Pilot_HUD");
    }

    /// <summary>A launch.json listing these mods, as the launcher writes it.</summary>
    private static LaunchFile Running(params (string Id, string Version, ModNeed Need)[] mods) => new()
    {
        Mods = mods.Select(m => new LaunchMod { Id = m.Id, Version = m.Version, Need = LobbyAdvert.NeedLetter(m.Need) }).ToList(),
    };

    [Fact]
    public void Running_game_fits_when_it_has_the_same_mods()
    {
        var join = new ResolvedJoin();
        join.Mods.Add(new RuntimeMod("A-Mod", "Mod", "1.0.0", "x", false, ModNeed.Everyone));
        join.Mods.Add(new RuntimeMod("B-Hud", "Hud", "2.0.0", "y", false, ModNeed.ClientOnly));

        Assert.True(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone), ("B-Hud", "2.0.0", ModNeed.ClientOnly))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.1", ModNeed.Everyone), ("B-Hud", "2.0.0", ModNeed.ClientOnly))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone))));
        // Any other mod the game runs means a restart, whoever needs it: the JOIN dialog says it's off for this game.
        Assert.False(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone), ("B-Hud", "2.0.0", ModNeed.ClientOnly), ("C-Gameplay", "1.0.0", ModNeed.Everyone))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone), ("B-Hud", "2.0.0", ModNeed.ClientOnly), ("D-Scoop", "1.0.0", ModNeed.Session))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone), ("B-Hud", "2.0.0", ModNeed.ClientOnly), ("E-Skin", "1.0.0", ModNeed.ClientOnly))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running()));
        Assert.False(JoinPlanner.RunningGameFits(join, null));
    }

    [Fact]
    public void Joining_restarts_a_game_that_runs_a_mod_the_join_turns_off()
    {
        Have("NihilityShift-VoidManager", "1.2.11");
        Have("VoidCrewModdingTeam-Pilot_HUD", "0.0.4");
        // A VoidManager "Session" mod: joining players don't need it, but VoidManager disconnects a player who runs one
        // from a lobby that isn't modded (and says nothing the launcher can show).
        HaveCopy("local-grapple", "Personal Grapple", "1.0.0", new[] { "jack.voidcrew.grapple" }, enabled: true, voidManagerType: 20);
        JoinPlan plan = _planner.Plan(LobbyReader.Read(Room(modding: "none"), "eu", _manager.Index));
        Assert.Contains(plan.TurnedOff, m => m.Id == "local-grapple");
        ResolvedJoin join = _planner.Resolve(plan);
        Assert.Equal(new[] { "NihilityShift-VoidManager", "VoidCrewModdingTeam-Pilot_HUD" }, join.Mods.Select(m => m.Id).Order());

        // The game as PLAY started it, with every mod that's on.
        LaunchFile played = Running(("NihilityShift-VoidManager", "1.2.11", ModNeed.ClientOnly), ("VoidCrewModdingTeam-Pilot_HUD", "0.0.4", ModNeed.ClientOnly),
            ("local-grapple", "1.0.0", ModNeed.Session));
        Assert.False(JoinPlanner.RunningGameFits(join, played));

        // Started again for this join: it has just what the join loads.
        var joined = new LaunchFile { Mods = join.Mods.Select(m => new LaunchMod { Id = m.Id, Version = m.Version, Need = LobbyAdvert.NeedLetter(m.Need), Local = m.Local }).ToList() };
        Assert.True(JoinPlanner.RunningGameFits(join, joined));
    }

    [Fact]
    public void A_long_VoidManager_list_is_merged_entry_by_entry()
    {
        // Ten mods listed a hundred times each (VoidManager allows 1000 entries; the list comes from another player).
        byte[] list = VoidManagerModList.Encode("1.2.11", Enumerable.Range(0, 1000).Select(i => new VoidManagerMod($"Mod {i % 10}", $"someone.mod{i % 10}", "1.0.0", 30, "")));
        Lobby lobby = LobbyReader.Read(Room(voidManager: list), "eu", _manager.Index);
        Assert.Equal(10, lobby.Mods.Count(m => !ModPolicy.IsVoidManager(m.Id)));
        Assert.All(lobby.Mods, m => Assert.Equal(ModNeed.Everyone, m.Need));
        Assert.Equal(new[] { "someone.mod3" }, lobby.Mods.Single(m => m.Name == "Mod 3").PluginGuids);

        // An advert's mods take VoidManager's entries for them by GUID, package or name; the others are left out.
        string advert = LobbyAdvert.Encode(new[]
        {
            new AdvertMod { Id = "VoidCrewModdingTeam-Max_Players", Version = "1.1.2", Need = ModNeed.HostOnly },
            new AdvertMod { Id = "Mod 4", Version = "1.0.0", Need = ModNeed.HostOnly, Local = true },
        });
        byte[] withMaxPlayers = VoidManagerModList.Encode("1.2.11", Enumerable.Range(0, 1000).Select(i => i % 3 == 0
            ? new VoidManagerMod("Max Players", "VoidCrewModdingTeam.MaxPlayers", "1.1.2", 30, "")
            : new VoidManagerMod($"Mod {i % 10}", $"someone.mod{i % 10}", "1.0.0", 30, "")));
        Lobby advertised = LobbyReader.Read(Room(advert, withMaxPlayers), "eu", _manager.Index);
        Assert.Equal(new[] { "Mod 4", "Max_Players", "VoidManager" }.Order(), advertised.Mods.Select(m => m.Package?.Name ?? m.Name).Order());
        Assert.Equal(new[] { "VoidCrewModdingTeam.MaxPlayers" }, advertised.Mods.Single(m => m.Id == "VoidCrewModdingTeam-Max_Players").PluginGuids);
        Assert.Equal(new[] { "someone.mod4" }, advertised.Mods.Single(m => m.Name == "Mod 4").PluginGuids);
        Assert.All(advertised.Mods, m => Assert.Equal(ModNeed.Everyone, m.Need));
    }

    // ---------------------------------------------------------------- the host's local mods

    /// <summary>A mod with these plugin GUIDs in the library and the store: added from your PC, unless another source is given.</summary>
    private InstalledMod HaveCopy(string id, string name, string version, string[] guids, bool enabled = false, int? voidManagerType = null,
        ModSource source = ModSource.Local, JoinBehaviour joining = JoinBehaviour.Auto)
    {
        string folder = _manager.Store.VersionDirectory(id, version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Plugin.dll"), id);
        var mod = new InstalledMod
        {
            Id = id, Name = name, Version = version, Source = source, PluginGuids = guids.ToList(), Enabled = enabled,
            VoidManagerType = voidManagerType, WhenJoining = joining,
        };
        _manager.Library.AddOrReplace(mod);
        return mod;
    }

    /// <summary>A host running Fleet and a local copy of Jack's particle cannon (as launcher 0.8.2 advertises it, with its GUID).</summary>
    private static string CannonAdvert(string version = "1.38.0", params string[] guids) => LobbyAdvert.Encode(new[]
    {
        new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Version = "0.3.0", Need = ModNeed.Everyone },
        new AdvertMod { Id = "Penitence Particle Cannon", Version = version, Need = ModNeed.Everyone, Local = true, PluginGuids = guids.ToList() },
    });

    [Fact]
    public void Host_local_mod_is_found_by_its_plugin_guid_whatever_your_copy_is_called()
    {
        // Added from a differently named zip on this PC, and switched off in the INSTALLED tab.
        HaveCopy("local-ppcbuild", "PPC build", "1.38.0", new[] { "jack.voidcrew.ppc" }, enabled: false, voidManagerType: 30);
        Lobby lobby = LobbyReader.Read(Room(CannonAdvert("1.38.0", "jack.voidcrew.ppc")), "eu", _manager.Index);

        JoinPlan plan = _planner.Plan(lobby);
        JoinPlanLine cannon = plan.Lines.Single(l => l.Mod.Local);
        Assert.Equal(LobbyModStatus.Ready, cannon.Status);
        Assert.True(cannon.Include);
        Assert.Equal("local-ppcbuild", cannon.Copy?.Id);
        Assert.Empty(plan.Warnings);
        Assert.DoesNotContain(plan.TurnedOff, m => m.Id == "local-ppcbuild");

        ResolvedJoin join = _planner.Resolve(plan);
        RuntimeMod loaded = Assert.Single(join.Mods, m => m.Id == "local-ppcbuild");
        Assert.True(loaded.Local);
        Assert.Equal("1.38.0", loaded.Version);
        Assert.Contains(join.Mods, m => m.Id == "Captain_Chuncks_Mods-Void_Crew_Fleet");
    }

    [Fact]
    public void Missing_local_mod_says_which_one_and_that_it_cant_be_downloaded()
    {
        Lobby lobby = LobbyReader.Read(Room(CannonAdvert("1.38.0", "jack.voidcrew.ppc")), "eu", _manager.Index);
        JoinPlan plan = _planner.Plan(lobby);
        JoinPlanLine cannon = plan.Lines.Single(l => l.Mod.Local);
        Assert.Equal(LobbyModStatus.Unavailable, cannon.Status);
        Assert.False(cannon.Include);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("Penitence Particle Cannon", warning);
        Assert.Contains("jack.voidcrew.ppc", warning);
        Assert.Contains("can't be downloaded", warning);
    }

    [Fact]
    public void Local_mod_at_another_version_isnt_used()
    {
        HaveCopy("local-penitenceparticlecannon", "Penitence Particle Cannon", "1.37.0", new[] { "jack.voidcrew.ppc" }, enabled: true, voidManagerType: 30);
        Lobby lobby = LobbyReader.Read(Room(CannonAdvert("1.38.0", "jack.voidcrew.ppc")), "eu", _manager.Index);
        JoinPlan plan = _planner.Plan(lobby);
        Assert.Equal(LobbyModStatus.Unavailable, plan.Lines.Single(l => l.Mod.Local).Status);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("1.38.0", warning);
        Assert.Contains("1.37.0", warning);
        // Your other version stays off: VoidManager checks the version too.
        Assert.Contains(plan.TurnedOff, m => m.Id == "local-penitenceparticlecannon");
    }

    [Fact]
    public void Old_host_advert_without_guids_uses_VoidManager_list_or_the_name()
    {
        HaveCopy("local-mycannon", "My cannon", "1.38.0", new[] { "jack.voidcrew.ppc" });
        string oldAdvert = "vcml1;Captain_Chuncks_Mods-Void_Crew_Fleet@0.3.0!R;#Penitence Particle Cannon@1.38.0!R";
        byte[] list = VoidManagerModList.Encode("1.2.11", new[] { new VoidManagerMod("Penitence Particle Cannon", "jack.voidcrew.ppc", "1.38.0", 30, "") });

        Lobby withList = LobbyReader.Read(Room(oldAdvert, list), "eu", _manager.Index);
        Assert.Equal(new[] { "jack.voidcrew.ppc" }, withList.Mods.Single(m => m.Local).PluginGuids);
        Assert.Equal("local-mycannon", _planner.Plan(withList).Lines.Single(l => l.Mod.Local).Copy?.Id);

        // No GUID anywhere: a copy with the same name.
        Lobby withoutList = LobbyReader.Read(Room(oldAdvert), "eu", _manager.Index);
        Assert.Null(_planner.Plan(withoutList).Lines.Single(l => l.Mod.Local).Copy);
        HaveCopy("local-penitenceparticlecannon", "Penitence Particle Cannon", "1.38.0", new[] { "jack.voidcrew.ppc" });
        Assert.Equal("local-penitenceparticlecannon", _planner.Plan(withoutList).Lines.Single(l => l.Mod.Local).Copy?.Id);
    }

    [Fact]
    public void The_same_plugin_from_Thunderstore_counts_as_your_copy()
    {
        HaveCopy("Jack_Modding-Penitence_Particle_Cannon", "Penitence_Particle_Cannon", "1.38.0", new[] { "jack.voidcrew.ppc" }, enabled: true, voidManagerType: 30, source: ModSource.Thunderstore);
        Lobby lobby = LobbyReader.Read(Room(CannonAdvert("1.38.0", "jack.voidcrew.ppc")), "eu", _manager.Index);
        JoinPlan plan = _planner.Plan(lobby);
        Assert.Equal("Jack_Modding-Penitence_Particle_Cannon", plan.Lines.Single(l => l.Mod.Local).Copy?.Id);
        Assert.DoesNotContain(plan.TurnedOff, m => m.Id == "Jack_Modding-Penitence_Particle_Cannon");
        Assert.Contains(_planner.Resolve(plan).Mods, m => m.Id == "Jack_Modding-Penitence_Particle_Cannon" && !m.Local);
    }

    [Fact]
    public void A_second_copy_of_a_plugin_the_lobby_loads_stays_off()
    {
        HaveCopy("local-ppc", "Penitence Particle Cannon", "1.38.0", new[] { "jack.voidcrew.ppc" }, enabled: true, voidManagerType: 30);
        HaveCopy("local-ppcdev", "PPC dev", "1.39.0", new[] { "jack.voidcrew.ppc" }, enabled: true, voidManagerType: 30, joining: JoinBehaviour.Keep);
        Lobby lobby = LobbyReader.Read(Room(CannonAdvert("1.38.0", "jack.voidcrew.ppc")), "eu", _manager.Index);
        JoinPlan plan = _planner.Plan(lobby);
        Assert.Equal("local-ppc", plan.Lines.Single(l => l.Mod.Local).Copy?.Id);
        Assert.Contains(plan.TurnedOff, m => m.Id == "local-ppcdev");
        Assert.DoesNotContain(_planner.Resolve(plan).Mods, m => m.Id == "local-ppcdev");
    }

    [Fact]
    public void Your_VoidManager_checked_local_mods_stay_off_in_lobbies_without_them()
    {
        HaveCopy("local-ppc", "Penitence Particle Cannon", "1.38.0", new[] { "jack.voidcrew.ppc" }, enabled: true, voidManagerType: 30);
        HaveCopy("local-uuvr", "UUVR", "0.4.0", new[] { "raicuparta.uuvr-modern" }, enabled: true, voidManagerType: 4);
        string advert = LobbyAdvert.Encode(new[] { new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Version = "0.3.0", Need = ModNeed.Everyone } });

        JoinPlan plan = _planner.Plan(LobbyReader.Read(Room(advert), "eu", _manager.Index));
        // VoidManager would turn you away for an "All" mod the host doesn't run; a VR mod only changes your own game.
        Assert.Contains(plan.TurnedOff, m => m.Id == "local-ppc");
        Assert.Contains(plan.Kept, m => m.Id == "local-uuvr");
        Assert.Empty(plan.Warnings);

        _manager.Library.Update("local-ppc", m => m.WhenJoining = JoinBehaviour.Keep);
        plan = _planner.Plan(LobbyReader.Read(Room(advert), "eu", _manager.Index));
        Assert.Contains(plan.Kept, m => m.Id == "local-ppc");
        Assert.Contains(plan.Warnings, w => w.Contains("Penitence Particle Cannon"));
    }

    [Fact]
    public void VoidManager_all_type_in_the_room_beats_the_advert()
    {
        // A host before 0.8.2 who picked "Only the host runs it" for a mod VoidManager checks on everyone.
        string advert = "vcml1;VoidCrewModdingTeam-Max_Players@1.1.2!H;NihilityShift-VoidManager@1.2.11!C";
        byte[] list = VoidManagerModList.Encode("1.2.11", new[] { new VoidManagerMod("Max Players", "VoidCrewModdingTeam.MaxPlayers", "1.1.2", 30, "") });
        Lobby lobby = LobbyReader.Read(Room(advert, list), "eu", _manager.Index);
        Assert.Equal(ModNeed.Everyone, lobby.Mods.Single(m => m.Id == "VoidCrewModdingTeam-Max_Players").Need);
        Assert.Equal(ModNeed.Everyone, lobby.Mods.Single(m => m.Id == "NihilityShift-VoidManager").Need);
        Assert.All(_planner.Plan(lobby).Lines, l => Assert.True(l.Include));
    }

    [Fact]
    public void Mods_Required_room_without_VoidManager_in_its_list_gets_VoidManager()
    {
        // VoidManager leaves itself out of its list; its version is in the list's header.
        byte[] list = VoidManagerModList.Encode("1.2.10", new[] { new VoidManagerMod("Max Players", "VoidCrewModdingTeam.MaxPlayers", "1.1.2", 30, "") });
        Lobby lobby = LobbyReader.Read(Room(voidManager: list), "eu", _manager.Index);
        LobbyMod voidManager = lobby.Mods.Single(m => ModPolicy.IsVoidManager(m.Id));
        Assert.Equal("NihilityShift-VoidManager", voidManager.Id);
        Assert.Equal(ModNeed.Everyone, voidManager.Need);
        Assert.Equal("1.2.10", voidManager.PackageVersion?.VersionNumber);

        // Without an "All" mod (and so without "[Mods Required]"), joining players don't need it.
        byte[] relaxed = VoidManagerModList.Encode("1.2.10", new[] { new VoidManagerMod("Improved Scoop", "NihilityShift.ImprovedScoop", "1.1.6", 14, "") });
        RoomDto relaxedRoom = Room(voidManager: relaxed);
        relaxedRoom.Name = "Captain's Room";
        Lobby relaxedLobby = LobbyReader.Read(relaxedRoom, "eu", _manager.Index);
        Assert.DoesNotContain(relaxedLobby.Mods, m => ModPolicy.IsVoidManager(m.Id));
    }

    [Fact]
    public void Install_resolves_dependencies_and_marks_them()
    {
        var resolved = _manager.Resolve(new[] { (_manager.Index.Find("NihilityShift-Improved_Scoop")!, _manager.Index.Find("NihilityShift-Improved_Scoop")!.Latest!) });
        Assert.Equal(new[] { "NihilityShift-VoidManager", "NihilityShift-Improved_Scoop" }, resolved.Select(r => r.Package.FullName));
        Assert.Equal("1.2.11", resolved[0].Version.VersionNumber);
    }
}
