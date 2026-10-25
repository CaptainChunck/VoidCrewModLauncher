using VoidCrewModLauncher.Core;

namespace VoidCrewModLauncher.Tests;

/// <summary>
/// The links the launcher opens in the browser (WEBSITE buttons, README links, a mod's page): web pages only, so a mod's
/// website field or README can never make the launcher run a file.
/// </summary>
public class WebLinkTests
{
    [Theory]
    [InlineData("https://thunderstore.io/c/void-crew/p/Captain_Chuncks_Mods/Void_Crew_Fleet/")]
    [InlineData("http://example.com")]
    [InlineData("  https://github.com/CaptainChunck/VoidCrewModLauncher  ")]
    public void Web_pages_are_opened(string text)
    {
        Assert.True(WebLink.IsWebPage(text, out Uri? uri));
        Assert.Equal(text.Trim().TrimEnd('/'), uri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\server\share\setup.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ms-settings:privacy")]
    [InlineData("steam://run/1063420")]
    [InlineData("voidcrewmod://install/A-B")]
    [InlineData("README.md")]
    [InlineData("https://")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_not(string? text)
    {
        Assert.False(WebLink.IsWebPage(text, out Uri? uri));
        Assert.Null(uri);
    }
}
