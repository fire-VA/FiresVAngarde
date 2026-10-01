using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Server side of the character handoff. On login (<c>RPC_PeerInfo</c>) the socket is wrapped in a
    /// <see cref="BufferingSocket"/> so world data is held until the authoritative profile is sent over
    /// a direct RPC; on receiving a client's save, it is persisted via <see cref="CharacterStore"/>.
    /// First cut: no single-character / hardcore / maintenance enforcement yet, and the logout-race
    /// save relies on the client's save-on-logout. Not runtime-tested.
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersServer
    {
        internal const string RpcLogin = "VA_CharLogin"; // server -> client: push authoritative profile
        internal const string RpcSave  = "VA_CharSave";  // client -> server: upload profile
        internal const string RpcSaved = "VA_CharSaved"; // server -> client: the upload's length once saved, NotSaved if refused
        internal const int NotSaved = 0;

        private static readonly Dictionary<ZNetPeer, (string id, string name)> _peerProfile = new();

        private static bool Active => CharactersConfig.Enabled == null || CharactersConfig.Enabled.Value;

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (!Active || !__instance.IsServer()) return;
            peer.m_rpc.Register<ZPackage>(RpcSave, CharacterProfileTransport.Receiver(OnServerReceivedProfile));

            // Emergency-backup anti-rollback: generate the persistent master key (idempotent),
            // register the signature-verification receiver, and push a per-connection derived
            // AES key + timestamp to this peer. Client uses (key, time) to sign sidecar backups
            // it writes on unclean disconnect; server derives the same key from `time` at
            // verification using the persistent master key.
            CharactersEmergencyBackup.EnsureServerKey();
            peer.m_rpc.Register<ZPackage>(CharactersEmergencyBackup.RpcCheckSig,
                CharactersEmergencyBackup.OnServerReceiveSignature);
            CharactersEmergencyBackup.SendKeyToPeer(peer);
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyPriority(Priority.High)]
        [HarmonyPrefix]
        private static void RPC_PeerInfo_Prefix(ref BufferingSocket __state, ZNet __instance, ZRpc rpc)
        {
            if (!Active || !__instance.IsServer()) return;
            __state = new BufferingSocket(rpc.GetSocket());
            rpc.m_socket = __state;
            ZNetPeer peer = ZNet.instance.GetPeer(rpc);
            if (peer != null && ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
                peer.m_socket = __state;
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyPostfix]
        private static void RPC_PeerInfo_Postfix(BufferingSocket __state, ZNet __instance, ZRpc rpc)
        {
            if (!Active || !__instance.IsServer() || __state == null) return;

            ZNetPeer peer = __instance.GetPeer(rpc);
            if (peer == null) { Flush(__state, rpc, null); return; }

            string steamId = PlayerId(peer.m_socket.GetHostName());
            string name = peer.m_playerName?.ToLower();
            _peerProfile[peer] = (steamId, name);

            __instance.StartCoroutine(SendLoginProfile(__state, rpc, peer, steamId, name));
        }

        private static IEnumerator SendLoginProfile(BufferingSocket state, ZRpc rpc, ZNetPeer peer, string steamId, string name)
        {
            // A quick relog must read the save its logout just uploaded, not the file before it.
            float pendingWait = 0f;
            while (CharacterStore.HasPendingSave(steamId) && pendingWait < 10f)
            {
                pendingWait += Time.unscaledDeltaTime;
                yield return null;
            }

            if (peer.m_uid != 0L)
            {
                // Single-character gate (server-authoritative, before world data flushes): when enforcement
                // is on, the SteamID may join only with its ONE canonical character. Any other character —
                // a brand-new name OR a pre-existing extra .fch — is refused. Admins bypass when allowed.
                bool adminBypass = CharactersConfig.SingleCharacterAdminBypass?.Value == true &&
                                   CharactersEnforcement.IsAdmin(peer.m_socket.GetHostName());
                if (CharactersConfig.SingleCharacterMode?.Value == true && !adminBypass)
                {
                    string canonical = CharacterStore.GetCanonicalCharacterName(steamId);
                    if (canonical != null && !string.Equals(canonical, name, StringComparison.OrdinalIgnoreCase))
                    {
                        FiresCore.Bridge.FiresConnectReason.Send(peer.m_rpc,
                            "One character per player on this server",
                            new[]
                            {
                                $"You already have a character here: '{canonical}'.",
                                "Reconnect with that character to play.",
                            });
                        peer.m_rpc.Invoke("Error", (object)CharactersEnforcement.ErrorSingleChar);
                        Debug.Log($"[Characters] single-char: kicked {steamId}/{name} — canonical character is '{canonical}'.");
                        Flush(state, rpc, peer);
                        yield break;
                    }
                }

                byte[] bytes = CharacterStore.LoadProfileBytes(steamId, name);

                bool done = false;
                CharacterProfileTransport.SendToPeer(peer, RpcLogin, bytes, () => done = true);

                float waited = 0f;
                while (!done && waited < CharactersConfig.NoProgressTimeoutSeconds)
                {
                    waited += Time.deltaTime;
                    yield return null;
                }
            }

            Flush(state, rpc, peer);
        }

        private static void OnServerReceivedProfile(ZRpc rpc, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || ZNet.instance == null) return;

            ZNetPeer peer = ZNet.instance.GetPeer(rpc);
            string steamId, name;
            if (peer != null && _peerProfile.TryGetValue(peer, out var pn)) { steamId = pn.id; name = pn.name; }
            else if (peer != null) { steamId = PlayerId(peer.m_socket.GetHostName()); name = peer.m_playerName?.ToLower(); }
            else
            {
                Debug.LogWarning($"[Characters] a profile ({bytes.Length}B) arrived from a connection that is no longer a peer; not saved.");
                return;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int length = bytes.Length;
            // Taken now, while the peer is still connected: the save lands on the disk thread, maybe after a logout disconnect.
            long sessionKey = CharactersEmergencyBackup.PeerKeyTime(rpc);
            double mainMs = -1d; // set once the call returns; an older-version upload is saved inside it, before that
            bool accepted = CharacterStore.SaveProfileBytesInBackground(steamId, name, bytes, (saved, diskMs) =>
            {
                double main = mainMs >= 0d ? mainMs : timer.Elapsed.TotalMilliseconds;
                if (saved)
                {
                    Debug.Log($"[Characters] saved profile for {steamId}/{name} ({length}B) in {timer.ElapsedMilliseconds} ms: main thread {main:0.0} ms, disk thread {diskMs:0.0} ms (file + backup).");
                }
                else
                    Debug.LogWarning($"[Characters] profile for {steamId}/{name} ({length}B) was NOT saved: writing it failed (see above).");
                Acknowledge(rpc, saved ? length : NotSaved);
            });
            mainMs = timer.Elapsed.TotalMilliseconds;
            if (accepted)
            {
                // Counted when accepted, not in the save's done callback: that one runs on the main-thread dispatcher, which no longer
                // ticks once the server is shutting down (the shutdown pull's saves), while the write itself completes in the drain.
                CharactersEmergencyBackup.NoteAcceptedUpload(sessionKey, steamId, name);   // the restore gate's count (0.2.36)
                CharactersShutdownPull.NoteReceived(rpc, steamId, name, length);
            }
            if (!accepted)
            {
                Debug.LogWarning($"[Characters] profile for {steamId}/{name} ({length}B) was NOT saved: it did not load as a player profile.");
                Acknowledge(rpc, NotSaved);
            }
        }

        // A logging-out client holds its logout until this arrives (R38: PlayFab closed the link in the logout frame). The save may
        // finish after the client has gone, so only a live link is answered.
        private static void Acknowledge(ZRpc rpc, int result)
        {
            if (rpc?.GetSocket() == null || !rpc.GetSocket().IsConnected()) return;
            rpc.Invoke(RpcSaved, result);
        }

        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        [HarmonyPostfix]
        private static void Disconnect_Postfix(ZNetPeer peer)
        {
            if (peer != null) _peerProfile.Remove(peer);
            if (peer != null) CharactersEmergencyBackup.ForgetPeer(peer.m_rpc);
        }

        private static void Flush(BufferingSocket state, ZRpc rpc, ZNetPeer peer)
        {
            if (rpc.GetSocket() is BufferingSocket)
            {
                rpc.m_socket = state.Original;
                if (peer != null) peer.m_socket = state.Original;
            }

            state.finished = true;
            for (int i = 0; i < state.Package.Count; i++)
            {
                if (i == state.versionMatchQueued) state.Original.VersionMatch();
                state.Original.Send(state.Package[i]);
            }
            if (state.Package.Count == state.versionMatchQueued) state.Original.VersionMatch();
        }

        private static string PlayerId(string host) =>
            !string.IsNullOrEmpty(host) && Regex.IsMatch(host, "^\\d+$") ? "Steam_" + host : host;

        /// <summary>
        /// Wraps the peer socket during login, buffering PeerInfo/RoutedRPC/ZDOData until the profile
        /// has been delivered, then replays them. Port of ServerCharacters' BufferingSocket, derived from
        /// ZPlayFabSocket like ServerSync's copy: on a crossplay server RPC_PeerInfo reads
        /// (peer.m_socket as ZPlayFabSocket).m_remotePlayerId, which throws for any other socket type.
        /// </summary>
        internal class BufferingSocket : ZPlayFabSocket, ISocket
        {
            public volatile bool finished;
            public volatile int versionMatchQueued = -1;
            public readonly List<ZPackage> Package = new List<ZPackage>();
            public readonly ISocket Original;

            public BufferingSocket(ISocket original) => Original = original;

            public new bool IsConnected() => Original.IsConnected();
            public new ZPackage Recv() => Original.Recv();
            public new int GetSendQueueSize() => Original.GetSendQueueSize();
            public new int GetCurrentSendRate() => Original.GetCurrentSendRate();
            public new bool IsHost() => Original.IsHost();
            public new void Dispose() => Original.Dispose();
            public new bool GotNewData() => Original.GotNewData();
            public new void Close() => Original.Close();
            public new string GetEndPointString() => Original.GetEndPointString();
            public new void GetAndResetStats(out int totalSent, out int totalRecv) => Original.GetAndResetStats(out totalSent, out totalRecv);
            public new void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
                => Original.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outByteSec, out inByteSec);
            public new ISocket Accept() => Original.Accept();
            public new int GetHostPort() => Original.GetHostPort();
            public new bool Flush() => Original.Flush();
            public new string GetHostName() => Original.GetHostName();

            public new void VersionMatch()
            {
                if (finished) Original.VersionMatch();
                else versionMatchQueued = Package.Count;
            }

            public new void Send(ZPackage pkg)
            {
                int pos = pkg.GetPos();
                pkg.SetPos(0);
                int hash = pkg.ReadInt();
                if (!finished && (hash == "PeerInfo".GetStableHashCode() ||
                                  hash == "RoutedRPC".GetStableHashCode() ||
                                  hash == "ZDOData".GetStableHashCode()))
                {
                    var copy = new ZPackage(pkg.GetArray());
                    copy.SetPos(pos);
                    Package.Add(copy);
                }
                else
                {
                    pkg.SetPos(pos);
                    Original.Send(pkg);
                }
            }
        }
    }
}
