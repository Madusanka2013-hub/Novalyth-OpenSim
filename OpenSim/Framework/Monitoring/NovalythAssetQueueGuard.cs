// NOVALYTH R1 Stage 4
// Asset-specific queue admission control, backpressure and per-agent fairness.
//
// This does NOT change ObjectJobEngine globally. Instead it bounds the number
// of accepted Asset CAPS requests before they enter the existing worker pool.

using System;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;

namespace OpenSim.Framework.Monitoring
{
    public static class NovalythAssetQueueGuard
    {
        private static readonly object s_lock = new();
        private static readonly Dictionary<UUID, int> s_perAgentOutstanding = new();

        private static int s_maxOutstanding;
        private static int s_maxOutstandingPerAgent;

        private static int s_outstanding;
        private static int s_peakOutstanding;
        private static int s_peakPerAgent;
        private static int s_peakActiveAgents;

        private static long s_accepted;
        private static long s_released;
        private static long s_rejectedGlobal;
        private static long s_rejectedPerAgent;
        private static long s_enqueueFailures;

        public static void Configure(int maxOutstanding, int maxOutstandingPerAgent)
        {
            if (maxOutstanding < 0)
                maxOutstanding = 0;
            if (maxOutstandingPerAgent < 0)
                maxOutstandingPerAgent = 0;

            if (maxOutstanding > 0
                && maxOutstandingPerAgent > maxOutstanding)
            {
                maxOutstandingPerAgent = maxOutstanding;
            }

            lock (s_lock)
            {
                s_maxOutstanding = maxOutstanding;
                s_maxOutstandingPerAgent = maxOutstandingPerAgent;

                if (s_peakOutstanding < s_outstanding)
                    s_peakOutstanding = s_outstanding;

                UpdatePeakActiveAgentsLocked();
            }
        }

        public static bool TryAcquire(UUID agentID, out bool tracked, out string rejectReason)
        {
            tracked = false;
            rejectReason = null;

            lock (s_lock)
            {
                bool enabled = s_maxOutstanding > 0 || s_maxOutstandingPerAgent > 0;
                if (!enabled)
                    return true;

                if (s_maxOutstanding > 0 && s_outstanding >= s_maxOutstanding)
                {
                    ++s_rejectedGlobal;
                    rejectReason = "global-capacity";
                    return false;
                }

                s_perAgentOutstanding.TryGetValue(agentID, out int agentOutstanding);

                if (s_maxOutstandingPerAgent > 0
                    && agentOutstanding >= s_maxOutstandingPerAgent)
                {
                    ++s_rejectedPerAgent;
                    rejectReason = "per-agent-limit";
                    return false;
                }

                ++s_outstanding;
                ++agentOutstanding;
                s_perAgentOutstanding[agentID] = agentOutstanding;

                ++s_accepted;
                tracked = true;

                if (s_outstanding > s_peakOutstanding)
                    s_peakOutstanding = s_outstanding;

                if (agentOutstanding > s_peakPerAgent)
                    s_peakPerAgent = agentOutstanding;

                UpdatePeakActiveAgentsLocked();
                return true;
            }
        }

        public static void Release(UUID agentID, bool enqueueFailure = false)
        {
            lock (s_lock)
            {
                if (enqueueFailure)
                    ++s_enqueueFailures;

                if (s_outstanding > 0)
                    --s_outstanding;

                if (s_perAgentOutstanding.TryGetValue(agentID, out int agentOutstanding))
                {
                    --agentOutstanding;
                    if (agentOutstanding <= 0)
                        s_perAgentOutstanding.Remove(agentID);
                    else
                        s_perAgentOutstanding[agentID] = agentOutstanding;
                }

                ++s_released;
            }
        }

        public static void ResetMetrics()
        {
            lock (s_lock)
            {
                s_accepted = 0;
                s_released = 0;
                s_rejectedGlobal = 0;
                s_rejectedPerAgent = 0;
                s_enqueueFailures = 0;

                s_peakOutstanding = s_outstanding;
                s_peakPerAgent = 0;
                foreach (int value in s_perAgentOutstanding.Values)
                {
                    if (value > s_peakPerAgent)
                        s_peakPerAgent = value;
                }

                s_peakActiveAgents = s_perAgentOutstanding.Count;
            }
        }

        public static string FormatReport()
        {
            lock (s_lock)
            {
                StringBuilder sb = new();
                sb.AppendLine("=== NOVALYTH ASSET QUEUE GUARD ===");
                sb.Append("config: enabled=")
                    .Append(s_maxOutstanding > 0 || s_maxOutstandingPerAgent > 0 ? "true" : "false")
                    .Append(" max_outstanding=").Append(s_maxOutstanding)
                    .Append(" per_agent=").Append(s_maxOutstandingPerAgent)
                    .AppendLine();

                sb.Append("queue: outstanding=").Append(s_outstanding)
                    .Append(" peak=").Append(s_peakOutstanding)
                    .Append(" active_agents=").Append(s_perAgentOutstanding.Count)
                    .Append(" peak_active_agents=").Append(s_peakActiveAgents)
                    .Append(" peak_per_agent=").Append(s_peakPerAgent)
                    .AppendLine();

                sb.Append("traffic: accepted=").Append(s_accepted)
                    .Append(" released=").Append(s_released)
                    .Append(" reject_global=").Append(s_rejectedGlobal)
                    .Append(" reject_agent=").Append(s_rejectedPerAgent)
                    .Append(" enqueue_fail=").Append(s_enqueueFailures);

                return sb.ToString();
            }
        }

        private static void UpdatePeakActiveAgentsLocked()
        {
            if (s_perAgentOutstanding.Count > s_peakActiveAgents)
                s_peakActiveAgents = s_perAgentOutstanding.Count;
        }
    }
}
