using System;
using System.Collections.Generic;
using System.Reflection;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using CG.Profile;
using Code.Utilities;
using Gameplay.Mutators;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using PlatformAbstractionLayer;
using ResourceAssets;
using ToolClasses;
using UnityEngine;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// Reads what the launcher shows from the game: where the game is (main menu, hub, mission) and the room list the
    /// game itself keeps for its matchmaking screens. Read-only; called on the main thread.
    /// </summary>
    internal static class GameInfo
    {
        /// <summary>VoidManager's lobby property with the host's mod list.</summary>
        public const string VoidManagerModsKey = "Mods";

        private static readonly FieldInfo CachedRoomsField = AccessTools.Field(typeof(MatchmakingController), "_cachedMatchmakingRooms");

        private static float _menuSince = -1f;

        /// <summary>List the game's simulated debug rooms too (developer testing, see LaunchFile.DebugRooms).</summary>
        public static bool IncludeDebugRooms;

        public static IState CurrentState
        {
            get
            {
                GameStateMachine machine = Singleton<GameStateMachine>.Instance;
                return machine != null ? machine.CurrentState : null;
            }
        }

        public static bool InLobby
        {
            get { return PhotonNetwork.NetworkClientState == ClientState.JoinedLobby; }
        }

        public static string Phase()
        {
            IState state = CurrentState;
            if (state == null) return "boot";
            if (state is GSMainMenu) return "menu";
            if (state is GSIngame || state is GSSpawn)
            {
                Room room = PhotonNetwork.CurrentRoom;
                object inHub;
                if (room != null && room.CustomProperties.TryGetValue("R_IH", out inHub) && inHub is bool && !(bool)inHub) return "mission";
                return "hub";
            }
            if (state is GSTallyScreen || state is GSEndTallyScreen || state is GSEndSession) return "mission";
            if (state is GSPhotonDisconnected) return "disconnected";
            if (state is GSQuitFromMenu || state is GSTearDown) return "quitting";
            if (state is GSIntro) return "boot";
            return "loading";
        }

        /// <summary>
        /// The main menu is up, connected to the lobby server and has been for a moment (its panels are set up then).
        /// Joins and hosts wait for this.
        /// </summary>
        public static bool MenuReady()
        {
            bool ready = CurrentState is GSMainMenu && InLobby && PlayerProfile.Instance != null;
            if (ready && LicensingHandler.Instance != null && !LicensingHandler.Instance.HasEulaAccept()) ready = false;
            if (!ready)
            {
                _menuSince = -1f;
                return false;
            }
            if (_menuSince < 0f) _menuSince = Time.realtimeSinceStartup;
            return Time.realtimeSinceStartup - _menuSince >= 2f;
        }

        public static bool CanJoinModded()
        {
            try
            {
                return SettingsManager.CurrentSettings.GeneralSettings.JoinModdedGames.ValueAsBool;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Developer test (LaunchFile.TestModdedJoinsOff): the setting off in memory, not saved.</summary>
        public static void TestModdedJoinsOff()
        {
            if (!SettingsManager.IsRunning) return;
            SettingsManager.CurrentSettings.GeneralSettings.JoinModdedGames.SetValue(0);
            Log.Warn("Developer test: switched \"Can join [mod_session] games\" off in memory.");
        }

        /// <summary>
        /// Turns on the game's "Can join [mod_session] games" (General settings) and saves it with the game's own
        /// settings (Settings.json): the launcher is there to join modded lobbies. False until the game has loaded its
        /// settings.
        /// </summary>
        public static bool AllowModdedJoins()
        {
            if (!SettingsManager.IsRunning) return false;
            try
            {
                IntSetting setting = SettingsManager.CurrentSettings.GeneralSettings.JoinModdedGames;
                if (setting.ValueAsBool) return true;
                setting.SetValue(1);
                SettingsManager.Save();
                Log.Info("Turned on Void Crew's \"Can join [mod_session] games\" setting for the launcher.");
            }
            catch (Exception e)
            {
                Log.Failure("Turning on joining modded games", e);
            }
            return true;
        }

        public static bool CanHost()
        {
            try
            {
                return PlayerProfile.Instance != null && PlayerProfile.PlayerData.CompletedAwakening;
            }
            catch
            {
                return false;
            }
        }

        public static StateMessage State(string busy)
        {
            var state = new StateMessage
            {
                Phase = Phase(),
                InLobby = InLobby,
                InRoom = PhotonNetwork.InRoom,
                CanJoinModded = CanJoinModded(),
                CanHost = CanHost(),
                Busy = busy,
                Regions = new List<RegionDto>(),
                ServerRooms = PhotonNetwork.CountOfRooms,
                ServerPlayers = PhotonNetwork.CountOfPlayers,
            };

            try
            {
                PhotonService service = PunSingleton<PhotonService>.Instance;
                if (service != null)
                {
                    Region current = service.CurrentRegion();
                    state.Region = current != null ? current.Code : null;
                    foreach (Region region in service.GetAllAvailableRegions())
                    {
                        state.Regions.Add(new RegionDto { Code = region.Code, Ping = region.Ping });
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Reading the game's regions", e);
            }

            Room room = PhotonNetwork.CurrentRoom;
            if (room != null)
            {
                state.RoomId = room.Name;
                state.IsHost = PhotonNetwork.IsMasterClient;
                state.Players = room.PlayerCount;
                state.MaxPlayers = room.MaxPlayers;
                object name;
                if (room.CustomProperties.TryGetValue("R_Na", out name) && name is string) state.RoomName = WordFiltering.Censor((string)name);
            }
            return state;
        }

        /// <summary>
        /// The rooms of the game's current region, from the same cache the game's own join screens use (filled while
        /// the game is in the main menu). Rooms the game wouldn't let this player into are left out.
        /// </summary>
        public static List<RoomDto> Rooms()
        {
            var rooms = new List<RoomDto>();
            MatchmakingController controller = MatchmakingController.I;
            Dictionary<string, RoomInfo> infos = controller.GetCachedRoomList();
            var parsed = CachedRoomsField != null ? CachedRoomsField.GetValue(controller) as Dictionary<string, MatchmakingRoom> : null;
            if (infos == null)
            {
                LastListSummary = "no list yet";
                return rooms;
            }

            Platform myPlatform = PAL.Platform.GetPlatform();
            bool crossPlay = HasCrossPlay();
            string current = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom.Name : null;
            int debug = 0, notJoinable = 0;
            foreach (KeyValuePair<string, RoomInfo> pair in infos)
            {
                RoomInfo info = pair.Value;
                // The game keeps only open, visible rooms here; checked again in case that changes.
                if (info == null || info.RemovedFromList || !info.IsOpen || !info.IsVisible || info.Name == current) continue;
                MatchmakingRoom room = null;
                if (parsed != null) parsed.TryGetValue(pair.Key, out room);
                if (room == null) continue;
                if (room.IsDebug && !IncludeDebugRooms)
                {
                    debug++;
                    continue;
                }
                if (!Joinable(room, myPlatform, crossPlay))
                {
                    notJoinable++;
                    continue;
                }

                try
                {
                    rooms.Add(ToDto(info, room, myPlatform));
                }
                catch (Exception e)
                {
                    Log.Failure("Reading room " + info.Name, e);
                }
            }
            LastListSummary = rooms.Count + " shown of the game's " + infos.Count + " open public lobbies" +
                              (notJoinable > 0 ? "; " + notJoinable + " left out (another platform without crossplay, or a blocked host)" : "") +
                              (debug > 0 ? "; " + debug + " debug rooms left out" : "");
            return rooms;
        }

        /// <summary>What the last <see cref="Rooms"/> call showed and left out, for the log.</summary>
        public static string LastListSummary { get; private set; }

        /// <summary>
        /// Whether the game lets this player into the room, as its join screen and its spawn check decide: its list
        /// greys out a blocked host's room, a crossplay room needs this player's crossplay permission, and any other
        /// room only takes players of the host's platform.
        /// </summary>
        public static bool Joinable(MatchmakingRoom room, Platform myPlatform, bool crossPlay)
        {
            if (room.HostBlocked) return false;
            return room.IsCrossPlay ? crossPlay : room.Platform == myPlatform;
        }

        public static bool HasCrossPlay()
        {
            try
            {
                return PAL.Privileges.HasCrossPlayPrivilege(false);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>The game's parsed copy of a listed room, or null.</summary>
        public static MatchmakingRoom ListedRoom(string roomId)
        {
            if (CachedRoomsField == null || MatchmakingController.I == null) return null;
            var parsed = CachedRoomsField.GetValue(MatchmakingController.I) as Dictionary<string, MatchmakingRoom>;
            MatchmakingRoom room;
            return parsed != null && parsed.TryGetValue(roomId, out room) ? room : null;
        }

        /// <summary>Cheap fingerprint of the room cache, to send the list only when it changed.</summary>
        public static int RoomsSignature()
        {
            Dictionary<string, RoomInfo> infos = MatchmakingController.I.GetCachedRoomList();
            if (infos == null) return 0;
            var parsed = CachedRoomsField != null ? CachedRoomsField.GetValue(MatchmakingController.I) as Dictionary<string, MatchmakingRoom> : null;
            unchecked
            {
                int hash = infos.Count;
                foreach (KeyValuePair<string, RoomInfo> pair in infos)
                {
                    RoomInfo info = pair.Value;
                    if (info == null) continue;
                    int roomHash = info.Name.GetHashCode() * 31 + info.PlayerCount;
                    roomHash = roomHash * 31 + info.MaxPlayers;
                    roomHash = roomHash * 31 + (info.IsOpen ? 1 : 0) + (info.IsVisible ? 2 : 0);
                    // Every listed property counts (roles, rank, quest, mutators, mod lists), whatever order Photon keeps them in.
                    int properties = 0;
                    foreach (var property in info.CustomProperties)
                    {
                        properties += (property.Key != null ? property.Key.GetHashCode() : 0) * 31 + ValueHash(property.Value);
                    }
                    roomHash = roomHash * 31 + properties;
                    // The game fills these in a moment after the room arrives (once it has checked its block list): a blocked
                    // host's room then leaves the list, and the host's name may change.
                    MatchmakingRoom room;
                    if (parsed != null && parsed.TryGetValue(pair.Key, out room) && room != null)
                    {
                        roomHash = roomHash * 31 + (room.HostBlocked ? 1 : 0);
                        roomHash = roomHash * 31 + (room.HostName != null ? room.HostName.GetHashCode() : 0);
                    }
                    hash += roomHash * -1640531535;
                }
                return hash;
            }
        }

        private static int ValueHash(object value)
        {
            if (value == null) return 0;
            var bytes = value as byte[];
            if (bytes != null)
            {
                int hash = bytes.Length;
                unchecked { foreach (byte b in bytes) hash = hash * 31 + b; }
                return hash;
            }
            var array = value as Array;
            if (array != null)
            {
                int hash = array.Length;
                unchecked { foreach (object item in array) hash = hash * 31 + ValueHash(item); }
                return hash;
            }
            return value.GetHashCode();
        }

        private static RoomDto ToDto(RoomInfo info, MatchmakingRoom room, Platform myPlatform)
        {
            var dto = new RoomDto
            {
                Id = info.Name,
                Name = room.RoomName,
                Host = room.HostName,
                Players = info.PlayerCount,
                MaxPlayers = info.MaxPlayers,
                InHub = room.InHub,
                Quest = room.QuestType.ToString(),
                AverageRank = room.AverageRank,
                CrossPlay = room.IsCrossPlay,
                Platform = room.Platform.ToString(),
                SamePlatform = room.Platform == myPlatform,
                Modding = room.ModdingType == ModdingType.mod_session ? "session" : room.ModdingType == ModdingType.mod_local ? "local" : "none",
                Roles = new List<string>(),
                Mutators = new List<string>(),
            };

            if (string.IsNullOrEmpty(dto.Host))
            {
                object hostName;
                if (info.CustomProperties.TryGetValue("R_HN", out hostName) && hostName is string) dto.Host = WordFiltering.Censor((string)hostName);
            }

            try { dto.Ship = room.ShipName; } catch { dto.Ship = "--"; }
            try { dto.System = room.SystemName; } catch { dto.System = "--"; }

            foreach (EPlayerRole role in room.PlayerRoles)
            {
                if (role != EPlayerRole.None) dto.Roles.Add(role.ToString());
            }

            foreach (GUIDUnion mutator in room.Mutators)
            {
                try
                {
                    MutatorDef def = ResourceAssetContainer<MutatorContainer, Mutator, MutatorDef>.Instance.GetAssetDefById(mutator);
                    if (def != null && def.ContextInfo != null && !string.IsNullOrEmpty(def.ContextInfo.HeaderText)) dto.Mutators.Add(def.ContextInfo.HeaderText);
                }
                catch
                {
                    // A mutator this game version doesn't know; leave it out.
                }
            }

            // The host's game sets both: one far bigger than any real list is left out rather than sent to the launcher with
            // every lobby list (the launcher wouldn't read it either).
            object mods;
            if (info.CustomProperties.TryGetValue(VoidManagerModsKey, out mods) && mods is byte[] && ((byte[])mods).Length <= LobbyAdvert.MaxVoidManagerListBytes)
            {
                dto.VoidManagerMods = Convert.ToBase64String((byte[])mods);
            }
            object advert;
            if (info.CustomProperties.TryGetValue(LobbyAdvert.PropertyKey, out advert) && advert is string && ((string)advert).Length <= LobbyAdvert.MaxTextLength)
            {
                dto.Advert = (string)advert;
            }
            return dto;
        }
    }
}
