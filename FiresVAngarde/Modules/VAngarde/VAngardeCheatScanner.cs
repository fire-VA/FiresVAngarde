using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Bridge;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Server-side behavioral cheat detection for VAngarde.
    ///
    /// HONEST SCOPE (see Tools/VANGARDE_SECURITY_AUDIT.md): Valheim is owner-authoritative — a
    /// connected client owns its own player ZDO, so health, position, the fly flag and equipped
    /// items are values that client itself writes. The server reads them but cannot trust them
    /// against a patched client. Therefore:
    ///   - Speed and flight are treated as server-OBSERVED signals: they read the position/flag the
    ///     server received and enforce (kick) only on states a legit client cannot produce
    ///     (impossible sustained speed; the literal debug-fly flag). Thresholds are tuned to never
    ///     false-kick normal play (boats, falls, lag, teleports).
    ///   - God mode is a LOG-ONLY heuristic. It reads client-authored health, so it can never be
    ///     proof; it reports a suspicion for admin review and NEVER auto-kicks. (The old lethal
    ///     "damage poke" was removed: it used the wrong RPC name so it hit nobody, and its
    ///     "survived the poke" logic could kick a legitimate player who merely outlasted the window.
    ///     A real god-mode check requires server-authoritative health — a separate, larger change.)
    /// </summary>
    public static class VAngardeCheatScanner
    {
        private class PlayerTrack
        {
            public long PeerUid;
            public ZDOID Zdoid;
            public Vector3 LastPosition;
            public float LastPositionTime;
            public float LastHealth;
            public int HighSpeedFrames;

            // God mode — near-death survival heuristic (log-only)
            public int NearDeathHits;
            public int TotalDamageScans;
            public float TrackingStartTime;
            public bool GodSuspicionReported;

            // Speed — knockback grace + log-tier de-dupe
            public float LastDamageTime;
            public bool SpeedSuspicionReported;
        }

        // Fraction of maxHP below which a player is "near death" — a normal player is one or two hits
        // from dying here.
        private const float NearDeathThreshold = 0.20f;

        // Sustained near-death hits (at ~2s scan interval, 5 ≈ 10s) before we log a god-mode suspicion.
        private const int NearDeathHitsForSuspicion = 5;

        // Minimum observation time before a god-mode suspicion can be logged.
        private const float MinObservationTime = 30f;

        // After a damage event, ignore the speed check this long — a hit, explosion, or troll-swat
        // knockback (any force push) briefly flings the character far faster than they can run.
        private const float DamageGraceSeconds = 3f;

        private static readonly Dictionary<long, PlayerTrack> _tracks = new Dictionary<long, PlayerTrack>();

        public static void ScanAll()
        {
            if (!VAngardeCore.IsActive()) return;
            if (ZNet.instance == null || ZNet.instance.m_zdoMan == null) return;

            var peers = ZNet.instance.GetPeers();
            if (peers == null) return;

            // Clean up tracks for disconnected peers
            var toRemove = new List<long>();
            foreach (var kvp in _tracks)
            {
                bool found = false;
                foreach (var peer in peers)
                {
                    if (peer.m_uid == kvp.Key) { found = true; break; }
                }
                if (!found) toRemove.Add(kvp.Key);
            }
            foreach (var uid in toRemove)
                _tracks.Remove(uid);

            float now = Time.time;
            foreach (var peer in peers)
            {
                if (!VAngardeCore.IsPeerScanned(peer.m_uid))
                    continue;

                ScanPeer(peer, now);
            }
        }

        private static void ScanPeer(ZNetPeer peer, float now)
        {
            if (!_tracks.TryGetValue(peer.m_uid, out var track))
            {
                track = new PlayerTrack
                {
                    PeerUid = peer.m_uid,
                    LastPositionTime = now,
                    LastHealth = -1f,
                    TrackingStartTime = now
                };
                _tracks[peer.m_uid] = track;
            }

            ZDOID characterZdoid = peer.m_characterID;
            if (characterZdoid.IsNone()) return;

            ZDO zdo = ZNet.instance.m_zdoMan.GetZDO(characterZdoid);
            if (zdo == null) return;

            track.Zdoid = characterZdoid;

            // Note a damage event this sample (health dropped) so the speed check can grace-skip the
            // knockback / force-push a hit, explosion, or troll-swat produces.
            float curHealth = zdo.GetFloat(ZDOVars.s_health, 0f);
            if (track.LastHealth > 0f && curHealth < track.LastHealth - 0.1f)
                track.LastDamageTime = now;

            if (VAngardeConfig.DetectFlight != null && VAngardeConfig.DetectFlight.Value)
                CheckFlight(peer, zdo, track);

            if (VAngardeConfig.DetectGodMode != null && VAngardeConfig.DetectGodMode.Value)
                CheckGodMode(peer, zdo, track, now);

            if (VAngardeConfig.DetectSpeedHack != null && VAngardeConfig.DetectSpeedHack.Value)
                CheckSpeedHack(peer, zdo, track, now);

            if (VAngardeConfig.DetectCheatItems != null && VAngardeConfig.DetectCheatItems.Value)
                CheckCheatItems(peer, zdo);

            // Update snapshots (after checks so LastHealth/LastPosition are the *previous* reading)
            track.LastPosition = zdo.GetPosition();
            track.LastPositionTime = now;
            track.LastHealth = zdo.GetFloat(ZDOVars.s_health, 0f);
        }

        // ── Cheat Items ─────────────────────────────────────────────

        private static HashSet<string> _bannedItemsCache;
        private static string _bannedItemsRaw;

        private static HashSet<string> GetBannedItems()
        {
            string raw = VAngardeConfig.BannedItems?.Value ?? "";
            if (raw != _bannedItemsRaw || _bannedItemsCache == null)
            {
                _bannedItemsRaw = raw;
                _bannedItemsCache = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var part in raw.Split(','))
                {
                    string trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        _bannedItemsCache.Add(trimmed);
                }
            }
            return _bannedItemsCache;
        }

        private static void CheckCheatItems(ZNetPeer peer, ZDO zdo)
        {
            var banned = GetBannedItems();
            if (banned.Count == 0) return;

            string rightItem = zdo.GetString(ZDOVars.s_rightItem, "");
            string leftItem = zdo.GetString(ZDOVars.s_leftItem, "");

            if (!string.IsNullOrEmpty(rightItem) && banned.Contains(rightItem))
            {
                VAngardeCore.ReportBehavioralViolation(peer,
                    $"Banned cheat item equipped (right hand): {rightItem}");
                return;
            }

            if (!string.IsNullOrEmpty(leftItem) && banned.Contains(leftItem))
            {
                VAngardeCore.ReportBehavioralViolation(peer,
                    $"Banned cheat item equipped (left hand): {leftItem}");
            }
        }

        // ── Flight ──────────────────────────────────────────────────
        // Server-observed: the debug-fly flag lives on the character ZDO. Catches the literal
        // devcommands `debugfly` (the common case) with zero false positives. A custom position-driven
        // fly hack that never sets the flag is not caught here — that needs a ground-height physics
        // probe, which false-positives on tall builds/terrain and is deferred (see the audit).

        private static void CheckFlight(ZNetPeer peer, ZDO zdo, PlayerTrack track)
        {
            if (zdo.GetBool(ZDOVars.s_debugFly, false))
                VAngardeCore.ReportBehavioralViolation(peer, "Debug flight mode active (ZDO s_debugFly=true)");
        }

        // ── God Mode (LOG-ONLY heuristic) ───────────────────────────
        // Reads client-authored health, so it is advisory only: a suspicion for an admin to review,
        // never an auto-kick. Valheim god mode prevents death, not damage, so a god-mode player can
        // take repeated hits at very low HP without dying — that pattern is what we surface.

        private static void CheckGodMode(ZNetPeer peer, ZDO zdo, PlayerTrack track, float now)
        {
            float health = zdo.GetFloat(ZDOVars.s_health, 0f);
            float maxHealth = zdo.GetFloat(ZDOVars.s_maxHealth, 25f);

            // Died → not in god mode. Reset.
            if (health <= 0f)
            {
                ResetGodModeTracking(track, now);
                return;
            }
            if (maxHealth <= 0f) return;

            if (track.LastHealth > 0f)
            {
                float delta = health - track.LastHealth;
                if (delta < -0.1f) // took damage (float-noise threshold)
                {
                    track.TotalDamageScans++;
                    if (health / maxHealth < NearDeathThreshold)
                        track.NearDeathHits++;
                }
            }

            float observationTime = now - track.TrackingStartTime;
            if (!track.GodSuspicionReported &&
                track.NearDeathHits >= NearDeathHitsForSuspicion &&
                observationTime >= MinObservationTime)
            {
                track.GodSuspicionReported = true; // report once per tracking window; no kick
                VAngardeCore.ReportSuspicionLogOnly(peer,
                    $"God mode suspected: survived {track.NearDeathHits} near-death hits over {observationTime:F0}s " +
                    $"(heuristic on client-authored health — review manually, not auto-enforced)");
            }
        }

        private static void ResetGodModeTracking(PlayerTrack track, float now)
        {
            track.NearDeathHits = 0;
            track.TotalDamageScans = 0;
            track.TrackingStartTime = now;
            track.GodSuspicionReported = false;
        }

        // ── Speed Hack ──────────────────────────────────────────────
        // Server-observed horizontal position deltas. Enforced (kick) only on speeds no legit play
        // reaches: base sprint ≈ 6-7 m/s and the fastest boat (longship) ≈ 10-11 m/s, so the ceiling
        // sits well above both. Vertical movement is ignored (falls don't count), single-sample
        // teleports are dropped (distance guard), and one-off lag catch-up is dropped (5-sample
        // sustain requirement).

        private static void CheckSpeedHack(ZNetPeer peer, ZDO zdo, PlayerTrack track, float now)
        {
            float dt = now - track.LastPositionTime;
            if (dt < 0.5f || track.LastPositionTime <= 0) return;

            // Knockback / force-push grace: a recent hit, explosion, or troll-swat flings the character.
            if (now - track.LastDamageTime < DamageGraceSeconds) { track.HighSpeedFrames = 0; return; }

            Vector3 a = zdo.GetPosition();          // horizontal only — jumps, falls, and feather-cape
            Vector3 b = track.LastPosition;         // descent are vertical and must not count as "speed"
            a.y = 0f; b.y = 0f;
            float distance = Vector3.Distance(a, b);
            if (distance >= 1000f) { track.HighSpeedFrames = 0; return; } // single-sample teleport / zone load
            float speed = distance / dt;

            // Two tiers so legit modded speed is never auto-kicked. Kick ONLY on speeds no legit source
            // reaches (set the ceiling above your fastest: sprint ~6-7, longship ~10-11, mounts/wind/
            // speed-potions higher). Sustained speed between 60% of the ceiling and the ceiling is LOGGED
            // for review — that gray zone is where boats, mounts, and speed buffs live.
            float kickCeiling = Mathf.Max(20f, VAngardeConfig.SpeedHackThreshold?.Value ?? 30f);
            float logFloor = Mathf.Max(15f, kickCeiling * 0.6f);

            if (speed > logFloor)
            {
                track.HighSpeedFrames++;
                if (track.HighSpeedFrames > 5)  // sustained ~3s+ — not a lag catch-up burst or a brief push
                {
                    if (speed > kickCeiling)
                    {
                        VAngardeCore.ReportBehavioralViolation(peer,
                            $"Speed hack: {speed:F1} m/s sustained (kick ceiling {kickCeiling:F0} m/s - above any boat/mount/buff)");
                        track.HighSpeedFrames = 0;
                    }
                    else if (!track.SpeedSuspicionReported)
                    {
                        track.SpeedSuspicionReported = true; // log once per episode; never kicks
                        VAngardeCore.ReportSuspicionLogOnly(peer,
                            $"Elevated speed: {speed:F1} m/s (under kick ceiling {kickCeiling:F0} - could be boat/mount/speed buff or a mild hack; review)");
                    }
                }
            }
            else
            {
                track.HighSpeedFrames = Mathf.Max(0, track.HighSpeedFrames - 1);
                track.SpeedSuspicionReported = false; // re-arm once they return to normal speed
            }
        }
    }
}
