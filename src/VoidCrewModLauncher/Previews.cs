using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Infrastructure;
using VoidCrewModLauncher.Protocol;
using VoidCrewModLauncher.ViewModels;
using VoidCrewModLauncher.Views;

namespace VoidCrewModLauncher;

/// <summary>
/// VoidCrewModLauncher.exe --render-previews &lt;folder&gt;: draws each page with sample lobbies and mods into PNGs,
/// off screen, in a throwaway data folder. Uses Thunderstore's real list (the cached copy in the launcher's data folder,
/// or --data-dir's, when there is one); nothing is written there.
/// </summary>
public static class Previews
{
    /// <summary>The window's size in the README's screenshots.</summary>
    private const int DefaultWidth = 1360;

    /// <param name="dataDir">The data folder whose cached Thunderstore list to use (--data-dir); null for the usual one.</param>
    /// <param name="width">The window's width; 0 for the screenshots' (the window can't be narrower than its MinWidth).</param>
    public static void Render(string outDir, string? dataDir = null, int width = 0)
    {
        Directory.CreateDirectory(outDir);
        // Every binding WPF can't resolve goes to bindings.log, so wiring mistakes show without clicking through.
        var bindingLog = new System.Diagnostics.TextWriterTraceListener(Path.Combine(outDir, "bindings.log"));
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "vcml-preview-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        AppLog.Init(Path.Combine(outDir, "preview.log"));
        AppPaths real = AppPaths.FromEnvironment(dataDir);
        if (File.Exists(real.PackageListCache))
        {
            File.Copy(real.PackageListCache, paths.PackageListCache, true);
            File.SetLastWriteTimeUtc(paths.PackageListCache, DateTime.UtcNow);
        }

        using var service = new LauncherService(paths);
        try
        {
            // Off the UI thread: blocking it on an await that wants to come back to it would never finish.
            Task.Run(() => service.RefreshPackagesAsync(false)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            AppLog.Warn("Previews without Thunderstore: " + e.Message);
        }
        AddSampleLibrary(service);

        var dialogs = new Dialogs();
        var model = new MainViewModel(service, dialogs, preview: true);
        var window = new MainWindow
        {
            DataContext = model,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = 0,
            Width = width > 0 ? width : DefaultWidth,
            Height = 880,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        dialogs.Owner = window;
        window.Show();

        // The game's state as the launcher words it, in the language being checked.
        model.GameState = GameState.NotRunning;
        model.GameStateText = Loc.T("NOT RUNNING");
        model.SetStatus(Loc.T("Ready."));
        Snap(window, Path.Combine(outDir, "1-browser-not-running.png"));

        model.GameState = GameState.Menu;
        model.GameStateText = Loc.F("MAIN MENU · {0}", MainViewModel.RegionName("eu"));
        model.Browser.OnState(new StateMessage
        {
            Phase = "menu",
            InLobby = true,
            Region = "eu",
            CanHost = true,
            CanJoinModded = true,
            Regions = new List<RegionDto> { new() { Code = "eu", Ping = 31 }, new() { Code = "us", Ping = 104 }, new() { Code = "asia", Ping = 256 } },
        });
        model.Browser.OnRooms(new RoomsMessage { Region = "eu", Rooms = SampleRooms() });
        model.Browser.Selected = model.Browser.LobbiesView.Cast<LobbyViewModel>().FirstOrDefault(l => l.Lobby.ModSource == LobbyModSource.Advert);
        model.SetStatus(Loc.T("Ready."));
        Snap(window, Path.Combine(outDir, "2-browser-live.png"));

        model.Navigate("mods");
        model.Mods.RefreshInstalled();
        Snap(window, Path.Combine(outDir, "3-mods-installed.png"));

        model.Mods.ShowBrowse = true;
        model.Mods.RefreshOnline();
        model.Mods.SelectedPackage = model.Mods.OnlineView.Cast<PackageViewModel>().FirstOrDefault(p => p.Id == "Captain_Chuncks_Mods-Void_Crew_Fleet")
                                     ?? model.Mods.OnlineView.Cast<PackageViewModel>().FirstOrDefault();
        Snap(window, Path.Combine(outDir, "4-mods-browse.png"), 6);

        model.Navigate("host");
        Snap(window, Path.Combine(outDir, "5-host.png"));

        model.Navigate("settings");
        model.Settings.ShowExamplePaths();
        Snap(window, Path.Combine(outDir, "6-settings.png"));

        LobbyViewModel? lobby = model.Browser.LobbiesView.Cast<LobbyViewModel>().FirstOrDefault(l => l.Lobby.ModSource == LobbyModSource.VoidManager);
        if (lobby != null)
        {
            JoinPlan plan = service.Planner.Plan(lobby.Lobby);
            var join = new JoinWindow(plan, service.Planner.Resolve, _ => true, true)
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = 0,
                ShowActivated = false,
            };
            join.Show();
            Snap(join, Path.Combine(outDir, "7-join-dialog.png"));
            join.Close();
        }

        window.Close();
        bindingLog.Flush();
        bindingLog.Close();
        try
        {
            Directory.Delete(paths.Root, true);
        }
        catch (IOException)
        {
            // Temp folder.
        }
    }

    private static void Snap(Window window, string file, double settleSeconds = 2.5)
    {
        Pump(settleSeconds);
        // The window's own root, so margins around the content (pop-ups have one for their shadow) are included.
        var content = VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement root
            ? root
            : (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth);
        int height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(file);
        encoder.Save(stream);
    }

    /// <summary>Lets layout, bindings and icon downloads run for a moment.</summary>
    private static void Pump(double seconds)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(30);
        }
    }

    private static void AddSampleLibrary(LauncherService service)
    {
        void Add(string id, bool enabled, bool dependency = false)
        {
            TsPackage? package = service.Index.Find(id);
            if (package?.Latest == null) return;
            InstalledMod mod = ModManager.Describe(package, package.Latest, service.Paths.Store);
            mod.PluginGuids.Clear();
            mod.Enabled = enabled;
            mod.InstalledAsDependency = dependency;
            service.Library.AddOrReplace(mod);
        }

        Add("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", true);
        // A version newer on GitHub than on Thunderstore, for the GITHUB badge.
        service.Library.Update("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", m =>
        {
            m.Version = "3.15.0";
            m.GitHubRelease = "https://github.com/CaptainChunck/VoidCrewVRUIFix/releases/tag/v3.15.0";
        });
        Add("Captain_Chuncks_Mods-Ship_Customizer", true);
        Add("Captain_Chuncks_Mods-Void_Crew_Fleet", true);
        Add("NihilityShift-VoidManager", true, true);
        Add("VoidCrewModdingTeam-Pilot_HUD", false);
        Add("Aquarel-NoUnrepairableDamage", false);
        service.Library.AddOrReplace(new InstalledMod
        {
            Id = "local-uuvr",
            Source = ModSource.Local,
            Name = "UUVR (modern)",
            Author = "Your PC",
            Version = "0.5.1",
            Description = "Universal Unity VR, imported from Rai Pal.",
            Enabled = true,
        });
    }

    private static List<RoomDto> SampleRooms()
    {
        string advert = LobbyAdvert.Encode(new[]
        {
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Version = "0.3.0", Need = ModNeed.Everyone },
            new AdvertMod { Id = "Captain_Chuncks_Mods-Ship_Customizer", Version = "0.7.0", Need = ModNeed.Everyone },
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", Version = "3.14.1", Need = ModNeed.ClientOnly },
            new AdvertMod { Id = "UUVR (modern)", Version = "0.5.1", Need = ModNeed.ClientOnly, Local = true },
        });
        string voidManager = Convert.ToBase64String(VoidManagerModList.Encode("1.2.11", new[]
        {
            new VoidManagerMod("VoidManager", "VoidCrewModdingTeam.VoidManager", "1.2.11", 8, "NihilityShift/VoidManager"),
            new VoidManagerMod("Improved Scoop", "NihilityShift.ImprovedScoop", "1.1.6", 20, "NihilityShift/Improved_Scoop"),
            new VoidManagerMod("Uncapped Turret Stats", "VoidCrewModdingTeam.UncappedTurretStats", "1.0.1", 30, ""),
            new VoidManagerMod("Pilot HUD", "VoidCrewModdingTeam.PilotHUD", "0.0.4", 8, ""),
        }));
        string turrets = Convert.ToBase64String(VoidManagerModList.Encode("1.2.11", new[]
        {
            new VoidManagerMod("VoidManager", "VoidCrewModdingTeam.VoidManager", "1.2.11", 8, "NihilityShift/VoidManager"),
            new VoidManagerMod("Backpack", "NihilityShift.Backpack", "1.0.3", 30, "NihilityShift/Backpack"),
        }));
        return new List<RoomDto>
        {
            new() { Id = "A1", Name = "Fleet night: two ships, PvP after", Host = "Captain Chunck", Players = 5, MaxPlayers = 16, InHub = true, Ship = "METEM Destroyer: Rookie", System = "--", Modding = "session", Advert = advert, AverageRank = 21, CrossPlay = true, Platform = "STEAM", SamePlatform = true, Quest = "Generic" },
            new() { Id = "B2", Name = "[Mods Required] Chill pilgrimage", Host = "Nova", Players = 3, MaxPlayers = 4, InHub = false, Ship = "METEM Frigate: Scout", System = "Echo Sector", Modding = "session", VoidManagerMods = voidManager, AverageRank = 34, Quest = "Pilgrimage", Platform = "STEAM", SamePlatform = true, Mutators = new() { "Hardened Hulls" } },
            new() { Id = "C3", Name = "Vanilla - new players welcome", Host = "Riley", Players = 2, MaxPlayers = 4, InHub = true, Ship = "METEM Striker: Rookie", Modding = "none", AverageRank = 5, Quest = "Generic", Platform = "STEAM", SamePlatform = true },
            new() { Id = "D4", Name = "Backpacks everywhere", Host = "Kestrel", Players = 4, MaxPlayers = 4, InHub = true, Ship = "METEM Destroyer: Heavy", Modding = "session", VoidManagerMods = turrets, AverageRank = 40, Quest = "Generic", Platform = "STEAM", SamePlatform = true },
            new() { Id = "E5", Name = "Late night salvage", Host = "Juno", Players = 1, MaxPlayers = 6, InHub = false, Ship = "METEM Frigate: Rookie", System = "Viridian Belt", Modding = "local", AverageRank = 12, Quest = "Survivor", Platform = "STEAM", SamePlatform = true },
            new() { Id = "F6", Name = "Modded mystery box", Host = "Orion", Players = 2, MaxPlayers = 6, InHub = true, Ship = "METEM Destroyer: Rookie", Modding = "session", AverageRank = 28, Quest = "Generic", Platform = "STEAM", SamePlatform = true },
        };
    }
}
