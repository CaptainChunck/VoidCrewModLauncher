using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Steam;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Tests;

public class FormatTests
{
    [Fact]
    public void Advert_round_trips()
    {
        var mods = new[]
        {
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Version = "0.3.0", Need = ModNeed.Everyone },
            new AdvertMod { Id = "NihilityShift-VoidManager", Version = "1.2.11", Need = ModNeed.ClientOnly },
            new AdvertMod { Id = "UUVR (modern)", Version = "1.0", Need = ModNeed.ClientOnly, Local = true },
            new AdvertMod { Id = "Someone-Mystery", Version = "2.0.0", Need = ModNeed.Unknown },
        };
        string text = LobbyAdvert.Encode(mods);
        Assert.StartsWith("vcml1;", text);

        List<AdvertMod>? back = LobbyAdvert.Decode(text);
        Assert.NotNull(back);
        Assert.Equal(4, back!.Count);
        Assert.Equal("Captain_Chuncks_Mods-Void_Crew_Fleet", back[0].Id);
        Assert.Equal("0.3.0", back[0].Version);
        Assert.Equal(ModNeed.Everyone, back[0].Need);
        Assert.True(back[2].Local);
        Assert.Equal("UUVR (modern)", back[2].Id);
        Assert.Equal(ModNeed.Unknown, back[3].Need);
    }

    [Fact]
    public void Advert_strips_separators_from_names()
    {
        string text = LobbyAdvert.Encode(new[] { new AdvertMod { Id = "My;Mod@!#", Version = "1;0", Need = ModNeed.HostOnly, Local = true } });
        List<AdvertMod> back = LobbyAdvert.Decode(text)!;
        Assert.Single(back);
        Assert.Equal("MyMod", back[0].Id);
        Assert.Equal("10", back[0].Version);
        Assert.Equal(ModNeed.HostOnly, back[0].Need);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something else")]
    [InlineData("vcml2;A-B@1.0.0!R")]
    public void Advert_ignores_what_it_does_not_know(string? text)
    {
        Assert.Null(LobbyAdvert.Decode(text));
    }

    [Fact]
    public void VoidManager_list_decodes()
    {
        byte[] data = VoidManagerModList.Encode("1.2.11", new[]
        {
            new VoidManagerMod("VoidManager", "VoidCrewModdingTeam.VoidManager", "1.2.11", 8, "NihilityShift/VoidManager"),
            new VoidManagerMod("Max Players", "VoidCrewModdingTeam.MaxPlayers", "1.1.2", 30, ""),
            new VoidManagerMod("Hidden", "x.hidden", "1.0.0", 0, ""),
        });
        (string Version, List<VoidManagerMod> Mods)? list = VoidManagerModList.Decode(Convert.ToBase64String(data));
        Assert.NotNull(list);
        Assert.Equal("1.2.11", list!.Value.Version);
        Assert.Equal(3, list.Value.Mods.Count);
        Assert.Equal(30, list.Value.Mods[1].MultiplayerType);
        Assert.Equal("NihilityShift/VoidManager", list.Value.Mods[0].DownloadId);
    }

    [Fact]
    public void VoidManager_list_rejects_garbage()
    {
        Assert.Null(VoidManagerModList.Decode("not base64!"));
        Assert.Null(VoidManagerModList.Decode(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("BepInEx-BepInExPack-5.4.2305", "BepInEx", "BepInExPack", "5.4.2305")]
    [InlineData("Captain_Chuncks_Mods-Void_Crew_Fleet-0.3.0", "Captain_Chuncks_Mods", "Void_Crew_Fleet", "0.3.0")]
    [InlineData("NihilityShift-VoidManager", "NihilityShift", "VoidManager", null)]
    public void Dependency_strings_parse(string text, string owner, string name, string? version)
    {
        PackageReference? reference = PackageReference.Parse(text);
        Assert.NotNull(reference);
        Assert.Equal(owner, reference!.Value.Namespace);
        Assert.Equal(name, reference.Value.Name);
        Assert.Equal(version, reference.Value.Version);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0.0", 0)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("0.3.0", "0.7.1", -1)]
    [InlineData("v2.0", "2.0.0", 0)]
    [InlineData("1.0.0-beta", "1.0.0", 0)]
    public void Versions_compare(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(VersionNumber.Compare(a, b)));
    }

    [Fact]
    public void Steam_library_folders_parse()
    {
        const string text = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "1" }
                }
                // a comment
                "1"
                {
                    "path"		"G:\\SteamLibrary"
                }
            }
            """;
        VdfNode root = Vdf.Parse(text);
        VdfNode folders = root["libraryfolders"]!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders["0"]!.Get("path"));
        Assert.Equal(@"G:\SteamLibrary", folders["1"]!.Get("path"));
        Assert.Equal("1", folders["0"]!["apps"]!.Get("228980"));
    }

    [Fact]
    public void Package_index_finds_by_id_forms()
    {
        PackageIndex index = Packages.Index();
        Assert.Equal("NihilityShift-VoidManager", index.Find("NihilityShift/VoidManager")?.FullName);
        Assert.Equal("NihilityShift-VoidManager", index.Find("https://thunderstore.io/c/void-crew/p/NihilityShift/VoidManager/")?.FullName);
        Assert.Equal("NihilityShift-VoidManager", index.Find("NihilityShift-VoidManager-1.2.11")?.FullName);
        Assert.Null(index.Find("Nobody-Nothing"));
    }

    [Theory]
    [InlineData("Void Crew Fleet", "Captain_Chuncks_Mods-Void_Crew_Fleet")]
    [InlineData("VoidCrewShipCustomizer", "Captain_Chuncks_Mods-Ship_Customizer")]
    [InlineData("Void Crew VR UI Fix", "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix")]
    [InlineData("VoidManager", "NihilityShift-VoidManager")]
    [InlineData("Max Players", "VoidCrewModdingTeam-Max_Players")]
    public void Package_index_finds_by_mod_name(string name, string expected)
    {
        Assert.Equal(expected, Packages.Index().FindByName(name)?.FullName);
    }

    [Fact]
    public void Package_index_hides_bepinex_and_tools_from_mods()
    {
        PackageIndex index = Packages.Index();
        Assert.DoesNotContain(index.Mods, p => p.FullName == "ebkr-r2modman");
        Assert.DoesNotContain(index.Mods, p => p.FullName == PackageIndex.BepInExPackId);
        Assert.NotNull(index.Find(PackageIndex.BepInExPackId));
    }
}
