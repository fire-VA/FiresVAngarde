using HarmonyLib;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Lifecycle hooks for the character subsystem: bind the transport RPCs once the routed-RPC
    /// layer is alive (Game.Start), and register the diagnostic console command when the terminal
    /// initializes.
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersPatches
    {
        [HarmonyPatch(typeof(Game), nameof(Game.Start))]
        [HarmonyPostfix]
        private static void Game_Start_Postfix()
        {
            CharactersModule.OnReady();
        }

        private static bool _cmdAdded;

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        [HarmonyPostfix]
        private static void Terminal_InitTerminal_Postfix()
        {
            if (_cmdAdded) return;
            _cmdAdded = true;
            CharactersModule.RegisterConsoleCommands();
        }
    }
}
