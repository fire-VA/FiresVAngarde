using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

using FiresCore.Sync;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// BepInEx config entries for the VAngarde anti-cheat system.
    /// All entries are server-locked via ConfigSync.
    /// </summary>
    public static class VAngardeConfig
    {
        // ?????? Master Toggle ??????
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> VerboseLogging;

        // ?????? Mod Validation ??????
        public static ConfigEntry<bool> EnforceModList;
        public static ConfigEntry<bool> UseWhitelist;
        public static ConfigEntry<bool> UseBlacklist;
        public static ConfigEntry<bool> UseExactMatch;
        public static ConfigEntry<bool> AutoUpdateModList;
        public static ConfigEntry<bool> AdminModsExtendWhitelist;

        // ?????? Challenge / Response ??????
        public static ConfigEntry<float> ChallengeTimeoutSeconds;
        public static ConfigEntry<float> PeriodicRecheckMinutes;

        // ?????? Behavioral Detection ??????
        public static ConfigEntry<bool> DetectGodMode;
        public static ConfigEntry<bool> DetectFlight;
        public static ConfigEntry<bool> DetectSpeedHack;
        public static ConfigEntry<float> SpeedHackThreshold;
        public static ConfigEntry<bool> DetectCheatItems;
        public static ConfigEntry<string> BannedItems;

        // ?????? Enforcement ??????
        public static ConfigEntry<string> ViolationAction;
        public static ConfigEntry<bool> AdminBypass;
        public static ConfigEntry<bool> AdminMonitoring;
        public static ConfigEntry<bool> AdminCommandLogging;

        // ?????? Hardening v2 ??????
        public static ConfigEntry<bool> EnforceModListAudit;
        public static ConfigEntry<bool> EnforceSessionSignature;
        public static ConfigEntry<bool> EnforceIntegrityCheck;
        public static ConfigEntry<bool> EnforceDllHashMatch;

        // ?????? File Paths (computed, not config) ??????
        private static string _configDir;
        public static string ConfigDir
        {
            get
            {
                if (_configDir != null) return _configDir;
                _configDir = Path.Combine(Paths.ConfigPath, "VAngarde");
                try { Directory.CreateDirectory(_configDir); }
                catch (System.Exception ex) { UnityEngine.Debug.LogWarning($"[VAngarde] could not create config dir: {ex.Message}"); }
                return _configDir;
            }
        }
        public static string WhitelistPath => Path.Combine(ConfigDir, "mod_whitelist.txt");
        public static string BlacklistPath => Path.Combine(ConfigDir, "mod_blacklist.txt");
        public static string ExactMatchPath => Path.Combine(ConfigDir, "mod_exactmatch.txt");
        public static string ExemptPlayersPath => Path.Combine(ConfigDir, "exempt_players.txt");
        public static string ServerOnlyPath => Path.Combine(ConfigDir, "mod_serveronly.txt");
        public static string AdminOnlyPath => Path.Combine(ConfigDir, "mod_adminonly.txt");
        public static string AdminCharacterExceptionsPath => Path.Combine(ConfigDir, "admin_character_exceptions.txt");
        public static string AdminModListsDir => Path.Combine(ConfigDir, "AdminModLists");
        public static string ClientLogsDir => Path.Combine(ConfigDir, "ClientLogs");

        /// <summary>
        /// Bind all VAngarde config entries to the BepInEx config file.
        /// Called from ConfigManager.Initialize().
        /// </summary>
        public static void Initialize(ConfigFile config)
        {
            // ?? Master Toggle ??
            Enabled = config.Bind("VAngarde", "Enabled", true,
                "Enable VAngarde anti-cheat system. Only active on server/host. ON by default — " +
                "an installed anti-cheat that ships disabled is a no-op that only brands the server name.");
            VerboseLogging = config.Bind("VAngarde", "VerboseLogging", false,
                "Enable detailed VAngarde debug logging (server-side only).");

            // ?? Mod Validation ??
            EnforceModList = config.Bind("VAngarde.ModValidation", "EnforceModList", false,
                "Require clients to send their mod list for validation.");
            UseWhitelist = config.Bind("VAngarde.ModValidation", "UseWhitelist", false,
                "Only allow mods listed in mod_whitelist.txt.");
            UseBlacklist = config.Bind("VAngarde.ModValidation", "UseBlacklist", true,
                "Kick players with mods listed in mod_blacklist.txt.");
            UseExactMatch = config.Bind("VAngarde.ModValidation", "UseExactMatch", true,
                "Require every mod in mod_exactmatch.txt to be present on the client at the exact version " +
                "(extra client-only mods are allowed). ON by default; with AutoUpdateModList the list tracks " +
                "the server's own plugins automatically. Set false to disable mod-list enforcement.");
            AutoUpdateModList = config.Bind("VAngarde.ModValidation", "AutoUpdateModList", false,
                "When ON, the server OVERWRITES mod_whitelist.txt and mod_exactmatch.txt from its loaded " +
                "plugins on EVERY launch. Default OFF so the lists stay stable and grow only when an admin " +
                "connects with a new mod (see AdminModsExtendWhitelist). Turn ON once to bootstrap the lists " +
                "on a fresh server, then set it back OFF.");
            AdminModsExtendWhitelist = config.Bind("VAngarde.ModValidation", "AdminModsExtendWhitelist", true,
                "When an ADMIN (from adminlist.txt) connects, add any mod they're running that isn't already " +
                "whitelisted to mod_whitelist.txt - admins approve a client mod for everyone just by connecting " +
                "with it. Server-authoritative (admin status can't be client-spoofed). Pairs with UseWhitelist.");

            // ?? Challenge / Response ??
            ChallengeTimeoutSeconds = config.Bind("VAngarde.Timing", "ChallengeTimeoutSeconds", 90f,
                "Seconds to wait for a client's challenge response before disconnecting. The timer starts when " +
                "the challenge is sent (right after login, while the client is still loading the world), so on a " +
                "heavy modpack this must be generous or legit players get kicked mid-load. Raise it further if " +
                "your pack's login takes longer.");
            PeriodicRecheckMinutes = config.Bind("VAngarde.Timing", "PeriodicRecheckMinutes", 5f,
                "Minutes between periodic re-validation challenges. 0 = connect-only.");

            // ?? Behavioral Detection ??
            DetectGodMode = config.Bind("VAngarde.Detection", "DetectGodMode", true,
                "Log-only god-mode heuristic (near-death survival). Reads client-authored health, so it " +
                "NEVER auto-kicks - it reports a suspicion for admin review only.");
            DetectFlight = config.Bind("VAngarde.Detection", "DetectFlight", true,
                "Detect the debug-fly flag on the character (server-observed; catches the devcommands fly).");
            DetectSpeedHack = config.Bind("VAngarde.Detection", "DetectSpeedHack", true,
                "Detect impossible sustained horizontal movement from server-observed position deltas.");
            SpeedHackThreshold = config.Bind("VAngarde.Detection", "SpeedHackThreshold", 30.0f,
                "KICK ceiling in m/s (floored at 20). Sustained horizontal speed above this is kicked - set it " +
                "ABOVE your fastest legit source (sprint ~6-7, longship ~10-11, mounts/wind/speed-potions higher). " +
                "Speeds between 60% of this and the ceiling are LOGGED for review, never kicked. Knockback/force " +
                "pushes (3s grace after any hit), teleports, and vertical falls / feather-cape descent are ignored.");
            DetectCheatItems = config.Bind("VAngarde.Detection", "DetectCheatItems", true,
                "Kick non-admin players who equip banned/cheat items (e.g. SwordCheat, SledgeCheat).");
            BannedItems = config.Bind("VAngarde.Detection", "BannedItems", "SwordCheat,SledgeCheat",
                "Comma-separated list of prefab names that are banned. Players equipping these get kicked immediately.");

            // ?? Enforcement ??
            ViolationAction = config.Bind("VAngarde.Enforcement", "ViolationAction", "kick",
                "Action on violation: 'kick' = disconnect, 'log' = log only (no enforcement).");
            AdminBypass = config.Bind("VAngarde.Enforcement", "AdminBypass", true,
                "Exempt admins from all behavioral detection (god mode, flight, speed). " +
                "Set to false to test anti-cheat as an admin. Admins still get mod-list caching regardless.");

            AdminMonitoring = config.Bind("VAngarde.Enforcement", "AdminMonitoring", true,
                "When AdminBypass is ON, still SCAN admins and log detections to Discord (monitor mode) - " +
                "admins are never kicked (god mode is log-only for everyone now). Lets you watch " +
                "what the anti-cheat catches using admins as test subjects. No effect when AdminBypass is OFF.");

            AdminCommandLogging = config.Bind("VAngarde.Enforcement", "AdminCommandLogging", true,
                "Log to Discord (anti-cheat channel) when an admin toggles a cheat/debug command " +
                "(devcommands/debugmode/god/ghost/fly/nocost), and a snapshot at login if any are already active.");

            EnforceModListAudit = config.Bind("VAngarde.HardeningV2", "EnforceModListAudit", false,
                "TELEMETRY ONLY (not proof). Requires a three-source client modlist snapshot (Chainloader, " +
                "AppDomain assemblies, filesystem .dll scan) and flags DLLs loaded-but-not-declared. Catches a " +
                "LAZY injector who drops a cheat DLL without patching VAngarde; a client that patches its own " +
                "collector defeats it (all three sources are gathered by the client). Default OFF - enable only " +
                "as a casual-cheat speed bump, never as authentication. See Tools/VANGARDE_SECURITY_AUDIT.md.");

            EnforceSessionSignature = config.Bind("VAngarde.HardeningV2", "EnforceSessionSignature", false,
                "TELEMETRY ONLY (not proof). HMAC-SHA256 over (nonce|modlist|hash) keyed by a session UID. NOTE: " +
                "the key is a constant BAKED INTO THE CLIENT DLL, identical for every client, so a decompiler " +
                "extracts it and forges valid signatures over a faked modlist (audit rating 1/5). Stops only a " +
                "wire-level forger who never runs the mod. Default OFF. The real fix binds to Steam's session-auth " +
                "ticket instead of a shared secret.");

            EnforceIntegrityCheck = config.Bind("VAngarde.HardeningV2", "EnforceIntegrityCheck", false,
                "TELEMETRY ONLY (not proof). The client self-reports a Harmony tamper bit for VAngarde's own " +
                "methods - i.e. it asks the (possibly patched) process whether it was patched, so a one-line patch " +
                "of the reporter (or an IL body-replace) defeats it, and the 'trusted owner' is a public string a " +
                "cheat can claim. Default OFF; a casual speed bump at best.");

            EnforceDllHashMatch = config.Bind("VAngarde.HardeningV2", "EnforceDllHashMatch", false,
                "TELEMETRY ONLY (not proof), and INERT unless mod_exactmatch.txt lines carry a ';sha256-hex' " +
                "suffix (nothing writes one automatically). The client self-reports DLL hashes, so it can report a " +
                "pristine hash while running patched code. Default OFF.");

            // Ensure config directory and template files exist
            EnsureConfigFiles();
        }

        /// <summary>
        /// Registers all VAngarde config entries with ConfigSync for server-locking.
        /// </summary>
        public static void BindToSync(ConfigSync configSync)
        {
            if (configSync == null) return;

            configSync.AddConfigEntry(Enabled);
            configSync.AddConfigEntry(VerboseLogging);
            configSync.AddConfigEntry(EnforceModList);
            configSync.AddConfigEntry(UseWhitelist);
            configSync.AddConfigEntry(UseBlacklist);
            configSync.AddConfigEntry(UseExactMatch);
            configSync.AddConfigEntry(AutoUpdateModList);
            configSync.AddConfigEntry(AdminModsExtendWhitelist);
            configSync.AddConfigEntry(ChallengeTimeoutSeconds);
            configSync.AddConfigEntry(PeriodicRecheckMinutes);
            configSync.AddConfigEntry(EnforceModListAudit);
            configSync.AddConfigEntry(EnforceSessionSignature);
            configSync.AddConfigEntry(EnforceIntegrityCheck);
            configSync.AddConfigEntry(EnforceDllHashMatch);
            configSync.AddConfigEntry(DetectGodMode);
            configSync.AddConfigEntry(DetectFlight);
            configSync.AddConfigEntry(DetectSpeedHack);
            configSync.AddConfigEntry(SpeedHackThreshold);
            configSync.AddConfigEntry(DetectCheatItems);
            configSync.AddConfigEntry(BannedItems);
            configSync.AddConfigEntry(ViolationAction);
            configSync.AddConfigEntry(AdminBypass);
            configSync.AddConfigEntry(AdminMonitoring);
            configSync.AddConfigEntry(AdminCommandLogging);
        }

        /// <summary>
        /// Creates the VAngarde config directory and template list files if they don't exist.
        /// </summary>
        private static void EnsureConfigFiles()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                    Directory.CreateDirectory(ConfigDir);

                // NOTE: whitelist + exact-match auto-population MOVED to VAngardeCore.Initialize,
                // which runs on ZNet.Awake postfix — by then every BepInEx plugin has finished
                // loading and Chainloader.PluginInfos is the complete set. Doing it here (during
                // FiresVAngarde's own Awake) would only see plugins loaded BEFORE us, missing
                // ~half the modset on a typical server (Jotunn, JereKuusela mods, TimeoutLimit,
                // LocationPlacementAccelerator, etc. all load alphabetically after FiresVAngarde).

                CreateTemplateIfMissing(BlacklistPath,
                    "# VAngarde Mod Blacklist",
                    "# One BepInEx plugin GUID per line. These mods will get the player kicked.",
                    "# Lines starting with # are comments.",
                    "# Example:",
                    "# com.example.cheatmod");

                CreateTemplateIfMissing(ExemptPlayersPath,
                    "# VAngarde Exempt Players",
                    "# One SteamID per line. These players bypass all AC checks.",
                    "# Lines starting with # are comments.",
                    "# Example:",
                    "# Steam_76561198012345678");

                // Ensure admin mod lists directory exists
                if (!Directory.Exists(AdminModListsDir))
                    Directory.CreateDirectory(AdminModListsDir);

                // Ensure client logs directory exists
                if (!Directory.Exists(ClientLogsDir))
                    Directory.CreateDirectory(ClientLogsDir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to create config files: {ex.Message}");
            }
        }

        private static void CreateTemplateIfMissing(string path, params string[] lines)
        {
            if (File.Exists(path)) return;
            File.WriteAllLines(path, lines);
        }
    }
}
