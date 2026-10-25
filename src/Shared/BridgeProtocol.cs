// Shared source: compiled into the launcher (.NET 10) and into the bridge plugin inside the game (Mono, C# 7.3).
// Keep it to plain classes with public properties so both System.Text.Json and Newtonsoft.Json read it.
#if NET5_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace VoidCrewModLauncher.Protocol
{
    /// <summary>
    /// The launcher and its helper mod in the game (the bridge) talk over one loopback TCP connection: one JSON object
    /// per line, camelCase names, and a "type" field that says which message it is. The launcher listens; the bridge
    /// connects and says hello, and the launcher answers with a welcome. Each side proves it knows the token from
    /// launch.json without sending it (see <see cref="BridgeAuth"/>). Neither side acts on the other's commands before that.
    /// </summary>
    public static class MessageTypes
    {
        // Bridge to launcher
        public const string Hello = "hello";
        public const string State = "state";
        public const string Rooms = "rooms";
        public const string JoinResult = "joinResult";
        public const string HostResult = "hostResult";
        public const string Disconnected = "disconnected";
        public const string Advert = "advert";

        // Launcher to bridge
        public const string Welcome = "welcome";
        public const string Join = "join";
        public const string Host = "host";
        public const string SetRegion = "setRegion";
        public const string Refresh = "refresh";
        public const string Quit = "quit";
        public const string Notify = "notify";
    }

    public static class BridgeDefaults
    {
        /// <summary>The launcher listens on the first free port of these.</summary>
        public static readonly int[] Ports = { 47815, 47816, 47817, 47818, 47819 };

        /// <summary>
        /// Plugin GUID of the bridge; the launcher leaves it out of every mod list it shows. BepInEx loads plugins in the
        /// order of their GUIDs, and this one sorts before every GUID that starts with a letter, so the bridge loads first
        /// and sees the game assets every other mod registers (see ModChecks).
        /// </summary>
        public const string PluginGuid = "0.captainchunck.voidcrew.modlauncher.bridge";

        /// <summary>The bridge's GUID up to 0.9.1, which lobbies of hosts on those launchers still list.</summary>
        public const string OldPluginGuid = "com.captainchunck.voidcrew.modlauncher.bridge";

        /// <summary>A plugin GUID that is the bridge's, under either of its GUIDs.</summary>
        public static bool IsBridge(string guid)
        {
            return string.Equals(guid, PluginGuid, StringComparison.OrdinalIgnoreCase) || string.Equals(guid, OldPluginGuid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Folder the bridge is installed in under BepInEx\plugins of the launcher's runtime.</summary>
        public const string PluginFolder = "VoidCrewModLauncher-Bridge";

        /// <summary>Written by the launcher next to the runtime's BepInEx folder before every launch.</summary>
        public const string LaunchFileName = "launch.json";
    }

    /// <summary>
    /// The handshake's proofs: an HMAC of the token over a fresh random nonce and the launcher's port. The token itself is
    /// never sent, so a program that answers on a launcher port learns nothing, and since the port is in the proof, a
    /// program in between can't pass a proof on from the real launcher on another port.
    /// </summary>
    public static class BridgeAuth
    {
        public const string HelloPurpose = "hello";
        public const string WelcomePurpose = "welcome";

        public static string NewNonce()
        {
            var bytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Hex(bytes);
        }

        public static string Proof(string token, string purpose, string nonce, int port)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token ?? "")))
            {
                return Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose + "|" + nonce + "|" + port)));
            }
        }

        public static bool Check(string token, string purpose, string nonce, int port, string proof)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(nonce) || nonce.Length > 64 || string.IsNullOrEmpty(proof)) return false;
            string expected = Proof(token, purpose, nonce, port);
            if (proof.Length != expected.Length) return false;
            int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ proof[i];
            return difference == 0;
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }
    }

    public class Message
    {
        public string Type { get; set; }
    }

    // ------------------------------------------------------------------ bridge to launcher

    public class HelloMessage : Message
    {
        public HelloMessage() { Type = MessageTypes.Hello; }
        public string Nonce { get; set; }
        public string Proof { get; set; }
        public string BridgeVersion { get; set; }
        public string GameVersion { get; set; }
        public string LaunchId { get; set; }
        public int ProcessId { get; set; }
        public List<PluginDto> Plugins { get; set; }
    }

    public class PluginDto
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        /// <summary>Path of the plugin's DLL; under the launcher's runtime its folder is the mod's id.</summary>
        public string Location { get; set; }
    }

    /// <summary>Where the game is. Sent whenever any field changes.</summary>
    public class StateMessage : Message
    {
        public StateMessage() { Type = MessageTypes.State; }

        /// <summary>boot, menu, loading, hub, mission, disconnected, quitting.</summary>
        public string Phase { get; set; }

        /// <summary>True when connected to Void Crew's lobby server, which is when the room list is live.</summary>
        public bool InLobby { get; set; }

        public string Region { get; set; }
        public List<RegionDto> Regions { get; set; }
        public bool InRoom { get; set; }
        public string RoomId { get; set; }
        public string RoomName { get; set; }
        public bool IsHost { get; set; }
        public int Players { get; set; }
        public int MaxPlayers { get; set; }

        /// <summary>The game's "Can join [mod_session] games" setting.</summary>
        public bool CanJoinModded { get; set; }

        /// <summary>False until the player has finished the game's tutorial (Awakening); the game won't host before.</summary>
        public bool CanHost { get; set; }

        /// <summary>A join or host the bridge started and that hasn't finished yet.</summary>
        public string Busy { get; set; }

        /// <summary>Rooms on the region's server, private ones included (from Photon's statistics).</summary>
        public int ServerRooms { get; set; }

        /// <summary>Players online in the region (from Photon's statistics).</summary>
        public int ServerPlayers { get; set; }
    }

    public class RegionDto
    {
        public string Code { get; set; }
        public int Ping { get; set; }
    }

    /// <summary>The whole room list the game has for its current region (not a delta).</summary>
    public class RoomsMessage : Message
    {
        public RoomsMessage() { Type = MessageTypes.Rooms; }
        public string Region { get; set; }
        public List<RoomDto> Rooms { get; set; }
    }

    public class RoomDto
    {
        /// <summary>The Photon room name (the game's join code).</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Players { get; set; }
        public int MaxPlayers { get; set; }
        public bool InHub { get; set; }
        public string Quest { get; set; }
        public string Ship { get; set; }
        public string System { get; set; }
        public int AverageRank { get; set; }
        public bool CrossPlay { get; set; }
        public string Platform { get; set; }

        /// <summary>The host plays on the same platform as this player.</summary>
        public bool SamePlatform { get; set; }

        /// <summary>none, local or session: the game's own [mod_local] / [mod_session] tag.</summary>
        public string Modding { get; set; }

        public List<string> Roles { get; set; }
        public List<string> Mutators { get; set; }

        /// <summary>VoidManager's host mod list (room property "Mods"), base64 of its binary form.</summary>
        public string VoidManagerMods { get; set; }

        /// <summary>The launcher's own advert (room property "VCML"), see LobbyAdvert.</summary>
        public string Advert { get; set; }
    }

    public class JoinResultMessage : Message
    {
        public JoinResultMessage() { Type = MessageTypes.JoinResult; }
        public string RoomId { get; set; }
        public bool Ok { get; set; }

        /// <summary>Why it failed, in English (launchers up to 0.9.1 show it as it is).</summary>
        public string Error { get; set; }

        /// <summary>
        /// Why it failed, as one of <see cref="BridgeErrors"/>: the launcher says it in the player's language. Bridges up to
        /// 0.9.1 leave it out.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>What goes into the sentence of <see cref="ErrorCode"/> (a region, a number of seconds...).</summary>
        public List<string> ErrorDetails { get; set; }

        /// <summary>
        /// When the game left the lobby while joining it: why, as in <see cref="DisconnectedMessage.Reason"/> (kicked, mods or
        /// connection), and that message follows with the details. Null otherwise, and from bridges before 0.9.1.
        /// </summary>
        public string Reason { get; set; }
    }

    public class HostResultMessage : Message
    {
        public HostResultMessage() { Type = MessageTypes.HostResult; }
        public bool Ok { get; set; }

        /// <summary>Why it failed, in English (launchers up to 0.9.1 show it as it is).</summary>
        public string Error { get; set; }

        /// <summary>
        /// Why it failed, as one of <see cref="BridgeErrors"/>: the launcher says it in the player's language. Bridges up to
        /// 0.9.1 leave it out.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>What goes into the sentence of <see cref="ErrorCode"/>.</summary>
        public List<string> ErrorDetails { get; set; }

        public string RoomId { get; set; }
    }

    /// <summary>
    /// Why a join or host the launcher asked for didn't happen. The bridge sends one of these codes with the details its
    /// sentence needs, and the launcher shows that sentence in the player's language. The English sentence goes along in
    /// Error: launchers up to 0.9.1 show it as it is, and it is what the logs say.
    /// </summary>
    public static class BridgeErrors
    {
        public const string NoLobby = "noLobby";
        public const string Starting = "starting";
        public const string Busy = "busy";
        public const string InThisLobby = "inThisLobby";

        /// <summary>Details: where the game is (loading, quitting...).</summary>
        public const string GameBusy = "gameBusy";

        public const string NotConnected = "notConnected";
        public const string OnMission = "onMission";
        public const string ModdedJoinsOff = "moddedJoinsOff";
        public const string HostBlocked = "hostBlocked";
        public const string CrossPlayNotAllowed = "crossPlayNotAllowed";
        public const string OtherPlatform = "otherPlatform";

        /// <summary>Details: the lobby's region.</summary>
        public const string OtherRegion = "otherRegion";

        public const string NoLobbySettings = "noLobbySettings";
        public const string NotInMenu = "notInMenu";
        public const string TutorialFirst = "tutorialFirst";
        public const string HostPanelMissing = "hostPanelMissing";
        public const string HostPanelChanged = "hostPanelChanged";

        /// <summary>Details: the seconds the bridge waited.</summary>
        public const string JoinTimeout = "joinTimeout";

        /// <summary>Details: the seconds the bridge waited.</summary>
        public const string HostTimeout = "hostTimeout";

        public const string LobbyGone = "lobbyGone";
        public const string LobbyFull = "lobbyFull";
        public const string LobbyClosed = "lobbyClosed";

        /// <summary>Details: Photon's error code and its message.</summary>
        public const string JoinRefused = "joinRefused";

        public const string ModsMismatch = "modsMismatch";
        public const string Kicked = "kicked";
        public const string ConnectionLost = "connectionLost";

        /// <summary>Details: Photon's error code and its message.</summary>
        public const string CreateFailed = "createFailed";

        /// <summary>Every code.</summary>
        public static readonly string[] All =
        {
            NoLobby, Starting, Busy, InThisLobby, GameBusy, NotConnected, OnMission, ModdedJoinsOff, HostBlocked, CrossPlayNotAllowed,
            OtherPlatform, OtherRegion, NoLobbySettings, NotInMenu, TutorialFirst, HostPanelMissing, HostPanelChanged, JoinTimeout,
            HostTimeout, LobbyGone, LobbyFull, LobbyClosed, JoinRefused, ModsMismatch, Kicked, ConnectionLost, CreateFailed,
        };

        /// <summary>The English sentence of <paramref name="code"/> with its details in place; null for a code this side doesn't know.</summary>
        public static string English(string code, IList<string> details)
        {
            switch (code)
            {
                case NoLobby: return "No lobby to join.";
                case Starting: return "Void Crew is still starting.";
                case Busy: return "Void Crew is already joining or hosting a lobby.";
                case InThisLobby: return "You're already in this lobby.";
                case GameBusy: return Format("Void Crew is busy right now ({0}). Try again from the main menu or the hub.", details, 1);
                case NotConnected: return "Void Crew isn't connected to its lobby server yet.";
                case OnMission: return "You're on a mission. Leave it in the game first, then join from the hub or the main menu.";
                case ModdedJoinsOff:
                    return "This lobby is [mod_session] and your Void Crew settings don't allow joining those. " +
                           "Turn on \"Can join [mod_session] games\" in the game's General settings, then join again.";
                case HostBlocked: return "You've blocked this lobby's host.";
                case CrossPlayNotAllowed: return "This lobby has crossplay on, and crossplay isn't allowed for your account.";
                case OtherPlatform: return "This lobby is on another platform and has crossplay off.";
                case OtherRegion: return Format("This lobby is in another region ({0}). Leave your game, then join it from the main menu.", details, 1);
                case NoLobbySettings: return "No lobby settings.";
                case NotInMenu: return "Lobbies are hosted from the main menu; leave the current game first.";
                case TutorialFirst: return "Void Crew only lets you host after you've finished its tutorial (the Awakening). Play it once, then host.";
                case HostPanelMissing: return "Void Crew's host panel isn't loaded yet.";
                case HostPanelChanged: return "This version of Void Crew's host panel is different from the one the launcher knows; host from the game's menu instead.";
                case JoinTimeout: return Format("Void Crew didn't join the lobby within {0} seconds.", details, 1);
                case HostTimeout: return Format("Void Crew didn't open the lobby within {0} seconds.", details, 1);
                case LobbyGone: return "The lobby doesn't exist any more.";
                case LobbyFull: return "The lobby is full.";
                case LobbyClosed: return "The lobby is closed (its crew may have started a mission).";
                case JoinRefused: return Format("Photon refused the join ({0}): {1}", details, 2);
                case ModsMismatch: return "The lobby's mods don't match yours.";
                case Kicked: return "The host removed you.";
                case ConnectionLost: return "The connection was lost.";
                case CreateFailed: return Format("Photon couldn't create the lobby ({0}): {1}", details, 2);
                default: return null;
            }
        }

        /// <summary>The sentence with the first <paramref name="count"/> details in it (an empty text for one that is missing).</summary>
        private static string Format(string sentence, IList<string> details, int count)
        {
            var values = new object[count];
            for (int i = 0; i < count; i++) values[i] = details != null && i < details.Count && details[i] != null ? details[i] : "";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, sentence, values);
        }
    }

    /// <summary>The game left a room on its own: kicked, a mod mismatch, or a lost connection.</summary>
    public class DisconnectedMessage : Message
    {
        public DisconnectedMessage() { Type = MessageTypes.Disconnected; }

        /// <summary>kicked, mods or connection.</summary>
        public string Reason { get; set; }

        /// <summary>The room the game left (or was joining), when known.</summary>
        public string RoomId { get; set; }

        /// <summary>Mods the room needs that this player doesn't have.</summary>
        public List<ModNameDto> MissingMods { get; set; }

        /// <summary>Mods this player has that the room doesn't allow.</summary>
        public List<ModNameDto> InvalidMods { get; set; }
    }

    public class ModNameDto
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
    }

    /// <summary>
    /// The advert this game put on the lobby it hosts (see LobbyAdvert), sent when it is set up for a room. The launcher
    /// learns from it which mods the game or VoidManager check on joining players, to show on the HOST tab next time.
    /// </summary>
    public class AdvertMessage : Message
    {
        public AdvertMessage() { Type = MessageTypes.Advert; }
        public string Advert { get; set; }

        /// <summary>Ids of this launch's mods (LaunchMod.Id) that joining players are checked for, so everyone gets them.</summary>
        public List<string> Required { get; set; }
    }

    // ------------------------------------------------------------------ launcher to bridge

    public class WelcomeMessage : Message
    {
        public WelcomeMessage() { Type = MessageTypes.Welcome; }
        public string Proof { get; set; }
        public string LauncherVersion { get; set; }
    }

    public class JoinCommand : Message
    {
        public JoinCommand() { Type = MessageTypes.Join; }
        public string RoomId { get; set; }
        public string Region { get; set; }

        /// <summary>For messages only.</summary>
        public string RoomName { get; set; }

        /// <summary>The room is [mod_session]; the bridge checks the player's "Can join [mod_session] games" setting.</summary>
        public bool Modded { get; set; }
    }

    public class HostCommand : Message
    {
        public HostCommand() { Type = MessageTypes.Host; }
        public string Name { get; set; }
        public bool IsPublic { get; set; }
        public int MaxPlayers { get; set; }
        public bool CrossPlay { get; set; }
    }

    public class SetRegionCommand : Message
    {
        public SetRegionCommand() { Type = MessageTypes.SetRegion; }
        public string Region { get; set; }
    }

    /// <summary>A line for the player while they play (mod updates): shown quietly in their own text chat only.</summary>
    public class NotifyCommand : Message
    {
        public NotifyCommand() { Type = MessageTypes.Notify; }
        public string Text { get; set; }
    }

    // ------------------------------------------------------------------ launch.json

    /// <summary>
    /// Written by the launcher before it starts the game; the bridge reads it when the game starts. It says what this
    /// launch is for (play, browse, host or join), which mods are in it and how to reach the launcher.
    /// </summary>
    public class LaunchFile
    {
        public string LaunchId { get; set; }
        public string LauncherVersion { get; set; }

        /// <summary>play, browse, host or join.</summary>
        public string Mode { get; set; }

        public List<int> Ports { get; set; }
        public string Token { get; set; }
        public List<LaunchMod> Mods { get; set; }
        public JoinCommand Join { get; set; }
        public HostCommand Host { get; set; }

        /// <summary>
        /// Developer testing only: once in the main menu, the bridge asks the game for this many of its own simulated
        /// rooms (MatchmakingController.SimulateDebugRooms), gives them sample mod lists, and lists them like real ones.
        /// </summary>
        public int DebugRooms { get; set; }

        /// <summary>
        /// Developer testing only: the bridge switches the game's "Can join [mod_session] games" off in memory first, so
        /// turning it on (and the game saving it) runs even when the setting is already on.
        /// </summary>
        public bool TestModdedJoinsOff { get; set; }
    }

    public class LaunchMod
    {
        /// <summary>Thunderstore "Namespace-Name", or the local mod's id.</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }

        /// <summary>What other players need, as a LobbyAdvert need letter: R, S, H, C or ?.</summary>
        public string Need { get; set; }

        /// <summary>
        /// The host chose <see cref="Need"/> on the launcher's HOST tab. Otherwise it is the launcher's guess, and the
        /// bridge replaces it with the mod's own VoidManager multiplayer type when it has one. Either way, a mod the game
        /// or VoidManager checks on joining players is listed for everyone (LobbyAdvert.AdvertisedNeed).
        /// </summary>
        public bool NeedSetByUser { get; set; }

        /// <summary>Not from Thunderstore, so other players can't download it.</summary>
        public bool Local { get; set; }
    }
}
