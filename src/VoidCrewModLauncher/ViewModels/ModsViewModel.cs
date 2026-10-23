using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Infrastructure;

namespace VoidCrewModLauncher.ViewModels;

/// <summary>The MODS page: INSTALLED (your library, with the on/off switches) and BROWSE (Thunderstore).</summary>
public partial class ModsViewModel : ObservableObject
{
    public static readonly string[] SortOptions = { "Most downloaded", "Top rated", "Recently updated", "Newest", "Name" };

    private readonly MainViewModel _main;
    private readonly ObservableCollection<InstalledModViewModel> _installed = new();
    private readonly ObservableCollection<PackageViewModel> _online = new();
    private int _readmeRequest;

    public ModsViewModel(MainViewModel main)
    {
        _main = main;
        InstalledView = CollectionViewSource.GetDefaultView(_installed);
        InstalledView.Filter = o => o is InstalledModViewModel m && Matches(InstalledSearch, $"{m.Name} {m.Mod.Author} {m.Description}");
        InstalledView.SortDescriptions.Add(new SortDescription(nameof(InstalledModViewModel.IsDependency), ListSortDirection.Ascending));
        InstalledView.SortDescriptions.Add(new SortDescription(nameof(InstalledModViewModel.Name), ListSortDirection.Ascending));
        OnlineView = CollectionViewSource.GetDefaultView(_online);
        OnlineView.Filter = OnlineFilter;
        ApplySort();
        RefreshInstalled();
    }

    public ICollectionView InstalledView { get; }
    public ICollectionView OnlineView { get; }
    public ObservableCollection<string> Categories { get; } = new() { "All categories" };
    public IReadOnlyList<string> Sorts => SortOptions;

    [ObservableProperty] private bool showBrowse;
    [ObservableProperty] private string installedSearch = "";
    [ObservableProperty] private string onlineSearch = "";
    [ObservableProperty] private string category = "All categories";
    [ObservableProperty] private string sort = SortOptions[0];
    [ObservableProperty] private bool showDeprecated;
    [ObservableProperty] private PackageViewModel? selectedPackage;
    [ObservableProperty] private FlowDocument? readme;
    [ObservableProperty] private int updateCount;
    [ObservableProperty] private int installedCount;
    [ObservableProperty] private int enabledCount;
    [ObservableProperty] private string bepInExText = "BepInEx: not downloaded yet";

    public string UpdateAllText => UpdateCount == 0 ? "UP TO DATE" : $"UPDATE ALL ({UpdateCount})";

    private static bool Matches(string search, string text) => string.IsNullOrWhiteSpace(search) || text.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- INSTALLED

