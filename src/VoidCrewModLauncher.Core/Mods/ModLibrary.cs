namespace VoidCrewModLauncher.Core.Mods;

/// <summary>The installed mods (library.json). Thread-safe; readers get copies of the list.</summary>
public sealed class ModLibrary
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private List<InstalledMod> _mods;

    /// <summary>Reads library.json. Throws (an IOException) when it is there but can't be opened, so an unreadable library is never saved over as empty.</summary>
    public ModLibrary(AppPaths paths)
    {
        _paths = paths;
        _mods = JsonFiles.Load<LibraryFile>(paths.LibraryFile)?.Mods ?? new List<InstalledMod>();
        _mods.RemoveAll(m => string.IsNullOrEmpty(m.Id));
    }

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    public IReadOnlyList<InstalledMod> Mods
    {
        get
        {
            lock (_gate) return _mods.ToList();
        }
    }

    public IReadOnlyList<InstalledMod> Enabled
    {
        get
        {
            lock (_gate) return _mods.Where(m => m.Enabled).ToList();
        }
    }

    public InstalledMod? Find(string id)
    {
        lock (_gate) return _mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void AddOrReplace(InstalledMod mod)
    {
        lock (_gate)
        {
            int index = _mods.FindIndex(m => string.Equals(m.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _mods[index] = mod;
            else _mods.Add(mod);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _mods.RemoveAll(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Changes one mod in place (enabled, join behaviour...).</summary>
    public void Update(string id, Action<InstalledMod> change)
    {
        lock (_gate)
        {
            InstalledMod? mod = _mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (mod == null) return;
            change(mod);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Changes any number of mods in place, saving once: <paramref name="change"/> returns true for a mod it changed.</summary>
    public void UpdateAll(Func<InstalledMod, bool> change)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (InstalledMod mod in _mods) changed |= change(mod);
            if (changed) SaveLocked();
        }
        if (changed) Changed?.Invoke();
    }

    private void SaveLocked()
    {
        try
        {
            JsonFiles.Save(_paths.LibraryFile, new LibraryFile { Mods = _mods.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList() });
        }
        catch (Exception e)
        {
            AppLog.Error("Could not save the mod library", e);
        }
    }
}
