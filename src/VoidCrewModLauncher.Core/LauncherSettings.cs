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

    /// <summary>Minimise Void Crew when it was started only to fill the server browser.</summary>
    public bool MinimizeGameWhenBrowsing { get; set; }

    /// <summary>Show BepInEx's log console window next to the game.</summary>
    public bool ShowBepInExConsole { get; set; }

    /// <summary>The player agreed that the launcher may add BepInEx's Doorstop loader to the game folder.</summary>
    public bool DoorstopConsent { get; set; }

    /// <summary>At start, look for a new launcher on GitHub and install it.</summary>
    public bool AutoUpdateLauncher { get; set; } = true;

    /// <summary>At start and on refresh, update installed Thunderstore mods to their newest versions.</summary>
    public bool AutoUpdateMods { get; set; } = true;

    /// <summary>For mods whose Thunderstore page names a GitHub repo, use the repo's newest release when it's newer.</summary>
    public bool GitHubUpdates { get; set; } = true;

    /// <summary>The launcher version the self-update last tried, and when: a release that won't install isn't retried in a loop.</summary>
    public string? UpdateTriedVersion { get; set; }

    public DateTime? UpdateTriedUtc { get; set; }

    public HostSettings Host { get; set; } = new();

    public BrowserSettings Browser { get; set; } = new();

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
}

public sealed class HostSettings
{
    public string Name { get; set; } = "";
    public bool IsPublic { get; set; } = true;
    public int MaxPlayers { get; set; } = 4;
    public bool CrossPlay { get; set; } = true;

    /// <summary>The host's choice of what joining players need, per mod id; mods not listed use the launcher's guess.</summary>
    public Dictionary<string, ModNeed> ModNeeds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BrowserSettings
{
    public bool HideFull { get; set; }
    public bool HideVanilla { get; set; }
    public bool HideModded { get; set; }
    public bool OnlyInHub { get; set; }
}
