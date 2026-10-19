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
        Assert.Equal(LobbyModStatus.NotNeeded, plan.Lines.Single(l => l.Mod.Id == "NihilityShift-VoidManager").Status);

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
        LobbyMod mod = Assert.Single(lobby.Mods);
        Assert.Equal(ModNeed.Everyone, mod.Need);
        Assert.Equal("pilot.hud", mod.Guid);
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

    [Fact]
    public void Running_game_fits_when_it_has_the_same_mods()
    {
        var join = new ResolvedJoin();
        join.Mods.Add(new RuntimeMod("A-Mod", "Mod", "1.0.0", "x", false, ModNeed.Everyone));
        LaunchFile Running(params (string Id, string Version, ModNeed Need)[] mods) => new()
        {
            Mods = mods.Select(m => new LaunchMod { Id = m.Id, Version = m.Version, Need = LobbyAdvert.NeedLetter(m.Need) }).ToList(),
        };

        Assert.True(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone))));
        Assert.True(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone), ("B-Hud", "2.0.0", ModNeed.ClientOnly))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.1", ModNeed.Everyone))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running(("A-Mod", "1.0.0", ModNeed.Everyone), ("C-Gameplay", "1.0.0", ModNeed.Everyone))));
        Assert.False(JoinPlanner.RunningGameFits(join, Running()));
        Assert.False(JoinPlanner.RunningGameFits(join, null));
    }

    [Fact]
    public void Install_resolves_dependencies_and_marks_them()
    {
        var resolved = _manager.Resolve(new[] { (_manager.Index.Find("NihilityShift-Improved_Scoop")!, _manager.Index.Find("NihilityShift-Improved_Scoop")!.Latest!) });
        Assert.Equal(new[] { "NihilityShift-VoidManager", "NihilityShift-Improved_Scoop" }, resolved.Select(r => r.Package.FullName));
        Assert.Equal("1.2.11", resolved[0].Version.VersionNumber);
    }
}
