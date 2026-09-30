using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters.Transport
{
    /// <summary>
    /// Reliable, bidirectional, large-payload transfer over Valheim's routed-RPC transport.
    ///
    /// This is the fix for the ServerCharacters large-transfer disconnect. The reference mod sent
    /// profiles with a direct <c>ZRpc.Invoke</c> and a fixed 30-second hard kill whenever the
    /// socket send-queue stayed above 20 KB — so a big inventory plus any congestion meant a
    /// forced <c>ErrorConnectFailed</c>. This channel instead:
    ///
    ///  • sends over <see cref="ZRoutedRpc.InvokeRoutedRPC"/> (the same path VAngarde's log relay
    ///    uses for multi-MB payloads without tripping Steam's 512 KB single-message limit),
    ///  • announces each transfer with a metadata message, then streams fixed-size chunks,
    ///  • applies pure back-pressure (yield while the send-queue is high) and NEVER disconnects —
    ///    a slow link just transfers slower,
    ///  • lets the receiver ACK progress and name missing chunks, which the sender selectively
    ///    resends, so a dropped chunk costs one chunk, not the whole transfer,
    ///  • times out only on lack of *progress* (no ACK advance for a configurable window), and
    ///    even then it reports failure to the caller rather than killing the connection.
    ///
    /// Register a receiver per logical channel with <see cref="RegisterReceiver"/>, call
    /// <see cref="EnsureRpcsRegistered"/> once the routed-RPC layer is alive, then
    /// <see cref="Send"/> a payload to a peer.
    /// </summary>
    public static class ReliableChunkChannel
    {
        // Non-descriptive RPC names, consistent with the rest of the mod's wire surface.
        private const string RPC_Meta = "VA_ch_meta"; // sender -> receiver: announce a transfer
        private const string RPC_Data = "VA_ch_data"; // sender -> receiver: one chunk
        private const string RPC_Ack  = "VA_ch_ack";  // receiver -> sender: progress + missing list

        // Tunables (overridable via CharactersConfig once it is initialized).
        private const int DefaultChunkSize = 256 * 1024;        // bytes of payload per chunk
        private const int HardChunkCeiling = 480 * 1024;        // keep the ZPackage under Steam's 512 KB cap
        private const int DefaultQueueBackpressureBytes = 200_000;
        private const float DefaultNoProgressTimeout = 120f;    // seconds without ACK advance => fail (no disconnect)
        private const float AckFlushInterval = 0.5f;            // receiver ACK cadence
        private const int MaxMissingPerAck = 256;               // cap the missing list per ACK message

        private static readonly Dictionary<string, Action<long, byte[]>> _receivers =
            new Dictionary<string, Action<long, byte[]>>(StringComparer.Ordinal);

        private static readonly Dictionary<long, SendState> _sends = new Dictionary<long, SendState>();
        private static readonly Dictionary<string, RecvState> _recvs = new Dictionary<string, RecvState>();

        private static long _nextTransferId = 1;
        private static bool _rpcsRegistered;

        // ── Public API ───────────────────────────────────────────────────────────────────────

        /// <summary>Register the handler invoked with the full payload once a transfer completes on this channel.</summary>
        public static void RegisterReceiver(string channel, Action<long, byte[]> onPayload)
        {
            if (string.IsNullOrEmpty(channel) || onPayload == null) return;
            _receivers[channel] = onPayload;
        }

        /// <summary>Bind the three routed RPCs. Safe to call repeatedly; only the first call registers.</summary>
        public static void EnsureRpcsRegistered()
        {
            if (_rpcsRegistered) return;
            if (ZRoutedRpc.instance == null) return;

            ZRoutedRpc.instance.Register<ZPackage>(RPC_Meta, OnMeta);
            ZRoutedRpc.instance.Register<ZPackage>(RPC_Data, OnData);
            ZRoutedRpc.instance.Register<ZPackage>(RPC_Ack, OnAck);
            _rpcsRegistered = true;

            Driver.Ensure();
            Debug.Log("[Characters] ReliableChunkChannel RPCs registered.");
        }

        /// <summary>
        /// Stream <paramref name="payload"/> to <paramref name="targetPeer"/> on <paramref name="channel"/>.
        /// <paramref name="onComplete"/> is invoked with true on confirmed delivery, false on a no-progress
        /// timeout. The peer connection is never forcibly closed by this method.
        /// </summary>
        public static void Send(long targetPeer, string channel, byte[] payload, Action<bool> onComplete = null)
        {
            if (ZRoutedRpc.instance == null) { onComplete?.Invoke(false); return; }
            payload ??= Array.Empty<byte>();

            int chunkSize = LinkPace.ChunkBytes(SocketOf(targetPeer));
            int totalChunks = Math.Max(1, (payload.Length + chunkSize - 1) / chunkSize);

            var state = new SendState
            {
                TransferId = _nextTransferId++,
                Target = targetPeer,
                Channel = channel,
                Payload = payload,
                ChunkSize = chunkSize,
                TotalChunks = totalChunks,
                Hash = Sha256(payload),
                OnComplete = onComplete,
                LastProgress = Time.realtimeSinceStartup,
                Acked = new bool[totalChunks]
            };
            _sends[state.TransferId] = state;

            Driver.Ensure().StartCoroutine(SendRoutine(state));
        }

        // ── Sender ───────────────────────────────────────────────────────────────────────────

        private static IEnumerator SendRoutine(SendState s)
        {
            // 1) Announce the transfer.
            var meta = new ZPackage();
            meta.Write(s.TransferId);
            meta.Write(s.Channel ?? string.Empty);
            meta.Write(s.Payload.Length);
            meta.Write(s.TotalChunks);
            meta.Write(s.ChunkSize);
            meta.Write(s.Hash);
            ZRoutedRpc.instance.InvokeRoutedRPC(s.Target, RPC_Meta, meta);

            Debug.Log($"[Characters] TX {s.TransferId} '{s.Channel}' -> {s.Target}: {s.Payload.Length}B in {s.TotalChunks} chunk(s) of {s.ChunkSize}B");

            // 2) Stream every chunk once, then keep resending whatever the receiver still reports missing,
            //    until it confirms completion or progress stalls past the timeout.
            int round = 0;
            while (!s.Complete)
            {
                IEnumerable<int> toSend = round == 0
                    ? Enumerable.Range(0, s.TotalChunks)
                    : s.PendingResend.ToArray();
                s.PendingResend.Clear();

                foreach (int i in toSend)
                {
                    if (s.Complete) break;
                    if (i < 0 || i >= s.TotalChunks || s.Acked[i]) continue;

                    // Back-pressure: wait while the peer's send queue is saturated. No timeout, no kill.
                    while (LinkPace.Saturated(SocketOf(s.Target)))
                    {
                        if (TimedOut(s)) { Fail(s); yield break; }
                        yield return null;
                    }

                    ZRoutedRpc.instance.InvokeRoutedRPC(s.Target, RPC_Data, BuildChunk(s, i));
                    yield return null; // one chunk per frame keeps ZNet's pump responsive
                }

                round++;

                // Wait for an ACK to advance progress (or for the resend list to fill), bounded by the
                // no-progress timeout. We never break the connection here.
                float waitStart = Time.realtimeSinceStartup;
                while (!s.Complete && s.PendingResend.Count == 0)
                {
                    if (TimedOut(s)) { Fail(s); yield break; }
                    // If everything is acked but no completion flag arrived yet, keep waiting for it.
                    if (Time.realtimeSinceStartup - waitStart > AckFlushInterval * 4f) break; // re-loop to resend gaps
                    yield return null;
                }
            }

            s.OnComplete?.Invoke(true);
            _sends.Remove(s.TransferId);
            Debug.Log($"[Characters] TX {s.TransferId} complete ({s.Payload.Length}B).");
        }

        private static ZPackage BuildChunk(SendState s, int index)
        {
            int offset = index * s.ChunkSize;
            int len = Math.Min(s.ChunkSize, s.Payload.Length - offset);
            var chunk = new byte[Math.Max(0, len)];
            if (len > 0) Array.Copy(s.Payload, offset, chunk, 0, len);

            var pkg = new ZPackage();
            pkg.Write(s.TransferId);
            pkg.Write(index);
            pkg.Write(chunk);
            return pkg;
        }

        private static void OnAck(long sender, ZPackage pkg)
        {
            long transferId = pkg.ReadLong();
            bool complete = pkg.ReadBool();
            int missingCount = pkg.ReadInt();
            var missing = new List<int>(missingCount);
            for (int i = 0; i < missingCount; i++) missing.Add(pkg.ReadInt());

            if (!_sends.TryGetValue(transferId, out var s)) return;

            s.LastProgress = Time.realtimeSinceStartup;

            // Mark everything not in the missing set as acked.
            var missingSet = new HashSet<int>(missing);
            for (int i = 0; i < s.TotalChunks; i++)
                if (!missingSet.Contains(i)) s.Acked[i] = true;

            if (complete) { s.Complete = true; return; }
            foreach (int m in missing) s.PendingResend.Add(m);
        }

        private static void Fail(SendState s)
        {
            _sends.Remove(s.TransferId);
            Debug.LogWarning($"[Characters] TX {s.TransferId} '{s.Channel}' failed: no progress for {CharactersConfig.NoProgressTimeoutSeconds:0}s " +
                             $"({s.Acked.Count(a => a)}/{s.TotalChunks} chunks acked). Connection left intact.");
            s.OnComplete?.Invoke(false);
        }

        private static bool TimedOut(SendState s) =>
            Time.realtimeSinceStartup - s.LastProgress > CharactersConfig.NoProgressTimeoutSeconds;

        // ── Receiver ─────────────────────────────────────────────────────────────────────────

        private static void OnMeta(long sender, ZPackage pkg)
        {
            long transferId = pkg.ReadLong();
            string channel = pkg.ReadString();
            int totalBytes = pkg.ReadInt();
            int totalChunks = pkg.ReadInt();
            int chunkSize = pkg.ReadInt();
            byte[] hash = pkg.ReadByteArray();

            string key = RecvKey(sender, transferId);
            _recvs[key] = new RecvState
            {
                Sender = sender,
                TransferId = transferId,
                Channel = channel,
                TotalBytes = totalBytes,
                TotalChunks = totalChunks,
                ChunkSize = chunkSize,
                Hash = hash,
                Chunks = new byte[totalChunks][],
                Received = 0,
                LastProgress = Time.realtimeSinceStartup,
                LastAck = 0f
            };
            Driver.Ensure();
            Debug.Log($"[Characters] RX {transferId} '{channel}' from {sender}: expecting {totalBytes}B in {totalChunks} chunk(s)");
        }

        private static void OnData(long sender, ZPackage pkg)
        {
            long transferId = pkg.ReadLong();
            int index = pkg.ReadInt();
            byte[] chunk = pkg.ReadByteArray();

            string key = RecvKey(sender, transferId);
            if (!_recvs.TryGetValue(key, out var r)) return; // META not seen (or already completed) — ignore
            if (index < 0 || index >= r.TotalChunks) return;

            if (r.Chunks[index] == null)
            {
                r.Chunks[index] = chunk;
                r.Received++;
                r.LastProgress = Time.realtimeSinceStartup;
            }

            if (r.Received >= r.TotalChunks)
                CompleteReceive(key, r);
        }

        private static void CompleteReceive(string key, RecvState r)
        {
            var payload = new byte[r.TotalBytes];
            int offset = 0;
            for (int i = 0; i < r.TotalChunks; i++)
            {
                var c = r.Chunks[i];
                if (c == null) { r.Received--; return; } // race: not actually complete
                Array.Copy(c, 0, payload, offset, c.Length);
                offset += c.Length;
            }

            if (!HashEquals(Sha256(payload), r.Hash))
            {
                // Integrity failure: drop the bad chunks and let the sender resend everything.
                Debug.LogWarning($"[Characters] RX {r.TransferId} hash mismatch — requesting full resend.");
                Array.Clear(r.Chunks, 0, r.Chunks.Length);
                r.Received = 0;
                SendAck(r, complete: false);
                return;
            }

            SendAck(r, complete: true);
            _recvs.Remove(key);
            Debug.Log($"[Characters] RX {r.TransferId} complete ({r.TotalBytes}B) — delivering on '{r.Channel}'.");

            if (_receivers.TryGetValue(r.Channel ?? string.Empty, out var handler))
            {
                try { handler(r.Sender, payload); }
                catch (Exception ex) { Debug.LogError($"[Characters] receiver for '{r.Channel}' threw: {ex}"); }
            }
            else
            {
                Debug.LogWarning($"[Characters] no receiver registered for channel '{r.Channel}'.");
            }
        }

        private static void SendAck(RecvState r, bool complete)
        {
            var missing = new List<int>();
            if (!complete)
            {
                for (int i = 0; i < r.TotalChunks && missing.Count < MaxMissingPerAck; i++)
                    if (r.Chunks[i] == null) missing.Add(i);
            }

            var pkg = new ZPackage();
            pkg.Write(r.TransferId);
            pkg.Write(complete);
            pkg.Write(missing.Count);
            foreach (int m in missing) pkg.Write(m);
            ZRoutedRpc.instance.InvokeRoutedRPC(r.Sender, RPC_Ack, pkg);
            r.LastAck = Time.realtimeSinceStartup;
        }

        // ── Driver: periodic ACK flush + no-progress cleanup ─────────────────────────────────

        private class Driver : MonoBehaviour
        {
            private static Driver _instance;

            public static Driver Ensure()
            {
                if (_instance != null) return _instance;
                var go = new GameObject("FiresVAngarde_ReliableChunkChannel");
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<Driver>();
                return _instance;
            }

            private void Update()
            {
                if (_recvs.Count == 0) return;
                float now = Time.realtimeSinceStartup;

                // Throttled progress ACKs so the sender can resend gaps, plus drop stalled receives.
                List<string> dead = null;
                foreach (var kv in _recvs)
                {
                    var r = kv.Value;
                    if (now - r.LastAck >= AckFlushInterval && r.Received < r.TotalChunks)
                        SendAck(r, complete: false);

                    if (now - r.LastProgress > CharactersConfig.NoProgressTimeoutSeconds)
                        (dead ??= new List<string>()).Add(kv.Key);
                }
                if (dead != null)
                    foreach (var k in dead)
                    {
                        Debug.LogWarning($"[Characters] RX {k} timed out (no chunk progress) — dropping.");
                        _recvs.Remove(k);
                    }
            }
        }

        // ── Helpers / state ──────────────────────────────────────────────────────────────────

        private static string RecvKey(long sender, long transferId) => sender + ":" + transferId;

        private static ISocket SocketOf(long peerId)
        {
            try { return ZNet.instance?.GetPeer(peerId)?.m_socket; }
            catch { return null; }
        }

        private static byte[] Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        private static bool HashEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private class SendState
        {
            public long TransferId;
            public long Target;
            public string Channel;
            public byte[] Payload;
            public int ChunkSize;
            public int TotalChunks;
            public byte[] Hash;
            public bool[] Acked;
            public readonly HashSet<int> PendingResend = new HashSet<int>();
            public bool Complete;
            public float LastProgress;
            public Action<bool> OnComplete;
        }

        private class RecvState
        {
            public long Sender;
            public long TransferId;
            public string Channel;
            public int TotalBytes;
            public int TotalChunks;
            public int ChunkSize;
            public byte[] Hash;
            public byte[][] Chunks;
            public int Received;
            public float LastProgress;
            public float LastAck;
        }
    }
}
