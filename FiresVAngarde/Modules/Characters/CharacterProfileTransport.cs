using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Profile transfer over a DIRECT per-peer RPC (<c>peer.m_rpc</c>). Going direct (rather than the
    /// routed <see cref="Transport.ReliableChunkChannel"/>) is deliberate: the login
    /// <c>BufferingSocket</c> buffers PeerInfo/RoutedRPC/ZDOData until the profile is delivered, and a
    /// custom direct RPC passes straight through that buffer — so the profile lands before world
    /// streaming. This is ServerCharacters' <c>Shared.sendCompressedDataToPeer</c> /
    /// <c>receiveCompressedFromPeer</c>, MINUS its fixed 30 s send-queue kill: we apply pure
    /// back-pressure and never disconnect.
    /// </summary>
    public static class CharacterProfileTransport
    {
        private static long _counter;
        private const float CacheExpirySeconds = 120f;

        private class Reassembly
        {
            public readonly SortedDictionary<int, byte[]> Fragments = new SortedDictionary<int, byte[]>();
            public int Total;
            public float Expiry;
        }

        private static readonly Dictionary<string, Reassembly> _incoming = new Dictionary<string, Reassembly>();

        public static void SendToPeer(ZNetPeer peer, string rpcName, byte[] payload, Action onComplete)
        {
            if (peer?.m_rpc == null || ZNet.instance == null) { onComplete?.Invoke(); return; }
            ZNet.instance.StartCoroutine(SendRoutine(peer, rpcName, payload ?? Array.Empty<byte>(), onComplete));
        }

        private static IEnumerator SendRoutine(ZNetPeer peer, string rpcName, byte[] payload, Action onComplete)
        {
            byte[] data = Compress(payload);
            int chunkSize = Transport.LinkPace.ChunkBytes(peer.m_socket);
            int fragments = Math.Max(1, (data.Length + chunkSize - 1) / chunkSize);
            long packageId = ++_counter;
            Debug.Log($"[Characters] {rpcName}: {payload.Length}B ({data.Length}B deflated) in {fragments} fragment(s); " +
                      $"{Transport.LinkPace.Describe(peer.m_socket)}.");

            for (int i = 0; i < fragments; i++)
            {
                // Pure back-pressure: wait while the socket is saturated. Never disconnect.
                while (peer.m_socket != null && peer.m_socket.IsConnected() && Transport.LinkPace.Saturated(peer.m_socket))
                    yield return null;

                if (peer.m_socket == null || !peer.m_socket.IsConnected()) { onComplete?.Invoke(); yield break; }

                var pkg = new ZPackage();
                pkg.Write(packageId);
                pkg.Write(i);
                pkg.Write(fragments);
                pkg.Write(Slice(data, i * chunkSize, chunkSize));
                peer.m_rpc.Invoke(rpcName, pkg);

                if (i != fragments - 1) yield return null;
            }

            onComplete?.Invoke();
        }

        /// <summary>Build a per-peer RPC handler that reassembles + decompresses, then delivers the payload.</summary>
        public static Action<ZRpc, ZPackage> Receiver(Action<ZRpc, byte[]> onComplete)
        {
            return (rpc, pkg) =>
            {
                float now = Time.realtimeSinceStartup;
                foreach (var staleKey in _incoming.Where(kv => kv.Value.Expiry < now).Select(kv => kv.Key).ToList())
                    _incoming.Remove(staleKey);

                long packageId = pkg.ReadLong();
                int index = pkg.ReadInt();
                int total = pkg.ReadInt();
                byte[] frag = pkg.ReadByteArray();

                string key = (rpc?.GetSocket()?.GetHostName() ?? "?") + ":" + packageId;
                if (!_incoming.TryGetValue(key, out var r))
                {
                    r = new Reassembly { Total = total };
                    _incoming[key] = r;
                }
                r.Fragments[index] = frag;
                r.Expiry = now + CacheExpirySeconds;

                if (r.Fragments.Count < total) return;

                _incoming.Remove(key);
                byte[] payload;
                try { payload = Decompress(Concat(r.Fragments.Values)); }
                catch (Exception ex) { Debug.LogError($"[Characters] decompress failed: {ex.Message}"); return; }

                try { onComplete?.Invoke(rpc, payload); }
                catch (Exception ex) { Debug.LogError($"[Characters] profile receive handler threw: {ex}"); }
            };
        }

        // The fragments in index order, joined by block copies: enumerating a multi-megabyte profile one byte at a time
        // (SelectMany) was most of the dedi's CPU in this handler.
        private static byte[] Concat(ICollection<byte[]> fragments)
        {
            int length = 0;
            foreach (byte[] fragment in fragments) length += fragment.Length;
            var joined = new byte[length];
            int offset = 0;
            foreach (byte[] fragment in fragments)
            {
                Buffer.BlockCopy(fragment, 0, joined, offset, fragment.Length);
                offset += fragment.Length;
            }
            return joined;
        }

        private static byte[] Compress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var deflate = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                deflate.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] Slice(byte[] src, int offset, int len)
        {
            int n = Math.Min(len, src.Length - offset);
            if (n <= 0) return Array.Empty<byte>();
            var dst = new byte[n];
            Array.Copy(src, offset, dst, 0, n);
            return dst;
        }
    }
}
