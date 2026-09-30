using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

using FiresCore.Sync;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Console commands for managing VAngarde anti-cheat system.
    ///
    /// Commands:
    ///   vangarde status              - Show VAngarde status and config
    ///   vangarde reload              - Hot-reload all list files (server)
    ///   vangarde kick [steamid]      - Force-disconnect a player by SteamID (server)
    ///   vangarde scan                - Run an immediate cheat scan (server)
    ///   vangarde bypass [on|off]     - Toggle admin bypass at runtime (admin, synced to server)
    ///   vangarde push whitelist      - Reload whitelist from disk (server)
    ///   vangarde push blacklist      - Reload blacklist from disk (server)
    ///   vangarde push exempt         - Reload exempt players from disk (server)
    ///   vangarde push mods           - Push your mod list to server as whitelist/exact match (admin client)
    ///
    /// Registered via Harmony patch on Terminal.InitTerminal (same pattern as ProgressionConsoleCommands).
    /// </summary>
    [HarmonyPatch]
    public static class VAngardeConsoleCommands
    {
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        [HarmonyPriority(Priority.Low)]
        public static class Patch_Terminal_InitTerminal
        {
            private static bool _added;

            static void Postfix()
            {
                if (_added) return;
                _added = true;

                new Terminal.ConsoleCommand("vangarde",
                    "VAngarde anti-cheat commands. Usage: vangarde [status|reload|kick|scan|bypass|push]",
                    HandleCommand, isCheat: true, isNetwork: false, onlyServer: false);
            }
        }

        private static void HandleCommand(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                PrintUsage(args);
                return;
            }

            string subCommand = args[1].ToLowerInvariant();
            switch (subCommand)
            {
                case "status":
                    CmdStatus(args);
                    break;
                case "reload":
                    CmdReload(args);
                    break;
                case "kick":
                    CmdKick(args);
                    break;
                case "scan":
                    CmdScan(args);
                    break;
                case "bypass":
                    CmdBypass(args);
                    break;
                case "push":
                    CmdPush(args);
                    break;
                case "log":
                    CmdSendLog(args);
                    break;
                default:
                    PrintUsage(args);
                    break;
            }
        }

        private static void PrintUsage(Terminal.ConsoleEventArgs args)
        {
            args.Context.AddString("[VAngarde] Commands:");
            args.Context.AddString("  vangarde status  - Show status & tracked peers");
            args.Context.AddString("  vangarde reload  - Hot-reload all list files");
            args.Context.AddString("  vangarde kick <steamid>  - Force-disconnect player");
            args.Context.AddString("  vangarde scan    - Run immediate cheat scan");
            args.Context.AddString("  vangarde bypass [on|off]  - Toggle admin bypass (syncs to server)");
            args.Context.AddString("  vangarde push <whitelist|blacklist|exempt>  - Reload a specific list");
            args.Context.AddString("  vangarde push mods  - Push your mod list to server as whitelist/exact match (admin)");
            args.Context.AddString("  vangarde log <steamid>  - Post a player's cached BepInEx log to Discord");
        }

        private static void CmdStatus(Terminal.ConsoleEventArgs args)
        {
            bool active = VAngardeCore.IsActive();
            bool enabled = VAngardeConfig.Enabled?.Value ?? false;
            bool modEnforce = VAngardeConfig.EnforceModList?.Value ?? false;
            bool adminBypass = VAngardeConfig.AdminBypass?.Value ?? true;

            args.Context.AddString($"[VAngarde] Enabled: {enabled} | Active: {active}");
            args.Context.AddString($"  Admin bypass: {adminBypass}");
            args.Context.AddString($"  Mod enforcement: {modEnforce}");
            args.Context.AddString($"  Whitelist: {VAngardeConfig.UseWhitelist?.Value} ({VAngardeModValidator.WhitelistCount} entries)");
            args.Context.AddString($"  Blacklist: {VAngardeConfig.UseBlacklist?.Value} ({VAngardeModValidator.BlacklistCount} entries)");
            args.Context.AddString($"  Exact match: {VAngardeConfig.UseExactMatch?.Value} ({VAngardeModValidator.ExactMatchCount} entries)");
            args.Context.AddString($"  Detection - God: {VAngardeConfig.DetectGodMode?.Value}, " +
                                   $"Fly: {VAngardeConfig.DetectFlight?.Value}, " +
                                   $"Speed: {VAngardeConfig.DetectSpeedHack?.Value} (god=log-only)");

            if (ZNet.instance != null)
            {
                var peers = ZNet.instance.GetPeers();
                args.Context.AddString($"  Connected peers: {peers?.Count ?? 0}");
            }
        }

        private static void CmdReload(Terminal.ConsoleEventArgs args)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer())
            {
                args.Context.AddString("[VAngarde] 'reload' is server-only");
                return;
            }
            VAngardeModValidator.ReloadAll();
            args.Context.AddString("[VAngarde] All list files reloaded");
        }

        private static void CmdKick(Terminal.ConsoleEventArgs args)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer())
            {
                args.Context.AddString("[VAngarde] 'kick' is server-only");
                return;
            }
            if (args.Length < 3)
            {
                args.Context.AddString("[VAngarde] Usage: vangarde kick <steamid>");
                return;
            }

            string targetId = args[2];
            var peers = ZNet.instance?.GetPeers();
            if (peers == null)
            {
                args.Context.AddString("[VAngarde] No peers connected");
                return;
            }

            ZNetPeer target = null;
            foreach (var peer in peers)
            {
                if (peer.m_socket.GetHostName().Contains(targetId))
                {
                    target = peer;
                    break;
                }
            }

            if (target == null)
            {
                args.Context.AddString($"[VAngarde] No peer found matching '{targetId}'");
                return;
            }

            VAngardeCore.ForceDisconnect(target, "Admin kick via vangarde command");
            args.Context.AddString($"[VAngarde] Force-disconnected {target.m_socket.GetHostName()}");
        }

        private static void CmdScan(Terminal.ConsoleEventArgs args)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer())
            {
                args.Context.AddString("[VAngarde] 'scan' is server-only");
                return;
            }
            VAngardeCheatScanner.ScanAll();
            args.Context.AddString("[VAngarde] Cheat scan completed");
        }

        private static void CmdBypass(Terminal.ConsoleEventArgs args)
        {
            if (VAngardeConfig.AdminBypass == null)
            {
                args.Context.AddString("[VAngarde] AdminBypass config not initialized");
                return;
            }

            if (args.Length < 3)
            {
                // No argument - toggle
                bool current = VAngardeConfig.AdminBypass.Value;
                VAngardeConfig.AdminBypass.Value = !current;
                args.Context.AddString($"[VAngarde] AdminBypass toggled: {current} ? {!current}");
                args.Context.AddString("  Change synced to server via ConfigSync.");
                return;
            }

            string arg = args[2].ToLowerInvariant();
            switch (arg)
            {
                case "on":
                case "true":
                case "1":
                    VAngardeConfig.AdminBypass.Value = true;
                    args.Context.AddString("[VAngarde] AdminBypass set to ON - admins exempt from scanning");
                    break;
                case "off":
                case "false":
                case "0":
                    VAngardeConfig.AdminBypass.Value = false;
                    args.Context.AddString("[VAngarde] AdminBypass set to OFF - admins will be scanned");
                    break;
                default:
                    args.Context.AddString("[VAngarde] Usage: vangarde bypass [on|off]");
                    return;
            }

            args.Context.AddString("  Change synced to server via ConfigSync.");
        }

        private static void CmdPush(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 3)
            {
                args.Context.AddString("[VAngarde] Usage: vangarde push <whitelist|blacklist|exempt|mods>");
                return;
            }

            string listName = args[2].ToLowerInvariant();
            switch (listName)
            {
                case "whitelist":
                {
                    if (!ZNet.instance || !ZNet.instance.IsServer())
                    { args.Context.AddString("[VAngarde] 'push whitelist' is server-only. Use 'push mods' from a client."); break; }
                    int count = VAngardeModValidator.ReloadWhitelist();
                    args.Context.AddString($"[VAngarde] Whitelist reloaded from disk ({count} entries)");
                    if (count > 0)
                        PrintListPreview(args, VAngardeConfig.WhitelistPath, 5);
                    break;
                }
                case "blacklist":
                {
                    if (!ZNet.instance || !ZNet.instance.IsServer())
                    { args.Context.AddString("[VAngarde] 'push blacklist' is server-only."); break; }
                    int count = VAngardeModValidator.ReloadBlacklist();
                    args.Context.AddString($"[VAngarde] Blacklist reloaded from disk ({count} entries)");
                    if (count > 0)
                        PrintListPreview(args, VAngardeConfig.BlacklistPath, 5);
                    break;
                }
                case "exempt":
                {
                    if (!ZNet.instance || !ZNet.instance.IsServer())
                    { args.Context.AddString("[VAngarde] 'push exempt' is server-only."); break; }
                    int count = VAngardeModValidator.ReloadExemptPlayers();
                    args.Context.AddString($"[VAngarde] Exempt players reloaded from disk ({count} entries)");
                    if (count > 0)
                        PrintListPreview(args, VAngardeConfig.ExemptPlayersPath, 5);
                    break;
                }
                case "mods":
                {
                    // Admin push: send local mod list to server as whitelist/exact match override
                    if (ZNet.instance != null && ZNet.instance.IsServer())
                    {
                        args.Context.AddString("[VAngarde] 'push mods' must be run from a client, not the server console.");
                        args.Context.AddString("  On the server, edit the files directly or use 'vangarde reload'.");
                    }
                    else
                    {
                        VAngardeCore.ClientPushModList();
                        var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
                        args.Context.AddString($"[VAngarde] Pushed {plugins?.Count ?? 0} mods to server as whitelist/exact match override.");
                        args.Context.AddString("  Server will hot-reload the lists automatically.");
                    }
                    break;
                }
                default:
                    args.Context.AddString($"[VAngarde] Unknown list: {listName}");
                    args.Context.AddString("  Valid options: whitelist, blacklist, exempt, mods");
                    break;
            }
        }

        private static void CmdSendLog(Terminal.ConsoleEventArgs args)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer())
            {
                args.Context.AddString("[VAngarde] 'log' is server-only");
                return;
            }

            if (args.Length < 3)
            {
                args.Context.AddString("[VAngarde] Usage: vangarde log <steamid>");
                return;
            }

            if (!FiresCore.Bridge.DiscordSink.IsActive())
            {
                args.Context.AddString("[VAngarde] Discord integration is not active - cannot post log");
                return;
            }

            string targetId = args[2];

            // Resolve the player name from connected peers (best-effort)
            string playerName = targetId;
            var peers = ZNet.instance?.GetPeers();
            if (peers != null)
            {
                foreach (var peer in peers)
                {
                    if (peer.m_socket.GetHostName().Contains(targetId))
                    {
                        playerName = peer.m_playerName;
                        // Use the full hostname as the lookup key
                        targetId = peer.m_socket.GetHostName();
                        break;
                    }
                }
            }

            byte[] logBytes = VAngardeModValidator.ReadCachedClientLog(targetId);
            if (logBytes == null || logBytes.Length == 0)
            {
                args.Context.AddString($"[VAngarde] No cached log found for '{targetId}'");
                args.Context.AddString($"  Logs are stored in: {VAngardeConfig.ClientLogsDir}");
                return;
            }

            FiresCore.Bridge.DiscordSink.SendClientLogOnRequest(
                playerName, targetId, logBytes);

            args.Context.AddString($"[VAngarde] Posting cached log for {playerName} ({targetId}) to Discord...");
        }

        /// <summary>
        /// Print the first N non-comment entries from a list file as a preview.
        /// </summary>
        private static void PrintListPreview(Terminal.ConsoleEventArgs args, string path, int maxEntries)
        {
            try
            {
                if (!File.Exists(path)) return;
                var entries = File.ReadAllLines(path)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                    .Take(maxEntries)
                    .ToList();
                if (entries.Count > 0)
                {
                    args.Context.AddString("  Preview:");
                    foreach (var entry in entries)
                        args.Context.AddString($"    {entry}");
                }
            }
            catch { /* non-critical preview */ }
        }
    }
}
