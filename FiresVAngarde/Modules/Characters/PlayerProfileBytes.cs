using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Deserialize a player profile from wire bytes without touching disk. The body below is replaced at load time by
    /// the transpiler with <c>PlayerProfile.LoadPlayerFromDisk</c>'s own IL, except the call that reads the profile
    /// from disk (<c>this.LoadPlayerDataFromDisk()</c>) is swapped for <c>new ZPackage(data)</c>, so the rest of the
    /// vanilla load path runs against the in-memory bytes. Symmetric with the send side, which uses
    /// <c>PlayerProfile.LoadPlayerDataFromDisk().GetArray()</c>.
    ///
    /// ServerCharacters' trick, found by the call rather than by position: 1.0's loader starts with a
    /// <c>Stopwatch.StartNew()</c>, so replacing "the first two instructions" (the pre-1.0 layout) left the disk read in
    /// place, and every uploaded profile loaded the server's own missing file instead and was never saved
    /// (Tools\DEDI_LOGOUT_COSTS.md, 2026-09-28).
    /// </summary>
    public static class PlayerProfileBytes
    {
        public static bool LoadFromBytes(this PlayerProfile profile, byte[] data)
        {
            throw new NotImplementedException("Body is supplied by the transpiler in LoadFromBytesPatch.");
        }

        [HarmonyPatch(typeof(PlayerProfileBytes), nameof(LoadFromBytes))]
        private static class LoadFromBytesPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(ILGenerator il)
            {
                List<CodeInstruction> source = PatchProcessor.GetOriginalInstructions(
                    AccessTools.DeclaredMethod(typeof(PlayerProfile), "LoadPlayerFromDisk"), il);
                var readFromDisk = AccessTools.DeclaredMethod(typeof(PlayerProfile), "LoadPlayerDataFromDisk");
                var fromBytes = AccessTools.DeclaredConstructor(typeof(ZPackage), new[] { typeof(byte[]) });

                bool replaced = false;
                for (int i = 0; i < source.Count; i++)
                {
                    if (!replaced && i + 1 < source.Count && source[i].IsLdarg(0) && source[i + 1].Calls(readFromDisk))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(source[i]).MoveBlocksFrom(source[i]);
                        yield return new CodeInstruction(OpCodes.Newobj, fromBytes).MoveLabelsFrom(source[i + 1]).MoveBlocksFrom(source[i + 1]);
                        i++;
                        replaced = true;
                        continue;
                    }
                    yield return source[i];
                }
                if (!replaced)
                    Debug.LogError("[Characters] LoadFromBytes: PlayerProfile.LoadPlayerFromDisk no longer calls LoadPlayerDataFromDisk; "
                                   + "uploaded profiles cannot be read, so server-held characters are not saved.");
            }
        }
    }
}
