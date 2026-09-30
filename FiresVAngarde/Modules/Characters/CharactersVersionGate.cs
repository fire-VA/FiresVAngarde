using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Version-string gate. Appends <c>-VAngarde Anticheat</c> to the advertised game version so the server
    /// is branded in the main-menu character/server list (the same mechanism ServerCharacters used with its
    /// own <c>-ServerCharacters</c> tag — a <see cref="GameVersion.ToString"/> postfix, NOT a world modifier).
    /// The server kicks any client connecting without the suffix, which guarantees the character/anti-cheat
    /// mod is present. (Was <c>-ServerCharacters</c> for drop-in compat; rebranded now that VAngarde owns this.)
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersVersionGate
    {
        // What identifies a VAngarde client on the wire. Matched WITHOUT the separator so the gate keeps
        // working if the separator ever has to change again.
        internal const string VersionMarker = "VAngarde Anticheat";

        // Emitted form. The separator is a DOT, and that is load-bearing on Valheim 1.0.
        //
        // ZNet.RPC_PeerInfo only reads the peer's network-version uint when
        // GameVersion.TryParseGameVersion(versionString) succeeds; when the parse fails the uint is never
        // read and stays 0, so the very next line rejects the peer with ErrorVersion. 1.0 rewrote that
        // parser: it used to pull the LEADING digits out of each segment via a local TryGetFirstIntFromString
        // helper, so "0.220.5-VAngarde Anticheat" parsed as patch 5 and everything worked. 1.0 replaced that
        // helper with plain int.TryParse, which demands the WHOLE segment be numeric - so "7-VAngarde Anticheat"
        // stopped parsing and every VAngarde client was kicked from every VAngarde server with
        // "Incompatible version", reporting network version 0.
        //
        // The parser splits on '.' and reads only segments 0, 1 and 2; anything beyond index 2 is ignored
        // outright. So a suffix introduced by a dot lands in a FOURTH segment, leaves the three numeric ones
        // intact, and parses on both vanilla and modded clients.
        internal const string VersionSuffix = "." + VersionMarker;

        [HarmonyPatch(typeof(GameVersion), nameof(GameVersion.ToString))]
        [HarmonyPriority(Priority.Low)]
        [HarmonyPostfix]
        private static void GameVersion_ToString_Postfix(GameVersion __instance, ref string __result)
        {
            if (__instance != global::Version.CurrentVersion) return;
            if (string.IsNullOrEmpty(__result)) return;
            if (__result.Contains(VersionMarker)) return;

            // GameVersion.ToString has three shapes: "1.0.7", "1.0" (patch 0) and "1.0.rc3". Only the first
            // and third already carry three segments; "1.0" would put the suffix AT index 2 and fail the
            // parse for exactly the same reason the old '-' form did. Pad it out first.
            int dots = 0;
            for (int i = 0; i < __result.Length; i++) if (__result[i] == '.') dots++;
            if (dots < 2) __result += ".0";

            __result += VersionSuffix;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        [HarmonyPriority(Priority.Low)]
        [HarmonyPrefix]
        private static bool ZNet_RPC_PeerInfo_Prefix(ZRpc rpc, ZPackage pkg)
        {
            if (CharactersConfig.Enabled != null && !CharactersConfig.Enabled.Value) return true;

            pkg.ReadLong();
            string version = pkg.ReadString();
            pkg.SetPos(0);

            if (!ZNet.instance.IsServer() || version.Contains(VersionMarker)) return true;

            // Tell the client WHY before the generic version-mismatch kick (shown via FiresConnectReasonPanel).
            FiresCore.Bridge.FiresConnectReason.Send(rpc,
                "This server is protected by VAngarde Anticheat",
                new[]
                {
                    "Your client connected without the FiresVAngarde mod this server requires.",
                    "Install FiresVAngarde and reconnect.",
                });
            rpc.Invoke("Error", (object)3); // ConnectionStatus 3 = version mismatch
            Debug.Log($"[VAngarde] Kicked {rpc.m_socket.GetHostName()}: connected without the mod ({VersionSuffix}).");
            return false;
        }
    }

    /// <summary>
    /// The version suffix is needed for the connection handshake, but <see cref="GameVersion.ToString"/> appends
    /// it to EVERY server-list row whose numeric version equals ours (the browser has no per-server "runs the mod"
    /// data — only the numeric version). That made the tag appear on every same-version server. Strip it from the
    /// server-list VERSION column so it isn't shown as a misleading blanket; the handshake string is unaffected.
    /// Client-only (FiresVAngarde is a bare BaseUnityPlugin with no auto dedi-skip — guarded via Prepare()).
    /// </summary>
    [HarmonyPatch(typeof(ServerListElement), "UpdateTextAndIcons")]
    internal static class ServerListVersionDisplayClean
    {
        private static System.Reflection.FieldInfo _versionField;

        private static bool Prepare() => UnityEngine.SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static void Postfix(ServerListElement __instance)
        {
            try
            {
                if (_versionField == null) _versionField = AccessTools.Field(typeof(ServerListElement), "m_version");
                var txt = _versionField?.GetValue(__instance) as TMPro.TMP_Text;
                if (txt == null || string.IsNullOrEmpty(txt.text)) return;
                if (!txt.text.Contains(CharactersVersionGate.VersionMarker)) return;
                // Strip whichever separator produced the tag - a peer still on the pre-1.0 '-' form shows up
                // in the same list - then tidy the padding zero the postfix may have added to a 2-segment
                // version so the column reads "1.0" rather than "1.0.0".
                txt.text = txt.text
                    .Replace("." + CharactersVersionGate.VersionMarker, "")
                    .Replace("-" + CharactersVersionGate.VersionMarker, "")
                    .Replace(CharactersVersionGate.VersionMarker, "");
            }
            catch { }
        }
    }

    /// <summary>
    /// PER-SERVER anti-cheat marker. The browser has no per-server mod field, but each server's advertised NAME
    /// IS per-server and is shown in the list — so when VAngarde anti-cheat is ENABLED, append a tag to the name
    /// this server registers with Steam matchmaking. Only anti-cheat-enabled servers get it (truly per-server),
    /// and the advertised name does NOT affect the connection handshake. Server-side (RegisterServer runs on the
    /// host; ZSteamMatchmaking exists on a dedicated server, so no client-only guard is needed).
    /// </summary>
    [HarmonyPatch(typeof(ZSteamMatchmaking), nameof(ZSteamMatchmaking.RegisterServer))]
    internal static class VAngardeServerNameTag
    {
        internal const string NameTag = " - VAngarde Anticheat";

        private static void Prefix(ref string name)
        {
            try
            {
                var enabled = global::VerdantsAscent.Modules.AntiCheat.VAngardeConfig.Enabled;
                if (enabled == null || !enabled.Value) return;
                if (string.IsNullOrEmpty(name) || name.Contains(NameTag)) return;
                name += NameTag;
            }
            catch { }
        }
    }
}
