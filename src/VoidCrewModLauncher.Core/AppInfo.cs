using System.Reflection;

namespace VoidCrewModLauncher.Core;

/// <summary>Fixed facts about the launcher and the game it launches.</summary>
public static class AppInfo
{
    public const string Name = "Void Crew Mod Launcher";
    public const string SteamAppId = "1063420";
    public const string GameExeName = "Void Crew.exe";
    public const string GameProcessName = "Void Crew";
    public const string GameDataFolder = "Void Crew_Data";
    public const string ThunderstoreCommunity = "void-crew";
    public const string RepositoryUrl = "https://github.com/CaptainChunck/VoidCrewModLauncher";

    public static string Version { get; } = ReadVersion();

    public static string UserAgent => $"VoidCrewModLauncher/{Version} (+{RepositoryUrl})";

    private static string ReadVersion()
    {
        Version? version = typeof(AppInfo).Assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
