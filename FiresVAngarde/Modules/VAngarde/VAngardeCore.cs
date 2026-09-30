using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using FiresCore.Bridge;
using FiresCore.Logging;
using VerdantsAscent.Modules.Characters;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// VAngarde server-side anti-cheat core.
    ///
    /// Design principles (learned from OwO bypass analysis of AzuAntiCheat):
    ///  1. Server-authoritative: all decisions happen here, never on the client.
    ///  2. Socket-level disconnect: bypass Harmony patches on ZNet.Disconnect by
    ///     calling peer.m_rpc.GetSocket().Dispose() + removing from m_peers directly.
    ///  3. Timeout enforcement: if the client ignores the challenge RPC, silence = kick.
    ///  4. No client-side booleans/flags: nothing to toggle via reflection.
    ///  5. Server-side logging only: no client-side telemetry to block.
    ///
    /// Flow:
    ///  1. ZNet.OnNewConnection -> VAngardePatches registers our per-peer RPC
    ///  2. After PeerInfo validated -> VAngardeCore.OnPeerValidated sends challenge
    ///  3. Client responds -> VAngardeCore.OnChallengeResponse validates mod list
    ///  4. Periodic timer -> re-challenges connected peers
    ///  5. Timeout or violation -> ForceDisconnect at socket level
    /// </summary>
    public static class VAngardeCore
    {
        // -- RPC names (intentionally non-descriptive to resist reflection scanning) --
        // Using VA_ prefix to match project convention (see PrivateKeyManager.cs)
        internal const string RPC_Challenge  = "VA_vgc";   // server -> client: challenge
        internal const string RPC_Response   = "VA_vgr";   // client -> server: response
        internal const string RPC_Push       = "VA_vgp";   // client -> server: admin push mod list

        // -- Tracked peer state --
        private class PeerState
        {
            public ZNetPeer Peer;
            public string PlatformId;
            public string ChallengeNonce;
            public float ChallengeSentTime;
            public bool Validated;
            public float LastChallengeTime;
            public int ViolationCount;
            /// <summary>Permanent flag: this player is in Valheim's adminlist.txt. Never changes after connect.</summary>
            public bool IsValheimAdmin;
            /// <summary>Toggleable flag: scanner should skip this player. Controlled by AdminBypass config.</summary>
            public bool BypassScanning;
            /// <summary>Client's mod list captured from the most recent challenge response. Used for Discord reporting on kick.</summary>
            public Dictionary<string, string> ClientModList;
            /// <summary>Full contents of the client's BepInEx LogOutput.log, sent with the challenge response. Used for Discord reporting on kick.</summary>
            public byte[] ClientBepInExLog;

            /// <summary>Last cheat/debug commands this peer reported active (admin monitor); cached for detection embeds.</summary>
            public List<string> LastKnownAdminCommands;
            /// <summary>Per-category keys already reported for a monitored admin, so each detection posts to Discord once (not every scan).</summary>
            public HashSet<string> ReportedAdminDetections;
            /// <summary>
            /// Set by the periodic re-challenge loop <b>before</b> sending the challenge,
            /// cleared by <see cref="ProcessCompleteLog"/> after the refreshed log is
            /// cached. While true, the response handler treats the entire exchange as
            /// log-capture-only - no mod-list re-validation, no violation enforcement,
            /// no admin caching - because the periodic re-push exists solely so admins
            /// who request a log later in the session get the UP-TO-DATE contents, not
            /// the frozen snapshot from initial login. Initial validation happened once
            /// on connect and that's where we want it to stay.
            /// </summary>
            public bool LogRefreshOnly;

            /// <summary>Set once a timeout/violation disconnect is scheduled, so the per-frame ServerUpdate
            /// loop doesn't re-queue the same kick every frame while the deferred (Discord-gated) disconnect
            /// is still in flight - that was spamming "Force-disconnecting 0" dozens of times.</summary>
            public bool KickPending;
        }

        // -- Chunked log reassembly state --
        private class ChunkedLogState
        {
            public string Nonce;
            public int TotalChunks;
            public Dictionary<string, string> ModList;
            public byte[] Hash;
            public List<byte[]> Chunks;
            public float FirstChunkTime;
        }

        private static readonly Dictionary<long, ChunkedLogState> _pendingChunkedLogs = new Dictionary<long, ChunkedLogState>();
        private const float ChunkedLogTimeout = 30f; // 30 seconds to receive all chunks

        internal const string RPC_AdminSnapshot = "VA_vgas"; // client -> server: admin cheat-command snapshot

        private static readonly Dictionary<long, PeerState> _trackedPeers = new Dictionary<long, PeerState>();
        private static readonly object _lock = new object();
        private static float _lastPeriodicCheck;
        private static bool _initialized;

        // Persistent violation counts keyed by platformId - survives kick/reconnect
        private static readonly Dictionary<string, int> _violationHistory =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Tracks last known AdminBypass value so we can detect live config changes
        private static bool _lastAdminBypass = true;

        // -- Cached ZNet fields for socket-level disconnect --
        private static FieldInfo _znetPeersField;

        /// <summary>
        /// Initialize VAngarde on ZNet.Awake. Server-only.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            // Cache reflection for m_peers (used for forced disconnect)
            _znetPeersField = typeof(ZNet).GetField("m_peers", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_znetPeersField == null)
                Debug.LogWarning("[VAngarde] Could not find ZNet.m_peers field");

            // Populate the mod-validation lists. Runs in ZNet.Awake postfix, so Chainloader.PluginInfos
            // is the full modset. Three lists, three roles:
            //   exact-match  = the REQUIRED set (client MUST match). AutoUpdateModList ON: regenerated from
            //                  the server's plugins every launch, MINUS anything in mod_serveronly.txt.
            //   whitelist    = allowed EXTRA client mods (hand-curated; NEVER auto-generated from server).
            //   server-only  = server mods excluded from the required set (hand-curated; survives relaunch).
            bool autoUpdate = VAngardeConfig.AutoUpdateModList == null || VAngardeConfig.AutoUpdateModList.Value;
            try
            {
                // Serve the live four-list dump when someone reacts on a Discord list message.
                FiresCore.Bridge.DiscordSink.RegisterServerListSnapshotProvider(VAngardeListNotifier.BuildSnapshotText);

                // Hand-curated lists: create instructional templates once, never overwrite.
                VAngardeModValidator.EnsureServerOnlyTemplate(VAngardeConfig.ServerOnlyPath);
                VAngardeModValidator.EnsureWhitelistTemplate(VAngardeConfig.WhitelistPath);
                VAngardeModValidator.EnsureAdminOnlyTemplate(VAngardeConfig.AdminOnlyPath);
                VAngardeModValidator.EnsureAdminCharacterExceptionsTemplate(VAngardeConfig.AdminCharacterExceptionsPath);

                // Load lists FIRST so the exact-match regen can honor the server-only exclusions.
                VAngardeModValidator.ReloadAll();

                if (autoUpdate)
                {
                    VAngardeModValidator.AutoPopulateExactMatch(VAngardeConfig.ExactMatchPath);
                    VAngardeModValidator.ReloadAll();
                    Debug.Log($"[VAngarde] AutoUpdateModList ON: required(exact-match)={VAngardeModValidator.ExactMatchCount}, " +
                              $"server-only excluded={VAngardeModValidator.ServerOnlyCount}, allowed-extras(whitelist)={VAngardeModValidator.WhitelistCount}.");
                }
                else if (!File.Exists(VAngardeConfig.ExactMatchPath) || VAngardeModValidator.ExactMatchCount == 0)
                {
                    // Curated mode: seed the required set once (or heal a legacy 0-entry stub) so an armed
                    // toggle isn't a silent no-op.
                    VAngardeModValidator.AutoPopulateExactMatch(VAngardeConfig.ExactMatchPath);
                    VAngardeModValidator.ReloadAll();
                }

                // Never let an armed toggle silently do nothing.
                if (VAngardeConfig.UseExactMatch != null && VAngardeConfig.UseExactMatch.Value && VAngardeModValidator.ExactMatchCount == 0)
                    Debug.LogError("[VAngarde] UseExactMatch is ON but the exact-match list is empty - nothing is being enforced.");
                if (VAngardeConfig.UseWhitelist != null && VAngardeConfig.UseWhitelist.Value && VAngardeModValidator.WhitelistCount == 0)
                    Debug.Log("[VAngarde] UseWhitelist is ON with an empty whitelist - clients may run ONLY the required + server-only mods (add allowed client extras to mod_whitelist.txt).");
                if (VAngardeConfig.UseBlacklist != null && VAngardeConfig.UseBlacklist.Value && VAngardeModValidator.BlacklistCount == 0)
                    Debug.Log("[VAngarde] UseBlacklist is ON with 0 blacklist entries (nothing banned yet - add GUIDs to mod_blacklist.txt).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] mod-list init failed: {ex.Message}");
            }

            // Hardening v2 - server-only: generate a per-load session UID used for HMAC-signing
            // every challenge response. Clients receive it (encrypted with the baked AES key) over
            // the dedicated VA_vgs RPC the first time the server validates them.
            if (ZNet.instance != null && ZNet.instance.IsServer())
                VAngardeSession.OnServerStart();

            // Auto-exempt admins when AdminBypass is ON
            _lastAdminBypass = VAngardeConfig.AdminBypass?.Value ?? true;
            if (_lastAdminBypass)
            {
                VAngardeModValidator.MergeAdminExemptions();
            }

            // Set up file watcher for hot-reload of list files
            VAngardeFileWatcher.Initialize();

            _initialized = true;
            LogVerbose("Anti-cheat system initialized (server-side)");
            EmitLoadSummary();
        }

        // One "VANGARDE" box on the dedicated-server console in place of the
        // individual init lines (those are verbose-gated above).
        private static void EmitLoadSummary()
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            try
            {
                LoadSummary.For("FiresVAngarde").EmitMiniBox("\uD83D\uDEE1 VANGARDE", new[]
                {
                    VAngardeConfig.Enabled != null && VAngardeConfig.Enabled.Value
                        ? "anti-cheat live" : "anti-cheat disabled",
                    $"whitelist {VAngardeModValidator.WhitelistCount} mods",
                    VAngardeSession.ServerActive ? "session ok" : "session off",
                    string.IsNullOrEmpty(CharactersConfig.ServerKey?.Value)
                        ? "backup key on connect" : "backup ok",
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] load summary failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the ZNet shuts down. Clean up all state.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                _trackedPeers.Clear();
                _violationHistory.Clear();
            }
            VAngardeFileWatcher.Shutdown();
            _initialized = false;
        }

        /// <summary>
        /// Returns true if VAngarde should be active.
        /// Only runs on the server and only when enabled in config.
        /// </summary>
        public static bool IsActive()
        {
            if (VAngardeConfig.Enabled == null || !VAngardeConfig.Enabled.Value)
                return false;
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return false;
            return true;
        }

        /// <summary>
        /// Returns true if the client-login log-artifact pipeline should run, even when the
        /// full VAngarde anti-cheat is disabled. The admin opts in via
        /// <c>Discord.ClientLogs.NotifyClientLoginArtifacts</c>. This lets servers capture
        /// client logs/modlists + forward to Discord without having to enable mod validation
        /// or behavioral detection.
        /// </summary>
        public static bool IsClientLogCaptureActive()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;
            return FiresCore.Bridge.DiscordSink.NotifyClientLoginArtifacts();
        }

        /// <summary>
        /// True when either full anti-cheat OR the client-log capture pipeline wants the peer
        /// challenge to run. Used to decide whether to register peer state and issue the
        /// initial challenge on connect.
        /// </summary>
        public static bool ShouldChallengePeers()
        {
            return IsActive() || IsClientLogCaptureActive();
        }

        // ==============================================================
        // Peer Lifecycle
        // ==============================================================

        /// <summary>
        /// Called after ZNet has accepted and validated a peer (post-PeerInfo).
        /// Sends the initial challenge.
        /// </summary>
        public static void OnPeerValidated(ZNetPeer peer)
        {
            if (!ShouldChallengePeers()) return;

            string platformId = peer.m_socket.GetHostName();

            // Apply per-character admin exceptions before we read admin status: a real admin playing one of
            // their listed non-admin characters is demoted for this session (removed from the vanilla admin
            // list), so everything below - and every other admin surface - sees them as a normal player.
            VAngardeAdminCharacters.OnPeerConnect(peer);

            // Log-capture-only mode (VAngarde off, but admin wants client logs forwarded).
            // Issue a bypass-scan challenge so the client sends its log+modlist; skip all
            // anti-cheat plumbing (exempt lists, violation history, admin detection).
            if (!IsActive())
            {
                var logOnlyState = new PeerState
                {
                    Peer = peer,
                    PlatformId = platformId,
                    Validated = true,          // no mod validation in this mode
                    ViolationCount = 0,
                    IsValheimAdmin = false,
                    BypassScanning = true       // never trigger behavioral scans
                };
                lock (_lock)
                {
                    _trackedPeers[peer.m_uid] = logOnlyState;
                }
                SendChallenge(logOnlyState);
                return;
            }

            // Determine if this player is in the admin list (for mod-list caching). Character-aware so an
            // admin on one of their non-admin characters is treated as a normal player here too.
            bool isValheimAdmin = VAngardeModValidator.IsAdmin(platformId, peer.m_playerName);

            // Check exempt list - this now includes auto-added admin IDs when AdminBypass is ON
            if (VAngardeModValidator.IsPlayerExempt(platformId))
            {
                LogVerbose($"Player {platformId} is exempt from VAngarde checks" +
                           (isValheimAdmin ? " (admin)" : ""));

                // Even exempt admins get a challenge to cache their mod list
                if (isValheimAdmin)
                {
                    var adminState = new PeerState
                    {
                        Peer = peer,
                        PlatformId = platformId,
                        Validated = true,
                        ViolationCount = 0,
                        IsValheimAdmin = true,
                        BypassScanning = true
                    };
                    lock (_lock)
                    {
                        _trackedPeers[peer.m_uid] = adminState;
                    }
                    SendChallenge(adminState);
                }
                return;
            }

            // Non-exempt player - track and scan normally
            // (admins reach here only when AdminBypass is OFF)
            if (isValheimAdmin)
            {
                LogVerbose($"Player {platformId} is admin - AdminBypass is OFF, will be scanned like normal players");
            }

            // Restore persistent violation count from history (survives kick/reconnect)
            int priorViolations;
            lock (_lock)
            {
                _violationHistory.TryGetValue(platformId, out priorViolations);
            }

            var state = new PeerState
            {
                Peer = peer,
                PlatformId = platformId,
                Validated = false,
                ViolationCount = priorViolations,
                IsValheimAdmin = isValheimAdmin,
                BypassScanning = false // not bypassing, scan them
            };

            lock (_lock)
            {
                _trackedPeers[peer.m_uid] = state;
            }

            // Send challenge if mod validation is enabled, or if they're an admin (for caching)
            if (isValheimAdmin)
            {
                SendChallenge(state);
            }
            else
            {
                // Always challenge all clients so the server has their mod list on file
                SendChallenge(state);
            }
        }

        /// <summary>
        /// Called when a peer disconnects normally. Remove tracking.
        /// </summary>
        public static void OnPeerDisconnected(ZNetPeer peer)
        {
            lock (_lock)
            {
                _trackedPeers.Remove(peer.m_uid);
            }
        }

        // ==============================================================
        // Challenge / Response
        // ==============================================================

        /// <summary>
        /// Send a challenge to a tracked peer.
        /// </summary>
        private static void SendChallenge(PeerState state)
        {
            // Generate a random nonce
            var nonceBytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(nonceBytes);
            }
            state.ChallengeNonce = Convert.ToBase64String(nonceBytes);
            state.ChallengeSentTime = Time.time;
            state.LastChallengeTime = Time.time;

            // Hardening v2 - push the encrypted session UID to this peer BEFORE the challenge,
            // so by the time the client builds its response it has the UID and can sign with it.
            if (VAngardeSession.ServerActive && state.Peer != null && !VAngardeSession.PeerHasUid(state.Peer.m_uid))
                VAngardeSession.SendUidToPeer(state.Peer);

            var pkg = new ZPackage();
            pkg.Write(state.ChallengeNonce);
            pkg.Write(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            try
            {
                state.Peer.m_rpc.Invoke(RPC_Challenge, pkg);
                LogVerbose($"Sent challenge to {state.PlatformId}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to send challenge to {state.PlatformId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Server-side RPC handler: receives the client's challenge response.
        /// Registered on <see cref="ZRoutedRpc"/> rather than per-peer <see cref="ZRpc"/>
        /// so multi-chunk payloads (like the BepInEx log) flow through the routed-RPC
        /// transport - the same one RuntimeSpriteSync uses successfully for 50 MB
        /// sprite syncs. Direct <c>ZRpc.Invoke</c> caused Steam Networking Sockets to
        /// concatenate queued packages into a single message that exceeded the 512 KB
        /// per-message hard cap and triggered an infinite assertion-spam loop.
        /// </summary>
        public static void RPC_OnChallengeResponse(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (!ShouldChallengePeers()) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            s_phaseClock.Restart();
            s_phases.Length = 0;
            try { HandleChallengeResponse(sender, pkg); }
            finally
            {
                // Each response runs inside the server's RPC dispatch, the frame every relayed PvP hit waits on (R34).
                double ms = clock.Elapsed.TotalMilliseconds;
                if (ms >= SlowResponseReportMs)
                {
                    Phase("rest");
                    Debug.Log($"[VAngarde] challenge response from peer {sender} ({pkg.Size() / 1024} KB) held the main thread {ms:0.0} ms ({s_phases}).");
                }
            }
        }

        private const double SlowResponseReportMs = 5.0;

        // Where a slow response's time went (R43: 53.8 ms and 60.1 ms with every HardeningV2 check off). Main thread only.
        private static readonly System.Diagnostics.Stopwatch s_phaseClock = new System.Diagnostics.Stopwatch();
        private static readonly StringBuilder s_phases = new StringBuilder();

        private static void Phase(string name)
        {
            if (s_phases.Length > 0) s_phases.Append(", ");
            s_phases.Append(name).Append(' ').Append(s_phaseClock.Elapsed.TotalMilliseconds.ToString("0.0"));
            s_phaseClock.Restart();
        }

        private static void HandleChallengeResponse(long sender, ZPackage pkg)
        {

            // Find the peer state for this sender id
            PeerState state = null;
            lock (_lock)
            {
                foreach (var kvp in _trackedPeers)
                {
                    if (kvp.Value.Peer != null && kvp.Value.Peer.m_uid == sender)
                    {
                        state = kvp.Value;
                        break;
                    }
                }
            }

            if (state == null)
            {
                LogVerbose($"Received challenge response from unknown peer {sender}");
                return;
            }
            Phase("peer");

            // Global switch (VAngarde inactive = capture-only) OR per-peer override set
            // by the periodic re-challenge loop so those responses only refresh the
            // cached log without triggering re-validation / kicks. See
            // PeerState.LogRefreshOnly for the contract.
            bool logCaptureOnly = !IsActive() || state.LogRefreshOnly;

            try
            {
                // Read response header. All responses are now chunked-format
                // (chunkMarker == -1 followed by totalChunks + chunkIndex). The single-shot
                // inline wire format was removed once SendChunkedLogResponse became the only
                // client-side sender; anything else here is a stale/older-build response and
                // is treated as malformed.
                string echoedNonce = pkg.ReadString();
                int chunkMarker = pkg.ReadInt();
                if (chunkMarker != -1)
                {
                    LogVerbose($"Received non-chunked response from {state.PlatformId} (marker={chunkMarker}); stale client build, ignoring");
                    return;
                }

                int totalChunks = pkg.ReadInt();
                int chunkIndex = pkg.ReadInt();

                if (chunkIndex == 0)
                {
                    // First chunk - contains mod list + hash + first log data
                    int chunkModCount = pkg.ReadInt();
                    var chunkClientMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < chunkModCount && i < 500; i++)
                    {
                        string guid = pkg.ReadString();
                        string version = pkg.ReadString();
                        chunkClientMods[guid] = version;
                    }
                    byte[] chunkClientHash = pkg.ReadByteArray();
                    byte[] firstChunkData = pkg.ReadByteArray();
                    Phase("read");

                    // Verify nonce
                    if (echoedNonce != state.ChallengeNonce)
                    {
                        if (logCaptureOnly)
                        {
                            LogVerbose($"Log-capture-only: nonce mismatch from {state.PlatformId}, ignoring");
                            return;
                        }
                        OnViolation(state, "Challenge nonce mismatch (possible replay attack)");
                        return;
                    }

                    // Consume the nonce so a captured valid response cannot be replayed within the
                    // challenge window: a replay now echoes a nonce the server has already cleared,
                    // failing the check above. The next (re)challenge issues a fresh nonce.
                    state.ChallengeNonce = null;

                    // Verify hash
                    string chunkExpectedHashInput = string.Join("|",
                        chunkClientMods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                  .Select(kv => $"{kv.Key}:{kv.Value}"));
                    byte[] chunkExpectedHash;
                    using (var sha = SHA256.Create())
                    {
                        chunkExpectedHash = sha.ComputeHash(Encoding.UTF8.GetBytes(echoedNonce + "|" + chunkExpectedHashInput));
                    }

                    if (!ByteArraysEqual(chunkClientHash, chunkExpectedHash))
                    {
                        if (logCaptureOnly)
                        {
                            LogVerbose($"Log-capture-only: hash mismatch from {state.PlatformId}, ignoring");
                            return;
                        }
                        OnViolation(state, "Mod list hash mismatch (tampered response)");
                        return;
                    }

                    // Hardening v2 - optional trailing fields appended by newer clients: the
                    // three-source modlist audit + an HMAC over (nonce|hash) keyed by the server
                    // session UID. Both reads are wrapped so old clients (no trailing fields) are
                    // tolerated; whether the absence is itself a violation is decided by the
                    // EnforceModListAudit / EnforceSessionSignature config toggles.
                    VAngardeModListAudit.Snapshot auditSnapshot = null;
                    byte[] sessionSig = null;
                    VAngardeIntegrityCheck.Snapshot integrity = null;
                    try
                    {
                        auditSnapshot = VAngardeModListAudit.Read(pkg, chunkClientMods);
                        sessionSig = pkg.ReadByteArray();
                        integrity = VAngardeIntegrityCheck.Read(pkg);
                    }
                    catch { /* older client - trailing fields absent */ }
                    Phase("verify+trailer");

                    if (!logCaptureOnly && VAngardeConfig.EnforceModListAudit?.Value == true)
                    {
                        if (auditSnapshot == null)
                        {
                            OnViolation(state, "modlist audit: response missing audit snapshot");
                            return;
                        }
                        string lie = VAngardeModListAudit.FindLie(auditSnapshot);
                        if (lie != null) { OnViolation(state, lie); return; }
                    }

                    if (!logCaptureOnly && VAngardeConfig.EnforceSessionSignature?.Value == true)
                    {
                        if (sessionSig == null || sessionSig.Length == 0)
                        {
                            OnViolation(state, "session signature: response missing HMAC");
                            return;
                        }
                        byte[] sigInput = VAngardeSession.ComposeSigInput(echoedNonce, chunkClientHash);
                        if (!VAngardeSession.VerifyServer(sigInput, sessionSig))
                        {
                            OnViolation(state, "session signature: HMAC mismatch (wire-spoofed or unkeyed response)");
                            return;
                        }
                    }

                    if (!logCaptureOnly && VAngardeConfig.EnforceIntegrityCheck?.Value == true)
                    {
                        if (integrity == null) { OnViolation(state, "integrity: response missing tamper bit"); return; }
                        if (integrity.TamperDetected) { OnViolation(state, "integrity: client reports Harmony patches on critical anti-cheat methods"); return; }
                    }

                    if (!logCaptureOnly && VAngardeConfig.EnforceDllHashMatch?.Value == true)
                    {
                        if (integrity == null) { OnViolation(state, "dll hash: no integrity payload"); return; }
                        string dllLie = VAngardeIntegrityCheck.CheckExpectedHashes(integrity, VAngardeModValidator.ExactMatchHashes);
                        if (dllLie != null) { OnViolation(state, dllLie); return; }
                    }

                    Phase("checks");

                    // Store mod list immediately
                    state.ClientModList = new Dictionary<string, string>(chunkClientMods, StringComparer.OrdinalIgnoreCase);

                    // Initialize chunked log state
                    var chunkedState = new ChunkedLogState
                    {
                        Nonce = echoedNonce,
                        TotalChunks = totalChunks,
                        ModList = chunkClientMods,
                        Hash = chunkClientHash,
                        Chunks = new List<byte[]>(totalChunks),
                        FirstChunkTime = Time.time
                    };
                    chunkedState.Chunks.Add(firstChunkData);

                    lock (_lock)
                    {
                        _pendingChunkedLogs[state.Peer.m_uid] = chunkedState;
                    }

                    LogVerbose($"Received first chunk (1/{totalChunks}) from {state.PlatformId}, log size: {firstChunkData.Length} bytes");

                    // Single-chunk fast path: if the client only sent one chunk (small or empty
                    // log), reassemble + process immediately instead of waiting for more chunks
                    // that will never arrive.
                    if (totalChunks <= 1)
                    {
                        lock (_lock)
                        {
                            _pendingChunkedLogs.Remove(state.Peer.m_uid);
                        }
                        var modListCopy = new Dictionary<string, string>(chunkClientMods);
                        ProcessCompleteLog(state, modListCopy, firstChunkData ?? new byte[0], logCaptureOnly);
                    }
                }
                else
                {
                    // Subsequent chunk - just log data
                    byte[] chunkData = pkg.ReadByteArray();
                    Phase("read");

                    ChunkedLogState chunkedState;
                    lock (_lock)
                    {
                        if (!_pendingChunkedLogs.TryGetValue(state.Peer.m_uid, out chunkedState))
                        {
                            Debug.LogWarning($"[VAngarde] Received chunk {chunkIndex} from {state.PlatformId} but no pending chunked log state");
                            return;
                        }
                    }

                    // Verify nonce matches
                    if (echoedNonce != chunkedState.Nonce)
                    {
                        Debug.LogWarning($"[VAngarde] Chunk {chunkIndex} nonce mismatch from {state.PlatformId}");
                        lock (_lock)
                        {
                            _pendingChunkedLogs.Remove(state.Peer.m_uid);
                        }
                        return;
                    }

                    // CRITICAL: Lock during chunk collection to prevent race conditions
                    // Multiple threads could try to add chunks simultaneously
                    lock (_lock)
                    {
                        // Re-check state exists after acquiring lock
                        if (!_pendingChunkedLogs.TryGetValue(state.Peer.m_uid, out chunkedState))
                        {
                            Debug.LogWarning($"[VAngarde] Chunked log state was removed during processing for {state.PlatformId}");
                            return;
                        }

                        chunkedState.Chunks.Add(chunkData);
                        LogVerbose($"Received chunk {chunkIndex + 1}/{chunkedState.TotalChunks} from {state.PlatformId}, size: {chunkData.Length} bytes");

                        // Check if we have all chunks (inside lock to prevent double-processing)
                        if (chunkedState.Chunks.Count >= chunkedState.TotalChunks)
                        {
                            // Reassemble log (still inside lock)
                            int totalSize = chunkedState.Chunks.Sum(c => c.Length);
                            byte[] completeLog = new byte[totalSize];
                            int offset = 0;
                            foreach (var chunk in chunkedState.Chunks)
                            {
                                Array.Copy(chunk, 0, completeLog, offset, chunk.Length);
                                offset += chunk.Length;
                            }

                            Debug.Log($"[VAngarde] Reassembled chunked log from {state.PlatformId}: {completeLog.Length / 1024}KB from {chunkedState.TotalChunks} chunks");
                            Phase("reassemble");

                            // Clean up pending state (already inside lock)
                            _pendingChunkedLogs.Remove(state.Peer.m_uid);

                            // Store data for processing outside lock
                            var modListCopy = new Dictionary<string, string>(chunkedState.ModList);

                            // Process complete log OUTSIDE the lock to avoid blocking other threads
                            // The lock only protects the chunk collection, not the expensive processing
                            ProcessCompleteLog(state, modListCopy, completeLog, logCaptureOnly);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnViolation(state, $"Malformed challenge response: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes a complete log (either from a single response or reassembled from chunks).
        /// </summary>
        private static void ProcessCompleteLog(PeerState state, Dictionary<string, string> clientMods, 
            byte[] logBytes, bool logCaptureOnly)
        {
            try
            {
                state.ClientBepInExLog = logBytes;
                LogVerbose($"Received {logBytes.Length} bytes of client BepInEx log from {state.PlatformId}");

                // Persist to disk so it survives across sessions and can be retrieved on request
                string playerName = state.Peer?.m_playerName ?? "unknown";
                VAngardeModValidator.CacheClientLog(state.PlatformId, playerName, logBytes);
                Phase("cachelog");

                // In log-capture-only mode (VAngarde anti-cheat disabled OR this was a
                // periodic log-refresh re-challenge), we're done: the log is cached.
                // Skip mod validation, admin caching, and violation plumbing entirely.
                // Always clear LogRefreshOnly so the NEXT challenge round starts clean,
                // even if it turns out to be a genuine validation-critical one
                // (e.g. manual re-check triggered elsewhere).
                if (logCaptureOnly)
                {
                    state.LogRefreshOnly = false;
                    LogVerbose($"Log-capture-only mode: processed {state.PlatformId} ({clientMods.Count} mods)");
                    return;
                }

                // Validate mods against lists - detailed so the kick popup lists EVERY mismatch + versions.
                // isAdmin gates the admin-only list: admins may run those mods, regular players may not.
                var problems = VAngardeModValidator.ValidateModListDetailed(clientMods, state.IsValheimAdmin);
                string violation = problems.Count > 0 ? problems[0] : null;

                // One INFO line per login so misconfiguration is never silent: shows which
                // checks are actually armed (and their list sizes) plus the verdict.
                bool blackOn = VAngardeConfig.UseBlacklist?.Value ?? false;
                bool whiteOn = VAngardeConfig.UseWhitelist?.Value ?? false;
                bool exactOn = VAngardeConfig.UseExactMatch?.Value ?? false;
                Debug.Log($"[VAngarde] Mod validation for {state.PlatformId}{(state.IsValheimAdmin ? " (admin)" : "")}: {clientMods.Count} client mods | " +
                          $"blacklist={(blackOn ? $"ON({VAngardeModValidator.BlacklistCount})" : "off")} " +
                          $"whitelist={(whiteOn ? $"ON({VAngardeModValidator.WhitelistCount})" : "off")} " +
                          $"adminonly={VAngardeModValidator.AdminOnlyCount} " +
                          $"exactmatch={(exactOn ? $"ON({VAngardeModValidator.ExactMatchCount})" : "off")} " +
                          $"-> {(violation == null ? "PASS" : $"VIOLATION: {violation}")}");
                Phase("validate");

                // Admins: always cache their mod list. Enforcement depends on AdminBypass -
                // ON = never enforce (but log failures so admin-account testing still shows
                // the check firing); OFF = admins are validated exactly like normal players,
                // as the AdminBypass config description promises.
                if (state.IsValheimAdmin)
                {
                    VAngardeModValidator.CacheAdminModList(state.PlatformId, clientMods);
                    LogVerbose($"Admin {state.PlatformId} mod list cached ({clientMods.Count} mods)");
                    Phase("admincache");

                    // Admins curate the allowed-mods list by connecting: any mod they run that isn't yet
                    // whitelisted becomes allowed for everyone. Server-authoritative - IsValheimAdmin comes
                    // from adminlist.txt, so a client can't spoof its way into approving its own cheat mod.
                    if (VAngardeConfig.AdminModsExtendWhitelist == null || VAngardeConfig.AdminModsExtendWhitelist.Value)
                        VAngardeModValidator.ExtendWhitelistFromAdmin(state.PlatformId, clientMods);
                    Phase("whitelist");

                    // Re-check against the now-extended lists. Membership (whitelist) violations are gone;
                    // anything still failing is the exact-match VERSION authority (wrong/missing version) or a
                    // banned mod - and admins are NOT exempt from those, no matter what AdminBypass is set to
                    // (AdminBypass only exempts behavioral scans).
                    problems = VAngardeModValidator.ValidateModListDetailed(clientMods, isAdmin: true);
                    violation = problems.Count > 0 ? problems[0] : null;

                    if (violation == null)
                    {
                        state.Validated = true;
                        LogVerbose($"Admin {state.PlatformId} passed ({clientMods.Count} mods; whitelist now {VAngardeModValidator.WhitelistCount})");
                        return;
                    }

                    Debug.Log($"[VAngarde] Admin {state.PlatformId} kicked by the exact-match version authority: {violation}");
                    var adminPopup = new List<string>
                    {
                        $"Fix the {problems.Count} tagged mod(s), then reconnect (exact-match versions are enforced for admins too):"
                    };
                    adminPopup.AddRange(VAngardeModValidator.BuildModComparison(clientMods, isAdmin: true));
                    OnViolation(state, violation, adminPopup, bypassMonitoredAdmin: true);
                    return;
                }

                if (violation != null)
                {
                    // One-stop fix list for the disconnect popup: a header + every mismatch with versions.
                    var popupLines = new List<string>
                    {
                        $"Fix the {problems.Count} tagged mod(s), then reconnect:"
                    };
                    popupLines.AddRange(VAngardeModValidator.BuildModComparison(clientMods));
                    OnViolation(state, violation, popupLines);
                    return;
                }

                // Passed!
                state.Validated = true;
                LogVerbose($"Player {state.PlatformId} passed mod validation ({clientMods.Count} mods)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] ProcessCompleteLog failed for {state.PlatformId}: {ex.Message}");
            }
        }

        // ==============================================================
        // Client-Side Response Builder (static helper for VAngardePatches)
        // ==============================================================

        /// <summary>
        /// Called on the CLIENT when the server sends a challenge.
        /// Builds and sends the response (mod list + hash + BepInEx log).
        ///
        /// Transport: <see cref="ZRoutedRpc.InvokeRoutedRPC(long, string, object[])"/>
        /// targeted at <see cref="ZRoutedRpc.GetServerPeerID"/>. We do NOT use the
        /// per-peer <c>ZRpc.Invoke</c> here because the underlying Steam socket layer
        /// concatenates queued packages into a single SendMessageToConnection call,
        /// blowing past the 512 KB hard cap when a chunked log is in flight. The routed
        /// transport is the same one RuntimeSpriteSync uses to ship 50 MB sprite files.
        ///
        /// The log file read is dispatched to a worker thread so the Unity main thread
        /// never stalls on multi-megabyte disk I/O during a login; the RPC is still
        /// invoked on the main thread from the completion callback.
        /// </summary>
        public static void HandleServerChallenge(ZRpc rpc, ZPackage pkg)
        {
            if (pkg == null) return;

            try
            {
                string nonce = pkg.ReadString();
                long timestamp = pkg.ReadLong();

                // Enumerate loaded BepInEx plugins (shared helper - see ClientLogRelay/Transport)
                var modList = FiresCore.ClientLogRelay.Transport
                    .ClientLogCollector.BuildLocalModList();

                var sorted = modList.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();

                // Compute hash: nonce + sorted mod data
                string hashInput = string.Join("|", sorted.Select(kv => $"{kv.Key}:{kv.Value}"));
                byte[] hash;
                using (var sha = SHA256.Create())
                {
                    hash = sha.ComputeHash(Encoding.UTF8.GetBytes(nonce + "|" + hashInput));
                }

                // Capture the pieces the callback needs; the log read and the plugin hashing both
                // happen off-thread, in parallel.
                var nonceCapture  = nonce;
                var sortedCapture = sorted;
                var hashCapture   = hash;
                var pluginHashing = VAngardeIntegrityCheck.ComputePluginHashesInBackground();
                // The audit walks every assembly and the whole plugins tree (a 1 GB folder on a big modpack): a worker, not the RPC.
                var auditCapture = Task.Run(() => VAngardeModListAudit.Capture());

                FiresCore.ClientLogRelay.Transport.ClientLogCollector
                    .ReadLocalBepInExLogAsync(logData =>
                    {
                        try
                        {
                            byte[] safeLogData = logData ?? new byte[0];

                            // The routed-RPC server peer id isn't wired yet when this callback fires during
                            // early login (GetServerPeerID() == 0). Don't drop the response - that left the
                            // server waiting for a reply that never came, then kicking on timeout. Poll for
                            // routing to come up, then send. (Always chunked, even for a 1-chunk payload:
                            // ProcessCompleteLog fires after the first chunk lands, one wire format.)
                            FiresCore.Async.MainThreadDispatcher.StartRoutine(
                                SendResponseWhenRouted(nonceCapture, sortedCapture, hashCapture, safeLogData, pluginHashing, auditCapture));
                        }
                        catch (Exception cbEx)
                        {
                            Debug.LogWarning($"[VAngarde] Failed to send challenge response: {cbEx.Message}");
                        }
                    });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to respond to server challenge: {ex.Message}");
            }
        }

        /// <summary>
        /// Send the challenge response once the routed-RPC server peer id exists. During early login the
        /// client's ZRoutedRpc isn't wired to the server yet (GetServerPeerID() == 0), so poll for it rather
        /// than dropping the response and getting kicked on the server's challenge timeout.
        /// </summary>
        private static IEnumerator SendResponseWhenRouted(string nonce,
            List<KeyValuePair<string, string>> modList, byte[] hash, byte[] logData,
            Task<Dictionary<string, string>> pluginHashing, Task<VAngardeModListAudit.Snapshot> auditCapture)
        {
            while (!pluginHashing.IsCompleted || !auditCapture.IsCompleted) yield return null;
            var pluginHashes = VAngardeIntegrityCheck.CompletedHashesOrEmpty(pluginHashing);
            var audit = auditCapture.Status == TaskStatus.RanToCompletion && auditCapture.Result != null
                ? auditCapture.Result
                : VAngardeModListAudit.Capture();

            float deadline = Time.time + 60f;
            while (true)
            {
                long serverPeerId = ZRoutedRpc.instance != null ? ZRoutedRpc.instance.GetServerPeerID() : 0L;
                if (serverPeerId != 0L)
                {
                    try { SendChunkedLogResponse(serverPeerId, nonce, modList, hash, logData, pluginHashes, audit); }
                    catch (Exception ex) { Debug.LogWarning($"[VAngarde] Failed to send challenge response: {ex.Message}"); }
                    yield break;
                }
                if (Time.time > deadline)
                {
                    Debug.LogWarning("[VAngarde] Challenge response abandoned: routed-RPC server peer id never became available");
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        // 200 KB chunks.
        //
        // HISTORY: Started at 400 KB to match RuntimeSpriteSync. That was fine until
        // `yield return null` in PacedChunkedSend turned out NOT to guarantee a
        // ZSteamSocket.SendQueuedPackages flush between chunks - if our coroutine
        // runs AFTER the socket update inside the same Unity frame, chunk N is
        // queued but unsent; we yield, resume next frame, and queue chunk N+1 while
        // chunk N is still pending. Valheim's socket then concatenates both into a
        // single SteamNetworkingSockets.SendMessageToConnection call which trips
        // the 512 KB hard cap ("Message size 683789 is too big" assertion cascade
        // that kept spamming an hour into play, when the client log had grown past
        // 400 KB and the second chunk carried the tail).
        //
        // Halving to 200 KB means the worst-case two-chunk concatenation is 400 KB,
        // comfortably below Steam's 524288 B ceiling with headroom for mod-list +
        // headers on chunk 0.
        private const int ResponseChunkSize = 200 * 1024;

        /// <summary>
        /// Sends a large response (mod list + hash + log) in chunks via
        /// <see cref="ZRoutedRpc.InvokeRoutedRPC(long, string, object[])"/>. Wire format
        /// matches the existing server-side chunked-receiver path: first chunk carries
        /// nonce + chunkMarker(-1) + totalChunks + chunkIndex(0) + modList + hash + first
        /// log slice; subsequent chunks carry nonce + chunkMarker(-1) + totalChunks +
        /// chunkIndex + log slice.
        ///
        /// TRANSPORT SAFETY: the dispatch runs through <see cref="SafeRoutedRpc.PacedChunkedSend"/>
        /// which yields a full frame between chunks. That prevents
        /// <c>ZSteamSocket.SendQueuedPackages</c> from concatenating several chunked
        /// packages into a single Steam message that would exceed the 512 KB hard cap
        /// (the historical "Message size NNN is too big" assertion cascade). Every
        /// per-chunk <see cref="ZPackage"/> is also size-checked by <see cref="SafeRoutedRpc.InvokeSafe"/>
        /// before it reaches Steam, so a bloated first chunk (huge mod list) fails fast
        /// instead of crashing the transport.
        /// </summary>
        private static void SendChunkedLogResponse(long serverPeerId, string nonce,
            List<KeyValuePair<string, string>> modList, byte[] hash, byte[] logData,
            Dictionary<string, string> pluginHashes, VAngardeModListAudit.Snapshot audit)
        {
            byte[] logPayload = logData ?? Array.Empty<byte>();
            int totalChunks = Math.Max(1, (int)Math.Ceiling((double)logPayload.Length / ResponseChunkSize));
            Debug.Log($"[VAngarde] Sending challenge response with log ({logPayload.Length / 1024}KB) in {totalChunks} paced routed chunks of {ResponseChunkSize / 1024}KB each");

            // Snapshot header fields so the per-chunk closure stays pure (no shared mutable state).
            string nonceCapture = nonce;
            List<KeyValuePair<string, string>> modListCapture = modList ?? new List<KeyValuePair<string, string>>();
            byte[] hashCapture = hash ?? Array.Empty<byte>();

            IEnumerator routine = SafeRoutedRpc.PacedChunkedSend(
                serverPeerId,
                RPC_Response,
                logPayload,
                ResponseChunkSize,
                (pkg, chunkIndex, chunksTotal, slice) =>
                {
                    pkg.Write(nonceCapture);
                    pkg.Write(-1);            // chunked-response marker (matches legacy wire format)
                    pkg.Write(chunksTotal);
                    pkg.Write(chunkIndex);

                    if (chunkIndex == 0)
                    {
                        // First chunk also carries mod list + hash so the server can validate
                        // even before the full log has arrived.
                        pkg.Write(modListCapture.Count);
                        foreach (var mod in modListCapture)
                        {
                            pkg.Write(mod.Key);
                            pkg.Write(mod.Value);
                        }
                        pkg.Write(hashCapture);
                        pkg.Write(slice);

                        // Hardening v2 - append the three-source modlist audit + an HMAC signature
                        // over (nonce|hash) computed with the server's session UID. Server reads
                        // these in RPC_OnChallengeResponse and enforces depending on
                        // EnforceModListAudit / EnforceSessionSignature config toggles. Old servers
                        // that don't read these fields ignore them harmlessly.
                        VAngardeModListAudit.Write(pkg, audit ?? VAngardeModListAudit.Capture());
                        byte[] sig = VAngardeSession.SignClient(
                            VAngardeSession.ComposeSigInput(nonceCapture, hashCapture)) ?? Array.Empty<byte>();
                        pkg.Write(sig);

                        // Phase 5b - tamper bit + DLL content hashes. Tamper bit asks Harmony if any
                        // non-trusted owner has patched our critical methods (Capture/Sign/etc.); DLL
                        // hashes let the server's exact-match list pin a sha256 per plugin to catch
                        // same-GUID-different-binary attacks.
                        bool tampered = VAngardeIntegrityCheck.DetectHarmonyTampering(out _);
                        VAngardeIntegrityCheck.Write(pkg, pluginHashes, tampered);
                    }
                    else
                    {
                        pkg.Write(slice);
                    }
                },
                onComplete: sentChunks =>
                {
                    if (sentChunks < totalChunks)
                    {
                        Debug.LogWarning($"[VAngarde] Chunked log send aborted early: {sentChunks}/{totalChunks} chunks delivered.");
                    }
                });

            // Must run on the main thread - routed RPC dispatch touches Unity/ZNet state.
            // Routing through MainThreadDispatcher makes this callable from the off-thread
            // log-read completion callback without crashing.
            MainThreadDispatcher.Enqueue(() =>
                MainThreadDispatcher.StartRoutine(routine));
        }

        // ---------------------------------------------------------------------------
        // Chunked log send via ZRoutedRpc (RESTORED 2026-04 - see HandleServerChallenge)
        // ---------------------------------------------------------------------------
        // The client used to invoke RPC_Response directly via per-peer ZRpc, which sent
        // packages through ZSteamSocket's send queue. ZSteamSocket concatenates every
        // pending package in its queue into a single SteamNetworkingSockets
        // .SendMessageToConnection call, exceeding the 512 KB per-message hard cap as
        // soon as more than ~3 chunks were queued in a frame, and triggering an
        // infinite assertion-spam loop on the client. The fix is to route the response
        // through ZRoutedRpc.InvokeRoutedRPC instead - the same transport
        // RuntimeSpriteSync uses to ship 50 MB sprite files at 400 KB per chunk.
        // ---------------------------------------------------------------------------

        // ReadClientBepInExLog moved to
        // FiresCore.ClientLogRelay.Transport.ClientLogCollector.ReadLocalBepInExLog()

        // ==============================================================
        // Admin Mod List Push (client -> server)
        // ==============================================================

        /// <summary>
        /// Called on the CLIENT by an admin to push their local mod list
        /// to the server as the new whitelist + exact match override.
        /// </summary>
        public static void ClientPushModList()
        {
            if (ZNet.instance == null || ZNet.instance.IsServer())
            {
                Debug.LogWarning("[VAngarde] Push must be run from a client, not the server");
                return;
            }

            try
            {
                var modList = FiresCore.ClientLogRelay.Transport
                    .ClientLogCollector.BuildLocalModList();

                var pkg = new ZPackage();
                var sorted = modList.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
                pkg.Write(sorted.Count);
                foreach (var mod in sorted)
                {
                    pkg.Write(mod.Key);
                    pkg.Write(mod.Value);
                }

                // Send to server via ZRoutedRpc (server = ID 0)
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(),
                    RPC_Push, pkg);

                Debug.Log($"[VAngarde] Pushed {sorted.Count} mods to server as whitelist/exact match override");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to push mod list: {ex.Message}");
            }
        }

        /// <summary>
        /// Server-side RPC handler: receives an admin's mod list push.
        /// Verifies the sender is admin, then overwrites the whitelist and exact match files.
        /// </summary>
        public static void RPC_OnAdminPushModList(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (pkg == null) return;

            // Verify sender is admin
            if (!AdminSyncing.IsAdmin(sender))
            {
                Debug.LogWarning($"[VAngarde] Non-admin {sender} attempted to push mod list. Rejected.");
                return;
            }

            try
            {
                int modCount = pkg.ReadInt();
                var mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < modCount && i < 500; i++)
                {
                    string guid = pkg.ReadString();
                    string version = pkg.ReadString();
                    mods[guid] = version;
                }

                // Get the admin's platform ID for logging
                string platformId = "Unknown";
                var peer = ZNet.instance.GetPeer(sender);
                if (peer != null)
                    platformId = peer.m_socket.GetHostName();

                Debug.Log($"[VAngarde] Admin {platformId} pushed {mods.Count} mods as server override");

                // Overwrite whitelist and exact match files
                VAngardeModValidator.OverwriteListsFromAdmin(platformId, mods);

                // Hot-reload the lists so they take effect immediately
                VAngardeModValidator.ReloadAll();

                // Re-merge admin exemptions in case the reload cleared them
                if (VAngardeConfig.AdminBypass?.Value ?? true)
                    VAngardeModValidator.MergeAdminExemptions();

                Debug.Log($"[VAngarde] Server whitelist and exact match updated from admin {platformId} ({mods.Count} mods)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to process admin mod list push: {ex.Message}");
            }
        }

        // ==============================================================
        // Periodic Update (called from VAngardePatches.ZNet_Update)
        // ==============================================================

        /// <summary>
        /// Called each server frame to check timeouts and send periodic re-challenges.
        /// Also cleans up stale chunked log reassembly state.
        /// </summary>
        public static void ServerUpdate()
        {
            if (!IsActive()) return;

            // Detect live AdminBypass config changes and update accordingly
            bool currentBypass = VAngardeConfig.AdminBypass?.Value ?? true;
            if (currentBypass != _lastAdminBypass)
            {
                _lastAdminBypass = currentBypass;
                OnAdminBypassChanged(currentBypass);
            }

            float now = Time.time;
            float timeout = VAngardeConfig.ChallengeTimeoutSeconds?.Value ?? 15f;
            float recheckInterval = (VAngardeConfig.PeriodicRecheckMinutes?.Value ?? 5f) * 60f;

            List<PeerState> toDisconnect = null;
            List<long> staleChunkedLogs = null;

            lock (_lock)
            {
                // Clean up stale chunked log reassembly state
                foreach (var kvp in _pendingChunkedLogs)
                {
                    if (now - kvp.Value.FirstChunkTime > ChunkedLogTimeout)
                    {
                        if (staleChunkedLogs == null) staleChunkedLogs = new List<long>();
                        staleChunkedLogs.Add(kvp.Key);
                    }
                }

                if (staleChunkedLogs != null)
                {
                    foreach (var peerUid in staleChunkedLogs)
                    {
                        var chunkedState = _pendingChunkedLogs[peerUid];
                        Debug.LogWarning($"[VAngarde] Chunked log timeout for peer {peerUid}: received {chunkedState.Chunks.Count}/{chunkedState.TotalChunks} chunks");
                        _pendingChunkedLogs.Remove(peerUid);
                    }
                }

                foreach (var kvp in _trackedPeers)
                {
                    var state = kvp.Value;

                    // Check timeout on pending challenges (skip peers already scheduled for a kick so the
                    // per-frame loop doesn't re-queue the same disconnect while it defers on the Discord upload).
                    if (!state.Validated && !state.KickPending && state.ChallengeSentTime > 0)
                    {
                        if (now - state.ChallengeSentTime > timeout)
                        {
                            state.KickPending = true;
                            Debug.Log($"[VAngarde] Player {state.PlatformId} failed to respond to challenge (timeout {timeout}s)");
                            if (toDisconnect == null) toDisconnect = new List<PeerState>();
                            toDisconnect.Add(state);
                            continue;
                        }
                    }

                    // Periodic LOG-REFRESH re-challenge for validated peers.
                    //
                    // Purpose: keep the server's cached copy of each peer's BepInEx log
                    // fresh so a `vangarde log <steamid>` command (or a Discord reaction
                    // request) always returns the CURRENT log, not the frozen snapshot
                    // captured at initial login.
                    //
                    // What we explicitly do NOT do on the periodic path:
                    //  * Re-validate the mod list (initial login handled that once)
                    //  * Mark the peer unvalidated / eligible for kick
                    // Both are suppressed by PeerState.LogRefreshOnly, which is set here
                    // before SendChallenge and cleared by ProcessCompleteLog. While set,
                    // the response handler takes the capture-only branch (see
                    // RPC_OnChallengeResponse).
                    //
                    // The existing admin / EnforceModList gates are preserved so the
                    // periodic refresh still honours configuration: only fire when the
                    // peer is an admin whose modlist we care to cache, or when
                    // EnforceModList is on (which is the usual "log collection enabled"
                    // signal). Peers outside those gates get no periodic re-challenge,
                    // same as before.
                    //
                    // TRANSPORT SAFETY: response chunks travel through
                    // SendChunkedLogResponse -> SafeRoutedRpc.PacedChunkedSend with
                    // 200 KB chunks + two-frame yields, so logs that have grown past
                    // 400 KB mid-session cannot concatenate past Steam's 524288 B cap.
                    if (state.Validated && recheckInterval > 0 && now - state.LastChallengeTime > recheckInterval)
                    {
                        if (state.IsValheimAdmin)
                        {
                            state.LogRefreshOnly = true;
                            SendChallenge(state);
                        }
                        else if (VAngardeConfig.EnforceModList != null && VAngardeConfig.EnforceModList.Value)
                        {
                            state.LogRefreshOnly = true;
                            SendChallenge(state);
                        }
                    }
                }
            }

            // Disconnect timed-out peers outside the lock.
            // Defer actual disconnects until Discord webhook uploads complete.
            if (toDisconnect != null)
            {
                foreach (var state in toDisconnect)
                {
                    string playerName = state.Peer?.m_playerName ?? "Unknown";
                    string reason = "Challenge timeout - client did not respond";

                    // Notify Discord of the timeout kick (mod list/log may be null if client never responded)
                    DiscordSink.OnAntiCheatViolation(playerName, state.PlatformId, reason, state.ViolationCount + 1);

                    var peerToKick = state.Peer;
                    var peerUid = state.Peer.m_uid;

                    // Tell the client WHY before the disconnect (shown via FiresConnectReasonPanel).
                    FiresCore.Bridge.FiresConnectReason.Send(peerToKick?.m_rpc, "\uD83D\uDEE1 Disconnected by VAngarde anti-cheat", new[] { reason });

                    DiscordSink.OnAntiCheatKick(playerName, state.PlatformId, reason,
                        state.ClientModList, state.ClientBepInExLog,
                        onComplete: () =>
                        {
                            ForceDisconnect(peerToKick, reason);
                            lock (_lock)
                            {
                                _trackedPeers.Remove(peerUid);
                            }
                        });
                }
            }

            // Run behavioral scan
            if (now - _lastPeriodicCheck > 2f) // every 2 seconds
            {
                _lastPeriodicCheck = now;
                VAngardeCheatScanner.ScanAll();
            }
        }

        // ==============================================================
        // Violation Handling
        // ==============================================================

        private static void OnViolation(PeerState state, string reason, IReadOnlyList<string> detailLines = null, bool bypassMonitoredAdmin = false)
        {
            state.ViolationCount++;

            // Persist to history so the count survives kick/reconnect
            lock (_lock)
            {
                _violationHistory[state.PlatformId] = state.ViolationCount;
            }

            // Monitored admin (AdminBypass + AdminMonitoring on): detect-but-don't-kick for BEHAVIORAL
            // signals. Post to the admin Discord log ONCE per category, then stop. bypassMonitoredAdmin
            // overrides this for the mod-version authority: an admin with a wrong/missing version IS kicked.
            if (!bypassMonitoredAdmin && IsMonitoredAdminState(state))
            {
                string key = reason.Contains(":") ? reason.Substring(0, reason.IndexOf(':')) : reason;
                bool firstTime;
                int categoryCount;
                List<string> adminCmds;
                lock (_lock)
                {
                    if (state.ReportedAdminDetections == null)
                        state.ReportedAdminDetections = new HashSet<string>(StringComparer.Ordinal);
                    firstTime = state.ReportedAdminDetections.Add(key);
                    categoryCount = state.ReportedAdminDetections.Count;
                    adminCmds = state.LastKnownAdminCommands;
                }
                if (firstTime)
                {
                    // categoryCount = number of distinct cheat categories caught for this admin this session
                    // (ViolationCount inflates every scan with no kick to stop it, so it isn't reported here).
                    Debug.Log($"[VAngarde] ADMIN-MONITOR detection: {state.PlatformId} - {reason} (category #{categoryCount})");
                    DiscordSink.OnAdminCheatDetected(state.Peer?.m_playerName ?? "Unknown", state.PlatformId,
                        reason, categoryCount, adminCmds);
                }
                return;
            }

            Debug.Log($"[VAngarde] VIOLATION: Player {state.PlatformId} - {reason} (count: {state.ViolationCount})");

            // Notify Discord of the violation (even in log-only mode)
            string playerName = state.Peer?.m_playerName ?? "Unknown";
            DiscordSink.OnAntiCheatViolation(playerName, state.PlatformId, reason, state.ViolationCount);

            string action = VAngardeConfig.ViolationAction?.Value ?? "kick";
            if (action.Equals("log", StringComparison.OrdinalIgnoreCase))
            {
                // Log-only mode - don't disconnect
                return;
            }

            // Notify Discord of the kick - defer the actual disconnect until the webhook
            // upload completes so the mod list and client BepInEx log reach Discord first.
            var peerToKick = state.Peer;
            var peerUid = state.Peer.m_uid;
            string kickReason = reason;

            // Tell the client WHY before the disconnect (shown via FiresConnectReasonPanel). Prefer the
            // full mismatch list (install/update/remove + versions) when we have it, else the one-liner.
            IEnumerable<string> kickLines = (detailLines != null && detailLines.Count > 0)
                ? detailLines : new[] { reason };
            FiresCore.Bridge.FiresConnectReason.Send(peerToKick?.m_rpc, "\uD83D\uDEE1 Disconnected by VAngarde anti-cheat", kickLines);

            DiscordSink.OnAntiCheatKick(playerName, state.PlatformId, reason,
                state.ClientModList, state.ClientBepInExLog,
                onComplete: () =>
                {
                    ForceDisconnect(peerToKick, kickReason);
                    lock (_lock)
                    {
                        _trackedPeers.Remove(peerUid);
                    }
                });
        }

        /// <summary>
        /// Report a behavioral violation from VAngardeCheatScanner.
        /// </summary>
        internal static void ReportBehavioralViolation(ZNetPeer peer, string reason)
        {
            PeerState state = null;
            lock (_lock)
            {
                _trackedPeers.TryGetValue(peer.m_uid, out state);
            }

            if (state != null)
            {
                OnViolation(state, reason);
            }
            else
            {
                // Peer not tracked (exempt or admin) - just log
                Debug.Log($"[VAngarde] Behavioral alert for untracked peer {peer.m_socket.GetHostName()}: {reason}");
            }
        }

        /// <summary>
        /// Report a fakeable / heuristic suspicion (e.g. god-mode near-death survival read from the
        /// client-owned health ZDO) for human review. Logs + notifies Discord but NEVER kicks - these
        /// signals read client-authored data and are not reliable enough to auto-enforce.
        /// </summary>
        internal static void ReportSuspicionLogOnly(ZNetPeer peer, string reason)
        {
            string playerName = peer?.m_playerName ?? "Unknown";
            string platformId = peer?.m_socket?.GetHostName() ?? "?";
            Debug.Log($"[VAngarde] SUSPICION (log-only, no kick): {playerName} - {reason}");
            try { DiscordSink.OnAntiCheatViolation(playerName, platformId, reason + " [log-only]", 0); } catch { }
        }

        /// <summary>
        /// Called when the cheat scanner has definitively confirmed god mode
        /// (player survived a 1-damage poke at minimum health).
        /// This always kicks regardless of ViolationAction setting because the
        /// detection is confirmed, not heuristic.
        /// </summary>
        internal static void ReportGodModeConfirmed(ZNetPeer peer, string reason)
        {
            PeerState state = null;
            lock (_lock)
            {
                _trackedPeers.TryGetValue(peer.m_uid, out state);
            }

            if (state == null)
            {
                Debug.Log($"[VAngarde] God mode confirmed for untracked peer {peer.m_socket.GetHostName()}: {reason}");
                return;
            }

            // Safety invariant: a monitored admin is never kicked. The scanner suppresses the lethal
            // god-mode poke for admins, so this confirmed path is normally unreachable for them - this
            // is the backstop that guarantees no admin is ever disconnected by VAngarde.
            if (IsMonitoredAdminState(state)) return;

            state.ViolationCount++;

            // Persist to history
            lock (_lock)
            {
                _violationHistory[state.PlatformId] = state.ViolationCount;
            }

            Debug.Log($"[VAngarde] GOD MODE KICK: Player {state.PlatformId} - {reason} (violations: {state.ViolationCount})");

            // Notify Discord - violation
            string playerName = state.Peer?.m_playerName ?? "Unknown";
            DiscordSink.OnAntiCheatViolation(playerName, state.PlatformId, reason, state.ViolationCount);

            // Notify Discord - kick (god mode confirmed = always kick, even in log-only mode)
            // Include client mod list and BepInEx log for debugging.
            // Defer the actual disconnect until the webhook upload completes.
            var peerToKick = state.Peer;
            var peerUid = state.Peer.m_uid;
            string kickReason = reason;

            // Tell the client WHY before the disconnect (shown via FiresConnectReasonPanel).
            FiresCore.Bridge.FiresConnectReason.Send(peerToKick?.m_rpc, "\uD83D\uDEE1 Disconnected by VAngarde anti-cheat", new[] { reason });

            DiscordSink.OnAntiCheatKick(playerName, state.PlatformId, reason,
                state.ClientModList, state.ClientBepInExLog,
                onComplete: () =>
                {
                    ForceDisconnect(peerToKick, kickReason);
                    lock (_lock)
                    {
                        _trackedPeers.Remove(peerUid);
                    }
                });
        }

        // ==============================================================
        // Socket-Level Forced Disconnect
        // ==============================================================

        /// <summary>
        /// Force-disconnect a peer at the socket level.
        ///
        /// Why not use ZNet.Disconnect? Because OwO patches ZNet.Disconnect
        /// with a prefix that returns false, preventing the disconnect.
        /// We bypass this entirely by going straight to the socket.
        /// </summary>
        public static void ForceDisconnect(ZNetPeer peer, string reason)
        {
            if (peer == null || ZNet.instance == null) return;

            Debug.Log($"[VAngarde] Force-disconnecting {peer.m_socket.GetHostName()}: {reason}");

            try
            {
                // Step 1: Dispose the socket (closes Steam connection)
                peer.m_rpc.GetSocket().Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Socket dispose error: {ex.Message}");
            }

            try
            {
                // Step 2: Remove from ZNet.m_peers via reflection
                // This bypasses any Harmony prefix on ZNet.Disconnect
                if (_znetPeersField != null)
                {
                    var peers = _znetPeersField.GetValue(ZNet.instance) as System.Collections.IList;
                    if (peers != null)
                    {
                        for (int i = peers.Count - 1; i >= 0; i--)
                        {
                            if (peers[i] is ZNetPeer p && p.m_uid == peer.m_uid)
                            {
                                peers.RemoveAt(i);
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to remove peer from m_peers: {ex.Message}");
            }

            try
            {
                // Step 3: Clean up routing/ZDO data
                ZNet.instance.m_routedRpc?.RemovePeer(peer);
                ZNet.instance.m_zdoMan?.RemovePeer(peer);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Cleanup error: {ex.Message}");
            }
        }

        // ==============================================================
        // Admin Bypass Live Toggle
        // ==============================================================

        /// <summary>
        /// Called when the AdminBypass config value changes at runtime.
        /// Updates the exempt list and re-evaluates all tracked admin peers.
        /// </summary>
        private static void OnAdminBypassChanged(bool bypassEnabled)
        {
            if (bypassEnabled)
            {
                // Admins are now exempt - add them to the exempt set
                VAngardeModValidator.MergeAdminExemptions();

                // Update tracked peers: enable scanning bypass for admin peers
                lock (_lock)
                {
                    foreach (var kvp in _trackedPeers)
                    {
                        var state = kvp.Value;
                        if (state.IsValheimAdmin)
                        {
                            state.BypassScanning = true;
                            state.Validated = true;
                            LogVerbose($"AdminBypass ON: {state.PlatformId} now exempt from scanning");
                        }
                    }
                }
            }
            else
            {
                // Admins are no longer exempt - remove auto-added exemptions
                VAngardeModValidator.RemoveAdminExemptions();

                // Update tracked peers: disable scanning bypass so scanner picks up admins.
                // BUT if a peer is still in the exempt list (manually added to exempt_players.txt),
                // keep their bypass - only remove bypass for admins who were auto-exempted.
                lock (_lock)
                {
                    foreach (var kvp in _trackedPeers)
                    {
                        var state = kvp.Value;
                        if (state.IsValheimAdmin && state.BypassScanning)
                        {
                            if (VAngardeModValidator.IsPlayerExempt(state.PlatformId))
                            {
                                LogVerbose($"AdminBypass OFF: {state.PlatformId} stays exempt (manually in exempt_players list)");
                                continue;
                            }

                            state.BypassScanning = false;
                            LogVerbose($"AdminBypass OFF: {state.PlatformId} now subject to scanning");
                        }
                    }
                }
            }

            Debug.Log($"[VAngarde] AdminBypass toggled to {bypassEnabled} - updated all tracked peers");
        }

        // ==============================================================
        // Utilities
        // ==============================================================

        /// <summary>
        /// Get tracked peer state by peer UID (for cheat scanner).
        /// Returns false for peers with BypassScanning=true (exempt admins).
        /// </summary>
        internal static bool IsPeerTracked(long uid)
        {
            lock (_lock)
            {
                PeerState state;
                if (!_trackedPeers.TryGetValue(uid, out state))
                    return false;
                return !state.BypassScanning;
            }
        }

        /// <summary>True if the peer should be SCANNED: a normal enforced peer, OR a monitored admin.</summary>
        internal static bool IsPeerScanned(long uid)
        {
            lock (_lock)
            {
                if (!_trackedPeers.TryGetValue(uid, out var state)) return false;
                if (!state.BypassScanning) return true;
                return IsMonitoredAdminState(state);
            }
        }

        /// <summary>Admin in monitor mode: scanned + logged, never kicked (AdminBypass + AdminMonitoring on).</summary>
        private static bool IsMonitoredAdminState(PeerState state)
        {
            return state != null && state.BypassScanning && state.IsValheimAdmin
                && (VAngardeConfig.AdminBypass?.Value ?? true)
                && (VAngardeConfig.AdminMonitoring?.Value ?? true);
        }

        /// <summary>True if the peer is an admin being monitored (scanned + logged, never kicked/poked).</summary>
        internal static bool IsMonitoredAdmin(long uid)
        {
            lock (_lock)
            {
                _trackedPeers.TryGetValue(uid, out var state);
                return IsMonitoredAdminState(state);
            }
        }

        /// <summary>
        /// Server RPC: a client reported its active cheat/debug commands (admin monitor). The admin
        /// gate is server-authoritative - we resolve the PeerState from the routed sender and ignore
        /// non-admins / spoofed claims. Caches the list for detection embeds and forwards to Discord.
        /// </summary>
        public static void RPC_OnAdminSnapshot(long sender, ZPackage pkg)
        {
            if (pkg == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // Server-authoritative admin gate, resolved LIVE off ZNet's admin list - the same path the
            // admin mod-list push uses. Not spoofable (sender id is Steam-authenticated) and works even
            // when the peer wasn't tracked at connect (e.g. log-capture-only mode).
            if (!AdminSyncing.IsAdmin(sender)) return;

            string changedCommand;
            bool enabled;
            var active = new List<string>();
            try
            {
                changedCommand = pkg.ReadString();
                enabled = pkg.ReadBool();
                int n = pkg.ReadInt();
                for (int i = 0; i < n && i < 32; i++) active.Add(pkg.ReadString());
            }
            catch { return; }

            // Cache the active set on the PeerState (if tracked) for the OnAdminCheatDetected embeds.
            // Written under _lock to match the read in OnViolation; the value is always a fresh list.
            PeerState state;
            lock (_lock)
            {
                _trackedPeers.TryGetValue(sender, out state);
                if (state != null) state.LastKnownAdminCommands = active;
            }

            if (!(VAngardeConfig.AdminCommandLogging?.Value ?? true)) return;

            string name = state?.Peer?.m_playerName;
            string platformId = state?.PlatformId;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(platformId))
            {
                var peer = ZNet.instance.GetPeer(sender);
                if (peer != null)
                {
                    if (string.IsNullOrEmpty(name)) name = peer.m_playerName;
                    if (string.IsNullOrEmpty(platformId)) platformId = peer.m_socket?.GetHostName();
                }
            }

            DiscordSink.OnAdminCommandSnapshot(name ?? "Unknown", platformId ?? "Unknown",
                changedCommand, enabled, active);
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static void LogVerbose(string msg)
        {
            if (VAngardeConfig.VerboseLogging != null && VAngardeConfig.VerboseLogging.Value)
                Debug.Log($"[VAngarde] {msg}");
        }
    }
}
