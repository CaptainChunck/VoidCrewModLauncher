using System.Text;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Lobbies;

/// <summary>A mod in VoidManager's lobby list.</summary>
public sealed record VoidManagerMod(string Name, string Guid, string Version, byte MultiplayerType, string DownloadId);

/// <summary>
/// VoidManager (the Void Crew modding team's library) lists the host's mods in the room property "Mods", which the
/// lobby shows to everyone. Its "hashless" form, written with .NET's BinaryWriter:
/// string VoidManager version, int count, then per mod: string name, string GUID, string version, byte multiplayer
/// type, string Thunderstore id ("Namespace/Name", often empty).
/// The list comes from another player, so it is read within limits (<see cref="LobbyAdvert.MaxVoidManagerListBytes"/>,
/// at most 1000 mods, none with an over-long name, GUID, version or id) and its text without the characters
/// <see cref="LobbyAdvert.CleanName"/> removes.
/// </summary>
public static class VoidManagerModList
{
    /// <summary>The base64 text of the biggest list read.</summary>
    private const int MaxBase64Length = (LobbyAdvert.MaxVoidManagerListBytes + 2) / 3 * 4;

    public static (string Version, List<VoidManagerMod> Mods)? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > MaxBase64Length) return null;
        try
        {
            return Decode(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static (string Version, List<VoidManagerMod> Mods)? Decode(byte[] data)
    {
        if (data.Length > LobbyAdvert.MaxVoidManagerListBytes) return null;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            string version = LobbyAdvert.CleanName(reader.ReadString());
            int count = reader.ReadInt32();
            if (count < 0 || count > 1000 || version.Length > LobbyAdvert.MaxVersionLength) return null;
            var mods = new List<VoidManagerMod>(count);
            for (int i = 0; i < count; i++)
            {
                string name = LobbyAdvert.CleanName(reader.ReadString());
                string guid = LobbyAdvert.CleanName(reader.ReadString());
                string modVersion = LobbyAdvert.CleanName(reader.ReadString());
                byte type = reader.ReadByte();
                string downloadId = LobbyAdvert.CleanName(reader.ReadString());
                if (name.Length > LobbyAdvert.MaxIdLength || guid.Length > LobbyAdvert.MaxGuidLength
                    || modVersion.Length > LobbyAdvert.MaxVersionLength || downloadId.Length > LobbyAdvert.MaxIdLength) continue;
                mods.Add(new VoidManagerMod(name, guid, modVersion, type, downloadId));
            }
            return (version, mods);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The reverse, for tests and previews.</summary>
    public static byte[] Encode(string voidManagerVersion, IEnumerable<VoidManagerMod> mods)
    {
        var list = mods.ToList();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(voidManagerVersion);
            writer.Write(list.Count);
            foreach (VoidManagerMod mod in list)
            {
                writer.Write(mod.Name);
                writer.Write(mod.Guid);
                writer.Write(mod.Version);
                writer.Write(mod.MultiplayerType);
                writer.Write(mod.DownloadId);
            }
        }
        return stream.ToArray();
    }
}
