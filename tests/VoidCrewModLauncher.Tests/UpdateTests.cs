using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Setup;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

/// <summary>Answers HTTP requests from a table, like the real servers would (404 for anything else).</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _routes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _etags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = new();

    /// <summary>The If-None-Match header of the last request for each URL.</summary>
    public Dictionary<string, string> IfNoneMatch { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeHttp On(string url, string body, HttpStatusCode status = HttpStatusCode.OK) => On(url, Encoding.UTF8.GetBytes(body), status);

    public FakeHttp On(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = (status, body);
        return this;
    }

    /// <summary>Answers with this ETag, and 304 Not Modified when a request already has it.</summary>
    public FakeHttp WithETag(string url, string etag)
    {
        _etags[url] = etag;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.ToString();
        string? sent = request.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
        lock (Requests)
        {
            Requests.Add(url);
            if (sent != null) IfNoneMatch[url] = sent;
        }
        _etags.TryGetValue(url, out string? etag);
        if (etag != null && sent == etag) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        HttpResponseMessage response = _routes.TryGetValue(url, out var route)
            ? new HttpResponseMessage(route.Status) { Content = new ByteArrayContent(route.Body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"Not found.\"}") };
        if (etag != null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    }

    public static byte[] Gzip(string text)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Optimal, true)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return memory.ToArray();
    }

    /// <summary>A zip with these files.</summary>
    public static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach ((string name, string content) in files)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }
}

public class UpdateTests
{
    private const string Site = "https://thunderstore.io";

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>What the per-package API returns for a package listed in the Void Crew community.</summary>
    private static string CurrentJson(string owner, string name, string version, string community = "void-crew", string review = "approved") => $$"""
        {"name":"{{name}}","full_name":"{{owner}}-{{name}}","owner":"{{owner}}","namespace":"{{owner}}",
         "package_url":"https://thunderstore.io/package/{{owner}}/{{name}}/","date_created":"2026-10-01T00:00:00Z",
         "date_updated":"2026-10-19T14:51:47Z","rating_score":3,"is_pinned":false,"is_deprecated":false,"total_downloads":42,
         "latest":{{VersionJson(owner, name, version)}},
         "community_listings":[{"has_nsfw_content":false,"categories":["Mods","Client-Side","Host-Side"],"community":"{{community}}","review_status":"{{review}}"}]}
        """;

    private static string VersionJson(string owner, string name, string version) => $$"""
        {"name":"{{name}}","full_name":"{{owner}}-{{name}}-{{version}}","description":"{{name}} {{version}}","icon":"https://cdn/icon.png",
         "version_number":"{{version}}","dependencies":["BepInEx-BepInExPack-5.4.2305"],
         "download_url":"https://thunderstore.io/package/download/{{owner}}/{{name}}/{{version}}/","downloads":10,
         "date_created":"2026-10-19T14:51:47Z","website_url":"","is_active":true,"namespace":"{{owner}}"}
        """;

    private static string PackageUrl(string owner, string name) => $"{Site}/api/experimental/package/{owner}/{name}/";

    // ---------------------------------------------------------------- the package list and merging

    [Fact]
    public void Merge_adds_new_versions_and_missing_packages_and_leaves_the_rest()
    {
        PackageIndex index = Packages.Index();
        TsPackage fleet = index.Find("Captain_Chuncks_Mods-Void_Crew_Fleet")!;
        PackageIndex merged = index.Merge(new[]
        {
            Packages.Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { "0.4.0" }),
            Packages.Make("Captain_Chuncks_Mods", "Brand_New", new[] { "1.0.0" }),
        });

