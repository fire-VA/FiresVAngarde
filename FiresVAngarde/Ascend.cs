using BepInEx;
using FiresCore.Sync;
using HarmonyLib;
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Modules.AntiCheat;
using VerdantsAscent.Modules.Characters;
using VerdantsAscent.Utilities;

namespace VerdantsAscent
{
    // FiresVAngarde — server-management mod: VAngarde anti-cheat + server-side character
    // persistence/transfer. This is the Phase-1 skeleton: it loads, binds the two
    // infrastructure config entries to ConfigSync, and applies Harmony patches.
    // The anti-cheat and character subsystems are added in later phases.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.Fire.FiresUnifiedCore", BepInDependency.DependencyFlags.HardDependency)]
    public class FiresVAngarde : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresVAngarde";
        public const string PluginName = "FiresVAngarde";
        public const string PluginVersion = "0.2.36";

        public static FiresVAngarde Instance { get; private set; }
        public static readonly Harmony harmony = new(PluginGUID);

        /// <summary>
        /// BepInEx-native log sink: <see cref="BaseUnityPlugin.Logger"/>, exposed as a static so any
        /// module can call it without a plugin-instance handle.
        /// </summary>
        public static BepInEx.Logging.ManualLogSource Log { get; private set; }

        public ConfigSync configSync;

        private readonly Action<bool> _refreshConfigLockOnAdminChange =
            _ => ConfigManager.Instance?.OnConfigLockChanged();

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // BIG "loading" banner — very first console output of the mod.
            try { FiresVAngardeBanner.PrintBig(); }
            catch (Exception ex) { Log.LogWarning($"Big banner failed: {ex.Message}"); }

            Log.LogInfo($"[{PluginName}] Awake() entered — version {PluginVersion}");

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            AdminSyncing.AdminStatusChanged += _refreshConfigLockOnAdminChange;

            ConfigManager.Instance.Initialize(Config);
            BindConfigsToSync();

            // VAngarde anti-cheat config — server-locked. (FiresNPCs never called these, which
            // left the anti-cheat config unbound; wiring them here closes that latent gap.)
            VAngardeConfig.Initialize(Config);
            VAngardeConfig.BindToSync(configSync);

            // Character subsystem config (transport knobs now; profile lifecycle config added with those features).
            CharactersConfig.Initialize(Config);
            CharactersConfig.BindToSync(configSync);

            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{PluginName}] harmony.PatchAll threw: {ex}");
            }

            try { VerdantsAscent.Utilities.VangardeHelpContent.Register(); }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] Help registration failed: {ex.Message}"); }

            Debug.Log($"[{PluginName}] loaded — anti-cheat + character subsystems active (v{PluginVersion}).");

            // Compact "loaded" banner — deferred to world load so it bookends the load
            // with the same timing as the sibling Fires mods (BIG = early-load, compact
            // = world-load).
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // Waits for ZNetScene to be live (same readiness signal the sibling mods use),
        // then emits the compact loaded banner. Yields plain frames instead of
        // WaitForEndOfFrame so it also fires on headless servers, where end-of-frame
        // callbacks never run. Failure is non-fatal — falls back to the plain "loaded"
        // line so the load event is still recorded in the file log.
        private IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return null;

            try { FiresVAngardeBanner.Print(); }
            catch (Exception ex)
            {
                Log.LogInfo($"{PluginName} v{PluginVersion} loaded. (banner failed: {ex.Message})");
            }
        }

        private void BindConfigsToSync()
        {
            var cm = ConfigManager.Instance;
            configSync.AddLockingConfigEntry(cm.configServerAuthority);
            configSync.AddConfigEntry(cm.configVerboseLogging);
        }

        private void OnDestroy()
        {
            Modules.Characters.CharacterDiskWriter.Drain();
            if (configSync != null)
            {
                configSync.lockedConfigChanged -= ConfigManager.Instance.OnConfigLockChanged;
                configSync = null;
            }
            AdminSyncing.AdminStatusChanged -= _refreshConfigLockOnAdminChange;

            try { ConfigManager.Instance?.Dispose(); }
            catch { /* watcher already torn down */ }

            Instance = null;
        }
    }
}
