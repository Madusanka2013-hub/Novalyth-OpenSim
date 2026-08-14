/*
 * Novalyth OpenSim
 * Asset pipeline instrumentation for controlled performance measurements.
 */

using System;
using System.Text;
using System.Threading;

namespace OpenSim.Framework.Monitoring
{
    public static class NovalythAssetPipelineMetrics
    {
        private static readonly long[] s_boundsMs =
        {
            5, 10, 20, 50, 100, 250, 500, 1000,
            2500, 5000, 10000, 15000, 30000, 60000
        };

        private static readonly long[] s_capsQueueLatency = new long[s_boundsMs.Length + 1];
        private static readonly long[] s_backendLatency = new long[s_boundsMs.Length + 1];
        private static readonly long[] s_localLatency = new long[s_boundsMs.Length + 1];
        private static readonly long[] s_remoteLatency = new long[s_boundsMs.Length + 1];

        private static long s_capsRequests;
        private static long s_capsPending;
        private static long s_capsPeakPending;

        private static long s_backendStarted;
        private static long s_backendCompleted;
        private static long s_backendInFlight;
        private static long s_backendPeakInFlight;
        private static long s_backendTimeouts;
        private static long s_backendErrors;
        private static long s_backendNotFound;

        private static long s_memoryHits;
        private static long s_coalesced;

        private static long s_localPending;
        private static long s_localPeakPending;
        private static long s_localInFlight;
        private static long s_localPeakInFlight;
        private static long s_localCompleted;
        private static long s_localErrors;
        private static long s_localNotFound;

        private static long s_remotePending;
        private static long s_remotePeakPending;
        private static long s_remoteInFlight;
        private static long s_remotePeakInFlight;
        private static long s_remoteCompleted;
        private static long s_remoteErrors;
        private static long s_remoteNotFound;

        private static int s_capsWorkers = 3;
        private static int s_localWorkers = 2;
        private static int s_remoteWorkers = 2;
        private static int s_timeoutMs = 15000;

        private static void UpdatePeak(ref long target, long value)
        {
            while (true)
            {
                long current = Interlocked.Read(ref target);
                if (value <= current)
                    return;
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                    return;
            }
        }

        private static void Record(long[] histogram, long elapsedMs)
        {
            int i = 0;
            while (i < s_boundsMs.Length && elapsedMs > s_boundsMs[i])
                ++i;
            Interlocked.Increment(ref histogram[i]);
        }

        private static string Percentile(long[] histogram, double percentile)
        {
            long total = 0;
            for (int i = 0; i < histogram.Length; ++i)
                total += Interlocked.Read(ref histogram[i]);

            if (total == 0)
                return "n/a";

            long target = Math.Max(1, (long)Math.Ceiling(total * percentile));
            long cumulative = 0;
            for (int i = 0; i < histogram.Length; ++i)
            {
                cumulative += Interlocked.Read(ref histogram[i]);
                if (cumulative >= target)
                    return i < s_boundsMs.Length ? $"<={s_boundsMs[i]}ms" : ">60000ms";
            }
            return "n/a";
        }

        public static void SetCapsConfig(int workers, int timeoutMs)
        {
            Volatile.Write(ref s_capsWorkers, workers);
            Volatile.Write(ref s_timeoutMs, timeoutMs);
        }

        public static void SetRegionConfig(int localWorkers, int remoteWorkers)
        {
            Volatile.Write(ref s_localWorkers, localWorkers);
            Volatile.Write(ref s_remoteWorkers, remoteWorkers);
        }

        public static void CapsEnqueued()
        {
            Interlocked.Increment(ref s_capsRequests);
            long pending = Interlocked.Increment(ref s_capsPending);
            UpdatePeak(ref s_capsPeakPending, pending);
        }

        public static void CapsDequeued(long waitMs)
        {
            Interlocked.Decrement(ref s_capsPending);
            Record(s_capsQueueLatency, Math.Max(0, waitMs));
        }

