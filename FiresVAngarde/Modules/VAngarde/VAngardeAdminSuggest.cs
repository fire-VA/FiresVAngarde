using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// The admin-only mods an ADMIN lacks, suggested and never required (0.2.40, Fire 10-05: "if they are missing a mod we should
    /// flag it and put in a popup as their world loads telling them they're missing x mod and should install it, and they can either
    /// ignore or hit okay to back out and install.. if they ignore the decision is saved so they are not bothered again until the
    /// next time the server mod list is updated"). Only in AdminOnlyListMode = AdminWhitelist (AdminRequired refuses instead).
    /// - Server: once an admin passes validation, the admin-only GUIDs its client lacks go to that client (with the versions the
    ///   server knows), the world's id and a hash of the server's whole admin-only list. Info line.
    /// - Client: once its player has spawned, a Yes/No popup. Yes backs out to the menu (vanilla's logout) to install; No ignores,
    ///   remembered per world id + list hash in BepInEx\config\VAngarde\admin_suggestions_ignored.txt (inside the game folder), so
    ///   the popup comes back only when the server's admin-only list changes. Debug lines.
    /// </summary>
    [HarmonyPatch]
    internal static class VAngardeAdminSuggest
    {
        private const string RpcSuggest = "VA_vgsug";   // server -> admin client
        private const string IgnoreFile = "admin_suggestions_ignored.txt";

        // ── server ─────────────────────────────────────────────────────────────────────────────────────────────────────
        internal static void Suggest(ZNetPeer peer, string platformId, Dictionary<string, string> clientMods)
        {
            try
            {
                if (peer == null || VAngardeConfig.AdminOnlyRequired) return;
                var missing = VAngardeModValidator.AdminOnlyMissing(clientMods);
                if (missing.Count == 0) return;
                string list = string.Join(", ", missing.Select(m => string.IsNullOrEmpty(m.Value) ? m.Key : $"{m.Key} ({m.Value})").ToArray());
                FiresVAngarde.Log?.LogInfo($"[VAngarde] admin {platformId} lacks admin-only mods: {list} (suggested, not required)");
                var pkg = new ZPackage();
                pkg.Write(ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L);
                pkg.Write(Hash(VAngardeModValidator.AdminOnlySignature()));
                pkg.Write(missing.Count);
                foreach (var m in missing) { pkg.Write(m.Key); pkg.Write(m.Value ?? ""); }
                ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, RpcSuggest, pkg);
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] admin-only suggestion for {platformId} failed: {ex.Message}"); }
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").Substring(0, 16);
        }

        // ── client ─────────────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class Pending
        {
            public long World;
            public string ListHash;
            public List<KeyValuePair<string, string>> Mods;
        }

        private static Pending s_pending;
        private static bool s_showing;

        [HarmonyPatch(typeof(ZNet), "Awake"), HarmonyPostfix]
        private static void ZNet_Awake_Register()
        {
            try { ZRoutedRpc.instance?.Register<ZPackage>(RpcSuggest, RPC_Suggest); }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] admin suggestion RPC register failed: {ex.Message}"); }
        }

        private static void RPC_Suggest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            var p = new Pending { World = pkg.ReadLong(), ListHash = pkg.ReadString(), Mods = new List<KeyValuePair<string, string>>() };
            int n = pkg.ReadInt();
            for (int i = 0; i < n; i++) p.Mods.Add(new KeyValuePair<string, string>(pkg.ReadString(), pkg.ReadString()));
            if (Ignored(p))
            {
                FiresVAngarde.Log?.LogDebug($"[VAngarde] admin-only suggestion ({p.Mods.Count} mod(s)) not shown: ignored for this server's list {p.ListHash}");
                return;
            }
            s_pending = p;
            if (Player.m_localPlayer != null) Show();
        }

        // As the world finishes loading: the player has spawned.
        [HarmonyPatch(typeof(Player), "OnSpawned"), HarmonyPostfix]
        private static void Player_OnSpawned(Player __instance)
        {
            if (__instance == Player.m_localPlayer && s_pending != null) Show();
        }

        private static void Show()
        {
            var p = s_pending;
            if (p == null || s_showing) return;
            if (!UnifiedPopup.IsAvailable()) return;   // the next spawn tries again
            s_pending = null;
            s_showing = true;
            string mods = string.Join(", ", p.Mods.Select(m => string.IsNullOrEmpty(m.Value) ? m.Key : $"{m.Key} (v{m.Value})").ToArray());
            UnifiedPopup.Push(new YesNoPopup("Suggested admin mods",
                $"This server suggests these admin mods: {mods}. Install them for the full admin toolset.\n\n"
                + "Yes: back out to the menu to install them.  No: ignore (you won't be asked again until the server's admin mod list changes).",
                () => Answer(p, true), () => Answer(p, false), false));
        }

        private static void Answer(Pending p, bool backOut)
        {
            s_showing = false;
            UnifiedPopup.Pop();
            if (backOut)
            {
                FiresVAngarde.Log?.LogDebug($"[VAngarde] admin-only suggestion: backing out to install {p.Mods.Count} mod(s)");
                Game.instance?.Logout();
                return;
            }
            Remember(p);
            FiresVAngarde.Log?.LogDebug($"[VAngarde] admin-only suggestion ignored for world {p.World}, list {p.ListHash} ({p.Mods.Count} mod(s)); asked again when the list changes");
        }

        private static string IgnorePath => Path.Combine(VAngardeConfig.ConfigDir, IgnoreFile);

        private static bool Ignored(Pending p)
        {
            try
            {
                if (!File.Exists(IgnorePath)) return false;
                string line = $"{p.World}={p.ListHash}";
                return File.ReadAllLines(IgnorePath).Any(l => l.Trim() == line);
            }
            catch { return false; }
        }

        // One line per world id: a new list hash replaces that world's old line.
        private static void Remember(Pending p)
        {
            try
            {
                Directory.CreateDirectory(VAngardeConfig.ConfigDir);
                string prefix = p.World + "=";
                var lines = File.Exists(IgnorePath) ? File.ReadAllLines(IgnorePath).Where(l => !l.StartsWith(prefix, StringComparison.Ordinal)).ToList() : new List<string>();
                lines.Add(prefix + p.ListHash);
                File.WriteAllLines(IgnorePath, lines.ToArray());
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] could not remember the ignored admin-only suggestion: {ex.Message}"); }
        }
    }
}
