using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// The bridge's main loop, on the game's main thread: sends the launcher the game's state and room list when they
    /// change, runs the launcher's commands, and carries out this launch's join or host (from launch.json) once the
    /// main menu is ready.
    /// </summary>
    internal sealed class BridgeRuntime : MonoBehaviour
    {
        public static LaunchFile Launch;

        private Connection _connection;
        private int _sentGeneration = -1;
        private string _lastState;
        private int _lastRoomsSignature;
        private string _lastRoomsLog;
        private float _nextStateCheck;
        private float _nextRoomsCheck;
        private bool _autoDone;
        private float _autoGiveUpAt;
        private bool _debugRoomsDone;

        private void Start()
        {
            // The room list keeps updating while the launcher has the focus.
            Application.runInBackground = true;
            Actions.Report = Send;
            Advertiser.Init(Launch);
            _autoGiveUpAt = Time.realtimeSinceStartup + 600f;
            _autoDone = Launch == null || (Launch.Join == null && Launch.Host == null);

            if (Launch == null || string.IsNullOrEmpty(Launch.Token)) return;
            int[] ports = Launch.Ports != null && Launch.Ports.Count > 0 ? Launch.Ports.ToArray() : BridgeDefaults.Ports;
            _connection = new Connection(ports, Json.Write(Hello()));
            _connection.Start();
        }

        private void Update()
        {
            try
            {
                Actions.Tick();
                Advertiser.Tick();
                RunAutoAction();
                SimulateDebugRooms();
                if (_connection == null) return;

                string line;
                while (_connection.TryReceive(out line)) Handle(line);
                if (!_connection.Connected || !_connection.Welcomed) return;

                bool fresh = _sentGeneration != _connection.Generation;
                _sentGeneration = _connection.Generation;
                if (fresh)
                {
                    _lastState = null;
                    _lastRoomsSignature = int.MinValue;
                }
                SendStateIfChanged(fresh);
                SendRoomsIfChanged(fresh);
            }
            catch (Exception e)
            {
                Log.Failure("The bridge's update", e);
            }
        }

        private void OnApplicationQuit()
        {
            if (_connection != null) _connection.Dispose();
        }

        private void OnDestroy()
        {
            if (_connection != null) _connection.Dispose();
        }

        private void Send(Message message)
        {
            if (_connection != null && _connection.Welcomed) _connection.Send(Json.Write(message));
        }

        private void SendStateIfChanged(bool force)
        {
            if (!force && Time.realtimeSinceStartup < _nextStateCheck) return;
            _nextStateCheck = Time.realtimeSinceStartup + 0.5f;
            string state = Json.Write(GameInfo.State(Actions.Busy));
            if (state == _lastState) return;
            _lastState = state;
            _connection.Send(state);
        }

        private void SendRoomsIfChanged(bool force)
        {
            if (!force && Time.realtimeSinceStartup < _nextRoomsCheck) return;
            _nextRoomsCheck = Time.realtimeSinceStartup + 1f;
            // The list is the main menu's: the game only keeps it up to date there, once it's on the lobby server.
            if (!(GameInfo.CurrentState is CG.GameLoopStateMachine.GameStates.GSMainMenu) || !GameInfo.InLobby) return;
            string region = null;
            try
            {
                PhotonService service = ToolClasses.PunSingleton<PhotonService>.Instance;
                Photon.Realtime.Region current = service != null ? service.CurrentRegion() : null;
                region = current != null ? current.Code : null;
            }
            catch
            {
                // Region unknown while connecting.
            }
            // A new region counts as a change even when both lists are empty, so the launcher names the right region.
            int signature = unchecked(GameInfo.RoomsSignature() * 31 + (region ?? "").GetHashCode());
            if (signature == _lastRoomsSignature) return;
            _lastRoomsSignature = signature;
            _connection.Send(Json.Write(new RoomsMessage { Rooms = GameInfo.Rooms(), Region = region }));
            string summary = "Lobby list " + (region ?? "?") + ": " + GameInfo.LastListSummary + ".";
            if (summary != _lastRoomsLog)
            {
                _lastRoomsLog = summary;
                Log.Info(summary);
            }
        }

        private void Handle(string line)
        {
            JObject message;
            try
            {
                message = JObject.Parse(line);
            }
            catch (Exception e)
            {
                Log.Failure("Reading a message from the launcher", e);
                return;
            }

            string type = (string)message["type"];
            if (type == MessageTypes.Welcome)
            {
                if ((string)message["token"] == Launch.Token)
                {
                    _connection.Welcomed = true;
                    Log.Info("The launcher (" + (string)message["launcherVersion"] + ") is connected.");
                }
                else
                {
                    Log.Warn("A program that isn't the launcher of this game answered on the launcher's port; ignoring it.");
                }
                return;
            }
            if (!_connection.Welcomed) return;

            switch (type)
            {
                case MessageTypes.Join:
                {
                    var command = Json.Read<JoinCommand>(message);
                    string error = Actions.Join(command);
                    if (error != null) Send(new JoinResultMessage { RoomId = command.RoomId, Ok = false, Error = error });
                    break;
                }
                case MessageTypes.Host:
                {
                    string error = Actions.Host(Json.Read<HostCommand>(message));
                    if (error != null) Send(new HostResultMessage { Ok = false, Error = error });
                    break;
                }
                case MessageTypes.SetRegion:
                {
                    string error = Actions.SetRegion(Json.Read<SetRegionCommand>(message).Region);
                    if (error != null) Log.Info("Region change refused: " + error);
                    break;
                }
                case MessageTypes.Refresh:
                    _lastState = null;
                    _lastRoomsSignature = int.MinValue;
                    break;
                case MessageTypes.Quit:
                    Log.Info("The launcher asked Void Crew to close (to start it again with other mods).");
                    Application.Quit();
                    break;
            }
        }

        /// <summary>
        /// Developer testing (LaunchFile.DebugRooms): the game's own simulated rooms, given sample mod lists in the
        /// same room properties real hosts use (the launcher's advert, and VoidManager's binary list). The game adds the
        /// rooms to its list a frame or two later, so they are tagged on every update until the test ends.
        /// </summary>
        private void SimulateDebugRooms()
        {
            if (Launch == null || Launch.DebugRooms <= 0) return;
            if (!_debugRoomsDone)
            {
                if (!GameInfo.MenuReady()) return;
                _debugRoomsDone = true;
                GameInfo.IncludeDebugRooms = true;
                Log.Warn("Developer test: simulating " + Launch.DebugRooms + " debug rooms (they are not real lobbies).");
                MatchmakingController.I.SimulateDebugRooms(Launch.DebugRooms);
                return;
            }

            foreach (KeyValuePair<string, Photon.Realtime.RoomInfo> pair in MatchmakingController.I.GetCachedRoomList())
            {
                ExitGames.Client.Photon.Hashtable properties = pair.Value.CustomProperties;
                if (!properties.ContainsKey("R_Db") || properties.ContainsKey(LobbyAdvert.PropertyKey) || properties.ContainsKey(GameInfo.VoidManagerModsKey)) continue;
                int kind = Math.Abs(pair.Key.GetHashCode()) % 3;
                if (kind == 0) properties[LobbyAdvert.PropertyKey] = DebugAdvert;
                else if (kind == 1) properties[GameInfo.VoidManagerModsKey] = DebugVoidManagerList();
                else properties["R_Mod"] = ModdingType.mod_session;
            }
        }

        private static readonly string DebugAdvert = LobbyAdvert.Encode(new[]
        {
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_Fleet", Version = "0.3.0", Need = ModNeed.Everyone },
            new AdvertMod { Id = "Captain_Chuncks_Mods-Void_Crew_VR_UI_Fix", Version = "3.14.1", Need = ModNeed.ClientOnly },
        });

        private static byte[] DebugVoidManagerList()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8))
                {
                    writer.Write("1.2.11");
                    writer.Write(2);
                    writer.Write("VoidManager"); writer.Write("VoidCrewModdingTeam.VoidManager"); writer.Write("1.2.11"); writer.Write((byte)8); writer.Write("NihilityShift/VoidManager");
                    writer.Write("Uncapped Turret Stats"); writer.Write("VoidCrewModdingTeam.UncappedTurretStats"); writer.Write("1.0.1"); writer.Write((byte)30); writer.Write("");
                }
                return stream.ToArray();
            }
        }

        /// <summary>This launch's join or host from launch.json, once, as soon as the main menu is ready.</summary>
        private void RunAutoAction()
        {
            if (_autoDone) return;
            if (Time.realtimeSinceStartup > _autoGiveUpAt)
            {
                _autoDone = true;
                Log.Warn("Gave up on the launcher's " + Launch.Mode + ": the main menu never became ready.");
                return;
            }
            if (!GameInfo.MenuReady()) return;
            _autoDone = true;

            string error;
            if (Launch.Join != null)
            {
                error = Actions.Join(Launch.Join);
                if (error != null)
                {
                    Log.Warn("Could not join the launcher's lobby: " + error);
                    Send(new JoinResultMessage { RoomId = Launch.Join.RoomId, Ok = false, Error = error });
                }
            }
            else
            {
                error = Actions.Host(Launch.Host);
                if (error != null)
                {
                    Log.Warn("Could not host the launcher's lobby: " + error);
                    Send(new HostResultMessage { Ok = false, Error = error });
                }
            }
        }

        private static HelloMessage Hello()
        {
            var hello = new HelloMessage
            {
                Token = Launch.Token,
                BridgeVersion = Plugin.Version,
                GameVersion = Application.version,
                LaunchId = Launch.LaunchId,
                ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                Plugins = new List<PluginDto>(),
            };
            foreach (KeyValuePair<string, BepInEx.PluginInfo> plugin in Chainloader.PluginInfos)
            {
                BepInEx.PluginInfo info = plugin.Value;
                if (info == null || info.Metadata == null) continue;
                hello.Plugins.Add(new PluginDto
                {
                    Guid = info.Metadata.GUID,
                    Name = info.Metadata.Name,
                    Version = info.Metadata.Version != null ? info.Metadata.Version.ToString() : "",
                    Location = info.Location,
                });
            }
            return hello;
        }
    }
}
