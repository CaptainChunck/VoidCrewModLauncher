using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
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
    /// A mod the game or VoidManager checks on joining players (<see cref="ModChecks"/>) is listed for everyone, whatever
    /// the HOST tab says, and a local mod carries its plugin GUIDs so other launchers can find their own copy of it.
    /// It adds information only; it doesn't change the room or anyone's game.
    /// </summary>
    internal static class Advertiser
    {
        private static LaunchFile _launch;
        private static string _advert;
        private static List<string> _required;

        /// <summary>The mods the game required of joining players when the advert was built.</summary>
        private static HashSet<string> _requiredByGame;

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
            // Built again for each room: mods may have registered more of the game's assets since the last one.
            _advert = null;
            Publish(true);
        }

        public static void OnMasterClientSwitched(Player newMaster)
        {
            if (newMaster != null && newMaster.IsLocal)
            {
                _listingRequestedFor = null;
                _advert = null;
                Publish(true);
            }
        }

        /// <summary>
        /// Every few seconds while hosting: puts the advert back if something replaced the room's lobby list, and builds it
        /// again when a mod has since registered something the game requires of joining players.
        /// </summary>
        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 5f;
            bool rebuild = false;
            if (_advert != null && _requiredByGame != null && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
            {
                rebuild = !RequiredByGame(null).IsSubsetOf(_requiredByGame);
                if (rebuild) _advert = null;
            }
            Publish(rebuild);
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
                if (force && Actions.Report != null) Actions.Report(new AdvertMessage { Advert = advert, Required = new List<string>(_required ?? new List<string>()) });

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

        /// <summary>The advert text, built when a room is created (every plugin has loaded by then).</summary>
        private static string Advert()
        {
            if (_advert != null) return _advert;
            bool voidManagerChecks;
            Dictionary<string, ModNeed> voidManagerNeeds = ModChecks.VoidManagerNeeds(out voidManagerChecks);
            // VoidManager itself, when it turns away joining players who don't run it.
            string voidManagerMod = voidManagerChecks ? ModChecks.ModOfPlugin(ModChecks.VoidManagerGuid) : null;
            var untraced = new List<string>();
            HashSet<string> requiredByGame = RequiredByGame(untraced);
            Dictionary<string, List<string>> pluginGuids = ModChecks.PluginGuidsByMod();

            var mods = new List<AdvertMod>();
            var required = new List<string>();
            foreach (LaunchMod mod in _launch.Mods ?? new List<LaunchMod>())
            {
                if (mod == null || string.IsNullOrEmpty(mod.Id)) continue;
                ModNeed launcherNeed = LobbyAdvert.ParseNeed(mod.Need);
                ModNeed fromVoidManager;
                if (!voidManagerNeeds.TryGetValue(mod.Id, out fromVoidManager)) fromVoidManager = ModNeed.Unknown;
                string checkedBy = fromVoidManager == ModNeed.Everyone || string.Equals(mod.Id, voidManagerMod, StringComparison.OrdinalIgnoreCase) ? "VoidManager"
                    : requiredByGame.Contains(mod.Id) ? "the game" : null;
                ModNeed need = LobbyAdvert.AdvertisedNeed(launcherNeed, mod.NeedSetByUser, fromVoidManager, checkedBy != null);
                if (checkedBy != null)
                {
                    required.Add(mod.Id);
                    if (launcherNeed != ModNeed.Everyone)
                    {
                        Log.Info((mod.Name ?? mod.Id) + ": " + checkedBy + " turns away joining players without it, so your lobby lists it for everyone" +
                                 (mod.NeedSetByUser ? " instead of what the HOST tab says." : "."));
                    }
                }
                List<string> guids;
                pluginGuids.TryGetValue(mod.Id, out guids);
                mods.Add(new AdvertMod
                {
                    Id = mod.Local ? mod.Name ?? mod.Id : mod.Id,
                    Version = mod.Version,
                    Need = need,
                    Local = mod.Local,
                    PluginGuids = mod.Local ? guids : null,
                });
            }
            if (untraced.Count > 0)
            {
                Log.Info("The game requires these of joining players, but they couldn't be traced to a mod (registered before the bridge loaded): " +
                         string.Join(", ", untraced.ToArray()) + ".");
            }
            _required = required;
            _requiredByGame = requiredByGame;
            List<AdvertMod> leftOut;
            _advert = LobbyAdvert.Encode(mods, out leftOut);
            if (leftOut.Count > 0)
            {
                // The mods everyone needs went in first; launchers that read the advert set up only what it lists.
                Log.Warn("Your lobby's mod list is full: it lists " + (mods.Count - leftOut.Count) + " of your " + mods.Count + " mods. Left out: " +
                         string.Join(", ", leftOut.Select(m => m.Id + " " + m.Version).ToArray()) + ".");
            }
            return _advert;
        }

        private static HashSet<string> RequiredByGame(List<string> untraced)
        {
            try
            {
                return ModChecks.RequiredByGame(untraced);
            }
            catch (Exception e)
            {
                Log.Failure("Reading the game's required mods", e);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
