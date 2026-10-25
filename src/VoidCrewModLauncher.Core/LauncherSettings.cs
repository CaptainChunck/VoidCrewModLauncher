using System.Security.Cryptography;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core;

/// <summary>The launcher's settings (settings.json).</summary>
public sealed class LauncherSettings
{
    /// <summary>The Void Crew folder; found through Steam when empty.</summary>
    public string? GameDirectory { get; set; }

    /// <summary>Start the game through Steam (steam.exe -applaunch). Otherwise the exe is started directly.</summary>
    public bool LaunchThroughSteam { get; set; } = true;

    /// <summary>Shared secret between the launcher and the bridge; made once per install.</summary>
    public string BridgeToken { get; set; } = "";

    /// <summary>
    /// When Void Crew was started only to fill the server browser, keep the launcher in front of it. (The name is from
    /// when it minimised the game, which crashes Void Crew while it loads.)
    /// </summary>
    public bool MinimizeGameWhenBrowsing { get; set; }

    /// <summary>Show BepInEx's log console window next to the game.</summary>
    public bool ShowBepInExConsole { get; set; }

    /// <summary>The player agreed that the launcher may add BepInEx's Doorstop loader to the game folder.</summary>
    public bool DoorstopConsent { get; set; }

    /// <summary>The player agreed that the launcher may replace another install's outdated Doorstop (3.x) with its own.</summary>
    public bool ReplaceOutdatedDoorstop { get; set; }

    /// <summary>At start, look for a new launcher on GitHub and install it.</summary>
    public bool AutoUpdateLauncher { get; set; } = true;

    /// <summary>At start and on refresh, update installed Thunderstore mods to their newest versions.</summary>
    public bool AutoUpdateMods { get; set; } = true;

    /// <summary>For mods whose Thunderstore page names a GitHub repo, use the repo's newest release when it's newer.</summary>
    public bool GitHubUpdates { get; set; } = true;

    /// <summary>The launcher version the self-update last tried, and when: a release that won't install isn't retried in a loop.</summary>
    public string? UpdateTriedVersion { get; set; }

    public DateTime? UpdateTriedUtc { get; set; }

    /// <summary>The launcher's language (a code from Loc.Languages); null for the Windows language.</summary>
    public string? Language { get; set; }

    public HostSettings Host { get; set; } = new();

    public BrowserSettings Browser { get; set; } = new();

    /// <summary>
    /// Reads settings.json, or starts from defaults when there is none or it is broken. Throws (an IOException) when the
    /// file is there but can't be opened, rather than replacing the player's settings with defaults.
    /// </summary>
    public static LauncherSettings Load(AppPaths paths)
    {
        LauncherSettings settings = JsonFiles.Load<LauncherSettings>(paths.SettingsFile) ?? new LauncherSettings();
        if (string.IsNullOrEmpty(settings.BridgeToken))
        {
            settings.BridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            settings.Save(paths);
        }
        return settings;
    }

    public void Save(AppPaths paths) => JsonFiles.Save(paths.SettingsFile, this);

    /// <summary>
    /// Only the language from settings.json, for start-up before anything else is read (or logged): null when there is
    /// no file, no language, or the file can't be read.
    /// </summary>
    public static string? ReadLanguage(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.SettingsFile)) return null;
            using FileStream stream = new(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && document.RootElement.TryGetProperty("language", out var language)
                   && language.ValueKind == System.Text.Json.JsonValueKind.String
                ? language.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public sealed class HostSettings
{
    public string Name { get; set; } = "";
    public bool IsPublic { get; set; } = true;
    public int MaxPlayers { get; set; } = 4;
    public bool CrossPlay { get; set; } = true;

    /// <summary>
    /// The host's choice of what joining players need, per mod id (any case, as everywhere else); mods not listed use the
    /// launcher's guess. Reading settings.json gives a dictionary of its own, which is copied into one that ignores case.
    /// </summary>
    public Dictionary<string, ModNeed> ModNeeds
    {
        get => _modNeeds;
        set
        {
            var needs = new Dictionary<string, ModNeed>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ModNeed> pair in value ?? new Dictionary<string, ModNeed>()) needs[pair.Key] = pair.Value;
            _modNeeds = needs;
        }
    }

    private Dictionary<string, ModNeed> _modNeeds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mod ids joining players were checked for when you last hosted with them, as the bridge reported it, that the
    /// launcher can't tell from the mods' DLLs: mods that register required game assets (whatever their VoidManager type
    /// says), or that VoidManager's own settings make "All". Everyone gets them, whatever ModNeeds says.
    /// </summary>
    public List<string> CheckedInGame { get; set; } = new();
}

public sealed class BrowserSettings
{
    public bool HideFull { get; set; }
    public bool HideVanilla { get; set; }
    public bool HideModded { get; set; }
    public bool OnlyInHub { get; set; }
}
