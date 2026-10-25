using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// The Void Crew Mod Launcher's helper inside the game. The launcher installs it with every launch; it never changes
    /// gameplay or the game's screens. It:
    /// - sends the launcher the game's room list (the same one the game's join screens show) for its server browser;
    /// - joins or hosts the lobby the player picked in the launcher, through the game's own join and host code;
    /// - lists the mods of lobbies this player hosts (Thunderstore ids and versions) so other launcher users get them;
    /// - tells the launcher when a join fails, with the names of the mods the lobby wanted.
    /// It only ever talks to the launcher on this PC (loopback), and only after the launcher proves it started this game.
    /// </summary>
    [BepInPlugin(BridgeDefaults.PluginGuid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Name = "Void Crew Mod Launcher Bridge";
        public const string Version = "0.9.1";

        private void Awake()
        {
            Log.Init(Logger);
            Log.Info(Name + " " + Version + " | game " + Application.version + " | BepInEx " + typeof(BaseUnityPlugin).Assembly.GetName().Version);

            string launchPath = Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath) ?? Paths.GameRootPath, BridgeDefaults.LaunchFileName);
            BridgeRuntime.Launch = Json.ReadLaunchFile(launchPath);
            if (BridgeRuntime.Launch != null)
            {
                Log.Info("Started by the launcher for: " + BridgeRuntime.Launch.Mode +
                         (BridgeRuntime.Launch.Join != null ? " (join " + BridgeRuntime.Launch.Join.RoomName + ")" : "") +
                         (BridgeRuntime.Launch.Host != null ? " (host \"" + BridgeRuntime.Launch.Host.Name + "\")" : "") +
                         ", " + (BridgeRuntime.Launch.Mods != null ? BridgeRuntime.Launch.Mods.Count : 0) + " mods.");
            }

            Step("Hooking the game's room events", () => Patches.Apply(new Harmony(BridgeDefaults.PluginGuid)));
            Step("Starting the bridge", () =>
            {
                var runtime = new GameObject("VoidCrewModLauncherBridge");
                DontDestroyOnLoad(runtime);
                runtime.hideFlags = HideFlags.HideAndDontSave;
                runtime.AddComponent<BridgeRuntime>();
            });
        }

        /// <summary>Runs one start-up step; a failure is logged in full and the next steps still run.</summary>
        private static void Step(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Log.Error(what + " failed, so the launcher may not see this game. Details: " + e);
            }
        }
    }
}
