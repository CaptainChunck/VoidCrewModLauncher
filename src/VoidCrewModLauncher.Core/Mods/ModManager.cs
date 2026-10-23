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

    public PackageIndex Index { get; set; } = PackageIndex.Empty;

    public ModLibrary Library => _library;

    public PackageStore Store => _store;

    /// <summary>Installs a Thunderstore mod (the latest version unless one is given) with its dependencies, and turns it on.</summary>
    public async Task<InstalledMod> InstallAsync(string id, string? version = null, IProgress<InstallStatus>? status = null, CancellationToken cancel = default)
    {
        TsPackage package = Index.Find(id) ?? throw new InvalidOperationException($"{id} isn't on Thunderstore's Void Crew list.");
        TsVersion chosen = (version != null ? package.Version(version) : null) ?? package.Latest
            ?? throw new InvalidOperationException($"{package.FullName} has no versions.");

        List<(TsPackage Package, TsVersion Version)> plan = Resolve(new[] { (package, chosen) });
        int step = 0;
        foreach ((TsPackage item, TsVersion itemVersion) in plan)
        {
            step++;
            string label = $"{item.DisplayName} {itemVersion.VersionNumber}";
            status?.Report(new InstallStatus($"Downloading {label} ({step}/{plan.Count})", 0));
            var progress = new Progress<double>(f => status?.Report(new InstallStatus($"Downloading {label} ({step}/{plan.Count})", f)));
            string folder = await _store.EnsureAsync(itemVersion, progress, cancel);

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
        status?.Report(new InstallStatus($"Installed {package.DisplayName} {chosen.VersionNumber}."));
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
        return package.Latest!;
    }

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
            TsVersion? latest = Index.Find(mod.Id)?.Latest;
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
        if (latest != null)
        {
            if (!_store.Has(PackageIndex.BepInExPackId, latest.VersionNumber)) status?.Report(new InstallStatus($"Downloading BepInEx {latest.VersionNumber}", 0));
            return await _store.EnsureAsync(latest, new Progress<double>(f => status?.Report(new InstallStatus($"Downloading BepInEx {latest.VersionNumber}", f))), cancel);
        }
        string? newest = _store.Versions(PackageIndex.BepInExPackId).OrderByDescending(v => v, VersionNumber.Comparer).FirstOrDefault();
        if (newest != null) return _store.VersionDirectory(PackageIndex.BepInExPackId, newest);
        throw new InvalidOperationException("BepInEx isn't downloaded yet and Thunderstore can't be reached. Connect to the internet and try again.");
    }
}
