/*
 * Novalyth Appearance Core
 * Copyright (c) 2026 Novalyth contributors.
 *
 * Novalyth-owned code. OpenSimulator is currently only the bootstrap host.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Connectors;
using OpenSim.Services.Interfaces;

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

            string stateDirectory =
                section.GetString("StateDirectory", string.Empty);
            string manifestDirectory =
                section.GetString("ManifestDirectory", string.Empty);
            string token =
                section.GetString("ServiceToken", string.Empty);
            string inventoryServerURI =
                section.GetString("InventoryServerURI", string.Empty);
            string assetServerURI =
                section.GetString("AssetServerURI", string.Empty);
            string bakeContractVersion =
                section.GetString("BakeContractVersion", "sl-current-11-v1");
            bool bakeReady =
                section.GetBoolean("BakeReady", false);

            if (string.IsNullOrWhiteSpace(stateDirectory))
                throw new Exception("Novalyth Appearance StateDirectory is missing");
            if (string.IsNullOrWhiteSpace(manifestDirectory))
                throw new Exception("Novalyth Appearance ManifestDirectory is missing");
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Novalyth Appearance ServiceToken is missing");
            if (string.IsNullOrWhiteSpace(inventoryServerURI))
                throw new Exception("Novalyth Appearance InventoryServerURI is missing");
            if (string.IsNullOrWhiteSpace(assetServerURI))
                throw new Exception("Novalyth Appearance AssetServerURI is missing");

            Directory.CreateDirectory(stateDirectory);
            Directory.CreateDirectory(manifestDirectory);

            IInventoryService inventory =
                new XInventoryServicesConnector(inventoryServerURI);
            IAssetService assets =
                new AssetServicesConnector(assetServerURI);

            server.AddSimpleStreamHandler(
                new NovalythAppearanceStateHandler(
                    stateDirectory,
                    manifestDirectory,
                    token,
                    bakeReady,
                    bakeContractVersion,
                    inventory,
                    assets),
                true);
        }
    }

    internal sealed class NovalythAppearanceStateHandler : SimpleStreamHandler
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(typeof(NovalythAppearanceStateHandler));

        // Current Second Life viewer wearable type values.
        private const int WT_SHAPE = 0;
        private const int WT_SKIN = 1;
        private const int WT_HAIR = 2;
        private const int WT_EYES = 3;
        private const int WT_SHIRT = 4;
        private const int WT_PANTS = 5;
        private const int WT_SHOES = 6;
        private const int WT_SOCKS = 7;
        private const int WT_JACKET = 8;
        private const int WT_GLOVES = 9;
        private const int WT_UNDERSHIRT = 10;
        private const int WT_UNDERPANTS = 11;
        private const int WT_SKIRT = 12;
        private const int WT_ALPHA = 13;
        private const int WT_TATTOO = 14;
        private const int WT_PHYSICS = 15;
        private const int WT_UNIVERSAL = 16;

        private static readonly BakeDefinition[] s_bakeDefinitions =
        {
            new(0, "head", new[]
            {
                WT_SHAPE, WT_SKIN, WT_HAIR, WT_TATTOO, WT_ALPHA, WT_UNIVERSAL
            }),
            new(1, "upper", new[]
            {
                WT_SHAPE, WT_SKIN, WT_SHIRT, WT_JACKET, WT_GLOVES,
                WT_UNDERSHIRT, WT_TATTOO, WT_ALPHA, WT_UNIVERSAL
            }),
            new(2, "lower", new[]
            {
                WT_SHAPE, WT_SKIN, WT_PANTS, WT_SHOES, WT_SOCKS,
                WT_JACKET, WT_UNDERPANTS, WT_TATTOO, WT_ALPHA, WT_UNIVERSAL
            }),
            new(3, "eyes", new[]
            {
                WT_EYES, WT_UNIVERSAL, WT_ALPHA
            }),
            new(4, "skirt", new[]
            {
                WT_SKIRT, WT_UNIVERSAL
            }),
            new(5, "hair", new[]
            {
                WT_HAIR, WT_UNIVERSAL, WT_ALPHA
            }),
            new(6, "leftarm", new[] { WT_UNIVERSAL }),
            new(7, "leftleg", new[] { WT_UNIVERSAL }),
            new(8, "aux1", new[] { WT_UNIVERSAL }),
            new(9, "aux2", new[] { WT_UNIVERSAL }),
            new(10, "aux3", new[] { WT_UNIVERSAL })
        };

        // Second Life ETextureIndex -> baked texture slot. Baked output texture
        // indices themselves are intentionally absent: only local/source textures
        // participate in the C2 source graph.
        private static readonly IReadOnlyDictionary<int, string> s_sourceTextureBakeSlots =
            new Dictionary<int, string>
            {
                [0] = "head",      // TEX_HEAD_BODYPAINT
                [1] = "upper",     // TEX_UPPER_SHIRT
                [2] = "lower",     // TEX_LOWER_PANTS
                [3] = "eyes",      // TEX_EYES_IRIS
                [4] = "hair",      // TEX_HAIR
                [5] = "upper",     // TEX_UPPER_BODYPAINT
                [6] = "lower",     // TEX_LOWER_BODYPAINT
                [7] = "lower",     // TEX_LOWER_SHOES
                [12] = "lower",    // TEX_LOWER_SOCKS
                [13] = "upper",    // TEX_UPPER_JACKET
                [14] = "lower",    // TEX_LOWER_JACKET
                [15] = "upper",    // TEX_UPPER_GLOVES
                [16] = "upper",    // TEX_UPPER_UNDERSHIRT
                [17] = "lower",    // TEX_LOWER_UNDERPANTS
                [18] = "skirt",    // TEX_SKIRT
                [21] = "lower",    // TEX_LOWER_ALPHA
                [22] = "upper",    // TEX_UPPER_ALPHA
                [23] = "head",     // TEX_HEAD_ALPHA
                [24] = "eyes",     // TEX_EYES_ALPHA
                [25] = "hair",     // TEX_HAIR_ALPHA
                [26] = "head",     // TEX_HEAD_TATTOO
                [27] = "upper",    // TEX_UPPER_TATTOO
                [28] = "lower",    // TEX_LOWER_TATTOO
                [29] = "head",     // TEX_HEAD_UNIVERSAL_TATTOO
                [30] = "upper",    // TEX_UPPER_UNIVERSAL_TATTOO
                [31] = "lower",    // TEX_LOWER_UNIVERSAL_TATTOO
                [32] = "skirt",    // TEX_SKIRT_TATTOO
                [33] = "hair",     // TEX_HAIR_TATTOO
                [34] = "eyes",     // TEX_EYES_TATTOO
                [35] = "leftarm",  // TEX_LEFT_ARM_TATTOO
                [36] = "leftleg",  // TEX_LEFT_LEG_TATTOO
                [37] = "aux1",     // TEX_AUX1_TATTOO
                [38] = "aux2",     // TEX_AUX2_TATTOO
                [39] = "aux3"      // TEX_AUX3_TATTOO
            };

        private const int MaxWearableParameters = 4096;
        private const int MaxWearableTextures = 64;

        private readonly string m_stateDirectory;
        private readonly string m_manifestDirectory;
        private readonly byte[] m_token;
        private readonly bool m_bakeReady;
        private readonly string m_bakeContractVersion;
        private readonly IInventoryService m_inventory;
        private readonly IAssetService m_assets;

        private readonly ConcurrentDictionary<UUID, object> m_agentLocks = new();

        public NovalythAppearanceStateHandler(
            string stateDirectory,
            string manifestDirectory,
            string token,
            bool bakeReady,
            string bakeContractVersion,
            IInventoryService inventory,
            IAssetService assets)
            : base("/novalythappearance")
        {
            m_stateDirectory = stateDirectory;
            m_manifestDirectory = manifestDirectory;
            m_token = Encoding.UTF8.GetBytes(token);
            m_bakeReady = bakeReady;
            m_bakeContractVersion = bakeContractVersion;
            m_inventory = inventory;
            m_assets = assets;

            m_log.InfoFormat(
                "[NOVALYTH APPEARANCE C2A]: wearable parser + source graph online; contract={0}; bake-ready={1}",
                m_bakeContractVersion,
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
                health["phase"] = "C2A";
                health["sl_ssa_protocol_surface"] = true;
                health["authoritative_cof"] = "Inventory Core";
                health["bake_contract_version"] = m_bakeContractVersion;
                health["bake_slot_count"] = s_bakeDefinitions.Length;
                health["wearable_asset_parser"] = true;
                health["source_texture_graph"] = true;
                health["source_j2k_decode_audit"] = true;
                health["pixel_compositor"] = false;
                health["bake_asset_store"] = false;
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

                case "manifest":
                    if (httpRequest.HttpMethod == "GET")
                    {
                        HandleManifestGet(agentID, httpResponse);
                        return;
                    }
                    break;

                case "recipe":
                    if (httpRequest.HttpMethod == "GET")
                    {
                        HandleRecipeBuild(agentID, httpResponse);
                        return;
                    }
                    break;

                case "sourceaudit":
                    if (httpRequest.HttpMethod == "GET")
                    {
                        HandleSourceAudit(agentID, httpResponse);
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

        private string ShardedPath(string root, UUID agentID, string extension)
        {
            string id = agentID.ToString();
            string dir = System.IO.Path.Combine(
                root,
                id.Substring(0, 2),
                id.Substring(2, 2));

            return System.IO.Path.Combine(dir, id + extension);
        }

        private string StatePath(UUID agentID)
        {
            return ShardedPath(m_stateDirectory, agentID, ".llsd");
        }

        private string ManifestPath(UUID agentID)
        {
            return ShardedPath(m_manifestDirectory, agentID, ".llsd");
        }

        private AppearanceState LoadState(UUID agentID)
        {
            string path = StatePath(agentID);
            if (!File.Exists(path))
                return new AppearanceState();

            try
            {
                byte[] data = File.ReadAllBytes(path);
                using MemoryStream input = new(data, false);
                OSD osd = OSDParser.DeserializeLLSDXml(input);
                if (osd is OSDMap map)
                    return AppearanceState.FromMap(map);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH APPEARANCE C2A]: state read failed for {0}: {1}",
                    agentID,
                    e.Message);
            }

            return new AppearanceState();
        }

        private void SaveState(UUID agentID, AppearanceState state)
        {
            AtomicWrite(StatePath(agentID), state.ToMap());
        }

        private void SaveManifest(UUID agentID, OSDMap manifest)
        {
            AtomicWrite(ManifestPath(agentID), manifest);
        }

        private static void AtomicWrite(string path, OSDMap map)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);

            byte[] data = OSDParser.SerializeLLSDXmlBytes(map);
            string temp = path + "." + UUID.Random() + ".tmp";

            File.WriteAllBytes(temp, data);
            File.Move(temp, path, true);
        }

        private OSDMap LoadManifest(UUID agentID)
        {
            string path = ManifestPath(agentID);
            if (!File.Exists(path))
                return null;

            try
            {
                byte[] data = File.ReadAllBytes(path);
                using MemoryStream input = new(data, false);
                return OSDParser.DeserializeLLSDXml(input) as OSDMap;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH APPEARANCE C2A]: manifest read failed for {0}: {1}",
                    agentID,
                    e.Message);
                return null;
            }
        }

        private InventoryFolderBase GetAuthoritativeCOF(UUID agentID)
        {
            try
            {
                return m_inventory.GetFolderForType(agentID, FolderType.CurrentOutfit);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH APPEARANCE C2A]: Inventory Core COF lookup failed for {0}: {1}",
                    agentID,
                    e.Message);
                return null;
            }
        }

        private void HandleStateGet(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                AppearanceState state = LoadState(agentID);
                InventoryFolderBase cof = GetAuthoritativeCOF(agentID);

                if (cof != null)
                    state.CofVersion = cof.Version;

                OSDMap map = state.ToMap();
                map["agent_id"] = agentID;
                map["cof_authority"] = "Inventory Core";
                map["server_bake_ready"] = m_bakeReady;
                WriteMap(response, HttpStatusCode.OK, map);
            }
        }

        private void HandleStateDelete(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                string statePath = StatePath(agentID);
                string manifestPath = ManifestPath(agentID);

                if (File.Exists(statePath))
                    File.Delete(statePath);
                if (File.Exists(manifestPath))
                    File.Delete(manifestPath);

                OSDMap result = new();
                result["success"] = true;
                WriteMap(response, HttpStatusCode.OK, result);
            }
        }

        private void HandleManifestGet(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                OSDMap manifest = LoadManifest(agentID);
                if (manifest == null)
                {
                    WriteError(response, HttpStatusCode.NotFound, "manifest_not_found");
                    return;
                }

                WriteMap(response, HttpStatusCode.OK, manifest);
            }
        }

        private void HandleRecipeBuild(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                if (!TryBuildAndPersistRecipe(
                        agentID,
                        out OSDMap manifest,
                        out string error))
                {
                    OSDMap fail = new();
                    fail["success"] = false;
                    fail["error"] = error;
                    WriteMap(response, HttpStatusCode.OK, fail);
                    return;
                }

                manifest["success"] = true;
                WriteMap(response, HttpStatusCode.OK, manifest);
            }
        }

        private void HandleSourceAudit(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                if (!TryBuildAndPersistRecipe(
                        agentID,
                        out OSDMap manifest,
                        out string error))
                {
                    OSDMap fail = new();
                    fail["success"] = false;
                    fail["error"] = error;
                    WriteMap(response, HttpStatusCode.OK, fail);
                    return;
                }

                AuditSourceTextures(manifest);
                SaveManifest(agentID, manifest);

                AppearanceState state = LoadState(agentID);
                state.ManifestStatus = manifest["status"].AsString();
                state.UpdatedUtc = DateTime.UtcNow.ToString("O");
                SaveState(agentID, state);

                manifest["success"] = true;
                WriteMap(response, HttpStatusCode.OK, manifest);
            }
        }

        private void HandleIncrement(UUID agentID, IOSHttpResponse response)
        {
            lock (GetAgentLock(agentID))
            {
                InventoryFolderBase cof = GetAuthoritativeCOF(agentID);
                if (cof == null)
                {
                    OSDMap fail = new();
                    fail["success"] = false;
                    fail["error"] = "cof_not_found";
                    WriteMap(response, HttpStatusCode.OK, fail);
                    return;
                }

                ushort current = cof.Version;
                cof.Version = current == ushort.MaxValue
                    ? (ushort)1
                    : (ushort)(current + 1);

                bool updated;
                try
                {
                    updated = m_inventory.UpdateFolder(cof);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat(
                        "[NOVALYTH APPEARANCE C2A]: COF version increment failed for {0}: {1}",
                        agentID,
                        e.Message);
                    updated = false;
                }

                if (!updated)
                {
                    OSDMap fail = new();
                    fail["success"] = false;
                    fail["error"] = "cof_increment_failed";
                    fail["version"] = current;
                    WriteMap(response, HttpStatusCode.OK, fail);
                    return;
                }

                AppearanceState state = LoadState(agentID);
                state.CofVersion = cof.Version;
                state.UpdatedUtc = DateTime.UtcNow.ToString("O");
                SaveState(agentID, state);

                OSDMap result = new();
                result["success"] = true;
                result["version"] = (int)cof.Version;
                result["appearance_version"] = state.AppearanceVersion;
                result["cof_authority"] = "Inventory Core";
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
                InventoryFolderBase cof = GetAuthoritativeCOF(agentID);
                if (cof == null)
                {
                    OSDMap fail = new();
                    fail["success"] = false;
                    fail["error"] = "cof_not_found";
                    WriteMap(response, HttpStatusCode.OK, fail);
                    return;
                }

                int authoritativeVersion = cof.Version;

                if (requested != authoritativeVersion)
                {
                    OSDMap stale = new();
                    stale["success"] = false;
                    stale["error"] = "stale_cof_version";
                    stale["expected"] = authoritativeVersion;
                    stale["observed"] = requested;
                    stale["version"] = authoritativeVersion;
                    WriteMap(response, HttpStatusCode.OK, stale);
                    return;
                }

                bool recipeReady =
                    TryBuildAndPersistRecipe(
                        agentID,
                        out OSDMap manifest,
                        out string recipeError);

                AppearanceState state = LoadState(agentID);
                state.CofVersion = authoritativeVersion;

                if (recipeReady)
                {
                    state.RecipeHash = manifest["recipe_hash"].AsString();
                    state.RecipeCofVersion = authoritativeVersion;
                    state.ManifestStatus = manifest["status"].AsString();
                }
                else
                {
                    state.ManifestStatus = recipeError;
                }

                state.UpdatedUtc = DateTime.UtcNow.ToString("O");
                SaveState(agentID, state);

                // C1 deliberately never claims that pixels were baked.
                OSDMap pending = new();
                pending["success"] = false;
                pending["error"] = "server_bake_not_active";
                pending["expected"] = authoritativeVersion;
                pending["version"] = authoritativeVersion;
                pending["appearance_version"] = state.AppearanceVersion;
                pending["recipe_ready"] = recipeReady;
                pending["recipe_error"] = recipeError ?? string.Empty;
                pending["recipe_hash"] = state.RecipeHash ?? string.Empty;
                pending["bake_contract_version"] = m_bakeContractVersion;

                WriteMap(response, HttpStatusCode.OK, pending);
            }
        }

        private bool TryBuildAndPersistRecipe(
            UUID agentID,
            out OSDMap manifest,
            out string error)
        {
            manifest = null;
            error = string.Empty;

            InventoryFolderBase cof = GetAuthoritativeCOF(agentID);
            if (cof == null)
            {
                error = "cof_not_found";
                return false;
            }

            InventoryCollection content;
            try
            {
                content = m_inventory.GetFolderContent(agentID, cof.ID);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[NOVALYTH APPEARANCE C2A]: COF content lookup failed for {0}: {1}",
                    agentID,
                    e.Message);
                error = "cof_read_failed";
                return false;
            }

            if (content == null)
            {
                error = "cof_read_failed";
                return false;
            }

            List<RecipeItem> wearables = new();
            List<RecipeItem> attachments = new();
            List<string> brokenLinks = new();
            List<string> missingAssets = new();
            List<string> wearableParseErrors = new();
            List<string> wearableTypeMismatches = new();

            foreach (InventoryItemBase link in content.Items)
            {
                InventoryItemBase target = null;

                if (link.AssetType == (int)AssetType.Link)
                {
                    try
                    {
                        target = m_inventory.GetItem(agentID, link.AssetID);
                    }
                    catch
                    {
                        target = null;
                    }

                    if (target == null)
                    {
                        brokenLinks.Add(link.ID.ToString());
                        continue;
                    }
                }
                else
                {
                    // Keep compatibility with grids that place a direct item in COF.
                    target = link;
                }

                RecipeItem item = new()
                {
                    LinkItemID = link.ID,
                    InventoryItemID = target.ID,
                    AssetID = target.AssetID,
                    AssetType = target.AssetType,
                    InventoryType = target.InvType,
                    Flags = target.Flags,
                    Name = target.Name ?? string.Empty,
                    OrderKey = link.Description ?? string.Empty,
                    WearableType = (int)(target.Flags & 0xff)
                };

                if (target.InvType == (int)InventoryType.Wearable &&
                    (target.AssetType == (int)AssetType.Bodypart ||
                     target.AssetType == (int)AssetType.Clothing))
                {
                    AssetBase wearableAsset = null;

                    if (!target.AssetID.IsZero())
                    {
                        try
                        {
                            wearableAsset = m_assets.Get(target.AssetID.ToString());
                        }
                        catch (Exception e)
                        {
                            m_log.WarnFormat(
                                "[NOVALYTH APPEARANCE C2A]: wearable asset fetch failed {0}: {1}",
                                target.AssetID,
                                e.Message);
                        }
                    }

                    item.AssetPresent = wearableAsset?.Data != null;

                    if (!item.AssetPresent)
                    {
                        missingAssets.Add(target.AssetID.ToString());
                    }
                    else if (TryParseWearableAsset(
                                 wearableAsset.Data,
                                 out ParsedWearable parsed,
                                 out string parseError))
                    {
                        item.ParsedWearable = parsed;
                        item.WearablePayloadHash = parsed.ComputeHash();
                        item.WearableTypeMatchesAsset = parsed.WearableType == item.WearableType;
                        if (!item.WearableTypeMatchesAsset)
                        {
                            wearableTypeMismatches.Add(
                                target.AssetID + ":inventory=" + item.WearableType +
                                ",asset=" + parsed.WearableType);
                        }
                    }
                    else
                    {
                        item.WearableParseError = parseError;
                        wearableParseErrors.Add(
                            target.AssetID + ":" + parseError);
                    }

                    wearables.Add(item);
                }
                else if (target.InvType == (int)InventoryType.Object)
                {
                    attachments.Add(item);
                }
            }

            wearables.Sort(RecipeItem.Compare);
            attachments.Sort(RecipeItem.Compare);

            List<SourceTextureLayer> sourceLayers = BuildSourceTextureGraph(wearables);
            List<string> missingSourceAssets = ProbeSourceAssetExistence(sourceLayers);

            string recipeHash =
                ComputeRecipeHash(
                    agentID,
                    cof,
                    wearables,
                    attachments,
                    brokenLinks);

            OSDArray wearableArray = new();
            foreach (RecipeItem item in wearables)
                wearableArray.Add(item.ToOSD());

            OSDArray attachmentArray = new();
            foreach (RecipeItem item in attachments)
                attachmentArray.Add(item.ToOSD());

            OSDArray brokenArray = ToStringArray(brokenLinks);
            OSDArray missingArray = ToStringArray(missingAssets);
            OSDArray parseErrorArray = ToStringArray(wearableParseErrors);
            OSDArray typeMismatchArray = ToStringArray(wearableTypeMismatches);
            OSDArray missingSourceArray = ToStringArray(missingSourceAssets);

            OSDArray sourceTextureArray = new();
            foreach (SourceTextureLayer layer in sourceLayers)
                sourceTextureArray.Add(layer.ToOSD());

            OSDArray bakes = new();

            foreach (BakeDefinition bake in s_bakeDefinitions)
            {
                OSDMap bakeMap = new();
                bakeMap["index"] = bake.Index;
                bakeMap["name"] = bake.Name;
                bakeMap["status"] = "pending_compositor";
                bakeMap["asset_id"] = UUID.Zero;

                OSDArray contributors = new();

                foreach (RecipeItem item in wearables)
                {
                    if (bake.WearableTypes.Contains(item.WearableType))
                    {
                        OSDMap contribution = new();
                        contribution["wearable_type"] = item.WearableType;
                        contribution["inventory_item_id"] = item.InventoryItemID;
                        contribution["asset_id"] = item.AssetID;
                        contribution["order"] = item.OrderKey;
                        contribution["wearable_payload_hash"] = item.WearablePayloadHash;
                        contributors.Add(contribution);
                    }
                }

                OSDArray bakeSourceLayers = new();
                foreach (SourceTextureLayer layer in sourceLayers)
                {
                    if (string.Equals(
                            layer.BakeSlot,
                            bake.Name,
                            StringComparison.Ordinal))
                    {
                        bakeSourceLayers.Add(layer.ToOSD());
                    }
                }

                bakeMap["contributors"] = contributors;
                bakeMap["source_layers"] = bakeSourceLayers;
                bakes.Add(bakeMap);
            }

            manifest = new OSDMap();
            manifest["agent_id"] = agentID;
            manifest["cof_folder_id"] = cof.ID;
            manifest["cof_version"] = (int)cof.Version;
            manifest["cof_authority"] = "Inventory Core";
            manifest["recipe_hash"] = recipeHash;
            manifest["recipe_format"] = "novalyth-ssa-recipe-v2";
            manifest["bake_contract_version"] = m_bakeContractVersion;
            manifest["bake_slot_count"] = s_bakeDefinitions.Length;
            manifest["generated_utc"] = DateTime.UtcNow.ToString("O");
            manifest["wearables"] = wearableArray;
            manifest["attachments"] = attachmentArray;
            manifest["broken_links"] = brokenArray;
            manifest["missing_assets"] = missingArray;
            manifest["wearable_parse_errors"] = parseErrorArray;
            manifest["wearable_type_mismatches"] = typeMismatchArray;
            manifest["source_textures"] = sourceTextureArray;
            manifest["missing_source_assets"] = missingSourceArray;
            manifest["source_texture_count"] = sourceLayers.Count;
            manifest["source_audit_status"] = "not_run";
            manifest["pixel_compositor_status"] = "not_implemented_c2a";
            manifest["bakes"] = bakes;

            if (brokenLinks.Count > 0 ||
                missingAssets.Count > 0 ||
                wearableParseErrors.Count > 0 ||
                wearableTypeMismatches.Count > 0 ||
                missingSourceAssets.Count > 0)
            {
                manifest["status"] = "recipe_incomplete";
            }
            else
            {
                manifest["status"] = "recipe_source_ready";
            }

            SaveManifest(agentID, manifest);

            AppearanceState state = LoadState(agentID);
            state.CofVersion = cof.Version;
            state.RecipeCofVersion = cof.Version;
            state.RecipeHash = recipeHash;
            state.ManifestStatus = manifest["status"].AsString();
            state.UpdatedUtc = DateTime.UtcNow.ToString("O");
            SaveState(agentID, state);

            error = string.Empty;
            return true;
        }

        private static OSDArray ToStringArray(IEnumerable<string> values)
        {
            OSDArray result = new();

            foreach (string value in values
                         .Where(x => !string.IsNullOrEmpty(x))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                result.Add(OSD.FromString(value));
            }

            return result;
        }

        private List<string> ProbeSourceAssetExistence(
            IEnumerable<SourceTextureLayer> layers)
        {
            List<string> missing = new();

            foreach (UUID textureID in layers
                         .Select(x => x.TextureID)
                         .Where(x => !x.IsZero())
                         .Distinct()
                         .OrderBy(x => x.ToString(), StringComparer.Ordinal))
            {
                bool present = false;

                try
                {
                    present = m_assets.GetMetadata(textureID.ToString()) != null;
                }
                catch
                {
                    present = false;
                }

                if (!present)
                    missing.Add(textureID.ToString());
            }

            return missing;
        }

        private static List<SourceTextureLayer> BuildSourceTextureGraph(
            IEnumerable<RecipeItem> wearables)
        {
            List<SourceTextureLayer> result = new();
            int sequence = 0;

            foreach (RecipeItem item in wearables)
            {
                if (item.ParsedWearable == null)
                    continue;

                foreach (WearableTexture texture in item.ParsedWearable.Textures
                             .OrderBy(x => x.TextureIndex))
                {
                    if (!s_sourceTextureBakeSlots.TryGetValue(
                            texture.TextureIndex,
                            out string bakeSlot))
                    {
                        continue;
                    }

                    if (texture.TextureID.IsZero())
                        continue;

                    result.Add(new SourceTextureLayer
                    {
                        Sequence = sequence++,
                        BakeSlot = bakeSlot,
                        TextureIndex = texture.TextureIndex,
                        TextureID = texture.TextureID,
                        WearableType = item.WearableType,
                        InventoryItemID = item.InventoryItemID,
                        WearableAssetID = item.AssetID,
                        OrderKey = item.OrderKey ?? string.Empty
                    });
                }
            }

            return result;
        }

        private void AuditSourceTextures(OSDMap manifest)
        {
            if (!manifest.TryGetValue("source_textures", out OSD sourceOSD) ||
                sourceOSD is not OSDArray sourceTextures)
            {
                manifest["source_audit_status"] = "no_source_graph";
                manifest["status"] = "recipe_incomplete";
                return;
            }

            Dictionary<UUID, SourceDecodeResult> decoded = new();
            OSDArray decodeErrors = new();
            int successfulUnique = 0;

            foreach (OSD entry in sourceTextures)
            {
                if (entry is not OSDMap layer ||
                    !layer.TryGetValue("texture_id", out OSD textureOSD) ||
                    !UUID.TryParse(textureOSD.AsString(), out UUID textureID) ||
                    textureID.IsZero())
                {
                    continue;
                }

                if (!decoded.TryGetValue(textureID, out SourceDecodeResult result))
                {
                    result = DecodeSourceTexture(textureID);
                    decoded[textureID] = result;
                    if (result.Success)
                        successfulUnique++;
                    else
                        decodeErrors.Add(OSD.FromString(
                            textureID + ":" + result.Error));
                }

                layer["decode_status"] = result.Success ? "decoded" : "decode_failed";
                layer["width"] = result.Width;
                layer["height"] = result.Height;
                layer["asset_type"] = result.AssetType;
                layer["decode_error"] = result.Error ?? string.Empty;
            }

            manifest["source_audit_utc"] = DateTime.UtcNow.ToString("O");
            manifest["source_unique_texture_count"] = decoded.Count;
            manifest["source_decoded_unique_count"] = successfulUnique;
            manifest["source_decode_errors"] = decodeErrors;

            if (decodeErrors.Count == 0)
            {
                manifest["source_audit_status"] = "source_j2k_ready";

                if (manifest["status"].AsString() == "recipe_source_ready")
                    manifest["status"] = "source_decode_ready";
            }
            else
            {
                manifest["source_audit_status"] = "source_j2k_incomplete";
                manifest["status"] = "recipe_incomplete";
            }
        }

        private SourceDecodeResult DecodeSourceTexture(UUID textureID)
        {
            try
            {
                AssetBase asset = m_assets.Get(textureID.ToString());
                if (asset?.Data == null || asset.Data.Length == 0)
                {
                    return SourceDecodeResult.Fail("asset_not_found");
                }

                ManagedImage managedImage;
                Image image;

                if (!OpenJPEG.DecodeToImage(
                        asset.Data,
                        out managedImage,
                        out image) || image == null)
                {
                    return SourceDecodeResult.Fail("j2k_decode_failed", asset.Type);
                }

                try
                {
                    return SourceDecodeResult.Ok(
                        image.Width,
                        image.Height,
                        asset.Type);
                }
                finally
                {
                    image.Dispose();
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat(
                    "[NOVALYTH APPEARANCE C2A]: source texture decode failed {0}: {1}",
                    textureID,
                    e.Message);
                return SourceDecodeResult.Fail(e.GetType().Name);
            }
        }

        private static bool TryParseWearableAsset(
            byte[] data,
            out ParsedWearable parsed,
            out string error)
        {
            parsed = null;
            error = string.Empty;

            if (data == null || data.Length == 0)
            {
                error = "empty_wearable_asset";
                return false;
            }

            string text;
            try
            {
                text = Encoding.UTF8.GetString(data);
            }
            catch
            {
                error = "wearable_text_decode_failed";
                return false;
            }

            using StringReader reader = new(text);
            ParsedWearable result = new();
            bool sawType = false;
            bool sawParameters = false;
            bool sawTextures = false;
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (trimmed.StartsWith("type ", StringComparison.Ordinal))
                {
                    string value = trimmed.Substring(5).Trim();
                    if (!int.TryParse(
                            value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int wearableType))
                    {
                        error = "invalid_wearable_type";
                        return false;
                    }

                    result.WearableType = wearableType;
                    sawType = true;
                    continue;
                }

                if (trimmed.StartsWith("parameters ", StringComparison.Ordinal))
                {
                    if (!TryParseCount(trimmed, "parameters", MaxWearableParameters, out int count))
                    {
                        error = "invalid_parameters_header";
                        return false;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        string paramLine = ReadNextPopulatedLine(reader);
                        if (paramLine == null)
                        {
                            error = "unexpected_eof_parameters";
                            return false;
                        }

                        string[] fields = paramLine.Split(
                            (char[])null,
                            StringSplitOptions.RemoveEmptyEntries);

                        if (fields.Length < 2 ||
                            !int.TryParse(
                                fields[0],
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int id) ||
                            !float.TryParse(
                                fields[1],
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out float weight))
                        {
                            error = "invalid_parameter_entry";
                            return false;
                        }

                        result.Parameters.Add(new WearableParameter(id, weight));
                    }

                    sawParameters = true;
                    continue;
                }

                if (trimmed.StartsWith("textures ", StringComparison.Ordinal))
                {
                    if (!TryParseCount(trimmed, "textures", MaxWearableTextures, out int count))
                    {
                        error = "invalid_textures_header";
                        return false;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        string textureLine = ReadNextPopulatedLine(reader);
                        if (textureLine == null)
                        {
                            error = "unexpected_eof_textures";
                            return false;
                        }

                        string[] fields = textureLine.Split(
                            (char[])null,
                            StringSplitOptions.RemoveEmptyEntries);

                        if (fields.Length < 2 ||
                            !int.TryParse(
                                fields[0],
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int textureIndex) ||
                            textureIndex < 0 || textureIndex >= 45 ||
                            !UUID.TryParse(fields[1], out UUID textureID))
                        {
                            error = "invalid_texture_entry";
                            return false;
                        }

                        result.Textures.Add(
                            new WearableTexture(textureIndex, textureID));
                    }

                    sawTextures = true;
                }
            }

            if (!sawType)
            {
                error = "missing_wearable_type";
                return false;
            }

            if (!sawParameters)
            {
                error = "missing_parameters_block";
                return false;
            }

            if (!sawTextures)
            {
                error = "missing_textures_block";
                return false;
            }

            parsed = result;
            return true;
        }

        private static bool TryParseCount(
            string line,
            string keyword,
            int maximum,
            out int count)
        {
            count = 0;
            string value = line.Substring(keyword.Length).Trim();
            return int.TryParse(
                       value,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out count)
                && count >= 0
                && count <= maximum;
        }

        private static string ReadNextPopulatedLine(StringReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length > 0)
                    return line;
            }

            return null;
        }

        private static string ComputeRecipeHash(
            UUID agentID,
            InventoryFolderBase cof,
            List<RecipeItem> wearables,
            List<RecipeItem> attachments,
            List<string> brokenLinks)
        {
            StringBuilder canonical = new();

            canonical.Append("novalyth-ssa-recipe-v2\n");
            canonical.Append(agentID).Append('\n');
            canonical.Append(cof.ID).Append('\n');
            canonical.Append(cof.Version).Append('\n');

            foreach (RecipeItem item in wearables)
                canonical.Append("W|").Append(item.Canonical()).Append('\n');

            foreach (RecipeItem item in attachments)
                canonical.Append("A|").Append(item.Canonical()).Append('\n');

            foreach (string id in brokenLinks.OrderBy(x => x, StringComparer.Ordinal))
                canonical.Append("B|").Append(id).Append('\n');

            byte[] digest =
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

            return Convert.ToHexString(digest).ToLowerInvariant();
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
            public int RecipeCofVersion;
            public string RecipeHash = string.Empty;
            public string ManifestStatus = string.Empty;
            public string UpdatedUtc = string.Empty;

            public OSDMap ToMap()
            {
                OSDMap map = new();
                map["cof_version"] = CofVersion;
                map["appearance_version"] = AppearanceVersion;
                map["recipe_cof_version"] = RecipeCofVersion;
                map["recipe_hash"] = RecipeHash ?? string.Empty;
                map["manifest_status"] = ManifestStatus ?? string.Empty;
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

                if (map.TryGetValue("recipe_cof_version", out OSD recipeVersion))
                    state.RecipeCofVersion = recipeVersion.AsInteger();

                if (map.TryGetValue("recipe_hash", out OSD recipeHash))
                    state.RecipeHash = recipeHash.AsString();

                if (map.TryGetValue("manifest_status", out OSD manifestStatus))
                    state.ManifestStatus = manifestStatus.AsString();

                if (map.TryGetValue("updated_utc", out OSD updated))
                    state.UpdatedUtc = updated.AsString();

                return state;
            }
        }

        private sealed class RecipeItem
        {
            public UUID LinkItemID;
            public UUID InventoryItemID;
            public UUID AssetID;
            public int AssetType;
            public int InventoryType;
            public uint Flags;
            public int WearableType;
            public string Name = string.Empty;
            public string OrderKey = string.Empty;
            public bool AssetPresent;
            public ParsedWearable ParsedWearable;
            public string WearableParseError = string.Empty;
            public string WearablePayloadHash = string.Empty;
            public bool WearableTypeMatchesAsset;

            public static int Compare(RecipeItem a, RecipeItem b)
            {
                int c = a.WearableType.CompareTo(b.WearableType);
                if (c != 0)
                    return c;

                c = string.Compare(
                    a.OrderKey,
                    b.OrderKey,
                    StringComparison.Ordinal);

                if (c != 0)
                    return c;

                return string.Compare(
                    a.LinkItemID.ToString(),
                    b.LinkItemID.ToString(),
                    StringComparison.Ordinal);
            }

            public string Canonical()
            {
                return string.Join(
                    "|",
                    WearableType,
                    LinkItemID,
                    InventoryItemID,
                    AssetID,
                    AssetType,
                    InventoryType,
                    Flags,
                    OrderKey ?? string.Empty,
                    WearablePayloadHash ?? string.Empty);
            }

            public OSDMap ToOSD()
            {
                OSDMap map = new();
                map["link_item_id"] = LinkItemID;
                map["inventory_item_id"] = InventoryItemID;
                map["asset_id"] = AssetID;
                map["asset_type"] = AssetType;
                map["inventory_type"] = InventoryType;
                map["flags"] = (int)Flags;
                map["wearable_type"] = WearableType;
                map["name"] = Name ?? string.Empty;
                map["order"] = OrderKey ?? string.Empty;
                map["asset_present"] = AssetPresent;
                map["wearable_parse_status"] = ParsedWearable != null
                    ? "parsed"
                    : (AssetPresent ? "parse_failed" : "asset_missing");
                map["wearable_parse_error"] = WearableParseError ?? string.Empty;
                map["wearable_payload_hash"] = WearablePayloadHash ?? string.Empty;

                if (ParsedWearable != null)
                {
                    map["asset_wearable_type"] = ParsedWearable.WearableType;
                    map["wearable_type_matches_asset"] = WearableTypeMatchesAsset;
                    map["parameters"] = ParsedWearable.ParametersToOSD();
                    map["textures"] = ParsedWearable.TexturesToOSD();
                }

                return map;
            }
        }

        private sealed class ParsedWearable
        {
            public int WearableType = -1;
            public List<WearableParameter> Parameters { get; } = new();
            public List<WearableTexture> Textures { get; } = new();

            public string ComputeHash()
            {
                StringBuilder canonical = new();
                canonical.Append("wearable-v1|").Append(WearableType).Append('\n');

                foreach (WearableParameter parameter in Parameters.OrderBy(x => x.ID))
                {
                    canonical.Append("P|")
                        .Append(parameter.ID)
                        .Append('|')
                        .Append(parameter.Weight.ToString("R", CultureInfo.InvariantCulture))
                        .Append('\n');
                }

                foreach (WearableTexture texture in Textures.OrderBy(x => x.TextureIndex))
                {
                    canonical.Append("T|")
                        .Append(texture.TextureIndex)
                        .Append('|')
                        .Append(texture.TextureID)
                        .Append('\n');
                }

                byte[] digest = SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString()));
                return Convert.ToHexString(digest).ToLowerInvariant();
            }

            public OSDArray ParametersToOSD()
            {
                OSDArray result = new();
                foreach (WearableParameter parameter in Parameters.OrderBy(x => x.ID))
                {
                    OSDMap entry = new();
                    entry["id"] = parameter.ID;
                    entry["weight"] = parameter.Weight;
                    result.Add(entry);
                }
                return result;
            }

            public OSDArray TexturesToOSD()
            {
                OSDArray result = new();
                foreach (WearableTexture texture in Textures.OrderBy(x => x.TextureIndex))
                {
                    OSDMap entry = new();
                    entry["texture_index"] = texture.TextureIndex;
                    entry["texture_id"] = texture.TextureID;
                    entry["bake_slot"] = s_sourceTextureBakeSlots.TryGetValue(
                        texture.TextureIndex,
                        out string bakeSlot) ? bakeSlot : string.Empty;
                    result.Add(entry);
                }
                return result;
            }
        }

        private readonly struct WearableParameter
        {
            public int ID { get; }
            public float Weight { get; }

            public WearableParameter(int id, float weight)
            {
                ID = id;
                Weight = weight;
            }
        }

        private readonly struct WearableTexture
        {
            public int TextureIndex { get; }
            public UUID TextureID { get; }

            public WearableTexture(int textureIndex, UUID textureID)
            {
                TextureIndex = textureIndex;
                TextureID = textureID;
            }
        }

        private sealed class SourceTextureLayer
        {
            public int Sequence;
            public string BakeSlot = string.Empty;
            public int TextureIndex;
            public UUID TextureID;
            public int WearableType;
            public UUID InventoryItemID;
            public UUID WearableAssetID;
            public string OrderKey = string.Empty;

            public OSDMap ToOSD()
            {
                OSDMap map = new();
                map["sequence"] = Sequence;
                map["bake_slot"] = BakeSlot ?? string.Empty;
                map["texture_index"] = TextureIndex;
                map["texture_id"] = TextureID;
                map["wearable_type"] = WearableType;
                map["inventory_item_id"] = InventoryItemID;
                map["wearable_asset_id"] = WearableAssetID;
                map["order"] = OrderKey ?? string.Empty;
                return map;
            }
        }

        private sealed class SourceDecodeResult
        {
            public bool Success;
            public int Width;
            public int Height;
            public int AssetType;
            public string Error = string.Empty;

            public static SourceDecodeResult Ok(int width, int height, int assetType)
            {
                return new SourceDecodeResult
                {
                    Success = true,
                    Width = width,
                    Height = height,
                    AssetType = assetType
                };
            }

            public static SourceDecodeResult Fail(string error, int assetType = -1)
            {
                return new SourceDecodeResult
                {
                    Success = false,
                    AssetType = assetType,
                    Error = error ?? string.Empty
                };
            }
        }

        private sealed class BakeDefinition
        {
            public int Index { get; }
            public string Name { get; }
            public HashSet<int> WearableTypes { get; }

            public BakeDefinition(
                int index,
                string name,
                IEnumerable<int> wearableTypes)
            {
                Index = index;
                Name = name;
                WearableTypes = new HashSet<int>(wearableTypes);
            }
        }
    }
}
