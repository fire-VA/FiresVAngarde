using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Server-side persistence of player profiles as Valheim <c>.fch</c> files — drop-in compatible
    /// with ServerCharacters: <c>{steamId}_{name}.fch</c> in the local character folder, with rotating
    /// ZIP backups. (De)serialization rides <c>PlayerProfile</c> + <see cref="PlayerProfileBytes.LoadFromBytes"/>,
    /// so the on-disk and wire formats match ServerCharacters' and existing saves migrate untouched.
    /// </summary>
    public static class CharacterStore
    {
        public static string SaveDir => SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local);

        public static string FileName(string steamId, string characterName) =>
            $"{steamId}_{characterName?.ToLowerInvariant()}";

        /// <summary>Wire bytes of a stored profile (FileReader-decoded content), or empty if none exists.</summary>
        public static byte[] LoadProfileBytes(string steamId, string characterName)
        {
            try
            {
                var profile = new PlayerProfile(FileName(steamId, characterName), FileHelpers.FileSource.Local);
                return profile.LoadPlayerDataFromDisk()?.GetArray() ?? Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] LoadProfileBytes failed for {steamId}/{characterName}: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        /// <summary>Persist received wire bytes as the authoritative profile, backing up the prior version.</summary>
        public static bool SaveProfileBytes(string steamId, string characterName, byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            // A save still on the disk thread would land after this one and undo it.
            if (HasPendingSave(steamId)) CharacterDiskWriter.WaitIdle();
            try
            {
                var profile = new PlayerProfile(FileName(steamId, characterName), FileHelpers.FileSource.Local);
                if (!profile.LoadFromBytes(data)) return false;
                profile.SavePlayerToDisk();
                QueueBackup(profile);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Characters] SaveProfileBytes failed for {steamId}/{characterName}: {ex.Message}");
                return false;
            }
        }

        private static readonly Dictionary<string, int> s_pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while an upload of this Steam ID's character is still being written on the disk thread.</summary>
        public static bool HasPendingSave(string steamId)
        {
            lock (s_pending) return s_pending.TryGetValue(steamId ?? "", out int n) && n > 0;
        }

        private static void MarkPending(string steamId, int delta)
        {
            lock (s_pending)
            {
                s_pending.TryGetValue(steamId ?? "", out int n);
                n += delta;
                if (n > 0) s_pending[steamId ?? ""] = n;
                else s_pending.Remove(steamId ?? "");
            }
        }

        /// <summary>
        /// A logout upload: validated here on the main thread by Valheim's own loader (the anti-cheat gate), then written on
        /// the disk thread with the prior version's backup, so the server's frame doesn't wait on the disk (R42: 83 ms, 74-138 ms
        /// in earlier rounds). <paramref name="done"/> runs on the main thread once the file is in place (true) or failed (false),
        /// and is not called when the bytes don't load as a profile (the return value is then false).
        /// </summary>
        public static bool SaveProfileBytesInBackground(string steamId, string characterName, byte[] data, Action<bool, double> done)
        {
            if (data == null || data.Length < 4) return false;
            PlayerProfile profile;
            try
            {
                profile = new PlayerProfile(FileName(steamId, characterName), FileHelpers.FileSource.Local);
                if (!profile.LoadFromBytes(data)) return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Characters] SaveProfileBytes failed for {steamId}/{characterName}: {ex.Message}");
                return false;
            }

            // A current-version upload is byte for byte what Valheim's save would write (R42: the server's .fch = the client's);
            // an older one goes through Valheim's own save, which upgrades it, as does any cloud-storage setup.
            if (BitConverter.ToInt32(data, 0) != (int)global::Version.c_PlayerVersion || FileHelpers.CloudStorageSupported)
            {
                bool saved;
                try { profile.SavePlayerToDisk(); QueueBackup(profile); saved = true; }
                catch (Exception ex)
                {
                    Debug.LogError($"[Characters] SaveProfileBytes failed for {steamId}/{characterName}: {ex.Message}");
                    saved = false;
                }
                done?.Invoke(saved, 0d);
                return true;
            }

            string filename = profile.m_filename;
            string path = SaveDir + filename + ".fch";
            string backupDir = Path.Combine(SaveDir, "backups");
            int keep = Math.Max(1, CharactersConfig.BackupsToKeep);
            DateTime now = DateTime.Now;
            PlayerProfile.SavingStarted?.Invoke();
            MarkPending(steamId, +1);
            CharacterDiskWriter.Enqueue(() =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                bool saved = WriteProfileFile(path, data, filename);
                if (saved && File.Exists(path + ".old"))
                {
                    byte[] previous = File.ReadAllBytes(path + ".old");
                    WriteBackup(Path.Combine(backupDir, $"{filename}.zip"), $"{filename}-{now:yyyy-MM-ddTHH-mm-ss}.fch", keep, previous, filename);
                }
                double ms = timer.Elapsed.TotalMilliseconds;
                MarkPending(steamId, -1);
                FiresCore.Async.MainThreadDispatcher.Enqueue(() =>
                {
                    if (saved)
                    {
                        SaveSystem.InvalidateCache(SaveDataType.Character);
                        ZNet.ConsiderAutoBackup(filename, SaveDataType.Character, now);
                    }
                    PlayerProfile.SavingFinished?.Invoke();
                    done?.Invoke(saved, ms);
                });
            });
            return true;
        }

        // Valheim's .fch layout (length, payload, hash length, SHA-512 of the payload), written beside the file and swapped in with
        // the previous version kept as .fch.old, through the game's own local-file helpers.
        private static bool WriteProfileFile(string path, byte[] data, string filename)
        {
            try
            {
                byte[] hash = new ZPackage(data).GenerateHash();
                var writer = new FileWriter(path + ".new", Splatform.CloudStorageFileGrouping.SameFileEnding, FileHelpers.FileHelperType.Binary, FileHelpers.FileSource.Local);
                writer.m_binary.Write(data.Length);
                writer.m_binary.Write(data);
                writer.m_binary.Write(hash.Length);
                writer.m_binary.Write(hash);
                writer.Finish();
                if (writer.Status != FileWriter.WriterStatus.CloseSucceeded)
                {
                    Debug.LogWarning($"[Characters] writing {filename}.fch.new failed ({writer.Status}); the previous save is kept.");
                    return false;
                }
                FileHelpers.ReplaceOldFile(path, path + ".new", path + ".old", Splatform.CloudStorageFileGrouping.SameFileEnding, FileHelpers.FileSource.Local);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] writing {filename}.fch failed: {ex.Message}; the previous save is kept.");
                return false;
            }
        }

        public static bool Exists(string steamId, string characterName)
        {
            try { return File.Exists(SaveDir + FileName(steamId, characterName) + ".fch"); }
            catch { return false; }
        }

        /// <summary>True if this Steam ID already owns a stored character (single-character enforcement).</summary>
        public static bool SteamIdHasAnyProfile(string steamId) =>
            GetCanonicalCharacterName(steamId) != null;

        /// <summary>
        /// The single character a Steam ID is allowed to play under single-character enforcement, or
        /// <c>null</c> if the Steam ID owns no stored profile yet. Storage keys per (steamId, name), so a
        /// Steam ID can physically own several <c>.fch</c> files (pre-existing extras, or files predating
        /// enforcement). We make the choice deterministic by treating the OLDEST stored profile (earliest
        /// file write time, name as tiebreak) as canonical — "first-created wins" — so every other stored
        /// character for that Steam ID is also blocked, not just brand-new names.
        /// </summary>
        public static string GetCanonicalCharacterName(string steamId)
        {
            try
            {
                if (string.IsNullOrEmpty(steamId) || !Directory.Exists(SaveDir)) return null;

                string prefix = steamId + "_";
                string canonical = null;
                DateTime oldest = DateTime.MaxValue;

                foreach (string file in Directory.GetFiles(SaveDir, "*.fch"))
                {
                    string fileName = Path.GetFileName(file);
                    if (!IsProfilePattern(fileName) ||
                        !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - ".fch".Length);
                    DateTime written = File.GetCreationTimeUtc(file);

                    if (written < oldest ||
                        (written == oldest && string.CompareOrdinal(name, canonical) < 0))
                    {
                        oldest = written;
                        canonical = name;
                    }
                }

                return canonical;
            }
            catch { return null; }
        }

        private static bool IsProfilePattern(string file) =>
            file.Split('_').Length >= 3 && file.EndsWith(".fch", StringComparison.Ordinal) && !file.Contains("_backup_");

        // The prior version (the .fch.old the save just rotated) is read here, on the caller's thread, so the next save may
        // rotate it again at once; only the ZIP rewrite, the slow part, runs on the background writer, in queue order.
        private static void QueueBackup(PlayerProfile profile)
        {
            try
            {
                string folder = SaveSystem.GetCharacterFolderPath(profile.m_fileSource);
                string oldPath = $"{folder}{profile.m_filename}.fch.old";
                if (!FileHelpers.Exists(oldPath, profile.m_fileSource)) return;

                byte[] previous = ReadAll(oldPath, profile.m_fileSource);
                string backupDir = Path.Combine(SaveDir, "backups");
                string zipPath = Path.Combine(backupDir, $"{profile.m_filename}.zip");
                string entryName = $"{profile.m_filename}-{DateTime.Now:yyyy-MM-ddTHH-mm-ss}.fch";
                int keep = Math.Max(1, CharactersConfig.BackupsToKeep);
                string filename = profile.m_filename;
                CharacterDiskWriter.Enqueue(() => WriteBackup(zipPath, entryName, keep, previous, filename));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] backup failed for {profile.m_filename}: {ex.Message}");
            }
        }

        private static byte[] ReadAll(string path, FileHelpers.FileSource source)
        {
            var reader = new FileReader(path, source);
            try
            {
                using var copy = new MemoryStream();
                (reader.m_stream?.BaseStream ?? reader.m_binary.BaseStream).CopyTo(copy);
                return copy.ToArray();
            }
            finally { reader.Dispose(); }
        }

        // The zip holds every backup of the character, so it is edited as a copy that then replaces it: rewritten in place, a
        // crash mid-write could corrupt all of them (Tools\SAVE_SAFETY_AUDIT.md).
        private static void WriteBackup(string zipPath, string entryName, int keep, byte[] previous, string filename)
        {
            try
            {
                FiresCore.IO.AtomicFile.Update(zipPath, stream =>
                {
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
                    while (zip.Entries.Count >= keep && zip.Entries.Count > 0)
                        zip.Entries.First().Delete();

                    using Stream dest = zip.CreateEntry(entryName).Open();
                    dest.Write(previous, 0, previous.Length);
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] backup failed for {filename}: {ex.Message}");
            }
        }
    }
}
