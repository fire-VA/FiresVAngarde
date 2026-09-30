using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FiresCore.Bridge;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Server-side: watches the four VAngarde mod lists (enforced/exact-match, whitelist, server-only,
    /// blacklist) and pushes a Discord readout to the anti-cheat ("vangarde") channel whenever one changes —
    /// admin-grown whitelist, a hand-edit picked up by <see cref="VAngardeFileWatcher"/>, a console reload,
    /// or an off-line edit detected on startup. <see cref="CheckAndNotify"/> is called at the tail of
    /// <see cref="VAngardeModValidator.ReloadAll"/> (the single choke point) and directly after an admin
    /// extends the whitelist.
    ///
    /// A per-list signature snapshot is persisted to the config dir so a restart with no changes stays silent,
    /// while an edit made while the server was down still gets reported on the next boot. The first ever run
    /// (no snapshot file) only records the baseline — it never posts "everything is new".
    /// </summary>
    internal static class VAngardeListNotifier
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<DiscordListKind, HashSet<string>> _last =
            new Dictionary<DiscordListKind, HashSet<string>>();
        private static bool _init;
        private static bool _hasBaseline;

        private static readonly DiscordListKind[] _kinds =
        {
            DiscordListKind.Enforced, DiscordListKind.Whitelist, DiscordListKind.ServerOnly,
            DiscordListKind.Blacklist, DiscordListKind.AdminOnly
        };

        private static string StatePath => Path.Combine(VAngardeConfig.ConfigDir, ".listnotify_state.txt");

        /// <summary>Compare every list to its last-known snapshot and post a readout for each that changed.</summary>
        internal static void CheckAndNotify(string trigger = null)
        {
            try
            {
                if (!IsServer()) return;

                List<DiscordListChangeInfo> toSend = null;
                lock (_lock)
                {
                    EnsureInit();

                    var currentSigs = new Dictionary<DiscordListKind, HashSet<string>>();
                    foreach (var kind in _kinds) currentSigs[kind] = Signature(kind);

                    if (!_hasBaseline)
                    {
                        foreach (var kind in _kinds) _last[kind] = currentSigs[kind];
                        _hasBaseline = true;
                        Persist();
                        return;
                    }

                    foreach (var kind in _kinds)
                    {
                        _last.TryGetValue(kind, out var prev);
                        var cur = currentSigs[kind];
                        if (prev != null && prev.SetEquals(cur)) continue;

                        var info = BuildInfo(kind, prev ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            cur, trigger);
                        _last[kind] = cur;
                        (toSend ?? (toSend = new List<DiscordListChangeInfo>())).Add(info);
                    }

                    if (toSend == null) return;
                    Persist();
                }

                // StartCoroutine / GameObject creation inside the Discord poster is main-thread-only; the file
                // watcher fires ReloadAll on a threadpool thread, so marshal the actual post.
                var batch = toSend;
                FiresCore.Async.MainThreadDispatcher.Enqueue(() =>
                {
                    foreach (var info in batch)
                        DiscordSink.OnServerListChanged(info);
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] List-change notify failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Display-ready plain-text dump of all four current lists — served on demand when someone reacts
        /// on a list message in Discord (via FUC's server-list snapshot provider). Always live state.
        /// </summary>
        internal static string BuildSnapshotText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("VAngarde server mod lists");
            sb.AppendLine();

            var enforced = VAngardeModValidator.ExactMatchEntries;
            sb.AppendLine($"== Enforced (exact match) - {enforced.Count} mod(s) - clients MUST run these at these versions ==");
            if (enforced.Count == 0) sb.AppendLine("(none)");
            else foreach (var kv in enforced.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"{kv.Key} = {kv.Value}");
            sb.AppendLine();

            AppendGuidList(sb, "Whitelist (allowed extra client mods)", VAngardeModValidator.WhitelistGuids);
            AppendGuidList(sb, "Admin-only (ONLY admins may run these client-side)", VAngardeModValidator.AdminOnlyGuids);
            AppendGuidList(sb, "Server-only (not required on clients)", VAngardeModValidator.ServerOnlyGuids);
            AppendGuidList(sb, "Blacklist (banned)", VAngardeModValidator.BlacklistGuids);
            return sb.ToString();
        }

        private static void AppendGuidList(System.Text.StringBuilder sb, string label, IReadOnlyCollection<string> guids)
        {
            sb.AppendLine($"== {label} - {guids.Count} ==");
            if (guids.Count == 0) sb.AppendLine("(none)");
            else foreach (var g in guids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string v = ServerVersion(g);
                sb.AppendLine(string.IsNullOrEmpty(v) ? g : $"{g} (server runs v{v})");
            }
            sb.AppendLine();
        }

        // ── Signatures: the identity that defines a "change". Enforced tracks version (a bump IS a change);
        //    the GUID-only lists track membership only, so a server-only mod updating doesn't false-trigger. ──
        private static HashSet<string> Signature(DiscordListKind kind)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (kind == DiscordListKind.Enforced)
                foreach (var kv in VAngardeModValidator.ExactMatchEntries) set.Add($"{kv.Key}={kv.Value}");
            else
                foreach (var guid in GuidsFor(kind)) set.Add(guid);
            return set;
        }

        private static IEnumerable<string> GuidsFor(DiscordListKind kind)
        {
            switch (kind)
            {
                case DiscordListKind.Whitelist:  return VAngardeModValidator.WhitelistGuids;
                case DiscordListKind.ServerOnly: return VAngardeModValidator.ServerOnlyGuids;
                case DiscordListKind.Blacklist:  return VAngardeModValidator.BlacklistGuids;
                case DiscordListKind.AdminOnly:  return VAngardeModValidator.AdminOnlyGuids;
                default:                         return VAngardeModValidator.ExactMatchEntries.Keys;
            }
        }

        private static DiscordListChangeInfo BuildInfo(DiscordListKind kind, HashSet<string> prevSig,
            HashSet<string> curSig, string trigger)
        {
            var prev = ParseSig(prevSig);
            var cur = ParseSig(curSig);

            var added = new List<string>();
            var removed = new List<string>();
            var changed = new List<string>();

            foreach (var g in cur.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!prev.ContainsKey(g)) added.Add(Fmt(kind, g, cur[g]));
                else if (kind == DiscordListKind.Enforced && !string.Equals(prev[g], cur[g], StringComparison.OrdinalIgnoreCase))
                    changed.Add($"`{g}` `v{prev[g]}` → `v{cur[g]}`");
            }
            foreach (var g in prev.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (!cur.ContainsKey(g)) removed.Add(Fmt(kind, g, prev[g]));

            var current = cur.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(g => Fmt(kind, g, cur[g]))
                .ToList();

            return new DiscordListChangeInfo
            {
                Kind = kind,
                Trigger = string.IsNullOrEmpty(trigger) ? "server lists reloaded" : trigger,
                Added = added,
                Removed = removed,
                Changed = changed,
                Current = current,
                EnforcedCount = VAngardeModValidator.ExactMatchCount,
                WhitelistCount = VAngardeModValidator.WhitelistCount,
                ServerOnlyCount = VAngardeModValidator.ServerOnlyCount,
                BlacklistCount = VAngardeModValidator.BlacklistCount,
                AdminOnlyCount = VAngardeModValidator.AdminOnlyCount
            };
        }

        // Enforced signatures are "guid=version"; the others are bare GUIDs (version filled from the
        // server's loaded plugins for display only).
        private static Dictionary<string, string> ParseSig(HashSet<string> sig)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in sig)
            {
                int eq = line.IndexOf('=');
                if (eq > 0) map[line.Substring(0, eq)] = line.Substring(eq + 1);
                else map[line] = null;
            }
            return map;
        }

        private static string Fmt(DiscordListKind kind, string guid, string version)
        {
            string v = version ?? (kind == DiscordListKind.Enforced ? null : ServerVersion(guid));
            return string.IsNullOrEmpty(v) ? $"`{guid}`" : $"`{guid}` `v{v}`";
        }

        private static string ServerVersion(string guid)
        {
            try
            {
                var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (infos != null && infos.TryGetValue(guid, out var info) && info?.Metadata != null)
                    return info.Metadata.Version?.ToString();
            }
            catch { }
            return null;
        }

        private static bool IsServer()
        {
            try { return ZNet.instance != null && ZNet.instance.IsServer(); }
            catch { return false; }
        }

        // ── Snapshot persistence ──────────────────────────────────────────────────────────────────────
        private static void EnsureInit()
        {
            if (_init) return;
            _init = true;
            try
            {
                if (!File.Exists(StatePath)) { _hasBaseline = false; return; }

                HashSet<string> cur = null;
                foreach (var raw in File.ReadAllLines(StatePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        var name = line.Substring(1, line.Length - 2);
                        cur = Enum.TryParse<DiscordListKind>(name, out var k)
                            ? (_last[k] = new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                            : null;
                    }
                    else cur?.Add(line);
                }
                foreach (var kind in _kinds)
                    if (!_last.ContainsKey(kind)) _last[kind] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _hasBaseline = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Could not read list-notify snapshot: {ex.Message}");
                _hasBaseline = false;
            }
        }

        private static void Persist()
        {
            try
            {
                var lines = new List<string>();
                foreach (var kind in _kinds)
                {
                    lines.Add($"[{kind}]");
                    if (_last.TryGetValue(kind, out var set))
                        lines.AddRange(set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                    lines.Add("");
                }
                File.WriteAllLines(StatePath, lines.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Could not persist list-notify snapshot: {ex.Message}");
            }
        }
    }
}
