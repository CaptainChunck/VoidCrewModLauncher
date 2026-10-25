using VoidCrewModLauncher.Core.Mods;

namespace VoidCrewModLauncher.Tests;

/// <summary>How far the HOST tab's crew size goes: the game's 6, or what a mod that makes lobbies bigger allows.</summary>
public class HostLimitTests
{
    [Fact]
    public void The_crew_size_goes_as_high_as_the_mods_that_are_on_allow()
    {
        Assert.Equal(6, ModPolicy.RoomLimit(Array.Empty<string>()));
        Assert.Equal(6, ModPolicy.RoomLimit(new[] { "Captain_Chuncks_Mods-Ship_Customizer", "NihilityShift-VoidManager" }));

        // Void Crew Fleet: six ships of eight (its own setting, 24 unless raised, caps the lobby in the game).
        Assert.Equal(48, ModPolicy.RoomLimit(new[] { "NihilityShift-VoidManager", "captain_chuncks_mods-void_crew_fleet" }));

        // Max_Players' slider on the game's host panel goes to 24 unless its own setting is raised; both of its packages.
        Assert.Equal(24, ModPolicy.RoomLimit(new[] { "NihilityShift-Max_Players" }));
        Assert.Equal(24, ModPolicy.RoomLimit(new[] { "VoidCrewModdingTeam-Max_Players" }));
        Assert.Equal(48, ModPolicy.RoomLimit(new[] { "NihilityShift-Max_Players", "Captain_Chuncks_Mods-Void_Crew_Fleet" }));
    }
}
