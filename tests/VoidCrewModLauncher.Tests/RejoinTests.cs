using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

/// <summary>
/// FIX AND REJOIN: what the launcher changes after a lobby turned the game away for its mods, from the names the game
/// gave (RejoinPlan; the server browser shows it and joins with the result).
/// </summary>
public sealed class RejoinTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ThunderstoreClient _thunderstore;
    private readonly ModManager _manager;

    public RejoinTests()
    {
        AppPaths paths = _temp.AppPaths();
        _thunderstore = new ThunderstoreClient(paths);
        _manager = new ModManager(new ModLibrary(paths), new PackageStore(paths, _thunderstore)) { Index = Packages.Index() };
    }

    public void Dispose()
    {
        _thunderstore.Dispose();
        _temp.Dispose();
    }

    /// <summary>A lobby with Fleet for everyone, Improved Scoop only on the host and the VR UI Fix on the host's own game.</summary>
    private Lobby Lobby()
    {
        string advert = LobbyAdvert.Encode(new[]
        {
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Version = "0.3.0", Need = ModNeed.Everyone },
            new AdvertMod { Id = "NihilityShift-Improved_Scoop", Version = "1.1.6", Need = ModNeed.HostOnly },
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", Version = "3.14.1", Need = ModNeed.ClientOnly },
        });
        return LobbyReader.Read(new RoomDto { Id = "ROOM1", Name = "Fleet night", Players = 2, MaxPlayers = 8, Modding = "session", Advert = advert }, "eu", _manager.Index);
    }

    /// <summary>What the game was started with for the join.</summary>
    private static ResolvedJoin Join(params (string Id, string Name)[] mods)
    {
        var join = new ResolvedJoin();
        foreach ((string id, string name) in mods) join.Mods.Add(new RuntimeMod(id, name, "1.0.0", "folder-" + id, id.StartsWith("local-", StringComparison.Ordinal), ModNeed.Everyone));
        return join;
    }

    private static ModNameDto Named(string? name, string? displayName = null) => new() { Name = name!, DisplayName = displayName! };

    [Fact]
    public void Mods_the_lobby_doesnt_allow_are_matched_by_their_whole_name()
    {
        ResolvedJoin join = Join(("Captain_Chuncks_Mods-Void_Crew_Fleet", "Void_Crew_Fleet"), ("VoidCrewModdingTeam-Pilot_HUD", "Pilot_HUD"), ("local-ppc", "Penitence Particle Cannon"));

        // No usable name, or a part of one: nothing is left out (an empty name used to match every mod, "HUD" every mod
        // whose name ends in HUD), so nothing restarts the game for them.
        RejoinPlan none = RejoinPlan.For(Lobby(), join, null, new[] { Named(""), Named(null), Named("  "), Named("--"), Named("x1"), Named("HUD"), Named("Fleet") }, _manager.Index);
        Assert.Empty(none.Disallowed);
        Assert.False(none.CanFix);

        // The whole name, as the game gives it (with a version or a note after it), or the mod's id.
        RejoinPlan named = RejoinPlan.For(Lobby(), join, null,
            new[] { Named("Pilot HUD 0.0.4"), Named("x", "Penitence Particle Cannon (beta)"), Named("Captain_Chuncks_Mods-Void_Crew_Fleet") }, _manager.Index);
        Assert.Equal(new[] { "Captain_Chuncks_Mods-Void_Crew_Fleet", "VoidCrewModdingTeam-Pilot_HUD", "local-ppc" }.Order(), named.Disallowed.Select(m => m.Id).Order());
        Assert.True(named.CanFix);

        // "Void Crew" in front of the name counts as the same name.
        RejoinPlan withGame = RejoinPlan.For(Lobby(), Join(("A-Ship_Customizer", "Ship_Customizer")), null, new[] { Named("Void Crew Ship Customizer") }, _manager.Index);
        Assert.Equal("A-Ship_Customizer", Assert.Single(withGame.Disallowed).Id);
    }

    [Fact]
    public void A_missing_mod_the_join_already_loads_is_listed_as_loaded_and_changes_nothing()
    {
        ResolvedJoin join = Join(("Captain_Chuncks_Mods-Void_Crew_Fleet", "Void_Crew_Fleet"));
        RejoinPlan fix = RejoinPlan.For(Lobby(), join, new[] { Named("Void Crew Fleet 0.3.1") }, null, _manager.Index);

        (string name, TsPackage package) = Assert.Single(fix.AlreadyLoaded);
        Assert.Equal("Void Crew Fleet 0.3.1", name);
        Assert.Equal("Captain_Chuncks_Mods-Void_Crew_Fleet", package.FullName);
        // Not "not found on Thunderstore", and so not a reason to load the host's other mods.
        Assert.Empty(fix.NotFound);
        Assert.Empty(fix.HostsOtherMods);
        Assert.Empty(fix.Found);
        Assert.False(fix.CanFix);
    }

    [Fact]
    public void Something_the_launcher_cant_find_loads_the_hosts_other_mods()
    {
        ResolvedJoin join = Join(("Captain_Chuncks_Mods-Void_Crew_Fleet", "Void_Crew_Fleet"));
        // A mission or an item the game requires, named by itself rather than by its mod.
        RejoinPlan fix = RejoinPlan.For(Lobby(), join, new[] { Named("8f3c2a", "Warzone Mission") }, null, _manager.Index);

        Assert.Equal(new[] { "Warzone Mission" }, fix.NotFound);
        // The host-only mod the join left out, not the host's own client-side one.
        Assert.Equal("NihilityShift-Improved_Scoop", Assert.Single(fix.HostsOtherMods).Package!.FullName);
        Assert.True(fix.CanFix);

        ResolvedJoin fixedJoin = fix.FixedJoin(join, _manager);
        Assert.Contains(fixedJoin.Mods, m => m.Id == "Captain_Chuncks_Mods-Void_Crew_Fleet");
        // At the host's version, with what it needs; downloaded first.
        Assert.Contains(fixedJoin.Mods, m => m.Id == "NihilityShift-Improved_Scoop" && m.Version == "1.1.6" && m.Need == ModNeed.Everyone);
        Assert.Contains(fixedJoin.Mods, m => m.Id == "NihilityShift-VoidManager");
        Assert.Equal(new[] { "NihilityShift-Improved_Scoop", "NihilityShift-VoidManager" }.Order(), fixedJoin.Downloads.Select(d => d.Package.FullName).Order());
        Assert.DoesNotContain(fixedJoin.Mods, m => m.Id == "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix");

        // A name the game gives without anything to show is still something missing.
        Assert.Equal(new[] { "?" }, RejoinPlan.For(Lobby(), join, new[] { Named("", "") }, null, _manager.Index).NotFound);
    }

    [Fact]
    public void Missing_mods_found_on_Thunderstore_are_added_and_disallowed_ones_left_out()
    {
        ResolvedJoin join = Join(("Captain_Chuncks_Mods-Void_Crew_Fleet", "Void_Crew_Fleet"), ("VoidCrewModdingTeam-Pilot_HUD", "Pilot_HUD"));
        RejoinPlan fix = RejoinPlan.For(Lobby(), join, new[] { Named("Ship_Customizer", "Ship Customizer") }, new[] { Named("Pilot HUD") }, _manager.Index);

        (string name, TsPackage package) = Assert.Single(fix.Found);
        Assert.Equal("Ship Customizer", name);
        Assert.Equal("Captain_Chuncks_Mods-Ship_Customizer", package.FullName);
        Assert.Empty(fix.HostsOtherMods);

        ResolvedJoin fixedJoin = fix.FixedJoin(join, _manager);
        Assert.Equal(new[] { "Captain_Chuncks_Mods-Ship_Customizer", "Captain_Chuncks_Mods-Void_Crew_Fleet" }.Order(), fixedJoin.Mods.Select(m => m.Id).Order());
        Assert.Equal("0.7.0", fixedJoin.Mods.Single(m => m.Id == "Captain_Chuncks_Mods-Ship_Customizer").Version);
        Assert.Equal("Captain_Chuncks_Mods-Ship_Customizer", Assert.Single(fixedJoin.Downloads).Package.FullName);
    }

    [Theory]
    [InlineData("Ship Customizer 0.9.4 (beta)", "Ship Customizer")]
    [InlineData("Mod v2", "Mod v2")]
    [InlineData("1.2.3", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Versions_and_notes_are_taken_off_the_games_names(string? name, string expected)
    {
        Assert.Equal(expected, RejoinPlan.StripVersion(name));
    }
}
