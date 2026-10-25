using VoidCrewModLauncher.Core.Game;

namespace VoidCrewModLauncher.Tests;

public class OldLoaderTests
{
    [Fact]
    public void An_old_Doorstop_from_a_manual_install_is_replaced_and_kept_aside()
    {
        using var temp = new TempFolder();
        temp.File("pack/BepInExPack/winhttp.dll", "new proxy");
        temp.File("pack/BepInExPack/.doorstop_version", "4.5.0");
        temp.File("game/winhttp.dll", "old proxy");
        temp.File("game/.doorstop_version", "3.4.0");
        temp.File("game/doorstop_config.ini", "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx/core/BepInEx.Preloader.dll\n");
        string game = Path.Combine(temp.Path, "game");
        Assert.True(Doorstop.Inspect(game).Outdated);

        Doorstop.ReplaceOutdated(game, Path.Combine(temp.Path, "pack"));

        DoorstopState now = Doorstop.Inspect(game);
        Assert.True(now.InstalledByLauncher);
        Assert.False(now.Outdated);
        Assert.Equal("new proxy", File.ReadAllText(Path.Combine(game, Doorstop.ProxyFile)));
        Assert.Equal("old proxy", File.ReadAllText(Path.Combine(game, Doorstop.ProxyFile + Doorstop.OldLoaderSuffix)));
        Assert.True(File.Exists(Path.Combine(game, Doorstop.ConfigFile + Doorstop.OldLoaderSuffix)));

        // Removing the launcher's loader puts the old one back, so the manual install works again.
        Assert.True(Doorstop.Remove(game, new[] { Path.Combine(temp.Path, "pack") }));
        Assert.Equal("old proxy", File.ReadAllText(Path.Combine(game, Doorstop.ProxyFile)));
        Assert.Equal("3.4.0", File.ReadAllText(Path.Combine(game, Doorstop.VersionFile)));
        Assert.Contains("[UnityDoorstop]", File.ReadAllText(Path.Combine(game, Doorstop.ConfigFile)));
        Assert.Empty(Directory.GetFiles(game, "*" + Doorstop.OldLoaderSuffix));
    }

    [Fact]
    public void A_current_Doorstop_from_another_install_is_left_alone()
    {
        using var temp = new TempFolder();
        temp.File("pack/BepInExPack/winhttp.dll", "new proxy");
        temp.File("game/winhttp.dll", "rai pal proxy");
        temp.File("game/.doorstop_version", "4.5.0");
        temp.File("game/doorstop_config.ini", "[General]\nenabled=true\n");
        string game = Path.Combine(temp.Path, "game");
        Assert.False(Doorstop.Inspect(game).Outdated);

        Doorstop.ReplaceOutdated(game, Path.Combine(temp.Path, "pack"));

        Assert.Equal("rai pal proxy", File.ReadAllText(Path.Combine(game, Doorstop.ProxyFile)));
    }
}
