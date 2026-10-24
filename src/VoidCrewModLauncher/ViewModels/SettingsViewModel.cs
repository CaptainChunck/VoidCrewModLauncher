using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Steam;
using VoidCrewModLauncher.Infrastructure;

namespace VoidCrewModLauncher.ViewModels;

/// <summary>One entry of the LANGUAGE list: a code from <see cref="Loc.Languages"/>, or null for the Windows language.</summary>
public sealed record LanguageChoice(string? Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The SETTINGS page.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    private LauncherService Service => _main.Service;

    [ObservableProperty] private string gameDirectory = "";
    [ObservableProperty] private string gameText = "";
    [ObservableProperty] private string loaderText = "";
    [ObservableProperty] private bool canRemoveLoader;
    [ObservableProperty] private string storeText = "";

    public string DataFolder => _shownDataFolder ?? Service.Paths.Root;

    private string? _shownDataFolder;

    /// <summary>For the README screenshots (--render-previews): standard paths instead of this PC's.</summary>
    public void ShowExamplePaths()
    {
        GameDirectory = @"C:\Program Files (x86)\Steam\steamapps\common\Void Crew";
        _shownDataFolder = @"C:\Users\Player\AppData\Roaming\VoidCrewModLauncher";
        StoreText = Loc.F("Downloaded mods: {0}", Format.Bytes(13_002_342));
        OnPropertyChanged(nameof(DataFolder));
    }
    public string VersionText => $"{AppInfo.Name} {AppInfo.Version}";

    /// <summary>The LANGUAGE list: the Windows language first, then every language in its own name.</summary>
    public IReadOnlyList<LanguageChoice> Languages { get; } =
        new[] { new LanguageChoice(null, Loc.T("Windows language")) }
            .Concat(Loc.Languages.Select(l => new LanguageChoice(l.Code, l.NativeName)))
            .ToList();

    /// <summary>The chosen language; changing it restarts the launcher (after asking), since the text is set at start-up.</summary>
    public LanguageChoice SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => string.Equals(l.Code, Service.Settings.Language, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        set
        {
            if (value == null || string.Equals(value.Code, Service.Settings.Language, StringComparison.OrdinalIgnoreCase)) return;
            if (!_main.CheckIdle(Loc.T("Changing the language"))
                || !_main.Dialogs.Confirm(Loc.T("Restart the launcher"), Loc.T("The launcher restarts in the new language."), Loc.T("RESTART")))
            {
                // Put the list back once it has finished its own change.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedLanguage)));
                return;
            }
            Service.Settings.Language = value.Code;
            Service.SaveSettings();
            OnPropertyChanged();
            App.Restart(_main.LauncherArguments);
        }
    }
    public string RepositoryUrl => AppInfo.RepositoryUrl;

    public bool LaunchThroughSteam
    {
        get => Service.Settings.LaunchThroughSteam;
        set
        {
            Service.Settings.LaunchThroughSteam = value;
            Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool ShowBepInExConsole
    {
        get => Service.Settings.ShowBepInExConsole;
        set
        {
            Service.Settings.ShowBepInExConsole = value;
            Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool MinimizeWhenBrowsing
    {
        get => Service.Settings.MinimizeGameWhenBrowsing;
        set
        {
            Service.Settings.MinimizeGameWhenBrowsing = value;
            Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool AutoUpdateLauncher
    {
        get => Service.Settings.AutoUpdateLauncher;
        set
        {
            Service.Settings.AutoUpdateLauncher = value;
            Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool AutoUpdateMods
    {
        get => Service.Settings.AutoUpdateMods;
        set
        {
            Service.Settings.AutoUpdateMods = value;
            Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool GitHubUpdates
    {
        get => Service.Settings.GitHubUpdates;
        set
        {
            Service.Settings.GitHubUpdates = value;
            Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>A new launcher from GitHub (installed straight away), then updates for the installed mods.</summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (!_main.CheckIdle(Loc.T("Checking for updates"))) return;
        if (await _main.UpdateLauncherAsync(manual: true)) return;
        if (await _main.UpdateModsAsync() == null) _main.SetStatus(Loc.F("{0} {1}", _main.StatusText, Loc.T("Your mods are up to date.")));
    }

    public void Refresh()
    {
        string? game = Service.GameDirectory;
        GameDirectory = game ?? "";
        GameText = game == null
            ? Loc.T("Not found. Choose the folder with Void Crew.exe in it.")
            : Service.Settings.GameDirectory == null ? Loc.T("Found through Steam.") : Loc.T("Chosen by you.");
        if (game != null)
        {
            DoorstopState state = Doorstop.Inspect(game);
            LoaderText = state.Describe();
            CanRemoveLoader = state.InstalledByLauncher;
        }
        else
        {
            LoaderText = "";
            CanRemoveLoader = false;
        }
        long bytes = DirectorySize(Service.Paths.Store);
        StoreText = Loc.F("Downloaded mods: {0}", Format.Bytes(bytes));
    }

    private static long DirectorySize(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    [RelayCommand]
    private void BrowseGame()
    {
        string? folder = _main.Dialogs.PickFolder(Loc.T("Choose the Void Crew folder (with Void Crew.exe)"), Service.GameDirectory);
        if (folder == null) return;
        if (!SteamLocator.IsGameDirectory(folder))
        {
            _main.Dialogs.Error(Loc.T("Not the Void Crew folder"), Loc.F("{0} has no {1}. Choose the folder Steam installed the game in (…\\steamapps\\common\\Void Crew).", folder, AppInfo.GameExeName));
            return;
        }
        Service.Settings.GameDirectory = folder;
        Service.SaveSettings();
        Refresh();
    }

    [RelayCommand]
    private void FindGame()
    {
        Service.Settings.GameDirectory = null;
        Service.SaveSettings();
        Refresh();
    }

    [RelayCommand]
    private void OpenGameFolder() => Format.Open(Service.GameDirectory);

    [RelayCommand]
    private void OpenDataFolder() => Format.Open(Service.Paths.Root);

    [RelayCommand]
    private void OpenLauncherLog() => Format.Reveal(Service.Paths.LauncherLog);

    [RelayCommand]
    private void OpenGameLog()
    {
        if (!File.Exists(Service.Paths.GameLog))
        {
            _main.Dialogs.Info(Loc.T("No game log yet"), Loc.T("BepInEx writes its log (LogOutput.log) the first time Void Crew starts from the launcher."));
            return;
        }
        Format.Reveal(Service.Paths.GameLog);
    }

    [RelayCommand]
    private void OpenConfigFolder() => Format.Open(Path.Combine(Service.Paths.RuntimeBepInEx, "config"));

    [RelayCommand]
    private void OpenRepository() => Format.Open(AppInfo.RepositoryUrl);

    [RelayCommand]
    private void RemoveLoader()
    {
        string? game = Service.GameDirectory;
        if (game == null || !Doorstop.Inspect(game).InstalledByLauncher || !_main.CheckIdle(Loc.T("Removing the loader"))) return;
        if (GameProcess.IsRunning())
        {
            _main.Dialogs.Info(Loc.T("Close Void Crew first"), Loc.T("Void Crew has the loader open while it runs."));
            return;
        }
        if (!_main.Dialogs.Confirm(Loc.T("Remove BepInEx's loader"),
                Loc.T("Remove winhttp.dll and doorstop_config.ini, which the launcher added to the game folder? The launcher adds them again (after asking) the next time you start a modded game."),
                Loc.T("REMOVE"))) return;
        // Something may have started while the question was open.
        if (!_main.CheckIdle(Loc.T("Removing the loader")) || GameProcess.IsRunning()) return;
        IEnumerable<string> packs = Service.Store.Versions(Core.Thunderstore.PackageIndex.BepInExPackId)
            .Select(v => Service.Store.VersionDirectory(Core.Thunderstore.PackageIndex.BepInExPackId, v)).ToList();
        if (!Doorstop.Remove(game, packs))
        {
            Refresh();
            _main.SetStatus(Loc.T("Could not remove the loader from the game folder."));
            return;
        }
        Service.Settings.DoorstopConsent = false;
        Service.SaveSettings();
        Refresh();
        _main.SetStatus(Loc.T("Removed the launcher's loader from the game folder."));
    }

    /// <summary>Deletes mod versions in the store that no installed mod uses (lobbies can download them again).</summary>
    [RelayCommand]
    private void FreeSpace()
    {
        if (!_main.CheckIdle(Loc.T("Freeing space"))) return;
        if (GameProcess.IsRunning())
        {
            _main.Dialogs.Info(Loc.T("Close Void Crew first"), Loc.T("The running game may be using some of these files."));
            return;
        }
        var keep = new HashSet<string>(Service.Library.Mods.Select(m => Path.GetFullPath(Service.Store.VersionDirectory(m.Id, m.Version))), StringComparer.OrdinalIgnoreCase);
        string? bepInEx = Service.Store.Versions(Core.Thunderstore.PackageIndex.BepInExPackId).OrderByDescending(v => v, Core.Thunderstore.VersionNumber.Comparer).FirstOrDefault();
        if (bepInEx != null) keep.Add(Path.GetFullPath(Service.Store.VersionDirectory(Core.Thunderstore.PackageIndex.BepInExPackId, bepInEx)));
        long freed = 0;
        int removed = 0;
        if (Directory.Exists(Service.Paths.Store))
        {
            foreach (string modFolder in Directory.GetDirectories(Service.Paths.Store))
            {
                foreach (string versionFolder in Directory.GetDirectories(modFolder))
                {
                    if (keep.Contains(Path.GetFullPath(versionFolder))) continue;
                    long size = DirectorySize(versionFolder);
                    try
                    {
                        FileTools.DeleteDirectory(versionFolder);
                        freed += size;
                        removed++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Warn($"Could not delete {versionFolder}: {e.Message}");
                    }
                }
                try
                {
                    if (Directory.GetFileSystemEntries(modFolder).Length == 0) Directory.Delete(modFolder);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Could not delete {modFolder}: {e.Message}");
                }
            }
        }
        Refresh();
        _main.SetStatus(removed == 1
            ? Loc.F("Removed {0} unused mod version ({1}).", removed, Format.Bytes(freed))
            : Loc.F("Removed {0} unused mod versions ({1}).", removed, Format.Bytes(freed)));
    }
}
