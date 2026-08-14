/*
 * Novalyth Second Life compatible Server Side Appearance protocol surface.
 * Copyright (c) 2026 Novalyth contributors.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Capabilities.Handlers;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace Novalyth.Region.Appearance
{
    [Extension(
        Path = "/OpenSim/RegionModules",
        NodeName = "RegionModule",
        Id = "Novalyth.ServerSideAppearance")]
    public sealed class NovalythServerSideAppearanceModule : ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(typeof(NovalythServerSideAppearanceModule));

        private static readonly HttpClient s_http = new HttpClient(
            new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        // Bake work is intentionally separate from the short viewer-CAPS proxy
        // timeout. UpdateAvatarAppearance must return promptly while the server
        // bake is generated out-of-band, matching the SL SSA transaction model.
        private static readonly HttpClient s_bakeHttp = new HttpClient(
            new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private static readonly IReadOnlyDictionary<string, uint>
            s_bakeTextureIndices = new Dictionary<string, uint>(StringComparer.Ordinal)
            {
                ["head"] = 8,
                ["upper"] = 9,
                ["lower"] = 10,
                ["eyes"] = 11,
                ["skirt"] = 19,
                ["hair"] = 20,
                ["leftarm"] = 40,
                ["leftleg"] = 41,
                ["aux1"] = 42,
                ["aux2"] = 43,
                ["aux3"] = 44
            };

        private readonly ConcurrentDictionary<Scene, byte> m_scenes = new();
        private readonly ConcurrentDictionary<UUID, BakeWorkState> m_bakeWork = new();

        private bool m_enabled;
        private bool m_registerCaps;
        private bool m_advertiseCentralBake;
        private int m_centralBakeVersion = 1;
        private int m_bakeCoalesceMilliseconds = 75;
        private int m_bakePrewarmParallelism = 4;
        private string m_serviceURI = string.Empty;
        private string m_serviceToken = string.Empty;

        public string Name => "Novalyth Server Side Appearance Protocol";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["NovalythSSA"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            m_registerCaps = config.GetBoolean("RegisterCaps", true);
            m_advertiseCentralBake =
                config.GetBoolean("AdvertiseCentralBake", false);
            m_centralBakeVersion =
                Math.Max(1, config.GetInt("CentralBakeVersion", 1));
            m_bakeCoalesceMilliseconds =
                Math.Clamp(config.GetInt("BakeCoalesceMilliseconds", 75), 0, 2000);
            m_bakePrewarmParallelism =
                Math.Clamp(config.GetInt("BakePrewarmParallelism", 4), 1, 11);
            m_serviceURI =
                config.GetString("ServiceURI", string.Empty).TrimEnd('/');
            m_serviceToken =
                config.GetString("ServiceToken", string.Empty);

            if (m_enabled &&
                (string.IsNullOrWhiteSpace(m_serviceURI) ||
                 string.IsNullOrWhiteSpace(m_serviceToken)))
            {
                m_log.Error(
                    "[NOVALYTH SSA C1]: disabled because ServiceURI/ServiceToken is missing");
                m_enabled = false;
            }

            if (m_advertiseCentralBake)
            {
                m_log.WarnFormat(
                    "[NOVALYTH SSA C4B]: viewer advertisement armed; CentralBakeVersion={0}; coalesce={1}ms; prewarm_parallel={2}; publish=RAM-hot->atomic-server-bake",
                    m_centralBakeVersion,
                    m_bakeCoalesceMilliseconds,
                    m_bakePrewarmParallelism);
            }
        }

        public void AddRegion(Scene scene)
        {
            m_scenes.TryAdd(scene, 0);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            if (m_registerCaps)
                scene.EventManager.OnRegisterCaps += RegisterCaps;

            if (!m_advertiseCentralBake)
                return;

            ISimulatorFeaturesModule simulatorFeatures =
                scene.RequestModuleInterface<ISimulatorFeaturesModule>();

            if (simulatorFeatures == null)
            {
                m_log.Error(
                    "[NOVALYTH SSA C3]: SimulatorFeatures module unavailable; " +
                    "CentralBakeVersion was NOT advertised");
                return;
            }

            simulatorFeatures.AddFeature(
                "CentralBakeVersion",
                OSD.FromInteger(m_centralBakeVersion));

            m_log.WarnFormat(
                "[NOVALYTH SSA C4A]: CentralBakeVersion={0} advertised through SimulatorFeatures",
                m_centralBakeVersion);
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnRegisterCaps -= RegisterCaps;
            m_scenes.TryRemove(scene, out _);

            if (m_advertiseCentralBake)
            {
                ISimulatorFeaturesModule simulatorFeatures =
                    scene.RequestModuleInterface<ISimulatorFeaturesModule>();
                simulatorFeatures?.RemoveFeature("CentralBakeVersion");
            }
        }

        public void PostInitialise() {}
        public void Close() {}

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            string updatePath = "/" + UUID.Random();
            string incrementCurrentPath = "/" + UUID.Random();
            string incrementLegacyPath = "/" + UUID.Random();

            caps.RegisterSimpleHandler(
                "UpdateAvatarAppearance",
                new SimpleStreamHandler(
                    updatePath,
                    (request, response) =>
                        ProxyUpdateAvatarAppearance(agentID, request, response)));

            // Current official Second Life viewer spelling.
            caps.RegisterSimpleHandler(
                "IncrementCofVersion",
                new SimpleStreamHandler(
                    incrementCurrentPath,
                    (request, response) =>
                        ProxyIncrementCofVersion(agentID, request, response)));

            // Historical spelling kept as a compatibility alias.
            caps.RegisterSimpleHandler(
                "IncrementCOFVersion",
                new SimpleStreamHandler(
                    incrementLegacyPath,
                    (request, response) =>
                        ProxyIncrementCofVersion(agentID, request, response)));

            if (m_advertiseCentralBake)
            {
                m_log.InfoFormat(
                    "[NOVALYTH SSA C4A]: registered UpdateAvatarAppearance + IncrementCofVersion + legacy IncrementCOFVersion for {0}; CentralBakeVersion={1}",
                    agentID,
                    m_centralBakeVersion);
            }
            else
            {
                m_log.InfoFormat(
                    "[NOVALYTH SSA C1]: registered UpdateAvatarAppearance + IncrementCofVersion + legacy IncrementCOFVersion for {0}; CentralBakeVersion remains disabled",
                    agentID);
            }
        }

        private void ProxyUpdateAvatarAppearance(
            UUID agentID,
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                WriteViewerError(
                    response,
                    HttpStatusCode.MethodNotAllowed,
                    "method_not_allowed");
                return;
            }

            try
            {
                byte[] body;
                using (MemoryStream copy = new MemoryStream())
                {
                    request.InputStream.CopyTo(copy);
                    body = copy.ToArray();
                }

                using HttpRequestMessage outgoing =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        m_serviceURI + "/update/" + agentID);

                outgoing.Headers.TryAddWithoutValidation(
                    "X-Novalyth-Service-Token",
                    m_serviceToken);

                ByteArrayContent content = new ByteArrayContent(body);
                content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/llsd+xml");
                outgoing.Content = content;

                using HttpResponseMessage coreResponse = s_http.Send(outgoing);
                byte[] reply = coreResponse.Content.ReadAsByteArrayAsync()
                    .GetAwaiter().GetResult();

                response.ContentType = "application/llsd+xml";
                response.RawBuffer = reply;
                response.StatusCode = (int)coreResponse.StatusCode;

                // SL semantics: UpdateAvatarAppearance requests a server-side
                // appearance update. Do not destroy the currently visible baked
                // appearance. Generate the new bake asynchronously and publish it
                // only after all 11 bake assets are ready.
                if (coreResponse.IsSuccessStatusCode && m_advertiseCentralBake)
                    QueueServerBake(agentID, "UpdateAvatarAppearance");
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH SSA C4A]: UpdateAvatarAppearance proxy failed for {0}: {1}",
                    agentID,
                    e.Message);

                WriteViewerError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "appearance_core_unavailable");
            }
        }

        private void QueueServerBake(UUID agentID, string reason)
        {
            BakeWorkState state =
                m_bakeWork.GetOrAdd(agentID, _ => new BakeWorkState());

            bool startWorker = false;
            long generation;

            lock (state.Sync)
            {
                generation = ++state.RequestedGeneration;
                state.LastReason = reason ?? string.Empty;

                if (!state.WorkerRunning)
                {
                    state.WorkerRunning = true;
                    startWorker = true;
                }
            }

            m_log.InfoFormat(
                "[NOVALYTH SSA C4A]: bake requested agent={0} generation={1} reason={2}",
                agentID,
                generation,
                reason);

            if (startWorker)
            {
                ThreadPool.QueueUserWorkItem(
                    _ => RunBakeWorker(agentID, state));
            }
        }

        private void RunBakeWorker(UUID agentID, BakeWorkState state)
        {
            while (true)
            {
                if (m_bakeCoalesceMilliseconds > 0)
                    Thread.Sleep(m_bakeCoalesceMilliseconds);

                long targetGeneration;
                string reason;

                lock (state.Sync)
                {
                    targetGeneration = state.RequestedGeneration;
                    reason = state.LastReason;
                }

                long started = Environment.TickCount64;

                if (!TryRequestServerBake(
                        agentID,
                        out OSDMap manifest,
                        out string bakeError))
                {
                    bool retry;
                    lock (state.Sync)
                    {
                        retry = state.RequestedGeneration != targetGeneration;
                        if (!retry)
                            state.WorkerRunning = false;
                    }

                    m_log.ErrorFormat(
                        "[NOVALYTH SSA C4A]: server bake failed agent={0} generation={1} reason={2} error={3}; last-known-good preserved",
                        agentID,
                        targetGeneration,
                        reason,
                        bakeError);

                    if (retry)
                        continue;

                    return;
                }

                long latestGeneration;
                lock (state.Sync)
                    latestGeneration = state.RequestedGeneration;

                if (latestGeneration != targetGeneration)
                {
                    m_log.InfoFormat(
                        "[NOVALYTH SSA C4A]: stale bake suppressed agent={0} built_generation={1} latest_generation={2}; last-known-good remains visible",
                        agentID,
                        targetGeneration,
                        latestGeneration);
                    continue;
                }

                if (!TryPublishServerBake(
                        agentID,
                        manifest,
                        state,
                        targetGeneration,
                        out string publishError))
                {
                    bool retry;
                    lock (state.Sync)
                    {
                        retry = state.RequestedGeneration != targetGeneration;
                        if (!retry)
                            state.WorkerRunning = false;
                    }

                    if (retry && publishError == "stale_generation_during_prewarm")
                    {
                        m_log.InfoFormat(
                            "[NOVALYTH SSA C4B]: stale generation suppressed during prewarm agent={0} built_generation={1}; newest generation will continue",
                            agentID,
                            targetGeneration);
                    }
                    else
                    {
                        m_log.ErrorFormat(
                            "[NOVALYTH SSA C4B]: server bake publish failed agent={0} generation={1} error={2}; last-known-good preserved",
                            agentID,
                            targetGeneration,
                            publishError);
                    }

                    if (retry)
                        continue;

                    return;
                }

                long elapsed = Environment.TickCount64 - started;
                bool done;

                lock (state.Sync)
                {
                    state.PublishedGeneration = targetGeneration;
                    done = state.RequestedGeneration == targetGeneration;
                    if (done)
                        state.WorkerRunning = false;
                }

                m_log.InfoFormat(
                    "[NOVALYTH SSA C4B]: atomic RAM-hot server appearance published agent={0} generation={1} cof={2} recipe={3} slots=11 elapsed_ms={4}",
                    agentID,
                    targetGeneration,
                    manifest["cof_version"].AsInteger(),
                    manifest["recipe_hash"].AsString(),
                    elapsed);

                if (done)
                    return;
            }
        }

        private bool TryRequestServerBake(
            UUID agentID,
            out OSDMap manifest,
            out string error)
        {
            manifest = null;
            error = string.Empty;

            try
            {
                using HttpRequestMessage outgoing =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        m_serviceURI + "/bake/" + agentID);

                outgoing.Headers.TryAddWithoutValidation(
                    "X-Novalyth-Service-Token",
                    m_serviceToken);

                using HttpResponseMessage coreResponse =
                    s_bakeHttp.Send(outgoing);

                byte[] reply = coreResponse.Content.ReadAsByteArrayAsync()
                    .GetAwaiter().GetResult();

                if (!coreResponse.IsSuccessStatusCode)
                {
                    error = "appearance_core_http_" +
                        (int)coreResponse.StatusCode;
                    return false;
                }

                using MemoryStream input = new MemoryStream(reply, false);
                manifest = OSDParser.DeserializeLLSDXml(input) as OSDMap;

                if (manifest == null)
                {
                    error = "appearance_core_invalid_llsd";
                    return false;
                }

                if (!manifest.TryGetValue("success", out OSD success) ||
                    !success.AsBoolean())
                {
                    error = manifest.TryGetValue("error", out OSD coreError)
                        ? coreError.AsString()
                        : "appearance_core_bake_failed";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ":" + e.Message;
                return false;
            }
        }

        private bool TryPublishServerBake(
            UUID agentID,
            OSDMap manifest,
            BakeWorkState state,
            long targetGeneration,
            out string error)
        {
            error = string.Empty;

            if (!manifest.TryGetValue("bakes", out OSD bakesOSD) ||
                bakesOSD is not OSDArray bakes)
            {
                error = "manifest_bakes_missing";
                return false;
            }

            Primitive.TextureEntry textureEntry =
                new Primitive.TextureEntry(
                    AppearanceManager.DEFAULT_AVATAR_TEXTURE);

            int validBakes = 0;
            List<UUID> bakeAssetIDs = new();

            foreach (OSD entry in bakes)
            {
                if (entry is not OSDMap bake)
                    continue;

                string name = bake["name"].AsString();
                if (!s_bakeTextureIndices.TryGetValue(name, out uint textureIndex))
                    continue;

                if (bake["status"].AsString() != "j2k_stored" ||
                    !UUID.TryParse(bake["asset_id"].AsString(), out UUID assetID) ||
                    assetID.IsZero())
                {
                    error = "invalid_bake_slot:" + name;
                    return false;
                }

                Primitive.TextureEntryFace face =
                    textureEntry.GetFace(textureIndex);
                face.TextureID = assetID;
                bakeAssetIDs.Add(assetID);
                validBakes++;
            }

            if (validBakes != s_bakeTextureIndices.Count)
            {
                error = "incomplete_bake_set:" + validBakes;
                return false;
            }

            ScenePresence sp = FindRootPresence(agentID);
            if (sp == null)
            {
                error = "root_presence_not_found";
                return false;
            }

            long prewarmStarted = Environment.TickCount64;
            if (!TryPrewarmBakeAssets(
                    sp,
                    bakeAssetIDs,
                    out int primed,
                    out long primedBytes,
                    out string prewarmError))
            {
                error = "bake_prewarm_failed:" + prewarmError;
                return false;
            }

            // Close the only remaining C4A race: an outfit can change while the
            // just-completed generation is being prewarmed. Never publish that
            // generation if a newer request arrived during the hot-cache fill.
            lock (state.Sync)
            {
                if (state.RequestedGeneration != targetGeneration)
                {
                    error = "stale_generation_during_prewarm";
                    return false;
                }
            }

            m_log.InfoFormat(
                "[NOVALYTH SSA C4B]: bake prewarm complete agent={0} generation={1} primed={2}/11 bytes={3} elapsed_ms={4}",
                agentID,
                targetGeneration,
                primed,
                primedBytes,
                Environment.TickCount64 - prewarmStarted);

            IAvatarFactoryModule factory =
                sp.Scene.RequestModuleInterface<IAvatarFactoryModule>();
            if (factory == null)
            {
                error = "avatar_factory_unavailable";
                return false;
            }

            // Transactional publish: SetAppearance receives the complete texture
            // entry only after every server bake is stored. Until this exact point
            // the previous Appearance.Texture stays untouched (last-known-good).
            factory.SetAppearance(
                sp,
                textureEntry,
                sp.Appearance.VisualParams,
                null);

            // OpenSim normally queues an appearance send. SSA should not add the
            // legacy two-second send delay after the server bake is already ready.
            // Send to the owning viewer as well as all observers immediately.
            sp.SendAppearanceToAgentNF(sp);
            sp.SendAppearanceToAllOtherAgents();

            return true;
        }

        private bool TryPrewarmBakeAssets(
            ScenePresence sp,
            List<UUID> bakeAssetIDs,
            out int primed,
            out long primedBytes,
            out string error)
        {
            primed = 0;
            primedBytes = 0;
            error = string.Empty;

            if (sp == null || bakeAssetIDs == null || bakeAssetIDs.Count != 11)
            {
                error = "invalid_prewarm_input";
                return false;
            }

            IAssetService assets =
                sp.Scene.RequestModuleInterface<IAssetService>();
            if (assets == null)
            {
                error = "asset_service_unavailable";
                return false;
            }

            ConcurrentQueue<string> failures = new();
            int primeCount = 0;
            long byteCount = 0;

            System.Threading.Tasks.Parallel.ForEach(
                bakeAssetIDs,
                new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = m_bakePrewarmParallelism
                },
                assetID =>
                {
                    try
                    {
                        AssetBase asset = assets.Get(assetID.ToString());
                        if (asset == null)
                        {
                            failures.Enqueue("missing:" + assetID);
                            return;
                        }

                        if (asset.Type != (sbyte)AssetType.Texture ||
                            asset.Data == null ||
                            asset.Data.Length == 0)
                        {
                            failures.Enqueue("invalid_texture:" + assetID);
                            return;
                        }

                        // Performance hint only: validation above is correctness;
                        // cache rejection must never make appearance publishing
                        // impossible if an operator intentionally disables C4B.
                        if (GetAssetsHandler.PrimeTextureAsset(asset))
                            Interlocked.Increment(ref primeCount);

                        Interlocked.Add(ref byteCount, asset.Data.Length);
                    }
                    catch (Exception e)
                    {
                        failures.Enqueue(
                            assetID + ":" + e.GetType().Name + ":" + e.Message);
                    }
                });

            if (!failures.IsEmpty)
            {
                error = string.Join("|", failures);
                return false;
            }

            primed = primeCount;
            primedBytes = byteCount;
            return true;
        }

        private ScenePresence FindRootPresence(UUID agentID)
        {
            foreach (Scene scene in m_scenes.Keys)
            {
                ScenePresence sp = scene.GetScenePresence(agentID);
                if (sp != null && !sp.IsDeleted && !sp.IsChildAgent)
                    return sp;
            }

            return null;
        }

        private void ProxyIncrementCofVersion(
            UUID agentID,
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (request.HttpMethod != "GET" &&
                request.HttpMethod != "POST")
            {
                WriteViewerError(
                    response,
                    HttpStatusCode.MethodNotAllowed,
                    "method_not_allowed");
                return;
            }

            try
            {
                using HttpRequestMessage outgoing =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        m_serviceURI + "/increment/" + agentID);

                outgoing.Headers.TryAddWithoutValidation(
                    "X-Novalyth-Service-Token",
                    m_serviceToken);

                using HttpResponseMessage coreResponse = s_http.Send(outgoing);
                byte[] reply = coreResponse.Content.ReadAsByteArrayAsync()
                    .GetAwaiter().GetResult();

                response.ContentType = "application/llsd+xml";
                response.RawBuffer = reply;
                response.StatusCode = (int)coreResponse.StatusCode;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH SSA C1]: IncrementCofVersion proxy failed for {0}: {1}",
                    agentID,
                    e.Message);

                WriteViewerError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "appearance_core_unavailable");
            }
        }

        private sealed class BakeWorkState
        {
            public readonly object Sync = new object();
            public long RequestedGeneration;
            public long PublishedGeneration;
            public bool WorkerRunning;
            public string LastReason = string.Empty;
        }

        private static void WriteViewerError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error)
        {
            OSDMap map = new();
            map["success"] = false;
            map["error"] = error;

            response.ContentType = "application/llsd+xml";
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(map);
            response.StatusCode = (int)status;
        }
    }
}