        public static void BackendStarted()
        {
            Interlocked.Increment(ref s_backendStarted);
            long active = Interlocked.Increment(ref s_backendInFlight);
            UpdatePeak(ref s_backendPeakInFlight, active);
        }

        public static void BackendFinished(long elapsedMs, bool timeout, bool error, bool notFound)
        {
            Interlocked.Decrement(ref s_backendInFlight);
            Interlocked.Increment(ref s_backendCompleted);
            if (timeout) Interlocked.Increment(ref s_backendTimeouts);
            if (error) Interlocked.Increment(ref s_backendErrors);
            if (notFound) Interlocked.Increment(ref s_backendNotFound);
            Record(s_backendLatency, Math.Max(0, elapsedMs));
        }

        public static void MemoryHit() => Interlocked.Increment(ref s_memoryHits);
        public static void Coalesced() => Interlocked.Increment(ref s_coalesced);

        public static void RegionEnqueued(bool remote)
        {
            if (remote)
            {
                long pending = Interlocked.Increment(ref s_remotePending);
                UpdatePeak(ref s_remotePeakPending, pending);
            }
            else
            {
                long pending = Interlocked.Increment(ref s_localPending);
                UpdatePeak(ref s_localPeakPending, pending);
            }
        }

        public static void RegionDequeued(bool remote)
        {
            if (remote)
            {
                Interlocked.Decrement(ref s_remotePending);
                long active = Interlocked.Increment(ref s_remoteInFlight);
                UpdatePeak(ref s_remotePeakInFlight, active);
            }
            else
            {
                Interlocked.Decrement(ref s_localPending);
                long active = Interlocked.Increment(ref s_localInFlight);
                UpdatePeak(ref s_localPeakInFlight, active);
            }
        }

        public static void RegionFinished(bool remote, long elapsedMs, bool error, bool notFound)
        {
            if (remote)
            {
                Interlocked.Decrement(ref s_remoteInFlight);
                Interlocked.Increment(ref s_remoteCompleted);
                if (error) Interlocked.Increment(ref s_remoteErrors);
                if (notFound) Interlocked.Increment(ref s_remoteNotFound);
                Record(s_remoteLatency, Math.Max(0, elapsedMs));
            }
            else
            {
                Interlocked.Decrement(ref s_localInFlight);
                Interlocked.Increment(ref s_localCompleted);
                if (error) Interlocked.Increment(ref s_localErrors);
                if (notFound) Interlocked.Increment(ref s_localNotFound);
                Record(s_localLatency, Math.Max(0, elapsedMs));
            }
        }

        private static void AppendLatency(StringBuilder sb, string name, long[] histogram)
        {
            sb.AppendFormat("{0,-20} p50={1,-10} p95={2,-10} p99={3,-10}",
                name,
                Percentile(histogram, 0.50),
                Percentile(histogram, 0.95),
                Percentile(histogram, 0.99));
            sb.AppendLine();
        }

