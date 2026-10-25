using VoidCrewModLauncher.Core.Bridge;
using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.GitHub;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Steam;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Core.Website;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core;

public enum LaunchMode
{
    /// <summary>Play with the mods turned on in the INSTALLED tab.</summary>
    Play,

    /// <summary>Same mods as Play, started to fill the server browser (the launcher stays in front if the player wants).</summary>
    Browse,

    /// <summary>Play's mods, then host a lobby with the HOST tab's settings.</summary>
    Host,

    /// <summary>A lobby's mods, then join it.</summary>
    Join,
}

/// <summary>The player has to agree before the launcher adds BepInEx's loader to the game folder.</summary>
public sealed class DoorstopConsentNeededException(string gameDirectory)
    : Exception(Loc.F("The launcher needs to add BepInEx's loader (winhttp.dll and doorstop_config.ini) to {0}.", gameDirectory))
{
    public string GameDirectory { get; } = gameDirectory;
}

/// <summary>The game folder has another install's Doorstop, too old to load the launcher's BepInEx.</summary>
public sealed class DoorstopOutdatedException(string gameDirectory, string version)
    : Exception(Loc.F("{0} has an old BepInEx loader (Doorstop {1}) that can't load the launcher's mods.", gameDirectory, version))
{
    public string GameDirectory { get; } = gameDirectory;
    public string Version { get; } = version;
}

/// <summary>
/// Everything the launcher does, for the UI to call: the mod library and Thunderstore, building and starting launches,
/// and the bridge to the running game. UI-free and long-lived; events may come from background threads.
/// </summary>
public sealed class LauncherService : IDisposable
{
    /// <param name="httpHandler">Answers Thunderstore's and GitHub's requests instead of the internet (tests).</param>
    public LauncherService(AppPaths paths, HttpMessageHandler? httpHandler = null)
    {
        Paths = paths;
        paths.EnsureCreated();
        Settings = LauncherSettings.Load(paths);
        Thunderstore = new ThunderstoreClient(paths, httpHandler);
        GitHub = new GitHubClient(paths, httpHandler);
        Website = new WebsiteClient(paths, httpHandler);
        Library = new ModLibrary(paths);
        Store = new PackageStore(paths, Thunderstore, GitHub);
        ReadVoidManagerTypes();
        Mods = new ModManager(Library, Store);
        Planner = new JoinPlanner(Mods);
        Runtime = new RuntimeBuilder(paths);
        Bridge = new BridgeServer(Settings.BridgeToken);
        Bridge.Connected += OnBridgeConnected;
    }

    public AppPaths Paths { get; }
    public LauncherSettings Settings { get; }
    public ThunderstoreClient Thunderstore { get; }
    public GitHubClient GitHub { get; }
    public WebsiteClient Website { get; }
    public ModLibrary Library { get; }
    public PackageStore Store { get; }
    public ModManager Mods { get; }
    public JoinPlanner Planner { get; }
    public RuntimeBuilder Runtime { get; }
    public BridgeServer Bridge { get; }

    public PackageIndex Index => Mods.Index;

    /// <summary>What the running game was started with (when the launcher started it).</summary>
    public LaunchFile? CurrentLaunch { get; private set; }

    /// <summary>When the last launch was started, to tell "starting" from "running without the launcher".</summary>
    public DateTime? LastLaunchUtc { get; private set; }

    public LaunchMode? LastLaunchMode { get; private set; }

    /// <summary>The package list was loaded again.</summary>
    public event Action? PackagesChanged;

    /// <summary>Newer data for some packages (by id) was merged into the list; the rest is unchanged.</summary>
    public event Action<IReadOnlyCollection<string>>? PackagesFreshened;

    public event Action<InstallStatus>? Status;

    /// <summary>The Void Crew folder: from Settings, else found through Steam.</summary>
    public string? GameDirectory
    {
        get
        {
            if (SteamLocator.IsGameDirectory(Settings.GameDirectory)) return Settings.GameDirectory;
            return _foundGame ??= SteamLocator.FindGame();
        }
    }

    private string? _foundGame;

    public void Start()
    {
        Bridge.Start();
        if (Settings.GameDirectory == null && GameDirectory != null) AppLog.Info($"Found Void Crew at {GameDirectory}.");
    }

    public void SaveSettings() => Settings.Save(Paths);

