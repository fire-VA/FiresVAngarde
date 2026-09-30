using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Phase-5b Layer 3 hardening — closes the two remaining vectors after the audit + session UID:
    ///
    /// <list type="bullet">
    /// <item><b>Anti-Harmony self-check.</b> Every challenge response computes a tamper bit by
    /// asking <see cref="Harmony"/> whether any of VAngarde's critical methods have been patched
    /// by an unknown owner. We hold our own owner ID; anything else patching
    /// <see cref="VAngardeModListAudit.Capture"/>, <see cref="VAngardeModListAudit.Read"/>,
    /// <see cref="VAngardeSession.SignClient"/>, or this class itself is treated as evidence the
    /// audit was neutered in-process. Server can require the bit to be zero.</item>
    ///
    /// <item><b>DLL content hash.</b> Client computes SHA256 of each plugin file under
    /// <c>BepInEx/plugins</c> and reports a fingerprint along with the GUID/version. The
    /// exact-match list can specify expected hashes; mismatches are violations (catches
    /// "same GUID + version, swapped binary" — i.e. a cheat repackaged under a clean mod's identity).</item>
    /// </list>
    /// Both layers are config-gated and additive on the wire: old clients (no hash, no tamper bit)
    /// are tolerated when the corresponding enforcement toggle is OFF.
    /// </summary>
    public static class VAngardeIntegrityCheck
    {
        /// <summary>Harmony owner id used by FiresVAngarde itself; patches by any other owner on the
        /// critical methods below are treated as evidence of in-process tampering.</summary>
        public const string TrustedHarmonyId = "com.Fire.FiresVAngarde";

        /// <summary>Returns true when a non-trusted Harmony owner has patched any critical anti-cheat method.</summary>
        public static bool DetectHarmonyTampering(out string offender)
        {
            offender = null;
            try
            {
                foreach (var m in CriticalMethods())
                {
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    string bad = ScanForUntrusted(info);
                    if (bad != null)
                    {
                        offender = $"{m.DeclaringType?.Name}.{m.Name}<-{bad}";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] integrity: harmony scan threw — treating as untampered: {ex.Message}");
            }
            return false;
        }

        private static IEnumerable<MethodInfo> CriticalMethods()
        {
            // The methods that, if patched out, would let a cheat trivially bypass the audit/session
            // layers. We deliberately list narrow targets — broad coverage would false-positive on
            // every legitimate Fires-* patch that touches the assembly.
            yield return typeof(VAngardeModListAudit).GetMethod(nameof(VAngardeModListAudit.Capture));
            yield return typeof(VAngardeModListAudit).GetMethod(nameof(VAngardeModListAudit.Write));
            yield return typeof(VAngardeSession).GetMethod(nameof(VAngardeSession.SignClient));
            yield return typeof(VAngardeSession).GetMethod(nameof(VAngardeSession.ComposeSigInput));
            yield return typeof(VAngardeIntegrityCheck).GetMethod(nameof(DetectHarmonyTampering));
        }

        private static string ScanForUntrusted(Patches info)
        {
            foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                if (!string.Equals(p.owner, TrustedHarmonyId, StringComparison.Ordinal))
                    return p.owner ?? "<unknown>";
            return null;
        }

        // ── DLL hashing ──────────────────────────────────────────────────────────────────────

        private static Dictionary<string, string> _hashCache; // filename (no path) -> sha256 hex
        private static readonly object HashCacheLock = new object();

        /// <summary>Compute SHA256 of every <c>*.dll</c> in the plugins folder. Cached after first call.</summary>
        public static Dictionary<string, string> ComputePluginHashes()
        {
            lock (HashCacheLock)
            {
                if (_hashCache != null) return _hashCache;
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string root = BepInEx.Paths.PluginPath;
                    if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    {
                        foreach (string dll in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
                        {
                            try { result[Path.GetFileName(dll)] = HashFile(dll); }
                            catch (Exception ex) { Debug.LogWarning($"[VAngarde] integrity: failed hashing {dll}: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VAngarde] integrity: plugin enumeration failed: {ex.Message}");
                }
                return _hashCache = result;
            }
        }

        /// <summary>
        /// The same hashes, computed on a worker. A full plugins folder is over a gigabyte of DLLs, and
        /// hashing it on the main thread froze every login for ~10 s inside the challenge response.
        /// </summary>
        public static Task<Dictionary<string, string>> ComputePluginHashesInBackground() =>
            Task.Run(() => ComputePluginHashes());

        public static Dictionary<string, string> CompletedHashesOrEmpty(Task<Dictionary<string, string>> hashing) =>
            hashing != null && hashing.Status == TaskStatus.RanToCompletion && hashing.Result != null
                ? hashing.Result
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string HashFile(string path)
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static void Write(ZPackage pkg, Dictionary<string, string> hashes, bool tampered)
        {
            pkg.Write(tampered);
            pkg.Write(hashes.Count);
            foreach (var kv in hashes) { pkg.Write(kv.Key ?? ""); pkg.Write(kv.Value ?? ""); }
        }

        public class Snapshot
        {
            public bool TamperDetected;
            public Dictionary<string, string> DllHashes = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Server-side validator: for every GUID in <paramref name="expected"/>, find a reported DLL
        /// whose file name maps to that GUID (fuzzy: file name without extension contains the GUID's
        /// last dotted segment, or matches the GUID outright) and require its hash to equal the
        /// expected one. Returns a violation reason or null when every expectation is satisfied.
        /// </summary>
        public static string CheckExpectedHashes(Snapshot snap, IReadOnlyDictionary<string, string> expected)
        {
            if (expected == null || expected.Count == 0) return null;
            if (snap == null || snap.DllHashes.Count == 0) return "dll hash: client reported no DLL hashes";

            foreach (var kv in expected)
            {
                string expectedHash = kv.Value;
                if (string.IsNullOrEmpty(expectedHash)) continue;
                string guid = kv.Key;
                string seg = LastSegment(guid);

                string reportedHash = null;
                foreach (var rh in snap.DllHashes)
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(rh.Key) ?? "";
                    if (string.Equals(nameNoExt, guid, StringComparison.OrdinalIgnoreCase) ||
                        nameNoExt.IndexOf(seg, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        guid.IndexOf(nameNoExt, StringComparison.OrdinalIgnoreCase) >= 0)
                    { reportedHash = rh.Value; break; }
                }

                if (reportedHash == null) return $"dll hash: client did not report a DLL for {guid}";
                if (!string.Equals(reportedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return $"dll hash: mismatch for {guid} (expected {Snip(expectedHash)} got {Snip(reportedHash)})";
            }
            return null;
        }

        private static string LastSegment(string guid)
        {
            int dot = guid?.LastIndexOf('.') ?? -1;
            return dot < 0 ? (guid ?? "") : guid.Substring(dot + 1);
        }

        private static string Snip(string hex)
            => string.IsNullOrEmpty(hex) ? "?" : hex.Length <= 12 ? hex : hex.Substring(0, 12) + "…";

        public static Snapshot Read(ZPackage pkg)
        {
            var s = new Snapshot();
            s.TamperDetected = pkg.ReadBool();
            int n = pkg.ReadInt();
            for (int i = 0; i < n && i < 4000; i++)
            {
                string file = pkg.ReadString();
                string hash = pkg.ReadString();
                if (!string.IsNullOrEmpty(file)) s.DllHashes[file] = hash;
            }
            return s;
        }
    }
}
