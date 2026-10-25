using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.GitHub;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Tests;

/// <summary>Answers some URLs with a response made for the test (headers, a stalled body), the rest from a FakeHttp.</summary>
public sealed class ScriptedHttp : DelegatingHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public ScriptedHttp(FakeHttp fake) : base(fake)
    {
        Fake = fake;
    }

    public FakeHttp Fake { get; }

    public ScriptedHttp On(string url, Func<HttpResponseMessage> answer)
    {
        _answers[url] = answer;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.ToString();
        if (!_answers.TryGetValue(url, out Func<HttpResponseMessage>? answer)) return base.SendAsync(request, cancellationToken);
        lock (Fake.Requests) Fake.Requests.Add(url);
        return Task.FromResult(answer());
    }
}

/// <summary>A response body that sends a few bytes and then nothing, like a stalled CDN.</summary>
public sealed class StallingStream : Stream
{
    private bool _sent;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_sent)
        {
            _sent = true;
            buffer.Span[..10].Fill(1);
            return 10;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public class ReliabilityTests
{
    private const string Owner = "Captain_Chuncks_Mods";
    private const string FleetId = "Captain_Chuncks_Mods-Void_Crew_Fleet";
    private const string FleetRepo = "https://github.com/CaptainChunck/VoidCrewFleet";

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static FakeHttp ThunderstoreList(FakeHttp http, params TsPackage[] packages) => http
        .On(ThunderstoreClient.ListingIndexUrl, FakeHttp.Gzip("[\"https://cdn.test/chunk.blob\"]"))
        .On("https://cdn.test/chunk.blob", FakeHttp.Gzip(JsonSerializer.Serialize(packages, Snake)));

    private static string Release(string tag, params (string Name, byte[] Body)[] assets) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft = false,
        prerelease = false,
        html_url = $"{FleetRepo}/releases/tag/{tag}",
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

    /// <summary>Void Crew Fleet 0.4.0 as Thunderstore lists it, with <paramref name="website"/> as its website.</summary>
    private static TsPackage Fleet(string website = FleetRepo)
    {
        TsPackage fleet = Packages.Make(Owner, "Void_Crew_Fleet", new[] { "0.4.0" }, new[] { "Mods" });
        fleet.Versions[0].WebsiteUrl = website;
        return fleet;
    }

    private static InstalledMod Installed(string id, string version) =>
        new() { Id = id, Source = ModSource.Thunderstore, Name = id.Split('-')[1], Author = Owner, Version = version };

    [Fact]
    public async Task A_release_of_another_projects_repo_isnt_offered_as_the_package()
    {
        // The mod's website is BepInEx's repo: its releases aren't the mod.
        using var temp = new TempFolder();
        var http = ThunderstoreList(new FakeHttp(), Fleet("https://github.com/BepInEx/BepInEx"))
            .On("https://api.github.com/repos/BepInEx/BepInEx/releases/latest", Release("v5.4.23", ("BepInEx_win_x64_5.4.23.zip", new byte[3])));
        using var service = new LauncherService(temp.AppPaths(), http);
        service.Library.AddOrReplace(Installed(FleetId, "0.4.0"));

        await service.RefreshPackagesAsync(true);
        Assert.Contains(http.Requests, r => r.EndsWith("/repos/BepInEx/BepInEx/releases/latest", StringComparison.Ordinal));
        Assert.Equal("0.4.0", service.Index.Find(FleetId)!.Latest!.VersionNumber);
        Assert.Empty(service.Mods.Updates());

        // A repo of many mods: only a zip or DLL named like the package is it.
        var monorepo = new GitHubRepo("CaptainChunck", "VoidCrewMods");
        TsVersion? Version(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return GitHubClient.ToVersion(document.RootElement, monorepo, Fleet());
        }
        Assert.Null(Version(Release("v0.5.0", ("Ship_Customizer-0.5.0.zip", new byte[3]))));
        Assert.Null(Version(Release("v0.5.0", ("ShipCustomizer.dll", new byte[3]))));
        Assert.Equal("Void_Crew_Fleet-0.5.0.zip", Version(Release("v0.5.0", ("Ship_Customizer-0.5.0.zip", new byte[3]), ("Void_Crew_Fleet-0.5.0.zip", new byte[4])))!.GitHub!.Package!.Name);
        Assert.Equal("VoidCrewFleet.dll", Assert.Single(Version(Release("v0.5.0", ("ShipCustomizer.dll", new byte[3]), ("VoidCrewFleet.dll", new byte[4])))!.GitHub!.Plugins).Name);
    }

    [Fact]
    public async Task A_GitHub_version_that_fails_falls_back_to_Thunderstore_and_isnt_offered_again()
    {
        using var temp = new TempFolder();
        TsPackage fleet = Fleet();
        byte[] notFleet = FakeHttp.Zip(("manifest.json", "{\"name\":\"Something_Else\",\"version_number\":\"0.4.1\"}"), ("VoidCrewFleet.dll", "not fleet"));
        var http = ThunderstoreList(new FakeHttp(), fleet)
            .On(fleet.Versions[0].DownloadUrl, FakeHttp.Zip(("manifest.json", "{\"name\":\"Void_Crew_Fleet\",\"version_number\":\"0.4.0\"}"), ("VoidCrewFleet.dll", "fleet 0.4.0")))
            .On("https://api.github.com/repos/CaptainChunck/VoidCrewFleet/releases/latest", Release("v0.4.1", ("Void_Crew_Fleet-0.4.1.zip", notFleet)))
            .On("https://github.test/v0.4.1/Void_Crew_Fleet-0.4.1.zip", notFleet);
        using var service = new LauncherService(temp.AppPaths(), http);
        await service.RefreshPackagesAsync(true);
        await service.FreshenAsync(new[] { FleetId });
        Assert.Equal("0.4.1", service.Index.Find(FleetId)!.Latest!.VersionNumber);

        InstalledMod mod = await service.InstallModAsync(FleetId, null);
        Assert.Equal("0.4.0", mod.Version);
        Assert.True(service.Store.Has(FleetId, "0.4.0"));
        Assert.False(service.Store.Has(FleetId, "0.4.1"));
        Assert.Equal("fleet 0.4.0", File.ReadAllText(Path.Combine(service.Store.VersionDirectory(FleetId, "0.4.0"), "VoidCrewFleet.dll")));
        Assert.Equal("0.4.0", service.Index.Find(FleetId)!.Latest!.VersionNumber);
        Assert.Empty(service.Mods.Updates());

        // Refreshing doesn't bring it back this session.
        await service.RefreshPackagesAsync(true);
        Assert.Equal("0.4.0", service.Index.Find(FleetId)!.Latest!.VersionNumber);
        Assert.Empty(service.Mods.Updates());
    }

    [Fact]
    public async Task Only_GitHubs_request_limit_stops_GitHub_lookups()
    {
        using var temp = new TempFolder();
        const string Api = GitHubClient.Api;
        var fake = new FakeHttp()
            .On(Api + "/repos/a/blocked/releases/latest", "{\"message\":\"Repository access blocked\"}", HttpStatusCode.Forbidden)
            .On(Api + "/repos/a/fine/releases/latest", Release("v1.0.0"))
            .On(Api + "/repos/a/other/releases/latest", Release("v1.0.0"));
        var http = new ScriptedHttp(fake).On(Api + "/repos/a/limited/releases/latest", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
            return response;
        });
        using var client = new GitHubClient(temp.AppPaths(), http);

        // A 403 that isn't the limit (a blocked repo): only that answer fails.
        Assert.Null(await client.GetAsync("/repos/a/blocked/releases/latest"));
        using (JsonDocument? fine = await client.GetAsync("/repos/a/fine/releases/latest")) Assert.NotNull(fine);

        // The limit used up: GitHub isn't asked again until it resets.
        Assert.Null(await client.GetAsync("/repos/a/limited/releases/latest"));
        int asked = fake.Requests.Count;
        Assert.Null(await client.GetAsync("/repos/a/other/releases/latest"));
        Assert.Equal(asked, fake.Requests.Count);
    }

    [Fact]
    public async Task Release_answers_that_arent_a_release_are_ignored()
    {
        using var temp = new TempFolder();
        const string Api = GitHubClient.Api;
        TsPackage package = Packages.Make("a", "b", new[] { "0.1.0" });
        string Releases(string repo) => $"{Api}/repos/a/{repo}/releases?per_page={GitHubClient.ReleasesLookedUp}";
        var http = new FakeHttp()
            .On(Api + "/repos/a/b/releases/latest", "[1,2]")
            .On(Releases("object"), "{\"tag_name\":\"v1.0.0\"}")
            .On(Releases("junk"), "[1,\"x\",null,[],{\"tag_name\":7},{\"tag_name\":\"v2.0.0\",\"assets\":[1,\"x\",null,[]]}]")
            .On(Releases("html"), "<html>not json</html>");
        using var client = new GitHubClient(temp.AppPaths(), http);

        Assert.Null(await client.GetLatestReleaseAsync(new GitHubRepo("a", "b"), package));
        Assert.Null(await client.GetReleaseAsync(new GitHubRepo("a", "object"), package, "1.0.0"));
        Assert.Null(await client.GetReleaseAsync(new GitHubRepo("a", "junk"), package, "2.0.0"));
        Assert.Null(await client.GetReleaseAsync(new GitHubRepo("a", "html"), package, "3.0.0"));
        // Not cached: asked again, still no exception.
        int html = http.Requests.Count(r => r == Releases("html"));
        Assert.Null(await client.GetReleaseAsync(new GitHubRepo("a", "html"), package, "3.0.0"));
        Assert.Equal(html + 1, http.Requests.Count(r => r == Releases("html")));

        // A lobby's version that isn't a version isn't looked for at all.
        int asked = http.Requests.Count;
        Assert.Null(await client.GetReleaseAsync(new GitHubRepo("a", "b"), package, ".."));
        Assert.Null(await client.GetReleaseAsync(new GitHubRepo("a", "b"), package, "1.0/../.."));
        Assert.Equal(asked, http.Requests.Count);
    }

    [Fact]
    public async Task A_download_tells_its_progress_about_ten_times_a_second()
    {
        // Ten thousand small pieces as fast as they come: one report per piece used to queue a redraw for each.
        var source = new MemoryStream(new byte[10_000 * 16]);
        var reports = new List<long>();
        long done = await ThunderstoreClient.CopyAsync(new Pieces(source, 16), Stream.Null, TimeSpan.FromSeconds(10), null, reports.Add, default);
        Assert.Equal(160_000, done);
        Assert.InRange(reports.Count, 2, 100);
        Assert.Equal(160_000, reports[^1]);
    }

    /// <summary>A stream that gives at most <paramref name="size"/> bytes per read, as a network stream often does.</summary>
    private sealed class Pieces(Stream inner, int size) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, size));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, size)], cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_stalled_launcher_update_fails_instead_of_waiting_forever()
    {
        HttpResponseMessage Stalled() => new(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        var http = new ScriptedHttp(new FakeHttp()).On("https://github.test/setup.exe", Stalled);
        using var updater = new Core.Setup.Updater(http) { StallTimeout = TimeSpan.FromSeconds(1) };
        var clock = Stopwatch.StartNew();
        string version = "99.1." + Random.Shared.Next(1000, 9999);
        IOException e = await Assert.ThrowsAsync<IOException>(() => updater.DownloadAsync(new Core.Setup.LauncherRelease(version, "https://github.test/setup.exe", 100, null, "page"), null));
        Assert.Contains("stalled", e.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
        // Nothing half-downloaded is left behind.
        Assert.False(File.Exists(Path.Combine(Core.Setup.Updater.DownloadFolder, $"VoidCrewModLauncher-{version}.exe.part")));
    }

    [Fact]
    public async Task A_stalled_download_fails_instead_of_waiting_forever()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        HttpResponseMessage Stalled() => new(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        var http = new ScriptedHttp(new FakeHttp()).On("https://cdn.test/stall.zip", Stalled).On("https://github.test/stall.zip", Stalled);
        TsVersion version = Packages.Make(Owner, "Void_Crew_Fleet", new[] { "0.4.0" }).Versions[0];
        version.DownloadUrl = "https://cdn.test/stall.zip";

        using (var thunderstore = new ThunderstoreClient(paths, http) { StallTimeout = TimeSpan.FromSeconds(1) })
        {
            var clock = Stopwatch.StartNew();
            IOException e = await Assert.ThrowsAsync<IOException>(() => thunderstore.DownloadAsync(version, null));
            Assert.Contains("stalled", e.Message);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
        }
        using (var github = new GitHubClient(paths, http) { StallTimeout = TimeSpan.FromSeconds(1) })
        {
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<IOException>(() => github.DownloadAsync(new GitHubAsset("stall.zip", "https://github.test/stall.zip", 100, null), null));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
        }
        // Nothing half-downloaded is left behind.
        Assert.Empty(Directory.GetFiles(paths.Downloads));
    }

    [Fact]
    public async Task BepInEx_already_downloaded_is_used_when_a_newer_one_cant_be()
    {
        using var temp = new TempFolder();
        AppPaths paths = temp.AppPaths();
        using var thunderstore = new ThunderstoreClient(paths, new FakeHttp());
        var store = new PackageStore(paths, thunderstore);
        var manager = new ModManager(new ModLibrary(paths), store)
        {
            Index = new PackageIndex(new[] { Packages.Make("BepInEx", "BepInExPack", new[] { "5.4.2305" }) }),
        };

        // Nothing stored: the failed download is the error.
        await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureBepInExAsync());

        string stored = store.VersionDirectory(PackageIndex.BepInExPackId, "5.4.2100");
        Directory.CreateDirectory(stored);
        Assert.Equal(stored, await manager.EnsureBepInExAsync());

        // Cancelled by the player: no fallback.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureBepInExAsync(null, cancelled.Token));
    }
}
