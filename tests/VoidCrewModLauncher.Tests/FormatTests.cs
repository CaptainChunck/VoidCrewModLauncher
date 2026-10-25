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

    // ---------------------------------------------------------------- lobby data from other players

    [Fact]
    public void An_advert_far_longer_than_any_launcher_writes_is_not_read()
    {
        string entry = ";Someone-Mod@1.0.0!R";
        string longest = "vcml1" + string.Concat(Enumerable.Repeat(entry, (LobbyAdvert.MaxTextLength - 5) / entry.Length));
        Assert.True(longest.Length <= LobbyAdvert.MaxTextLength);
        Assert.NotNull(LobbyAdvert.Decode(longest));
        Assert.Null(LobbyAdvert.Decode(longest + new string('x', LobbyAdvert.MaxTextLength)));
    }

    [Fact]
    public void An_advert_is_read_up_to_its_mod_limit()
    {
        string many = "vcml1" + string.Concat(Enumerable.Range(0, 500).Select(i => $";M{i}@1!R"));
        Assert.True(many.Length <= LobbyAdvert.MaxTextLength);
        List<AdvertMod> mods = LobbyAdvert.Decode(many)!;
        Assert.Equal(LobbyAdvert.MaxMods, mods.Count);
        Assert.Equal("M0", mods[0].Id);
    }

    [Fact]
    public void Advert_entries_with_overlong_ids_or_versions_are_left_out()
    {
        string id = new('a', LobbyAdvert.MaxIdLength);
        string version = new('1', LobbyAdvert.MaxVersionLength);
        string text = $"vcml1;{id}@1.0!R;{id}b@1.0!R;#Local@{version}!C;#Other@{version}1!C;Fine-Mod@2.0!H";
        Assert.Equal(new[] { id, "Local", "Fine-Mod" }, LobbyAdvert.Decode(text)!.Select(m => m.Id));
    }

    [Fact]
    public void Names_from_other_players_lose_control_and_direction_characters()
    {
        // A right-to-left override makes "Evil\u202Egnp.exe" show as "Evilexe.png"; a bell, a line break and isolates
        // would break the line it is shown or logged on.
        string text = "vcml1;#Evil\u202Egnp.exe@1.0\u200F!R:jack.\u2066evil\u2069;Some\u0007one-Mod\n@2.0!C";
        List<AdvertMod> mods = LobbyAdvert.Decode(text)!;
        Assert.Equal(new[] { "Evilgnp.exe", "Someone-Mod" }, mods.Select(m => m.Id));
        Assert.Equal(new[] { "1.0", "2.0" }, mods.Select(m => m.Version));
        Assert.Equal(new[] { "jack.evil" }, mods[0].PluginGuids);
        Assert.Equal(ModNeed.Everyone, mods[0].Need);
        Assert.Equal(ModNeed.ClientOnly, mods[1].Need);

        string plain = "Void Crew Fleet (日本語, Ελληνικά, \u05E2\u05D1\u05E8\u05D9\u05EA)";
        Assert.Same(plain, LobbyAdvert.CleanName(plain));
        Assert.Equal("", LobbyAdvert.CleanName(null!));

        byte[] list = VoidManagerModList.Encode("1.2.11\u202E", new[] { new VoidManagerMod("Max\u202E Players\r\n", "Voi\u200Ed.Max", "1.1.2\u0000", 30, "Nihility\u2067Shift/Max") });
        (string Version, List<VoidManagerMod> Mods) read = VoidManagerModList.Decode(list)!.Value;
        Assert.Equal("1.2.11", read.Version);
        Assert.Equal(new VoidManagerMod("Max Players", "Void.Max", "1.1.2", 30, "NihilityShift/Max"), read.Mods.Single());

        // The room's name too, as the launcher shows and logs it.
        Lobby lobby = LobbyReader.Read(new RoomDto { Id = "R", Name = "[Mods Required] Fun\u202E room\u0007" }, "eu", Packages.Index());
        Assert.Equal("Fun room", lobby.Name);
    }

    [Fact]
    public void A_VoidManager_list_is_read_within_limits()
    {
        // An entry with an over-long name, GUID, version or id is left out; the rest of the list still reads.
        string longName = new('n', LobbyAdvert.MaxIdLength + 1);
        byte[] data = VoidManagerModList.Encode("1.2.11", new[]
        {
            new VoidManagerMod(longName, "a.guid", "1.0.0", 30, ""),
            new VoidManagerMod("Long GUID", new string('g', LobbyAdvert.MaxGuidLength + 1), "1.0.0", 30, ""),
            new VoidManagerMod("Long version", "c.guid", new string('1', LobbyAdvert.MaxVersionLength + 1), 30, ""),
            new VoidManagerMod("Long id", "d.guid", "1.0.0", 30, new string('i', LobbyAdvert.MaxIdLength + 1)),
            new VoidManagerMod("Max Players", "VoidCrewModdingTeam.MaxPlayers", "1.1.2", 30, ""),
        });
        Assert.Equal(new[] { "Max Players" }, VoidManagerModList.Decode(data)!.Value.Mods.Select(m => m.Name));
        Assert.Null(VoidManagerModList.Decode(VoidManagerModList.Encode(new string('9', LobbyAdvert.MaxVersionLength + 1), Array.Empty<VoidManagerMod>())));

        // A list bigger than any real one isn't read at all, as bytes or as the base64 the bridge sends.
        VoidManagerMod filler = new(new string('n', 150), "x.guid", "1.0.0", 30, new string('i', 150));
        byte[] fits = VoidManagerModList.Encode("1.2.11", Enumerable.Repeat(filler, LobbyAdvert.MaxVoidManagerListBytes / 320));
        Assert.True(fits.Length <= LobbyAdvert.MaxVoidManagerListBytes);
        Assert.NotNull(VoidManagerModList.Decode(Convert.ToBase64String(fits)));
        byte[] tooBig = VoidManagerModList.Encode("1.2.11", Enumerable.Repeat(filler, LobbyAdvert.MaxVoidManagerListBytes / 300));
        Assert.True(tooBig.Length > LobbyAdvert.MaxVoidManagerListBytes);
        Assert.Null(VoidManagerModList.Decode(tooBig));
        Assert.Null(VoidManagerModList.Decode(Convert.ToBase64String(tooBig)));
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
