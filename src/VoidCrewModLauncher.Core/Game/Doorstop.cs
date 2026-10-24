using System.Diagnostics;
using System.Text;

namespace VoidCrewModLauncher.Core.Game;

/// <summary>What the game folder has of BepInEx's loader (Unity Doorstop: winhttp.dll and doorstop_config.ini).</summary>
public sealed record DoorstopState(bool HasLoader, string? Version, bool? EnabledInConfig, string? ConfigTarget, bool InstalledByLauncher)
{
    /// <summary>Who put the loader there, for the Settings tab (in the launcher's language).</summary>
    public string Describe() => Describe(true);

    /// <summary>Who put the loader there; in English (<paramref name="translate"/> false) for the launcher log.</summary>
    public string Describe(bool translate)
    {
        string version = Version ?? "?";
        if (!HasLoader) return translate ? Loc.T("Not installed yet.") : "Not installed yet.";
        if (InstalledByLauncher) return translate ? Loc.F("Installed by the launcher (Doorstop {0}).", version) : $"Installed by the launcher (Doorstop {version}).";
        return translate ? Loc.F("From another mod manager (Doorstop {0}). Left as it is.", version) : $"From another mod manager (Doorstop {version}). Left as it is.";
    }

    /// <summary>
    /// Another install's Doorstop older than 4: it doesn't load the launcher's BepInEx (it keeps to its own config), so
    /// the game starts without the launcher's mods until it is replaced.
    /// </summary>
    public bool Outdated => HasLoader && !InstalledByLauncher && System.Version.TryParse(Version, out System.Version? parsed) && parsed.Major < 4;
}

/// <summary>
/// BepInEx is loaded by Unity Doorstop, a proxy winhttp.dll next to the game's exe. The launcher keeps BepInEx in its
/// own folder and tells Doorstop where it is on the command line, as r2modman does, so the game folder only needs the
/// proxy itself. If the folder has none, the launcher adds one with its config turned off: starting the game from Steam
/// stays unmodded. An existing loader (Rai Pal, r2modman, a manual install) is never touched.
/// </summary>
public static class Doorstop
{
    public const string ProxyFile = "winhttp.dll";
    public const string ConfigFile = "doorstop_config.ini";
    public const string VersionFile = ".doorstop_version";
    private const string LauncherMarker = "# Installed by the Void Crew Mod Launcher";

    /// <summary>In the launcher's config: the .doorstop_version it added (one that was already there isn't removed with it).</summary>
    private const string AddedVersionMarker = "# Added .doorstop_version: ";

    /// <summary>The other names Doorstop's proxy can have.</summary>
    private static readonly string[] OtherProxyFiles = ["version.dll", "winmm.dll"];

