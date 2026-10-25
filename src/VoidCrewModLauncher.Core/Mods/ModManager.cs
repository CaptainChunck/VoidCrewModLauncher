using VoidCrewModLauncher.Core.Game;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Core.Mods;

/// <summary>A step of an install, for the status bar.</summary>
public readonly record struct InstallStatus(string Message, double? Fraction = null);

/// <summary>
/// Installing, updating, removing and switching mods on and off, with their Thunderstore dependencies. Downloads go
/// into the <see cref="PackageStore"/>; the <see cref="ModLibrary"/> records what you have and play with.
/// </summary>
public sealed class ModManager
{
    private readonly ModLibrary _library;
    private readonly PackageStore _store;

    public ModManager(ModLibrary library, PackageStore store)
    {
        _library = library;
        _store = store;
    }

    private readonly object _indexGate = new();
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private PackageIndex _index = PackageIndex.Empty;

    public PackageIndex Index
    {
        get => _index;
        set
        {
            lock (_indexGate) _index = value;
        }
    }

    /// <summary>Changes the index without losing a change made at the same time (from another thread).</summary>
    public PackageIndex UpdateIndex(Func<PackageIndex, PackageIndex> change)
    {
        lock (_indexGate) return _index = change(_index);
    }

    public ModLibrary Library => _library;

    public PackageStore Store => _store;

    /// <summary>A GitHub version that failed to install (its files weren't the package): not offered again this session.</summary>
    public bool IsRefused(TsVersion version)
    {
        lock (_refused) return version.GitHub != null && _refused.Contains(version.FullName);
    }

    private void Refuse(TsPackage package, TsVersion version, Exception e)
    {
        AppLog.Warn($"{package.FullName} {version.VersionNumber} from GitHub couldn't be used ({Loc.LogMessage(e)}); the launcher won't offer it again until it restarts.");
        lock (_refused) _refused.Add(version.FullName);
        UpdateIndex(index => index.WithoutVersion(package.FullName, version.VersionNumber));
    }

    /// <summary>
    /// Downloads one version into the store and returns its folder and the version it holds: a GitHub version that fails
    /// (not cancelled) is refused for the session and the newest Thunderstore version is used instead.
    /// </summary>
    public async Task<(string Folder, TsVersion Version)> EnsureAsync(TsPackage package, TsVersion version, IProgress<double>? progress, CancellationToken cancel)
    {
        try
        {
            return (await _store.EnsureAsync(version, progress, cancel), version);
        }
        catch (Exception e) when (version.GitHub != null && !cancel.IsCancellationRequested && package.LatestOnThunderstore is { } fallback)
        {
            Refuse(package, version, e);
            AppLog.Info($"Using {package.FullName} {fallback.VersionNumber} from Thunderstore instead.");
            return (await _store.EnsureAsync(fallback, progress, cancel), fallback);
        }
        catch (Exception e) when (version.GitHub != null && !cancel.IsCancellationRequested)
        {
            Refuse(package, version, e);
            throw;
        }
    }

    /// <summary>Installs a Thunderstore mod (the latest version unless one is given) with its dependencies, and turns it on.</summary>
    public async Task<InstalledMod> InstallAsync(string id, string? version = null, IProgress<InstallStatus>? status = null, CancellationToken cancel = default)
    {
        TsPackage package = Index.Find(id) ?? throw new InvalidOperationException(Loc.F("{0} isn't on Thunderstore's Void Crew list.", id)).InEnglish();
        TsVersion chosen = (version != null ? package.Version(version) : null) ?? Newest(package)
            ?? throw new InvalidOperationException(Loc.F("{0} has no versions.", package.FullName)).InEnglish();

        List<(TsPackage Package, TsVersion Version)> plan = Resolve(new[] { (package, chosen) });
        int step = 0;
        foreach ((TsPackage item, TsVersion planned) in plan)
        {
            step++;
            string text = Loc.F("Downloading {0} {1} ({2}/{3})", item.DisplayName, planned.VersionNumber, step, plan.Count);
            status?.Report(new InstallStatus(text, 0));
            var progress = new Progress<double>(f => status?.Report(new InstallStatus(text, f)));
            (string folder, TsVersion itemVersion) = await EnsureAsync(item, planned, progress, cancel);
            if (ReferenceEquals(item, package)) chosen = itemVersion;

            InstalledMod? existing = _library.Find(item.FullName);
            bool isTarget = ReferenceEquals(item, package);
            if (existing != null && !isTarget && VersionNumber.Compare(existing.Version, itemVersion.VersionNumber) >= 0 && _store.Has(existing.Id, existing.Version))
            {
                if (!existing.Enabled) _library.Update(existing.Id, m => m.Enabled = true);
                continue;
            }
            InstalledMod mod = Describe(item, itemVersion, folder);
            mod.Enabled = true;
            mod.InstalledAsDependency = !isTarget && (existing?.InstalledAsDependency ?? true);
            if (existing != null)
            {
                mod.WhenJoining = existing.WhenJoining;
                mod.InstalledUtc = existing.InstalledUtc;
            }
            _library.AddOrReplace(mod);
            AppLog.Info($"Installed {mod.Id} {mod.Version}{(mod.InstalledAsDependency ? " (dependency)" : "")}.");
        }
        status?.Report(new InstallStatus(Loc.F("Installed {0} {1}.", package.DisplayName, chosen.VersionNumber)));
        return _library.Find(package.FullName)!;
    }

