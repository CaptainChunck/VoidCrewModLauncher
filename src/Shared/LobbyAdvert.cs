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

    public sealed class AdvertMod
    {
        /// <summary>Thunderstore "Namespace-Name", or a local mod's name when <see cref="Local"/> is set.</summary>
        public string Id { get; set; }
        public string Version { get; set; }
        public ModNeed Need { get; set; }
        public bool Local { get; set; }
    }

    /// <summary>
    /// The launcher's lobby advert: the host's mods, with Thunderstore ids, versions and what joiners need, in one short
    /// room property ("VCML") that the game lists in the lobby so anyone browsing sees it before joining.
    /// Format: <c>vcml1;Namespace-Name@1.2.3!R;#LocalMod@1.0!C</c>. Thunderstore names never contain ; @ ! or #.
    /// </summary>
    public static class LobbyAdvert
    {
        public const string PropertyKey = "VCML";
        private const string Header = "vcml1";

        /// <summary>Keeps the room property small; the lobby sends it to everyone browsing.</summary>
        public const int MaxMods = 64;

        /// <summary>And short: Photon sends listed properties in every lobby list update.</summary>
        public const int MaxLength = 2000;

        public static string Encode(IEnumerable<AdvertMod> mods)
        {
            var text = new StringBuilder(Header);
            int count = 0;
            foreach (AdvertMod mod in mods)
            {
                if (mod == null || string.IsNullOrEmpty(mod.Id)) continue;
                if (++count > MaxMods) break;
                string entry = ";" + (mod.Local ? "#" : "") + Clean(mod.Id) + "@" + Clean(string.IsNullOrEmpty(mod.Version) ? "0" : mod.Version) +
                               "!" + (char)(mod.Need == 0 ? ModNeed.Unknown : mod.Need);
                if (text.Length + entry.Length > MaxLength) break;
                text.Append(entry);
            }
            return text.ToString();
        }

        /// <summary>Reads an advert; returns null when the text isn't one (another mod's property, or a newer format).</summary>
        public static List<AdvertMod> Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] parts = text.Split(';');
            if (parts[0] != Header) return null;
            var mods = new List<AdvertMod>();
            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
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
                    mod.Need = ParseNeed(part.Substring(bang + 1));
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
                if (mod.Id.Length > 0) mods.Add(mod);
            }
            return mods;
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

        /// <summary>VoidManager's multiplayer type byte (MPModChecks.MultiplayerType) as a need.</summary>
        public static ModNeed FromVoidManagerType(int mpType)
        {
            switch (mpType)
            {
                case 30: return ModNeed.Everyone;  // All
                case 20: return ModNeed.Session;   // Session
                case 14: return ModNeed.HostOnly;  // Host
                case 8: return ModNeed.ClientOnly; // Client
                default: return ModNeed.Unknown;   // Unmanaged (4), Hidden (0)
            }
        }
    }
}
