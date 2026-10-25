using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Core.Thunderstore;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Lobbies;

/// <summary>Where a lobby's mod list came from.</summary>
public enum LobbyModSource
{
    /// <summary>The host's game says nothing about its mods (vanilla, or modded without VoidManager or the launcher).</summary>
    None,

    /// <summary>The host uses this launcher: exact Thunderstore ids and versions.</summary>
    Advert,

    /// <summary>The host runs VoidManager, which lists mod names, versions and multiplayer types.</summary>
    VoidManager,
}

/// <summary>A mod a lobby's host runs.</summary>
public sealed class LobbyMod
{
    public string Name { get; init; } = "";

    /// <summary>
    /// Its BepInEx plugin GUIDs, from the advert (local mods) or VoidManager's list: what identifies a local mod on
    /// every PC, since its id comes from the file it was added from.
    /// </summary>
    public List<string> PluginGuids { get; init; } = new();

    public string Version { get; init; } = "";
    public ModNeed Need { get; set; }

    /// <summary>The need came from the host's game (advert or VoidManager), not from the launcher's guess.</summary>
    public bool NeedFromHost { get; set; }

    /// <summary>The host's own local mod: not on Thunderstore.</summary>
    public bool Local { get; init; }

    /// <summary>The Thunderstore id ("Namespace-Name") the host's game gave, even when the package list doesn't know it yet.</summary>
    public string? PackageId { get; init; }

    public TsPackage? Package { get; set; }

    /// <summary>The exact version on Thunderstore, when it has it.</summary>
    public TsVersion? PackageVersion { get; set; }

    public string Id => Package?.FullName ?? Name;

    public string DisplayName => Package?.DisplayName ?? Name.Replace('_', ' ');

    /// <summary>Joining players have to load it (VoidManager "All", or nothing says otherwise).</summary>
    public bool JoinerNeeds => ModPolicy.JoinerNeeds(Need);

    /// <summary>The launcher can download it.</summary>
    public bool Downloadable => !Local && Package != null;
}

/// <summary>A room from the game's list, with its mods read from the lobby properties.</summary>
public sealed class Lobby
{
    public Lobby(RoomDto room, string? region, List<LobbyMod> mods, LobbyModSource source, string? voidManagerVersion)
    {
        Room = room;
        Region = region;
        Mods = mods;
        ModSource = source;
        VoidManagerVersion = voidManagerVersion;
    }

    public RoomDto Room { get; }
    public string? Region { get; }
    public List<LobbyMod> Mods { get; }
    public LobbyModSource ModSource { get; }
    public string? VoidManagerVersion { get; }

    public string Id => Room.Id;

    /// <summary>The room name without the "[Mods Required]" VoidManager puts in front of it.</summary>
    public string Name
    {
        get
        {
            string name = Room.Name ?? "";
            const string required = "[Mods Required]";
            if (name.StartsWith(required, StringComparison.OrdinalIgnoreCase)) name = name[required.Length..].TrimStart();
            return name.Length == 0 ? Loc.T("Unnamed lobby") : name;
        }
    }

    public bool IsSessionModded => Room.Modding == "session";

    public bool IsModded => IsSessionModded || Room.Modding == "local" || Mods.Count > 0;

    public bool IsFull => Room.Players >= Room.MaxPlayers;

    public IEnumerable<LobbyMod> NeededMods => Mods.Where(m => m.JoinerNeeds);

    /// <summary>Modded, but nothing says which mods (the host has neither VoidManager nor the launcher).</summary>
    public bool ModsUnknown => IsSessionModded && ModSource == LobbyModSource.None;
}

