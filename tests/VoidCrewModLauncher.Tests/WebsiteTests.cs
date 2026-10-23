using System.Security.Cryptography;
using System.Text.Json;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Core.Website;

namespace VoidCrewModLauncher.Tests;

public class WebsiteTests
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static FakeHttp Thunderstore(FakeHttp http, params TsPackage[] packages) => http
        .On(ThunderstoreClient.ListingIndexUrl, FakeHttp.Gzip("[\"https://cdn.test/chunk.blob\"]"))
        .On("https://cdn.test/chunk.blob", FakeHttp.Gzip(JsonSerializer.Serialize(packages, Snake)));

    private static TsPackage SiteFleet(string version, byte[] zip, string? sha = null)
    {
        TsPackage package = Packages.Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { version });
        package.PackageUrl = "https://voidcrewmod.com/mods/void-crew-fleet";
        package.Versions[0].DownloadUrl = $"https://voidcrewmod.com/download/fleet-{version}.zip";
        package.Versions[0].Sha256 = sha ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        return package;
    }

    [Fact]
    public async Task The_websites_mods_come_before_Thunderstores()
    {
        using var temp = new TempFolder();
        byte[] zip = FakeHttp.Zip(("manifest.json", "{\"name\":\"Void_Crew_Fleet\",\"version_number\":\"0.4.0\"}"), ("VoidCrewFleet.dll", "from the website"));
        TsPackage onlyOnSite = Packages.Make("Someone", "Site_Only", new[] { "1.0.0" });
        var http = Thunderstore(new FakeHttp(), Packages.Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { "0.4.0", "0.3.0" }))
            .On(WebsiteClient.PackageListUrl, JsonSerializer.Serialize(new[] { SiteFleet("0.4.0", zip), onlyOnSite }, Snake))
            .On("https://voidcrewmod.com/download/fleet-0.4.0.zip", zip);
        using var service = new LauncherService(temp.AppPaths(), http);
        service.Settings.GitHubUpdates = false;
        await service.RefreshPackagesAsync(true);

        TsPackage fleet = service.Index.Find("Captain_Chuncks_Mods-Void_Crew_Fleet")!;
        Assert.True(fleet.Latest!.FromWebsite);
        Assert.Equal("https://voidcrewmod.com/mods/void-crew-fleet", fleet.PackageUrl);
        Assert.Equal(new[] { "0.4.0", "0.3.0" }, fleet.Versions.Select(v => v.VersionNumber));
        Assert.False(fleet.Version("0.3.0")!.FromWebsite);
        Assert.Contains(service.Index.Mods, p => p.FullName == "Someone-Site_Only");

        await service.InstallModAsync("Captain_Chuncks_Mods-Void_Crew_Fleet", null);
        string folder = service.Store.VersionDirectory("Captain_Chuncks_Mods-Void_Crew_Fleet", "0.4.0");
        Assert.Equal("from the website", File.ReadAllText(Path.Combine(folder, "VoidCrewFleet.dll")));
    }

    [Fact]
    public async Task A_website_download_that_doesnt_match_its_checksum_is_refused()
    {
        using var temp = new TempFolder();
        byte[] zip = FakeHttp.Zip(("VoidCrewFleet.dll", "tampered"));
        var http = Thunderstore(new FakeHttp())
            .On(WebsiteClient.PackageListUrl, JsonSerializer.Serialize(new[] { SiteFleet("0.4.0", zip, new string('0', 64)) }, Snake))
            .On("https://voidcrewmod.com/download/fleet-0.4.0.zip", zip);
        using var service = new LauncherService(temp.AppPaths(), http);
        await service.RefreshPackagesAsync(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallModAsync("Captain_Chuncks_Mods-Void_Crew_Fleet", null));
        Assert.False(service.Store.Has("Captain_Chuncks_Mods-Void_Crew_Fleet", "0.4.0"));
        Assert.Null(service.Library.Find("Captain_Chuncks_Mods-Void_Crew_Fleet"));
    }

    [Fact]
    public async Task Without_the_website_Thunderstore_covers_everything()
    {
        using var temp = new TempFolder();
        var http = Thunderstore(new FakeHttp(), Packages.Make("Captain_Chuncks_Mods", "Void_Crew_Fleet", new[] { "0.4.0" }));
        using var service = new LauncherService(temp.AppPaths(), http);
        await service.RefreshPackagesAsync(true);
        Assert.False(service.Index.Find("Captain_Chuncks_Mods-Void_Crew_Fleet")!.Latest!.FromWebsite);
    }
}
