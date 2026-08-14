/*
 * Novalyth Second Life compatible Server Side Appearance protocol surface.
 * Copyright (c) 2026 Novalyth contributors.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
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

        private bool m_enabled;
        private bool m_registerCaps;
        private bool m_advertiseCentralBake;
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
            m_serviceURI =
                config.GetString("ServiceURI", string.Empty).TrimEnd('/');
            m_serviceToken =
                config.GetString("ServiceToken", string.Empty);

            if (m_enabled &&
                (string.IsNullOrWhiteSpace(m_serviceURI) ||
                 string.IsNullOrWhiteSpace(m_serviceToken)))
            {
                m_log.Error(
                    "[NOVALYTH SSA]: disabled because ServiceURI/ServiceToken is missing");
                m_enabled = false;
            }

            if (m_advertiseCentralBake)
            {
                m_log.Warn(
                    "[NOVALYTH SSA]: AdvertiseCentralBake=True configured, " +
                    "but Phase B intentionally does not advertise SSA until the server baker exists");
            }
        }

        public void AddRegion(Scene scene) {}

        public void RegionLoaded(Scene scene)
        {
            if (m_enabled && m_registerCaps)
                scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnRegisterCaps -= RegisterCaps;
        }

        public void PostInitialise() {}
        public void Close() {}

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            string updatePath = "/" + UUID.Random();
            string incrementPath = "/" + UUID.Random();

            caps.RegisterSimpleHandler(
                "UpdateAvatarAppearance",
                new SimpleStreamHandler(
                    updatePath,
                    (request, response) =>
                        ProxyUpdateAvatarAppearance(agentID, request, response)));

            caps.RegisterSimpleHandler(
                "IncrementCOFVersion",
                new SimpleStreamHandler(
                    incrementPath,
                    (request, response) =>
                        ProxyIncrementCOFVersion(agentID, request, response)));

            m_log.InfoFormat(
                "[NOVALYTH SSA]: registered SL appearance caps for {0}; CentralBakeVersion remains disabled",
                agentID);
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
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH SSA]: UpdateAvatarAppearance proxy failed for {0}: {1}",
                    agentID, e.Message);

                WriteViewerError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "appearance_core_unavailable");
            }
        }

        private void ProxyIncrementCOFVersion(
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
                    "[NOVALYTH SSA]: IncrementCOFVersion proxy failed for {0}: {1}",
                    agentID, e.Message);

                WriteViewerError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "appearance_core_unavailable");
            }
        }

        private static void WriteViewerError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error)
        {
            OSDMap map = new OSDMap();
            map["success"] = false;
            map["error"] = error;

            response.ContentType = "application/llsd+xml";
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(map);
            response.StatusCode = (int)status;
        }
    }
}
