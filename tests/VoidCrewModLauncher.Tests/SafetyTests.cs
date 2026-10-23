using System.Diagnostics;
using System.IO.Compression;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Setup;

namespace VoidCrewModLauncher.Tests;

/// <summary>The launcher never deletes or overwrites what isn't its own, and copes with read-only and locked files.</summary>
public class SafetyTests
{
    [Fact]
    public void Deleting_the_data_folder_leaves_everything_else_in_it()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        File.WriteAllText(paths.SettingsFile, "{}");
        File.WriteAllText(paths.LibraryFile, "{}");
        File.WriteAllText(paths.SettingsFile + ".broken", "{");
        string stored = temp.File("data/store/local-mod/1.0.0/Mod.dll");
        File.SetAttributes(stored, FileAttributes.ReadOnly);
        temp.File("data/cache/downloads/x.zip");
        string notes = temp.File("data/notes.txt", "mine");
        string other = temp.File("data/Other Program/file.txt", "theirs");
        var locations = new InstallLocations(Path.Combine(temp.Path, "Programs", "VoidCrewModLauncher"), Path.Combine(temp.Path, "StartMenu"), Path.Combine(temp.Path, "Desktop"), null);

        Installer.Uninstall(locations, null, paths.Root);

        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(File.Exists(paths.LibraryFile));
        Assert.False(File.Exists(paths.SettingsFile + ".broken"));
        Assert.False(Directory.Exists(paths.Store));
        Assert.False(Directory.Exists(paths.Cache));
        Assert.False(Directory.Exists(paths.Runtime));
        Assert.False(Directory.Exists(paths.Logs));
        Assert.Equal("mine", File.ReadAllText(notes));
        Assert.Equal("theirs", File.ReadAllText(other));
    }

    [Fact]
    public void A_data_folder_with_only_the_launchers_files_goes_completely()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        File.WriteAllText(paths.SettingsFile, "{}");
        temp.File("data/store/mod/1.0.0/Mod.dll");

        Assert.Empty(paths.DeleteOwnFiles());
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void Read_only_files_and_folders_are_deleted()
    {
        using var temp = new TempFolder();
        string dir = Path.Combine(temp.Path, "clone");
        string file = temp.File("clone/sub/Mod.dll");
        temp.File("clone/.git/objects/ab/cdef");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(dir, ".git", "objects", "ab", "cdef"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        new DirectoryInfo(Path.Combine(dir, "sub")).Attributes |= FileAttributes.ReadOnly;

        FileTools.DeleteDirectory(dir);

        Assert.False(Directory.Exists(dir));
        // Not there any more: no error.
        FileTools.DeleteDirectory(dir);
        FileTools.DeleteFile(file);
    }

    [Fact]
    public void Deleting_a_folder_removes_a_junction_in_it_but_not_what_it_points_to()
    {
        using var temp = new TempFolder();
        string outside = temp.File("outside/keep.txt", "keep");
        string dir = Path.Combine(temp.Path, "store");
        Directory.CreateDirectory(dir);
        string junction = Path.Combine(dir, "link");
        using (Process? mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.GetDirectoryName(outside)}\"")
               {
                   UseShellExecute = false,
                   CreateNoWindow = true,
               }))
        {
            mklink!.WaitForExit();
        }
        Assert.True(File.Exists(Path.Combine(junction, "keep.txt")));

        FileTools.DeleteDirectory(dir);

        Assert.False(Directory.Exists(dir));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void Copies_lose_the_read_only_mark_and_can_be_overwritten()
    {
        using var temp = new TempFolder();
        string source = temp.File("clone/Mod.dll", "one");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        string target = Path.Combine(temp.Path, "Mod.dll");

        FileTools.CopyFile(source, target);
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));

        File.SetAttributes(target, FileAttributes.ReadOnly);
        File.SetAttributes(source, FileAttributes.Normal);
        File.WriteAllText(source, "two");
        FileTools.CopyFile(source, target);
        Assert.Equal("two", File.ReadAllText(target));
    }

    [Fact]
    public void A_local_mod_from_a_read_only_clone_can_be_replaced_and_removed()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        string source = temp.File("clone/Mod.dll", "one");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        using var thunderstore = new Core.Thunderstore.ThunderstoreClient(paths);
        var store = new PackageStore(paths, thunderstore);

        string folder = store.AddLocal("local-mod", "1.0.0", new[] { (source, "Mod.dll") });
        store.AddLocal("local-mod", "1.0.0", new[] { (source, "Mod.dll") });
        Assert.False(File.GetAttributes(Path.Combine(folder, "Mod.dll")).HasFlag(FileAttributes.ReadOnly));

        store.RemoveAll("local-mod");
        Assert.False(Directory.Exists(folder));
        File.SetAttributes(source, FileAttributes.Normal);
    }

    [Fact]
    public void Broken_json_starts_from_defaults_and_keeps_a_copy()
    {
        using var temp = new TempFolder();
        string file = temp.File("settings.json", "{ not json");

        Assert.Null(JsonFiles.Load<LauncherSettings>(file));
        Assert.Equal("{ not json", File.ReadAllText(file + ".broken"));
        Assert.Null(JsonFiles.Load<LauncherSettings>(Path.Combine(temp.Path, "missing.json")));
    }

    [Fact]
    public void A_locked_settings_file_throws_and_is_never_replaced_with_defaults()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        var settings = new LauncherSettings { GameDirectory = @"D:\Games\Void Crew", DoorstopConsent = true, BridgeToken = "secret" };
        settings.Save(paths);
        string saved = File.ReadAllText(paths.SettingsFile);
        File.WriteAllText(paths.LibraryFile, """{"mods":[{"id":"local-mod","version":"1.0.0"}]}""");

        using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(paths.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => JsonFiles.Load<LauncherSettings>(paths.SettingsFile, 2, TimeSpan.FromMilliseconds(10)));
            Assert.Throws<IOException>(() => LauncherSettings.Load(paths));
            Assert.Throws<IOException>(() => new ModLibrary(paths));
        }

        Assert.Equal(saved, File.ReadAllText(paths.SettingsFile));
        Assert.False(File.Exists(paths.SettingsFile + ".broken"));
        LauncherSettings loaded = LauncherSettings.Load(paths);
        Assert.Equal(@"D:\Games\Void Crew", loaded.GameDirectory);
        Assert.True(loaded.DoorstopConsent);
        Assert.Equal("secret", loaded.BridgeToken);
        Assert.Single(new ModLibrary(paths).Mods);
    }

    [Fact]
    public void Unpack_refuses_zips_over_the_limits_before_writing_anything()
    {
        using var temp = new TempFolder();
        string zip = Path.Combine(temp.Path, "big.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            for (int i = 0; i < 3; i++)
            {
                using var writer = new StreamWriter(archive.CreateEntry($"file{i}.txt").Open());
                writer.Write(new string('x', 100));
            }
        }
        string target = Path.Combine(temp.Path, "out");

        Assert.Throws<InvalidDataException>(() => PackageStore.Unpack(zip, target, null, 250, 100));
        Assert.Throws<InvalidDataException>(() => PackageStore.Unpack(zip, target, null, 10_000, 2));
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + ".unpacking"));

        PackageStore.Unpack(zip, target, null, 300, 3);
        Assert.Equal(3, Directory.GetFiles(target).Length);
    }

    [Fact]
    public void An_update_never_overwrites_anything_but_the_launcher()
    {
        using var temp = new TempFolder();
        string setup = temp.File("update/VoidCrewModLauncher-0.2.0.exe", "new launcher");
        var locations = new InstallLocations(Path.Combine(temp.Path, "Programs"), Path.Combine(temp.Path, "StartMenu"), Path.Combine(temp.Path, "Desktop"), null);

        string other = temp.File("game/Void Crew.exe", "the game");
        Assert.Throws<InvalidDataException>(() => Installer.ApplyUpdate(setup, int.MaxValue, other, locations, "0.2.0"));
        Assert.Equal("the game", File.ReadAllText(other));

        // Named like the launcher, but another program's exe.
        string renamed = Path.Combine(temp.Path, "renamed", "VoidCrewModLauncher.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
        File.Copy(typeof(object).Assembly.Location, renamed);
        Assert.Throws<InvalidDataException>(() => Installer.ApplyUpdate(setup, int.MaxValue, renamed, locations, "0.2.0"));
        Assert.NotEqual("new launcher", File.ReadAllText(renamed));

        Assert.Throws<InvalidDataException>(() => Installer.CheckUpdateTarget(@"relative\VoidCrewModLauncher.exe"));
        Installer.CheckUpdateTarget(Path.Combine(temp.Path, "portable", "VoidCrewModLauncher.exe"));
    }

    [Fact]
    public void Only_the_launchers_install_folder_is_deleted_after_uninstall()
    {
        using var temp = new TempFolder();
        InstallLocations At(string dir) => new(dir, temp.Path, temp.Path, null);

        string installed = Path.Combine(temp.Path, "Programs", "VoidCrewModLauncher");
        temp.File(Path.Combine("Programs", "VoidCrewModLauncher", InstallLocations.ExeName));
        Assert.True(Installer.IsInstallDirectory(At(installed)));

        string empty = Path.Combine(temp.Path, "Empty", "VoidCrewModLauncher");
        Directory.CreateDirectory(empty);
        Assert.False(Installer.IsInstallDirectory(At(empty)));
        temp.File(Path.Combine("Other", InstallLocations.ExeName));
        Assert.False(Installer.IsInstallDirectory(At(Path.Combine(temp.Path, "Other"))));
        Assert.False(Installer.IsInstallDirectory(At(@"C:\")));
        Assert.False(Installer.IsInstallDirectory(At("")));
        Assert.False(Installer.IsInstallDirectory(At(installed + "\" & rmdir C:\\x & \"")));
        Assert.False(Installer.IsInstallDirectory(At(@"relative\VoidCrewModLauncher")));
    }

    [Fact]
    public void Removing_the_loader_keeps_a_doorstop_version_the_launcher_did_not_add()
    {
        using var temp = new TempFolder();
        temp.File("pack/BepInExPack/winhttp.dll", "proxy");
        temp.File("pack/BepInExPack/.doorstop_version", "4.5.0");
        string pack = Path.Combine(temp.Path, "pack");

        // Added by the launcher: all three go.
        string fresh = Path.Combine(temp.Path, "fresh");
        Directory.CreateDirectory(fresh);
        Doorstop.InstallIfMissing(fresh, pack);
        Assert.True(Doorstop.Remove(fresh));
        Assert.Empty(Directory.GetFileSystemEntries(fresh));

        // Someone else's version file stays.
        string versioned = Path.Combine(temp.Path, "versioned");
        temp.File("versioned/.doorstop_version", "3.4.0");
        Doorstop.InstallIfMissing(versioned, pack);
        Assert.True(Doorstop.Remove(versioned, new[] { pack }));
        Assert.False(File.Exists(Path.Combine(versioned, Doorstop.ProxyFile)));
        Assert.False(File.Exists(Path.Combine(versioned, Doorstop.ConfigFile)));
        Assert.Equal("3.4.0", File.ReadAllText(Path.Combine(versioned, Doorstop.VersionFile)));

        // Another install's loader isn't removed.
        string other = Path.Combine(temp.Path, "other");
        temp.File("other/winhttp.dll", "rai pal");
        temp.File("other/doorstop_config.ini", "[General]\nenabled=true\n");
        Assert.False(Doorstop.Remove(other));
        Assert.True(File.Exists(Path.Combine(other, Doorstop.ProxyFile)));
    }

    [Fact]
    public void The_launchers_own_config_left_without_its_dll_is_replaced()
    {
        using var temp = new TempFolder();
        temp.File("pack/BepInExPack/winhttp.dll", "proxy");
        string game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(game);
        Doorstop.InstallIfMissing(game, Path.Combine(temp.Path, "pack"));
        File.Delete(Path.Combine(game, Doorstop.ProxyFile));

        Doorstop.InstallIfMissing(game, Path.Combine(temp.Path, "pack"));

        Assert.True(Doorstop.Inspect(game).InstalledByLauncher);
    }
}