    /// <summary>
    /// The packages to download for <paramref name="roots"/>: the roots and all their dependencies, dependencies first.
    /// A dependency uses the version you already have when it's new enough, otherwise Thunderstore's latest.
    /// BepInEx itself is left out; the launcher keeps its own copy.
    /// </summary>
    public List<(TsPackage Package, TsVersion Version)> Resolve(IEnumerable<(TsPackage Package, TsVersion Version)> roots, IReadOnlyDictionary<string, string>? pinned = null)
    {
        var ordered = new List<(TsPackage, TsVersion)>();
        var chosen = new Dictionary<string, TsVersion>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(TsPackage package, TsVersion version)
        {
            if (PackageIndex.IsBepInExPack(package.FullName)) return;
            if (chosen.ContainsKey(package.FullName) || !visiting.Add(package.FullName)) return;
            foreach (string dependency in version.Dependencies)
            {
                PackageReference? reference = PackageReference.Parse(dependency);
                if (reference == null || PackageIndex.IsBepInExPack(reference.Value.Id)) continue;
                TsPackage? dependencyPackage = Index.Find(reference.Value.Id);
                if (dependencyPackage == null)
                {
                    AppLog.Warn($"{package.FullName} needs {dependency}, which isn't on Thunderstore's Void Crew list any more.");
                    continue;
                }
                Visit(dependencyPackage, PickVersion(dependencyPackage, reference.Value.Version, pinned));
            }
            chosen[package.FullName] = version;
            ordered.Add((package, version));
        }

        foreach ((TsPackage package, TsVersion version) in roots)
        {
            Visit(package, version);
        }
        return ordered;
    }

    /// <summary>
    /// The packages a mod that isn't resolved through Thunderstore's list needs (one added from your PC, or no longer
    /// listed), from the dependencies its manifest names, each at the version <see cref="Resolve"/> picks for a
    /// dependency: roots to resolve with the launch's other mods.
    /// </summary>
    public List<(TsPackage Package, TsVersion Version)> DependencyRoots(InstalledMod mod, IReadOnlyDictionary<string, string>? pinned = null)
    {
        var roots = new List<(TsPackage, TsVersion)>();
        foreach (string dependency in mod.Dependencies)
        {
            PackageReference? reference = PackageReference.Parse(dependency);
            if (reference == null || PackageIndex.IsBepInExPack(reference.Value.Id)) continue;
            TsPackage? package = Index.Find(reference.Value.Id);
            if (package == null)
            {
                // Once: the JOIN dialog works the mods out again whenever a tick changes.
                bool told;
                lock (_told) told = !_told.Add($"{mod.Id}|{dependency}");
                if (!told) AppLog.Warn($"{mod.Id} needs {dependency}, which isn't on Thunderstore's Void Crew list; it is left out.");
                continue;
            }
            roots.Add((package, PickVersion(package, reference.Value.Version, pinned)));
        }
        return roots;
    }

    private TsVersion PickVersion(TsPackage package, string? minimum, IReadOnlyDictionary<string, string>? pinned)
    {
        if (pinned != null && pinned.TryGetValue(package.FullName, out string? pin) && package.Version(pin) is { } pinnedVersion) return pinnedVersion;
        InstalledMod? installed = _library.Find(package.FullName);
        if (installed != null && (minimum == null || VersionNumber.Compare(installed.Version, minimum) >= 0) && package.Version(installed.Version) is { } current)
        {
            return current;
        }
        return Newest(package)!;
    }

