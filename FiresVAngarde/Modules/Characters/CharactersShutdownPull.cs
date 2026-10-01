using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// A server shutting down with players online asks each of them for a save first (0.2.36; R90 runs 2/3: the dedi was stopped
    /// with both bots in and their whole sessions were lost, since a server-held character only reaches the server when its client
    /// saves). Game.Shutdown runs from OnApplicationQuit (Ctrl+C, server_exit.drp) and from a host's logout; by then Unity no longer
    /// ticks, so this sends "save now" to every peer and pumps the sockets itself for up to
    /// <see cref="CharactersConfig.ShutdownSaveWaitSeconds"/>, until every peer's upload has arrived. The disk writes finish in the
    /// shutdown's own drain. Whoever doesn't make it keeps the signed emergency backup their client writes on the disconnect, which
    /// the server restores at their next login.
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersShutdownPull
    {
        internal const string RpcSaveNow = "VA_SaveNow";   // server -> client: save your character now

        private static readonly HashSet<ZRpc> s_received = new HashSet<ZRpc>();
        private static bool s_pulling;

        private static bool Active => CharactersConfig.Enabled == null || CharactersConfig.Enabled.Value;

        /// <summary>A player's upload arrived (CharactersServer); counted while a shutdown pull waits.</summary>
        internal static void NoteReceived(ZRpc rpc, string steamId, string name, int length)
        {
            if (!s_pulling || rpc == null) return;
            if (s_received.Add(rpc)) Debug.Log($"[Characters] shutdown: {steamId}/{name}'s save arrived ({length}B)");
        }

        [HarmonyPatch(typeof(Game), "Shutdown")]
        [HarmonyPrefix]
        private static void Shutdown_Prefix(Game __instance)
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (!Active || znet == null || !znet.IsServer() || __instance == null || __instance.IsShuttingDown()) return;
                float wait = CharactersConfig.ShutdownSaveWaitSeconds;
                if (wait <= 0f) return;
                List<ZNetPeer> peers = znet.GetPeers().Where(p => p != null && p.m_rpc != null && p.m_socket != null && p.m_socket.IsConnected() && p.IsReady()).ToList();
                if (peers.Count == 0) return;

                s_received.Clear();
                s_pulling = true;
                foreach (ZNetPeer peer in peers) peer.m_rpc.Invoke(RpcSaveNow);
                Debug.Log($"[Characters] shutdown: asked {peers.Count} player(s) to save their character; waiting up to {wait:0} s");

                var clock = Stopwatch.StartNew();
                double last = 0d;
                while (clock.Elapsed.TotalSeconds < wait && peers.Any(p => !s_received.Contains(p.m_rpc) && p.m_socket != null && p.m_socket.IsConnected()))
                {
                    double now = clock.Elapsed.TotalSeconds;
                    float dt = (float)Math.Max(0.01d, now - last);
                    last = now;
                    // ZNet.Update's own order: the transports, then each peer's RPCs (the upload's chunks arrive through these).
                    ZSteamSocket.UpdateAllSockets(dt);
                    ZPlayFabSocket.UpdateAllSockets(dt);
                    foreach (ZNetPeer peer in peers)
                    {
                        try { peer.m_rpc.Update(dt); }
                        catch (Exception ex) { Debug.LogWarning($"[Characters] shutdown: reading {peer.m_playerName}'s connection failed: {ex.Message}"); }
                    }
                    Thread.Sleep(15);
                }
                s_pulling = false;
                int got = peers.Count(p => s_received.Contains(p.m_rpc));
                string missing = string.Join(", ", peers.Where(p => !s_received.Contains(p.m_rpc)).Select(p => p.m_playerName));
                Debug.Log($"[Characters] shutdown: {got} of {peers.Count} player save(s) arrived in {clock.ElapsedMilliseconds} ms" +
                          (missing.Length > 0 ? $"; not in time: {missing} (their signed backup restores at their next login)" : ""));
            }
            catch (Exception ex)
            {
                s_pulling = false;
                Debug.LogWarning($"[Characters] shutdown save pull failed: {ex.Message}");
            }
        }
    }
}
