using System.Text.Json.Serialization;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Mods;

public enum ModSource
{
    Thunderstore,
    Local,
}

/// <summary>What happens to one of your mods when you join somebody else's lobby.</summary>
public enum JoinBehaviour
{
    /// <summary>Kept when it only changes your own game (client-side, VR, local mods); otherwise only if the lobby has it.</summary>
    Auto,

    /// <summary>Always kept.</summary>
    Keep,

    /// <summary>Only loaded when the lobby runs it too.</summary>
    OnlyIfLobbyHasIt,
}

/// <summary>A mod in the launcher's library (library.json): what's installed, and whether you play with it.</summary>
public sealed class InstalledMod
{
    /// <summary>Thunderstore "Namespace-Name", or "local-..." for a mod added from your PC.</summary>
    public string Id { get; set; } = "";

    public ModSource Source { get; set; }

    /// <summary>Package name (the part after the namespace), or a local mod's name.</summary>
    public string Name { get; set; } = "";

    public string Author { get; set; } = "";

    /// <summary>The version you play with (more versions may be in the store for lobbies that use them).</summary>
    public string Version { get; set; } = "";

    public string Description { get; set; } = "";
    public string? WebsiteUrl { get; set; }
    public string? IconUrl { get; set; }

    /// <summary>The GitHub release <see cref="Version"/> came from, when it was newer there than on Thunderstore.</summary>
    public string? GitHubRelease { get; set; }

    /// <summary>Thunderstore dependency strings of <see cref="Version"/>.</summary>
    public List<string> Dependencies { get; set; } = new();

    public List<string> Categories { get; set; } = new();

    /// <summary>On in the INSTALLED tab: loaded when you play or host.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Installed only because another mod needs it.</summary>
    public bool InstalledAsDependency { get; set; }

    public JoinBehaviour WhenJoining { get; set; } = JoinBehaviour.Auto;

    /// <summary>BepInEx plugin GUIDs found in the mod's DLLs.</summary>
    public List<string> PluginGuids { get; set; } = new();

    public DateTime InstalledUtc { get; set; }

    [JsonIgnore]
    public string DisplayName => Name.Replace('_', ' ');

    [JsonIgnore]
    public bool IsLocal => Source == ModSource.Local;

    /// <summary>What players joining your lobby need, as the launcher guesses it (the HOST tab can override it).</summary>
    [JsonIgnore]
    public ModNeed GuessedNeed => ModPolicy.GuessNeed(Id, Categories, IsLocal);
}

/// <summary>library.json.</summary>
public sealed class LibraryFile
{
    public int Format { get; set; } = 1;
    public List<InstalledMod> Mods { get; set; } = new();
}