        public static string GetReport()
        {
            StringBuilder sb = new();
            sb.AppendLine("=== NOVALYTH ASSET PIPELINE ===");
            sb.AppendFormat("config: caps_workers={0} timeout_ms={1} local_workers={2} remote_workers={3}",
                Volatile.Read(ref s_capsWorkers), Volatile.Read(ref s_timeoutMs),
                Volatile.Read(ref s_localWorkers), Volatile.Read(ref s_remoteWorkers));
            sb.AppendLine();
            sb.AppendFormat("caps: requests={0} pending={1} peak_pending={2}",
                Interlocked.Read(ref s_capsRequests), Interlocked.Read(ref s_capsPending), Interlocked.Read(ref s_capsPeakPending));
            sb.AppendLine();
            sb.AppendFormat("backend: started={0} completed={1} inflight={2} peak_inflight={3} timeouts={4} errors={5} notfound={6}",
                Interlocked.Read(ref s_backendStarted), Interlocked.Read(ref s_backendCompleted),
                Interlocked.Read(ref s_backendInFlight), Interlocked.Read(ref s_backendPeakInFlight),
                Interlocked.Read(ref s_backendTimeouts), Interlocked.Read(ref s_backendErrors), Interlocked.Read(ref s_backendNotFound));
            sb.AppendLine();
            sb.AppendFormat("cache/coalesce: memory_hits={0} coalesced={1}",
                Interlocked.Read(ref s_memoryHits), Interlocked.Read(ref s_coalesced));
            sb.AppendLine();
            sb.AppendFormat("local: pending={0} peak_pending={1} inflight={2} peak_inflight={3} completed={4} errors={5} notfound={6}",
                Interlocked.Read(ref s_localPending), Interlocked.Read(ref s_localPeakPending),
                Interlocked.Read(ref s_localInFlight), Interlocked.Read(ref s_localPeakInFlight),
                Interlocked.Read(ref s_localCompleted), Interlocked.Read(ref s_localErrors), Interlocked.Read(ref s_localNotFound));
            sb.AppendLine();
            sb.AppendFormat("remote: pending={0} peak_pending={1} inflight={2} peak_inflight={3} completed={4} errors={5} notfound={6}",
                Interlocked.Read(ref s_remotePending), Interlocked.Read(ref s_remotePeakPending),
                Interlocked.Read(ref s_remoteInFlight), Interlocked.Read(ref s_remotePeakInFlight),
                Interlocked.Read(ref s_remoteCompleted), Interlocked.Read(ref s_remoteErrors), Interlocked.Read(ref s_remoteNotFound));
            sb.AppendLine();
            AppendLatency(sb, "CAPS queue wait", s_capsQueueLatency);
            AppendLatency(sb, "CAPS backend", s_backendLatency);
            AppendLatency(sb, "region local", s_localLatency);
            AppendLatency(sb, "region remote", s_remoteLatency);
            return sb.ToString();
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref s_capsRequests, 0);
            Interlocked.Exchange(ref s_capsPeakPending, Interlocked.Read(ref s_capsPending));
            Interlocked.Exchange(ref s_backendStarted, 0);
            Interlocked.Exchange(ref s_backendCompleted, 0);
            Interlocked.Exchange(ref s_backendPeakInFlight, Interlocked.Read(ref s_backendInFlight));
            Interlocked.Exchange(ref s_backendTimeouts, 0);
            Interlocked.Exchange(ref s_backendErrors, 0);
            Interlocked.Exchange(ref s_backendNotFound, 0);
            Interlocked.Exchange(ref s_memoryHits, 0);
            Interlocked.Exchange(ref s_coalesced, 0);
            Interlocked.Exchange(ref s_localPeakPending, Interlocked.Read(ref s_localPending));
            Interlocked.Exchange(ref s_localPeakInFlight, Interlocked.Read(ref s_localInFlight));
            Interlocked.Exchange(ref s_localCompleted, 0);
            Interlocked.Exchange(ref s_localErrors, 0);
            Interlocked.Exchange(ref s_localNotFound, 0);
            Interlocked.Exchange(ref s_remotePeakPending, Interlocked.Read(ref s_remotePending));
            Interlocked.Exchange(ref s_remotePeakInFlight, Interlocked.Read(ref s_remoteInFlight));
            Interlocked.Exchange(ref s_remoteCompleted, 0);
            Interlocked.Exchange(ref s_remoteErrors, 0);
            Interlocked.Exchange(ref s_remoteNotFound, 0);
            Array.Clear(s_capsQueueLatency, 0, s_capsQueueLatency.Length);
            Array.Clear(s_backendLatency, 0, s_backendLatency.Length);
            Array.Clear(s_localLatency, 0, s_localLatency.Length);
            Array.Clear(s_remoteLatency, 0, s_remoteLatency.Length);
        }
    }
}
