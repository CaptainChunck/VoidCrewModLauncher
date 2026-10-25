using System;
using System.Collections.Generic;
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
    /// Why the game can't do what the launcher asked: one of <see cref="BridgeErrors"/>, which the launcher says in the
    /// player's language, the English sentence (what launchers up to 0.9.1 show, and what the logs say) and the details
    /// that go into it.
    /// </summary>
    internal sealed class ActionError
    {
        public ActionError(string code, params string[] details)
        {
            Code = code;
            Details = new List<string>(details);
            Message = BridgeErrors.English(code, Details) ?? code;
        }

        public string Code { get; private set; }
        public string Message { get; private set; }
        public List<string> Details { get; private set; }
    }

    /// <summary>
    /// What the launcher can ask the game to do: join a room, host one, change region or quit. Each goes through the
    /// game's own code path (the states and menus the game uses for invites and its host panel), so the game's checks
    /// all still apply. Called on the main thread; returns null when started, or why not.
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

        public static ActionError Join(JoinCommand command)
        {
            if (command == null || string.IsNullOrEmpty(command.RoomId)) return new ActionError(BridgeErrors.NoLobby);
            GameStateMachine machine = Singleton<GameStateMachine>.Instance;
            if (machine == null) return new ActionError(BridgeErrors.Starting);
            IState state = machine.CurrentState;
            if (Busy != null) return new ActionError(BridgeErrors.Busy);
            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.Name == command.RoomId) return new ActionError(BridgeErrors.InThisLobby);
            if (!(state is GSMainMenu) && !(state is GSIngame)) return new ActionError(BridgeErrors.GameBusy, GameInfo.Phase());
            if (state is GSMainMenu && !GameInfo.InLobby) return new ActionError(BridgeErrors.NotConnected);
            // Leaving a mission ends it for the crew when you host; that's the player's call in the game, not the launcher's.
            if (GameInfo.Phase() == "mission") return new ActionError(BridgeErrors.OnMission);
            if (command.Modded && !GameInfo.CanJoinModded())
            {
                GameInfo.AllowModdedJoins();
                if (!GameInfo.CanJoinModded()) return new ActionError(BridgeErrors.ModdedJoinsOff);
            }
            MatchmakingRoom listed = GameInfo.ListedRoom(command.RoomId);
            if (listed != null && !GameInfo.Joinable(listed, PAL.Platform.GetPlatform(), GameInfo.HasCrossPlay()))
            {
                if (listed.HostBlocked) return new ActionError(BridgeErrors.HostBlocked);
                return new ActionError(listed.IsCrossPlay ? BridgeErrors.CrossPlayNotAllowed : BridgeErrors.OtherPlatform);
            }

            PhotonService service = PunSingleton<PhotonService>.Instance;
            Region current = service != null ? service.CurrentRegion() : null;
            bool otherRegion = !string.IsNullOrEmpty(command.Region) && current != null && current.Code != command.Region;
            if (otherRegion && !(state is GSMainMenu)) return new ActionError(BridgeErrors.OtherRegion, command.Region);

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

        public static ActionError Host(HostCommand command)
        {
            if (command == null) return new ActionError(BridgeErrors.NoLobbySettings);
            if (Busy != null) return new ActionError(BridgeErrors.Busy);
            if (!(GameInfo.CurrentState is GSMainMenu)) return new ActionError(BridgeErrors.NotInMenu);
            if (!GameInfo.InLobby) return new ActionError(BridgeErrors.NotConnected);
            if (!GameInfo.CanHost()) return new ActionError(BridgeErrors.TutorialFirst);

            MatchMakingMenu menu = UnityEngine.Object.FindObjectOfType<MatchMakingMenu>();
            if (menu == null) return new ActionError(BridgeErrors.HostPanelMissing);

            Traverse traverse = Traverse.Create(menu);
            var nameField = traverse.Field("_roomNameField").GetValue<TextField>();
            var limit = traverse.Field("_playerLimit").GetValue<IntSetting>();
            MethodInfo start = AccessTools.Method(typeof(MatchMakingMenu), command.IsPublic ? "OnStartPublicGame" : "OnStartPrivateGame");
            if (nameField == null || limit == null || start == null) return new ActionError(BridgeErrors.HostPanelChanged);

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
            string seconds = ((int)Timeout).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (PendingJoin != null) JoinFinished(false, new ActionError(BridgeErrors.JoinTimeout, seconds));
            if (PendingHost) HostFinished(false, new ActionError(BridgeErrors.HostTimeout, seconds));
        }

        /// <param name="reason">Why the game left the lobby while joining it (kicked, mods or connection), when it did.</param>
        public static void JoinFinished(bool ok, ActionError error, string reason = null)
        {
            if (PendingJoin == null) return;
            string room = PendingJoin;
            PendingJoin = null;
            _joinAfterRegion = null;
            if (ok) Log.Info("Joined " + room + ".");
            else Log.Warn("Joining " + room + " failed: " + (error != null ? error.Message : "?"));
            if (Report != null) Report(JoinResult(room, ok, error, reason));
        }

        public static void HostFinished(bool ok, ActionError error)
        {
            if (!PendingHost) return;
            PendingHost = false;
            string room = ok && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom.Name : null;
            if (ok) Log.Info("Lobby " + room + " is open.");
            else Log.Warn("Hosting failed: " + (error != null ? error.Message : "?"));
            if (Report != null) Report(HostResult(ok, error, room));
        }

        /// <summary>A join's outcome for the launcher.</summary>
        public static JoinResultMessage JoinResult(string roomId, bool ok, ActionError error, string reason = null)
        {
            ActionError failure = ok ? null : error;
            return new JoinResultMessage
            {
                RoomId = roomId,
                Ok = ok,
                Error = failure != null ? failure.Message : null,
                ErrorCode = failure != null ? failure.Code : null,
                ErrorDetails = failure != null && failure.Details.Count > 0 ? failure.Details : null,
                Reason = reason,
            };
        }

        /// <summary>A host's outcome for the launcher.</summary>
        public static HostResultMessage HostResult(bool ok, ActionError error, string roomId = null)
        {
            ActionError failure = ok ? null : error;
            return new HostResultMessage
            {
                Ok = ok,
                Error = failure != null ? failure.Message : null,
                ErrorCode = failure != null ? failure.Code : null,
                ErrorDetails = failure != null && failure.Details.Count > 0 ? failure.Details : null,
                RoomId = roomId,
            };
        }

        /// <summary>Photon's join error codes in words.</summary>
        public static ActionError JoinError(short code, string message)
        {
            switch (code)
            {
                case 32758: return new ActionError(BridgeErrors.LobbyGone);
                case 32765: return new ActionError(BridgeErrors.LobbyFull);
                case 32764: return new ActionError(BridgeErrors.LobbyClosed);
                case 32746: return new ActionError(BridgeErrors.InThisLobby);
                default: return new ActionError(BridgeErrors.JoinRefused, code.ToString(System.Globalization.CultureInfo.InvariantCulture), message ?? "");
            }
        }
    }
}
