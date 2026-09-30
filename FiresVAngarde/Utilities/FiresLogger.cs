using FiresCore.Logging;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    // FiresVAngarde's tagged logger. Thin wrapper over the shared FiresCore FiresLog engine so
    // logging behavior lives in one place; only the mod tag and verbose source are mod-specific.
    public static class FiresLogger
    {
        private static readonly FiresLog Log = new FiresLog(
            "FiresVAngarde",
            () => ConfigManager.Instance?.configVerboseLogging?.Value ?? false);

        public static bool VerboseEnabled => Log.VerboseEnabled;

        public static void LogInfo(string message) => Log.Info(message);

        public static void LogVerbose(string message) => Log.Verbose(message);

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