    /// <summary>The package's newest version that hasn't been refused (a GitHub release that wasn't the package).</summary>
    private TsVersion? Newest(TsPackage package) =>
        package.Versions.Where(v => !IsRefused(v)).MaxBy(v => v.VersionNumber, VersionNumber.Comparer) ?? package.Latest;

    public static InstalledMod Describe(TsPackage package, TsVersion version, string folder)
    {
        List<PluginInfo> plugins = PluginScanner.ScanDirectory(folder);
        return new InstalledMod
        {
            Id = package.FullName,
            Source = package.IsGitHubOnly ? ModSource.GitHub : ModSource.Thunderstore,
            Name = package.Name,
            Author = package.Owner,
            Version = version.VersionNumber,
            Description = version.Description,
            WebsiteUrl = string.IsNullOrEmpty(version.WebsiteUrl) ? package.PackageUrl : version.WebsiteUrl,
            IconUrl = version.Icon,
            GitHubRelease = version.GitHub?.ReleaseUrl,
            Dependencies = version.Dependencies.ToList(),
            Categories = package.Categories.ToList(),
            PluginGuids = plugins.Select(p => p.Guid).Distinct().ToList(),
            VoidManagerType = PluginScanner.StrictestType(plugins),
            InstalledUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Installed mods whose version is behind Thunderstore's latest.</summary>
    public List<(InstalledMod Mod, TsVersion Latest)> Updates()
    {
        var updates = new List<(InstalledMod, TsVersion)>();
        foreach (InstalledMod mod in _library.Mods.Where(m => m.Source is ModSource.Thunderstore or ModSource.GitHub))
        {
            TsVersion? latest = Index.Find(mod.Id) is { } package ? Newest(package) : null;
            if (latest != null && IsRefused(latest)) continue;
            if (latest != null && VersionNumber.Compare(latest.VersionNumber, mod.Version) > 0) updates.Add((mod, latest));
        }
        return updates;
    }

    public async Task UpdateAsync(string id, IProgress<InstallStatus>? status = null, CancellationToken cancel = default)
    {
        InstalledMod? before = _library.Find(id);
        if (before == null) return;
        bool wasEnabled = before.Enabled;
        bool wasDependency = before.InstalledAsDependency;
        await InstallAsync(id, null, status, cancel);
        _library.Update(id, m =>
        {
            m.Enabled = wasEnabled;
            m.InstalledAsDependency = wasDependency;
            m.WhenJoining = before.WhenJoining;
        });
    }

    /// <summary>Turns a mod on (with its dependencies, installing any that are missing) or off.</summary>
    public async Task SetEnabledAsync(string id, bool enabled, IProgress<InstallStatus>? status = null, CancellationToken cancel = default)
    {
        InstalledMod? mod = _library.Find(id);
        if (mod == null) return;
        if (!enabled || mod.IsLocal)
        {
            _library.Update(id, m => m.Enabled = enabled);
            return;
        }
        TsPackage? package = Index.Find(id);
        TsVersion? version = package?.Version(mod.Version);
        if (package == null || version == null)
        {
            _library.Update(id, m => m.Enabled = true);
            return;
        }
        foreach ((TsPackage dependency, TsVersion dependencyVersion) in Resolve(new[] { (package, version) }))
        {
            if (string.Equals(dependency.FullName, id, StringComparison.OrdinalIgnoreCase)) continue;
            InstalledMod? installed = _library.Find(dependency.FullName);
            if (installed == null || !_store.Has(installed.Id, installed.Version))
            {
                await InstallAsync(dependency.FullName, dependencyVersion.VersionNumber, status, cancel);
                _library.Update(dependency.FullName, m => m.InstalledAsDependency = installed?.InstalledAsDependency ?? true);
            }
            else if (!installed.Enabled)
            {
                _library.Update(installed.Id, m => m.Enabled = true);
            }
        }
        _library.Update(id, m => m.Enabled = true);
    }

    /// <summary>
    /// <paramref name="mods"/> without the copies BepInEx wouldn't load: when two of a launch's mods carry the same plugin
    /// (both of VoidManager's packages, say), BepInEx loads the newest version of it, the first folder on a tie, and skips
    /// the other. A mod none of whose plugins would load is left out of the launch too, so it isn't copied or listed in
    /// your lobby; your INSTALLED tab stays as you set it. Mods the library doesn't know the plugins of are all kept.
    /// </summary>
    public List<RuntimeMod> WithoutDuplicatePlugins(List<RuntimeMod> mods)
    {
        var holders = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < mods.Count; i++)
        {
            foreach (string guid in _library.Find(mods[i].Id)?.PluginGuids ?? new List<string>())
            {
                if (!holders.TryGetValue(guid, out List<int>? list)) holders[guid] = list = new List<int>();
                if (!list.Contains(i)) list.Add(i);
            }
        }
        // For each mod sharing a plugin: the plugin versions in its files (its own version when they can't be read).
        var versions = new Dictionary<int, Dictionary<string, string>>();
        string PluginVersion(int at, string guid)
        {
            if (!versions.TryGetValue(at, out Dictionary<string, string>? found))
            {
                found = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (PluginInfo plugin in PluginScanner.ScanDirectory(mods[at].Folder)) found.TryAdd(plugin.Guid, plugin.Version);
                versions[at] = found;
            }
            return found.TryGetValue(guid, out string? version) && version.Length > 0 ? version : mods[at].Version;
        }

        var loses = new Dictionary<int, int>();
        var winners = new Dictionary<int, (int Winner, string Guid)>();
        foreach ((string guid, List<int> list) in holders.Where(h => h.Value.Count > 1))
        {
            int winner = list.OrderByDescending(i => PluginVersion(i, guid), VersionNumber.Comparer)
                .ThenBy(i => PackageStore.SafeName(mods[i].Id), StringComparer.OrdinalIgnoreCase).First();
            foreach (int loser in list.Where(i => i != winner))
            {
                loses[loser] = loses.GetValueOrDefault(loser) + 1;
                winners[loser] = (winner, guid);
            }
        }
        var kept = new List<RuntimeMod>();
        for (int i = 0; i < mods.Count; i++)
        {
            int plugins = _library.Find(mods[i].Id)?.PluginGuids.Distinct(StringComparer.Ordinal).Count() ?? 0;
            if (plugins == 0 || loses.GetValueOrDefault(i) < plugins)
            {
                kept.Add(mods[i]);
                continue;
            }
            (int winner, string guid) = winners[i];
            lock (_told)
            {
                if (!_told.Add($"{mods[i].Id}|{mods[winner].Id}")) continue;
            }
            AppLog.Info($"{mods[i].Id} {mods[i].Version} and {mods[winner].Id} {mods[winner].Version} carry the same plugin ({guid}): loading only " +
                        $"{mods[winner].Id}, the copy BepInEx would load.");
        }
        return kept;
    }

    /// <summary>What <see cref="DependencyRoots"/> and <see cref="WithoutDuplicatePlugins"/> have written to the log (once each a session).</summary>
    private readonly HashSet<string> _told = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enabled mods that need <paramref name="id"/> (to warn before turning it off or removing it).</summary>
    public List<InstalledMod> EnabledDependents(string id) =>
        _library.Enabled.Where(m => !string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)
                                    && m.Dependencies.Any(d => string.Equals(PackageReference.Parse(d)?.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public void Uninstall(string id)
    {
        _library.Remove(id);
        _store.RemoveAll(id);
        AppLog.Info($"Removed {id}.");
    }

    /// <summary>The newest BepInExPack in the store, downloading it when there is none (or a newer one is out).</summary>
    public async Task<string> EnsureBepInExAsync(IProgress<InstallStatus>? status = null, CancellationToken cancel = default)
    {
        TsVersion? latest = Index.Find(PackageIndex.BepInExPackId)?.Latest;
        string? newest = _store.Versions(PackageIndex.BepInExPackId).OrderByDescending(v => v, VersionNumber.Comparer).FirstOrDefault();
        if (latest != null)
        {
            if (!_store.Has(PackageIndex.BepInExPackId, latest.VersionNumber)) status?.Report(new InstallStatus(Loc.F("Downloading BepInEx {0}", latest.VersionNumber), 0));
            try
            {
                return await _store.EnsureAsync(latest, new Progress<double>(f => status?.Report(new InstallStatus(Loc.F("Downloading BepInEx {0}", latest.VersionNumber), f))), cancel);
            }
            catch (Exception e) when (newest != null && !cancel.IsCancellationRequested)
            {
                // Offline (or the download failed): the BepInEx already downloaded still plays.
                AppLog.Warn($"BepInEx {latest.VersionNumber} couldn't be downloaded ({Loc.LogMessage(e)}); using BepInEx {newest}.");
            }
        }
        if (newest != null) return _store.VersionDirectory(PackageIndex.BepInExPackId, newest);
        throw new InvalidOperationException(Loc.T("BepInEx isn't downloaded yet and Thunderstore can't be reached. Connect to the internet and try again.")).InEnglish();
    }
}
