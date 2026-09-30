using BepInEx.Configuration;
using FiresCore.Sync;
using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// Owns the two infrastructure config entries (verbose logging + server authority/locking)
    /// and the shared config-file machinery: debounced hot-reload when the .cfg is edited on disk,
    /// and server-side rebroadcast of synced entries to clients. Subsystem-specific config
    /// (VAngarde anti-cheat, Characters) lives in its own class and binds itself to ConfigSync.
    /// </summary>
    public class ConfigManager
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ??= new ConfigManager();

        private FileSystemWatcher configWatcher;
        private bool _isShuttingDown;
        private DateTime _lastConfigUpdate = DateTime.MinValue;
        private Coroutine _reloadCoroutine;

        // Stamped on every ConfigEntry write we make (SettingChanged fires on .Value sets and
        // SaveOnConfigSet rewrites the .cfg on the same set) - lets the watcher tell our own
        // file writes from an external/admin edit, so interactive UI writes can't loop us into
        // endless self-reloads (+ synced-entry rebroadcasts).
        private DateTime _lastSelfWriteUtc = DateTime.MinValue;
        private const double SelfWriteSuppressSeconds = 3.0;

        private void SelfWriteStamp(object sender, SettingChangedEventArgs e)
            => _lastSelfWriteUtc = DateTime.UtcNow;

        private bool IsRecentSelfWrite()
            => (DateTime.UtcNow - _lastSelfWriteUtc).TotalSeconds < SelfWriteSuppressSeconds;

        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerAuthority;

        public void Initialize(ConfigFile config)
        {
            configVerboseLogging = config.Bind("General", "VerboseLogging", false,
                "Enable detailed debug logs.");
            configServerAuthority = config.Bind("General", "ServerAuthority", false,
                "When enabled, config changes are locked to the server admin only (synced to clients).");

            SetupConfigWatcher(config);
        }

        private void SetupConfigWatcher(ConfigFile config)
        {
            if (configWatcher != null) return;

            string configPath = config.ConfigFilePath;
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return;

            config.SettingChanged -= SelfWriteStamp;
            config.SettingChanged += SelfWriteStamp;

            configWatcher = new FileSystemWatcher
            {
                Path = Path.GetDirectoryName(configPath),
                Filter = Path.GetFileName(configPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };

            configWatcher.Changed += (_, _) => ScheduleReload();
            configWatcher.Renamed += (_, _) => ScheduleReload();
        }

        private void ScheduleReload()
        {
            if (_isShuttingDown || ConfigSync.ProcessingServerUpdate) return;
            if (IsRecentSelfWrite()) return;
            if (_reloadCoroutine != null) return;
            _reloadCoroutine = FiresVAngarde.Instance.StartCoroutine(ReloadConfigAfterDelay());
        }

        private IEnumerator ReloadConfigAfterDelay()
        {
            yield return new WaitForSeconds(2f);

            if (_isShuttingDown || ConfigSync.ProcessingServerUpdate) { _reloadCoroutine = null; yield break; }
            if (IsRecentSelfWrite()) { _reloadCoroutine = null; yield break; }
            if ((DateTime.Now - _lastConfigUpdate).TotalMilliseconds < 2000) { _reloadCoroutine = null; yield break; }
            _lastConfigUpdate = DateTime.Now;

            var configFile = FiresVAngarde.Instance.Config;
            bool saveOnSet = configFile.SaveOnConfigSet;
            configFile.SaveOnConfigSet = false;
            configFile.Reload();
            configFile.SaveOnConfigSet = saveOnSet;
            _reloadCoroutine = null;
        }

        public void OnConfigLockChanged()
        {
            if (configVerboseLogging != null && configVerboseLogging.Value)
                Debug.Log($"{FiresVAngarde.PluginName}: Config lock changed.");
        }

        public void Dispose()
        {
            _isShuttingDown = true;
            configWatcher?.Dispose();
        }
    }
}
