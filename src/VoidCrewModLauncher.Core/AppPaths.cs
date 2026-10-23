namespace VoidCrewModLauncher.Core;

/// <summary>
/// Where the launcher keeps its files. Everything lives in one folder (by default %APPDATA%\VoidCrewModLauncher):
/// <code>
/// settings.json, library.json   the launcher's settings and the installed mods
/// store\&lt;mod id&gt;\&lt;version&gt;\      every downloaded mod version, unpacked as Thunderstore ships it
/// cache\                         Thunderstore's package list, icons, downloads in progress
/// runtime\BepInEx\               the BepInEx folder the game loads; rebuilt for each launch from the store
/// runtime\launch.json            what this launch is for, read by the bridge plugin
/// logs\launcher.log
/// </code>
/// The folder can be moved with the VCML_DATA_DIR environment variable or the --data-dir argument.
/// </summary>
public sealed class AppPaths
{
    public const string DataDirVariable = "VCML_DATA_DIR";

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string Store => Path.Combine(Root, "store");
    public string Cache => Path.Combine(Root, "cache");
    public string Icons => Path.Combine(Cache, "icons");
    public string Downloads => Path.Combine(Cache, "downloads");
    public string PackageListCache => Path.Combine(Cache, $"thunderstore-{AppInfo.ThunderstoreCommunity}.json");
    public string Runtime => Path.Combine(Root, "runtime");
    public string RuntimeBepInEx => Path.Combine(Runtime, "BepInEx");
    public string RuntimeLaunchFile => Path.Combine(Runtime, Protocol.BridgeDefaults.LaunchFileName);
    public string Preloader => Path.Combine(RuntimeBepInEx, "core", "BepInEx.Preloader.dll");
    public string GameLog => Path.Combine(RuntimeBepInEx, "LogOutput.log");
    public string Logs => Path.Combine(Root, "logs");
    public string LauncherLog => Path.Combine(Logs, "launcher.log");

    public static AppPaths FromEnvironment(string? commandLineDataDir = null)
    {
        string? root = commandLineDataDir;
        if (string.IsNullOrWhiteSpace(root)) root = Environment.GetEnvironmentVariable(DataDirVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoidCrewModLauncher");
        }
        return new AppPaths(root);
    }

    public void EnsureCreated()
    {
        foreach (string dir in new[] { Root, Store, Cache, Icons, Downloads, Runtime, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// Deletes the launcher's own files and folders in <see cref="Root"/> (uninstall's "delete mods and settings"), then
    /// Root itself if nothing else is left in it. Root can be any folder (--data-dir), so nothing else there is touched.
    /// Returns what could not be deleted.
    /// </summary>
    public List<string> DeleteOwnFiles()
    {
        var failed = new List<string>();
        if (!Directory.Exists(Root)) return failed;
        foreach (string file in new[] { SettingsFile, LibraryFile })
        {
            foreach (string path in new[] { file, file + ".tmp", file + ".broken" })
            {
                if (!FileTools.TryDeleteFile(path)) failed.Add(path);
            }
        }
        foreach (string dir in new[] { Store, Cache, Runtime, Logs })
        {
            if (!FileTools.TryDeleteDirectory(dir)) failed.Add(dir);
        }
        try
        {
            if (Directory.GetFileSystemEntries(Root).Length == 0) Directory.Delete(Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed.Add(Root);
        }
        return failed;
    }
}

/// <summary>
/// Copies and deletes that cope with read-only files (a local mod copied from a git clone keeps its read-only marks)
/// and never follow a junction or symbolic link out of the folder being deleted.
/// </summary>
public static class FileTools
{
    /// <summary>Copies a file, overwriting, and leaves the copy without read-only, hidden or system marks.</summary>
    public static void CopyFile(string source, string destination)
    {
        ClearReadOnly(destination);
        File.Copy(source, destination, true);
        FileAttributes attributes = File.GetAttributes(destination);
        FileAttributes cleared = attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        if (cleared != attributes) File.SetAttributes(destination, cleared == 0 ? FileAttributes.Normal : cleared);
    }

    /// <summary>Deletes a file, read-only or not; no error when it isn't there.</summary>
    public static void DeleteFile(string path)
    {
        if (!ClearReadOnly(path)) return;
        File.Delete(path);
    }

    /// <summary>
    /// Deletes a folder and everything in it, clearing read-only marks first. A junction or symbolic link inside it (or
    /// the folder itself being one) is removed as a link: what it points to is never entered.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return;
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete();
            return;
        }
        foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // The link itself; Delete() on a link never touches its target.
                if (entry is DirectoryInfo link) link.Delete();
                else entry.Delete();
            }
            else if (entry is DirectoryInfo sub)
            {
                DeleteDirectory(sub.FullName);
            }
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        if (dir.Attributes.HasFlag(FileAttributes.ReadOnly)) dir.Attributes &= ~FileAttributes.ReadOnly;
        dir.Delete();
    }

    /// <summary><see cref="DeleteFile"/>, logging a failure instead of throwing. False when the file is still there.</summary>
    public static bool TryDeleteFile(string path)
    {
        try
        {
            DeleteFile(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not delete {path}: {e.Message}");
            return false;
        }
    }

    /// <summary><see cref="DeleteDirectory"/>, logging a failure instead of throwing. False when something is left.</summary>
    public static bool TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectory(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not delete {path}: {e.Message}");
            return false;
        }
    }

    /// <summary>Clears a file's read-only mark (not a link's: that would change its target). False when there is no file there.</summary>
    private static bool ClearReadOnly(string path)
    {
        FileAttributes attributes;
        try
        {
            // Reads the entry itself, so a link (even a broken one) is seen as a link.
            attributes = File.GetAttributes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        if (attributes.HasFlag(FileAttributes.Directory)) return false;
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        return true;
    }
}
