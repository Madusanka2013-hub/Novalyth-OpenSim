/*
 * Novalyth Appearance Core
 * Copyright (c) 2026 Novalyth contributors.
 *
 * Novalyth-owned code built inside the current OpenSimulator bootstrap tree
 * only as a migration step toward the standalone Novalyth grid.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;

namespace Novalyth.Server.Appearance
{
    public sealed class NovalythAppearanceStateConnector : ServiceConnector
    {
        public NovalythAppearanceStateConnector(
            IConfigSource config,
            IHttpServer server,
            string configName) : base(config, server, configName)
        {
            string sectionName = string.IsNullOrWhiteSpace(configName)
                ? "NovalythAppearanceStateService"
                : configName;

            IConfig section = config.Configs[sectionName]
                ?? config.Configs["NovalythAppearanceStateService"];

            if (section == null)
                throw new Exception("No NovalythAppearanceStateService configuration");

            string stateDirectory = section.GetString("StateDirectory", string.Empty);
            string token = section.GetString("ServiceToken", string.Empty);
            bool bakeReady = section.GetBoolean("BakeReady", false);

            if (string.IsNullOrWhiteSpace(stateDirectory))
                throw new Exception("Novalyth Appearance StateDirectory is missing");
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Novalyth Appearance ServiceToken is missing");

            Directory.CreateDirectory(stateDirectory);

            server.AddSimpleStreamHandler(
                new NovalythAppearanceStateHandler(stateDirectory, token, bakeReady),
                true);
        }
    }

    internal sealed class NovalythAppearanceStateHandler : SimpleStreamHandler
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(typeof(NovalythAppearanceStateHandler));

        private readonly string m_stateDirectory;
        private readonly byte[] m_token;
        private readonly bool m_bakeReady;
        private readonly ConcurrentDictionary<UUID, object> m_agentLocks = new();

        public NovalythAppearanceStateHandler(
            string stateDirectory,
            string token,
            bool bakeReady)
            : base("/novalythappearance")
        {
            m_stateDirectory = stateDirectory;
            m_token = Encoding.UTF8.GetBytes(token);
            m_bakeReady = bakeReady;

            m_log.InfoFormat(
                "[NOVALYTH APPEARANCE CORE]: SSA state protocol online; bake-ready={0}",
                m_bakeReady);
        }

        protected override void ProcessRequest(
            IOSHttpRequest httpRequest,
            IOSHttpResponse httpResponse)
        {
            httpResponse.ContentType = "application/llsd+xml";

            if (!Authorized(httpRequest))
            {
                WriteError(httpResponse, HttpStatusCode.Unauthorized, "unauthorized");
                return;
            }

            string param = GetParam(httpRequest.UriPath).Trim('/');
            string[] parts = param.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 1 && parts[0] == "health")
            {
                OSDMap health = new();
                health["status"] = "ok";
                health["service"] = "Novalyth Appearance Core";
                health["phase"] = "B";
                health["sl_ssa_protocol_surface"] = true;
                health["server_bake_ready"] = m_bakeReady;
                WriteMap(httpResponse, HttpStatusCode.OK, health);
                return;
            }

            if (parts.Length != 2 || !UUID.TryParse(parts[1], out UUID agentID))
            {
                WriteError(httpResponse, HttpStatusCode.NotFound, "invalid_path");
                return;
            }

            string action = parts[0];

            switch (action)
            {
                case "state":
                    if (httpRequest.HttpMethod == "GET")
                    {
                        HandleStateGet(agentID, httpResponse);
                        return;
                    }
                    if (httpRequest.HttpMethod == "DELETE")
                    {
                        HandleStateDelete(agentID, httpResponse);
                        return;
                    }
                    break;

                case "increment":
                    if (httpRequest.HttpMethod == "GET" ||
                        httpRequest.HttpMethod == "POST")
                    {
                        HandleIncrement(agentID, httpResponse);
                        return;
                    }
                    break;

                case "update":
                    if (httpRequest.HttpMethod == "POST")
                    {
                        HandleAppearanceUpdate(agentID, httpRequest, httpResponse);
                        return;
                    }
                    break;
            }

            WriteError(httpResponse, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
        }

        private bool Authorized(IOSHttpRequest request)
        {
            string supplied = request.Headers["X-Novalyth-Service-Token"];
            if (string.IsNullOrEmpty(supplied))
                return false;

            byte[] value = Encoding.UTF8.GetBytes(supplied);
            return value.Length == m_token.Length
                && CryptographicOperations.FixedTimeEquals(value, m_token);
        }

        private object GetAgentLock(UUID agentID)
        {
            return m_agentLocks.GetOrAdd(agentID, _ => new object());
        }

        private string StatePath(UUID agentID)
        {
            string id = agentID.ToString();
            string dir = System.IO.Path.Combine(
                m_stateDirectory,
                id.Substring(0, 2),
                id.Substring(2, 2));
            return System.IO.Path.Combine(dir, id + ".llsd");
        }

        private AppearanceState LoadState(UUID agentID)
        {
            string path = StatePath(agentID);
            if (!File.Exists(path))
                return new AppearanceState();

            try
            {
                byte[] data = File.ReadAllBytes(path);
                using MemoryStream input = new MemoryStream(data, false);
                OSD osd = OSDParser.DeserializeLLSDXml(input);
                if (osd is OSDMap map)
                    return AppearanceState.FromMap(map);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH APPEARANCE CORE]: state read failed for {0}: {1}",
                    agentID, e.Message);
            }

            return new AppearanceState();
        }

        private void SaveState(UUID agentID, AppearanceState state)
        {
            string path = StatePath(agentID);
            string dir = System.IO.Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);

            byte[] data = OSDParser.SerializeLLSDXmlBytes(state.ToMap());
            string temp = path + "." + UUID.Random() + ".tmp";

            File.WriteAllBytes(temp, data);
            File.Move(temp, path, true);
        }

        private void HandleStateGet(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                AppearanceState state = LoadState(agentID);
                OSDMap map = state.ToMap();
                map["agent_id"] = agentID;
                map["server_bake_ready"] = m_bakeReady;
                WriteMap(response, HttpStatusCode.OK, map);
            }
        }

        private void HandleStateDelete(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                string path = StatePath(agentID);
                if (File.Exists(path))
                    File.Delete(path);

                OSDMap result = new();
                result["success"] = true;
                WriteMap(response, HttpStatusCode.OK, result);
            }
        }

        private void HandleIncrement(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                AppearanceState state = LoadState(agentID);
                state.CofVersion = state.CofVersion == int.MaxValue
                    ? 1
                    : state.CofVersion + 1;
                state.UpdatedUtc = DateTime.UtcNow.ToString("O");
                SaveState(agentID, state);

                OSDMap result = new();
                result["success"] = true;
                result["version"] = state.CofVersion;
                result["appearance_version"] = state.AppearanceVersion;
                WriteMap(response, HttpStatusCode.OK, result);
            }
        }

        private void HandleAppearanceUpdate(
            UUID agentID,
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            OSDMap body;
            try
            {
                body = OSDParser.DeserializeLLSDXml(request.InputStream) as OSDMap;
            }
            catch
            {
                body = null;
            }

            if (body == null || !body.TryGetValue("cof_version", out OSD cofOSD))
            {
                WriteError(response, HttpStatusCode.BadRequest, "missing_cof_version");
                return;
            }

            int requested = cofOSD.AsInteger();

            lock (GetAgentLock(agentID))
            {
                AppearanceState state = LoadState(agentID);

                if (requested < state.CofVersion)
                {
                    OSDMap stale = new();
                    stale["success"] = false;
                    stale["error"] = "stale_cof_version";
                    stale["expected"] = state.CofVersion;
                    stale["version"] = state.CofVersion;
                    WriteMap(response, HttpStatusCode.OK, stale);
                    return;
                }

                if (requested > state.CofVersion)
                {
                    state.CofVersion = requested;
                    state.UpdatedUtc = DateTime.UtcNow.ToString("O");
                    SaveState(agentID, state);
                }

                // Phase B must never claim that a server bake exists.
                if (!m_bakeReady)
                {
                    OSDMap pending = new();
                    pending["success"] = false;
                    pending["error"] = "server_bake_not_active";
                    pending["expected"] = state.CofVersion;
                    pending["version"] = state.CofVersion;
                    pending["appearance_version"] = state.AppearanceVersion;
                    WriteMap(response, HttpStatusCode.OK, pending);
                    return;
                }

                OSDMap result = new();
                result["success"] = true;
                result["version"] = state.CofVersion;
                result["appearance_version"] = state.AppearanceVersion;
                WriteMap(response, HttpStatusCode.OK, result);
            }
        }

        private static void WriteError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error)
        {
            OSDMap map = new();
            map["success"] = false;
            map["error"] = error;
            WriteMap(response, status, map);
        }

        private static void WriteMap(
            IOSHttpResponse response,
            HttpStatusCode status,
            OSDMap map)
        {
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(map);
            response.StatusCode = (int)status;
        }

        private sealed class AppearanceState
        {
            public int CofVersion;
            public int AppearanceVersion;
            public string UpdatedUtc = string.Empty;

            public OSDMap ToMap()
            {
                OSDMap map = new();
                map["cof_version"] = CofVersion;
                map["appearance_version"] = AppearanceVersion;
                map["updated_utc"] = UpdatedUtc ?? string.Empty;
                return map;
            }

            public static AppearanceState FromMap(OSDMap map)
            {
                AppearanceState state = new();

                if (map.TryGetValue("cof_version", out OSD cof))
                    state.CofVersion = cof.AsInteger();
                if (map.TryGetValue("appearance_version", out OSD appearance))
                    state.AppearanceVersion = appearance.AsInteger();
                if (map.TryGetValue("updated_utc", out OSD updated))
                    state.UpdatedUtc = updated.AsString();

                return state;
            }
        }
    }
}
