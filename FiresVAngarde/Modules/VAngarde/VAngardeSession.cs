using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Session-UID handshake — the "telephone" layer. On <see cref="OnServerStart"/> the server
    /// generates a 32-byte random session UID. When a peer connects, the server sends the UID
    /// encrypted with a baked AES key over the <see cref="RpcSessionInit"/> RPC; the client stores
    /// it. Each subsequent challenge response is signed with <c>HMAC-SHA256(payload, sessionUid)</c>;
    /// server verifies the HMAC and treats a mismatch as a violation.
    ///
    /// What this catches: wire-level forgers (no UID, can't sign), dummy clients that never joined,
    /// and cross-session replay (each load has a fresh UID). It does NOT catch in-process cheats
    /// that read the UID out of the client's own memory — but those cheats must load VAngarde to
    /// get the UID, which then exposes them to <see cref="VAngardeModListAudit"/>.
    ///
    /// The baked AES key is obfuscation against wire sniffers and casual decompiler greps, not
    /// against a determined attacker. The real strength is the cross-layer guarantee.
    /// </summary>
    public static class VAngardeSession
    {
        public const string RpcSessionInit = "VA_vgs"; // server -> client: encrypted session UID

        private static byte[] _serverSessionUid; // server-side master
        private static byte[] _clientSessionUid; // client-side copy received from server

        private static readonly Dictionary<long, bool> _peerHasUid = new();

        public static bool ServerActive => _serverSessionUid != null;
        public static bool ClientHasUid => _clientSessionUid != null;

        public static void OnServerStart()
        {
            using var rng = RandomNumberGenerator.Create();
            _serverSessionUid = new byte[32];
            rng.GetBytes(_serverSessionUid);
            if (VAngardeConfig.VerboseLogging != null && VAngardeConfig.VerboseLogging.Value)
                Debug.Log("[VAngarde] session: generated server session UID (32 bytes).");
        }

        public static void OnServerShutdown()
        {
            _serverSessionUid = null;
            _peerHasUid.Clear();
        }

        public static void OnClientDisconnect() => _clientSessionUid = null;

        /// <summary>Server: encrypt + send the session UID to <paramref name="peer"/>.</summary>
        public static void SendUidToPeer(ZNetPeer peer)
        {
            if (peer?.m_rpc == null || _serverSessionUid == null) return;
            try
            {
                byte[] iv = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);
                byte[] cipher = AesEncrypt(_serverSessionUid, BakedKey, iv);

                var pkg = new ZPackage();
                pkg.Write(iv);
                pkg.Write(cipher);
                peer.m_rpc.Invoke(RpcSessionInit, pkg);
                _peerHasUid[peer.m_uid] = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] session: SendUidToPeer failed: {ex.Message}");
            }
        }

        /// <summary>Client: decrypt + store the session UID from the server.</summary>
        public static void OnClientReceiveUid(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                byte[] iv = pkg.ReadByteArray();
                byte[] cipher = pkg.ReadByteArray();
                _clientSessionUid = AesDecrypt(cipher, BakedKey, iv);
                Debug.Log($"[VAngarde] session: received session UID from server ({_clientSessionUid?.Length} bytes).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] session: OnClientReceiveUid failed: {ex.Message}");
                _clientSessionUid = null;
            }
        }

        /// <summary>True iff the server has confirmed the UID was delivered to <paramref name="peerId"/>.</summary>
        public static bool PeerHasUid(long peerId) => _peerHasUid.TryGetValue(peerId, out var v) && v;

        public static void ForgetPeer(long peerId) => _peerHasUid.Remove(peerId);

        /// <summary>Sign <paramref name="payload"/> with the local session UID. Returns null if the UID isn't available.</summary>
        public static byte[] SignClient(byte[] payload)
        {
            if (_clientSessionUid == null || payload == null) return null;
            using var hmac = new HMACSHA256(_clientSessionUid);
            return hmac.ComputeHash(payload);
        }

        /// <summary>Server-side verify of an HMAC-SHA256 produced with the master session UID.</summary>
        public static bool VerifyServer(byte[] payload, byte[] signature)
        {
            if (_serverSessionUid == null || payload == null || signature == null) return false;
            using var hmac = new HMACSHA256(_serverSessionUid);
            byte[] expected = hmac.ComputeHash(payload);
            return ConstantTimeEquals(expected, signature);
        }

        /// <summary>
        /// Canonical sig input shared by client (sign) and server (verify): UTF-8 bytes of the
        /// echoed nonce concatenated with the modlist hash. Binds the signature to both freshness
        /// (nonce changes every challenge) and content (hash changes if claimed modlist changes).
        /// </summary>
        public static byte[] ComposeSigInput(string nonce, byte[] modListHash)
        {
            byte[] nonceBytes = System.Text.Encoding.UTF8.GetBytes(nonce ?? string.Empty);
            byte[] hashBytes  = modListHash ?? Array.Empty<byte>();
            var combined = new byte[nonceBytes.Length + hashBytes.Length];
            Array.Copy(nonceBytes, 0, combined, 0, nonceBytes.Length);
            Array.Copy(hashBytes, 0, combined, nonceBytes.Length, hashBytes.Length);
            return combined;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        // Obfuscation against wire sniffers. NOT a security boundary — assume a determined attacker
        // dumps it out of the DLL. Derived at runtime from a long literal + simple mixing so a raw
        // hex grep doesn't reveal it directly.
        private static byte[] _bakedKey;
        private static byte[] BakedKey => _bakedKey ??= DeriveBakedKey();

        private static byte[] DeriveBakedKey()
        {
            const string seed = "fires.vangarde.session.2026.06.bakedKey.¶Δ§_telephoneGameRoundtrip";
            using var sha = SHA256.Create();
            return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
        }

        private static byte[] AesEncrypt(byte[] plain, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var enc = aes.CreateEncryptor();
            return enc.TransformFinalBlock(plain, 0, plain.Length);
        }

        private static byte[] AesDecrypt(byte[] cipher, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(cipher, 0, cipher.Length);
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
