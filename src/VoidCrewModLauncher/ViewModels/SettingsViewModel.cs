using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Steam;
using VoidCrewModLauncher.Infrastructure;

namespace VoidCrewModLauncher.ViewModels;

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
        StoreText = "Downloaded mods: 12.4 MB";
        OnPropertyChanged(nameof(DataFolder));
    }
    public string VersionText => $"{AppInfo.Name} {AppInfo.Version}";
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

    /// <summary>A new launcher from GitHub (installed straight away), then updates for the installed mods.</summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (await _main.UpdateLauncherAsync(manual: true)) return;
        if (await _main.UpdateModsAsync() == null) _main.SetStatus(_main.StatusText + " Your mods are up to date.");
    }

    public void Refresh()
    {
        string? game = Service.GameDirectory;
        GameDirectory = game ?? "";
        GameText = game == null
            ? "Not found. Choose the folder with Void Crew.exe in it."
            : Service.Settings.GameDirectory == null ? "Found through Steam." : "Chosen by you.";
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
        StoreText = $"Downloaded mods: {Format.Bytes(bytes)}";
    }

    private static long DirectorySize(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    [RelayCommand]
    private void BrowseGame()
    {
        string? folder = _main.Dialogs.PickFolder("Choose the Void Crew folder (with Void Crew.exe)", Service.GameDirectory);
        if (folder == null) return;
        if (!SteamLocator.IsGameDirectory(folder))
        {
            _main.Dialogs.Error("Not the Void Crew folder", $"{folder} has no {AppInfo.GameExeName}. Choose the folder Steam installed the game in (…\\steamapps\\common\\Void Crew).");
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
            _main.Dialogs.Info("No game log yet", "BepInEx writes its log (LogOutput.log) the first time Void Crew starts from the launcher.");
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
        if (game == null || !Doorstop.Inspect(game).InstalledByLauncher || !_main.CheckIdle("Removing the loader")) return;
        if (GameProcess.IsRunning())
        {
            _main.Dialogs.Info("Close Void Crew first", "Void Crew has the loader open while it runs.");
            return;
        }
        if (!_main.Dialogs.Confirm("Remove BepInEx's loader",
                "Remove winhttp.dll and doorstop_config.ini, which the launcher added to the game folder? The launcher adds them again (after asking) the next time you start a modded game.",
                "REMOVE")) return;
        foreach (string file in new[] { Doorstop.ProxyFile, Doorstop.ConfigFile, Doorstop.VersionFile })
        {
            try
            {
                File.Delete(Path.Combine(game, file));
            }
            catch (IOException e)
            {
                AppLog.Warn($"Could not delete {file}: {e.Message}");
            }
        }
        Service.Settings.DoorstopConsent = false;
        Service.SaveSettings();
        Refresh();
        _main.SetStatus("Removed the launcher's loader from the game folder.");
    }

    /// <summary>Deletes mod versions in the store that no installed mod uses (lobbies can download them again).</summary>
    [RelayCommand]
    private void FreeSpace()
    {
        if (!_main.CheckIdle("Freeing space")) return;
        if (GameProcess.IsRunning())
        {
            _main.Dialogs.Info("Close Void Crew first", "The running game may be using some of these files.");
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
                        Directory.Delete(versionFolder, true);
                        freed += size;
                        removed++;
                    }
                    catch (IOException e)
                    {
                        AppLog.Warn($"Could not delete {versionFolder}: {e.Message}");
                    }
                }
                if (Directory.GetFileSystemEntries(modFolder).Length == 0) Directory.Delete(modFolder);
            }
        }
        Refresh();
        _main.SetStatus($"Removed {removed} unused mod version{(removed == 1 ? "" : "s")} ({Format.Bytes(freed)}).");
    }
}
