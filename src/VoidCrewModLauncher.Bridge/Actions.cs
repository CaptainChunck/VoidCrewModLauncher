using System;
using System.Reflection;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using Code.Utilities;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using PlatformAbstractionLayer;
using ToolClasses;
using UnityEngine;
using UnityEngine.UIElements;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// What the launcher can ask the game to do: join a room, host one, change region or quit. Each goes through the
    /// game's own code path (the states and menus the game uses for invites and its host panel), so the game's checks
    /// all still apply. Called on the main thread; returns null when started, or a message saying why not.
    /// </summary>
    internal static class Actions
    {
        private const float Timeout = 45f;

        /// <summary>Room id of a join in progress.</summary>
        public static string PendingJoin { get; private set; }

        /// <summary>A host in progress.</summary>
        public static bool PendingHost { get; private set; }

        private static float _pendingSince;

        /// <summary>A join waiting for the game to finish switching to the lobby's region.</summary>
        private static JoinCommand _joinAfterRegion;

        /// <summary>Reports the outcome of a join or host to the launcher (set by the runtime).</summary>
        public static Action<Message> Report;

        public static string Busy
        {
            get
            {
                if (PendingJoin != null) return "join";
                if (PendingHost) return "host";
                return null;
            }
        }

        public static string Join(JoinCommand command)
        {
            if (command == null || string.IsNullOrEmpty(command.RoomId)) return "No lobby to join.";
            GameStateMachine machine = Singleton<GameStateMachine>.Instance;
            if (machine == null) return "Void Crew is still starting.";
            IState state = machine.CurrentState;
            if (Busy != null) return "Void Crew is already joining or hosting a lobby.";
            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.Name == command.RoomId) return "You're already in this lobby.";
            if (!(state is GSMainMenu) && !(state is GSIngame)) return "Void Crew is busy right now (" + GameInfo.Phase() + "). Try again from the main menu or the hub.";
            if (state is GSMainMenu && !GameInfo.InLobby) return "Void Crew isn't connected to its lobby server yet.";
            if (command.Modded && !GameInfo.CanJoinModded())
            {
                GameInfo.AllowModdedJoins();
                if (!GameInfo.CanJoinModded())
                {
                    return "This lobby is [mod_session] and your Void Crew settings don't allow joining those. " +
                           "Turn on \"Can join [mod_session] games\" in the game's General settings, then join again.";
                }
            }
            MatchmakingRoom listed = GameInfo.ListedRoom(command.RoomId);
            if (listed != null && !GameInfo.Joinable(listed, PAL.Platform.GetPlatform(), GameInfo.HasCrossPlay()))
            {
                if (listed.HostBlocked) return "You've blocked this lobby's host.";
                return listed.IsCrossPlay ? "This lobby has crossplay on, and crossplay isn't allowed for your account." : "This lobby is on another platform and has crossplay off.";
            }

            PhotonService service = PunSingleton<PhotonService>.Instance;
            Region current = service != null ? service.CurrentRegion() : null;
            bool otherRegion = !string.IsNullOrEmpty(command.Region) && current != null && current.Code != command.Region;
            if (otherRegion && !(state is GSMainMenu))
            {
                return "This lobby is in another region (" + command.Region + "). Leave your game, then join it from the main menu.";
            }

            Log.Info("Joining " + (string.IsNullOrEmpty(command.RoomName) ? command.RoomId : command.RoomName + " (" + command.RoomId + ")") +
                     (string.IsNullOrEmpty(command.Region) ? "" : " in region " + command.Region) + " for the launcher.");
            PendingJoin = command.RoomId;
            _pendingSince = Time.realtimeSinceStartup;
            if (otherRegion)
            {
                // The game's own join changes region by disconnecting, and once it has left the main menu it takes that
                // disconnect for a lost connection. Switching first, from the menu, keeps the join on the normal path.
                _joinAfterRegion = command;
                service.ConnectToRegion(command.Region);
                return null;
            }
            StartJoin(machine, command.RoomId);
            return null;
        }

        private static void StartJoin(GameStateMachine machine, string roomId)
        {
            GSJoinExternalGame join = machine.GetState<GSJoinExternalGame>();
            join.SetPhotonRoomToJoin(roomId);
            // The game's own joins don't all set a region, so never leave one behind for them.
            join.SetRegionToJoin("");
            machine.ChangeState<GSJoinExternalGame>();
        }

        public static string Host(HostCommand command)
        {
            if (command == null) return "No lobby settings.";
            if (Busy != null) return "Void Crew is already joining or hosting a lobby.";
            if (!(GameInfo.CurrentState is GSMainMenu)) return "Lobbies are hosted from the main menu; leave the current game first.";
            if (!GameInfo.InLobby) return "Void Crew isn't connected to its lobby server yet.";
            if (!GameInfo.CanHost()) return "Void Crew only lets you host after you've finished its tutorial (the Awakening). Play it once, then host.";

            MatchMakingMenu menu = UnityEngine.Object.FindObjectOfType<MatchMakingMenu>();
            if (menu == null) return "Void Crew's host panel isn't loaded yet.";

            Traverse traverse = Traverse.Create(menu);
            var nameField = traverse.Field("_roomNameField").GetValue<TextField>();
            var limit = traverse.Field("_playerLimit").GetValue<IntSetting>();
            MethodInfo start = AccessTools.Method(typeof(MatchMakingMenu), command.IsPublic ? "OnStartPublicGame" : "OnStartPrivateGame");
            if (nameField == null || limit == null || start == null) return "This version of Void Crew's host panel is different from the one the launcher knows; host from the game's menu instead.";

            string name = string.IsNullOrWhiteSpace(command.Name) ? nameField.value : command.Name.Trim();
            nameField.value = WordFiltering.Censor(name);
            limit.SetValue(Math.Max(1, command.MaxPlayers));
            bool crossPlay = command.CrossPlay && PAL.Privileges.HasCrossPlayPrivilege(false);
            menu.EnableCrossPlay = crossPlay;

            Log.Info("Hosting a " + (command.IsPublic ? "public" : "private") + " lobby \"" + nameField.value + "\" for up to " + limit.Value +
                     " players" + (crossPlay ? " with crossplay" : "") + " for the launcher.");
            PendingHost = true;
            _pendingSince = Time.realtimeSinceStartup;
            start.Invoke(menu, null);
            return null;
        }

        public static string SetRegion(string region)
        {
            if (string.IsNullOrEmpty(region)) return "No region.";
            if (Busy != null) return "Void Crew is joining or hosting a lobby right now.";
            if (PhotonNetwork.InRoom) return "Leave the current game to change region.";
            if (!(GameInfo.CurrentState is GSMainMenu)) return "Change region from the main menu.";
            PhotonService service = PunSingleton<PhotonService>.Instance;
            if (service == null || service.CurrentRegion() == null) return "Void Crew isn't connected yet.";
            if (service.CurrentRegion().Code == region) return null;
            Log.Info("Switching to region " + region + " for the launcher.");
            service.ConnectToRegion(region);
            return null;
        }

        /// <summary>Called every frame: joins once a region switch for a join is done, and gives up on a join or host that never finished.</summary>
        public static void Tick()
        {
            if (_joinAfterRegion != null && PendingJoin != null && GameInfo.CurrentState is GSMainMenu && GameInfo.InLobby)
            {
                PhotonService service = PunSingleton<PhotonService>.Instance;
                Region current = service != null ? service.CurrentRegion() : null;
                if (service != null && !service.ConnectingToRegion() && current != null && current.Code == _joinAfterRegion.Region)
                {
                    string room = _joinAfterRegion.RoomId;
                    _joinAfterRegion = null;
                    Log.Info("Now in region " + current.Code + "; joining " + room + ".");
                    StartJoin(Singleton<GameStateMachine>.Instance, room);
                }
            }
            if (Busy == null || Time.realtimeSinceStartup - _pendingSince < Timeout) return;
            if (PendingJoin != null) JoinFinished(false, "Void Crew didn't join the lobby within " + (int)Timeout + " seconds.");
            if (PendingHost) HostFinished(false, "Void Crew didn't open the lobby within " + (int)Timeout + " seconds.");
        }

        public static void JoinFinished(bool ok, string error)
        {
            if (PendingJoin == null) return;
            string room = PendingJoin;
            PendingJoin = null;
            _joinAfterRegion = null;
            if (ok) Log.Info("Joined " + room + ".");
            else Log.Warn("Joining " + room + " failed: " + error);
            if (Report != null) Report(new JoinResultMessage { RoomId = room, Ok = ok, Error = error });
        }

        public static void HostFinished(bool ok, string error)
        {
            if (!PendingHost) return;
            PendingHost = false;
            string room = ok && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom.Name : null;
            if (ok) Log.Info("Lobby " + room + " is open.");
            else Log.Warn("Hosting failed: " + error);
            if (Report != null) Report(new HostResultMessage { Ok = ok, Error = error, RoomId = room });
        }

        /// <summary>Photon's join error codes in words.</summary>
        public static string JoinError(short code, string message)
        {
            switch (code)
            {
                case 32758: return "The lobby doesn't exist any more.";
                case 32765: return "The lobby is full.";
                case 32764: return "The lobby is closed (its crew may have started a mission).";
                case 32746: return "You're already in this lobby.";
                default: return "Photon refused the join (" + code + "): " + message;
            }
        }
    }
}
