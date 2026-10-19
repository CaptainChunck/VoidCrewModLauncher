using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// Puts the launcher's advert (<see cref="LobbyAdvert"/>) on rooms this player hosts: the mods of this launch with
    /// their Thunderstore ids, versions and what joining players need. The property is listed in the lobby, so other
    /// launcher users see it in their server browser before they join, and their launcher installs the same mods.
    /// It adds information only; it doesn't change the room or anyone's game.
    /// </summary>
    internal static class Advertiser
    {
        private static LaunchFile _launch;
        private static string _advert;
        private static float _nextCheck;
        private static string _listingRequestedFor;
        private static float _listingRequestedAt;

        public static void Init(LaunchFile launch)
        {
            _launch = launch;
        }

        public static void OnRoomCreated()
        {
            _listingRequestedFor = null;
            Publish(true);
        }

        public static void OnMasterClientSwitched(Player newMaster)
        {
            if (newMaster != null && newMaster.IsLocal)
            {
                _listingRequestedFor = null;
                Publish(true);
            }
        }

        /// <summary>Every few seconds while hosting: puts the advert back if something replaced the room's lobby list.</summary>
        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 5f;
            Publish(false);
        }

        private static void Publish(bool force)
        {
            try
            {
                if (_launch == null || !PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient || PhotonNetwork.OfflineMode) return;
                Room room = PhotonNetwork.CurrentRoom;
                if (room == null) return;

                string advert = Advert();
                object current;
                room.CustomProperties.TryGetValue(LobbyAdvert.PropertyKey, out current);
                if (force || !(current is string) || (string)current != advert)
                {
                    room.SetCustomProperties(new Hashtable { { LobbyAdvert.PropertyKey, advert } });
                }

                string[] listed = room.PropertiesListedInLobby ?? new string[0];
                if (Array.IndexOf(listed, LobbyAdvert.PropertyKey) >= 0) return;
                // The listing comes back from the server, so ask again only now and then.
                if (_listingRequestedFor == room.Name && Time.realtimeSinceStartup - _listingRequestedAt < 30f) return;
                _listingRequestedFor = room.Name;
                _listingRequestedAt = Time.realtimeSinceStartup;
                room.SetPropertiesListedInLobby(listed.Concat(new[] { LobbyAdvert.PropertyKey }).ToArray());
                Log.Info("Your lobby lists its mods for the launcher's server browser: " + advert);
            }
            catch (Exception e)
            {
                Log.Failure("Listing your lobby's mods", e);
            }
        }

        /// <summary>The advert text; built once, after every plugin has loaded.</summary>
        private static string Advert()
        {
            if (_advert != null) return _advert;
            Dictionary<string, ModNeed> voidManagerNeeds = VoidManagerNeeds();
            var mods = new List<AdvertMod>();
            foreach (LaunchMod mod in _launch.Mods ?? new List<LaunchMod>())
            {
                ModNeed need = LobbyAdvert.ParseNeed(mod.Need);
                ModNeed fromVoidManager;
                if (!mod.NeedSetByUser && voidManagerNeeds.TryGetValue(mod.Id, out fromVoidManager) && fromVoidManager != ModNeed.Unknown) need = fromVoidManager;
                mods.Add(new AdvertMod { Id = mod.Local ? mod.Name ?? mod.Id : mod.Id, Version = mod.Version, Need = need, Local = mod.Local });
            }
            _advert = LobbyAdvert.Encode(mods);
            return _advert;
        }

        /// <summary>
        /// Mod id to need, from VoidManager's own multiplayer types (when VoidManager is loaded). A plugin's mod id is the
        /// folder it sits in under the runtime's BepInEx\plugins.
        /// </summary>
        private static Dictionary<string, ModNeed> VoidManagerNeeds()
        {
            var needs = new Dictionary<string, ModNeed>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type managerType = AccessTools.TypeByName("VoidManager.MPModChecks.MPModCheckManager");
                if (managerType == null) return needs;
                object manager = AccessTools.Property(managerType, "Instance")?.GetValue(null, null);
                var list = manager != null ? AccessTools.Field(managerType, "MyModList")?.GetValue(manager) as Array : null;
                if (list == null) return needs;
                foreach (object block in list)
                {
                    if (block == null) continue;
                    Type blockType = block.GetType();
                    string guid = AccessTools.Property(blockType, "ModGUID")?.GetValue(block, null) as string;
                    object mpType = AccessTools.Property(blockType, "MPType")?.GetValue(block, null);
                    if (guid == null || mpType == null) continue;
                    string modId = ModIdOfPlugin(guid);
                    if (modId == null) continue;
                    ModNeed need = LobbyAdvert.FromVoidManagerType(Convert.ToInt32(mpType));
                    ModNeed existing;
                    // A package with several plugins needs the strictest of them.
                    if (!needs.TryGetValue(modId, out existing) || Rank(need) > Rank(existing)) needs[modId] = need;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Reading VoidManager's mod types", e);
            }
            return needs;
        }

        private static int Rank(ModNeed need)
        {
            switch (need)
            {
                case ModNeed.Everyone: return 4;
                case ModNeed.Session: return 3;
                case ModNeed.HostOnly: return 2;
                case ModNeed.ClientOnly: return 1;
                default: return 0;
            }
        }

        /// <summary>The runtime folder under BepInEx\plugins that holds this plugin: the launcher names it after the mod.</summary>
        public static string ModIdOfPlugin(string guid)
        {
            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(guid, out info) || string.IsNullOrEmpty(info.Location)) return null;
            string pluginsRoot = Path.GetFullPath(BepInEx.Paths.PluginPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string location = Path.GetFullPath(info.Location);
            if (!location.StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase)) return null;
            string relative = location.Substring(pluginsRoot.Length);
            int slash = relative.IndexOfAny(new[] { '\\', '/' });
            return slash > 0 ? relative.Substring(0, slash) : null;
        }
    }
}
