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
    ///   bytes, and only restores when (signature valid) AND the backup is newer than what the server holds.
    ///
    /// • Newer (0.2.36, R90 runs 2/3: a server stopped with players online lost their whole session): the signature also carries
    ///   how many uploads the client had SENT in that session, hashed in with the profile. For every upload it accepts, the server
    ///   keeps <c>&lt;file&gt;.fch.session</c> = { the session's key time, uploads accepted in it }. A backup restores when it comes
    ///   from a later session, or from the same session with at least as many uploads sent as the server took: it was written after
    ///   all of them. Only the server's clock is involved. A signature without the count (an older client) or a character without
    ///   the session file falls back to the first rule: key time later than the .fch's last write.
    /// </summary>
    public static class CharactersEmergencyBackup
    {
        internal const string RpcKeyExchange = "VA_CharKey"; // server -> client: {key, time}
        internal const string RpcCheckSig    = "VA_CharSig"; // client -> server: signed backup
        internal const string EmergencyPendingFlagKey = "VAEmergencyBackupPending";

        private static byte[] _clientKey;     // client-side, key derived by the server
        private static long   _clientKeyTime; // matching server timestamp
        private static bool   _emergencyBackupPending;

        // Server: the key time sent to each connection this session, so an accepted upload can be tied to its session.
        private static readonly System.Collections.Generic.Dictionary<ZRpc, long> s_peerKeyTime =
            new System.Collections.Generic.Dictionary<ZRpc, long>();

        // ── Server ───────────────────────────────────────────────────────────────────────────

        /// <summary>Server: the key time this connection was given this session (0 when none was sent).</summary>
        internal static long PeerKeyTime(ZRpc rpc) => rpc != null && s_peerKeyTime.TryGetValue(rpc, out long time) ? time : 0L;

        /// <summary>
        /// Server: an upload made in the session keyed <paramref name="keyTime"/> was saved for <paramref name="steamId"/>/
        /// <paramref name="charName"/>. Counts it in that character's session file (key time, uploads accepted in it), the restore
        /// gate's other half.
        /// </summary>
        internal static void NoteAcceptedUpload(long keyTime, string steamId, string charName)
        {
            if (keyTime == 0L || string.IsNullOrEmpty(steamId) || string.IsNullOrEmpty(charName)) return;
            try
            {
                string path = SessionPath(steamId, charName);
                ReadSession(path, out long knownTime, out int accepted);
                accepted = knownTime == keyTime ? accepted + 1 : 1;
                File.WriteAllText(path, $"{keyTime} {accepted}");
            }
            catch (Exception ex) { Debug.LogWarning($"[Characters] emergency-backup: couldn't note the upload for {steamId}/{charName}: {ex.Message}"); }
        }

        internal static void ForgetPeer(ZRpc rpc)
        {
            if (rpc != null) s_peerKeyTime.Remove(rpc);
        }

        private static string SessionPath(string steamId, string charName) =>
            CharacterStore.SaveDir + CharacterStore.FileName(steamId, charName) + ".fch.session";

        private static bool ReadSession(string path, out long keyTime, out int accepted)
        {
            keyTime = 0L;
            accepted = 0;
            if (!File.Exists(path)) return false;
            string[] parts = File.ReadAllText(path).Trim().Split(' ');
            return parts.Length == 2 && long.TryParse(parts[0], out keyTime) && int.TryParse(parts[1], out accepted);
        }

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
                s_peerKeyTime[peer.m_rpc] = time;
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

                long time = VerifySignature(profileBytes, signatureZpkg, out int sentCount, out bool counted);
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

                string newer;
                if (counted && ReadSession(SessionPath(steamId, charName), out long lastSession, out int accepted))
                {
                    // 0.2.36: by session and upload count, on the server's clock only.
                    if (time < lastSession)
                    {
                        Debug.Log($"[Characters] emergency-backup: {steamId}/{charName}'s backup is from an earlier session than its last save — skipping.");
                        return;
                    }
                    if (time == lastSession && sentCount < accepted)
                    {
                        Debug.Log($"[Characters] emergency-backup: {steamId}/{charName}'s backup is older than the last save of that session (sent {sentCount}, the server took {accepted}) — skipping.");
                        return;
                    }
                    newer = time > lastSession ? "a later session than its last save" : $"after the last save of that session (sent {sentCount}, the server took {accepted})";
                }
                else
                {
                    if (new DateTime(time) <= fi.LastWriteTime)
                    {
                        Debug.Log($"[Characters] emergency-backup: signature is stale (sig {new DateTime(time):o} <= current {fi.LastWriteTime:o}) — skipping.");
                        return;
                    }
                    newer = "its session started after the last save";
                }

                if (CharacterStore.SaveProfileBytes(steamId, charName, profileBytes))
                    Debug.Log($"[Characters] emergency-backup: RESTORED {steamId}/{charName} from signed backup ({profileBytes.Length}B; {newer}).");
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

        // The key time when the signature is valid (else 0). A 0.2.36 signature also carries the uploads the client had sent that
        // session (counted = true), hashed in with the profile; an older one hashes the profile alone.
        private static long VerifySignature(byte[] profileData, byte[] signatureZpkgBytes, out int sentCount, out bool counted)
        {
            sentCount = 0;
            counted = false;
            try
            {
                var sigPkg = new ZPackage(signatureZpkgBytes);
                byte[] encryptedHash = sigPkg.ReadByteArray();
                byte[] iv = sigPkg.ReadByteArray();
                long time = sigPkg.ReadLong();
                if (sigPkg.GetPos() < sigPkg.Size())
                {
                    sentCount = sigPkg.ReadInt();
                    counted = true;
                }
                byte[] expectedHash = SHA512.Create().ComputeHash(counted ? WithCount(profileData, sentCount) : profileData);
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

        /// <summary>
        /// Client: after an unclean disconnect, writes the signed backup of <paramref name="profileBytes"/>, counting the
        /// <paramref name="uploadsSent"/> uploads this session already sent (0.2.36: the server's restore gate).
        /// </summary>
        public static bool TryWriteSidecars(PlayerProfile profile, byte[] profileBytes, int uploadsSent)
        {
            if (profile == null || profileBytes == null || profileBytes.Length == 0) return false;
            if (!_emergencyBackupPending || _clientKey == null) return false;
            try
            {
                string baseFile = SidecarBase(profile);
                File.WriteAllBytes(baseFile + ".fch.serverbackup", profileBytes);
                File.WriteAllBytes(baseFile + ".fch.signature", BuildSignature(profileBytes, _clientKey, _clientKeyTime, uploadsSent));
                Debug.Log($"[Characters] emergency-backup: wrote sidecars for {profile.m_filename} ({profileBytes.Length}B, after {uploadsSent} upload(s) this session).");
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
                string baseFile = SidecarBase(profile);
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
                string baseFile = SidecarBase(profile);
                foreach (var ext in new[] { ".fch.signature", ".fch.serverbackup" })
                    if (File.Exists(baseFile + ext)) File.Delete(baseFile + ext);
            }
            catch { /* best-effort cleanup */ }
        }

        // Client: the sidecars live in the LOCAL character folder whatever the profile's own file source (0.2.37, run 7: the server-held
        // profile a client adopts resolved to "\characters\", so "C:\characters\coop1.fch.serverbackup" failed and the backup never existed).
        private static string SidecarBase(PlayerProfile profile)
        {
            string folder = SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local);
            Directory.CreateDirectory(folder);
            return folder + profile.m_filename;
        }

        private static byte[] WithCount(byte[] profileBytes, int count)
        {
            var data = new byte[profileBytes.Length + 4];
            Buffer.BlockCopy(profileBytes, 0, data, 0, profileBytes.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(count), 0, data, profileBytes.Length, 4);
            return data;
        }

        private static byte[] BuildSignature(byte[] profileBytes, byte[] key, long time, int uploadsSent)
        {
            byte[] hash = SHA512.Create().ComputeHash(WithCount(profileBytes, uploadsSent));
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
            pkg.Write(uploadsSent);
            return pkg.GetArray();
        }
    }
}
