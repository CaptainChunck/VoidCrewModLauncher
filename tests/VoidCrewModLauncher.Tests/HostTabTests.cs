using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

/// <summary>
/// The HOST tab's rows show what the lobby lists for each mod ("Players can't join without it", the orange marker) by
/// asking the same HostNeeds the launch uses, built once per refresh: it has to follow the choices made in the rows after.
/// </summary>
public class HostTabTests
{
    [Fact]
    public void The_HOST_tab_shows_a_choice_made_after_its_rows_were_drawn()
    {
        using var temp = new TempFolder();
        using var service = new LauncherService(temp.AppPaths(), new FakeHttp());
        // A client-side HUD (VoidManager type 8) and a mod VoidManager checks on joining players (type 30).
        service.Library.AddOrReplace(new InstalledMod { Id = "local-hud", Source = ModSource.Local, Name = "My HUD", Version = "1.0.0", VoidManagerType = 8, Enabled = true });
        service.Library.AddOrReplace(new InstalledMod { Id = "local-ppc", Source = ModSource.Local, Name = "Particle Cannon", Version = "1.0.0", VoidManagerType = 30, Enabled = true });
        InstalledMod hud = service.Library.Find("local-hud")!;
        InstalledMod cannon = service.Library.Find("local-ppc")!;

        HostNeeds needs = service.CurrentHostNeeds();
        Assert.Equal(ModNeed.ClientOnly, needs.Effective(hud));
        Assert.False(needs.Overridden(cannon));

        // As HostViewModel.SetNeed saves a choice.
        service.Settings.Host.ModNeeds["local-hud"] = ModNeed.Everyone;
        service.Settings.Host.ModNeeds["local-ppc"] = ModNeed.HostOnly;
        Assert.Equal(ModNeed.Everyone, needs.Effective(hud));
        Assert.Equal(ModNeed.Everyone, needs.Effective(cannon));
        Assert.True(needs.Overridden(cannon));

        // Back to Auto.
        service.Settings.Host.ModNeeds.Remove("local-hud");
        service.Settings.Host.ModNeeds.Remove("local-ppc");
        Assert.Equal(ModNeed.ClientOnly, needs.Effective(hud));
        Assert.False(needs.Overridden(cannon));
    }
}
