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
        AppLog.Warn($"{package.FullName} {version.VersionNumber} from GitHub couldn't be used ({e.Message}); the launcher won't offer it again until it restarts.");
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
        TsPackage package = Index.Find(id) ?? throw new InvalidOperationException(Loc.F("{0} isn't on Thunderstore's Void Crew list.", id));
        TsVersion chosen = (version != null ? package.Version(version) : null) ?? Newest(package)
            ?? throw new InvalidOperationException(Loc.F("{0} has no versions.", package.FullName));

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

    public static InstalledMod Describe(TsPackage package, TsVersion version, string folder) => new()
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
        PluginGuids = PluginScanner.ScanDirectory(folder).Select(p => p.Guid).Distinct().ToList(),
        InstalledUtc = DateTime.UtcNow,
    };

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
                AppLog.Warn($"BepInEx {latest.VersionNumber} couldn't be downloaded ({e.Message}); using BepInEx {newest}.");
            }
        }
        if (newest != null) return _store.VersionDirectory(PackageIndex.BepInExPackId, newest);
        throw new InvalidOperationException(Loc.T("BepInEx isn't downloaded yet and Thunderstore can't be reached. Connect to the internet and try again."));
    }
}
