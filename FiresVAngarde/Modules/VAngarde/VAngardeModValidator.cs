using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Manages mod whitelist, blacklist, and exact-match lists for VAngarde.
    /// Lists are loaded from text files in the VAngarde config directory
    /// and can be hot-reloaded via <see cref="ReloadAll"/>.
    /// </summary>
    public static class VAngardeModValidator
    {
        // -- Parsed lists --
        private static readonly HashSet<string> _whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _exactMatch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Optional Phase-5b DLL hash expectations, keyed by GUID. Populated by LoadExactMatch when an
        // entry has the form "guid=version;sha256-hex". Empty when no entries specify a hash.
        private static readonly Dictionary<string, string> _exactMatchHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static IReadOnlyDictionary<string, string> ExactMatchHashes => _exactMatchHashes;
        private static readonly HashSet<string> _exemptPlayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Server-only mods: EXCLUDED from the auto-generated exact-match required set, and always counted
        // as "allowed" for a client that happens to have them. Hand-curated - never auto-generated.
        private static readonly HashSet<string> _serverOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Admin-only mods: ONLY admins may run these client-side. Fully manual (never auto-written), and it
        // OVERRIDES the whitelist - a GUID here is rejected for non-admins even if also whitelisted, and an
        // admin connecting with one never auto-adds it to the whitelist.
        private static readonly HashSet<string> _adminOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per-admin NON-admin character names (normalized SteamID -> character names that should NOT be admin).
        // A real admin (adminlist.txt) playing one of these named characters is demoted to non-admin for that
        // session; every OTHER character keeps admin. Fully manual, hand-curated.
        private static readonly Dictionary<string, HashSet<string>> _adminCharExceptions =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Number of GUIDs currently loaded from the whitelist file.</summary>
        public static int WhitelistCount => _whitelist.Count;

        /// <summary>Number of GUIDs currently loaded from the blacklist file.</summary>
        public static int BlacklistCount => _blacklist.Count;

        /// <summary>Number of GUID=Version entries currently loaded from the exact match file.</summary>
        public static int ExactMatchCount => _exactMatch.Count;

        /// <summary>Number of GUIDs currently loaded from the server-only bypass file.</summary>
        public static int ServerOnlyCount => _serverOnly.Count;

        /// <summary>Number of GUIDs currently loaded from the admin-only file.</summary>
        public static int AdminOnlyCount => _adminOnly.Count;

        /// <summary>Live view of the admin-only GUIDs.</summary>
        internal static IReadOnlyCollection<string> AdminOnlyGuids => _adminOnly;

        /// <summary>Live view of the enforced (exact-match) entries: GUID -> required version.</summary>
        internal static IReadOnlyDictionary<string, string> ExactMatchEntries => _exactMatch;

        /// <summary>Live view of the whitelisted (allowed-extra) GUIDs.</summary>
        internal static IReadOnlyCollection<string> WhitelistGuids => _whitelist;

        /// <summary>Live view of the blacklisted GUIDs.</summary>
        internal static IReadOnlyCollection<string> BlacklistGuids => _blacklist;

        /// <summary>Live view of the server-only GUIDs.</summary>
        internal static IReadOnlyCollection<string> ServerOnlyGuids => _serverOnly;

        /// <summary>
        /// Reload all lists from disk. Called on startup and on file change.
        /// </summary>
        public static void ReloadAll()
        {
            LoadHashSet(VAngardeConfig.ServerOnlyPath, _serverOnly, "server-only");
            LoadHashSet(VAngardeConfig.AdminOnlyPath, _adminOnly, "admin-only");
            LoadHashSet(VAngardeConfig.WhitelistPath, _whitelist, "whitelist");
            LoadHashSet(VAngardeConfig.BlacklistPath, _blacklist, "blacklist");
            LoadExactMatch(VAngardeConfig.ExactMatchPath);
            // Both lists are loaded now - drop any whitelist entry the required set already owns.
            PruneWhitelistOfRequiredMods();
            LoadHashSet(VAngardeConfig.ExemptPlayersPath, _exemptPlayers, "exempt_players");
            // The reload above wiped the in-memory admin auto-exemptions. Restore them (when AdminBypass is
            // ON) so an admin who reconnects after ANY hot-reload - e.g. another admin extending the whitelist
            // rewrites mod_whitelist.txt, which trips the file watcher -> ReloadAll - is still exempt and isn't
            // scanned/kicked for flying. Without this, the first config reload after boot silently un-exempts
            // every admin.
            if (VAngardeConfig.AdminBypass?.Value ?? true)
                MergeAdminExemptions();
            LoadAdminCharacterExceptions(VAngardeConfig.AdminCharacterExceptionsPath);
            VAngardeListNotifier.CheckAndNotify();
        }

        /// <summary>
        /// True when <paramref name="characterName"/> is a listed NON-admin character for the admin
        /// <paramref name="platformId"/> - i.e. this specific profile should be demoted to non-admin.
        /// </summary>
        public static bool IsCharacterDemoted(string platformId, string characterName)
        {
            if (string.IsNullOrEmpty(platformId) || string.IsNullOrEmpty(characterName)) return false;
            return _adminCharExceptions.TryGetValue(NormalizeId(platformId), out var names)
                   && names.Contains(characterName.Trim());
        }

        /// <summary>Whether any character exceptions are configured (cheap gate so the reconciler can no-op).</summary>
        public static bool HasAdminCharacterExceptions => _adminCharExceptions.Count > 0;

        // steamID/Name1,Name2  (names are the NON-admin profiles). First '/' splits id from names; names
        // split on ',' so multi-word character names ("Hobo The Troll") survive. Multiple lines per id and
        // comma lists both accumulate. IDs normalized so "Steam_7656..." and bare "7656..." match the socket.
        private static void LoadAdminCharacterExceptions(string path)
        {
            _adminCharExceptions.Clear();
            if (!File.Exists(path)) { LogVerbose($"Admin character exceptions file not found: {path}"); return; }
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int sep = line.IndexOf('/');
                    if (sep <= 0) continue;

                    string id = NormalizeId(line.Substring(0, sep));
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!_adminCharExceptions.TryGetValue(id, out var names))
                        _adminCharExceptions[id] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var part in line.Substring(sep + 1).Split(','))
                    {
                        var name = part.Trim();
                        if (name.Length > 0) names.Add(name);
                    }
                }
                int total = 0; foreach (var kv in _adminCharExceptions) total += kv.Value.Count;
                LogVerbose($"Loaded {total} admin character exception(s) across {_adminCharExceptions.Count} admin(s)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to load admin_character_exceptions.txt: {ex.Message}");
            }
        }

        /// <summary>Reload only the whitelist from disk.</summary>
        public static int ReloadWhitelist()
        {
            LoadHashSet(VAngardeConfig.WhitelistPath, _whitelist, "whitelist");
            return _whitelist.Count;
        }

        /// <summary>
        /// Merge an admin's mods into the whitelist and persist. Any GUID the admin is running that isn't
        /// already allowed is added, then mod_whitelist.txt is rewritten from the in-memory set. This is how
        /// the allowed-mods list grows - an admin approves a client mod for everyone by connecting with it.
        /// Returns the number of newly-added GUIDs.
        /// </summary>
        public static int ExtendWhitelistFromAdmin(string adminPlatformId, Dictionary<string, string> mods)
        {
            if (mods == null || mods.Count == 0) return 0;

            // RACE GUARD. LoadExactMatch() does _exactMatch.Clear() and THEN reads the file, and ReloadAll
            // runs on the file-watcher's threadpool thread - so there is a window where _exactMatch is empty
            // while a connect is being processed on the main thread. In that window the "already required"
            // skip below sees nothing and EVERY server mod the admin runs gets written into the whitelist.
            // It self-feeds: persisting the whitelist trips the watcher, which opens the window again.
            // If the required set is armed but momentarily empty we cannot tell a server mod from a client
            // extra, so add nothing this pass - the admin's next connect re-runs it against a loaded list.
            bool exactMatchArmed = VAngardeConfig.UseExactMatch == null || VAngardeConfig.UseExactMatch.Value;
            if (exactMatchArmed && _exactMatch.Count == 0)
            {
                LogVerbose($"Skipped whitelist extension for {adminPlatformId}: exact-match list is mid-reload (empty).");
                return 0;
            }

            var added = new List<string>();
            int keptAdminOnly = 0;
            foreach (var guid in mods.Keys)
            {
                if (string.IsNullOrEmpty(guid)) continue;
                // Only membership is auto-approved, and never for a banned mod. Versions are NOT stored in
                // the whitelist (it's GUID-only) - the exact-match list remains the sole version authority,
                // so an admin connecting with a wrong version can never bless that version here.
                if (_blacklist.Contains(guid)) continue;
                // Admin-only mods NEVER leak into the everyone-whitelist - that list is fully manual and an
                // admin connecting with one must not open it up for regular players.
                if (_adminOnly.Contains(guid)) { keptAdminOnly++; continue; }
                // Already allowed via the required set or server-only - a whitelist entry would be redundant.
                if (_exactMatch.ContainsKey(guid) || _serverOnly.Contains(guid)) continue;
                if (_whitelist.Add(guid))
                    added.Add(guid);
            }
            if (keptAdminOnly > 0)
                LogVerbose($"Admin {adminPlatformId} connected with {keptAdminOnly} admin-only mod(s) - kept OUT of the whitelist.");
            if (added.Count == 0) return 0;

            try
            {
                var lines = new List<string>
                {
                    "# VAngarde Mod Whitelist",
                    "# Allowed mod GUIDs. Grown automatically when an admin connects with a new mod",
                    "# (AdminModsExtendWhitelist). One GUID per line; lines starting with # are comments.",
                    ""
                };
                lines.AddRange(_whitelist.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
                File.WriteAllLines(VAngardeConfig.WhitelistPath, lines.ToArray());
                Debug.Log($"[VAngarde] Whitelist extended by admin {adminPlatformId}: +{added.Count} mod(s) [{string.Join(", ", added)}] -> {_whitelist.Count} allowed.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to persist whitelist extension: {ex.Message}");
            }
            VAngardeListNotifier.CheckAndNotify($"admin connected ({adminPlatformId})");
            return added.Count;
        }

        /// <summary>
        /// Drop whitelist entries that the exact-match set ALREADY requires. The whitelist only governs EXTRA
        /// client mods - anything the server runs belongs to the version authority, so a duplicate entry is
        /// noise at best and at worst reads like that mod is version-exempt. This prunes what already leaked
        /// in (see the race guard in <see cref="ExtendWhitelistFromAdmin"/>) and keeps the file converging.
        /// Server-only GUIDs are deliberately NOT pruned: serveronly + whitelist is the meaningful
        /// "optional client-side" combination. Persists only when something actually changed, so it settles
        /// after a single pass instead of re-tripping the file watcher in a loop.
        /// </summary>
        private static void PruneWhitelistOfRequiredMods()
        {
            if (_exactMatch.Count == 0 || _whitelist.Count == 0) return;

            var redundant = _whitelist.Where(g => _exactMatch.ContainsKey(g)).ToList();
            if (redundant.Count == 0) return;

            foreach (var guid in redundant) _whitelist.Remove(guid);

            try
            {
                var lines = new List<string>
                {
                    "# VAngarde Mod Whitelist",
                    "# Allowed mod GUIDs. Grown automatically when an admin connects with a new mod",
                    "# (AdminModsExtendWhitelist). One GUID per line; lines starting with # are comments.",
                    "# Mods the server itself runs are NOT listed here - those live in mod_exactmatch.txt,",
                    "# which is the sole version authority. Duplicates are pruned automatically on load.",
                    ""
                };
                lines.AddRange(_whitelist.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
                File.WriteAllLines(VAngardeConfig.WhitelistPath, lines.ToArray());
                Debug.Log($"[VAngarde] Whitelist pruned: removed {redundant.Count} entry(s) already required by the exact-match list -> {_whitelist.Count} allowed extras.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to persist whitelist prune: {ex.Message}");
            }
        }

        /// <summary>Reload only the blacklist from disk.</summary>
        public static int ReloadBlacklist()
        {
            LoadHashSet(VAngardeConfig.BlacklistPath, _blacklist, "blacklist");
            return _blacklist.Count;
        }

        /// <summary>Reload only the exempt players list from disk.</summary>
        public static int ReloadExemptPlayers()
        {
            LoadHashSet(VAngardeConfig.ExemptPlayersPath, _exemptPlayers, "exempt_players");
            return _exemptPlayers.Count;
        }

        // -- Admin Exemption Management ------------------------------

        // Admin IDs currently merged into the exempt set (tracked so we can remove them)
        private static readonly HashSet<string> _adminExemptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Read Valheim's admin list and merge those SteamIDs into the exempt set.
        /// Called when AdminBypass is ON. Safe to call multiple times - only adds new ones.
        /// </summary>
        public static void MergeAdminExemptions()
        {
            var adminIds = GetAdminIds();
            if (adminIds == null || adminIds.Count == 0) return;

            int added = 0;
            foreach (var id in adminIds)
            {
                // ALWAYS keep the admin in the live exempt set. _exemptPlayers is CLEARED and reloaded from
                // disk by ReloadAll() (admins live in memory, never in exempt_players.txt), so gating this add
                // on the _adminExemptions "already added" guard leaves a post-reload admin un-exempt -> scanned
                // -> KICKED for a normal admin action like flying. _adminExemptions only records which ids WE
                // auto-added, so RemoveAdminExemptions can pull exactly those; it must not gate the exempt add.
                bool newlyTracked = _adminExemptions.Add(id);
                _exemptPlayers.Add(id);
                if (newlyTracked) added++;
            }

            if (added > 0)
                Debug.Log($"[VAngarde] Auto-exempted {added} admin(s) from anti-cheat (AdminBypass=ON)");
        }

        /// <summary>
        /// Remove previously auto-added admin exemptions from the exempt set.
        /// Called when AdminBypass is turned OFF so admins become scannable.
        /// Does NOT remove admins that were manually added to exempt_players.txt.
        /// </summary>
        public static void RemoveAdminExemptions()
        {
            if (_adminExemptions.Count == 0) return;

            int removed = 0;
            foreach (var id in _adminExemptions)
            {
                if (_exemptPlayers.Remove(id))
                    removed++;
            }
            _adminExemptions.Clear();

            if (removed > 0)
                Debug.Log($"[VAngarde] Removed {removed} admin exemption(s) (AdminBypass=OFF)");
        }

        /// <summary>
        /// Add a single player to the in-memory exempt set (does not persist to file).
        /// </summary>
        public static void AddExemptPlayer(string platformId)
        {
            if (!string.IsNullOrEmpty(platformId))
                _exemptPlayers.Add(platformId);
        }

        /// <summary>
        /// Remove a single player from the in-memory exempt set.
        /// </summary>
        public static void RemoveExemptPlayer(string platformId)
        {
            if (!string.IsNullOrEmpty(platformId))
                _exemptPlayers.Remove(platformId);
        }

        /// <summary>
        /// Read Valheim's adminlist.txt via ZNet.m_adminList (SyncedList).
        /// Returns the list of SteamID strings, or empty list on failure.
        /// </summary>
        public static List<string> GetAdminIds()
        {
            try
            {
                if (ZNet.instance == null) return new List<string>();
                var adminList = (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
                if (adminList == null) return new List<string>();
                return adminList.GetList();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to read admin list: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Check if a given platformId is in Valheim's admin list.
        /// </summary>
        public static bool IsAdmin(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return false;
            return ContainsId(GetAdminIds(), platformId);
        }

        /// <summary>
        /// Character-aware admin check: a real admin is NOT admin while playing one of their listed
        /// non-admin characters (see admin_character_exceptions.txt). Independent of the m_adminList
        /// reconcile, so VAngarde's own logic is correct even before the reconcile runs for a fresh peer.
        /// </summary>
        public static bool IsAdmin(string platformId, string characterName)
        {
            return IsAdmin(platformId) && !IsCharacterDemoted(platformId, characterName);
        }

        // adminlist.txt / exempt entries may be stored bare ("7656...") or platform-prefixed
        // ("Steam_7656..."), but ZSteamSocket.GetHostName() hands us the bare form. Mirror Valheim's
        // ZNet.ListContainsId so either stored form resolves the same host - otherwise a real admin
        // whose adminlist entry is "Steam_..." is misread as a non-admin and gets scanned/poked/kicked.
        private static string NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            int us = id.IndexOf('_');
            return (us >= 0 ? id.Substring(us + 1) : id).Trim();
        }

        private static bool ContainsId(IEnumerable<string> ids, string lookup)
        {
            if (ids == null || string.IsNullOrEmpty(lookup)) return false;
            string norm = NormalizeId(lookup);
            foreach (var id in ids)
                if (NormalizeId(id).Equals(norm, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Auto-populate the exact match list file from the server's currently loaded BepInEx plugins.
        /// Called once on first server run when the file does not exist yet.
        /// </summary>
        public static void AutoPopulateExactMatch(string path)
        {
            try
            {
                var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
                var lines = new List<string>
                {
                    "# VAngarde Exact Match List - the mods a client MUST have, at these exact versions.",
                    "# AUTO-GENERATED from the server's loaded plugins each launch (AutoUpdateModList=ON).",
                    "# GUIDs in mod_serveronly.txt are EXCLUDED so clients aren't required to run server-side mods.",
                    "# Do not hand-edit while AutoUpdateModList is ON - it is rewritten on every boot.",
                    ""
                };

                int excluded = 0;
                if (plugins != null && plugins.Count > 0)
                {
                    foreach (var kvp in plugins.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (kvp.Value?.Metadata == null) continue;
                        if (_serverOnly.Contains(kvp.Value.Metadata.GUID)) { excluded++; continue; }
                        // Admin-only mods can never be REQUIRED - regular players aren't allowed to have them.
                        if (_adminOnly.Contains(kvp.Value.Metadata.GUID)) { excluded++; continue; }
                        lines.Add($"{kvp.Value.Metadata.GUID}={kvp.Value.Metadata.Version}");
                    }
                    if (VAngardeConfig.VerboseLogging != null && VAngardeConfig.VerboseLogging.Value)
                        Debug.Log($"[VAngarde] Auto-populated exact match list ({plugins.Count} plugins, {excluded} server-only excluded)");
                }
                else
                {
                    lines.Add("# No plugins were loaded when this file was generated.");
                    lines.Add("# Delete this file and restart the server to regenerate.");
                }

                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to auto-populate exact match list: {ex.Message}");
            }
        }

        /// <summary>
        /// Create mod_serveronly.txt with instructions if it does not exist. NEVER overwrites - this list
        /// is hand-curated and must survive relaunches (unlike the auto-generated exact-match list).
        /// </summary>
        public static void EnsureServerOnlyTemplate(string path)
        {
            if (File.Exists(path)) return;
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "# VAngarde Server-Only Mods",
                    "# GUIDs here are EXCLUDED from the auto-generated exact-match list, so clients are NOT",
                    "# required to have them (server-side mods clients don't run: Discord, dev commands, etc).",
                    "# A client that DOES also have one is still allowed (counts as allowed, not an unlisted extra).",
                    "# One BepInEx GUID per line. Hand-curated - this file is never auto-generated.",
                    "# Example:",
                    "#   com.Fire.FiresDiscordIntegration",
                    "#   server_devcommands",
                    ""
                });
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] Failed to create mod_serveronly.txt: {ex.Message}"); }
        }

        /// <summary>
        /// Create mod_adminonly.txt with instructions if it does not exist. FULLY MANUAL - never
        /// auto-written by anything (not admin connects, not push, not regen). NEVER overwrites.
        /// </summary>
        public static void EnsureAdminOnlyTemplate(string path)
        {
            if (File.Exists(path)) return;
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "# VAngarde Admin-Only Mods - client mods that ONLY admins (adminlist.txt) may run.",
                    "# OVERRIDES the whitelist: a GUID here is rejected for regular players even if it is also",
                    "# in mod_whitelist.txt, and an admin connecting with one NEVER auto-adds it to the whitelist.",
                    "# Excluded from the auto-generated exact-match list. One BepInEx GUID per line.",
                    "# FULLY MANUAL - nothing ever writes to this file.",
                    "# Example (admin tooling regular players must not run):",
                    "#   Azumatt.XRayVision",
                    "#   com.Fire.FiresDebugginTools",
                    ""
                });
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] Failed to create mod_adminonly.txt: {ex.Message}"); }
        }

        /// <summary>
        /// Create admin_character_exceptions.txt with instructions if it does not exist. Fully manual.
        /// </summary>
        public static void EnsureAdminCharacterExceptionsTemplate(string path)
        {
            if (File.Exists(path)) return;
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "# VAngarde Admin Character Exceptions",
                    "# Lets a server admin keep a NON-admin survival character alongside their admin ones.",
                    "# Format:  <SteamID>/<CharacterName>[,<CharacterName2>,...]",
                    "# The listed character names are the profiles that are NOT admin. The Steam ID must still",
                    "# be a real admin (in adminlist.txt); ALL of their OTHER characters keep admin. When that",
                    "# admin connects on a listed character they play as a normal, non-admin player (no",
                    "# devcommands, no admin bypass, config UI locked) for that session - switching characters",
                    "# is just a reconnect. Multiple names per line (comma-separated) and multiple lines per",
                    "# Steam ID both accumulate. Character names are matched exactly (case-insensitive).",
                    "# Only affects Steam IDs already on the admin list - it can never GRANT admin to anyone.",
                    "# Example:",
                    "#   76561198069040852/Hobo The Troll,Survivor Steve",
                    ""
                });
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] Failed to create admin_character_exceptions.txt: {ex.Message}"); }
        }

        /// <summary>
        /// Create mod_whitelist.txt with instructions if it does not exist. The whitelist is the set of
        /// allowed EXTRA client mods (beyond the required exact-match set); it is hand-curated (or set via
        /// 'vangarde push mods'), NOT auto-generated from the server. NEVER overwrites.
        /// </summary>
        public static void EnsureWhitelistTemplate(string path)
        {
            if (File.Exists(path)) return;
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "# VAngarde Whitelist - EXTRA client mods allowed beyond the required (exact-match) set.",
                    "# When UseWhitelist is ON, any client mod that is NOT in mod_exactmatch.txt, NOT here, and",
                    "# NOT in mod_serveronly.txt is rejected as an unlisted / injected mod.",
                    "# One BepInEx GUID per line. Hand-curated, or run 'vangarde push mods' from a reference client.",
                    "# Example (client-only UI/QoL mods):",
                    "#   Azumatt.XRayVision",
                    "#   shudnal.ConfigurationManager",
                    ""
                });
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] Failed to create mod_whitelist.txt: {ex.Message}"); }
        }

        /// <summary>
        /// Auto-populate the whitelist file from the currently loaded BepInEx plugins.
        /// Called once on first run when the file does not exist yet.
        /// Both server and client generate their own copy so admins have matching lists.
        /// </summary>
        public static void AutoPopulateWhitelist(string path)
        {
            try
            {
                var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
                var lines = new List<string>
                {
                    "# VAngarde Mod Whitelist",
                    "# Auto-populated from loaded mods on first run.",
                    "# One BepInEx plugin GUID per line. Only these mods are allowed when whitelist mode is ON.",
                    "# Lines starting with # are comments.",
                    "# Edit this file to add/remove mods. Delete it to regenerate on next start.",
                    ""
                };

                if (plugins != null && plugins.Count > 0)
                {
                    foreach (var kvp in plugins.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (kvp.Value?.Metadata != null)
                            lines.Add(kvp.Value.Metadata.GUID);
                    }
                    if (VAngardeConfig.VerboseLogging != null && VAngardeConfig.VerboseLogging.Value)
                        Debug.Log($"[VAngarde] Auto-populated whitelist with {plugins.Count} mods");
                }
                else
                {
                    lines.Add("# No plugins were loaded when this file was generated.");
                    lines.Add("# Delete this file and restart to regenerate.");
                    if (VAngardeConfig.VerboseLogging != null && VAngardeConfig.VerboseLogging.Value)
                        Debug.Log("[VAngarde] Auto-populated whitelist (no plugins loaded yet)");
                }

                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to auto-populate whitelist: {ex.Message}");
            }
        }

        /// <summary>
        /// Cache an admin's mod list to a file in the AdminModLists directory.
        /// Called when an admin connects and sends their mod list via challenge response,
        /// or when we capture it from the peer validation flow.
        /// </summary>
        public static void CacheAdminModList(string platformId, Dictionary<string, string> mods)
        {
            if (string.IsNullOrEmpty(platformId) || mods == null) return;

            try
            {
                string dir = VAngardeConfig.AdminModListsDir;

                // Sanitize platform ID for use as filename
                string safeId = platformId.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
                string filePath = Path.Combine(dir, $"{safeId}.txt");

                var lines = new List<string>
                {
                    $"# Admin mod list for {platformId}",
                    $"# Cached at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
                    $"# Format: GUID=Version",
                    ""
                };

                foreach (var kvp in mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"{kvp.Key}={kvp.Value}");
                }

                // Written on the mod's disk thread: it sat in the challenge-response RPC, inside the server's frame.
                string[] text = lines.ToArray();
                int count = mods.Count;
                VerdantsAscent.Modules.Characters.CharacterDiskWriter.Enqueue(() => WriteAdminModList(dir, filePath, text, platformId, count));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to cache admin mod list for {platformId}: {ex.Message}");
            }
        }

        private static void WriteAdminModList(string dir, string filePath, string[] lines, string platformId, int count)
        {
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllLines(filePath, lines);
                LogVerbose($"Cached admin mod list for {platformId} ({count} mods) to {filePath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to cache admin mod list for {platformId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Save a client's BepInEx LogOutput.log to disk under ClientLogs/{playerName}_{safeId}/LogOutput.log.
        /// Overwrites on every challenge response so the file always holds the latest log.
        /// Called server-side whenever a client sends their log in the challenge response.
        /// </summary>
        public static void CacheClientLog(string platformId, string playerName, byte[] logBytes)
        {
            if (string.IsNullOrEmpty(platformId) || logBytes == null || logBytes.Length == 0) return;

            string filePath;
            try
            {
                string safeId   = platformId.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
                string safeName = string.IsNullOrEmpty(playerName)
                    ? "unknown"
                    : string.Concat(playerName.Split(Path.GetInvalidFileNameChars()));

                string dir = Path.Combine(VAngardeConfig.ClientLogsDir, $"{safeName}_{safeId}");
                filePath = Path.Combine(dir, "LogOutput.log");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to cache client log for {platformId}: {ex.Message}");
                return;
            }

            // The write (a few hundred KB, every periodic refresh) runs on the mod's disk thread: on the server's main thread
            // it sat inside the challenge-response RPC handler, in the frame every relayed PvP hit waits on.
            VerdantsAscent.Modules.Characters.CharacterDiskWriter.Enqueue(() => WriteClientLog(filePath, logBytes, platformId));
        }

        // Written beside the file and swapped in, so an admin reading the cached log never gets half of one.
        private static void WriteClientLog(string filePath, byte[] logBytes, string platformId)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string temp = filePath + ClientLogTempSuffix;
                File.WriteAllBytes(temp, logBytes);
                if (File.Exists(filePath)) File.Replace(temp, filePath, null);
                else File.Move(temp, filePath);
                LogVerbose($"Cached client log for {platformId} ({logBytes.Length} bytes) to {filePath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to cache client log for {platformId}: {ex.Message}");
            }
        }

        private const string ClientLogTempSuffix = ".writing";

        /// <summary>
        /// Returns the path to a cached client log file, or null if none exists.
        /// Searches the ClientLogs directory for a folder matching the given platformId.
        /// </summary>
        public static string GetCachedClientLogPath(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return null;

            try
            {
                string safeId = platformId.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
                string dir = VAngardeConfig.ClientLogsDir;
                if (!Directory.Exists(dir)) return null;

                // Folder name ends with _{safeId} - match regardless of player name prefix
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (Path.GetFileName(sub).EndsWith("_" + safeId, StringComparison.OrdinalIgnoreCase))
                    {
                        string logFile = Path.Combine(sub, "LogOutput.log");
                        if (File.Exists(logFile))
                            return logFile;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to locate cached client log for {platformId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Read a cached client log from disk and return its bytes, or null if not found.
        /// </summary>
        public static byte[] ReadCachedClientLog(string platformId)
        {
            string path = GetCachedClientLogPath(platformId);
            if (path == null) return null;

            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to read cached client log for {platformId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Overwrite the server's whitelist and exact match files from an admin's pushed mod list.
        /// Called when an admin uses the push command to set their mods as the server standard.
        /// </summary>
        public static void OverwriteListsFromAdmin(string adminPlatformId, Dictionary<string, string> mods)
        {
            if (mods == null || mods.Count == 0) return;

            try
            {
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                // Overwrite whitelist (GUIDs only)
                var whitelistLines = new List<string>
                {
                    "# VAngarde Mod Whitelist",
                    $"# Pushed by admin {adminPlatformId} at {timestamp} UTC",
                    "# One BepInEx plugin GUID per line.",
                    ""
                };
                foreach (var kvp in mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    whitelistLines.Add(kvp.Key);
                }
                File.WriteAllLines(VAngardeConfig.WhitelistPath, whitelistLines.ToArray());
                Debug.Log($"[VAngarde] Whitelist overwritten by admin {adminPlatformId} ({mods.Count} entries)");

                // Overwrite exact match (GUID=Version)
                var exactLines = new List<string>
                {
                    "# VAngarde Exact Match List",
                    $"# Pushed by admin {adminPlatformId} at {timestamp} UTC",
                    "# Format: GUID=Version (one per line).",
                    ""
                };
                foreach (var kvp in mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    exactLines.Add($"{kvp.Key}={kvp.Value}");
                }
                File.WriteAllLines(VAngardeConfig.ExactMatchPath, exactLines.ToArray());
                Debug.Log($"[VAngarde] Exact match list overwritten by admin {adminPlatformId} ({mods.Count} entries)");

                // Also cache the admin's mod list
                CacheAdminModList(adminPlatformId, mods);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to overwrite lists from admin push: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the server's currently loaded BepInEx plugins as a GUID->Version dictionary.
        /// Used for cross-referencing against a client's mod list in Discord reports.
        /// </summary>
        public static Dictionary<string, string> GetServerModList()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (plugins != null)
                {
                    foreach (var kvp in plugins)
                    {
                        if (kvp.Value?.Metadata != null)
                            result[kvp.Value.Metadata.GUID] = kvp.Value.Metadata.Version.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to enumerate server mods: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Check if a player (by platform ID string) is exempt from AC checks.
        /// </summary>
        public static bool IsPlayerExempt(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return false;
            return ContainsId(_exemptPlayers, platformId);
        }

        /// <summary>
        /// Validate a client's mod list against configured rules.
        /// Returns null if valid, or a reason string if violation detected.
        /// </summary>
        public static string ValidateModList(Dictionary<string, string> clientMods, bool isAdmin = false)
        {
            if (clientMods == null)
                return "Client sent null mod list";

            // -- Blacklist - client must NOT have any of these --
            if (VAngardeConfig.UseBlacklist != null && VAngardeConfig.UseBlacklist.Value)
            {
                foreach (var mod in clientMods)
                {
                    if (_blacklist.Contains(mod.Key))
                        return $"Blacklisted mod detected: {mod.Key}";
                }
            }

            // -- Exact match - client MUST have every required mod, at the exact version --
            // The required set (auto-generated from server plugins minus mod_serveronly.txt). Extra client
            // mods are handled by the whitelist check below, not here.
            if (VAngardeConfig.UseExactMatch != null && VAngardeConfig.UseExactMatch.Value && _exactMatch.Count > 0)
            {
                foreach (var required in _exactMatch)
                {
                    if (!clientMods.TryGetValue(required.Key, out string clientVersion))
                        return $"Required mod missing: {required.Key}";
                    if (!string.Equals(clientVersion, required.Value, StringComparison.OrdinalIgnoreCase))
                        return $"Version mismatch for {required.Key}: expected {required.Value}, got {clientVersion}";
                }
            }

            // -- Whitelist - every EXTRA client mod must be explicitly allowed --
            // A client mod passes if it is required (exact-match), an admin-only mod on an ADMIN client, an
            // allowed client extra (whitelist), or a server-only mod the client also happens to have
            // (server-only bypass; a GUID in BOTH server-only and whitelist = "optional client-side", same
            // outcome). Admin-only is checked BEFORE the whitelist because it OVERRIDES it - a non-admin
            // with an admin-only mod is rejected even if the GUID was also hand-added to the whitelist.
            // Anything else is an unlisted mod = a potential injected cheat, and is rejected.
            if (VAngardeConfig.UseWhitelist != null && VAngardeConfig.UseWhitelist.Value)
            {
                foreach (var mod in clientMods)
                {
                    if (_exactMatch.ContainsKey(mod.Key)) continue;
                    if (_adminOnly.Contains(mod.Key))
                    {
                        if (isAdmin) continue;
                        return $"Admin-only mod detected: {mod.Key}";
                    }
                    if (_whitelist.Contains(mod.Key)) continue;
                    if (_serverOnly.Contains(mod.Key)) continue;
                    return $"Mod not allowed (add to mod_whitelist.txt if legit): {mod.Key}";
                }
            }

            return null; // valid
        }

        /// <summary>
        /// Every way the client's mods differ from what the server requires/allows, each as an actionable
        /// line with versions, so the kick popup is a one-stop "here's what to fix" list. Empty = valid.
        /// Order: REMOVE (banned / not-allowed) first, then UPDATE (wrong version), then INSTALL (missing).
        /// </summary>
        public static List<string> ValidateModListDetailed(Dictionary<string, string> clientMods, bool isAdmin = false)
        {
            var problems = new List<string>();
            if (clientMods == null) { problems.Add("Client sent no mod list."); return problems; }

            if (VAngardeConfig.UseBlacklist != null && VAngardeConfig.UseBlacklist.Value)
                foreach (var mod in clientMods.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                    if (_blacklist.Contains(mod.Key))
                        problems.Add($"REMOVE (banned): {mod.Key} ({mod.Value})");

            // Must MATCH ValidateModList's rule exactly (they'd disagree otherwise): an extra client mod is
            // rejected unless it's required (exact-match), admin-only ON AN ADMIN, an allowed extra
            // (whitelist), or a server-only mod. Admin-only overrides the whitelist. NO Count>0 gate - an
            // empty whitelist still enforces, so a client mod in none of the lists is flagged as
            // unlisted/injected.
            if (VAngardeConfig.UseWhitelist != null && VAngardeConfig.UseWhitelist.Value)
                foreach (var mod in clientMods.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (_exactMatch.ContainsKey(mod.Key)) continue;
                    if (_adminOnly.Contains(mod.Key))
                    {
                        if (!isAdmin) problems.Add($"REMOVE (admin-only): {mod.Key} ({mod.Value})");
                        continue;
                    }
                    if (_whitelist.Contains(mod.Key)) continue;
                    if (_serverOnly.Contains(mod.Key)) continue;
                    problems.Add($"REMOVE (not allowed): {mod.Key} ({mod.Value})");
                }

            if (VAngardeConfig.UseExactMatch != null && VAngardeConfig.UseExactMatch.Value && _exactMatch.Count > 0)
                foreach (var required in _exactMatch.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!clientMods.TryGetValue(required.Key, out string clientVersion))
                        problems.Add($"INSTALL: {required.Key} (v{required.Value})");
                    else if (!string.Equals(clientVersion, required.Value, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"UPDATE: {required.Key}  {clientVersion} -> {required.Value}");
                }

            return problems;
        }

        /// <summary>
        /// Full client-vs-server comparison for the kick panel's split table. Each line is a tab-delimited
        /// record "&lt;status&gt;&lt;guid&gt;&lt;clientVersion&gt;&lt;serverVersion&gt;"
        /// (the client-side FiresConnectReasonPanel parses these into a table). Covers every required mod
        /// (OK / UPDATE / MISSING) and every client mod not in the required set (ALLOWED / SERVERONLY /
        /// BANNED / NOTALLOWED). Problems are sorted to the top.
        /// </summary>
        public static List<string> BuildModComparison(Dictionary<string, string> clientMods, bool isAdmin = false)
        {
            const char S = '\t';
            if (clientMods == null) clientMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var recs = new List<(string status, string guid, string cv, string sv)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The panel must only tag a row as a problem the server will ACTUALLY kick for. This method read
            // the four lists directly and ignored the enable toggles, so with UseWhitelist=false - where
            // ValidateModList permits every extra client mod unconditionally - each unlisted mod still came
            // out NOTALLOWED, and Rank() sorts NOTALLOWED to the TOP. A player then sees red "not allowed"
            // rows above the single real violation and fixes the wrong thing. Gate on the same three toggles
            // the other two validators use.
            bool blacklistOn = VAngardeConfig.UseBlacklist != null && VAngardeConfig.UseBlacklist.Value;
            bool whitelistOn = VAngardeConfig.UseWhitelist != null && VAngardeConfig.UseWhitelist.Value;
            bool exactOn = VAngardeConfig.UseExactMatch != null && VAngardeConfig.UseExactMatch.Value && _exactMatch.Count > 0;

            foreach (var req in _exactMatch)
            {
                // seen is populated UNCONDITIONALLY even when the version authority is off, because both
                // validators exempt a required GUID from the whitelist check without consulting
                // UseExactMatch. Skipping this when exactOn is false would push required mods through the
                // extras branch and invent a fresh drift.
                seen.Add(req.Key);
                bool has = clientMods.TryGetValue(req.Key, out string cv);
                if (!exactOn)
                {
                    if (has) recs.Add(("OK", req.Key, cv, req.Value));
                    continue;
                }
                string status = !has ? "MISSING"
                    : (string.Equals(cv, req.Value, StringComparison.OrdinalIgnoreCase) ? "OK" : "UPDATE");
                recs.Add((status, req.Key, has ? cv : "", req.Value));
            }
            foreach (var mod in clientMods)
            {
                if (seen.Contains(mod.Key)) continue;
                // Admin-only outranks the whitelist (same precedence as the validators): a non-admin with
                // one gets the red ADMINONLY tag; an admin sees it as an allowed admin perk. Both validators
                // check admin-only INSIDE the UseWhitelist gate, so it is not enforced on its own either.
                string status;
                if (blacklistOn && _blacklist.Contains(mod.Key)) status = "BANNED";
                else if (!whitelistOn) status = "ALLOWED";
                else if (_adminOnly.Contains(mod.Key)) status = isAdmin ? "ADMINOK" : "ADMINONLY";
                else if (_whitelist.Contains(mod.Key)) status = "ALLOWED";
                else if (_serverOnly.Contains(mod.Key)) status = "SERVERONLY";
                else status = "NOTALLOWED";
                recs.Add((status, mod.Key, mod.Value, ""));
            }

            int Rank(string s) => s == "MISSING" || s == "NOTALLOWED" || s == "BANNED" || s == "ADMINONLY" ? 0 : s == "UPDATE" ? 1 : 2;
            return recs
                .OrderBy(r => Rank(r.status))
                .ThenBy(r => r.guid, StringComparer.OrdinalIgnoreCase)
                .Select(r => $"{r.status}{S}{r.guid}{S}{r.cv}{S}{r.sv}")
                .ToList();
        }

        // -- File parsing helpers --

        private static void LoadHashSet(string path, HashSet<string> set, string listName)
        {
            set.Clear();
            if (!File.Exists(path))
            {
                LogVerbose($"List file not found: {path}");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;
                    set.Add(line);
                }
                LogVerbose($"Loaded {set.Count} entries from {listName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to load {listName}: {ex.Message}");
            }
        }

        private static void LoadExactMatch(string path)
        {
            _exactMatch.Clear();
            _exactMatchHashes.Clear();
            if (!File.Exists(path))
            {
                LogVerbose($"Exact match file not found: {path}");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;

                    var eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;

                    var guid = line.Substring(0, eqIdx).Trim();
                    var versionPlus = line.Substring(eqIdx + 1).Trim();
                    if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(versionPlus)) continue;

                    // Phase-5b: optional ";sha256-hex" suffix carries the expected DLL hash for this GUID.
                    var semi = versionPlus.IndexOf(';');
                    string version, hash = null;
                    if (semi >= 0)
                    {
                        version = versionPlus.Substring(0, semi).Trim();
                        hash = versionPlus.Substring(semi + 1).Trim();
                    }
                    else { version = versionPlus; }

                    if (string.IsNullOrEmpty(version)) continue;
                    _exactMatch[guid] = version;
                    if (!string.IsNullOrEmpty(hash)) _exactMatchHashes[guid] = hash;
                }
                LogVerbose($"Loaded {_exactMatch.Count} entries from exact match list ({_exactMatchHashes.Count} with DLL hash)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to load exact match list: {ex.Message}");
            }
        }

        private static void LogVerbose(string msg)
        {
            if (VAngardeConfig.VerboseLogging != null && VAngardeConfig.VerboseLogging.Value)
                Debug.Log($"[VAngarde] {msg}");
        }
    }
}
