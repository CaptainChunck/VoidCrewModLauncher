using VoidCrewModLauncher.Core.Game;

namespace VoidCrewModLauncher.Tests;

public class LaunchPathTests
{
    private const string Spaced = @"C:\Users\Jo Smith\AppData\Roaming\VoidCrewModLauncher\runtime\BepInEx\core\BepInEx.Preloader.dll";

    [Fact]
    public void A_path_without_spaces_goes_through_Steam_as_it_is()
    {
        const string plain = @"C:\Users\ashle\AppData\Roaming\VoidCrewModLauncher\runtime\BepInEx\core\BepInEx.Preloader.dll";
        Assert.Equal((plain, true), GameProcess.PathForSteam(plain, true, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void A_path_with_a_space_goes_through_Steam_in_its_short_form()
    {
        const string shortForm = @"C:\Users\JOSMIT~1\AppData\Roaming\VOIDCR~1\runtime\BepInEx\core\BEPINE~1.DLL";
        Assert.Equal((shortForm, true), GameProcess.PathForSteam(Spaced, true, _ => shortForm));
    }

    [Fact]
    public void Without_a_short_form_the_game_is_started_directly_with_the_full_path()
    {
        Assert.Equal((Spaced, false), GameProcess.PathForSteam(Spaced, true, _ => null));
        Assert.Equal((Spaced, false), GameProcess.PathForSteam(Spaced, true, path => path));
    }

    [Fact]
    public void A_direct_start_keeps_the_path_as_it_is()
    {
        Assert.Equal((Spaced, false), GameProcess.PathForSteam(Spaced, false, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void Windows_gives_a_real_folder_with_a_space_a_short_form_without_one()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "Jo Smith");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "BepInEx.Preloader.dll");
        File.WriteAllText(file, "");
        string? shortForm = GameProcess.ShortPath(file);
        // Some drives have short names turned off; the launcher then starts the game directly instead.
        if (shortForm == null || shortForm.Contains(' ')) return;
        Assert.True(File.Exists(shortForm));
        Assert.Equal(Path.GetFullPath(file), new FileInfo(shortForm).FullName, ignoreCase: true);
    }
}
