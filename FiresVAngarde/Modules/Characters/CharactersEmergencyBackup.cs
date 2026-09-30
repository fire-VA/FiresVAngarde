using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Anti-rollback emergency-backup signature scheme — adapted from ServerCharacters so the
    /// existing <c>.fch.signature</c> / <c>.fch.serverbackup</c> sidecar files migrate untouched:
    ///
    /// • Server master key: persistent base64 32-byte secret (<see cref="CharactersConfig.ServerKey"/>).
    ///   Generated once on first server start. Per-connection key = Rfc2898DeriveBytes(masterKey,
    ///   BitConverter.GetBytes(time), 1000) → 32 bytes. Server sends (key, time) to each peer on
    ///   connect; client stores both.
    ///
    /// • On an unclean disconnect, the client's next save writes the raw profile bytes to
    ///   <c>.fch.serverbackup</c> and a ZPackage signature to <c>.fch.signature</c> = <c>{
    ///   AES_encrypt(SHA512(profileBytes), key, IV), IV, time }</c>. On reconnect the client ships
    ///   both files to the server, which derives the same key from the embedded <c>time</c> using
    ///   its master key, decrypts the SHA512, compares against the recomputed hash of the backup
    ///   bytes, and only restores when (signature valid) AND (time &gt; current .fch's last-write).
    ///   The time gate is the actual rollback guard: stale signatures can't overwrite newer state.
    /// </summary>
    public static class CharactersEmergencyBackup
    {
        internal const string RpcKeyExchange = "VA_CharKey"; // server -> client: {key, time}
        internal const string RpcCheckSig    = "VA_CharSig"; // client -> server: signed backup
        internal const string EmergencyPendingFlagKey = "VAEmergencyBackupPending";

        private static byte[] _clientKey;     // client-side, key derived by the server
        private static long   _clientKeyTime; // matching server timestamp
        private static bool   _emergencyBackupPending;

        // ── Server ───────────────────────────────────────────────────────────────────────────

        public static void EnsureServerKey()
        {
            string current = CharactersConfig.ServerKey?.Value;
            if (!string.IsNullOrEmpty(current)) return;
            byte[] master = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(master);
            CharactersConfig.ServerKey.Value = Convert.ToBase64String(master);
            if (FiresLogger.VerboseEnabled)
                Debug.Log("[Characters] emergency-backup: generated server master key.");
        }

        public static void SendKeyToPeer(ZNetPeer peer)
        {
            if (peer?.m_rpc == null) return;
            try
            {
                long time = DateTime.Now.Ticks;
                byte[] key = DeriveKey(time);
                if (key == null) return;
                var pkg = new ZPackage();
                pkg.Write(key);
                pkg.Write(time);
                peer.m_rpc.Invoke(RpcKeyExchange, pkg);
            }
            catch (Exception ex) { Debug.LogWarning($"[Characters] SendKeyToPeer failed: {ex.Message}"); }
        }

        /// <summary>Server-side RPC handler: validate a signed emergency backup and restore on success.</summary>
        public static void OnServerReceiveSignature(ZRpc rpc, ZPackage outerPkg)
        {
            try
            {
                byte[] profileBytes = outerPkg.ReadByteArray();
                byte[] signatureZpkg = outerPkg.ReadByteArray();

                long time = VerifySignature(profileBytes, signatureZpkg);
                if (time <= 0L)
                {
                    Debug.Log($"[Characters] emergency-backup: invalid signature from {rpc.m_socket?.GetHostName()} — skipping.");
                    return;
                }

                // Decode the profile to learn its character name (for the target .fch filename).
                var stage = new PlayerProfile(fileSource: FileHelpers.FileSource.Local);
                if (!stage.LoadFromBytes(profileBytes))
                {
                    Debug.Log("[Characters] emergency-backup: profile bytes failed to decode — skipping.");
                    return;
                }

                string host = rpc.m_socket?.GetHostName();
                string steamId = !string.IsNullOrEmpty(host) && System.Text.RegularExpressions.Regex.IsMatch(host, "^\\d+$") ? "Steam_" + host : host;
                string charName = stage.m_playerName?.ToLower();
                if (string.IsNullOrEmpty(steamId) || string.IsNullOrEmpty(charName)) return;

                string fch = CharacterStore.SaveDir + CharacterStore.FileName(steamId, charName) + ".fch";
                var fi = new FileInfo(fch);
                if (!fi.Exists)
                {
                    Debug.Log($"[Characters] emergency-backup: no existing .fch for {steamId}/{charName} — skipping restore.");
                    return;
                }

                if (new DateTime(time) <= fi.LastWriteTime)
                {
                    Debug.Log($"[Characters] emergency-backup: signature is stale (sig {new DateTime(time):o} <= current {fi.LastWriteTime:o}) — skipping.");
                    return;
                }

                if (CharacterStore.SaveProfileBytes(steamId, charName, profileBytes))
                    Debug.Log($"[Characters] emergency-backup: RESTORED {steamId}/{charName} from signed backup.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] OnServerReceiveSignature threw: {ex.Message}");
            }
        }

        private static byte[] DeriveKey(long time)
        {
            string masterB64 = CharactersConfig.ServerKey?.Value;
            if (string.IsNullOrEmpty(masterB64)) return null;
            using var derive = new Rfc2898DeriveBytes(masterB64, BitConverter.GetBytes(time), 1000);
            return derive.GetBytes(32);
        }

        private static long VerifySignature(byte[] profileData, byte[] signatureZpkgBytes)
        {
            try
            {
                byte[] expectedHash = SHA512.Create().ComputeHash(profileData);
                var sigPkg = new ZPackage(signatureZpkgBytes);
                byte[] encryptedHash = sigPkg.ReadByteArray();
                byte[] iv = sigPkg.ReadByteArray();
                long time = sigPkg.ReadLong();
                byte[] key = DeriveKey(time);
                if (key == null) return 0L;

                using var aes = Aes.Create();
                aes.Key = key; aes.IV = iv;
                using var dec = aes.CreateDecryptor();
                byte[] decryptedHash = dec.TransformFinalBlock(encryptedHash, 0, encryptedHash.Length);

                return decryptedHash.SequenceEqual(expectedHash) ? time : 0L;
            }
            catch { return 0L; }
        }

        // ── Client ───────────────────────────────────────────────────────────────────────────

        public static void OnClientReceiveKey(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                _clientKey = pkg.ReadByteArray();
                _clientKeyTime = pkg.ReadLong();
                Debug.Log("[Characters] emergency-backup: received server-derived key on this session.");
            }
            catch (Exception ex) { Debug.LogWarning($"[Characters] OnClientReceiveKey failed: {ex.Message}"); }
        }

        public static void MarkEmergencyBackupPending() => _emergencyBackupPending = true;

        public static void ClearClientState() { _clientKey = null; _clientKeyTime = 0L; _emergencyBackupPending = false; }

        public static bool TryWriteSidecars(PlayerProfile profile, byte[] profileBytes)
        {
            if (profile == null || profileBytes == null || profileBytes.Length == 0) return false;
            if (!_emergencyBackupPending || _clientKey == null) return false;
            try
            {
                string baseFile = SaveSystem.GetCharacterFolderPath(profile.m_fileSource) + profile.m_filename;
                File.WriteAllBytes(baseFile + ".fch.serverbackup", profileBytes);
                File.WriteAllBytes(baseFile + ".fch.signature", BuildSignature(profileBytes, _clientKey, _clientKeyTime));
                Debug.Log($"[Characters] emergency-backup: wrote sidecars for {profile.m_filename}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] TryWriteSidecars failed: {ex.Message}");
                return false;
            }
        }

        public static void TrySendStoredEmergencyBackup(ZNetPeer serverPeer, PlayerProfile profile)
        {
            if (serverPeer?.m_rpc == null || profile == null) return;
            try
            {
                string baseFile = SaveSystem.GetCharacterFolderPath(profile.m_fileSource) + profile.m_filename;
                string sigPath = baseFile + ".fch.signature";
                string bakPath = baseFile + ".fch.serverbackup";
                if (!File.Exists(sigPath) || !File.Exists(bakPath)) return;

                byte[] profileBytes = File.ReadAllBytes(bakPath);
                byte[] sigBytes = File.ReadAllBytes(sigPath);

                var outer = new ZPackage();
                outer.Write(profileBytes);
                outer.Write(sigBytes);
                serverPeer.m_rpc.Invoke(RpcCheckSig, outer);
                Debug.Log($"[Characters] emergency-backup: sent stored sidecars for {profile.m_filename} to server for verification.");
            }
            catch (Exception ex) { Debug.LogWarning($"[Characters] TrySendStoredEmergencyBackup failed: {ex.Message}"); }
        }

        public static void CleanSidecarsIfAny(PlayerProfile profile)
        {
            if (profile == null) return;
            try
            {
                string baseFile = SaveSystem.GetCharacterFolderPath(profile.m_fileSource) + profile.m_filename;
                foreach (var ext in new[] { ".fch.signature", ".fch.serverbackup" })
                    if (File.Exists(baseFile + ext)) File.Delete(baseFile + ext);
            }
            catch { /* best-effort cleanup */ }
        }

        private static byte[] BuildSignature(byte[] profileBytes, byte[] key, long time)
        {
            byte[] hash = SHA512.Create().ComputeHash(profileBytes);
            using var aes = Aes.Create();
            aes.Key = key;
            // Fresh IV every signature.
            using (var rng = RandomNumberGenerator.Create()) { var iv = new byte[16]; rng.GetBytes(iv); aes.IV = iv; }
            using var enc = aes.CreateEncryptor();
            byte[] cipher = enc.TransformFinalBlock(hash, 0, hash.Length);

            var pkg = new ZPackage();
            pkg.Write(cipher);
            pkg.Write(aes.IV);
            pkg.Write(time);
            return pkg.GetArray();
        }
    }
}
