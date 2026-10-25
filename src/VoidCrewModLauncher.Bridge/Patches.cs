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
    /// room was created or joined, when a join or host failed, why the game left a room (with the names of missing
    /// mods), and which mod registered each of the game's runtime assets and external mods (for <see cref="ModChecks"/>).
    /// Each patch is applied on its own, so a game update that moves one method only turns off that report.
    /// </summary>
    internal static class Patches
    {
        public static void Apply(Harmony harmony)
        {
            Postfix(harmony, typeof(PhotonService), "OnCreatedRoom", nameof(CreatedRoom));
            // A prefix: it reads whether the game will try again before the game counts this attempt.
            Prefix(harmony, typeof(PhotonService), "OnCreateRoomFailed", nameof(CreateRoomFailed));
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

        /// <summary>The attempts the game has left at creating a room (PhotonService._currentAttempts), when this game version has it.</summary>
        private static readonly FieldInfo CreateAttemptsLeft = AccessTools.Field(typeof(PhotonService), "_currentAttempts");

        private static void CreateRoomFailed(PhotonService __instance, short returnCode, string message)
        {
            try
            {
                Log.Warn("Photon could not create the room (" + returnCode + "): " + message);
                // The game tries again while it has attempts left, and gives up on this one when it has none: only then is the
                // host a failure the launcher hears about (else it waits for the bridge's timeout).
                object left = CreateAttemptsLeft != null ? CreateAttemptsLeft.GetValue(__instance) : null;
                if (left is int && (int)left <= 0)
                {
                    Actions.HostFinished(false, new ActionError(BridgeErrors.CreateFailed, returnCode.ToString(System.Globalization.CultureInfo.InvariantCulture), message ?? ""));
                }
            }
            catch (Exception e)
            {
                Log.Failure("Reporting the failed host", e);
            }
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
                    // With the reason, so the launcher leaves a mod mismatch to the message below (it names the mods).
                    string code = message.Reason == "mods" ? BridgeErrors.ModsMismatch : message.Reason == "kicked" ? BridgeErrors.Kicked : BridgeErrors.ConnectionLost;
                    Actions.JoinFinished(false, new ActionError(code), message.Reason);
                }
                // A host the game lost its connection during never opens: said now, not after the bridge's timeout.
                if (Actions.PendingHost) Actions.HostFinished(false, new ActionError(BridgeErrors.ConnectionLost));
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
            RuntimeAssetsRegister register = RuntimeAssetsRegister.Instance;
            foreach (GUIDUnion id in ids)
            {
                RuntimeAssetInfo info;
                // Not TryGetInfo: it warns in the game's log about every id this player hasn't registered, which is every
                // missing one, before the host's descriptions below name it.
                if (register != null && (register.TryGetAssetInfo(id, out info) || register.TryGetModInfo(id, out info)) && info != null)
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
