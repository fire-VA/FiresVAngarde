using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// One background thread for the mod's disk work (rewriting the character backups' rotating ZIP, caching client logs), run
    /// in the order it was queued, so two saves of one character never interleave. The server's main thread only hands over bytes it has
    /// already read (Tools\DEDI_LOGOUT_COSTS.md: the logout save stalled the dedi 160 ms). Drained when the plugin shuts
    /// down, so a backup queued just before quit still lands.
    /// </summary>
    internal static class CharacterDiskWriter
    {
        private const int DrainTimeoutMs = 10000;
        private const string ThreadName = "FiresVAngarde.CharacterDisk";

        private static readonly BlockingCollection<Action> s_jobs = new BlockingCollection<Action>();
        private static readonly object s_startLock = new object();
        private static Thread s_thread;
        private static int s_queued;

        internal static void Enqueue(Action job)
        {
            if (job == null) return;
            EnsureThread();
            Interlocked.Increment(ref s_queued);
            try { s_jobs.Add(job); }
            catch (InvalidOperationException) { Run(job); }
        }

        internal static void Drain()
        {
            if (s_thread == null) return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var done = new ManualResetEventSlim(false);
            try { s_jobs.Add(() => done.Set()); }
            catch (InvalidOperationException) { return; }
            if (done.Wait(DrainTimeoutMs))
                Debug.Log($"[Characters] shutdown: character backup queue drained in {timer.ElapsedMilliseconds} ms ({s_queued} backup(s) this session).");
            else
                Debug.LogWarning($"[Characters] character backups still writing after {DrainTimeoutMs / 1000} s at shutdown.");
        }

        /// <summary>Block until everything queued so far is written (a few seconds at most). For a rare save that must follow them.</summary>
        internal static void WaitIdle(int timeoutMs = 5000)
        {
            if (s_thread == null || Thread.CurrentThread == s_thread) return;
            var done = new ManualResetEventSlim(false);
            try { s_jobs.Add(() => done.Set()); }
            catch (InvalidOperationException) { return; }
            if (!done.Wait(timeoutMs))
                Debug.LogWarning($"[Characters] the disk thread was still busy after {timeoutMs} ms; saving anyway.");
        }

        private static void EnsureThread()
        {
            lock (s_startLock)
            {
                if (s_thread != null) return;
                s_thread = new Thread(Loop) { IsBackground = true, Name = ThreadName };
                s_thread.Start();
            }
        }

        private static void Loop()
        {
            foreach (var job in s_jobs.GetConsumingEnumerable()) Run(job);
        }

        private static void Run(Action job)
        {
            try { job(); }
            catch (Exception ex) { Debug.LogWarning($"[Characters] background disk job failed: {ex.Message}"); }
        }
    }
}
