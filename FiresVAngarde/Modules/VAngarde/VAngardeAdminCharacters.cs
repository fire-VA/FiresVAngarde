using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Server-side reconciler for admin_character_exceptions.txt: a real admin (adminlist.txt) playing one of
    /// their listed NON-admin characters is demoted to non-admin for that session. It works by removing that
    /// Steam ID from the in-memory vanilla admin list (<c>ZNet.m_adminList</c>) - the single source every
    /// admin surface derives from: vanilla devcommands (pushed to clients via SendPlayerList), FUC
    /// AdminSyncing / config-lock, and VAngarde's own admin gating. The SyncedList's backing List is mutated
    /// directly (via GetList()) so <c>Save()</c> is never called and adminlist.txt is never rewritten; every
    /// removal is remembered and restored when the admin disconnects or reconnects on an admin character.
    ///
    /// Reconcile is idempotent and runs on a light periodic loop (independent of the anti-cheat master
    /// toggle) plus a direct call at connect, and it re-asserts each tick so a mid-session hand-edit of
    /// adminlist.txt (which SyncedList reloads within ~10s) can't silently re-grant a demoted character.
    /// Note: a listen-server HOST is always admin (ZNet.IsServer short-circuits), so this only affects
    /// connecting players on a dedicated server.
    /// </summary>
    [HarmonyPatch]
    internal static class VAngardeAdminCharacters
    {
        private const float ReconcileIntervalSeconds = 3f;

        // normalized SteamID -> the exact adminlist entry we pulled, so it can be restored verbatim.
        private static readonly Dictionary<string, string> _removed =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static FieldInfo _adminListField;
        private static MethodInfo _sendAdminList;
        private static bool _loopRunning;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        private static void ZNet_Awake_Postfix(ZNet __instance)
        {
            try
            {
                if (__instance == null || !__instance.IsServer() || _loopRunning) return;
                _loopRunning = true;
                _removed.Clear();
                __instance.StartCoroutine(ReconcileLoop());
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] Admin-character loop start failed: {ex.Message}"); }
        }

        private static IEnumerator ReconcileLoop()
        {
            while (ZNet.instance != null && ZNet.instance.IsServer())
            {
                Reconcile();
                yield return new WaitForSeconds(ReconcileIntervalSeconds);
            }
            _loopRunning = false;
        }

        /// <summary>Immediate reconcile hook (called from the connect flow so demotion isn't delayed a tick).</summary>
        internal static void OnPeerConnect(ZNetPeer peer) => Reconcile();

        // Reconcile BEFORE every admin-list broadcast so the demoted id is already pulled when the packet is
        // built — including the connect-time SendAdminList (ZNet line ~741), which otherwise ships a fresh
        // client the full list for the brief window before our periodic/connect reconcile runs, letting them
        // enable devcommands in that gap. push:false because the caller's send carries the corrected list —
        // calling PushAdminList here would re-enter SendAdminList (guarded harmless, but pointless).
        [HarmonyPatch(typeof(ZNet), "SendAdminList")]
        [HarmonyPrefix]
        private static void ZNet_SendAdminList_Prefix(ZNet __instance)
        {
            if (__instance != null && __instance.IsServer())
                Reconcile(push: false);
        }

        internal static void Reconcile(bool push = true)
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null || !znet.IsServer()) return;
                // Nothing configured and nothing outstanding to restore → don't touch the admin list at all.
                if (!VAngardeModValidator.HasAdminCharacterExceptions && _removed.Count == 0) return;

                var list = GetAdminBackingList(znet);
                if (list == null) return;

                // normId -> host name, for every connected peer currently on a listed non-admin character.
                var demoteNow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var peer in znet.GetPeers())
                {
                    string host = peer?.m_socket?.GetHostName();
                    string character = peer?.m_playerName;
                    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(character)) continue;
                    if (VAngardeModValidator.IsCharacterDemoted(host, character))
                        demoteNow[NormalizeId(host)] = host;
                }

                bool dirty = false;

                // Demote: pull the matching admin-list entry. If the id isn't in the list it isn't a real
                // admin - nothing to do (this can never GRANT admin).
                foreach (var kv in demoteNow)
                {
                    // Re-assert every tick: if the id is present (fresh connect, OR a reload of adminlist.txt
                    // re-added it - SyncedList re-reads the file within ~10s), pull it again. If it isn't in
                    // the list it's either already demoted by us or simply not a real admin - nothing to do.
                    string entry = list.FirstOrDefault(e => NormalizeId(e).Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) continue;
                    list.Remove(entry);
                    if (!_removed.ContainsKey(kv.Key))
                        Debug.Log($"[VAngarde] Admin {kv.Value} is on a non-admin character - admin revoked for this session.");
                    _removed[kv.Key] = entry;
                    dirty = true;
                }

                // Restore: anything we removed that no longer has a connected demoted peer (left, or switched
                // to an admin character) gets its original entry back.
                foreach (var normId in _removed.Keys.ToList())
                {
                    if (demoteNow.ContainsKey(normId)) continue;
                    string entry = _removed[normId];
                    if (!list.Any(e => NormalizeId(e).Equals(normId, StringComparison.OrdinalIgnoreCase)))
                        list.Add(entry);
                    _removed.Remove(normId);
                    dirty = true;
                    Debug.Log($"[VAngarde] Restored admin {entry} (no longer on a non-admin character).");
                }

                if (dirty && push) PushAdminList(znet);
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] Admin-character reconcile failed: {ex.Message}"); }
        }

        // SyncedList.GetList() returns the LIVE internal List<string> - mutating it does NOT trigger Save()
        // (only SyncedList.Add/Remove do), so adminlist.txt is left untouched.
        private static List<string> GetAdminBackingList(ZNet znet)
        {
            if (_adminListField == null) _adminListField = AccessTools.Field(typeof(ZNet), "m_adminList");
            var syncedList = _adminListField?.GetValue(znet);
            if (syncedList == null) return null;
            var getList = syncedList.GetType().GetMethod("GetList");
            return getList?.Invoke(syncedList, null) as List<string>;
        }

        // Re-push the ADMIN list: SendAdminList -> RPC_AdminList -> the client's m_adminListForRpc, which is
        // exactly what ZNet.PlayerIsAdmin / LocalPlayerIsAdminOrHost (and therefore the devcommands gate)
        // read. NOT SendPlayerList - that only carries player names/positions and never touches admin status.
        private static void PushAdminList(ZNet znet)
        {
            try
            {
                if (_sendAdminList == null) _sendAdminList = AccessTools.Method(typeof(ZNet), "SendAdminList");
                _sendAdminList?.Invoke(znet, null);
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] SendAdminList push failed: {ex.Message}"); }
        }

        // adminlist entries may be bare ("7656…") or platform-prefixed ("Steam_7656…"); the socket hands us
        // the bare form. Mirror ZNet.ListContainsId so either stored form matches.
        private static string NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            int us = id.IndexOf('_');
            return (us >= 0 ? id.Substring(us + 1) : id).Trim();
        }
    }
}
