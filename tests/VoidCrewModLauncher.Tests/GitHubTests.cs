using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.GitHub;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

public class GitHubTests
{
    private const string Owner = "Captain_Chuncks_Mods";
    private const string Repo = "https://github.com/CaptainChunck/VoidCrewVRUIFix";
    private const string LatestUrl = "https://api.github.com/repos/CaptainChunck/VoidCrewVRUIFix/releases/latest";

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>VR UI Fix as Thunderstore lists it: 3.14.1, with its GitHub repo as the website.</summary>
    private static TsPackage VrUiFix(string version = "3.14.1")
    {
        TsPackage package = Packages.Make(Owner, "Void_Crew_VR_UI_Fix", new[] { version }, new[] { "Client-Side", "Mods" }, new[] { "BepInEx-BepInExPack-5.4.2305" });
        package.Versions[0].WebsiteUrl = Repo;
        return package;
    }

    private static string Release(string tag, params (string Name, byte[] Body)[] assets) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft = false,
        prerelease = false,
        html_url = $"{Repo}/releases/tag/{tag}",
        published_at = "2026-10-19T13:04:03Z",
        assets = assets.Select(a => new
        {
            name = a.Name,
            size = a.Body.Length,
            state = "uploaded",
            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(a.Body)).ToLowerInvariant(),
            browser_download_url = $"https://github.test/{tag}/{a.Name}",
        }),
    });

    private static FakeHttp ThunderstoreList(FakeHttp http, params TsPackage[] packages) => http
        .On(ThunderstoreClient.ListingIndexUrl, FakeHttp.Gzip("[\"https://cdn.test/chunk.blob\"]"))
        .On("https://cdn.test/chunk.blob", FakeHttp.Gzip(JsonSerializer.Serialize(packages, Snake)));

    private static byte[] VrUiFixZip(string name = "Void_Crew_VR_UI_Fix", string version = "3.15.0") => FakeHttp.Zip(
        ("manifest.json", $$"""{"name":"{{name}}","version_number":"{{version}}","website_url":"{{Repo}}","description":"VR","dependencies":["BepInEx-BepInExPack-5.4.2305"]}"""),
        ("plugins/VoidCrewVRUIFix.dll", "new dll " + version));

    private static InstalledMod Installed(string id, string version) =>
        new() { Id = id, Source = ModSource.Thunderstore, Name = id.Split('-')[1], Author = Owner, Version = version };

    [Fact]
    public void GitHub_repos_are_read_from_mod_websites()
    {
        Assert.Equal(new GitHubRepo("CaptainChunck", "VoidCrewFleet"), GitHubRepo.Parse("https://github.com/CaptainChunck/VoidCrewFleet"));
        Assert.Equal(new GitHubRepo("CaptainChunck", "VoidCrewFleet"), GitHubRepo.Parse("https://github.com/CaptainChunck/VoidCrewFleet/releases/tag/v0.4.0"));
        Assert.Equal(new GitHubRepo("a-b", "c.d_e"), GitHubRepo.Parse("https://www.github.com/a-b/c.d_e.git/"));
        Assert.Null(GitHubRepo.Parse("https://github.com/CaptainChunck"));
        Assert.Null(GitHubRepo.Parse("https://gitlab.com/someone/mod"));
        Assert.Null(GitHubRepo.Parse("https://thunderstore.io/c/void-crew/p/x/y/"));
        Assert.Null(GitHubRepo.Parse("not a url"));
        Assert.Null(GitHubRepo.Parse(null));
    }

    [Fact]
    public void A_release_becomes_a_version_with_its_package_zip_or_else_its_plugin_dlls()
    {
        var repo = new GitHubRepo("CaptainChunck", "VoidCrewVRUIFix");
        TsPackage package = VrUiFix();
        TsVersion? Version(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return GitHubClient.ToVersion(document.RootElement, repo, package);
        }

        // The package's zip wins over the DLL and over other zips.
        TsVersion zipped = Version(Release("v3.15.0", ("Sources.zip", new byte[3]), ("VoidCrewVRUIFix.dll", new byte[5]), ("Void_Crew_VR_UI_Fix-3.15.0.zip", new byte[7])))!;
        Assert.Equal("3.15.0", zipped.VersionNumber);
        Assert.Equal("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix-3.15.0", zipped.FullName);
        Assert.Equal("Void_Crew_VR_UI_Fix-3.15.0.zip", zipped.GitHub!.Package!.Name);
        Assert.Equal(7, zipped.FileSize);
        Assert.Equal(Repo, zipped.WebsiteUrl);
        Assert.Equal(new[] { "BepInEx-BepInExPack-5.4.2305" }, zipped.Dependencies);
        Assert.NotNull(zipped.GitHub.Package.Sha256);

        // Only a DLL: it goes into the newest Thunderstore version's files.
        TsVersion dlls = Version(Release("3.15.0", ("VoidCrewVRUIFix.dll", new byte[5])))!;
        Assert.Null(dlls.GitHub!.Package);
        Assert.Equal("VoidCrewVRUIFix.dll", Assert.Single(dlls.GitHub.Plugins).Name);
        Assert.Equal("3.14.1", dlls.GitHub.Base!.VersionNumber);

        Assert.Null(Version(Release("nightly", ("Void_Crew_VR_UI_Fix.zip", new byte[1]))));
        Assert.Null(Version(Release("v3.15.0")));
        Assert.Null(Version(Release("v3.15.0").Replace("\"prerelease\":false", "\"prerelease\":true")));
    }

    [Fact]
    public async Task A_newer_GitHub_release_is_installed_from_its_checked_package()
    {
        using var temp = new TempFolder();
        byte[] zip = VrUiFixZip();
        var http = ThunderstoreList(new FakeHttp(), VrUiFix())
            .On(LatestUrl, Release("v3.15.0", ("VoidCrewVRUIFix.dll", new byte[5]), ("Void_Crew_VR_UI_Fix-3.15.0.zip", zip)))
            .On("https://github.test/v3.15.0/Void_Crew_VR_UI_Fix-3.15.0.zip", zip);
        using var service = new LauncherService(temp.AppPaths(), http);
        service.Library.AddOrReplace(Installed("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", "3.14.1"));

        await service.RefreshPackagesAsync(true);
        TsVersion latest = service.Index.Find("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix")!.Latest!;
        Assert.Equal("3.15.0", latest.VersionNumber);
        Assert.NotNull(latest.GitHub);
        Assert.Equal("3.14.1", service.Index.Find("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix")!.LatestOnThunderstore!.VersionNumber);

        (List<string> updated, List<string> failed) = await service.UpdateModsAsync(null);
        Assert.Empty(failed);
        Assert.Equal("Void Crew VR UI Fix 3.15.0", Assert.Single(updated));
        InstalledMod mod = service.Library.Find("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix")!;
        Assert.Equal("3.15.0", mod.Version);
        Assert.Equal($"{Repo}/releases/tag/v3.15.0", mod.GitHubRelease);
        string folder = service.Store.VersionDirectory(mod.Id, "3.15.0");
        Assert.Equal("new dll 3.15.0", File.ReadAllText(Path.Combine(folder, "plugins", "VoidCrewVRUIFix.dll")));

        // Switched off: GitHub isn't asked, and Thunderstore's version is the newest again.
        service.Settings.GitHubUpdates = false;
        int asked = http.Requests.Count(r => r.StartsWith("https://api.github.com", StringComparison.Ordinal));
        await service.RefreshPackagesAsync(true);
        Assert.Equal("3.14.1", service.Index.Find("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix")!.Latest!.VersionNumber);
        Assert.Equal(asked, http.Requests.Count(r => r.StartsWith("https://api.github.com", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_GitHub_package_that_is_another_mod_or_version_is_refused()
    {
        using var temp = new TempFolder();
        byte[] wrongName = VrUiFixZip(name: "Something_Else");
        var http = ThunderstoreList(new FakeHttp(), VrUiFix())
            .On(LatestUrl, Release("v3.15.0", ("Void_Crew_VR_UI_Fix-3.15.0.zip", wrongName)))
            .On("https://github.test/v3.15.0/Void_Crew_VR_UI_Fix-3.15.0.zip", wrongName);
        using var service = new LauncherService(temp.AppPaths(), http);
        service.Library.AddOrReplace(Installed("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", "3.14.1"));
        await service.RefreshPackagesAsync(true);

        (List<string> updated, List<string> failed) = await service.UpdateModsAsync(null);
        Assert.Empty(updated);
        Assert.Single(failed);
        Assert.Equal("3.14.1", service.Library.Find("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix")!.Version);
        Assert.False(service.Store.Has("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", "3.15.0"));
    }

    [Fact]
    public async Task A_release_of_only_plugin_dlls_goes_into_the_Thunderstore_files()
    {
        using var temp = new TempFolder();
        TsPackage fleet = Packages.Make(Owner, "Void_Crew_Fleet", new[] { "0.4.0" }, new[] { "Mods" });
        fleet.Versions[0].WebsiteUrl = "https://github.com/CaptainChunck/VoidCrewFleet";
        byte[] dll = System.Text.Encoding.UTF8.GetBytes("fleet 0.4.1");
        var http = ThunderstoreList(new FakeHttp(), fleet)
            .On(fleet.Versions[0].DownloadUrl, FakeHttp.Zip(("manifest.json", "{\"name\":\"Void_Crew_Fleet\",\"version_number\":\"0.4.0\"}"), ("VoidCrewFleet.dll", "fleet 0.4.0"), ("README.md", "readme")))
            .On("https://api.github.com/repos/CaptainChunck/VoidCrewFleet/releases/latest", Release("v0.4.1", ("VoidCrewFleet.dll", dll)))
            .On("https://github.test/v0.4.1/VoidCrewFleet.dll", dll);
        using var service = new LauncherService(temp.AppPaths(), http);
        service.Library.AddOrReplace(Installed("Captain_Chuncks_Mods-Void_Crew_Fleet", "0.4.0"));
        await service.RefreshPackagesAsync(true);

        (List<string> updated, List<string> failed) = await service.UpdateModsAsync(null);
        Assert.Empty(failed);
        Assert.Single(updated);
        string folder = service.Store.VersionDirectory("Captain_Chuncks_Mods-Void_Crew_Fleet", "0.4.1");
        Assert.Equal("fleet 0.4.1", File.ReadAllText(Path.Combine(folder, "VoidCrewFleet.dll")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(folder, "README.md")));
        Assert.Equal("fleet 0.4.0", File.ReadAllText(Path.Combine(service.Store.VersionDirectory("Captain_Chuncks_Mods-Void_Crew_Fleet", "0.4.0"), "VoidCrewFleet.dll")));
    }

    [Fact]
    public async Task Joiners_get_a_hosts_GitHub_version_from_the_same_release()
    {
        using var temp = new TempFolder();
        byte[] zip = VrUiFixZip();
        var http = ThunderstoreList(new FakeHttp(), VrUiFix())
            .On("https://api.github.com/repos/CaptainChunck/VoidCrewVRUIFix/releases/tags/v3.15.0", Release("v3.15.0", ("Void_Crew_VR_UI_Fix-3.15.0.zip", zip)));
        using var service = new LauncherService(temp.AppPaths(), http);
        await service.RefreshPackagesAsync(true);

        var room = new RoomDto { Id = "R1", Name = "VR night", Players = 1, MaxPlayers = 4, Modding = "session", Advert = "vcml1;Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix@3.15.0!R" };
        Lobby before = LobbyReader.Read(room, "eu", service.Index);
        Assert.Null(before.Mods[0].PackageVersion);
        Assert.True(await service.FreshenLobbyAsync(before));
        Lobby after = LobbyReader.Read(room, "eu", service.Index);
        Assert.NotNull(after.Mods[0].PackageVersion!.GitHub);
        Assert.Equal(LobbyModStatus.Download, service.Planner.Plan(after).Lines[0].Status);
    }

    [Fact]
    public async Task GitHub_answers_are_reused_and_asked_again_with_their_ETag()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        var http = new FakeHttp().On(LatestUrl, Release("v3.15.0")).WithETag(LatestUrl, "\"abc\"");
        using (var client = new GitHubClient(paths, http))
        {
            using (JsonDocument? first = await client.GetAsync("/repos/CaptainChunck/VoidCrewVRUIFix/releases/latest")) Assert.NotNull(first);
            using (JsonDocument? again = await client.GetAsync("/repos/CaptainChunck/VoidCrewVRUIFix/releases/latest")) Assert.NotNull(again);
            Assert.Single(http.Requests);
        }

        // An hour later: asked again with the ETag; "not modified" keeps the saved answer.
        string cache = Path.Combine(paths.Cache, "github.json");
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cache))!.AsObject();
        foreach (var entry in saved) entry.Value!["FetchedUtc"] = DateTime.UtcNow.AddHours(-1);
        File.WriteAllText(cache, saved.ToJsonString());
        using (var client = new GitHubClient(paths, http))
        {
            using JsonDocument? cached = await client.GetAsync("/repos/CaptainChunck/VoidCrewVRUIFix/releases/latest");
            Assert.Equal("v3.15.0", cached!.RootElement.GetProperty("tag_name").GetString());
            Assert.Equal(2, http.Requests.Count);
            Assert.Equal("\"abc\"", http.IfNoneMatch[LatestUrl]);
        }
    }

    [Fact]
    public async Task Updates_that_come_out_while_playing_are_told_once()
    {
        using var temp = new TempFolder();
        byte[] zip = VrUiFixZip();
        var http = ThunderstoreList(new FakeHttp(), VrUiFix(), Packages.Make(Owner, "Void_Crew_Fleet", new[] { "0.4.0", "0.3.0" }));
        using var service = new LauncherService(temp.AppPaths(), http);
        service.Library.AddOrReplace(Installed("Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", "3.14.1"));
        service.Library.AddOrReplace(Installed("Captain_Chuncks_Mods-Void_Crew_Fleet", "0.4.0"));
        service.Settings.GitHubUpdates = false;
        await service.RefreshPackagesAsync(true);
        service.Settings.GitHubUpdates = true;

        // Started for a lobby that runs Fleet 0.3.0: 0.4.0 was already known then, so it isn't news.
        service.UseLaunch(new LaunchFile
        {
            Mods = new List<LaunchMod>
            {
                new() { Id = "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", Name = "Void Crew VR UI Fix", Version = "3.14.1" },
                new() { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Name = "Void Crew Fleet", Version = "0.3.0" },
                new() { Id = "local-mine", Name = "Mine", Version = "1.0", Local = true },
            },
        });
        Assert.Empty(service.NewUpdatesForRunningGame());

        // VR UI Fix 3.15.0 comes out on GitHub while the game runs.
        http.On(LatestUrl, Release("v3.15.0", ("Void_Crew_VR_UI_Fix-3.15.0.zip", zip))).On("https://github.test/v3.15.0/Void_Crew_VR_UI_Fix-3.15.0.zip", zip);
        await service.FreshenAsync(new[] { "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix" });
        await service.UpdateModsAsync(null);

        (string name, string version, bool ready) = Assert.Single(service.NewUpdatesForRunningGame());
        Assert.Equal(("Void Crew VR UI Fix", "3.15.0", true), (name, version, ready));
        Assert.Empty(service.NewUpdatesForRunningGame());
    }
}
