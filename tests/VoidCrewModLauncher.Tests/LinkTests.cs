using Microsoft.Win32;
using VoidCrewModLauncher.Core.Setup;

namespace VoidCrewModLauncher.Tests;

[Collection(TestRegistryCollection.Name)]
public class LinkTests
{
    [Fact]
    public void Website_links_are_read_strictly()
    {
        Assert.Equal(new LauncherLink(LinkAction.Install, "Captain_Chuncks_Mods-Void_Crew_Fleet"), LauncherLinks.Parse("voidcrewmod://install/Captain_Chuncks_Mods-Void_Crew_Fleet"));
        Assert.Equal(new LauncherLink(LinkAction.Install, "Captain_Chuncks_Mods-Void_Crew_Fleet", "0.4.0"), LauncherLinks.Parse("voidcrewmod://install/Captain_Chuncks_Mods-Void_Crew_Fleet/0.4.0/"));
        Assert.Equal(new LauncherLink(LinkAction.Install, "github-Raicuparta-UUVR"), LauncherLinks.Parse("VoidCrewMod:install/github-Raicuparta-UUVR?from=site"));
        Assert.Equal(new LauncherLink(LinkAction.Open), LauncherLinks.Parse("voidcrewmod://open"));
        Assert.Equal(new LauncherLink(LinkAction.Open), LauncherLinks.Parse("voidcrewmod://"));
        Assert.Null(LauncherLinks.Parse("voidcrewmod://install/../../evil"));
        Assert.Null(LauncherLinks.Parse("voidcrewmod://install/Name\"--uninstall"));
        Assert.Null(LauncherLinks.Parse("voidcrewmod://install/Ns-Name/latest"));
        Assert.Null(LauncherLinks.Parse("voidcrewmod://uninstall/Ns-Name"));
        Assert.Null(LauncherLinks.Parse("https://voidcrewmod.com/install/Ns-Name"));
        Assert.Equal("voidcrewmod://open", LauncherLinks.FindIn(new[] { "--data-dir", "x", "voidcrewmod://open" }));
    }

    [Fact]
    public void Registration_points_links_at_the_launcher_and_uninstall_removes_it()
    {
        using var test = new TestRegistryKey();
        string classes = test.Path;
        LauncherLinks.Register(@"C:\Launcher\VoidCrewModLauncher.exe", classes);
        Assert.True(LauncherLinks.IsRegistered(@"C:\Launcher\VoidCrewModLauncher.exe", classes));
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"{classes}\voidcrewmod")!)
        {
            Assert.Equal("", key.GetValue("URL Protocol"));
        }
        LauncherLinks.Unregister(classes);
        Assert.False(LauncherLinks.IsRegistered(@"C:\Launcher\VoidCrewModLauncher.exe", classes));
    }

    [Fact]
    public async Task A_second_start_hands_its_link_to_the_open_launcher()
    {
        string name = "VoidCrewModLauncher-links-test-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string>();
        using var pipe = new LinkPipe(name);
        pipe.Start(link => received.TrySetResult(link));
        Assert.True(await LinkPipe.SendAsync("voidcrewmod://install/Ns-Name", name));
        Assert.Equal("voidcrewmod://install/Ns-Name", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await LinkPipe.SendAsync("voidcrewmod://open", name + "-nobody", 300));
    }
}
