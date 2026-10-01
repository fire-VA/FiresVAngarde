using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Client side of the character handoff: register the login receiver on connect, adopt the
    /// server's authoritative profile in-memory when it arrives, and upload the profile to the server
    /// whenever it saves. First cut — no template/emergency-backup/overwrite-guard yet. Not runtime-tested.
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersClient
    {
        // Set by Game.Logout when the connection is in a non-Connected state — i.e. unclean drop.
        // Read by the SavePlayerToDisk postfix below: if true, the local save also writes the signed
        // sidecar backup so the server can verify and restore on next clean reconnect.
        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        [HarmonyPrefix]
        private static void Game_Logout_Prefix()
        {
            try
            {
                var status = ZNet.GetConnectionStatus();
                if (status != ZNet.ConnectionStatus.Connected && status != ZNet.ConnectionStatus.Connecting)
                    CharactersEmergencyBackup.MarkEmergencyBackupPending();
            }
            catch { /* ZNet not ready */ }
        }

        // R38 (PlayFab): vanilla's logout saves the profile, shuts ZNet down and disposes the socket in one frame, and a
        // crossplay link drops whatever it still holds, so 1 of the profile's 9 fragments left and the server kept the previous
        // save. ContinueLogout runs after every mod's Game.Logout hooks and before that save: it is held while the profile is
        // saved and uploaded, until the server confirms it (or LogoutSaveWaitSeconds pass, or the link drops), then runs as
        // usual. Another mod may hold it the same way: each holds once per logout, swallows the calls that come while it
        // waits, and calls it again when done, so the last one to finish lets it through.
        private const string SavingMessage = "Saving your character on the server...";
        private static readonly MethodInfo s_continueLogout = AccessTools.Method(typeof(Game), "ContinueLogout");
        private static Game s_holdingFor, s_heldFor;
        private static int s_uploadsSent, s_uploadsSettled;
        private static bool s_uploadConfirmed, s_holdUploaded, s_earlyQuitLogged;
        private static readonly System.Diagnostics.Stopwatch s_holdClock = new System.Diagnostics.Stopwatch();

        [HarmonyPatch(typeof(Game), "ContinueLogout")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Game_ContinueLogout_Prefix(Game __instance, bool save, bool shouldExit, bool changeToStartScene)
        {
            if (s_holdingFor == __instance) return false;
            if (s_heldFor == __instance || !save || !ServerLinkUp()) return true;
            s_holdingFor = __instance;
            s_holdUploaded = s_earlyQuitLogged = false;
            s_holdClock.Restart();
            __instance.StartCoroutine(HoldLogout(__instance, save, shouldExit, changeToStartScene));
            return false;
        }

        private static bool ServerLinkUp()
        {
            if (!Active || !IsServerCharacter || ZNet.instance == null || ZNet.instance.IsServer()) return false;
            ZNetPeer server = ZNet.instance.GetServerPeer();
            return server != null && server.IsReady() && server.m_socket != null && server.m_socket.IsConnected();
        }

        private static IEnumerator HoldLogout(Game game, bool save, bool shouldExit, bool changeToStartScene)
        {
            yield return SaveAndWait(game, "logout");
            s_holdingFor = null;
            s_heldFor = game;
            if (game != null) s_continueLogout.Invoke(game, new object[] { save, shouldExit, changeToStartScene });
        }

        // A quit asked for while the logout was held goes ahead once the logout really ran (a prefix may have skipped it).
        [HarmonyPatch(typeof(Game), "ContinueLogout")]
        [HarmonyPostfix]
        private static void Game_ContinueLogout_Postfix(Game __instance)
        {
            if (!s_quitPending || !__instance.IsShuttingDown()) return;
            s_quitPending = false;
            s_quitReleased = true;
            Application.Quit();
        }

        // Vanilla's Quit button and closing the window (Alt+F4) quit without a logout, and OnApplicationQuit saves and shuts ZNet
        // down with no frame left to send in (R39). R40: a script's Application.Quit went on to OnApplicationQuit although a
        // wantsToQuit handler had returned false, so a quit can't be relied on to wait. Instead it becomes a logout, whose save hold
        // is proven, and the quit is issued from the postfix above once that logout has really run.
        private static bool s_quitHookAdded, s_quitPending, s_quitReleased;

        private static bool WantsToQuit()
        {
            Game game = Game.instance;
            if (s_quitReleased || game == null || game.IsShuttingDown()) return true;
            if (s_holdingFor != game && !ServerLinkUp()) return true;
            QuitAfterLogout(game);
            return false;
        }

        [HarmonyPatch(typeof(Menu), "QuitGame")]
        [HarmonyPrefix]
        private static bool Menu_QuitGame_Prefix()
        {
            Game game = Game.instance;
            if (s_quitReleased || game == null || game.IsShuttingDown()) return true;
            if (s_holdingFor != game && !ServerLinkUp()) return true;
            QuitAfterLogout(game);
            return false;
        }

        private static void QuitAfterLogout(Game game)
        {
            s_quitPending = true;
            if (s_holdingFor != game) game.Logout(true, false);
        }

        // R40 ([fgn]): Unity still sent OnApplicationQuit although a wantsToQuit handler had returned false, and the process ran on.
        // Vanilla's OnApplicationQuit would then shut ZNet down in the middle of the held logout. While the logout waits for the save,
        // that early call is skipped; the held logout shuts down properly and the quit follows it. Should the process really be going
        // down, the profile is still saved to disk here, as vanilla's body would (no upload: that one is already on its way).
        [HarmonyPatch(typeof(Game), "OnApplicationQuit")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Game_OnApplicationQuit_Prefix(Game __instance)
        {
            if (s_quitReleased || __instance.IsShuttingDown() || s_holdingFor != __instance) return true;
            s_quitPending = true;
            if (s_earlyQuitLogged) return false;
            s_earlyQuitLogged = true;
            try { __instance.SavePlayerProfile(setLogoutPoint: true); }
            catch (Exception ex) { Debug.LogWarning($"[Characters] local save at the early OnApplicationQuit failed: {ex.Message}"); }
            Debug.LogWarning($"[Characters] OnApplicationQuit arrived during the held logout ({s_holdClock.ElapsedMilliseconds} ms into the hold): " +
                             "vanilla's shutdown skipped, the profile saved to disk, the logout finishes it and then quits.");
            return false;
        }

        private static IEnumerator SaveAndWait(Game game, string leaving)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int awaited = s_uploadsSent + 1;
            game.SavePlayerProfile(setLogoutPoint: true);
            bool uploaded = s_uploadsSent >= awaited;
            s_holdUploaded = uploaded;
            if (uploaded && MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, SavingMessage);
            while (uploaded && s_uploadsSettled < awaited && ServerLinkUp()
                   && timer.Elapsed.TotalSeconds < CharactersConfig.LogoutSaveWaitSeconds)
                yield return null;

            s_uploadConfirmed = uploaded && s_uploadsSettled >= awaited;
            string outcome = !uploaded ? "nothing to upload"
                : s_uploadConfirmed ? "the server answered"
                : ServerLinkUp() ? $"no answer within {CharactersConfig.LogoutSaveWaitSeconds:0} s"
                : "the connection was lost";
            Debug.Log($"[Characters] {leaving} held {timer.ElapsedMilliseconds} ms for the character save: {outcome}.");
        }

        private static void OnServerSettledSave(ZRpc rpc, int length)
        {
            s_uploadsSettled++;
            if (length == CharactersServer.NotSaved)
                Debug.LogWarning("[Characters] the server did NOT save the uploaded profile: it did not load there as a player profile.");
            else
                Debug.Log($"[Characters] the server saved the uploaded profile ({length}B).");
        }

        /// <summary>True once the server has confirmed this is a server-managed character.</summary>
        internal static bool IsServerCharacter;

        private static bool Active => CharactersConfig.Enabled == null || CharactersConfig.Enabled.Value;

        /// <summary>
        /// True when server-character enforcement applies to this client (ServerCharacters anti-import):
        /// a character already played elsewhere must be rejected rather than imported. On when
        /// <c>ForceServerCharacter</c> is enabled AND the local client is not an admin being bypassed.
        /// Config is read defensively (this can run before CharactersConfig.Initialize); the admin
        /// self-check uses vanilla <see cref="ZNet.LocalPlayerIsAdminOrHost"/>, which reads the
        /// server-synced admin list and is valid on a pure client (unlike the server-only
        /// CharactersEnforcement.IsAdmin).
        /// </summary>
        private static bool ShouldEnforceServerCharacter()
        {
            bool force = CharactersConfig.ForceServerCharacter?.Value ?? false;
            if (!force) return false;

            bool adminBypass = CharactersConfig.ForceServerCharacterAdminBypass?.Value ?? true;
            if (!adminBypass) return true;

            bool localIsAdmin = false;
            try { localIsAdmin = ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost(); }
            catch { localIsAdmin = false; }
            return !localIsAdmin;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (!Active || __instance.IsServer()) return;
            IsServerCharacter = false;
            s_uploadsSent = s_uploadsSettled = 0;
            s_uploadConfirmed = s_quitPending = s_earlyQuitLogged = false;
            // A new session: the last one's unclean-disconnect flag and key are spent (its sidecars go out below). 0.2.36
            CharactersEmergencyBackup.ClearClientState();
            // The server asks for a save when it shuts down with players online (0.2.36, CharactersShutdownPull).
            peer.m_rpc.Register(CharactersShutdownPull.RpcSaveNow, OnServerAsksSave);
            if (!s_quitHookAdded)
            {
                s_quitHookAdded = true;
                Application.wantsToQuit += WantsToQuit;
            }
            peer.m_rpc.Register<ZPackage>(CharactersServer.RpcLogin,
                CharacterProfileTransport.Receiver(OnClientReceivedProfile));
            peer.m_rpc.Register<int>(CharactersServer.RpcSaved, OnServerSettledSave);

            // Emergency-backup: register the key-receiver, then ship any stored sidecars from a
            // previous unclean disconnect to the server for verification + restore.
            peer.m_rpc.Register<ZPackage>(CharactersEmergencyBackup.RpcKeyExchange,
                CharactersEmergencyBackup.OnClientReceiveKey);
            try
            {
                var local = Game.instance?.m_playerProfile;
                if (local != null) CharactersEmergencyBackup.TrySendStoredEmergencyBackup(peer, local);
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Characters] emergency-backup send-on-connect failed: {ex.Message}");
            }
        }

        private static void OnClientReceivedProfile(ZRpc rpc, byte[] bytes)
        {
            if (Game.instance == null) return;

            if (bytes == null || bytes.Length == 0)
            {
                // New to this server — the server has no stored profile for this id.
                var local = Game.instance.m_playerProfile;
                bool hasPlayedData = local?.m_worldData != null && local.m_worldData.Count != 0;

                if (ShouldEnforceServerCharacter() && hasPlayedData)
                {
                    // ServerCharacters anti-import: this character has been played elsewhere
                    // (single-player or another server). REJECT it back to the menu WITHOUT touching the
                    // profile or its .fch on disk — no data is lost. The player is asked to create a new
                    // character first. Mirrors SC's ClientSide.onReceivedProfile empty-data path exactly.
                    FiresCore.Bridge.FiresConnectReason.SetLocalReason(
                        "This server requires a fresh character",
                        new[]
                        {
                            "You're connecting with a character that has already been played elsewhere.",
                            "Create a NEW character before connecting, to avoid loss of data."
                        });
                    Debug.LogWarning($"[Characters] ForceServerCharacter: rejecting '{local.GetName()}' — " +
                                     "character has been played elsewhere; asked to create a new one (no data touched).");
                    AccessTools.Field(typeof(ZNet), "m_connectionStatus")
                        .SetValue(null, ZNet.ConnectionStatus.ErrorConnectFailed);
                    Game.instance.Logout();
                    return;
                }

                // Brand-new blank character, OR enforcement off / admin-bypassed: keep the local
                // character and let it become this player's server character.
                IsServerCharacter = true;
                if (hasPlayedData)
                {
                    Debug.Log("[Characters] server has no stored character but local profile has world data — " +
                              "keeping local character (ForceServerCharacter off or admin-bypassed).");
                }

                // The starter template is for a brand-new character only: one that has played somewhere already keeps
                // its skills and inventory ([perf] 2026-09-28: it was re-applied to existing characters at every join).
                if (!hasPlayedData) CharactersPlayerTemplate.Mark();
                return;
            }

            try
            {
                var profile = new PlayerProfile(Game.instance.m_playerProfile.m_filename);
                if (!profile.LoadFromBytes(bytes))
                {
                    Debug.LogError("[Characters] received profile failed to load — keeping local profile.");
                    return;
                }
                Game.instance.m_playerProfile = profile;
                IsServerCharacter = true;
                Debug.Log($"[Characters] adopted server profile '{profile.m_playerName}' ({bytes.Length}B).");

                // Adopting the server's authoritative profile invalidates any local sidecars left
                // over from a prior unclean disconnect — clean them up so they can't be replayed.
                CharactersEmergencyBackup.CleanSidecarsIfAny(profile);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Characters] OnClientReceivedProfile failed: {ex}");
            }
        }

        // The server is shutting down with us online (0.2.36): save now, which uploads through the postfix below.
        private static void OnServerAsksSave(ZRpc rpc)
        {
            try
            {
                if (!Active || !IsServerCharacter || Game.instance == null || Player.m_localPlayer == null || Game.instance.IsShuttingDown()) return;
                Debug.Log("[Characters] the server is shutting down: saving the character now");
                Game.instance.SavePlayerProfile(false);
            }
            catch (Exception ex) { Debug.LogWarning($"[Characters] the shutdown save failed: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
        [HarmonyPostfix]
        private static void SavePlayerToDisk_Postfix(PlayerProfile __instance)
        {
            if (!Active || !IsServerCharacter) return;
            ZNet znet = ZNet.instance;
            if (znet == null || znet.IsServer()) return;

            try
            {
                // Re-serialize the just-saved file via a throwaway profile (no mutation of the active one).
                byte[] bytes = new PlayerProfile(__instance.m_filename, __instance.m_fileSource)
                    .LoadPlayerDataFromDisk()?.GetArray();
                if (bytes == null || bytes.Length == 0) return;

                // After an unclean disconnect (the server stopped, the link dropped), the signed backup the server restores at the
                // next login. Before the server check (0.2.36, R90 runs 2/3): with the server gone there is no peer, and that is
                // exactly when it's needed; it used to sit after the check and was never written. Counts the uploads already sent.
                CharactersEmergencyBackup.TryWriteSidecars(__instance, bytes, s_uploadsSent);

                ZNetPeer server = znet.GetServerPeer();
                if (server == null || !server.IsReady()) return;
                // The logout's own save repeats the one the hold just had confirmed; sent now it would only be cut off. While the hold
                // waits on its upload, any other save (a last-resort one at an early OnApplicationQuit, FGN's) stays on disk.
                if (!s_uploadConfirmed && !(s_holdingFor != null && s_holdUploaded))
                {
                    CharacterProfileTransport.SendToPeer(server, CharactersServer.RpcSave, bytes, null);
                    s_uploadsSent++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] client save upload failed: {ex.Message}");
            }
        }
    }
}
