using UnityEngine;
using VerdantsAscent.Modules.Characters.Transport;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// Bootstrap for the character subsystem: registers the reliable-chunk transport RPCs and the
    /// diagnostic console command. The profile lifecycle (login send, save, backups, enforcement)
    /// is layered on top of <see cref="ReliableChunkChannel"/> in later work.
    /// </summary>
    public static class CharactersModule
    {
        // Diagnostic channel used by `va_chan_test` to prove large transfers don't time out.
        private const string TestChannel = "va.test";

        public static void OnReady()
        {
            if (CharactersConfig.Enabled != null && !CharactersConfig.Enabled.Value) return;
            ReliableChunkChannel.EnsureRpcsRegistered();
            ReliableChunkChannel.RegisterReceiver(TestChannel, OnTestPayload);
        }

        public static void RegisterConsoleCommands()
        {
            new Terminal.ConsoleCommand("va_chan_test",
                "va_chan_test <sizeKB> — stream a test payload over the reliable chunk channel to prove " +
                "large transfers complete without the ServerCharacters-style 30s timeout.",
                CmdChanTest, isCheat: true, isNetwork: false, onlyServer: false);
        }

        private static void CmdChanTest(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                args.Context?.AddString("[Characters] not connected to a world.");
                return;
            }

            int sizeKB = 4096;
            if (args.Length >= 2) int.TryParse(args[1], out sizeKB);
            sizeKB = Mathf.Clamp(sizeKB, 1, 64 * 1024);
            int bytes = sizeKB * 1024;

            var payload = new byte[bytes];
            for (int i = 0; i < bytes; i++) payload[i] = (byte)(i & 0xFF);

            long target;
            if (ZNet.instance.IsServer())
            {
                var peers = ZNet.instance.GetPeers();
                if (peers == null || peers.Count == 0)
                {
                    args.Context?.AddString("[Characters] no connected peers to send to.");
                    return;
                }
                target = peers[0].m_uid;
            }
            else
            {
                target = ZRoutedRpc.instance.GetServerPeerID();
            }

            float start = Time.realtimeSinceStartup;
            args.Context?.AddString($"[Characters] streaming {sizeKB} KB to {target}…");
            ReliableChunkChannel.Send(target, TestChannel, payload, ok =>
            {
                float secs = Time.realtimeSinceStartup - start;
                Debug.Log($"[Characters][test] send {(ok ? "CONFIRMED" : "FAILED")} in {secs:0.0}s ({sizeKB} KB).");
            });
        }

        private static void OnTestPayload(long sender, byte[] payload)
        {
            bool ok = true;
            for (int i = 0; i < payload.Length; i++)
                if (payload[i] != (byte)(i & 0xFF)) { ok = false; break; }
            Debug.Log($"[Characters][test] received {payload.Length}B from {sender} — pattern {(ok ? "OK" : "CORRUPT")}.");
        }
    }
}
