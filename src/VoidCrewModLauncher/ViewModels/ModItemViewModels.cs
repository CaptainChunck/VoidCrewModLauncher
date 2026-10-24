using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Infrastructure;

namespace VoidCrewModLauncher.ViewModels;

/// <summary>A row of the INSTALLED tab.</summary>
public partial class InstalledModViewModel : ObservableObject
{
    private readonly ModsViewModel _owner;
    private bool _loading;

    public InstalledModViewModel(ModsViewModel owner, InstalledMod mod)
    {
        _owner = owner;
        Mod = mod;
        _loading = true;
        isEnabled = mod.Enabled;
        _loading = false;
    }

    public InstalledMod Mod { get; private set; }

    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private ImageSource? icon;
    [ObservableProperty] private string? updateVersion;

    public string Id => Mod.Id;
    public string Name => Mod.DisplayName;
    public string Author => Mod.IsLocal ? Loc.T("added from your PC") : Loc.F("by {0}", Mod.Author.Replace('_', ' '));
    public string Version => "v" + Mod.Version;
    // Saved in library.json in English (see LocalMods), so it is translated when shown.
    public string Description => Mod.IsLocal && Mod.Description == "Added from your PC." ? Loc.T("Added from your PC.") : Mod.Description;
    public bool IsLocal => Mod.IsLocal;
    public bool IsDependency => Mod.InstalledAsDependency;

    /// <summary>The version you have came from the mod's GitHub releases (newer there than on Thunderstore).</summary>
    public bool FromGitHub => Mod.GitHubRelease != null;

    public bool HasUpdate => UpdateVersion != null;
    public string UpdateText => UpdateVersion == null ? "" : Loc.F("UPDATE TO {0}", UpdateVersion);
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public string JoinText => Mod.WhenJoining switch
    {
        JoinBehaviour.Keep => Loc.T("Always kept when joining lobbies"),
        JoinBehaviour.OnlyIfLobbyHasIt => Loc.T("Only when a lobby runs it"),
        _ => ModPolicy.KeepWhenJoining(Mod) ? Loc.T("Kept when joining lobbies (client-side)") : Loc.T("Only when a lobby runs it"),
    };

    public bool JoinAuto => Mod.WhenJoining == JoinBehaviour.Auto;
    public bool JoinKeep => Mod.WhenJoining == JoinBehaviour.Keep;
    public bool JoinOnlyIfLobby => Mod.WhenJoining == JoinBehaviour.OnlyIfLobbyHasIt;

    public void Refresh(InstalledMod mod)
    {
        Mod = mod;
        _loading = true;
        IsEnabled = mod.Enabled;
        _loading = false;
        OnPropertyChanged(string.Empty);
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = _owner.SetEnabledAsync(this, value);
    }

    partial void OnUpdateVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateText));
    }

    /// <summary>Puts the switch back without acting on it (when the player cancels).</summary>
    public void ResetEnabled(bool value)
    {
        _loading = true;
        IsEnabled = value;
        _loading = false;
    }
}

/// <summary>A Thunderstore package on the BROWSE tab.</summary>
public partial class PackageViewModel : ObservableObject
{
    public PackageViewModel(TsPackage package, InstalledMod? installed)
    {
        Package = package;
        Installed = installed;
    }

    public TsPackage Package { get; private set; }

    /// <summary>Shows newer data for the same package (every binding refreshes).</summary>
    public void Replace(TsPackage package)
    {
        Package = package;
        OnPropertyChanged(string.Empty);
    }

    [ObservableProperty] private ImageSource? icon;
    [ObservableProperty] private InstalledMod? installed;

    public string Id => Package.FullName;
    public string Name => Package.DisplayName;
    public string Author => Loc.F("by {0}", Package.Author);
    public string Description => Package.Latest?.Description ?? "";
    public string LatestVersion => Package.Latest?.VersionNumber ?? "";

    /// <summary>The newest version is a GitHub release (Thunderstore doesn't have it yet).</summary>
    public bool LatestFromGitHub => Package.Latest?.GitHub != null;

    /// <summary>The newest version comes from voidcrewmod.com.</summary>
    public bool LatestFromWebsite => Package.LatestFromWebsite;
    public string Downloads => Format.Count(Package.TotalDownloads);
    public long DownloadCount => Package.TotalDownloads;
    public int Rating => Package.RatingScore;
    public DateTimeOffset Updated => Package.DateUpdated;
    public DateTimeOffset Created => Package.DateCreated;
    public string UpdatedText => Loc.F("updated {0}", Format.Ago(Package.DateUpdated));
    public bool IsDeprecated => Package.IsDeprecated;
    public IReadOnlyList<string> Categories => Package.Categories.Where(c => c != "Mods").ToList();
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public bool IsInstalled => Installed != null;
    public bool HasUpdate => Installed != null && !Installed.IsLocal && VersionNumber.Compare(LatestVersion, Installed.Version) > 0;

    public string ActionText => Installed == null ? Loc.T("INSTALL") : HasUpdate ? Loc.T("UPDATE") : Loc.T("INSTALLED");

    /// <summary>Installed and nothing newer: the INSTALL button shows INSTALLED in green (bind this, not the translated text).</summary>
    public bool IsUpToDate => Installed != null && !HasUpdate;

    public string Dependencies
    {
        get
        {
            var names = (Package.Latest?.Dependencies ?? new List<string>())
                .Select(d => PackageReference.Parse(d) is { } r ? r.Name.Replace('_', ' ') : d)
                .Concat(Core.GitHub.GitHubModCatalog.Default.NeededBy(Id).Select(m => m.Name + " (GitHub)"))
                .ToList();
            return names.Count > 0 ? string.Join(", ", names) : Loc.T("None");
        }
    }

    public string FileSize => Format.Bytes(Package.Latest?.FileSize ?? 0);

    partial void OnInstalledChanged(InstalledMod? value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsUpToDate));
    }
}
