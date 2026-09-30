using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAngarde's sections for the shared FiresCore help panel (registration only).
    // NOTE: never print ServerKey values or list-file contents in help text.
    internal static class VangardeHelpContent
    {
        private const string ModId = "FiresVAngarde";
        private static bool _registered;

        public static void Register()
        {
            if (_registered || Application.isBatchMode) return;
            _registered = true;

            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "The Server Guardian", 0, BuildOverview);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Mod Validation", 1, BuildModValidation, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Detection & Enforcement", 2, BuildDetection, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Character Enforcement", 3, BuildCharacters, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Commands", 4, BuildCommands, adminOnly: true);
        }

        private static void BuildOverview(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("The Server Guardian");
            w.Paragraph("FiresVAngarde keeps the server's mod list and characters honest. As a player " +
                "you'll only ever notice it if something is off with your install:");
            w.Bullet("If your mods don't match the server's requirements, you're disconnected with a " +
                "CLEAR panel — not a silent boot. It lists exactly what to REMOVE, UPDATE " +
                "(client → server version shown), or INSTALL.");
            w.Bullet("Fix your mod list to match and reconnect — that's all there is to it.");
            w.Paragraph("Character enforcement (single character, server-owned characters, hardcore) " +
                "may also be active depending on the server's settings.");
        }

        private static void BuildModValidation(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Mod Validation");
            w.Paragraph("Connecting clients are challenged for their mod list and validated against " +
                "list files in BepInEx/config/VAngarde/ on the server:");
            w.Bullet("mod_blacklist.txt — reject if present");
            w.Bullet("mod_exactmatch.txt — required mods at exact versions (auto-generated from the " +
                "server's plugins minus server-only entries)");
            w.Bullet("mod_whitelist.txt — extra client mods that are allowed");
            w.Bullet("mod_serveronly.txt — server-side mods excluded from the required set");
            w.Bullet("mod_adminonly.txt — mods only admins may run (overrides the whitelist)");
            w.Bullet("exempt_players.txt — SteamIDs that bypass anti-cheat entirely");
            w.Bullet("admin_character_exceptions.txt — demote a real admin to non-admin on named characters");
            w.AdminDivider();

            w.SubHeader("Keeping Lists Fresh");
            w.Bullet("[VAngarde.ModValidation] EnforceModList / UseWhitelist / UseBlacklist / " +
                "UseExactMatch — which checks run");
            w.Bullet("AutoUpdateModList — regenerate whitelist+exactmatch from the server's own " +
                "plugins each launch");
            w.Bullet("AdminModsExtendWhitelist — an admin connecting with a new mod whitelists it " +
                "for everyone");
            w.Bullet("vangarde push mods — push YOUR client's mod list up as the new standard");
            w.Bullet("Edit a list file, then: vangarde reload (all) or vangarde push " +
                "whitelist|blacklist|exempt (one)");
        }

        private static void BuildDetection(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Detection & Enforcement");
            w.Bullet("DetectGodMode — LOG-ONLY, never auto-kicks (vanilla admins use god legitimately)");
            w.Bullet("DetectFlight / DetectSpeedHack (threshold m/s) / DetectCheatItems + BannedItems");
            w.Bullet("ViolationAction — kick or log");
            w.Bullet("AdminBypass — admins exempt; AdminMonitoring still scans and reports them");
            w.Bullet("AdminCommandLogging — devcommands/god/fly toggles by admins post to Discord");
            w.Bullet("ChallengeTimeoutSeconds / PeriodicRecheckMinutes — validation cadence");
            w.Bullet("[VAngarde.HardeningV2] — audit/signature/integrity toggles are TELEMETRY only " +
                "(client self-reports), not proof; leave off unless collecting data");
            w.AdminDivider();
            w.Paragraph("Kicks and violations post to Discord through FiresDiscordIntegration when " +
                "it's installed (anti-cheat webhook).");
        }

        private static void BuildCharacters(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Character Enforcement");
            w.Paragraph("Server-side character management under [Characters]:");
            w.Bullet("SingleCharacterMode — one character per SteamID on this server");
            w.Bullet("ForceServerCharacter — characters made elsewhere are sent back to create a " +
                "fresh one here (ServerCharacters-style)");
            w.Bullet("HardcoreMode — character is deleted on death");
            w.Bullet("MaintenanceMode — non-admins are kicked (with a countdown) until it's lifted");
            w.Bullet("AfkKickMinutes — auto-logout idle players (0 = off; admins exempt by default)");
            w.Bullet("BackupsToKeep — rotating ZIP backups of server-held characters (default 25)");
            w.AdminDivider();
            w.Paragraph("Profile transfers ride a chunked reliable channel sized to dodge the classic " +
                "30-second big-character timeout ([Characters.Transport]). va_chan_test <sizeKB> " +
                "proves the pipe with a synthetic payload. The Characters.ServerKey is auto-generated " +
                "and signs saves against rollback — never edit or share it.");
        }

        private static void BuildCommands(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Commands");
            w.Bullet("vangarde status — config + tracked peers");
            w.Bullet("vangarde scan — run an immediate cheat scan (server)");
            w.Bullet("vangarde kick <steamid> — force-disconnect a peer (server)");
            w.Bullet("vangarde reload — hot-reload all list files (server)");
            w.Bullet("vangarde push whitelist|blacklist|exempt — reload one list (server)");
            w.Bullet("vangarde push mods — push your local mod list as whitelist/exact-match (client admin)");
            w.Bullet("vangarde bypass [on|off] — toggle admin bypass at runtime");
            w.Bullet("vangarde log <steamid> — post a player's cached BepInEx log to Discord (server)");
            w.Bullet("va_chan_test <sizeKB> — stress-test the chunked transfer channel");
        }
    }
}
