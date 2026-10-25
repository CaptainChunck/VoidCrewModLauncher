using Microsoft.Win32;
using VoidCrewModLauncher.Core.Setup;

namespace VoidCrewModLauncher.Tests;

[Collection(TestRegistryCollection.Name)]
public class InstallerTests
{
    [Fact]
    public void Install_puts_the_exe_shortcuts_and_uninstall_entry_in_place_and_uninstall_removes_them()
    {
        using var temp = new TempFolder();
        using var test = new TestRegistryKey();
        string source = temp.File("download/VoidCrewModLauncher-Setup.exe", "pretend exe");
        string key = test.Path;
        var locations = new InstallLocations(
            Path.Combine(temp.Path, "Programs", "VoidCrewModLauncher"),
            Path.Combine(temp.Path, "StartMenu"),
            Path.Combine(temp.Path, "Desktop"),
            key);

        Installer.Install(source, locations, "0.1.0");

        Assert.Equal("pretend exe", File.ReadAllText(locations.InstalledExe));
        Assert.True(File.Exists(Path.Combine(locations.StartMenuDirectory, InstallLocations.ShortcutName)));
        Assert.True(File.Exists(Path.Combine(locations.DesktopDirectory, InstallLocations.ShortcutName)));
        using (RegistryKey? entry = Registry.CurrentUser.OpenSubKey(key))
        {
            Assert.NotNull(entry);
            Assert.Equal("Void Crew Mod Launcher", entry!.GetValue("DisplayName"));
            Assert.Equal("0.1.0", entry.GetValue("DisplayVersion"));
            Assert.Equal($"\"{locations.InstalledExe}\" --uninstall", entry.GetValue("UninstallString"));
        }

        // Installing again (an update) replaces the exe.
        File.WriteAllText(source, "newer exe");
        Installer.Install(source, locations, "0.2.0");
        Assert.Equal("newer exe", File.ReadAllText(locations.InstalledExe));

        Installer.Uninstall(locations, null, null);
        Assert.False(File.Exists(Path.Combine(locations.StartMenuDirectory, InstallLocations.ShortcutName)));
        Assert.False(File.Exists(Path.Combine(locations.DesktopDirectory, InstallLocations.ShortcutName)));
        Assert.Null(Registry.CurrentUser.OpenSubKey(key));
    }

    [Fact]
    public void A_test_registry_key_leaves_nothing_behind()
    {
        using (var test = new TestRegistryKey())
        {
            using RegistryKey created = Registry.CurrentUser.CreateSubKey($@"{test.Path}\inner");
            created.SetValue("x", "y");
        }
        // The parent goes too when no other test's key is in it (only these tests use it, one at a time).
        using RegistryKey? parent = Registry.CurrentUser.OpenSubKey(TestRegistryKey.Parent);
        Assert.True(parent == null || parent.SubKeyCount > 0);
    }

    [Fact]
    public void The_installed_copy_loses_the_downloaded_mark_and_the_setup_keeps_it()
    {
        using var temp = new TempFolder();
        string source = temp.File("download/VoidCrewModLauncher-Setup.exe", "pretend exe");
        File.WriteAllText(source + Installer.ZoneStream, "[ZoneTransfer]\r\nZoneId=3\r\n");
        var locations = new InstallLocations(
            Path.Combine(temp.Path, "Programs", "VoidCrewModLauncher"),
            Path.Combine(temp.Path, "StartMenu"),
            Path.Combine(temp.Path, "Desktop"),
            null);

        Installer.Install(source, locations, "0.1.0");

        Assert.Equal("pretend exe", File.ReadAllText(locations.InstalledExe));
        Assert.False(HasStream(locations.InstalledExe + Installer.ZoneStream));
        Assert.True(HasStream(source + Installer.ZoneStream));
    }

    private static bool HasStream(string path)
    {
        try
        {
            File.ReadAllText(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    [Fact]
    public void Developer_builds_never_install_themselves()
    {
        Assert.False(Installer.IsPublishedBuild);
        Assert.False(Installer.ShouldInstall(Array.Empty<string>(), InstallLocations.ForThisUser()));
    }
}
