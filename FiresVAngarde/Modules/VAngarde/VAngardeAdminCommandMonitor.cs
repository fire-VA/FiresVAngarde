using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Client-side monitor of admin cheat/debug commands. Polls devcommands/debugmode/god/ghost/
    /// fly/nocost about once a second; on any toggle - and once at login if anything is already
    /// active - it reports to the server over the VA_AdminSnapshot routed RPC. The server decides
    /// whether to surface it to Discord, and the admin gate there is server-authoritative.
    ///
    /// Runs on every real client. A non-admin cannot enable any of these on a remote server, so for
    /// normal players the active set stays empty and zero RPCs are ever sent - the monitor self-limits.
    /// </summary>
    [HarmonyPatch]
    internal static class VAngardeAdminCommandMonitor
    {
        private const float PollInterval = 1f;
        private const float LoginGraceSeconds = 8f;

        private static float _lastPoll;
        private static bool _loginReported;
        private static float _localPlayerSince = -1f;
        private static readonly HashSet<string> _active = new HashSet<string>(StringComparer.Ordinal);

        // Terminal.IsCheatsEnabled() is gated on IsServer() and so is always false on a connected client.
        // The actual devcommands flag is the private static Terminal.m_cheat - read it directly.
        private static readonly FieldInfo _cheatField = AccessTools.Field(typeof(Terminal), "m_cheat");

        private static bool DevCommandsEnabled()
        {
            try { return _cheatField != null && (bool)_cheatField.GetValue(null); }
            catch { return false; }
        }

        /// <summary>
        /// Current active cheat/debug commands for the local player. Empty when there is no local
        /// player (menu / dedicated server). Read directly from Valheim's own cheat state, so it is
        /// accurate regardless of how the cheat was enabled.
        /// </summary>
        public static List<string> GetActiveAdminCommands()
        {
            var list = new List<string>();
            try
            {
                if (DevCommandsEnabled()) list.Add("devcommands");
                if (Player.m_debugMode) list.Add("debugmode");

                var p = Player.m_localPlayer;
                if (p != null)
                {
                    if (p.InGodMode()) list.Add("god");
                    if (p.InGhostMode()) list.Add("ghost");
                    if (p.IsDebugFlying()) list.Add("fly");
                    if (p.NoCostCheat()) list.Add("nocost");
                }
            }
            catch { /* cheat-state read is best-effort; never throw from a poll */ }
            return list;
        }

        [HarmonyPatch(typeof(Game), "Update")]
        [HarmonyPostfix]
        private static void Game_Update_Postfix()
        {
            var znet = ZNet.instance;
            bool connectedClient = znet != null && !znet.IsDedicated() && !znet.IsServer()
                                   && ZRoutedRpc.instance != null;
            if (!connectedClient)
            {
                // Genuinely disconnected / at menu / dedicated server: re-arm so the next connect emits
                // a fresh login snapshot.
                _loginReported = false;
                _active.Clear();
                _localPlayerSince = -1f;
                return;
            }

            // Connected but no local player yet: initial spawn, or a transient null during death/respawn
            // or teleport. Wait WITHOUT clearing state, so a respawn resumes edge detection instead of
            // re-emitting a (login) snapshot for commands (devcommands/debugmode) that persist across death.
            if (Player.m_localPlayer == null) return;

            // Mark when the local player first exists this connect, to time the login grace window below.
            if (_localPlayerSince < 0f) _localPlayerSince = Time.realtimeSinceStartup;

            if (Time.realtimeSinceStartup - _lastPoll < PollInterval) return;
            _lastPoll = Time.realtimeSinceStartup;

            var current = GetActiveAdminCommands();
            var currentSet = new HashSet<string>(current, StringComparer.Ordinal);

            // Login grace: an admin who connects with cheats already on may not have every command
            // register on the first poll (god/nocost re-apply a beat after spawn). Wait for the state
            // to settle, THEN emit ONE "(login)" snapshot of everything active - so it batches into a
            // single "loaded commands" report instead of N per-command toggle messages.
            if (!_loginReported)
            {
                if (Time.realtimeSinceStartup - _localPlayerSince < LoginGraceSeconds) return;
                _loginReported = true;
                _active.UnionWith(currentSet);
                if (current.Count > 0)
                    Send("(login)", true, current);
                return;
            }

            // Per-command edge detection.
            foreach (var cmd in currentSet)
                if (!_active.Contains(cmd)) Send(cmd, true, current);
            foreach (var cmd in _active)
                if (!currentSet.Contains(cmd)) Send(cmd, false, current);

            _active.Clear();
            _active.UnionWith(currentSet);
        }

        private static void Send(string changedCommand, bool enabled, List<string> active)
        {
            try
            {
                var pkg = new ZPackage();
                pkg.Write(changedCommand ?? string.Empty);
                pkg.Write(enabled);
                pkg.Write(active.Count);
                foreach (var c in active) pkg.Write(c);

                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(),
                    VAngardeCore.RPC_AdminSnapshot, pkg);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] admin-snapshot send failed: {ex.Message}");
            }
        }
    }
}
