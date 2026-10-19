// Shared source: compiled into the launcher (.NET 10) and into the bridge plugin inside the game (Mono, C# 7.3).
// Keep it to plain classes with public properties so both System.Text.Json and Newtonsoft.Json read it.
#if NET5_0_OR_GREATER
#nullable disable
#endif
using System.Collections.Generic;

namespace VoidCrewModLauncher.Protocol
{
    /// <summary>
    /// The launcher and its helper mod in the game (the bridge) talk over one loopback TCP connection: one JSON object
    /// per line, camelCase names, and a "type" field that says which message it is. The launcher listens; the bridge
    /// connects and says hello with the token from launch.json, and the launcher answers with a welcome carrying the same
    /// token. Neither side acts on the other's commands before that.
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

        /// <summary>Plugin GUID of the bridge; the launcher leaves it out of every mod list it shows.</summary>
        public const string PluginGuid = "com.captainchunck.voidcrew.modlauncher.bridge";

        /// <summary>Folder the bridge is installed in under BepInEx\plugins of the launcher's runtime.</summary>
        public const string PluginFolder = "VoidCrewModLauncher-Bridge";

        /// <summary>Written by the launcher next to the runtime's BepInEx folder before every launch.</summary>
        public const string LaunchFileName = "launch.json";
    }

    public class Message
    {
        public string Type { get; set; }
    }

    // ------------------------------------------------------------------ bridge to launcher

    public class HelloMessage : Message
    {
        public HelloMessage() { Type = MessageTypes.Hello; }
        public string Token { get; set; }
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
        public string Error { get; set; }
    }

    public class HostResultMessage : Message
    {
        public HostResultMessage() { Type = MessageTypes.HostResult; }
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string RoomId { get; set; }
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

    // ------------------------------------------------------------------ launcher to bridge

    public class WelcomeMessage : Message
    {
        public WelcomeMessage() { Type = MessageTypes.Welcome; }
        public string Token { get; set; }
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
        /// bridge replaces it with the mod's own VoidManager multiplayer type when it has one.
        /// </summary>
        public bool NeedSetByUser { get; set; }

        /// <summary>Not from Thunderstore, so other players can't download it.</summary>
        public bool Local { get; set; }
    }
}
