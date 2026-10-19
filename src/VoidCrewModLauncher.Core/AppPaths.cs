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
}
