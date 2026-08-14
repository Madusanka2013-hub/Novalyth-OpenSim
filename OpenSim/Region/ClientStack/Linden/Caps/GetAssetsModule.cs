/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using Mono.Addins;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Console;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Capabilities.Handlers;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GetAssetsModule")]
    public class GetAssetsModule : INonSharedRegionModule
    {
//        private static readonly ILog m_log =
//            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_Enabled;
        // C4B: eight workers is the low-latency baseline for modern HTTP/J2K
        // texture traffic. Operators can still tune 1..64 in the INI.
        private int m_capsAssetWorkers = 8;
        private int m_assetFetchTimeoutMs = 15000;
        private int m_textureHotCacheMB = 512;
        private int m_textureHotCacheTTLSeconds = 900;
        private int m_textureHotCacheMaxAssetMB = 16;

        private string m_GetTextureURL;
        private string m_GetMeshURL;
        private string m_GetMesh2URL;
        private string m_GetAssetURL;

        class APollRequest
        {
            public PollServiceAssetEventArgs thepoll;
            public UUID reqID;
            public OSHttpRequest request;
            public long enqueuedAtMs;
            public UUID queueAgentID;
            public bool queueGuardTracked;
        }

        public class APollResponse
        {
            public OSHttpResponse osresponse;
        }

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static IAssetService m_assetService = null;
        private static GetAssetsHandler m_getAssetHandler;
        private static ObjectJobEngine m_workerpool = null;
        private static int m_NumberScenes = 0;
        private static object m_loadLock = new object();
        private static int m_commandsRegistered = 0;
        private static bool m_assetResponseWake = false;
        private static int m_assetMaxOutstanding = 0;
        private static int m_assetMaxOutstandingPerAgent = 0;
        private static int m_queueCommandsRegistered = 0;
        private static int m_assetFairDispatchSlots = 8;
        private static bool m_assetFairStopping = false;

        private static readonly object m_assetFairLock = new();
        private static readonly Dictionary<UUID, Queue<APollRequest>> m_assetFairQueues = new();
        private static readonly Queue<UUID> m_assetFairRoundRobin = new();
        private static int m_assetFairQueued = 0;
        private static int m_assetFairPeakQueued = 0;
        private static int m_assetFairInFlight = 0;
        private static int m_assetFairPeakInFlight = 0;
        private static int m_assetFairPeakAgents = 0;
        private static long m_assetFairDispatched = 0;
        private static long m_assetFairWorkerEnqueueFailures = 0;

        protected IUserManagement m_UserManagement = null;

        private static void RegisterAssetQueueCommands()
        {
            if (System.Threading.Interlocked.CompareExchange(
                    ref m_queueCommandsRegistered, 1, 0) != 0)
                return;

            MainConsole.Instance.Commands.AddCommand(
                "Assets", false,
                "show asset queue",
                "show asset queue",
                "Show Novalyth Asset CAPS queue guard/backpressure metrics",
                string.Empty,
                HandleShowAssetQueue);

            MainConsole.Instance.Commands.AddCommand(
                "Assets", false,
                "reset asset queue",
                "reset asset queue",
                "Reset Novalyth Asset CAPS queue guard/backpressure metrics",
                string.Empty,
                HandleResetAssetQueue);
        }

        private static void HandleShowAssetQueue(string module, string[] cmd)
        {
            MainConsole.Instance.Output(NovalythAssetQueueGuard.FormatReport());
            MainConsole.Instance.Output(FormatAssetFairQueueReport());
        }

        private static void HandleResetAssetQueue(string module, string[] cmd)
        {
            NovalythAssetQueueGuard.ResetMetrics();
            ResetAssetFairQueueMetrics();
            MainConsole.Instance.Output("[NOVALYTH] Asset queue/fair-dispatch metrics reset.");
        }

        private static OSHttpResponse BuildAssetBackpressureResponse(
            OSHttpRequest request, string reason)
        {
            OSHttpResponse response = new(request)
            {
                StatusCode = (int)System.Net.HttpStatusCode.ServiceUnavailable,
                ContentType = "text/plain",
                ContentLength64 = 0,
                RawBuffer = Array.Empty<byte>(),
                RawBufferLen = 0,
                KeepAlive = true
            };

            response.AddHeader("Retry-After", "1");
            response.AddHeader(
                "X-Novalyth-Asset-Backpressure",
                string.IsNullOrEmpty(reason) ? "capacity" : reason);

            return response;
        }

        private static bool EnqueueFairAssetRequest(APollRequest request)
        {
            if (request == null || request.reqID.IsZero())
                return false;

            lock (m_assetFairLock)
            {
                if (m_assetFairStopping || m_workerpool == null)
                    return false;

                if (!m_assetFairQueues.TryGetValue(
                        request.queueAgentID,
                        out Queue<APollRequest> queue))
                {
                    queue = new Queue<APollRequest>();
                    m_assetFairQueues[request.queueAgentID] = queue;
                    m_assetFairRoundRobin.Enqueue(request.queueAgentID);

                    if (m_assetFairQueues.Count > m_assetFairPeakAgents)
                        m_assetFairPeakAgents = m_assetFairQueues.Count;
                }

                queue.Enqueue(request);
                ++m_assetFairQueued;

                if (m_assetFairQueued > m_assetFairPeakQueued)
                    m_assetFairPeakQueued = m_assetFairQueued;

                DispatchFairAssetRequestsLocked();
                return true;
            }
        }

        private static void CompleteFairAssetRequest()
        {
            lock (m_assetFairLock)
            {
                if (m_assetFairInFlight > 0)
                    --m_assetFairInFlight;

                DispatchFairAssetRequestsLocked();
            }
        }

        private static void DispatchFairAssetRequestsLocked()
        {
            while (!m_assetFairStopping
                && m_workerpool != null
                && m_assetFairInFlight < m_assetFairDispatchSlots
                && m_assetFairRoundRobin.Count > 0)
            {
                UUID agentID = m_assetFairRoundRobin.Dequeue();

                if (!m_assetFairQueues.TryGetValue(
                        agentID,
                        out Queue<APollRequest> queue)
                    || queue.Count == 0)
                {
                    m_assetFairQueues.Remove(agentID);
                    continue;
                }

                APollRequest next = queue.Dequeue();
                --m_assetFairQueued;

                if (queue.Count > 0)
                    m_assetFairRoundRobin.Enqueue(agentID);
                else
                    m_assetFairQueues.Remove(agentID);

                ++m_assetFairInFlight;
                ++m_assetFairDispatched;

                if (m_assetFairInFlight > m_assetFairPeakInFlight)
                    m_assetFairPeakInFlight = m_assetFairInFlight;

                if (!m_workerpool.Enqueue(next))
                {
                    --m_assetFairInFlight;
                    ++m_assetFairWorkerEnqueueFailures;

                    if (next.queueGuardTracked)
                    {
                        NovalythAssetQueueGuard.Release(
                            next.queueAgentID,
                            enqueueFailure: true);
                        next.queueGuardTracked = false;
                    }

                    next.thepoll.FailQueuedRequest(
                        next,
                        "worker-unavailable");
                }
            }
        }

        private static int StopAndDrainFairAssetRequests()
        {
            List<APollRequest> drained = new();

            lock (m_assetFairLock)
            {
                m_assetFairStopping = true;

                foreach (Queue<APollRequest> queue in m_assetFairQueues.Values)
                {
                    while (queue.Count > 0)
                        drained.Add(queue.Dequeue());
                }

                m_assetFairQueues.Clear();
                m_assetFairRoundRobin.Clear();
                m_assetFairQueued = 0;
            }

            foreach (APollRequest request in drained)
            {
                if (request.queueGuardTracked)
                {
                    NovalythAssetQueueGuard.Release(request.queueAgentID);
                    request.queueGuardTracked = false;
                }

                try
                {
                    request.thepoll.FailQueuedRequest(
                        request,
                        "region-shutdown");
                }
                catch (Exception e)
                {
                    m_log.WarnFormat(
                        "[GETASSETS]: failed to finish queued asset request during shutdown: {0}",
                        e.Message);
                }
            }

            return drained.Count;
        }

        private static bool WaitForFairAssetInflightDrain(int timeoutMs)
        {
            long deadline = Environment.TickCount64 + Math.Max(1000, timeoutMs);

            while (true)
            {
                lock (m_assetFairLock)
                {
                    if (m_assetFairInFlight <= 0)
                        return true;
                }

                if (Environment.TickCount64 >= deadline)
                    return false;

                Thread.Sleep(10);
            }
        }

        private static string FormatAssetFairQueueReport()
        {
            lock (m_assetFairLock)
            {
                return string.Format(
                    "=== NOVALYTH ASSET FAIR DISPATCH ===\n" +
                    "queue: queued={0} peak_queued={1} inflight={2} peak_inflight={3} active_agents={4} peak_agents={5}\n" +
                    "traffic: dispatched={6} worker_enqueue_fail={7}",
                    m_assetFairQueued,
                    m_assetFairPeakQueued,
                    m_assetFairInFlight,
                    m_assetFairPeakInFlight,
                    m_assetFairQueues.Count,
                    m_assetFairPeakAgents,
                    m_assetFairDispatched,
                    m_assetFairWorkerEnqueueFailures);
            }
        }

        private static void ResetAssetFairQueueMetrics()
        {
            lock (m_assetFairLock)
            {
                m_assetFairPeakQueued = m_assetFairQueued;
                m_assetFairPeakInFlight = m_assetFairInFlight;
                m_assetFairPeakAgents = m_assetFairQueues.Count;
                m_assetFairDispatched = 0;
                m_assetFairWorkerEnqueueFailures = 0;
            }
        }

        #region Region Module interfaceBase Members

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            m_capsAssetWorkers = Math.Clamp(config.GetInt("Cap_AssetWorkers", 8), 1, 64);
            m_assetFetchTimeoutMs = Math.Clamp(
                config.GetInt("Cap_AssetFetchTimeoutMs", 15000), 1000, 120000);
            // Wake the poll response immediately when a worker finishes rather
            // than waiting for the next poll tick. This directly improves
            // time-to-first-range for progressive J2K textures.
            m_assetResponseWake = config.GetBoolean("Cap_AssetResponseWake", true);
            m_textureHotCacheMB = Math.Clamp(
                config.GetInt("Cap_TextureHotCacheMB", 512), 0, 4096);
            m_textureHotCacheTTLSeconds = Math.Clamp(
                config.GetInt("Cap_TextureHotCacheTTLSeconds", 900), 30, 86400);
            m_textureHotCacheMaxAssetMB = Math.Clamp(
                config.GetInt("Cap_TextureHotCacheMaxAssetMB", 16), 1, 64);

            GetAssetsHandler.ConfigureTextureHotCache(
                m_textureHotCacheMB,
                m_textureHotCacheTTLSeconds,
                m_textureHotCacheMaxAssetMB);
            m_assetMaxOutstanding = Math.Clamp(
                config.GetInt("Cap_AssetMaxOutstanding", 0), 0, 8192);
            m_assetMaxOutstandingPerAgent = Math.Clamp(
                config.GetInt("Cap_AssetMaxOutstandingPerAgent", 0), 0, 2048);

            if (m_assetMaxOutstanding > 0
                && m_assetMaxOutstandingPerAgent > m_assetMaxOutstanding)
            {
                m_assetMaxOutstandingPerAgent = m_assetMaxOutstanding;
            }

            NovalythAssetQueueGuard.Configure(
                m_assetMaxOutstanding,
                m_assetMaxOutstandingPerAgent);
            RegisterAssetQueueCommands();

            m_GetTextureURL = config.GetString("Cap_GetTexture", string.Empty);
            if (m_GetTextureURL != string.Empty)
                m_Enabled = true;

            m_GetMeshURL = config.GetString("Cap_GetMesh", string.Empty);
            if (m_GetMeshURL != string.Empty)
                m_Enabled = true;

            m_GetMesh2URL = config.GetString("Cap_GetMesh2", string.Empty);
            if (m_GetMesh2URL != string.Empty)
                m_Enabled = true;

            m_GetAssetURL = config.GetString("Cap_GetAsset", string.Empty);
            if (m_GetAssetURL != string.Empty)
                m_Enabled = true;
        }

        public void AddRegion(Scene pScene)
        {
            if (!m_Enabled)
                return;

            m_scene = pScene;
        }

        public void RemoveRegion(Scene s)
        {
            if (!m_Enabled)
                return;

            s.EventManager.OnRegisterCaps -= RegisterCaps;
            m_NumberScenes--;
            m_scene = null;
        }

        public void RegionLoaded(Scene s)
        {
            if (!m_Enabled)
                return;

            lock(m_loadLock)
            {
                if (m_assetService == null)
                {
                    m_assetService = s.RequestModuleInterface<IAssetService>();
                    // We'll reuse the same handler for all requests.
                    m_getAssetHandler = new GetAssetsHandler(m_assetService, m_assetFetchTimeoutMs);
                }

                if (m_assetService == null)
                {
                    m_Enabled = false;
                    return;
                }

                if(m_UserManagement == null)
                    m_UserManagement = s.RequestModuleInterface<IUserManagement>();

                s.EventManager.OnRegisterCaps += RegisterCaps;

                m_NumberScenes++;

                lock (m_assetFairLock)
                {
                    m_assetFairStopping = false;
                    m_assetFairDispatchSlots = m_capsAssetWorkers;
                }

                if (m_workerpool == null)
                {
                    m_assetFairDispatchSlots = m_capsAssetWorkers;
                    m_workerpool = new ObjectJobEngine(
                        DoAssetRequests, "GetCapsAssetWorker", 1000, m_capsAssetWorkers);
                    NovalythAssetPipelineMetrics.SetCapsConfig(
                        m_capsAssetWorkers, m_assetFetchTimeoutMs);
                    m_log.InfoFormat(
                        "[GETASSETS]: CAPS asset workers={0}, fetch timeout={1} ms",
                        m_capsAssetWorkers, m_assetFetchTimeoutMs);
                    m_log.InfoFormat(
                        "[GETASSETS]: asset response wake={0}",
                        m_assetResponseWake ? "enabled" : "disabled");
                    m_log.InfoFormat(
                        "[GETASSETS]: asset queue guard max_outstanding={0}, per_agent={1}",
                        m_assetMaxOutstanding,
                        m_assetMaxOutstandingPerAgent);
                    m_log.InfoFormat(
                        "[GETASSETS]: fair asset dispatch=enabled, dispatch_slots={0}",
                        m_capsAssetWorkers);
                    m_log.InfoFormat(
                        "[NOVALYTH TEXTURE C4B]: hot-cache max={0}MB ttl={1}s max_asset={2}MB; response_wake={3}",
                        m_textureHotCacheMB,
                        m_textureHotCacheTTLSeconds,
                        m_textureHotCacheMaxAssetMB,
                        m_assetResponseWake ? "enabled" : "disabled");
                }

                if (Interlocked.CompareExchange(ref m_commandsRegistered, 1, 0) == 0)
                {
                    MainConsole.Instance.Commands.AddCommand(
                        "Novalyth", false,
                        "show asset pipeline", "show asset pipeline",
                        "Show Novalyth asset pipeline metrics",
                        HandleShowAssetPipeline);
                    MainConsole.Instance.Commands.AddCommand(
                        "Novalyth", false,
                        "reset asset pipeline", "reset asset pipeline",
                        "Reset Novalyth asset pipeline metrics",
                        HandleResetAssetPipeline);
                    MainConsole.Instance.Commands.AddCommand(
                        "Novalyth", false,
                        "show texture hot cache", "show texture hot cache",
                        "Show Novalyth C4B texture RAM hot-cache metrics",
                        HandleShowTextureHotCache);
                    MainConsole.Instance.Commands.AddCommand(
                        "Novalyth", false,
                        "reset texture hot cache", "reset texture hot cache",
                        "Reset Novalyth C4B texture hot-cache counters",
                        HandleResetTextureHotCache);
                }
            }
        }

        public void Close()
        {
            lock (m_loadLock)
            {
                if (m_NumberScenes > 0)
                    return;

                int drained = StopAndDrainFairAssetRequests();

                if (drained > 0)
                {
                    m_log.InfoFormat(
                        "[GETASSETS]: shutdown drained {0} queued fair asset requests",
                        drained);
                }

                if (m_workerpool != null)
                {
                    int drainTimeoutMs =
                        Math.Clamp(m_assetFetchTimeoutMs + 5000, 6000, 125000);

                    if (WaitForFairAssetInflightDrain(drainTimeoutMs))
                    {
                        m_workerpool.Dispose();
                        m_workerpool = null;
                    }
                    else
                    {
                        m_log.ErrorFormat(
                            "[GETASSETS]: shutdown fair-dispatch drain timed out after {0} ms; " +
                            "workerpool retained to avoid dropping tracked in-flight requests",
                            drainTimeoutMs);
                    }
                }
            }
        }

        public string Name { get { return "GetAssetsModule"; } }

        #endregion

        private static void DoAssetRequests(object o)
        {
            APollRequest poolreq = o as APollRequest;
            if (poolreq == null || poolreq.reqID.IsZero())
                return;

            try
            {
                if (m_NumberScenes <= 0)
                {
                    poolreq.thepoll.FailQueuedRequest(
                        poolreq,
                        "region-shutdown");
                }
                else
                {
                    poolreq.thepoll.Process(poolreq);
                }
            }
            finally
            {
                if (poolreq.queueGuardTracked)
                {
                    NovalythAssetQueueGuard.Release(poolreq.queueAgentID);
                    poolreq.queueGuardTracked = false;
                }

                CompleteFairAssetRequest();
            }
        }


        private static void HandleShowTextureHotCache(
            string module,
            string[] args)
        {
            MainConsole.Instance.Output(
                GetAssetsHandler.GetTextureHotCacheReport());
        }

        private static void HandleResetTextureHotCache(
            string module,
            string[] args)
        {
            GetAssetsHandler.ResetTextureHotCacheMetrics();
            MainConsole.Instance.Output(
                "[NOVALYTH TEXTURE C4B] hot-cache counters reset.");
        }

        private static void HandleResetAssetPipeline(string module, string[] args)
        {
            NovalythAssetPipelineMetrics.Reset();
            MainConsole.Instance.Output("[NOVALYTH] Asset pipeline metrics reset.");
        }


        private static void HandleShowAssetPipeline(string module, string[] args)
        {
            MainConsole.Instance.Output(NovalythAssetPipelineMetrics.GetReport());
        }

        private class PollServiceAssetEventArgs : PollServiceEventArgs
        {
            //private List<Hashtable> requests = new List<Hashtable>();
            private List<OSHttpRequest> requests = new List<OSHttpRequest>();
            private Dictionary<UUID, APollResponse> responses =new Dictionary<UUID, APollResponse>();
            private HashSet<UUID> dropedResponses = new HashSet<UUID>();

            private Scene m_scene;
            private string m_hgassets = null;
            public PollServiceAssetEventArgs(string uri, UUID pId, Scene scene, string HGAssetSVC) :
                base(null, uri, null, null, null, null, pId, int.MaxValue)
            {
                m_scene = scene;
                m_hgassets = HGAssetSVC;
                UseResponseReadyNotification = GetAssetsModule.m_assetResponseWake;

                HasEvents = delegate(UUID requestID, UUID _)
                {
                    lock (responses)
                    {
                        return responses.ContainsKey(requestID);
                    }
                };

                Drop = delegate(UUID requestID, UUID _)
                {
                    lock (responses)
                    {
                        responses.Remove(requestID);
                        lock(dropedResponses)
                            dropedResponses.Add(requestID);
                    }
                };

                GetEvents = delegate(UUID requestID, UUID _)
                {
                    lock (responses)
                    {
                        if(responses.Remove(requestID, out APollResponse apr))
                        {
                            OSHttpResponse response = apr.osresponse;
                            if (response.Priority < 0)
                                response.Priority = 0;

                            Hashtable lixo = new()
                            {
                                ["h"] = response
                            };
                            return lixo;
                        }
                    }
                    return new Hashtable();
                };
                
                Request = delegate (UUID requestID, OSHttpRequest request)
                {
                    APollRequest reqinfo = new()
                    {
                        thepoll = this,
                        reqID = requestID,
                        request = request,
                        queueAgentID = Id,
                        enqueuedAtMs = Environment.TickCount64
                    };

                    if (!NovalythAssetQueueGuard.TryAcquire(
                            reqinfo.queueAgentID,
                            out bool queueTracked,
                            out string rejectReason))
                    {
                        return BuildAssetBackpressureResponse(request, rejectReason);
                    }

                    reqinfo.queueGuardTracked = queueTracked;

                    NovalythAssetPipelineMetrics.CapsEnqueued();

                    if (!EnqueueFairAssetRequest(reqinfo))
                    {
                        if (reqinfo.queueGuardTracked)
                        {
                            NovalythAssetQueueGuard.Release(
                                reqinfo.queueAgentID,
                                enqueueFailure: true);
                            reqinfo.queueGuardTracked = false;
                        }

                        return BuildAssetBackpressureResponse(
                            request, "worker-unavailable");
                    }

                    return null;
                };

                // this should never happen except possible on shutdown
                NoEvents = delegate (UUID _, UUID _)
                {
                    /*
                    lock (requests)
                    {
                        Hashtable request = requests.Find(id => id["RequestID"].ToString() == x.ToString());
                        requests.Remove(request);
                    }
                    */
                    Hashtable response = new Hashtable();

                    response["int_response_code"] = 500;
                    response["str_response_string"] = "timeout";
                    response["content_type"] = "text/plain";
                    response["keepalive"] = false;
                    return response;
                };
            }

            public void FailQueuedRequest(
                APollRequest requestinfo, string reason)
            {
                UUID requestID = requestinfo.reqID;

                OSHttpResponse response =
                    BuildAssetBackpressureResponse(
                        requestinfo.request, reason);

                bool stored = false;

                lock (responses)
                {
                    lock (dropedResponses)
                    {
                        if (dropedResponses.Contains(requestID))
                        {
                            dropedResponses.Remove(requestID);
                            return;
                        }
                    }

                    responses[requestID] = new APollResponse()
                    {
                        osresponse = response
                    };
                    stored = true;
                }

                if (stored && UseResponseReadyNotification)
                    ResponseReady?.Invoke(requestID);
            }

            public void Process(APollRequest requestinfo)
            {
                UUID requestID = requestinfo.reqID;

                if(m_scene.ShuttingDown)
                    return;

                lock(responses)
                {
                    lock(dropedResponses)
                    {
                        if(dropedResponses.Contains(requestID))
                        {
                            dropedResponses.Remove(requestID);
                            return;
                        }
                    }
/* can't do this with current viewers; HG problem
                    // If the avatar is gone, don't bother to get the texture
                    if(m_scene.GetScenePresence(Id) == null)
                    {
                        curresponse = new Hashtable();
                        curresponse["int_response_code"] = 500;
                        curresponse["str_response_string"] = "timeout";
                        curresponse["content_type"] = "text/plain";
                        curresponse["keepalive"] = false;
                        responses[requestID] = new APollResponse() { bytes = 0, response = curresponse };
                        return;
                    }
*/
                }
                OSHttpResponse response = new OSHttpResponse(requestinfo.request);
                m_getAssetHandler.Handle(requestinfo.request, response, m_hgassets);

                lock(responses)
                {
                    lock(dropedResponses)
                    {
                        if(dropedResponses.Contains(requestID))
                        {
                            dropedResponses.Remove(requestID);
                            return;
                        }
                    }

                    APollResponse preq= new APollResponse()
                    {
                        osresponse = response
                    };
                    responses[requestID] = preq;
                }

                if (UseResponseReadyNotification)
                    ResponseReady?.Invoke(requestID);
            }
        }

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            /*
            string hostName = m_scene.RegionInfo.ExternalHostName;
            uint port = (MainServer.Instance == null) ? 0 : MainServer.Instance.Port;
            string protocol = "http";
            if (MainServer.Instance.UseSSL)
            {
                hostName = MainServer.Instance.SSLCommonName;
                port = MainServer.Instance.SSLPort;
                protocol = "https";
            }
            string baseURL = String.Format("{0}://{1}:{2}", protocol, hostName, port);
            */
            string hgassets = null;
            if(m_UserManagement != null)
                hgassets = m_UserManagement.GetUserServerURL(agentID, "AssetServerURI");

            IExternalCapsModule handler = m_scene.RequestModuleInterface<IExternalCapsModule>();

            if (m_GetTextureURL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "GetTexture", capUrl);
                else
                    caps.RegisterPollHandler("GetTexture", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets));
            }
            else
            {
                caps.RegisterHandler("GetTexture", m_GetTextureURL);
            }

            //GetMesh
            if (m_GetMeshURL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "GetMesh", capUrl);
                else
                    caps.RegisterPollHandler("GetMesh", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets));
            }
            else if (!string.IsNullOrEmpty(m_GetMeshURL))
                caps.RegisterHandler("GetMesh", m_GetMeshURL);

            //GetMesh2
            if (m_GetMesh2URL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "GetMesh2", capUrl);
                else
                    caps.RegisterPollHandler("GetMesh2", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets));
            }
            else if (!string.IsNullOrEmpty(m_GetMesh2URL))
                caps.RegisterHandler("GetMesh2", m_GetMesh2URL);

            //ViewerAsset
            if (m_GetAssetURL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "ViewerAsset", capUrl);
                else
                    caps.RegisterPollHandler("ViewerAsset", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets));
            }
            else if (!string.IsNullOrEmpty(m_GetAssetURL))
                caps.RegisterHandler("ViewerAsset", m_GetAssetURL);
        }
    }
}
