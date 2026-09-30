using System;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Modules.AntiCheat
{
    /// <summary>
    /// Watches VAngarde config files (whitelist, blacklist, exact match, exempt players)
    /// for changes and triggers a hot-reload of the mod validation lists.
    ///
    /// Follows the same pattern as <see cref="VerdantsAscent.Managers.ServerConfigFileWatcher"/>
    /// but dedicated to the VAngarde config directory.
    /// </summary>
    public static class VAngardeFileWatcher
    {
        private static FileSystemWatcher _watcher;
        private static DateTime _lastReloadTime;
        private const int MinReloadIntervalMs = 2000; // debounce: 2 seconds

        /// <summary>
        /// Start watching the VAngarde config directory.
        /// Server-only — called from VAngardeCore.Initialize().
        /// </summary>
        public static void Initialize()
        {
            if (_watcher != null) return;

            string dir = VAngardeConfig.ConfigDir;
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VAngarde] Cannot create config dir: {ex.Message}");
                    return;
                }
            }

            try
            {
                _watcher = new FileSystemWatcher(dir, "*.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Deleted += OnFileChanged;

                LogVerbose($"File watcher started on {dir}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to start file watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop watching and dispose the watcher.
        /// </summary>
        public static void Shutdown()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce
            var now = DateTime.UtcNow;
            if ((now - _lastReloadTime).TotalMilliseconds < MinReloadIntervalMs)
                return;
            _lastReloadTime = now;

            Debug.Log($"[VAngarde] Config file changed: {e.Name} — reloading lists");

            try
            {
                VAngardeModValidator.ReloadAll();

                // ReloadAll() clears _exemptPlayers and re-reads from disk,
                // which wipes in-memory admin exemptions. Re-merge them if AdminBypass is ON.
                if (VAngardeConfig.AdminBypass?.Value ?? true)
                    VAngardeModValidator.MergeAdminExemptions();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VAngarde] Failed to reload lists: {ex.Message}");
            }
        }

        private static void LogVerbose(string msg)
        {
            if (VAngardeConfig.VerboseLogging != null && VAngardeConfig.VerboseLogging.Value)
                Debug.Log($"[VAngarde] {msg}");
        }
    }
}