    public static DoorstopState Inspect(string gameDirectory)
    {
        string? proxy = FindProxy(gameDirectory);
        if (proxy == null) return new DoorstopState(false, null, null, null, false);

        string? version = null;
        string versionFile = Path.Combine(gameDirectory, VersionFile);
        if (File.Exists(versionFile)) version = File.ReadAllText(versionFile).Trim();
        if (string.IsNullOrEmpty(version))
        {
            try
            {
                version = FileVersionInfo.GetVersionInfo(proxy).FileVersion;
            }
            catch
            {
                version = null;
            }
        }

        bool? enabled = null;
        string? target = null;
        bool ours = false;
        string config = Path.Combine(gameDirectory, ConfigFile);
        if (File.Exists(config))
        {
            foreach (string raw in File.ReadAllLines(config))
            {
                string line = raw.Trim();
                if (line.StartsWith(LauncherMarker, StringComparison.Ordinal)) ours = true;
                if (line.StartsWith('#') || line.StartsWith(';')) continue;
                int equals = line.IndexOf('=');
                if (equals < 0) continue;
                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase)) enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (key.Equals("target_assembly", StringComparison.OrdinalIgnoreCase) || key.Equals("targetAssembly", StringComparison.OrdinalIgnoreCase)) target = value;
            }
        }
        return new DoorstopState(true, version, enabled, target, ours && Path.GetFileName(proxy) == ProxyFile);
    }

    /// <summary>
    /// The folder's Doorstop proxy: winhttp.dll, or version.dll or winmm.dll when they are Doorstop's (by their
    /// version info, or a doorstop_config.ini next to them).
    /// </summary>
    private static string? FindProxy(string gameDirectory)
    {
        string winhttp = Path.Combine(gameDirectory, ProxyFile);
        if (File.Exists(winhttp)) return winhttp;
        bool config = File.Exists(Path.Combine(gameDirectory, ConfigFile));
        foreach (string name in OtherProxyFiles)
        {
            string path = Path.Combine(gameDirectory, name);
            if (File.Exists(path) && (config || IsDoorstop(path))) return path;
        }
        return null;
    }

    private static bool IsDoorstop(string dll)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(dll);
            string text = $"{info.CompanyName} {info.ProductName} {info.FileDescription} {info.InternalName}";
            return text.Contains("Doorstop", StringComparison.OrdinalIgnoreCase) || text.Contains("NeighTools", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds the proxy from a BepInExPack in the store, turned off for normal launches. Does nothing if there is a
    /// loader already, and never replaces another install's files: a doorstop_config.ini without a loader (BepInEx
    /// switched off by removing its winhttp.dll) is left for the player to sort out.
    /// </summary>
    public static void InstallIfMissing(string gameDirectory, string bepInExPackDirectory)
    {
        if (FindProxy(gameDirectory) != null) return;
        string configPath = Path.Combine(gameDirectory, ConfigFile);
        if (File.Exists(configPath))
        {
            // The launcher's own config without its dll: an earlier install stopped half way. Start again.
            if (IsOurs(configPath)) File.Delete(configPath);
            else
            {
                throw new InvalidOperationException(Loc.F(
                    "Void Crew's folder has a doorstop_config.ini from another BepInEx install, but not its winhttp.dll. To use the launcher, delete doorstop_config.ini (and .doorstop_version, if it's there) from\n{0}\nor put that install's winhttp.dll back.",
                    gameDirectory));
            }
        }
        string pack = Path.Combine(bepInExPackDirectory, "BepInExPack");
        string proxy = Path.Combine(pack, ProxyFile);
        if (!File.Exists(proxy)) throw new FileNotFoundException(Loc.T("The BepInExPack in the launcher's store has no winhttp.dll."), proxy);

        string versionFile = Path.Combine(pack, VersionFile);
        string gameVersionFile = Path.Combine(gameDirectory, VersionFile);
        string? addedVersion = File.Exists(versionFile) && !File.Exists(gameVersionFile) ? File.ReadAllText(versionFile).Trim() : null;

        // The marked config goes in before the dll: whenever our dll is there, so is the mark that says it's ours.
        var config = new StringBuilder();
        config.AppendLine(LauncherMarker + ". Off unless the launcher starts the game with mods; delete this file and");
        config.AppendLine("# winhttp.dll to remove it (the game itself is not changed).");
        if (addedVersion != null) config.AppendLine(AddedVersionMarker + addedVersion);
        config.AppendLine("[General]");
        config.AppendLine("enabled = false");
        config.AppendLine(@"target_assembly = BepInEx\core\BepInEx.Preloader.dll");
        config.AppendLine("redirect_output_log = false");
        config.AppendLine("boot_config_override =");
        config.AppendLine("ignore_disable_switch = false");
        config.AppendLine();
        config.AppendLine("[UnityMono]");
        config.AppendLine("dll_search_path_override =");
        config.AppendLine("debug_enabled = false");
        config.AppendLine("debug_address = 127.0.0.1:10000");
        config.AppendLine("debug_suspend = false");
        using (var file = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(config.ToString());
        }
        try
        {
            File.Copy(proxy, Path.Combine(gameDirectory, ProxyFile));
        }
        catch
        {
            try
            {
                File.Delete(configPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Marked as ours, so the next try replaces it.
            }
            throw;
        }
        if (addedVersion != null)
        {
            try
            {
                File.Copy(versionFile, gameVersionFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Could not add {VersionFile} to {gameDirectory}: {e.Message}");
            }
        }
        AppLog.Info($"Added BepInEx's loader (Doorstop) to {gameDirectory}, off for normal launches.");
    }

    /// <summary>The name an outdated loader's files are kept under when the launcher's replaces them.</summary>
    public const string OldLoaderSuffix = ".old-doorstop";

    /// <summary>
    /// Puts the launcher's Doorstop in place of another install's outdated one (see <see cref="DoorstopState.Outdated"/>).
    /// The old files are kept next to it, renamed with <see cref="OldLoaderSuffix"/>.
    /// </summary>
    public static void ReplaceOutdated(string gameDirectory, string bepInExPackDirectory)
    {
        if (!Inspect(gameDirectory).Outdated) return;
        foreach (string name in new[] { ProxyFile, ConfigFile, VersionFile })
        {
            string path = Path.Combine(gameDirectory, name);
            if (File.Exists(path)) File.Move(path, path + OldLoaderSuffix, true);
        }
        AppLog.Info($"Moved the outdated Doorstop in {gameDirectory} aside ({OldLoaderSuffix}).");
        InstallIfMissing(gameDirectory, bepInExPackDirectory);
    }

    /// <summary>
    /// Removes the loader the launcher added (nothing when it isn't the launcher's): winhttp.dll first, then the config
    /// that marks it as ours, then .doorstop_version only if the launcher put it there (its config says so, or it matches
    /// the copy in one of <paramref name="bepInExPackDirectories"/>). False when something couldn't be deleted.
    /// </summary>
    public static bool Remove(string gameDirectory, IEnumerable<string>? bepInExPackDirectories = null)
    {
        if (!Inspect(gameDirectory).InstalledByLauncher) return false;
        string configPath = Path.Combine(gameDirectory, ConfigFile);
        string versionPath = Path.Combine(gameDirectory, VersionFile);
        bool removeVersion = false;
        try
        {
            if (File.Exists(versionPath))
            {
                string installed = File.ReadAllText(versionPath).Trim();
                string? recorded = File.ReadLines(configPath).Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith(AddedVersionMarker, StringComparison.Ordinal))?[AddedVersionMarker.Length..].Trim();
                removeVersion = recorded == installed || (bepInExPackDirectories ?? Enumerable.Empty<string>())
                    .Select(pack => Path.Combine(pack, "BepInExPack", VersionFile))
                    .Any(shipped => File.Exists(shipped) && File.ReadAllText(shipped).Trim() == installed);
            }
            File.Delete(Path.Combine(gameDirectory, ProxyFile));
            File.Delete(configPath);
            if (removeVersion) File.Delete(versionPath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not remove the loader from {gameDirectory}: {e.Message}");
            return false;
        }
    }

    private static bool IsOurs(string configPath)
    {
        try
        {
            return File.ReadLines(configPath).Any(l => l.Trim().StartsWith(LauncherMarker, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The arguments that make Doorstop load the launcher's BepInEx. Both spellings: Doorstop 4 (BepInEx 5.4.22+) and
    /// Doorstop 3; each ignores the other's. The game reads the Doorstop 4 pair too, to find mods' asset bundles.
    /// </summary>
    public static string[] Arguments(string preloader) =>
    [
        "--doorstop-enabled", "true",
        "--doorstop-target-assembly", preloader,
        "--doorstop-enable", "true",
        "--doorstop-target", preloader,
    ];
}
