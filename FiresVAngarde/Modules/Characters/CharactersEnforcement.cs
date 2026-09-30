using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Single-character + hardcore + maintenance gates. Single-character is checked inline by
    /// <see cref="CharactersServer"/> during the login profile send (cheapest place to test "this
    /// SteamID already owns a stored character"). Maintenance kicks non-admins on connect and is
    /// driven by <see cref="MaintenanceTicker"/>. Hardcore deletes the .fch on the death RPC; client
    /// fires it from <see cref="Player.OnDeath"/>.
    /// </summary>
    internal static class CharactersEnforcement
    {
        internal const int ErrorSingleChar  = 845979243;
        internal const int ErrorMaintenance = 987345987;

        internal const string RpcPlayerDied = "VA_CharDied";

        internal static bool IsAdmin(string hostName)
        {
            if (string.IsNullOrEmpty(hostName) || ZNet.instance == null) return false;
            try
            {
                var adminList = (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
                if (adminList == null) return false;
                var list = adminList.GetList();
                if (list.Contains(hostName)) return true;
                // Mirror vanilla ZNet.ListContainsId: a Steam admin is matched by BOTH the "Steam_<id>" form
                // AND the bare SteamID64. The connecting peer's host token is the "Steam_"-prefixed form, but
                // admins are usually entered in adminlist.txt as the bare SteamID64 — a plain Contains would
                // miss them and (with admin-bypass on) WRONGLY KICK an admin joining with a 2nd character. [admin-id-normalization]
                string alt = hostName.StartsWith("Steam_", System.StringComparison.Ordinal) ? hostName.Substring(6) : "Steam_" + hostName;
                return list.Contains(alt);
            }
            catch { return false; }
        }

        internal static void DeleteProfile(string steamId, string characterName)
        {
            try
            {
                string folder = CharacterStore.SaveDir;
                string filename = CharacterStore.FileName(steamId, characterName);
                foreach (string ext in new[] { ".fch", ".fch.old", ".fch.signature", ".fch.serverbackup" })
                {
                    string p = folder + filename + ext;
                    if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                }
                Debug.Log($"[Characters] hardcore: deleted profile {filename}.fch (+ siblings).");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Characters] hardcore delete failed for {steamId}/{characterName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Maintenance gate: kick non-admins on connect while MaintenanceMode is on, and kick everyone
    /// not exempt when the maintenance timer expires.
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersMaintenance
    {
        private static float _expireAt = -1f;

        // The maintenance kick rides the vanilla "Error" RPC with a CUSTOM code the client has no string
        // for, so on its own it lands as a bare "failed to connect". Push our own reason first (same pattern
        // as the single-character kick in CharactersServer) and FiresConnectReasonPanel paints it instead.
        // Sent immediately before the Error invoke so it flushes ahead of the disconnect; the reason's TTL
        // is long enough that a mid-session (timer-expiry) kick still paints after the world teardown.
        private static void SendMaintenanceReason(ZNetPeer peer)
        {
            FiresCore.Bridge.FiresConnectReason.Send(peer?.m_rpc,
                "\U0001F527 Server Under Maintenance",
                new[]
                {
                    "The server is currently undergoing maintenance.",
                    "Please stand by - you'll be able to reconnect shortly.",
                });
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (!__instance.IsServer()) return;
            if (CharactersConfig.MaintenanceMode?.Value != true) return;

            string host = peer?.m_socket?.GetHostName();
            if (CharactersEnforcement.IsAdmin(host)) return;

            SendMaintenanceReason(peer);
            peer.m_rpc.Invoke("Error", (object)CharactersEnforcement.ErrorMaintenance);
            Debug.Log($"[Characters] maintenance mode: kicked {host} on connect.");
        }

        [HarmonyPatch(typeof(ZNet), "Update")]
        [HarmonyPostfix]
        private static void ZNet_Update_Postfix(ZNet __instance)
        {
            if (__instance == null || !__instance.IsServer()) return;
            bool on = CharactersConfig.MaintenanceMode?.Value == true;

            if (!on) { _expireAt = -1f; return; }

            if (_expireAt < 0f)
                _expireAt = Time.realtimeSinceStartup + Mathf.Max(10, CharactersConfig.MaintenanceTimerSeconds?.Value ?? 300);

            if (Time.realtimeSinceStartup < _expireAt) return;

            // Timer expired — kick all non-admin peers, once.
            foreach (var peer in __instance.GetConnectedPeers())
            {
                string host = peer?.m_socket?.GetHostName();
                if (string.IsNullOrEmpty(host) || CharactersEnforcement.IsAdmin(host)) continue;
                SendMaintenanceReason(peer);
                peer.m_rpc.Invoke("Error", (object)CharactersEnforcement.ErrorMaintenance);
            }
            _expireAt = Time.realtimeSinceStartup + 60f; // re-kick stragglers no more than once a minute
            Debug.Log("[Characters] maintenance timer expired — kicked non-admins.");
        }
    }

    /// <summary>Hardcore mode: client tells the server on death; server deletes the profile.</summary>
    [HarmonyPatch]
    internal static class CharactersHardcore
    {
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (!__instance.IsServer()) return;
            peer.m_rpc.Register<ZPackage>(CharactersEnforcement.RpcPlayerDied, OnServerReceivedDeath);
        }

        private static void OnServerReceivedDeath(ZRpc rpc, ZPackage _ /* reserved */)
        {
            if (CharactersConfig.HardcoreMode?.Value != true) return;
            var peer = ZNet.instance?.GetPeer(rpc);
            if (peer == null) return;

            string host = peer.m_socket.GetHostName();
            string steamId = !string.IsNullOrEmpty(host) && System.Text.RegularExpressions.Regex.IsMatch(host, "^\\d+$") ? "Steam_" + host : host;
            string name = peer.m_playerName?.ToLower();
            if (!string.IsNullOrEmpty(steamId) && !string.IsNullOrEmpty(name))
                CharactersEnforcement.DeleteProfile(steamId, name);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
        [HarmonyPostfix]
        private static void Player_OnDeath_Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (CharactersConfig.HardcoreMode?.Value != true) return;
            if (!CharactersClient.IsServerCharacter) return;

            var znet = ZNet.instance;
            var server = znet?.GetServerPeer();
            if (server == null || !server.IsReady()) return;

            try { server.m_rpc.Invoke(CharactersEnforcement.RpcPlayerDied, new ZPackage()); }
            catch (System.Exception ex) { Debug.LogWarning($"[Characters] hardcore RPC failed: {ex.Message}"); }

            if (Game.instance != null) Game.instance.Logout();
        }
    }

    /// <summary>
    /// AFK kick: client-side input monitor. Logs out the local player after N minutes without
    /// keyboard/mouse input. Admins are exempt by default.
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersAfkKick
    {
        private static float _lastInputAt;
        private static bool _kicked;

        [HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        [HarmonyPostfix]
        private static void Player_SetLocalPlayer_Postfix()
        {
            _lastInputAt = Time.realtimeSinceStartup;
            _kicked = false;
        }

        [HarmonyPatch(typeof(Game), "Update")]
        [HarmonyPostfix]
        private static void Game_Update_Postfix()
        {
            if (_kicked || Player.m_localPlayer == null || Game.instance == null) return;
            int minutes = CharactersConfig.AfkKickMinutes?.Value ?? 0;
            if (minutes <= 0) return;

            if (Input.anyKeyDown || Input.mousePosition != _lastMouse) _lastInputAt = Time.realtimeSinceStartup;
            _lastMouse = Input.mousePosition;

            if (Time.realtimeSinceStartup - _lastInputAt < minutes * 60f) return;

            // Exempt admins by default (we self-identify as admin via the server's adminList being
            // synced to the client when the player connects).
            if (CharactersConfig.ExcludeAdminsFromAfk?.Value == true &&
                CharactersEnforcement.IsAdmin(ZNet.instance?.GetServerPeer()?.m_socket?.GetHostName())) return;

            _kicked = true;
            Debug.Log("[Characters] AFK kick: logging out local player.");
            Game.instance.Logout();
        }

        private static Vector3 _lastMouse;
    }
}
