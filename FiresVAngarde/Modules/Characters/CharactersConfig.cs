using BepInEx.Configuration;
using FiresCore.Sync;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Config for the server-side character subsystem. For now this is the transport surface
    /// (the anti-timeout knobs); the profile lifecycle config (single-character, hardcore,
    /// maintenance, backups, …) is added alongside those features.
    /// </summary>
    public static class CharactersConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> ConfigChunkSizeBytes;
        public static ConfigEntry<int> ConfigQueueBackpressureBytes;
        public static ConfigEntry<int> ConfigCrossplayChunkSizeBytes;
        public static ConfigEntry<int> ConfigCrossplayInFlightBytes;
        public static ConfigEntry<float> ConfigNoProgressTimeoutSeconds;
        public static ConfigEntry<float> ConfigLogoutSaveWaitSeconds;
        public static ConfigEntry<int> ConfigBackupsToKeep;

        // Enforcement
        public static ConfigEntry<bool> SingleCharacterMode;
        public static ConfigEntry<bool> SingleCharacterAdminBypass;
        public static ConfigEntry<bool> ForceServerCharacter;
        public static ConfigEntry<bool> ForceServerCharacterAdminBypass;
        public static ConfigEntry<bool> HardcoreMode;
        public static ConfigEntry<bool> MaintenanceMode;
        public static ConfigEntry<int> MaintenanceTimerSeconds;
        public static ConfigEntry<int> AfkKickMinutes;
        public static ConfigEntry<bool> ExcludeAdminsFromAfk;
        public static ConfigEntry<string> ServerKey; // base64 32-byte master, generated once

        // Defensive accessors so callers read sane values even before Initialize runs.
        public static int ChunkSizeBytes => ConfigChunkSizeBytes?.Value ?? (256 * 1024);
        public static int QueueBackpressureBytes => ConfigQueueBackpressureBytes?.Value ?? 200_000;
        public static int CrossplayChunkSizeBytes => ConfigCrossplayChunkSizeBytes?.Value ?? (16 * 1024);
        public static int CrossplayInFlightBytes => ConfigCrossplayInFlightBytes?.Value ?? (64 * 1024);
        public static float NoProgressTimeoutSeconds => ConfigNoProgressTimeoutSeconds?.Value ?? 120f;
        public static float LogoutSaveWaitSeconds => ConfigLogoutSaveWaitSeconds?.Value ?? 15f;
        public static int BackupsToKeep => ConfigBackupsToKeep?.Value ?? 25;

        public static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind("Characters", "Enabled", true,
                "Enable the server-side character persistence/transfer subsystem.");

            ConfigChunkSizeBytes = config.Bind("Characters.Transport", "ChunkSizeBytes", 256 * 1024,
                new ConfigDescription(
                    "Payload bytes per chunk. Held well under Steam's 512 KB single-message limit.",
                    new AcceptableValueRange<int>(16 * 1024, 480 * 1024)));

            ConfigQueueBackpressureBytes = config.Bind("Characters.Transport", "QueueBackpressureBytes", 200_000,
                new ConfigDescription(
                    "Pause sending while the peer's socket send-queue exceeds this many bytes. Pure " +
                    "back-pressure — a slow link transfers slower, the transfer is never aborted for being slow.",
                    new AcceptableValueRange<int>(20_000, 1_000_000)));

            ConfigCrossplayChunkSizeBytes = config.Bind("Characters.Transport", "CrossplayChunkSizeBytes", 16 * 1024,
                new ConfigDescription(
                    "Payload bytes per chunk on a crossplay (PlayFab) connection. Kept small: PlayFab dropped a join that " +
                    "sent the profile as one large message among the join's other traffic.",
                    new AcceptableValueRange<int>(4 * 1024, 64 * 1024)));

            ConfigCrossplayInFlightBytes = config.Bind("Characters.Transport", "CrossplayInFlightBytes", 64 * 1024,
                new ConfigDescription(
                    "On a crossplay (PlayFab) connection, pause while it has more than this many bytes sent and not yet " +
                    "acknowledged (all of its traffic, not only the profile). Vanilla's own world-data sender stays near 40 KB.",
                    new AcceptableValueRange<int>(16 * 1024, 512 * 1024)));

            ConfigNoProgressTimeoutSeconds = config.Bind("Characters.Transport", "NoProgressTimeoutSeconds", 120f,
                new ConfigDescription(
                    "Abort a transfer only after this many seconds with NO progress (no ACK advance). " +
                    "Reported to the caller as failure; the peer connection is never closed.",
                    new AcceptableValueRange<float>(30f, 600f)));

            ConfigLogoutSaveWaitSeconds = config.Bind("Characters.Transport", "LogoutSaveWaitSeconds", 15f,
                new ConfigDescription(
                    "At logout, keep the connection open up to this many seconds until the server confirms it saved the " +
                    "character. A crossplay (PlayFab) link drops whatever is still queued when it closes, so without the wait " +
                    "the logout's save never arrives. The logout goes ahead at once if the connection is lost.",
                    new AcceptableValueRange<float>(2f, 60f)));

            ConfigBackupsToKeep = config.Bind("Characters", "BackupsToKeep", 25,
                new ConfigDescription("Rotating profile backups to keep per character (ZIP in the backups folder).",
                    new AcceptableValueRange<int>(1, 50)));

            SingleCharacterMode = config.Bind("Characters.Enforcement", "SingleCharacterMode", false,
                "When enabled, each Steam ID is restricted to a single stored character. Admins are exempt.");
            SingleCharacterAdminBypass = config.Bind("Characters.Enforcement", "SingleCharacterAdminBypass", true,
                "When ON (default), admins may join with any character and bypass single-character enforcement. " +
                "Turn OFF to also enforce single-character on admins (e.g. for testing).");
            ForceServerCharacter = config.Bind("Characters.Enforcement", "ForceServerCharacter", false,
                "When ON, a player connecting with a character that has already been played elsewhere " +
                "(single-player or another server) is sent back to the menu asking them to create a new " +
                "character first — no data is lost. Brand-new characters are allowed and become the player's " +
                "server character. Returning players get their stored server character. (ServerCharacters behavior.)");
            ForceServerCharacterAdminBypass = config.Bind("Characters.Enforcement", "ForceServerCharacterAdminBypass", true,
                "When ON (default), admins bypass ForceServerCharacter and keep their imported character. " +
                "Turn OFF to enforce ForceServerCharacter on admins too (e.g. for testing). " +
                "Mirrors SingleCharacterAdminBypass.");
            HardcoreMode = config.Bind("Characters.Enforcement", "HardcoreMode", false,
                "When enabled, a character is deleted on death and the player is logged out.");
            MaintenanceMode = config.Bind("Characters.Maintenance", "MaintenanceMode", false,
                "When enabled, non-admin clients are kicked on connect and the maintenance countdown is broadcast.");
            MaintenanceTimerSeconds = config.Bind("Characters.Maintenance", "MaintenanceTimerSeconds", 300,
                new ConfigDescription("Seconds of countdown before connected non-admins are kicked when MaintenanceMode is enabled.",
                    new AcceptableValueRange<int>(10, 1800)));
            AfkKickMinutes = config.Bind("Characters.Enforcement", "AfkKickMinutes", 0,
                new ConfigDescription("Minutes of input inactivity before a player is auto-logged-out. 0 disables.",
                    new AcceptableValueRange<int>(0, 60)));
            ExcludeAdminsFromAfk = config.Bind("Characters.Enforcement", "ExcludeAdminsFromAfk", true,
                "When enabled, admins are not AFK-kicked.");

            ServerKey = config.Bind("Characters", "ServerKey", string.Empty,
                "Master base64-encoded 32-byte server key used to derive per-connection AES keys for the " +
                "emergency-backup signature (anti-rollback). Auto-generated on first server start when empty. " +
                "Do not edit. Rotating it invalidates all outstanding emergency backups.");
        }

        public static void BindToSync(ConfigSync sync)
        {
            sync.AddConfigEntry(Enabled);
            sync.AddConfigEntry(ConfigChunkSizeBytes);
            sync.AddConfigEntry(ConfigQueueBackpressureBytes);
            sync.AddConfigEntry(ConfigCrossplayChunkSizeBytes);
            sync.AddConfigEntry(ConfigCrossplayInFlightBytes);
            sync.AddConfigEntry(ConfigNoProgressTimeoutSeconds);
            sync.AddConfigEntry(ConfigLogoutSaveWaitSeconds);
            sync.AddConfigEntry(ConfigBackupsToKeep);
            sync.AddConfigEntry(SingleCharacterMode);
            sync.AddConfigEntry(SingleCharacterAdminBypass);
            sync.AddConfigEntry(ForceServerCharacter);
            sync.AddConfigEntry(ForceServerCharacterAdminBypass);
            sync.AddConfigEntry(HardcoreMode);
            sync.AddConfigEntry(MaintenanceMode);
            sync.AddConfigEntry(MaintenanceTimerSeconds);
            sync.AddConfigEntry(AfkKickMinutes);
            sync.AddConfigEntry(ExcludeAdminsFromAfk);
        }
    }
}
