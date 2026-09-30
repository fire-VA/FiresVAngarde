using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Cross-source modlist audit — the fix for the headline VAngarde crack.
    ///
    /// The original protocol verifies <c>SHA256(nonce | claimedModList)</c> equals the claimed hash,
    /// which only proves the bytes weren't tampered in flight; it proves nothing about whether the
    /// claimed list reflects what's loaded. A cheat that intercepts the response (or
    /// <see cref="BepInEx.Bootstrap.Chainloader.PluginInfos"/>) just substitutes a sanitized list and
    /// recomputes the hash — total bypass in ~10 lines of Harmony.
    ///
    /// To raise the bar, the client now reports modlist EVIDENCE from three independent reflective
    /// sources: BepInEx plugin enumeration, AppDomain assemblies in the plugins folder, and the
    /// filesystem itself. A cheat now has to lie consistently across all three AND hide its own DLL
    /// file from enumeration. Server compares the sources and flags any mod that appears in the
    /// filesystem/assembly evidence but is absent from the declared plugin list — the classic
    /// "loaded but not reported" lie.
    /// </summary>
    public static class VAngardeModListAudit
    {
        public class Snapshot
        {
            public Dictionary<string, string> Declared = new(StringComparer.OrdinalIgnoreCase);
            public List<string> AssemblyFiles = new();
            public List<string> PluginFolderDlls = new();
        }

        /// <summary>Capture the local client's three-source view of the loaded modlist.</summary>
        public static Snapshot Capture()
        {
            var s = new Snapshot();

            try
            {
                var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (plugins != null)
                    foreach (var kv in plugins)
                        if (kv.Value?.Metadata != null)
                            s.Declared[kv.Value.Metadata.GUID] = kv.Value.Metadata.Version?.ToString() ?? "";
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] audit: Chainloader read failed: {ex.Message}"); }

            try
            {
                string pluginsRoot = Paths.PluginPath;
                if (!string.IsNullOrEmpty(pluginsRoot) && Directory.Exists(pluginsRoot))
                {
                    string pluginsRootCanon = Path.GetFullPath(pluginsRoot).TrimEnd(Path.DirectorySeparatorChar);

                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string loc = SafeAssemblyLocation(asm);
                        if (string.IsNullOrEmpty(loc)) continue;
                        if (loc.StartsWith(pluginsRootCanon, StringComparison.OrdinalIgnoreCase))
                            s.AssemblyFiles.Add(Path.GetFileName(loc));
                    }

                    foreach (var dll in Directory.GetFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories))
                        s.PluginFolderDlls.Add(Path.GetFileName(dll));
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[VAngarde] audit: AppDomain/FS scan failed: {ex.Message}"); }

            return s;
        }

        private static string SafeAssemblyLocation(Assembly asm)
        {
            try { return asm.IsDynamic ? null : asm.Location; }
            catch { return null; }
        }

        /// <summary>
        /// Server-side check: every DLL file the client saw on disk in the plugins folder should
        /// correspond to a loaded assembly with a declared plugin GUID. Anything in the filesystem
        /// or assembly evidence but absent from the declared list is "loaded but not reported" —
        /// i.e. a cheat hiding itself from <see cref="BepInEx.Bootstrap.Chainloader.PluginInfos"/>.
        /// Returns a violation reason, or null if the snapshot looks honest.
        /// </summary>
        public static string FindLie(Snapshot s)
        {
            if (s == null) return null;

            // Build a set of "DLLs that would belong to a declared plugin" — heuristically, a DLL whose
            // file name (without extension) contains, or is contained by, a declared GUID. Real BepInEx
            // plugin naming is loose; this is a fuzzy but defensible match.
            var declared = s.Declared.Keys.ToList();
            int unexplainedFiles = 0;
            int unexplainedAssemblies = 0;
            string firstUnexplained = null;

            foreach (string file in s.PluginFolderDlls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (LooksLikeExplained(file, declared) || LooksLikeKnownInfra(file)) continue;
                unexplainedFiles++;
                firstUnexplained ??= "file:" + file;
            }
            foreach (string asmFile in s.AssemblyFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (LooksLikeExplained(asmFile, declared) || LooksLikeKnownInfra(asmFile)) continue;
                unexplainedAssemblies++;
                firstUnexplained ??= "asm:" + asmFile;
            }

            // Tolerate a small number of unexplained entries (BepInEx itself, MMHook libs, patchers etc.
            // are often loose). Two or more unexplained DLLs that look like a plugin = suspicious.
            int total = unexplainedFiles + unexplainedAssemblies;
            if (total >= 2)
                return $"modlist audit: {total} unexplained plugin-folder DLL(s) (e.g. {firstUnexplained}) " +
                       $"declared={s.Declared.Count} files={s.PluginFolderDlls.Count} asms={s.AssemblyFiles.Count}";

            return null;
        }

        private static bool LooksLikeExplained(string fileNameWithExt, List<string> declaredGuids)
        {
            string name = Path.GetFileNameWithoutExtension(fileNameWithExt) ?? "";
            if (name.Length == 0) return true;
            foreach (string guid in declaredGuids)
            {
                if (string.IsNullOrEmpty(guid)) continue;
                if (guid.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf(LastSegment(guid), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string LastSegment(string guid)
        {
            int dot = guid.LastIndexOf('.');
            return dot < 0 ? guid : guid.Substring(dot + 1);
        }

        // BepInEx itself, common patcher/dep DLLs that ship without registering as a [BepInPlugin].
        private static readonly HashSet<string> _knownInfra = new(StringComparer.OrdinalIgnoreCase)
        {
            "BepInEx", "BepInEx.Harmony", "BepInEx.Preloader", "0Harmony", "0Harmony20", "Mono.Cecil",
            "MonoMod", "MonoMod.RuntimeDetour", "MonoMod.Utils", "MonoMod.HookGen", "MMHOOK_assembly_valheim",
            "Newtonsoft.Json", "YamlDotNet", "LiteDB", "ConfigurationManager", "JsonNet",
            "HarmonyXInterop"
        };

        private static bool LooksLikeKnownInfra(string fileNameWithExt)
        {
            string name = Path.GetFileNameWithoutExtension(fileNameWithExt) ?? "";
            return _knownInfra.Contains(name) || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                                              || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                                              || name.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase);
        }

        // ── Wire format ──────────────────────────────────────────────────────────────────────

        public static void Write(ZPackage pkg, Snapshot s)
        {
            pkg.Write(s.AssemblyFiles.Count);
            foreach (var f in s.AssemblyFiles) pkg.Write(f ?? "");
            pkg.Write(s.PluginFolderDlls.Count);
            foreach (var f in s.PluginFolderDlls) pkg.Write(f ?? "");
        }

        public static Snapshot Read(ZPackage pkg, Dictionary<string, string> declared)
        {
            var s = new Snapshot { Declared = declared ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
            int n = pkg.ReadInt();
            for (int i = 0; i < n && i < 2000; i++) s.AssemblyFiles.Add(pkg.ReadString());
            n = pkg.ReadInt();
            for (int i = 0; i < n && i < 4000; i++) s.PluginFolderDlls.Add(pkg.ReadString());
            return s;
        }
    }
}
