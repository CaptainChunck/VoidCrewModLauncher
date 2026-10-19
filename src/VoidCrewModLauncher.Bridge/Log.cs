using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// Wrapper over the BepInEx logger. Code that runs every frame must only report problems through
    /// <see cref="Failure"/>, so a persistent fault produces a few lines instead of thousands.
    /// </summary>
    internal static class Log
    {
        private static ManualLogSource _source;
        private static readonly Dictionary<string, int> FailureCounts = new Dictionary<string, int>();
        private static readonly object Gate = new object();

        public static void Init(ManualLogSource source)
        {
            _source = source;
        }

        public static void Info(string message)
        {
            if (_source != null) _source.LogInfo(message);
        }

        public static void Warn(string message)
        {
            if (_source != null) _source.LogWarning(message);
        }

        public static void Error(string message)
        {
            if (_source != null) _source.LogError(message);
        }

        /// <summary>Logs the first three failures of <paramref name="what"/> in full, then only every hundredth.</summary>
        public static void Failure(string what, Exception e)
        {
            int count;
            lock (Gate)
            {
                FailureCounts.TryGetValue(what, out count);
                FailureCounts[what] = ++count;
            }
            if (count <= 3) Error(what + " failed: " + e);
            else if (count % 100 == 0) Error(what + " failed again (" + count + " times so far): " + e.Message);
        }
    }
}
