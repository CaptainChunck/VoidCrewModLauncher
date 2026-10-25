using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using ResourceAssets;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// What a joining player is checked for in this game, by mod (a mod is its folder under the runtime's
    /// BepInEx\plugins, which the launcher names after the mod's id):
    /// - VoidManager turns away a joining player without one of its "All" mods (or at another version), and once the
    ///   host has an "All" mod, a joining player who doesn't run VoidManager at all;
    /// - the game itself sends a joining player back to the menu without a runtime asset or external mod that a mod
    ///   registered as required (Warzone's missions, say, whatever VoidManager type the mod has).
    /// The lobby advert lists these mods for everyone, so other launchers set them up before they join. Read-only.
    /// </summary>
    internal static class ModChecks
    {
        public const string VoidManagerGuid = "VoidCrewModdingTeam.VoidManager";

        /// <summary>The mod that registered each of the game's runtime assets and external mods (first one wins).</summary>
        private static readonly Dictionary<GUIDUnion, string> Registrations = new Dictionary<GUIDUnion, string>();

        /// <summary>
        /// The mod of every assembly seen so far (null for the game's, BepInEx's and the bridge's own): a mod registers its
        /// assets one by one, and each call's stack is looked up, so its DLL's path is worked out once.
        /// </summary>
        private static readonly Dictionary<Assembly, string> ModsByAssembly = new Dictionary<Assembly, string>();

        private static string _pluginsRoot;

        /// <summary>
        /// Called after RuntimeAssetsRegister.RegisterAsset, RegisterExternalMod and AddAssetEffect: notes which mod made
        /// the call, from the first method on the stack that lives in a DLL under BepInEx\plugins. The bridge loads before
        /// the other plugins (its GUID sorts first), but registrations made before it loaded aren't seen here;
        /// <see cref="RequiredByGame"/> traces those through the asset's type.
        /// </summary>
        public static void NoteRegistration(GUIDUnion id)
        {
            string mod = CallingMod();
            if (mod == null) return;
            lock (Registrations)
            {
                if (!Registrations.ContainsKey(id)) Registrations[id] = mod;
            }
        }

        private static string CallingMod()
        {
            StackFrame[] frames = new StackTrace(1, false).GetFrames();
            if (frames == null) return null;
            foreach (StackFrame frame in frames)
            {
                MethodBase method = frame.GetMethod();
                // Harmony's patched copies of methods have no declaring type; the next frame is their caller.
                Type type = method != null ? method.DeclaringType : null;
                string mod = type != null ? ModOfAssembly(type.Assembly) : null;
                if (mod != null) return mod;
            }
            return null;
        }

        /// <summary>
        /// The mods the game itself requires of joining players right now: the owners of the runtime assets and external
        /// mods it lists as required (what the host's session will ask for). <paramref name="untraced"/> names the
        /// required ones that couldn't be traced to a mod (registered before the bridge loaded, without a class of the
        /// mod's own).
        /// </summary>
        public static HashSet<string> RequiredByGame(List<string> untraced)
        {
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RuntimeAssetsRegister register = RuntimeAssetsRegister.Instance;
            var ids = new List<GUIDUnion>(register.GetRequiredAssets().Keys);
            ids.AddRange(register.GetRequiredExternalMods().Keys);
            foreach (GUIDUnion id in ids)
            {
                string mod;
                lock (Registrations) Registrations.TryGetValue(id, out mod);
                if (mod == null)
                {
                    // Registered before the bridge loaded: an asset made from a class of the mod's own DLL still says whose it is.
                    UnityEngine.Object asset = register.GetAsset(id);
                    if (asset != null) mod = ModOfAssembly(asset.GetType().Assembly);
                }
                if (mod != null)
                {
                    mods.Add(mod);
                    continue;
                }
                RuntimeAssetInfo info;
                bool named = (register.TryGetAssetInfo(id, out info) || register.TryGetModInfo(id, out info)) && info != null && !string.IsNullOrEmpty(info.Name);
                if (untraced != null) untraced.Add(named ? info.Name : id.ToString());
            }
            return mods;
        }

        /// <summary>
        /// Mod to the strictest VoidManager type of its plugins as a need, from VoidManager's own list (empty without
        /// VoidManager). <paramref name="hasAllMods"/>: a loaded mod is VoidManager "All", so VoidManager turns away
        /// joining players who don't run it.
        /// </summary>
        public static Dictionary<string, ModNeed> VoidManagerNeeds(out bool hasAllMods)
        {
            var needs = new Dictionary<string, ModNeed>(StringComparer.OrdinalIgnoreCase);
            hasAllMods = false;
            try
            {
                Type managerType = AccessTools.TypeByName("VoidManager.MPModChecks.MPModCheckManager");
                if (managerType == null) return needs;
                object manager = AccessTools.Property(managerType, "Instance")?.GetValue(null, null);
                if (manager == null) return needs;
                object highest = AccessTools.Property(managerType, "HighestLevelOfMPMods")?.GetValue(manager, null);
                hasAllMods = highest != null && Convert.ToInt32(highest) == (int)VoidManagerType.All;
                var list = AccessTools.Field(managerType, "MyModList")?.GetValue(manager) as Array;
                if (list == null) return needs;
                // Looked up once for the list's type, not for every entry.
                Type blockType = null;
                PropertyInfo guidProperty = null;
                PropertyInfo typeProperty = null;
                foreach (object block in list)
                {
                    if (block == null) continue;
                    if (block.GetType() != blockType)
                    {
                        blockType = block.GetType();
                        guidProperty = AccessTools.Property(blockType, "ModGUID");
                        typeProperty = AccessTools.Property(blockType, "MPType");
                    }
                    string guid = guidProperty != null ? guidProperty.GetValue(block, null) as string : null;
                    object mpType = typeProperty != null ? typeProperty.GetValue(block, null) : null;
                    if (guid == null || mpType == null) continue;
                    string modId = ModOfPlugin(guid);
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

        /// <summary>Mod to the GUIDs of its loaded plugins.</summary>
        public static Dictionary<string, List<string>> PluginGuidsByMod()
        {
            var byMod = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, BepInEx.PluginInfo> plugin in Chainloader.PluginInfos)
            {
                BepInEx.PluginInfo info = plugin.Value;
                if (info == null || info.Metadata == null) continue;
                string mod = ModOfLocation(info.Location);
                if (mod == null) continue;
                List<string> guids;
                if (!byMod.TryGetValue(mod, out guids)) byMod[mod] = guids = new List<string>();
                if (!guids.Contains(info.Metadata.GUID)) guids.Add(info.Metadata.GUID);
            }
            return byMod;
        }

        /// <summary>The mod that holds a loaded plugin, or null.</summary>
        public static string ModOfPlugin(string guid)
        {
            BepInEx.PluginInfo info;
            return guid != null && Chainloader.PluginInfos.TryGetValue(guid, out info) && info != null ? ModOfLocation(info.Location) : null;
        }

        /// <summary>The mod a DLL belongs to; null for the game, BepInEx, Harmony and the bridge itself.</summary>
        public static string ModOfAssembly(Assembly assembly)
        {
            if (assembly == null || assembly == typeof(ModChecks).Assembly || assembly.IsDynamic) return null;
            string mod;
            lock (ModsByAssembly)
            {
                if (ModsByAssembly.TryGetValue(assembly, out mod)) return mod;
            }
            try
            {
                mod = ModOfLocation(assembly.Location);
            }
            catch (NotSupportedException)
            {
                mod = null;
            }
            lock (ModsByAssembly) ModsByAssembly[assembly] = mod;
            return mod;
        }

        /// <summary>The folder under BepInEx\plugins that holds <paramref name="location"/>: the launcher names it after the mod.</summary>
        public static string ModOfLocation(string location)
        {
            if (string.IsNullOrEmpty(location)) return null;
            try
            {
                if (_pluginsRoot == null) _pluginsRoot = Path.GetFullPath(BepInEx.Paths.PluginPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(location);
                if (!full.StartsWith(_pluginsRoot, StringComparison.OrdinalIgnoreCase)) return null;
                string relative = full.Substring(_pluginsRoot.Length);
                int slash = relative.IndexOfAny(new[] { '\\', '/' });
                string mod = slash > 0 ? relative.Substring(0, slash) : null;
                return string.Equals(mod, BridgeDefaults.PluginFolder, StringComparison.OrdinalIgnoreCase) ? null : mod;
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException || e is System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
