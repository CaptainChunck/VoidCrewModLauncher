// Shared source: compiled into the launcher (.NET 10) and into the bridge plugin inside the game (Mono, C# 7.3).
#if NET5_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Text;

namespace VoidCrewModLauncher.Protocol
{
    /// <summary>What players who join a lobby need to do about one of the host's mods.</summary>
    public enum ModNeed
    {
        /// <summary>Not known; the joining player's launcher decides from the mod's Thunderstore categories.</summary>
        Unknown = '?',

        /// <summary>Everyone in the lobby must have it (VoidManager "All", or a mod the game itself requires).</summary>
        Everyone = 'R',

        /// <summary>Only needs the lobby to be [mod_session] (VoidManager "Session"); joiners don't need it.</summary>
        Session = 'S',

        /// <summary>Only the host runs it.</summary>
        HostOnly = 'H',

        /// <summary>Only changes the host's own game (UI, VR, sound); joiners don't need it.</summary>
        ClientOnly = 'C',
    }

    /// <summary>VoidManager's multiplayer types (VoidManager.MPModChecks.MultiplayerType), the byte in its mod lists.</summary>
    public enum VoidManagerType
    {
        /// <summary>Left out of VoidManager's lists.</summary>
        Hidden = 0,

        /// <summary>A BepInEx plugin that doesn't register with VoidManager; VoidManager doesn't check it.</summary>
        Unmanaged = 4,

        /// <summary>Only changes the player's own game.</summary>
        Client = 8,

        /// <summary>Only the host needs it.</summary>
        Host = 14,

        /// <summary>The room must be [mod_session]; joining players don't need it. VoidPlugin's default.</summary>
        Session = 20,

        /// <summary>Everyone needs it, at the same version: VoidManager turns away a joining player without it.</summary>
        All = 30,
    }

    public sealed class AdvertMod
    {
        /// <summary>Thunderstore "Namespace-Name", or a local mod's name when <see cref="Local"/> is set.</summary>
        public string Id { get; set; }
        public string Version { get; set; }
        public ModNeed Need { get; set; }
        public bool Local { get; set; }

        /// <summary>
        /// The BepInEx plugin GUIDs in a local mod: what identifies it on every PC (the launcher names a local mod after
        /// its file on the PC it was added from). Null or empty when not known.
        /// </summary>
        public List<string> PluginGuids { get; set; }
    }

    /// <summary>
    /// The launcher's lobby advert: the host's mods, with Thunderstore ids, versions and what joiners need, in one short
    /// room property ("VCML") that the game lists in the lobby so anyone browsing sees it before joining.
    /// Format: <c>vcml1;Namespace-Name@1.2.3!R;#LocalMod@1.0!R:plugin.guid,other.guid</c>. Thunderstore names never
    /// contain ; @ ! or #. A local mod's plugin GUIDs follow its need letter after a colon (launcher 0.8.2 and later):
    /// older launchers read only the letter, so they still read the rest of the advert as before.
    /// </summary>
    public static class LobbyAdvert
    {
        public const string PropertyKey = "VCML";
        private const string Header = "vcml1";

        /// <summary>Keeps the room property small; the lobby sends it to everyone browsing.</summary>
        public const int MaxMods = 64;

        /// <summary>And short: Photon sends listed properties in every lobby list update.</summary>
        public const int MaxLength = 2000;

        /// <summary>Plugin GUIDs per mod, and their length: the advert comes from another player.</summary>
        public const int MaxGuids = 8;
        public const int MaxGuidLength = 100;

        /// <summary>
        /// The longest advert a reader takes: a few times what launchers write, so a longer advert from a newer launcher
        /// still reads, but not whatever another player puts in the room property. Longer text isn't read at all.
        /// </summary>
        public const int MaxTextLength = 4 * MaxLength;

        /// <summary>
        /// The longest mod id (a local mod's name) and version a reader takes; real ones are far shorter. An entry with a
        /// longer one is left out.
        /// </summary>
        public const int MaxIdLength = 200;
        public const int MaxVersionLength = 64;

        /// <summary>
        /// The biggest VoidManager mod list (room property "Mods") the bridge passes on and the launcher reads. A real one
        /// is about 100 bytes a mod; a bigger one is someone filling the property, and is left out.
        /// </summary>
        public const int MaxVoidManagerListBytes = 64 * 1024;

        public static string Encode(IEnumerable<AdvertMod> mods)
        {
            var text = new StringBuilder(Header);
            int count = 0;
            foreach (AdvertMod mod in mods)
            {
                if (mod == null || string.IsNullOrEmpty(mod.Id)) continue;
                if (++count > MaxMods) break;
                string entry = ";" + (mod.Local ? "#" : "") + Clean(mod.Id) + "@" + Clean(string.IsNullOrEmpty(mod.Version) ? "0" : mod.Version) +
                               "!" + (char)(mod.Need == 0 ? ModNeed.Unknown : mod.Need) + GuidList(mod.PluginGuids);
                if (text.Length + entry.Length > MaxLength) break;
                text.Append(entry);
            }
            return text.ToString();
        }

        private static string GuidList(List<string> guids)
        {
            if (guids == null) return "";
            var clean = new List<string>();
            foreach (string guid in guids)
            {
                string value = CleanGuid(guid);
                if (value.Length > 0 && value.Length <= MaxGuidLength && !clean.Contains(value)) clean.Add(value);
                if (clean.Count == MaxGuids) break;
            }
            return clean.Count == 0 ? "" : ":" + string.Join(",", clean.ToArray());
        }