    public void RefreshInstalled()
    {
        LauncherService service = _main.Service;
        var mods = service.Library.Mods;
        var updates = service.Mods.Updates().ToDictionary(u => u.Mod.Id, u => u.Latest.VersionNumber, StringComparer.OrdinalIgnoreCase);
        foreach (InstalledModViewModel gone in _installed.Where(vm => mods.All(m => !string.Equals(m.Id, vm.Id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _installed.Remove(gone);
        }
        foreach (InstalledMod mod in mods)
        {
            InstalledModViewModel? row = _installed.FirstOrDefault(vm => string.Equals(vm.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                row = new InstalledModViewModel(this, mod);
                _installed.Add(row);
                LoadIcon(row, mod.IconUrl);
            }
            else
            {
                row.Refresh(mod);
            }
            row.UpdateVersion = updates.TryGetValue(mod.Id, out string? latest) ? latest : null;
        }
        UpdateCount = updates.Count;
        InstalledCount = mods.Count;
        EnabledCount = mods.Count(m => m.Enabled);
        string? bepInEx = service.Store.Versions(PackageIndex.BepInExPackId).OrderByDescending(v => v, VersionNumber.Comparer).FirstOrDefault();
        BepInExText = bepInEx == null ? "BepInEx: not downloaded yet" : $"BepInEx {bepInEx}";
        OnPropertyChanged(nameof(UpdateAllText));
        InstalledView.Refresh();
        foreach (PackageViewModel package in _online) package.Installed = service.Library.Find(package.Id);
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

    private void LoadIcon(InstalledModViewModel row, string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        _ = SetIconAsync(url, icon => row.Icon = icon);
    }

    private async Task SetIconAsync(string url, Action<System.Windows.Media.ImageSource> set)
    {
        try
        {
            System.Windows.Media.ImageSource? icon = await _main.Icons.GetAsync(url);
            if (icon != null) _main.OnUi(() => set(icon));
        }
        catch (Exception e)
        {
            AppLog.Warn($"Icon {url}: {e.Message}");
        }
    }

    partial void OnInstalledSearchChanged(string value) => InstalledView.Refresh();

    public async Task SetEnabledAsync(InstalledModViewModel row, bool enabled)
    {
        ModManager mods = _main.Service.Mods;
        if (!enabled)
        {
            string what = $"Turning off {row.Name}";
            if (!_main.CheckIdle(what))
            {
                row.ResetEnabled(true);
                return;
            }
            List<InstalledMod> dependents = _main.Service.EnabledDependents(row.Id);
            bool alsoDependents = false;
            if (dependents.Count > 0)
            {
                int choice = _main.Dialogs.Ask("Other mods need it",
                    $"{string.Join(", ", dependents.Select(d => d.DisplayName))} need{(dependents.Count == 1 ? "s" : "")} {row.Name}. " +
                    "While they're on, the launcher still loads it for them.\n\nTurn them off too?",
                    new DialogButton("TURN THEM OFF TOO", ButtonKind.Primary), new DialogButton("ONLY THIS ONE"), new DialogButton("CANCEL"));
                if (choice == 2 || choice < 0)
                {
                    row.ResetEnabled(true);
                    return;
                }
                alsoDependents = choice == 0;
            }
            bool off = await _main.RunAsync(what, async () =>
            {
                if (alsoDependents)
                {
                    foreach (InstalledMod dependent in dependents) await mods.SetEnabledAsync(dependent.Id, false);
                }
                await mods.SetEnabledAsync(row.Id, false);
            });
            if (!off) row.ResetEnabled(true);
            else _main.SetStatus($"{row.Name} is off. It isn't loaded when you play or host.");
            return;
        }
        bool ok = await _main.RunAsync($"Turning on {row.Name}", () => _main.Service.SetModEnabledAsync(row.Id, true, new SyncProgress<InstallStatus>(s => _main.OnUi(() => _main.SetStatus(s.Message, s.Fraction)))));
        if (!ok) row.ResetEnabled(false);
        else _main.SetStatus($"{row.Name} is on.");
    }

    [RelayCommand]
    private Task Update(InstalledModViewModel row) =>
        _main.RunAsync($"Updating {row.Name}", () => _main.Service.UpdateModAsync(row.Id, Progress()));

    private bool CanUpdateAll() => UpdateCount > 0;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAll()
    {
        if (await _main.UpdateModsAsync() == null) _main.SetStatus("Your mods are up to date.");
    }

    [RelayCommand]
    private void Uninstall(InstalledModViewModel row)
    {
        if (!_main.CheckIdle($"Removing {row.Name}")) return;
        List<InstalledMod> dependents = _main.Service.EnabledDependents(row.Id);
        string warning = dependents.Count > 0 ? $"\n\n{string.Join(", ", dependents.Select(d => d.DisplayName))} need it, so the launcher will download it again for them." : "";
        if (!_main.Dialogs.Confirm("Remove mod", $"Remove {row.Name} {row.Version} from the launcher?{warning}\n\nIts settings file (BepInEx\\config) is kept.", "REMOVE")) return;
        // Something may have started while the question was up.
        if (!_main.CheckIdle($"Removing {row.Name}")) return;
        _main.Service.Mods.Uninstall(row.Id);
        _main.SetStatus($"Removed {row.Name}.");
    }

    [RelayCommand]
    private void SetJoinBehaviour(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not InstalledModViewModel row || args[1] is not string behaviour) return;
        JoinBehaviour value = Enum.Parse<JoinBehaviour>(behaviour);
        _main.Service.Library.Update(row.Id, m => m.WhenJoining = value);
    }

    [RelayCommand]
    private void OpenModFolder(InstalledModViewModel row) => Format.Open(_main.Service.Store.VersionDirectory(row.Id, row.Mod.Version));

    [RelayCommand]
    private void OpenModPage(InstalledModViewModel row)
    {
        TsPackage? package = _main.Service.Index.Find(row.Id);
        Format.Open(package?.PackageUrl ?? row.Mod.WebsiteUrl);
    }

    [RelayCommand]
    private void AddLocal()
    {
        if (!_main.CheckIdle("Adding a mod")) return;
        string? file = _main.Dialogs.PickFile("Add a mod from your PC", "Mods (*.dll;*.zip)|*.dll;*.zip|All files|*.*");
        if (file == null) return;
        try
        {
            InstalledMod mod = LocalMods.Add(_main.Service.Mods, file);
            _main.SetStatus($"Added {mod.DisplayName} {mod.Version}. It's on.");
        }
        catch (Exception e)
        {
            _main.Dialogs.Error("Couldn't add the mod", e.Message);
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        string? folder = _main.Dialogs.PickFolder("Choose a BepInEx folder to import from", GuessImportFolder());
        if (folder == null) return;
        List<ImportCandidate> candidates = LocalMods.Scan(folder, _main.Service.Index);
        if (candidates.Count == 0)
        {
            _main.Dialogs.Info("Nothing to import", $"No mods in {folder}. Choose the BepInEx folder (the one with plugins in it).");
            return;
        }
        List<(ImportCandidate Candidate, bool FromThunderstore)>? picked = _main.Dialogs.PickImports(candidates, folder);
        if (picked == null || picked.Count == 0) return;
        await _main.RunAsync("Importing mods", async () =>
        {
            foreach ((ImportCandidate candidate, bool fromThunderstore) in picked)
            {
                if (fromThunderstore && candidate.Match != null) await _main.Service.InstallModAsync(candidate.Match.FullName, Progress());
                else LocalMods.Import(_main.Service.Mods, candidate);
            }
            int configs = LocalMods.CopyMissingConfigs(folder, _main.Service.Paths.RuntimeBepInEx);
            _main.SetStatus($"Imported {picked.Count} mod{(picked.Count == 1 ? "" : "s")}" + (configs > 0 ? $" and {configs} settings files." : "."));
        });
    }

    /// <summary>Rai Pal's BepInEx for Void Crew, read from the game's doorstop_config.ini, if there is one.</summary>
    private string? GuessImportFolder()
    {
        string? game = _main.Service.GameDirectory;
        if (game == null) return null;
        Core.Game.DoorstopState state = Core.Game.Doorstop.Inspect(game);
        if (state.ConfigTarget is { Length: > 0 } target && !state.InstalledByLauncher)
        {
            string path = Path.IsPathRooted(target) ? target : Path.Combine(game, target);
            string? bepInEx = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (bepInEx != null && Directory.Exists(bepInEx)) return bepInEx;
        }
        string inGame = Path.Combine(game, "BepInEx");
        return Directory.Exists(inGame) ? inGame : game;
    }

    private IProgress<InstallStatus> Progress() => new SyncProgress<InstallStatus>(s => _main.OnUi(() => _main.SetStatus(s.Message, s.Fraction)));

    // ---------------------------------------------------------------- BROWSE

    public void RefreshOnline()
    {
        LauncherService service = _main.Service;
        string? selected = SelectedPackage?.Id;
        _online.Clear();
        foreach (TsPackage package in service.Index.Mods)
        {
            _online.Add(new PackageViewModel(package, service.Library.Find(package.FullName)));
        }
        string current = Category;
        var categories = service.Index.Mods.SelectMany(p => p.Categories).Where(c => c != "Mods").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        Categories.Clear();
        Categories.Add("All categories");
        foreach (string category in categories) Categories.Add(category);
        Category = Categories.Contains(current) ? current : "All categories";
        OnlineView.Refresh();
        SelectedPackage = _online.FirstOrDefault(p => p.Id == selected);
    }

    private bool OnlineFilter(object item)
    {
        if (item is not PackageViewModel package) return false;
        if (!ShowDeprecated && package.IsDeprecated) return false;
        if (Category != "All categories" && !package.Package.Categories.Contains(Category, StringComparer.OrdinalIgnoreCase)) return false;
        return Matches(OnlineSearch, $"{package.Name} {package.Package.Owner} {package.Description}");
    }

    partial void OnOnlineSearchChanged(string value) => OnlineView.Refresh();
    partial void OnCategoryChanged(string value) => OnlineView.Refresh();
    partial void OnShowDeprecatedChanged(bool value) => OnlineView.Refresh();
    partial void OnSortChanged(string value) => ApplySort();

    private void ApplySort()
    {
        OnlineView.SortDescriptions.Clear();
        OnlineView.SortDescriptions.Add(Sort switch
        {
            "Top rated" => new SortDescription(nameof(PackageViewModel.Rating), ListSortDirection.Descending),
            "Recently updated" => new SortDescription(nameof(PackageViewModel.Updated), ListSortDirection.Descending),
            "Newest" => new SortDescription(nameof(PackageViewModel.Created), ListSortDirection.Descending),
            "Name" => new SortDescription(nameof(PackageViewModel.Name), ListSortDirection.Ascending),
            _ => new SortDescription(nameof(PackageViewModel.DownloadCount), ListSortDirection.Descending),
        });
    }

    partial void OnShowBrowseChanged(bool value)
    {
        if (value && _online.Count == 0) RefreshOnline();
    }

    partial void OnSelectedPackageChanged(PackageViewModel? value)
    {
        Readme = null;
        if (value == null) return;
        _ = FreshenAsync(value.Id);
        if (value.Icon == null && value.Package.Latest?.Icon is { } iconUrl) _ = SetIconAsync(iconUrl, icon => value.Icon = icon);
        int request = ++_readmeRequest;
        TsVersion? latest = value.Package.Latest;
        if (latest == null) return;
        _ = LoadReadmeAsync(value.Package, latest, request);
    }

    /// <summary>Checks the package shown in the details with Thunderstore (its list can be behind).</summary>
    private async Task FreshenAsync(string id)
    {
        try
        {
            await _main.Service.FreshenAsync(new[] { id });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Warn($"Checking {id} on Thunderstore failed: {e.Message}");
        }
    }

    private async Task LoadReadmeAsync(TsPackage package, TsVersion version, int request)
    {
        // A version from voidcrewmod.com has its README there (same API as Thunderstore's).
        string? site = version.FromWebsite ? Core.Website.WebsiteClient.Site : null;
        string? markdown = await _main.Service.Thunderstore.GetReadmeAsync(package.Owner, package.Name, version.VersionNumber, site);
        _main.OnUi(() =>
        {
            if (request == _readmeRequest) Readme = Markdown.ToDocument(markdown ?? version.Description);
        });
    }

    /// <summary>Icons for rows as they scroll into view.</summary>
    public void EnsureIcon(PackageViewModel package)
    {
        if (package.Icon != null || package.Package.Latest?.Icon is not { } url) return;
        _ = SetIconAsync(url, icon => package.Icon = icon);
    }

    [RelayCommand]
    private Task Install(PackageViewModel package)
    {
        if (package.IsInstalled && !package.HasUpdate)
        {
            ShowBrowse = false;
            return Task.CompletedTask;
        }
        return _main.RunAsync($"Installing {package.Name}", async () =>
        {
            if (package.HasUpdate) await _main.Service.UpdateModAsync(package.Id, Progress());
            else await _main.Service.InstallModAsync(package.Id, Progress());
            string version = _main.Service.Library.Find(package.Id)?.Version ?? package.LatestVersion;
            _main.SetStatus($"{package.Name} {version} is installed and on. It loads the next time you play or host.");
        });
    }

    [RelayCommand]
    private void OpenPackagePage(PackageViewModel package) => Format.Open(package.Package.PackageUrl);

    [RelayCommand]
    private void OpenPackageWebsite(PackageViewModel package) => Format.Open(package.Package.Latest?.WebsiteUrl);

    [RelayCommand]
    private async Task RefreshOnlineList()
    {
        if (!await _main.RunAsync("Refreshing Thunderstore's mod list", () => _main.Service.RefreshPackagesAsync(true))) return;
        if (_main.Service.Settings.AutoUpdateMods) await _main.UpdateModsAsync();
    }

    /// <summary>Newer data for some packages arrived: those rows change in place (the list keeps its place and selection).</summary>
    public void OnPackagesFreshened(IReadOnlyCollection<string> ids)
    {
        LauncherService service = _main.Service;
        foreach (string id in ids)
        {
            if (service.Index.Find(id) is not { } package) continue;
            PackageViewModel? row = _online.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                string before = row.LatestVersion;
                row.Replace(package);
                if (row == SelectedPackage && before != row.LatestVersion) OnSelectedPackageChanged(row);
            }
            else if (_online.Count > 0 && service.Index.Mods.Contains(package))
            {
                // A package Thunderstore's list left out.
                _online.Add(new PackageViewModel(package, service.Library.Find(package.FullName)));
            }
        }
        RefreshInstalled();
    }
}