    /// <summary>
    /// Reads from the DLLs in the store the VoidManager type of installed mods that don't have one yet: mods installed
    /// before the launcher read it (0.8.1 and older), or whose plugin decides it while the game runs (read again, cheaply).
    /// </summary>
    private void ReadVoidManagerTypes()
    {
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledMod mod in Library.Mods.Where(m => m.VoidManagerType == null))
        {
            string folder = Store.VersionDirectory(mod.Id, mod.Version);
            if (Directory.Exists(folder) && PluginScanner.StrictestType(PluginScanner.ScanDirectory(folder)) is int type) types[mod.Id] = type;
        }
        if (types.Count == 0) return;
        Library.UpdateAll(mod =>
        {
            if (mod.VoidManagerType != null || !types.TryGetValue(mod.Id, out int type)) return false;
            mod.VoidManagerType = type;
            return true;
        });
    }

    /// <summary>
    /// Remembers which of the running game's mods joining players were checked for, from the advert the game put on
    /// the lobby it hosts, when the launcher can't tell that from the mods' DLLs (a mod that registers required game
    /// assets, or one VoidManager's own settings make "All"): the HOST tab then shows them as needed by everyone, and the
    /// next launch lists them so. True when something changed (settings are saved).
    /// </summary>
    public bool RememberCheckedMods(AdvertMessage advert)
    {
        List<LaunchMod>? launched = CurrentLaunch?.Mods;
        if (launched == null) return false;
        var required = new HashSet<string>(advert.Required ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        List<string> remembered = Settings.Host.CheckedInGame;
        bool changed = false;
        foreach (LaunchMod mod in launched.Where(m => !string.IsNullOrEmpty(m.Id)))
        {
            InstalledMod? mine = Library.Find(mod.Id);
            bool seenInDlls = mine != null && (mine.CheckedByVoidManager || mine.IsVoidManager);
            bool remember = required.Contains(mod.Id) && !seenInDlls;
            bool known = remembered.Contains(mod.Id, StringComparer.OrdinalIgnoreCase);
            if (remember == known) continue;
            if (remember)
            {
                remembered.Add(mod.Id);
                AppLog.Info($"Joining players are checked for {mod.Id} in the game; the HOST tab lists it for everyone from now on.");
            }
            else remembered.RemoveAll(id => string.Equals(id, mod.Id, StringComparison.OrdinalIgnoreCase));
            changed = true;
        }
        if (changed) SaveSettings();
        return changed;
    }

    /// <summary>
    /// Loads Thunderstore's package list, then checks every installed Thunderstore mod against the per-package API: the
    /// list is cached on Thunderstore's side and can be an hour or more behind, or miss a package.
    /// </summary>
    public async Task RefreshPackagesAsync(bool force, CancellationToken cancel = default)
    {
        // Packages merged while this refresh runs (a lobby's versions, say) go into the new list too.
        var mergedMeanwhile = new List<TsPackage>();
        lock (_refreshes) _refreshes.Add(mergedMeanwhile);
        try
        {
            Task<List<TsPackage>> siteTask = Website.GetPackagesAsync(force, cancel);
            List<TsPackage> packages;
            try
            {
                packages = await Thunderstore.GetPackagesAsync(force, cancel);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                // Thunderstore is down (or timed out): voidcrewmod.com's mods still work when it answered.
                if ((await siteTask).Count == 0) throw;
                AppLog.Warn($"Thunderstore couldn't be reached ({Loc.LogMessage(e)}); using voidcrewmod.com's mods only.");
                packages = new List<TsPackage>();
            }
            // voidcrewmod.com first: its versions and downloads win over Thunderstore's for a mod both have.
            var index = new PackageIndex(packages).Prefer(await siteTask);
            List<string> installed = Library.Mods.Where(m => m.Source == ModSource.Thunderstore).Select(m => m.Id).ToList();
            if (!Thunderstore.IsOffline) index = index.Merge(await Thunderstore.GetCurrentAsync(installed, cancel));
            index = index.Merge(await NewerOnGitHubAsync(installed.Select(index.Find).OfType<TsPackage>(), cancel));
            index = index.Merge(await GitHubOnlyAsync(GitHubModsInUse(), cancel));
            Mods.UpdateIndex(_ =>
            {
                lock (_refreshes) return index.Merge(mergedMeanwhile.ToList());
            });
        }
        finally
        {
            lock (_refreshes) _refreshes.Remove(mergedMeanwhile);
        }
        PackagesChanged?.Invoke();
    }

    /// <summary>For each refresh that is running, the packages merged since it started.</summary>
    private readonly List<List<TsPackage>> _refreshes = new();

    /// <summary>How many GitHub (or lobby) lookups run at once.</summary>
    private const int ParallelLookups = 4;

    /// <summary>How many of a lobby's mods are looked up (lobbies come from other players).</summary>
    private const int MaxLobbyLookups = 32;

    /// <summary>Runs <paramref name="work"/> for each item, a few at a time, and returns the answers that aren't null, in order.</summary>
    private static async Task<List<TResult>> FewAtATimeAsync<T, TResult>(IEnumerable<T> items, Func<T, Task<TResult?>> work, CancellationToken cancel) where TResult : class
    {
        using var slots = new SemaphoreSlim(ParallelLookups, ParallelLookups);
        TResult?[] results = await Task.WhenAll(items.Select(async item =>
        {
            await slots.WaitAsync(cancel);
            try
            {
                return await work(item);
            }
            finally
            {
                slots.Release();
            }
        }));
        return results.OfType<TResult>().ToList();
    }

    /// <summary>
    /// For packages whose Thunderstore page names a GitHub repo: the package with the repo's newest release added, when
    /// that release is newer than everything Thunderstore has. Off with Settings.GitHubUpdates.
    /// </summary>
    private async Task<List<TsPackage>> NewerOnGitHubAsync(IEnumerable<TsPackage> packages, CancellationToken cancel)
    {
        if (!Settings.GitHubUpdates) return new List<TsPackage>();
        return await FewAtATimeAsync<TsPackage, TsPackage>(packages.ToList(), async package =>
        {
            if (package.LatestOnThunderstore is not { } newest || GitHubRepo.Parse(newest.WebsiteUrl) is not { } repo) return null;
            TsVersion? release = await GitHub.GetLatestReleaseAsync(repo, package, cancel);
            if (release == null || VersionNumber.Compare(release.VersionNumber, newest.VersionNumber) <= 0 || !Usable(package, release)) return null;
            AppLog.Info($"{package.FullName} {release.VersionNumber} is on GitHub ({repo}); Thunderstore has {newest.VersionNumber}.");
            return package.WithVersions(new[] { release });
        }, cancel);
    }

    /// <summary>
    /// A GitHub version worth offering: not one that failed to install this session, and for plugin DLLs, each one
    /// replaces a file of the Thunderstore version when that version is already downloaded.
    /// </summary>
    private bool Usable(TsPackage package, TsVersion release)
    {
        if (Mods.IsRefused(release)) return false;
        if (release.GitHub is not { Package: null, Base: { } basis } github || !Store.Has(package.FullName, basis.VersionNumber)) return true;
        string folder = Store.VersionDirectory(package.FullName, basis.VersionNumber);
        var files = new HashSet<string>(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        if (github.Plugins.All(p => files.Contains(p.Name))) return true;
        AppLog.Warn($"{package.FullName} {release.VersionNumber} on GitHub ({github.Repo}) has DLLs that aren't files of the mod; not offering it.");
        return false;
    }

    /// <summary>
    /// Brings packages up to date from the per-package API, with the dependencies of their newest versions that the list
    /// doesn't have recent enough versions of.
    /// </summary>
    public async Task FreshenAsync(IEnumerable<string> ids, CancellationToken cancel = default)
    {
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<TsPackage>();
        List<string> wanted = ids.Where(id => !string.IsNullOrEmpty(id)).ToList();
        List<string> next = wanted;
        while (next.Count > 0 && asked.Count < 64)
        {
            List<TsPackage> found = await Thunderstore.GetCurrentAsync(next.Where(asked.Add), cancel);
            fresh.AddRange(found);
            PackageIndex known = Mods.Index.Merge(fresh);
            next = found.SelectMany(p => p.Latest?.Dependencies ?? new List<string>())
                .Select(PackageReference.Parse).OfType<PackageReference>()
                .Where(d => !PackageIndex.IsBepInExPack(d.Id) && !asked.Contains(d.Id)
                            && (known.Find(d.Id) is not { } package || VersionNumber.Compare(package.Latest?.VersionNumber, d.Version) < 0))
                .Select(d => d.Id).ToList();
        }
        PackageIndex withFresh = Mods.Index.Merge(fresh);
        fresh.AddRange(await NewerOnGitHubAsync(wanted.Select(withFresh.Find).OfType<TsPackage>(), cancel));
        fresh.AddRange(await GitHubOnlyAsync(wanted.Select(GitHubMods.Find).OfType<GitHubMod>(), cancel));
        Merge(fresh);
    }

    /// <summary>
    /// Looks up the exact versions a lobby runs that the package list doesn't have yet (or packages missing from it).
    /// True when something was found; read the lobby again with <see cref="Index"/> to see it.
    /// </summary>
    public async Task<bool> FreshenLobbyAsync(Lobby lobby, CancellationToken cancel = default)
    {
        // The lobby's list comes from another player: only real ids and version numbers, and only so many of them.
        List<LobbyMod> wanted = lobby.Mods
            .Where(m => !m.Local && m.PackageVersion == null && PackageReference.Parse(m.PackageId) != null && VersionNumber.IsValid(m.Version))
            .GroupBy(m => $"{m.PackageId}@{m.Version}", StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Take(MaxLobbyLookups).ToList();
        List<TsPackage> fresh = await FewAtATimeAsync<LobbyMod, TsPackage>(wanted, async mod =>
        {
            TsPackage? package = mod.Package ?? Index.Find(mod.PackageId) ?? (await Thunderstore.GetCurrentAsync(new[] { mod.PackageId! }, cancel)).FirstOrDefault();
            if (package == null || package.ListingRejected) return await FromRejectedListingAsync(mod, cancel);
            TsVersion? exact = await Thunderstore.GetVersionAsync(mod.PackageId!, mod.Version, cancel);
            // A host who got a newer version from the mod's GitHub releases: the same release.
            if (exact == null && Settings.GitHubUpdates && GitHubRepo.Parse(package.LatestOnThunderstore?.WebsiteUrl) is { } repo)
            {
                exact = await GitHub.GetReleaseAsync(repo, package, mod.Version, cancel);
                if (exact != null && !Usable(package, exact)) exact = null;
            }
            return exact != null ? package.WithVersions(new[] { exact }) : null;
        }, cancel);
        return Merge(fresh);
    }

    /// <summary>
    /// A lobby's mod that isn't listed for Void Crew because Thunderstore rejected the listing (it rejected Ship
    /// Customizer's): the package with the host's version from the mod's GitHub releases as its only version, never with
    /// Thunderstore's files, and only for authors the launcher knows (<see cref="ModPolicy.MayUseRejectedListing"/>). The
    /// release must be the package's own zip: plugin DLLs would go into the rejected listing's files. Null otherwise.
    /// </summary>
    private async Task<TsPackage?> FromRejectedListingAsync(LobbyMod mod, CancellationToken cancel)
    {
        if (!Settings.GitHubUpdates || !ModPolicy.MayUseRejectedListing(mod.PackageId)) return null;
        TsPackage? rejected = await Thunderstore.GetForGitHubAsync(mod.PackageId!, cancel);
        if (rejected is not { ListingRejected: true } || GitHubRepo.Parse(rejected.LatestOnThunderstore?.WebsiteUrl) is not { } repo) return null;
        TsVersion? release = await GitHub.GetReleaseAsync(repo, rejected, mod.Version, cancel);
        if (release?.GitHub?.Package == null || !Usable(rejected, release)) return null;
        AppLog.Info($"Thunderstore rejected {rejected.FullName}'s Void Crew listing; a lobby runs {release.VersionNumber}, which comes from its GitHub releases ({repo}).");
        return rejected.WithOnly(release);
    }

    /// <summary>Merges fresh package data into the list and tells the UI which packages changed.</summary>
    private bool Merge(List<TsPackage> fresh)
    {
        if (fresh.Count == 0) return false;
        bool changed = false;
        Mods.UpdateIndex(before =>
        {
            PackageIndex merged = before.Merge(fresh);
            changed = !ReferenceEquals(merged, before);
            // A refresh that is building a new list keeps these too.
            lock (_refreshes)
            {
                foreach (List<TsPackage> refresh in _refreshes) refresh.AddRange(fresh);
            }
            return merged;
        });
        if (!changed) return false;
        PackagesFreshened?.Invoke(fresh.Select(p => p.FullName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return true;
    }

    /// <summary>
    /// Installs a Thunderstore mod at its newest version (checked with Thunderstore first) and turns it on, with the
    /// GitHub-only mods it needs (UUVR for the VR UI Fix before 4.0.0).
    /// </summary>
    public async Task<InstalledMod> InstallModAsync(string id, IProgress<InstallStatus>? status, CancellationToken cancel = default)
    {
        await FreshenAsync(new[] { id }, cancel);
        InstalledMod mod = await Mods.InstallAsync(id, null, status, cancel);
        await AddNeededGitHubModsAsync(id, status, cancel);
        return mod;
    }

    /// <summary>Turns a mod on (with what it needs, GitHub-only mods included) or off.</summary>
    public async Task SetModEnabledAsync(string id, bool enabled, IProgress<InstallStatus>? status, CancellationToken cancel = default)
    {
        await Mods.SetEnabledAsync(id, enabled, status, cancel);
        if (enabled) await AddNeededGitHubModsAsync(id, status, cancel);
    }

    // ---------------------------------------------------------------- GitHub-only mods (UUVR)

    /// <summary>The GitHub-only mods the launcher knows and which mods need them.</summary>
    public GitHubModCatalog GitHubMods { get; init; } = GitHubModCatalog.Default;

    /// <summary>The copy of a GitHub-only mod in your mods: the launcher's own, or one with its plugin (imported from Rai Pal, say).</summary>
    public InstalledMod? CopyOf(GitHubMod mod) =>
        Library.Find(mod.Id) ?? Library.Mods.FirstOrDefault(m => m.PluginGuids.Any(g => g.StartsWith(mod.PluginGuidPrefix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>GitHub-only mods you have, or that one of your mods needs.</summary>
    private IEnumerable<GitHubMod> GitHubModsInUse() => GitHubMods.All.Where(m =>
        Library.Find(m.Id) != null || Library.Mods.Any(mine => GitHubMods.NeededBy(mine.Id, mine.Version).Contains(m)));

    /// <summary>The newest releases of GitHub-only mods, as packages for the list.</summary>
    private async Task<List<TsPackage>> GitHubOnlyAsync(IEnumerable<GitHubMod> mods, CancellationToken cancel) =>
        await FewAtATimeAsync<GitHubMod, TsPackage>(mods.ToList(), async mod =>
            await GitHub.GetModAsync(mod, cancel) is { Latest: { } latest } package && !Mods.IsRefused(latest) ? package : null, cancel);

    /// <summary>
    /// Installs the GitHub-only mods <paramref name="id"/> needs (UUVR for the VR UI Fix before 4.0.0) at their newest release, or turns
    /// your copy on. A copy you already have, such as UUVR imported from Rai Pal, is used instead of a second one.
    /// Returns "Name version" of each mod installed.
    /// </summary>
    public async Task<List<string>> AddNeededGitHubModsAsync(string id, IProgress<InstallStatus>? status, CancellationToken cancel = default)
    {
        var added = new List<string>();
        foreach (GitHubMod needed in GitHubMods.NeededBy(id, Library.Find(id)?.Version))
        {
            if (CopyOf(needed) is { } copy)
            {
                if (!copy.Enabled) Library.Update(copy.Id, m => m.Enabled = true);
                continue;
            }
            if (Index.Find(needed.Id) == null) Merge(await GitHubOnlyAsync(new[] { needed }, cancel));
            if (Index.Find(needed.Id) == null)
            {
                AppLog.Warn($"{needed.Name} couldn't be found on GitHub ({needed.Repo}); {id} needs it. The launcher tries again next time it starts.");
                continue;
            }
            status?.Report(new InstallStatus(Loc.F("Installing {0} from GitHub, which {1} needs", needed.Name, Library.Find(id)?.DisplayName ?? id)));
            InstalledMod mod = await Mods.InstallAsync(needed.Id, null, status, cancel);
            Library.Update(mod.Id, m => m.InstalledAsDependency = true);
            added.Add($"{mod.DisplayName} {mod.Version}");
        }
        return added;
    }

    /// <summary>The GitHub-only mods your turned-on mods need and you don't have yet (a download that failed before, say).</summary>
    public async Task<List<string>> AddMissingGitHubModsAsync(IProgress<InstallStatus>? status, CancellationToken cancel = default)
    {
        var added = new List<string>();
        foreach (InstalledMod mod in Library.Enabled.ToList())
        {
            if (GitHubMods.NeededBy(mod.Id, mod.Version).All(needed => CopyOf(needed) != null)) continue;
            added.AddRange(await AddNeededGitHubModsAsync(mod.Id, status, cancel));
        }
        return added;
    }

    /// <summary>Your turned-on mods that need <paramref name="id"/>: its dependents, and for a GitHub-only mod the mods that need it.</summary>
    public List<InstalledMod> EnabledDependents(string id)
    {
        List<InstalledMod> dependents = Mods.EnabledDependents(id);
        InstalledMod? mine = Library.Find(id);
        foreach (GitHubMod provided in GitHubMods.All.Where(g => mine != null && CopyOf(g)?.Id == mine.Id))
        {
            dependents.AddRange(Library.Enabled.Where(m => GitHubMods.NeededBy(m.Id, m.Version).Contains(provided) && !dependents.Contains(m)));
        }
        return dependents;
    }

    /// <summary>
    /// <paramref name="mods"/> without the GitHub-only mods another of them includes, your own copy (imported from Rai Pal,
    /// say) too: UUVR next to Void Crew VR 4.0.0 or newer would start VR a second time. Void Crew VR keeps it from starting,
    /// but its patcher would still copy UUVR's files into the game's folder on every start.
    /// </summary>
    public List<RuntimeMod> WithoutIncludedMods(List<RuntimeMod> mods)
    {
        var includers = new List<(RuntimeMod Mod, GitHubMod Included)>();
        foreach (RuntimeMod mod in mods)
        {
            foreach (GitHubMod included in GitHubMods.IncludedBy(mod.Id, mod.Version)) includers.Add((mod, included));
        }
        if (includers.Count == 0) return mods;

        var kept = new List<RuntimeMod>();
        foreach (RuntimeMod mod in mods)
        {
            (RuntimeMod Mod, GitHubMod Included) includer = includers.FirstOrDefault(i => IsCopy(mod, i.Included));
            if (includer.Mod == null)
            {
                kept.Add(mod);
                continue;
            }
            AppLog.Info($"Leaving {mod.Id} {mod.Version} out of this launch: {includer.Mod.Id} {includer.Mod.Version} includes {includer.Included.Name}.");
        }
        return kept;
    }

    private bool IsCopy(RuntimeMod mod, GitHubMod gitHubMod) =>
        string.Equals(mod.Id, gitHubMod.Id, StringComparison.OrdinalIgnoreCase) ||
        (Library.Find(mod.Id)?.PluginGuids.Any(g => g.StartsWith(gitHubMod.PluginGuidPrefix, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>Updates one installed mod to its newest version (checked with Thunderstore first).</summary>
    public async Task UpdateModAsync(string id, IProgress<InstallStatus>? status, CancellationToken cancel = default)
    {
        await FreshenAsync(new[] { id }, cancel);
        await Mods.UpdateAsync(id, status, cancel);
    }

    /// <summary>
    /// Updates every installed Thunderstore mod that has a newer version. One that fails doesn't stop the others.
    /// Returns "Name version" of each update, and the names of the mods that couldn't update.
    /// </summary>
    public async Task<(List<string> Updated, List<string> Failed)> UpdateModsAsync(IProgress<InstallStatus>? status, CancellationToken cancel = default)
    {
        var updated = new List<string>();
        var failed = new List<string>();
        foreach ((InstalledMod mod, TsVersion latest) in Mods.Updates())
        {
            try
            {
                await Mods.UpdateAsync(mod.Id, status, cancel);
                // A GitHub version that turned out unusable falls back to Thunderstore's, which may be the one you had.
                string now = Library.Find(mod.Id)?.Version ?? mod.Version;
                if (VersionNumber.Compare(now, mod.Version) > 0) updated.Add($"{mod.DisplayName} {now}");
                else failed.Add(mod.DisplayName);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                AppLog.Error($"Updating {mod.Id} to {latest.VersionNumber} failed", e);
                failed.Add(mod.DisplayName);
            }
        }
        return (updated, failed);
    }

    private void Report(string message, double? fraction = null) => Status?.Invoke(new InstallStatus(message, fraction));

    private IProgress<InstallStatus> StatusProgress() => new SyncProgress<InstallStatus>(s => Status?.Invoke(s));

    // ---------------------------------------------------------------- mod sets

    /// <summary>What joining players need of each of your mods when you host (the HOST tab shows the same).</summary>
    public HostNeeds CurrentHostNeeds() => new(Library.Enabled, Settings.Host.ModNeeds, Settings.Host.CheckedInGame);

    /// <summary>
    /// The mods of a Play or Host launch: every enabled mod and its dependencies, downloading what's missing. A mod that
    /// isn't on Thunderstore (one added from your PC, or no longer listed) brings the dependencies its manifest names.
    /// </summary>
    public async Task<List<RuntimeMod>> PlayModsAsync(CancellationToken cancel = default)
    {
        var mods = new List<RuntimeMod>();
        var roots = new List<(TsPackage, TsVersion)>();
        var unlistedNeed = new List<(TsPackage, TsVersion)>();
        HostNeeds needs = CurrentHostNeeds();
        foreach (InstalledMod mod in Library.Enabled)
        {
            ModNeed need = needs.Effective(mod);
            bool byUser = needs.Chosen(mod.Id) != null;
            TsPackage? package = mod.IsLocal ? null : Index.Find(mod.Id);
            TsVersion? version = package?.Version(mod.Version);
            if (package != null && version != null)
            {
                roots.Add((package, version));
                continue;
            }
            string folder = Store.VersionDirectory(mod.Id, mod.Version);
            if (Directory.Exists(folder))
            {
                mods.Add(new RuntimeMod(mod.Id, mod.Name, mod.Version, folder, mod.IsLocal, need, byUser));
                unlistedNeed.AddRange(Mods.DependencyRoots(mod));
            }
            else AppLog.Warn($"{mod.Id} {mod.Version} is turned on but isn't in the store any more; leaving it out.");
        }
        // After your own mods, so a dependency you have turned on loads at your version.
        roots.AddRange(unlistedNeed);

        var enabledIds = new HashSet<string>(Library.Enabled.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        foreach ((TsPackage package, TsVersion version) in Mods.Resolve(roots))
        {
            // An enabled copy Thunderstore no longer lists is already in; don't load a second version next to it.
            if (mods.Any(m => string.Equals(m.Id, package.FullName, StringComparison.OrdinalIgnoreCase))) continue;
            if (!enabledIds.Contains(package.FullName)) AppLog.Info($"Also loading {package.FullName} {version.VersionNumber}: another enabled mod needs it.");
            (string folder, TsVersion used) = await Mods.EnsureAsync(package, version, new SyncProgress<double>(f => Report(Loc.F("Downloading {0} {1}", package.DisplayName, version.VersionNumber), f)), cancel);
            bool byUser = needs.Chosen(package.FullName) != null;
            // A dependency you have is judged like your own mods (its DLLs say what VoidManager checks); else from its page.
            ModNeed need = Library.Find(package.FullName) is { } installed ? needs.Effective(installed)
                : needs.Chosen(package.FullName) ?? ModPolicy.GuessNeed(package.FullName, package.Categories);
            mods.Add(new RuntimeMod(package.FullName, package.Name, used.VersionNumber, folder, false, need, byUser));
        }

        // GitHub-only mods your turned-on mods need (UUVR for the VR UI Fix before 4.0.0) load with them, like dependencies.
        foreach (GitHubMod needed in Library.Enabled.SelectMany(m => GitHubMods.NeededBy(m.Id, m.Version)).Distinct())
        {
            if (CopyOf(needed) is not { Enabled: false } copy || mods.Any(m => string.Equals(m.Id, copy.Id, StringComparison.OrdinalIgnoreCase))) continue;
            string folder = Store.VersionDirectory(copy.Id, copy.Version);
            if (!Directory.Exists(folder)) continue;
            AppLog.Info($"Also loading {copy.Id} {copy.Version}: another enabled mod needs it.");
            mods.Add(new RuntimeMod(copy.Id, copy.Name, copy.Version, folder, copy.IsLocal, needs.Effective(copy)));
        }
        return Mods.WithoutDuplicatePlugins(mods);
    }

    // ---------------------------------------------------------------- launching

    /// <summary>Starts Void Crew for Play, Browse or Host with your enabled mods. The game must not be running.</summary>
    public async Task LaunchAsync(LaunchMode mode, HostCommand? host = null, CancellationToken cancel = default)
    {
        if (mode == LaunchMode.Join) throw new ArgumentException("Use JoinAsync to join a lobby.", nameof(mode));
        List<RuntimeMod> mods = await PlayModsAsync(cancel);
        await LaunchWithAsync(mode, mods, host, null, cancel);
    }

    /// <summary>Downloads what the join needs, then starts Void Crew with the lobby's mods to join it.</summary>
    public async Task JoinAsync(Lobby lobby, ResolvedJoin join, CancellationToken cancel = default)
    {
        int index = 0;
        foreach ((TsPackage package, TsVersion version) in join.Downloads)
        {
            index++;
            string label = Loc.F("Downloading {0} {1} ({2}/{3})", package.DisplayName, version.VersionNumber, index, join.Downloads.Count);
            Report(label, 0);
            await Store.EnsureAsync(version, new SyncProgress<double>(f => Report(label, f)), cancel);
        }
        var command = new JoinCommand
        {
            RoomId = lobby.Id,
            Region = lobby.Region,
            RoomName = lobby.Name,
            Modded = lobby.IsSessionModded,
        };
        await LaunchWithAsync(LaunchMode.Join, join.Mods, null, command, cancel);
    }

    private async Task LaunchWithAsync(LaunchMode mode, List<RuntimeMod> mods, HostCommand? host, JoinCommand? join, CancellationToken cancel)
    {
        string game = GameDirectory ?? throw new InvalidOperationException(Loc.T("Void Crew wasn't found. Choose its folder on the SETTINGS tab.")).InEnglish();
        if (GameProcess.IsRunning()) throw new InvalidOperationException(Loc.T("Void Crew is already running. Close it first.")).InEnglish();
        mods = mods.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        mods = WithoutIncludedMods(mods);

        string bepInExPack = await Mods.EnsureBepInExAsync(StatusProgress(), cancel);
        DoorstopState doorstop = Doorstop.Inspect(game);
        if (!doorstop.HasLoader)
        {
            if (!Settings.DoorstopConsent) throw new DoorstopConsentNeededException(game);
            Doorstop.InstallIfMissing(game, bepInExPack);
        }
        else if (doorstop.Outdated)
        {
            // An old Doorstop (3.x) from a manual BepInEx install never loads the launcher's BepInEx: the game would start
            // without mods. It is another install's, so the player says yes once; its files are kept next to the new ones.
            if (!Settings.ReplaceOutdatedDoorstop) throw new DoorstopOutdatedException(game, doorstop.Version ?? "?");
            Doorstop.ReplaceOutdated(game, bepInExPack);
        }

        var launch = new LaunchFile
        {
            LaunchId = Guid.NewGuid().ToString("N"),
            LauncherVersion = AppInfo.Version,
            Mode = mode.ToString().ToLowerInvariant(),
            Ports = BridgeDefaults.Ports.ToList(),
            Token = Settings.BridgeToken,
            Mods = mods.Select(m => new LaunchMod
            {
                Id = m.Id,
                Name = m.Name.Replace('_', ' '),
                Version = m.Version,
                Need = LobbyAdvert.NeedLetter(m.Need),
                NeedSetByUser = m.NeedSetByUser,
                Local = m.Local,
            }).ToList(),
            Host = host,
            Join = join,
        };

        Report(Loc.T("Preparing BepInEx and the mods"));
        await Task.Run(() => Runtime.Build(bepInExPack, mods, launch, Settings.ShowBepInExConsole), cancel);
        GameProcess.Start(game, Paths.Preloader, Settings.LaunchThroughSteam);
        UseLaunch(launch);
        LastLaunchUtc = DateTime.UtcNow;
        LastLaunchMode = mode;
        Report(mode switch
        {
            LaunchMode.Join => Loc.F("Starting Void Crew to join {0}", join?.RoomName),
            LaunchMode.Host => Loc.T("Starting Void Crew to host your lobby"),
            LaunchMode.Browse => Loc.T("Starting Void Crew for the server browser"),
            _ => Loc.T("Starting Void Crew"),
        });
    }

    /// <summary>Closes the game (through the bridge when connected, else like closing its window) and waits for it.</summary>
    public async Task<bool> CloseGameAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        if (!GameProcess.IsRunning()) return true;
        Report(Loc.T("Closing Void Crew"));
        if (!await Bridge.SendAsync(new Message { Type = MessageTypes.Quit })) GameProcess.RequestClose();
        return await GameProcess.WaitForExitAsync(timeout, cancel);
    }

    /// <summary>The running game's mods match the join (no restart needed) and it is connected to the launcher.</summary>
    public bool CanJoinWithoutRestart(ResolvedJoin join) =>
        Bridge.IsConnected && join.Downloads.Count == 0 && JoinPlanner.RunningGameFits(join, CurrentLaunch);

    /// <summary>The running game has exactly the Play mods, listed for joining players as now (so hosting needs no restart).</summary>
    public async Task<bool> RunningGameHasPlayModsAsync()
    {
        if (!Bridge.IsConnected || CurrentLaunch?.Mods == null) return false;
        return RunningGameHosts(await PlayModsAsync());
    }

    /// <summary>
    /// Whether the running game would host with <paramref name="play"/>: the same mods at the same versions, each with the
    /// need letter and "chosen on the HOST tab" it was started with. The bridge reads those from launch.json once, when
    /// the game starts, and lists the lobby's mods from them, so a choice changed since then only shows after a restart.
    /// A mod joining players are checked for is listed for everyone either way, so only its version counts (the launcher
    /// learns some of those from the game when you host, which mustn't cost a restart the next time).
    /// </summary>
    internal bool RunningGameHosts(IReadOnlyCollection<RuntimeMod> play)
    {
        List<LaunchMod>? launched = CurrentLaunch?.Mods;
        if (launched == null) return false;
        var running = new Dictionary<string, LaunchMod>(StringComparer.OrdinalIgnoreCase);
        foreach (LaunchMod mod in launched) running.TryAdd(mod.Id, mod);
        if (play.Count != running.Count) return false;
        HostNeeds needs = CurrentHostNeeds();
        foreach (RuntimeMod mod in play)
        {
            if (!running.TryGetValue(mod.Id, out LaunchMod? loaded) || !VersionNumber.Same(loaded.Version, mod.Version)) return false;
            if (Library.Find(mod.Id) is { } mine && needs.Checked(mine)) continue;
            // As the bridge reads them (LaunchWithAsync writes the letter with LobbyAdvert.NeedLetter).
            if (LobbyAdvert.ParseNeed(loaded.Need) != LobbyAdvert.ParseNeed(LobbyAdvert.NeedLetter(mod.Need))) return false;
            if (loaded.NeedSetByUser != mod.NeedSetByUser) return false;
        }
        return true;
    }

    private void OnBridgeConnected(HelloMessage hello)
    {
        // After a launcher restart, learn what the running game was started with from launch.json.
        if (CurrentLaunch?.LaunchId == hello.LaunchId) return;
        LaunchFile? file = null;
        try
        {
            if (File.Exists(Paths.RuntimeLaunchFile))
            {
                file = System.Text.Json.JsonSerializer.Deserialize<LaunchFile>(File.ReadAllText(Paths.RuntimeLaunchFile), JsonFiles.Protocol);
            }
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not read {Paths.RuntimeLaunchFile}: {e.Message}");
        }
        UseLaunch(file?.LaunchId == hello.LaunchId ? file : null);
    }

    /// <summary>The running game was started with <paramref name="launch"/> (tests use this without starting it).</summary>
    internal void UseLaunch(LaunchFile? launch)
    {
        CurrentLaunch = launch;
        RememberKnownVersions();
    }

    // ---------------------------------------------------------------- updates while the game runs

    /// <summary>For each mod of the running game, the newest version known when it started: later ones are news.</summary>
    private Dictionary<string, string> _knownAtLaunch = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _knownGate = new();

    private void RememberKnownVersions()
    {
        lock (_knownGate)
        {
            _knownAtLaunch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (LaunchMod mod in CurrentLaunch?.Mods ?? new List<LaunchMod>())
            {
                if (!mod.Local) _knownAtLaunch[mod.Id] = Newest(mod.Version, NewestKnown(mod.Id));
            }
        }
    }

    /// <summary>The newest version of a mod the launcher knows of: yours, or on Thunderstore or GitHub.</summary>
    private string? NewestKnown(string id)
    {
        InstalledMod? mine = Library.Find(id);
        string? yours = mine is { IsLocal: false } ? mine.Version : null;
        return Newest(yours, Index.Find(id)?.Latest?.VersionNumber);
    }

    private static string Newest(string? a, string? b) =>
        string.IsNullOrEmpty(a) ? b ?? "" : string.IsNullOrEmpty(b) ? a : VersionNumber.Compare(a, b) >= 0 ? a : b;

    /// <summary>
    /// Updates that came out while the game runs, for the mods it was started with (each one once). Ready means it is
    /// already downloaded and on in your mods, so starting the game from the launcher loads it.
    /// </summary>
    public List<(string Name, string Version, bool Ready)> NewUpdatesForRunningGame()
    {
        var news = new List<(string, string, bool)>();
        if (CurrentLaunch?.Mods == null) return news;
        lock (_knownGate)
        {
            foreach (LaunchMod running in CurrentLaunch.Mods.Where(m => !m.Local))
            {
                string? newest = NewestKnown(running.Id);
                string known = _knownAtLaunch.TryGetValue(running.Id, out string? k) ? k : running.Version;
                if (string.IsNullOrEmpty(newest) || VersionNumber.Compare(newest, known) <= 0 || VersionNumber.Compare(newest, running.Version) <= 0) continue;
                _knownAtLaunch[running.Id] = newest;
                bool ready = Library.Find(running.Id) is { } mine && VersionNumber.Same(mine.Version, newest) && Store.Has(mine.Id, mine.Version);
                news.Add((running.Name, newest, ready));
            }
        }
        return news;
    }

    public void Dispose()
    {
        Bridge.Dispose();
        Thunderstore.Dispose();
        GitHub.Dispose();
        Website.Dispose();
    }
}

/// <summary>An IProgress that reports on the calling thread (no SynchronizationContext capture).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