        /// <summary>
        /// Reads an advert; returns null when the text isn't one (another mod's property, a newer format, or far longer
        /// than an advert). It comes from another player, so it is read within limits: at most <see cref="MaxMods"/> mods,
        /// none with an over-long id or version, and without the characters <see cref="CleanName"/> removes, before the
        /// launcher shows or logs any of it.
        /// </summary>
        public static List<AdvertMod> Decode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength) return null;
            string[] parts = text.Split(';');
            if (parts[0] != Header) return null;
            var mods = new List<AdvertMod>();
            for (int i = 1; i < parts.Length && mods.Count < MaxMods; i++)
            {
                string part = CleanName(parts[i]);
                if (part.Length == 0) continue;
                var mod = new AdvertMod { Need = ModNeed.Unknown };
                if (part[0] == '#')
                {
                    mod.Local = true;
                    part = part.Substring(1);
                }
                int bang = part.LastIndexOf('!');
                if (bang >= 0)
                {
                    string need = part.Substring(bang + 1);
                    mod.Need = ParseNeed(need);
                    if (need.Length > 2 && need[1] == ':') mod.PluginGuids = ParseGuids(need.Substring(2));
                    part = part.Substring(0, bang);
                }
                int at = part.LastIndexOf('@');
                if (at > 0)
                {
                    mod.Id = part.Substring(0, at);
                    mod.Version = part.Substring(at + 1);
                }
                else
                {
                    mod.Id = part;
                    mod.Version = "";
                }
                if (mod.Id.Length > 0 && mod.Id.Length <= MaxIdLength && mod.Version.Length <= MaxVersionLength) mods.Add(mod);
            }
            return mods;
        }

        /// <summary>
        /// Text from another player's lobby without control characters and the invisible marks that change the direction
        /// of text (Unicode's bidirectional embeddings, overrides, isolates and direction marks), which could make one name
        /// look like another or scramble the line it is shown or logged on. Returns the same string when there is nothing
        /// to remove.
        /// </summary>
        public static string CleanName(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            StringBuilder text = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hidden = char.IsControl(c) || c == '\u061C' || c == '\u200E' || c == '\u200F' ||
                              (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');
                if (!hidden)
                {
                    if (text != null) text.Append(c);
                }
                else if (text == null)
                {
                    text = new StringBuilder(value.Length).Append(value, 0, i);
                }
            }
            return text == null ? value : text.ToString();
        }

        public static ModNeed ParseNeed(string letter)
        {
            if (string.IsNullOrEmpty(letter)) return ModNeed.Unknown;
            switch (char.ToUpperInvariant(letter[0]))
            {
                case 'R': return ModNeed.Everyone;
                case 'S': return ModNeed.Session;
                case 'H': return ModNeed.HostOnly;
                case 'C': return ModNeed.ClientOnly;
                default: return ModNeed.Unknown;
            }
        }

        public static string NeedLetter(ModNeed need)
        {
            return ((char)(need == 0 ? ModNeed.Unknown : need)).ToString();
        }

        private static List<string> ParseGuids(string text)
        {
            var guids = new List<string>();
            foreach (string part in text.Split(','))
            {
                string guid = part.Trim();
                if (guid.Length == 0 || guid.Length > MaxGuidLength || guids.Contains(guid)) continue;
                guids.Add(guid);
                if (guids.Count == MaxGuids) break;
            }
            return guids;
        }

        private static string Clean(string value)
        {
            var text = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == ';' || c == '@' || c == '!' || c == '#' || char.IsControl(c)) continue;
                text.Append(c);
            }
            return text.ToString().Trim();
        }

        /// <summary>A plugin GUID without the characters that separate entries and GUIDs.</summary>
        private static string CleanGuid(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == ',' || c == ':' || char.IsWhiteSpace(c)) continue;
                text.Append(c);
            }
            return Clean(text.ToString());
        }

        /// <summary>VoidManager's multiplayer type byte (MPModChecks.MultiplayerType) as a need.</summary>
        public static ModNeed FromVoidManagerType(int mpType)
        {
            switch ((VoidManagerType)mpType)
            {
                case VoidManagerType.All: return ModNeed.Everyone;
                case VoidManagerType.Session: return ModNeed.Session;
                case VoidManagerType.Host: return ModNeed.HostOnly;
                case VoidManagerType.Client: return ModNeed.ClientOnly;
                default: return ModNeed.Unknown; // Unmanaged, Hidden
            }
        }

        /// <summary>
        /// What a lobby lists for one of its host's mods. A mod that joining players are checked for (VoidManager "All",
        /// or one the game itself requires) is for everyone, whatever else says: without it they'd be turned away.
        /// Otherwise the host's own choice on the HOST tab wins, then the mod's VoidManager type, then the launcher's guess.
        /// </summary>
        /// <param name="launcherNeed">What the launcher put in launch.json: the host's choice, or its guess.</param>
        /// <param name="chosenByHost">The host picked <paramref name="launcherNeed"/> on the HOST tab.</param>
        /// <param name="voidManagerNeed">The mod's VoidManager type in the running game as a need (Unknown when it has none).</param>
        /// <param name="checkedOnJoiners">The game or VoidManager turns away a joining player without the mod.</param>
        public static ModNeed AdvertisedNeed(ModNeed launcherNeed, bool chosenByHost, ModNeed voidManagerNeed, bool checkedOnJoiners)
        {
            if (checkedOnJoiners || voidManagerNeed == ModNeed.Everyone) return ModNeed.Everyone;
            if (!chosenByHost && voidManagerNeed != ModNeed.Unknown) return voidManagerNeed;
            return launcherNeed == 0 ? ModNeed.Unknown : launcherNeed;
        }
    }
}
