using System;
using System.Collections.Generic;
using Gameplay.Chat;
using UnityEngine;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// Lines from the launcher for the player while they play (mod updates). They go into the player's own text chat as
    /// one line, like a crewmate's message: nothing pops up, takes the controls or makes a sound, and nobody else sees
    /// it. A line waits until the player is in a hub or mission and the chat has been up for a few seconds.
    /// </summary>
    internal static class Notices
    {
        private const string Sender = "Mod Launcher";
        private const float SettleTime = 5f;
        private const float RetryTime = 5f;
        private const float LineGap = 2f;
        private const int MaxWaiting = 10;

        private static readonly Queue<string> Waiting = new Queue<string>();
        private static TextChat _chat;
        private static float _chatSince;
        private static float _nextTry;

        public static void Add(string text)
        {
            if (string.IsNullOrEmpty(text) || Waiting.Contains(text) || Waiting.Count >= MaxWaiting) return;
            Log.Info("Notice for the player: " + text);
            Waiting.Enqueue(text);
        }

        /// <summary>Called every frame: shows waiting lines once the chat is there and settled.</summary>
        public static void Tick()
        {
            if (Waiting.Count == 0) return;
            TextChat chat = TextChat.Instance;
            if (chat != _chat)
            {
                _chat = chat;
                _chatSince = Time.realtimeSinceStartup;
            }
            string phase = GameInfo.Phase();
            if (chat == null || (phase != "hub" && phase != "mission")) return;
            float now = Time.realtimeSinceStartup;
            if (now - _chatSince < SettleTime || now < _nextTry) return;

            // One line at a time, a little apart, so several never flood the chat at once.
            try
            {
                chat.AddLog(new LogEntry(Sender, Waiting.Peek()));
                Log.Info("Showed the notice in the chat (" + phase + ").");
                Waiting.Dequeue();
                _nextTry = now + LineGap;
            }
            catch (Exception e)
            {
                // The chat isn't ready after all; the line waits for the next try.
                Log.Warn("Couldn't show a notice in the chat yet: " + e.Message);
                _nextTry = now + RetryTime;
            }
        }
    }
}
