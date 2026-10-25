using System;
using System.Collections.Generic;
using System.Reflection;
using CG.GameLoopStateMachine.GameStates;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using ResourceAssets;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// The few places the bridge listens to the game. None of them change what the game does: they only report when a
    /// room was created or joined, when a join failed, why the game left a room (with the names of missing mods), and
    /// which mod registered each of the game's runtime assets and external mods (for <see cref="ModChecks"/>).
    /// Each patch is applied on its own, so a game update that moves one method only turns off that report.
    /// </summary>
    internal static class Patches
    {
        public static void Apply(Harmony harmony)
        {
            Postfix(harmony, typeof(PhotonService), "OnCreatedRoom", nameof(CreatedRoom));
            Postfix(harmony, typeof(PhotonService), "OnCreateRoomFailed", nameof(CreateRoomFailed));
            Postfix(harmony, typeof(PhotonService), "OnJoinedRoom", nameof(JoinedRoom));
            // A prefix: the game's own handler turns the failure into a disconnect, which would hide Photon's reason.
            Prefix(harmony, typeof(PhotonService), "OnJoinRoomFailed", nameof(JoinRoomFailed));
            Postfix(harmony, typeof(PhotonService), "OnMasterClientSwitched", nameof(MasterClientSwitched));
            Prefix(harmony, typeof(GSPhotonDisconnected), "OnEnter", nameof(Disconnected));
            Postfix(harmony, typeof(RuntimeAssetsRegister), "RegisterAsset", nameof(Registered));
            Postfix(harmony, typeof(RuntimeAssetsRegister), "RegisterExternalMod", nameof(Registered));
            Postfix(harmony, typeof(RuntimeAssetsRegister), "AddAssetEffect", nameof(Registered));
        }

        private static void Postfix(Harmony harmony, Type type, string method, string patch)
        {
            Apply(harmony, type, method, patch, false);
        }

        private static void Prefix(Harmony harmony, Type type, string method, string patch)
        {
            Apply(harmony, type, method, patch, true);
        }

        private static void Apply(Harmony harmony, Type type, string method, string patch, bool prefix)
        {
            try
            {
                MethodInfo original = AccessTools.Method(type, method);
                if (original == null)
                {
                    Log.Warn(type.Name + "." + method + " is not in this version of Void Crew; that report to the launcher is off.");
                    return;
                }
                var handler = new HarmonyMethod(AccessTools.Method(typeof(Patches), patch));
                if (prefix) harmony.Patch(original, prefix: handler);
                else harmony.Patch(original, postfix: handler);
            }
            catch (Exception e)
            {
                Log.Error("Could not hook " + type.Name + "." + method + "; that report to the launcher is off. " + e);
            }
        }

        private static void CreatedRoom()
        {
            try
            {
                Advertiser.OnRoomCreated();
                Actions.HostFinished(true, null);
            }
            catch (Exception e)
            {
                Log.Failure("Reporting the new room", e);
            }
        }

        private static void CreateRoomFailed(short returnCode, string message)
        {
            // The game retries a few times on its own; only report once it has given up.
            Log.Warn("Photon could not create the room (" + returnCode + "): " + message);
        }

        /// <summary>The last room the game was in, for a disconnect that comes after the room is gone.</summary>
        private static string _lastRoom;

        private static void JoinedRoom()
        {
            try
            {
                if (PhotonNetwork.CurrentRoom != null) _lastRoom = PhotonNetwork.CurrentRoom.Name;
                if (Actions.PendingJoin != null && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.Name == Actions.PendingJoin)
                {
                    Actions.JoinFinished(true, null);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Reporting the joined room", e);
            }
        }

        private static void JoinRoomFailed(short returnCode, string message)
        {
            try
            {
                Actions.JoinFinished(false, Actions.JoinError(returnCode, message));
            }
            catch (Exception e)
            {
                Log.Failure("Reporting the failed join", e);
            }
        }

        private static void MasterClientSwitched(Player newMasterClient)
        {
            try
            {
                Advertiser.OnMasterClientSwitched(newMasterClient);
            }
            catch (Exception e)
            {
                Log.Failure("Taking over the lobby advert", e);
            }
        }

        /// <summary>After a mod registers one of the game's runtime assets or external mods, or changes what it requires.</summary>
        private static void Registered(GUIDUnion guid)
        {
            try
            {
                ModChecks.NoteRegistration(guid);
            }
            catch (Exception e)
            {
                Log.Failure("Noting which mod registered a game asset", e);
            }
        }

        /// <summary>Reads why the game is leaving the room before the game resets the reason.</summary>
        private static void Disconnected(GSPhotonDisconnected __instance)
        {
            try
            {
                var message = new DisconnectedMessage
                {
                    Reason = __instance.Reason == GSPhotonDisconnected.DisconnectReason.KICKED ? "kicked"
                        : __instance.Reason == GSPhotonDisconnected.DisconnectReason.MODS_INCOMPATIBILITY ? "mods"
                        : "connection",
                    RoomId = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : Actions.PendingJoin ?? _lastRoom,
                    MissingMods = new List<ModNameDto>(),
                    InvalidMods = new List<ModNameDto>(),
                };
                if (__instance.Reason == GSPhotonDisconnected.DisconnectReason.MODS_INCOMPATIBILITY)
                {
                    AddNames(message.MissingMods, __instance.MissingExternalMods, __instance);
                    AddNames(message.MissingMods, __instance.MissingRuntimeAssets, __instance);
                    AddNames(message.InvalidMods, __instance.InvalidLoadedExternalMods, __instance);
                    AddNames(message.InvalidMods, __instance.InvalidLoadedRuntimeAssets, __instance);
                }
                Log.Info("Void Crew left the room (" + message.Reason + ")" +
                         (message.MissingMods.Count > 0 ? "; missing mods: " + Join(message.MissingMods) : "") +
                         (message.InvalidMods.Count > 0 ? "; mods the room doesn't allow: " + Join(message.InvalidMods) : "") + ".");
                if (Actions.PendingJoin != null)
                {
                    Actions.JoinFinished(false, message.Reason == "mods" ? "The lobby's mods don't match yours." : message.Reason == "kicked" ? "The host removed you." : "The connection was lost.");
                }
                if (Actions.Report != null) Actions.Report(message);
            }
            catch (Exception e)
            {
                Log.Failure("Reporting why the game left the room", e);
            }
        }

        private static void AddNames(List<ModNameDto> names, List<GUIDUnion> ids, GSPhotonDisconnected state)
        {
            if (ids == null) return;
            foreach (GUIDUnion id in ids)
            {
                RuntimeAssetInfo info;
                if (RuntimeAssetsRegister.Instance != null && RuntimeAssetsRegister.Instance.TryGetInfo(id, out info))
                {
                    names.Add(new ModNameDto { Name = info.Name, DisplayName = info.DisplayName });
                }
                else if (state.MissingRuntimeDescriptions != null && state.MissingRuntimeDescriptions.TryGetValue(id, out info))
                {
                    names.Add(new ModNameDto { Name = info.Name, DisplayName = info.DisplayName });
                }
                else
                {
                    names.Add(new ModNameDto { Name = id.ToString() });
                }
            }
        }

        private static string Join(List<ModNameDto> names)
        {
            var parts = new List<string>();
            foreach (ModNameDto name in names) parts.Add(string.IsNullOrEmpty(name.DisplayName) ? name.Name : name.DisplayName);
            return string.Join(", ", parts.ToArray());
        }
    }
}
