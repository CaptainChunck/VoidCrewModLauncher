using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VoidCrewModLauncher.Core.Setup;

/// <summary>What a voidcrewmod:// link asks for.</summary>
public enum LinkAction
{
    /// <summary>Just bring the launcher up.</summary>
    Open,

    /// <summary>Install a mod (with what it needs): voidcrewmod://install/Namespace-Name[/1.2.3].</summary>
    Install,
}

/// <summary>A parsed voidcrewmod:// link.</summary>
public sealed record LauncherLink(LinkAction Action, string? ModId = null, string? Version = null);

/// <summary>
/// voidcrewmod:// links, so a website's "download with the Mod Launcher" button opens the launcher and installs that
/// mod (the player confirms first). Windows starts the launcher with the link as its argument; the setup registers the
/// link type for the user (no admin rights). Formats:
/// <list type="bullet">
/// <item>voidcrewmod://install/Captain_Chuncks_Mods-Void_Crew_Fleet (Thunderstore "Namespace-Name"), or
/// .../Namespace-Name/1.2.3 for one version;</item>
/// <item>voidcrewmod://install/github-Raicuparta-UUVR (a GitHub-only mod the launcher knows);</item>
/// <item>voidcrewmod://open.</item>
/// </list>
/// </summary>
public static class LauncherLinks
{
    public const string Scheme = "voidcrewmod";

    private static readonly Regex ModId = new("^[A-Za-z0-9_]+(-[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionText = new(@"^\d+(\.\d+){1,3}$", RegexOptions.CultureInvariant);

    /// <summary>The link among a start's arguments, if any.</summary>
    public static string? FindIn(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads a link; null for anything that isn't a well-formed voidcrewmod:// link.</summary>
    public static LauncherLink? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300) return null;
        text = text.Trim();
        if (!text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return null;
        string rest = text[(Scheme.Length + 1)..].TrimStart('/');
        int query = rest.IndexOfAny(new[] { '?', '#' });
        if (query >= 0) rest = rest[..query];
        string[] parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length == 0) return new LauncherLink(LinkAction.Open);
        switch (parts[0].ToLowerInvariant())
        {
            case "open":
                return new LauncherLink(LinkAction.Open);
            case "install":
                if (parts.Length < 2 || parts.Length > 3 || !ModId.IsMatch(parts[1])) return null;
                if (parts.Length == 3 && !VersionText.IsMatch(parts[2])) return null;
                return new LauncherLink(LinkAction.Install, parts[1], parts.Length == 3 ? parts[2] : null);
            default:
                return null;
        }
    }

    /// <summary>Makes Windows open voidcrewmod:// links with <paramref name="exe"/> (for this user).</summary>
    public static void Register(string exe, string classesKey)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{classesKey}\{Scheme}");
        key.SetValue("", $"URL:{AppInfo.Name}");
        key.SetValue("URL Protocol", "");
        using (RegistryKey icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
        using RegistryKey command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\" \"%1\"");
    }

    /// <summary>Whether the link type points at <paramref name="exe"/> already.</summary>
    public static bool IsRegistered(string exe, string classesKey)
    {
        using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"{classesKey}\{Scheme}\shell\open\command");
        return string.Equals(command?.GetValue("") as string, $"\"{exe}\" \"%1\"", StringComparison.OrdinalIgnoreCase);
    }

    public static void Unregister(string classesKey) => Registry.CurrentUser.DeleteSubKeyTree($@"{classesKey}\{Scheme}", false);
}