/// <summary>Reads a room's mods from the launcher's advert and VoidManager's list, and matches them to Thunderstore.</summary>
public static class LobbyReader
{
    public static Lobby Read(RoomDto room, string? region, PackageIndex index)
    {
        var mods = new List<LobbyMod>();
        LobbyModSource source = LobbyModSource.None;

        List<AdvertMod>? advert = LobbyAdvert.Decode(room.Advert);
        if (advert != null)
        {
            source = LobbyModSource.Advert;
            foreach (AdvertMod mod in advert)
            {
                TsPackage? package = mod.Local ? null : index.Find(mod.Id);
                mods.Add(new LobbyMod
                {
                    Name = package?.DisplayName ?? mod.Id,
                    PluginGuids = mod.PluginGuids?.ToList() ?? new List<string>(),
                    Version = mod.Version,
                    Need = mod.Need,
                    NeedFromHost = mod.Need != ModNeed.Unknown,
                    Local = mod.Local,
                    PackageId = mod.Local ? null : PackageIndex.NormalizeId(mod.Id),
                    Package = package,
                    PackageVersion = package?.Version(mod.Version),
                });
            }
        }

        (string Version, List<VoidManagerMod> Mods)? voidManager = VoidManagerModList.Decode(room.VoidManagerMods);
        if (voidManager != null)
        {
            if (source == LobbyModSource.None) source = LobbyModSource.VoidManager;
            foreach (VoidManagerMod mod in voidManager.Value.Mods)
            {
                if (mod.MultiplayerType == 0 || mod.Guid == BridgeDefaults.PluginGuid) continue;
                TsPackage? package = index.Find(mod.DownloadId) ?? index.FindByName(mod.Name);
                ModNeed need = LobbyAdvert.FromVoidManagerType(mod.MultiplayerType);
                LobbyMod? same = mods.FirstOrDefault(m => m.PluginGuids.Contains(mod.Guid, StringComparer.OrdinalIgnoreCase))
                                 ?? mods.FirstOrDefault(m => (package != null && m.Package == package) || PackageIndex.Simplify(m.Name) == PackageIndex.Simplify(mod.Name));
                if (same != null)
                {
                    if (!string.IsNullOrEmpty(mod.Guid) && !same.PluginGuids.Contains(mod.Guid, StringComparer.OrdinalIgnoreCase)) same.PluginGuids.Add(mod.Guid);
                    // VoidManager turns away joining players without an "All" mod, whatever the advert says (a host before
                    // launcher 0.8.1 could pick less on the HOST tab).
                    if (need == ModNeed.Everyone || (!same.NeedFromHost && need != ModNeed.Unknown))
                    {
                        same.Need = need;
                        same.NeedFromHost = true;
                    }
                    continue;
                }
                // The launcher's advert already lists every mod of the launch; VoidManager's extra entries are plugins
                // inside those packages.
                if (advert != null) continue;
                mods.Add(new LobbyMod
                {
                    Name = mod.Name,
                    PluginGuids = { mod.Guid },
                    Version = mod.Version,
                    Need = need,
                    NeedFromHost = need != ModNeed.Unknown,
                    PackageId = package?.FullName ?? PackageIndex.NormalizeId(mod.DownloadId),
                    Package = package,
                    PackageVersion = package?.Version(mod.Version),
                });
            }
        }

        // Once the host has a VoidManager "All" mod (VoidManager puts "[Mods Required]" in front of the room name then),
        // VoidManager turns away joining players who don't run it, so everyone needs VoidManager itself. It isn't in its
        // own list, and hosts before launcher 0.8.1 advertised it as a library only.
        bool voidManagerChecks = voidManager?.Mods.Any(m => m.MultiplayerType == (byte)VoidManagerType.All) == true
                                 || (room.Name ?? "").StartsWith("[Mods Required]", StringComparison.OrdinalIgnoreCase);
        if (voidManagerChecks)
        {
            List<LobbyMod> carriers = mods.Where(m => ModPolicy.IsVoidManager(m.Package?.FullName ?? m.PackageId)
                                                      || m.PluginGuids.Contains(ModPolicy.VoidManagerGuid, StringComparer.OrdinalIgnoreCase)).ToList();
            if (carriers.Count == 0 && voidManager != null && ModPolicy.VoidManagerIds.Select(index.Find).FirstOrDefault(p => p != null) is { } package)
            {
                var added = new LobbyMod
                {
                    Name = package.DisplayName,
                    PluginGuids = { ModPolicy.VoidManagerGuid },
                    Version = voidManager.Value.Version,
                    PackageId = package.FullName,
                    Package = package,
                    PackageVersion = package.Version(voidManager.Value.Version),
                };
                mods.Add(added);
                carriers.Add(added);
            }
            foreach (LobbyMod carrier in carriers)
            {
                carrier.Need = ModNeed.Everyone;
                carrier.NeedFromHost = true;
            }
        }

        foreach (LobbyMod mod in mods)
        {
            if (mod.Need != ModNeed.Unknown || mod.Package == null) continue;
            mod.Need = ModPolicy.GuessNeed(mod.Package.FullName, mod.Package.Categories);
        }
        mods.RemoveAll(m => m.Package != null && PackageIndex.IsBepInExPack(m.Package.FullName));
        return new Lobby(room, region, mods, source, voidManager?.Version);
    }
}