        Assert.Equal("0.4.0", merged.Find("Captain_Chuncks_Mods-Void_Crew_Fleet")!.Latest!.VersionNumber);
        Assert.Equal(new[] { "0.4.0", "0.3.0", "0.2.1" }, merged.Find("Captain_Chuncks_Mods-Void_Crew_Fleet")!.Versions.Select(v => v.VersionNumber));
        Assert.NotNull(merged.Find("Captain_Chuncks_Mods-Brand_New"));
        Assert.Same(index.Find("NihilityShift-VoidManager"), merged.Find("NihilityShift-VoidManager"));
        // The old index and its packages don't change.
        Assert.Equal("0.3.0", fleet.Latest!.VersionNumber);
        Assert.Null(index.Find("Captain_Chuncks_Mods-Brand_New"));
        // Nothing new: the same index.
        Assert.Same(merged, merged.Merge(new[] { Packages.Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { "0.3.0" }) }));
    }

    [Fact]
    public async Task The_package_list_comes_from_the_listing_index_and_falls_back_to_the_old_list()
    {
        using var temp = new TempFolder();
        string chunk = JsonSerializer.Serialize(new[] { Packages.Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { "0.4.0" }) }, Snake);
        var http = new FakeHttp()
            .On(ThunderstoreClient.ListingIndexUrl, FakeHttp.Gzip("[\"https://cdn.test/chunk1.blob\"]"))
            .On("https://cdn.test/chunk1.blob", FakeHttp.Gzip(chunk));
        using (var client = new ThunderstoreClient(temp.AppPaths(), http))
        {
            List<TsPackage> packages = await client.GetPackagesAsync(true);
            Assert.Equal("0.4.0", Assert.Single(packages).Latest!.VersionNumber);
            Assert.False(client.IsOffline);
        }

        // The index fails: the older single list is read instead.
        string list = JsonSerializer.Serialize(new[] { Packages.Make("Captain_Chuncks_Mods", "Ship_Customizer", new[] { "0.9.0" }) }, Snake);
        var fallback = new FakeHttp().On(ThunderstoreClient.ListingIndexUrl, "oops", HttpStatusCode.InternalServerError).On(ThunderstoreClient.PackageListUrl, list);
        using (var client = new ThunderstoreClient(temp.AppPaths(), fallback))
        {
            Assert.Equal("Captain_Chuncks_Mods-Ship_Customizer", Assert.Single(await client.GetPackagesAsync(true)).FullName);
        }

        // Offline: the cached list (written by the last download) is used.
        using (var client = new ThunderstoreClient(temp.AppPaths(), new FakeHttp()))
        {
            Assert.Equal("Captain_Chuncks_Mods-Ship_Customizer", Assert.Single(await client.GetPackagesAsync(true)).FullName);
            Assert.True(client.IsOffline);
        }
    }

    [Fact]
    public async Task Current_packages_and_exact_versions_come_from_the_per_package_api()
    {
        using var temp = new TempFolder();
        var http = new FakeHttp()
            .On(PackageUrl("Captain_Chuncks_Mods", "Ship_Customizer"), CurrentJson("Captain_Chuncks_Mods", "Ship_Customizer", "0.9.0"))
            .On(PackageUrl("Someone", "Other_Game_Mod"), CurrentJson("Someone", "Other_Game_Mod", "1.0.0", community: "lethal-company"))
            .On(PackageUrl("Someone", "Rejected"), CurrentJson("Someone", "Rejected", "1.0.0", review: "rejected"))
            .On($"{Site}/api/experimental/package/Captain_Chuncks_Mods/Ship_Customizer/0.7.0/", VersionJson("Captain_Chuncks_Mods", "Ship_Customizer", "0.7.0"));
        using var client = new ThunderstoreClient(temp.AppPaths(), http);

        List<TsPackage> current = await client.GetCurrentAsync(new[]
        {
            "Captain_Chuncks_Mods-Ship_Customizer", "Captain_Chuncks_Mods/Ship_Customizer", "Someone-Other_Game_Mod", "Someone-Rejected", "Nobody-Missing",
        });
        TsPackage customizer = Assert.Single(current);
        Assert.Equal("0.9.0", customizer.Latest!.VersionNumber);
        Assert.Equal("Captain_Chuncks_Mods", customizer.Owner);
        Assert.Contains("Client-Side", customizer.Categories);
        Assert.Equal(new[] { "BepInEx-BepInExPack-5.4.2305" }, customizer.Latest.Dependencies);
        Assert.Equal(ThunderstoreClient.PackagePage("Captain_Chuncks_Mods", "Ship_Customizer"), customizer.PackageUrl);

        Assert.Equal("0.7.0", (await client.GetVersionAsync("Captain_Chuncks_Mods-Ship_Customizer", "0.7.0"))!.VersionNumber);
        Assert.Null(await client.GetVersionAsync("Captain_Chuncks_Mods-Ship_Customizer", "0.8.0"));

        // Answers are reused for a while instead of asking again.
        int asked = http.Requests.Count;
        await client.GetCurrentAsync(new[] { "Captain_Chuncks_Mods-Ship_Customizer" });
        await client.GetVersionAsync("Captain_Chuncks_Mods-Ship_Customizer", "0.8.0");
        Assert.Equal(asked, http.Requests.Count);
    }

    [Fact]
    public async Task Refreshing_finds_updates_the_stale_list_misses_and_lobbies_get_versions_it_lacks()
    {
        using var temp = new TempFolder();
        // Thunderstore's cached list: Fleet 0.3.0, and no Ship Customizer at all.
        string list = JsonSerializer.Serialize(new[] { Packages.Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { "0.3.0" }) }, Snake);
        var http = new FakeHttp()
            .On(ThunderstoreClient.ListingIndexUrl, FakeHttp.Gzip("[\"https://cdn.test/chunk.blob\"]"))
            .On("https://cdn.test/chunk.blob", FakeHttp.Gzip(list))
            .On(PackageUrl("Captain_Chuncks_Mods", "Void_Crew_Fleet"), CurrentJson("Captain_Chuncks_Mods", "Void_Crew_Fleet", "0.4.0"))
            .On(PackageUrl("Captain_Chuncks_Mods", "Ship_Customizer"), CurrentJson("Captain_Chuncks_Mods", "Ship_Customizer", "0.9.0"))
            .On($"{Site}/api/experimental/package/Captain_Chuncks_Mods/Ship_Customizer/0.9.0/", VersionJson("Captain_Chuncks_Mods", "Ship_Customizer", "0.9.0"));
        using var service = new LauncherService(temp.AppPaths(), http);
        service.Library.AddOrReplace(new InstalledMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Source = ModSource.Thunderstore, Name = "Void_Crew_Fleet", Version = "0.3.0" });

        await service.RefreshPackagesAsync(true);
        var update = Assert.Single(service.Mods.Updates());
        Assert.Equal("0.4.0", update.Latest.VersionNumber);

        // A lobby runs Ship Customizer 0.9.0, which the list doesn't have.
        var room = new RoomDto { Id = "R1", Name = "Fleet night", Players = 1, MaxPlayers = 8, Modding = "session", Advert = "vcml1;Captain_Chuncks_Mods-Ship_Customizer@0.9.0!R" };
        Lobby before = LobbyReader.Read(room, "eu", service.Index);
        Assert.Null(Assert.Single(before.Mods).Package);
        Assert.Equal("Captain_Chuncks_Mods-Ship_Customizer", before.Mods[0].PackageId);
        var freshened = new List<string>();
        service.PackagesFreshened += ids => freshened.AddRange(ids);

        Assert.True(await service.FreshenLobbyAsync(before));
        Lobby after = LobbyReader.Read(room, "eu", service.Index);
        Assert.Equal("0.9.0", after.Mods[0].PackageVersion!.VersionNumber);
        Assert.Equal(new[] { "Captain_Chuncks_Mods-Ship_Customizer" }, freshened);
        Assert.Equal(LobbyModStatus.Download, service.Planner.Plan(after).Lines[0].Status);
    }

    // ---------------------------------------------------------------- the launcher's self-update

    private static string ReleaseJson(string tag, bool prerelease = false, string asset = InstallLocations.SetupName, string digest = "sha256:ABC123") => $$"""
        {"tag_name":"{{tag}}","name":"Void Crew Mod Launcher","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},
         "html_url":"https://github.com/CaptainChunck/VoidCrewModLauncher/releases/tag/{{tag}}",
         "assets":[{"name":"Void_Crew_Mod_Launcher.zip","size":5,"browser_download_url":"https://github.test/zip"},
                   {"name":"{{asset}}","size":1234,"digest":"{{digest}}","browser_download_url":"https://github.test/setup.exe"}]}
        """;

    private static LauncherRelease? Parse(string json, string current)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Updater.Parse(document.RootElement, current);
    }

    [Fact]
    public void Only_a_newer_published_release_with_the_setup_is_an_update()
    {
        LauncherRelease release = Parse(ReleaseJson("v0.2.0"), "0.1.0")!;
        Assert.Equal("0.2.0", release.Version);
        Assert.Equal("https://github.test/setup.exe", release.DownloadUrl);
        Assert.Equal(1234, release.Size);
        Assert.Equal("abc123", release.Sha256);

        Assert.Null(Parse(ReleaseJson("v0.2.0"), "0.2.0"));
        Assert.Null(Parse(ReleaseJson("v0.1.9"), "0.2.0"));
        Assert.Null(Parse(ReleaseJson("v0.3.0", prerelease: true), "0.2.0"));
        Assert.Null(Parse(ReleaseJson("v0.3.0", asset: "Something-else.exe"), "0.2.0"));
        Assert.Null(Parse(ReleaseJson("v0.3.0", digest: "md5:1"), "0.2.0")!.Sha256);
        Assert.Equal("0.10.0", Parse(ReleaseJson("0.10.0"), "0.9.0")!.Version);
    }

    [Fact]
    public async Task A_download_must_match_the_size_and_checksum_GitHub_lists()
    {
        byte[] setup = Encoding.UTF8.GetBytes("pretend new launcher");
        string sha = Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant();
        var http = new FakeHttp().On("https://github.test/setup.exe", setup);
        using var updater = new Updater(http);
        string version = "99.0." + Random.Shared.Next(1000, 9999);

        string file = await updater.DownloadAsync(new LauncherRelease(version, "https://github.test/setup.exe", setup.Length, sha, "page"), null);
        try
        {
            Assert.Equal(setup, File.ReadAllBytes(file));
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(new LauncherRelease(version, "https://github.test/setup.exe", setup.Length, new string('0', 64), "page"), null));
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(new LauncherRelease(version, "https://github.test/setup.exe", setup.Length + 1, sha, "page"), null));
            // Not a launcher exe: refused before it would ever run.
            Assert.Throws<InvalidDataException>(() => Updater.CheckSetup(file, version));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Applying_an_update_replaces_a_portable_exe_without_the_downloaded_mark()
    {
        using var temp = new TempFolder();
        string target = temp.File("portable/VoidCrewModLauncher.exe", "old launcher");
        string setup = temp.File("update/VoidCrewModLauncher-0.2.0.exe", "new launcher");
        File.WriteAllText(setup + Installer.ZoneStream, "[ZoneTransfer]\r\nZoneId=3\r\n");
        var locations = new InstallLocations(Path.Combine(temp.Path, "Programs"), Path.Combine(temp.Path, "StartMenu"), Path.Combine(temp.Path, "Desktop"), null);

        // A process id that isn't running: the old launcher has already closed.
        Installer.ApplyUpdate(setup, int.MaxValue, target, locations, "0.2.0");

        Assert.Equal("new launcher", File.ReadAllText(target));
        Assert.Throws<FileNotFoundException>(() => File.ReadAllText(target + Installer.ZoneStream));
        Assert.False(File.Exists(locations.InstalledExe));
    }
}
