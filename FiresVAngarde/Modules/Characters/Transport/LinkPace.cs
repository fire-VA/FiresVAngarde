using System;
using System.Collections.Generic;
using HarmonyLib;

namespace VerdantsAscent.Modules.Characters.Transport
{
    /// <summary>
    /// How fast a profile may go out on one connection. A crossplay (PlayFab Party) link gets small chunks and a cap on
    /// its bytes in flight: M1's first crossplay join dropped ("PlayFab network error ... invalid handle") the moment the
    /// profile went out as one ~131 KB message amid the join's other traffic, while the bot's small one held. Vanilla's
    /// ZPlayFabSocket reports only a quarter of its in-flight bytes as the send queue (INFLIGHT_SCALING_FACTOR), so the
    /// Steam-sized queue test let ~800 KB fly there. Steam links keep the configured chunk size and queue test.
    /// </summary>
    internal static class LinkPace
    {
        // ZPlayFabSocket.GetSendQueueSize() = in-flight bytes * 0.25.
        private const int PlayFabQueueReportDivisor = 4;
        // The join's buffering sockets (VAngarde's own and ServerSync's) derive from ZPlayFabSocket and keep the real
        // socket in "Original", so a Steam link mid-join looks like PlayFab until they are looked through.
        private const string WrapperTypeName = "BufferingSocket";
        private const string WrappedField = "Original";
        private const int MaxWrapDepth = 4;
        private const int MinChunkBytes = 16 * 1024;
        private const int MaxChunkBytes = 480 * 1024;

        private static readonly Dictionary<Type, System.Reflection.FieldInfo> s_wrappedFields = new Dictionary<Type, System.Reflection.FieldInfo>();

        internal static bool IsCrossplay(ISocket socket) => Innermost(socket)?.GetType() == typeof(ZPlayFabSocket);

        internal static int ChunkBytes(ISocket socket) => IsCrossplay(socket)
            ? CharactersConfig.CrossplayChunkSizeBytes
            : Math.Max(MinChunkBytes, Math.Min(MaxChunkBytes, CharactersConfig.ChunkSizeBytes));

        // Everything on the connection counts, not just this transfer, so the profile shares the cap with world data.
        internal static bool Saturated(ISocket socket)
        {
            if (socket == null) return false;
            int reported = socket.GetSendQueueSize();
            return IsCrossplay(socket)
                ? reported * PlayFabQueueReportDivisor > CharactersConfig.CrossplayInFlightBytes
                : reported > CharactersConfig.QueueBackpressureBytes;
        }

        internal static string Describe(ISocket socket) => IsCrossplay(socket)
            ? $"crossplay link: {CharactersConfig.CrossplayChunkSizeBytes / 1024} KB chunks, at most {CharactersConfig.CrossplayInFlightBytes / 1024} KB in flight"
            : "steam link";

        private static ISocket Innermost(ISocket socket)
        {
            for (int depth = 0; socket != null && depth < MaxWrapDepth && socket.GetType().Name == WrapperTypeName; depth++)
            {
                if (!(WrappedFieldOf(socket.GetType())?.GetValue(socket) is ISocket inner)) break;
                socket = inner;
            }
            return socket;
        }

        private static System.Reflection.FieldInfo WrappedFieldOf(Type type)
        {
            if (!s_wrappedFields.TryGetValue(type, out var field))
            {
                field = AccessTools.Field(type, WrappedField);
                s_wrappedFields[type] = field;
            }
            return field;
        }
    }
}
