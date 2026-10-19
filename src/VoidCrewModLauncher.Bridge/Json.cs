using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>The protocol's JSON: camelCase names, nulls left out (Newtonsoft.Json from the game).</summary>
    internal static class Json
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public static string Write(object value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }

        public static T Read<T>(JObject value)
        {
            return value.ToObject<T>(Serializer);
        }

        /// <summary>Reads launch.json; null when it is missing or unreadable (the bridge then stays quiet).</summary>
        public static LaunchFile ReadLaunchFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warn("No " + BridgeDefaults.LaunchFileName + " at " + path + ". The game was not started by the Void Crew Mod Launcher, so the bridge stays idle.");
                    return null;
                }
                return JsonConvert.DeserializeObject<LaunchFile>(File.ReadAllText(path), Settings);
            }
            catch (Exception e)
            {
                Log.Error("Could not read " + path + ": " + e.Message);
                return null;
            }
        }
    }
}
