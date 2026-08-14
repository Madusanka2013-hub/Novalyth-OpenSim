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
using System.Xml.Linq;
using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Assets;
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

        // NOVALYTH APPEARANCE C4D3
        // Project Sunshine/SSA AgentAppearance version. Firestorm also knows
        // this value as visual parameter 11000 (AppearanceMessage_Version).
        private const int ServerAppearanceVersion = 1;
        private const int AppearanceMessageVersionParamID = 11000;

        // NOVALYTH APPEARANCE C5
        // Pixel output MUST be versioned independently from the outfit recipe.
        // Any compositor semantic change automatically invalidates every older
        // stored bake even when COF/wearables are byte-identical.
        private const string CompositorProfile =
            "novalyth-c5-sl-reference-2k-v1";

        private const string CompositorFingerprint =
            "novalyth-c5-sl-reference-2k-v1|" +
            "viewer=dcff8c97ea5448f0acaff76fa0a74a89889be678|" +
            "avatar_lad=83380dcc2cbc3bce0e74b9429a2da3f7b701756c74248b6edeef6b61c96c20da|" +
            "libomv=f3ee229dc2a17e0dfd7256f8f634afe8e7610487";

        private const string CompositorSemantics =
            "sl-reference-color-ops_skin-bodypaint-authority_head-order_" +
            "alpha-final_write-all_local-alpha-only_morph-bump_modern-11";

        // NOVALYTH APPEARANCE C4D2
        // SL bodyparts are singleton authorities. Multiple clothing/tattoo/
        // alpha/universal layers are valid, but Shape/Skin/Hair/Eyes must each
        // resolve to exactly one item in the authoritative final COF.
        private static readonly int[] s_singletonBodypartWearableTypes =
        {
            WT_SHAPE, WT_SKIN, WT_HAIR, WT_EYES
        };

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

        // C2B1 intentionally keeps bake execution bounded. The service is
        // internal-only until C3, but parallel login/outfit changes must still
        // not turn 2K compositing into an unbounded memory spike.
        private static readonly SemaphoreSlim s_bakeConcurrency = new(2, 2);

        private const string AvatarLadSourceCommit =
            "dcff8c97ea5448f0acaff76fa0a74a89889be678";

        private static readonly string s_avatarLadPath =
            System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "openmetaverse_data",
                "novalyth_avatar_lad.xml");

        private static readonly System.Lazy<AvatarLadProfile> s_avatarLadProfile =
            new(LoadAvatarLadProfile, true);

        private static readonly IReadOnlyDictionary<string, int>
            s_avatarLadLocalTextureIndices =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["head_bodypaint"] = 0,
                ["upper_shirt"] = 1,
                ["lower_pants"] = 2,
                ["eyes_iris"] = 3,
                ["hair"] = 4,
                ["hair_grain"] = 4,
                ["upper_bodypaint"] = 5,
                ["lower_bodypaint"] = 6,
                ["lower_shoes"] = 7,
                ["lower_socks"] = 12,
                ["upper_jacket"] = 13,
                ["lower_jacket"] = 14,
                ["upper_gloves"] = 15,
                ["upper_undershirt"] = 16,
                ["lower_underpants"] = 17,
                ["skirt"] = 18,
                ["lower_alpha"] = 21,
                ["upper_alpha"] = 22,
                ["head_alpha"] = 23,
                ["eyes_alpha"] = 24,
                ["hair_alpha"] = 25,
                ["head_tattoo"] = 26,
                ["upper_tattoo"] = 27,
                ["lower_tattoo"] = 28,
                ["head_universal_tattoo"] = 29,
                ["upper_universal_tattoo"] = 30,
                ["lower_universal_tattoo"] = 31,
                ["skirt_tattoo"] = 32,
                ["hair_tattoo"] = 33,
                ["eyes_tattoo"] = 34,
                ["left_arm_tattoo"] = 35,
                ["leftarm_tattoo"] = 35,
                ["left_leg_tattoo"] = 36,
                ["leftleg_tattoo"] = 36,
                ["aux1_tattoo"] = 37,
                ["aux2_tattoo"] = 38,
                ["aux3_tattoo"] = 39
            };


        // Direct RGB visual-param triplets for local textures whose tint is
        // channel-specific. This is especially important for Universal
        // wearables: one Universal asset can carry different colors for head,
        // upper, lower, skirt, hair, eyes and all five auxiliary bakes.
        private static readonly IReadOnlyDictionary<int, int[]> s_textureRgbParams =
            new Dictionary<int, int[]>
            {
                [1] = new[] { 803, 804, 805 },       // shirt
                [2] = new[] { 806, 807, 808 },       // pants
                [7] = new[] { 812, 813, 817 },       // shoes
                [12] = new[] { 818, 819, 820 },      // socks
                [13] = new[] { 831, 832, 833 },      // jacket upper
                [14] = new[] { 809, 810, 811 },      // jacket lower
                [15] = new[] { 827, 829, 830 },      // gloves
                [16] = new[] { 821, 822, 823 },      // undershirt
                [17] = new[] { 824, 825, 826 },      // underpants
                [18] = new[] { 921, 922, 923 },      // skirt
                [26] = new[] { 1062, 1063, 1064 },   // head tattoo
                [27] = new[] { 1065, 1066, 1067 },   // upper tattoo
                [28] = new[] { 1068, 1069, 1070 },   // lower tattoo
                [29] = new[] { 1229, 1230, 1231 },   // head universal
                [30] = new[] { 1232, 1233, 1234 },   // upper universal
                [31] = new[] { 1235, 1236, 1237 },   // lower universal
                [32] = new[] { 1208, 1209, 1210 },   // skirt universal
                [33] = new[] { 1211, 1212, 1213 },   // hair universal
                [34] = new[] { 924, 925, 926 },      // eyes universal
                [35] = new[] { 1214, 1215, 1216 },   // left arm universal
                [36] = new[] { 1217, 1218, 1219 },   // left leg universal
                [37] = new[] { 1220, 1221, 1222 },   // aux1 universal
                [38] = new[] { 1223, 1224, 1225 },   // aux2 universal
                [39] = new[] { 1226, 1227, 1228 }    // aux3 universal
            };

        private readonly string m_stateDirectory;
        private readonly string m_manifestDirectory;
        private readonly byte[] m_token;
        private readonly bool m_bakeReady;
        private readonly string m_bakeContractVersion;
        private readonly IInventoryService m_inventory;
        private readonly IAssetService m_assets;

        // NOVALYTH APPEARANCE C4D
        // Short state/control operations must never queue behind a 2K bake.
        // A separate per-agent bake lock serializes expensive bake execution,
        // while UpdateAvatarAppearance can acknowledge newer COF generations
        // immediately so the Region can suppress stale results.
        private readonly ConcurrentDictionary<UUID, object> m_agentLocks = new();
        private readonly ConcurrentDictionary<UUID, object> m_agentBakeLocks = new();

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

            AvatarLadProfile avatarLad = s_avatarLadProfile.Value;
            if (avatarLad.Sets.Count != 11)
                throw new Exception("Novalyth C2B2 avatar_lad profile does not contain 11 bake layer sets");

            m_log.InfoFormat(
                "[NOVALYTH APPEARANCE C2B2]: 11-slot compositor foundation + J2K Asset Core store online; contract={0}; bake-ready={1}",
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
                health["phase"] = "C2B2";
                health["sl_ssa_protocol_surface"] = true;
                health["authoritative_cof"] = "Inventory Core";
                health["bake_contract_version"] = m_bakeContractVersion;
                health["bake_slot_count"] = s_bakeDefinitions.Length;
                health["wearable_asset_parser"] = true;
                health["source_texture_graph"] = true;
                health["source_j2k_decode_audit"] = true;
                health["pixel_compositor"] = true;
                health["compositor_profile"] = CompositorProfile;
                health["avatar_lad_profile"] = true;
                health["avatar_lad_source_commit"] = AvatarLadSourceCommit;
                health["avatar_lad_layer_sets"] = s_avatarLadProfile.Value.Sets.Count;
                health["avatar_lad_static_layer_parity"] = true;
                health["bake_asset_store"] = true;
                health["bake_asset_authority"] = "Asset Core";
                health["central_bake_advertised"] = false;
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

                case "bake":
                    if (httpRequest.HttpMethod == "POST")
                    {
                        HandleBakeBuild(agentID, httpResponse);
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

        private object GetAgentBakeLock(UUID agentID)
        {
            return m_agentBakeLocks.GetOrAdd(agentID, _ => new object());
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
                    "[NOVALYTH APPEARANCE C2B2]: state read failed for {0}: {1}",
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
                    "[NOVALYTH APPEARANCE C2B2]: manifest read failed for {0}: {1}",
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
                    "[NOVALYTH APPEARANCE C2B2]: Inventory Core COF lookup failed for {0}: {1}",
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


        private void HandleBakeBuild(UUID agentID, IOSHttpResponse response)
        {
            // C4D: serialize expensive bakes separately. Do NOT hold the short
            // state/control lock for the full decode/composite/J2K duration.
            lock (GetAgentBakeLock(agentID))
            {
                if (!s_bakeConcurrency.Wait(TimeSpan.FromSeconds(120)))
                {
                    OSDMap busy = new();
                    busy["success"] = false;
                    busy["error"] = "bake_capacity_timeout";
                    WriteMap(response, HttpStatusCode.ServiceUnavailable, busy);
                    return;
                }

                try
                {
                    OSDMap previous = LoadManifest(agentID);

                    if (!TryBuildAndPersistRecipe(
                            agentID,
                            out OSDMap manifest,
                            out string recipeError))
                    {
                        OSDMap fail = new();
                        fail["success"] = false;
                        fail["error"] = recipeError;
                        WriteMap(response, HttpStatusCode.OK, fail);
                        return;
                    }

                    if (manifest["status"].AsString() != "recipe_source_ready")
                    {
                        SaveManifest(agentID, manifest);

                        OSDMap fail = new();
                        fail["success"] = false;
                        fail["error"] = "recipe_not_ready";
                        fail["manifest_status"] = manifest["status"].AsString();
                        fail["cof_stable"] =
                            manifest.TryGetValue("cof_stable", out OSD stable) &&
                            stable.AsBoolean();
                        fail["bodypart_singletons_valid"] =
                            manifest.TryGetValue(
                                "bodypart_singletons_valid",
                                out OSD singletons) &&
                            singletons.AsBoolean();
                        WriteMap(response, HttpStatusCode.OK, fail);
                        return;
                    }

                    AuditSourceTextures(manifest);

                    if (manifest["status"].AsString() != "source_decode_ready")
                    {
                        SaveManifest(agentID, manifest);

                        OSDMap fail = new();
                        fail["success"] = false;
                        fail["error"] = "source_decode_not_ready";
                        fail["manifest_status"] = manifest["status"].AsString();
                        fail["source_audit_status"] =
                            manifest["source_audit_status"].AsString();
                        WriteMap(response, HttpStatusCode.OK, fail);
                        return;
                    }

                    if (TryReuseStoredBakes(previous, manifest))
                    {
                        SaveManifest(agentID, manifest);
                        UpdateStateFromManifest(agentID, manifest);
                        manifest["success"] = true;
                        manifest["bake_reused"] = true;
                        WriteMap(response, HttpStatusCode.OK, manifest);
                        return;
                    }

                    if (!TryCompositeAndStoreBakes(
                            agentID,
                            manifest,
                            out string bakeError))
                    {
                        manifest["status"] = "c2b2_bake_failed";
                        manifest["pixel_compositor_status"] = "failed";
                        manifest["bake_error"] = bakeError ?? string.Empty;
                        SaveManifest(agentID, manifest);
                        UpdateStateFromManifest(agentID, manifest);

                        OSDMap fail = new();
                        fail["success"] = false;
                        fail["error"] = bakeError ?? "c2b2_bake_failed";
                        fail["recipe_hash"] = manifest["recipe_hash"].AsString();
                        WriteMap(response, HttpStatusCode.OK, fail);
                        return;
                    }

                    SaveManifest(agentID, manifest);
                    UpdateStateFromManifest(agentID, manifest);

                    manifest["success"] = true;
                    manifest["bake_reused"] = false;
                    WriteMap(response, HttpStatusCode.OK, manifest);
                }
                finally
                {
                    s_bakeConcurrency.Release();
                }
            }
        }

        private void UpdateStateFromManifest(UUID agentID, OSDMap manifest)
        {
            // C4D: state writes remain serialized with Increment/state requests,
            // but this lock is held only for the tiny atomic state-file update.
            lock (GetAgentLock(agentID))
            {
                AppearanceState state = LoadState(agentID);
                state.CofVersion = manifest["cof_version"].AsInteger();
                state.RecipeCofVersion = manifest["cof_version"].AsInteger();
                state.RecipeHash = manifest["recipe_hash"].AsString();
                state.ManifestStatus = manifest["status"].AsString();
                state.UpdatedUtc = DateTime.UtcNow.ToString("O");
                SaveState(agentID, state);
            }
        }

        private bool TryReuseStoredBakes(OSDMap previous, OSDMap current)
        {
            if (previous == null)
                return false;

            if (!previous.TryGetValue("recipe_hash", out OSD previousHash) ||
                previousHash.AsString() != current["recipe_hash"].AsString())
            {
                return false;
            }

            if (!previous.TryGetValue("compositor_profile", out OSD previousProfile) ||
                previousProfile.AsString() != CompositorProfile)
            {
                return false;
            }

            if (!previous.TryGetValue(
                    "compositor_fingerprint",
                    out OSD previousFingerprint) ||
                previousFingerprint.AsString() != CompositorFingerprint)
            {
                return false;
            }

            if (!previous.TryGetValue("bakes", out OSD previousBakesOSD) ||
                previousBakesOSD is not OSDArray previousBakes ||
                previousBakes.Count != s_bakeDefinitions.Length)
            {
                return false;
            }

            foreach (OSD entry in previousBakes)
            {
                if (entry is not OSDMap bake ||
                    bake["status"].AsString() != "j2k_stored" ||
                    !UUID.TryParse(bake["asset_id"].AsString(), out UUID assetID) ||
                    assetID.IsZero())
                {
                    return false;
                }

                try
                {
                    if (m_assets.GetMetadata(assetID.ToString()) == null)
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            current["bakes"] = previousBakes;
            current["pixel_compositor_status"] =
                previous["pixel_compositor_status"].AsString();
            current["compositor_profile"] = CompositorProfile;
            current["compositor_fingerprint"] = CompositorFingerprint;
            current["compositor_semantics"] = CompositorSemantics;
            current["bake_asset_store_status"] = "asset_core_ready";
            current["bake_asset_authority"] = "Asset Core";
            current["bake_generated_utc"] =
                previous["bake_generated_utc"].AsString();
            current["central_bake_advertised"] = false;
            current["status"] = "c2b2_bakes_ready_not_advertised";
            return true;
        }

        private bool TryCompositeAndStoreBakes(
            UUID agentID,
            OSDMap manifest,
            out string error)
        {
            error = string.Empty;

            if (!TryBuildBakeLayerInputs(
                    manifest,
                    out Dictionary<string, List<BakeLayerInput>> layersByBake,
                    out Dictionary<int, float> visualParams,
                    out List<string> inputErrors))
            {
                error = inputErrors.Count > 0
                    ? string.Join(",", inputErrors)
                    : "bake_input_build_failed";
                return false;
            }

            if (!manifest.TryGetValue("bakes", out OSD bakesOSD) ||
                bakesOSD is not OSDArray bakes)
            {
                error = "manifest_bakes_missing";
                return false;
            }

            foreach (BakeDefinition bake in s_bakeDefinitions)
            {
                OSDMap bakeMap = FindBakeMap(bakes, bake.Name);
                if (bakeMap == null)
                {
                    error = "manifest_bake_slot_missing:" + bake.Name;
                    return false;
                }

                List<BakeLayerInput> layers =
                    layersByBake.TryGetValue(bake.Name, out List<BakeLayerInput> found)
                        ? found
                        : new List<BakeLayerInput>();

                if (!TryCompositeBake(
                        bake,
                        layers,
                        visualParams,
                        out byte[] encoded,
                        out int width,
                        out int height,
                        out string compositeError))
                {
                    bakeMap["status"] = "compositor_failed";
                    bakeMap["error"] = compositeError ?? string.Empty;
                    error = bake.Name + ":" + compositeError;
                    return false;
                }

                string j2kHash =
                    Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
                UUID assetID = DeterministicBakeAssetID(
                    manifest["recipe_hash"].AsString(),
                    bake.Name,
                    j2kHash);

                bool exists = false;
                try
                {
                    exists = m_assets.GetMetadata(assetID.ToString()) != null;
                }
                catch
                {
                    exists = false;
                }

                if (!exists)
                {
                    AssetBase asset = new(
                        assetID,
                        "Novalyth SSA " + bake.Name,
                        (sbyte)AssetType.Texture,
                        agentID.ToString())
                    {
                        Data = encoded,
                        Description = "Novalyth C2B2 " + bake.Name,
                        Local = false,
                        Temporary = false,
                        Flags = AssetFlags.Normal
                    };

                    string stored;
                    try
                    {
                        stored = m_assets.Store(asset);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[NOVALYTH APPEARANCE C2B2]: bake asset store failed {0}/{1}: {2}",
                            agentID,
                            bake.Name,
                            e.Message);
                        stored = string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(stored))
                    {
                        error = bake.Name + ":asset_store_failed";
                        bakeMap["status"] = "asset_store_failed";
                        return false;
                    }
                }

                bakeMap["status"] = "j2k_stored";
                bakeMap["asset_id"] = assetID;
                bakeMap["width"] = width;
                bakeMap["height"] = height;
                bakeMap["j2k_bytes"] = encoded.Length;
                bakeMap["j2k_sha256"] = j2kHash;
                bakeMap["compositor_profile"] = CompositorProfile;
                bakeMap["compositor_fingerprint"] = CompositorFingerprint;
                bakeMap["error"] = string.Empty;
            }

            manifest["pixel_compositor_status"] = "c2b2_ready";
            manifest["compositor_profile"] = CompositorProfile;
            manifest["compositor_fingerprint"] = CompositorFingerprint;
            manifest["compositor_semantics"] = CompositorSemantics;
            manifest["bake_asset_store_status"] = "asset_core_ready";
            manifest["bake_asset_authority"] = "Asset Core";
            manifest["bake_generated_utc"] = DateTime.UtcNow.ToString("O");
            manifest["central_bake_advertised"] = false;
            manifest["status"] = "c2b2_bakes_ready_not_advertised";

            return true;
        }

        private bool TryBuildBakeLayerInputs(
            OSDMap manifest,
            out Dictionary<string, List<BakeLayerInput>> layersByBake,
            out Dictionary<int, float> visualParams,
            out List<string> errors)
        {
            layersByBake = new Dictionary<string, List<BakeLayerInput>>(
                StringComparer.Ordinal);

            foreach (BakeDefinition bake in s_bakeDefinitions)
                layersByBake[bake.Name] = new List<BakeLayerInput>();

            errors = new List<string>();
            visualParams = new Dictionary<int, float>();

            if (!manifest.TryGetValue("wearables", out OSD wearablesOSD) ||
                wearablesOSD is not OSDArray wearables)
            {
                errors.Add("manifest_wearables_missing");
                return false;
            }

            Dictionary<UUID, AssetTexture> decodedTextures = new();

            foreach (OSD entry in wearables)
            {
                if (entry is not OSDMap wearableMap ||
                    !UUID.TryParse(
                        wearableMap["asset_id"].AsString(),
                        out UUID wearableAssetID) ||
                    wearableAssetID.IsZero())
                {
                    errors.Add("invalid_wearable_manifest_entry");
                    continue;
                }

                int wearableType = wearableMap["wearable_type"].AsInteger();
                string orderKey = wearableMap["order"].AsString();

                AssetBase rawWearable;
                try
                {
                    rawWearable = m_assets.Get(wearableAssetID.ToString());
                }
                catch
                {
                    rawWearable = null;
                }

                if (rawWearable?.Data == null)
                {
                    errors.Add(wearableAssetID + ":wearable_asset_missing");
                    continue;
                }

                AssetWearable wearableAsset;

                switch ((AssetType)rawWearable.Type)
                {
                    case AssetType.Bodypart:
                        wearableAsset =
                            new AssetBodypart(
                                wearableAssetID,
                                rawWearable.Data);
                        break;

                    case AssetType.Clothing:
                        wearableAsset =
                            new AssetClothing(
                                wearableAssetID,
                                rawWearable.Data);
                        break;

                    default:
                        errors.Add(
                            wearableAssetID +
                            ":unsupported_wearable_asset_type:" +
                            rawWearable.Type);
                        continue;
                }

                if (!wearableAsset.Decode())
                {
                    errors.Add(wearableAssetID + ":wearable_decode_failed");
                    continue;
                }

                foreach (KeyValuePair<int, float> parameter in wearableAsset.Params)
                {
                    if (!visualParams.ContainsKey(parameter.Key))
                        visualParams[parameter.Key] = parameter.Value;
                }

                AppearanceManager.TextureData[] temp =
                    new AppearanceManager.TextureData[
                        (int)AvatarTextureIndex.NumberOfEntries];

                for (int i = 0; i < temp.Length; i++)
                {
                    temp[i].TextureIndex = (AvatarTextureIndex)i;
                    temp[i].Color = Color4.White;
                }

                AppearanceManager.WearableData wearable =
                    new()
                    {
                        ItemID = UUID.Zero,
                        AssetID = wearableAssetID,
                        WearableType = (WearableType)wearableType,
                        AssetType = (AssetType)rawWearable.Type,
                        Asset = wearableAsset
                    };

                try
                {
                    AppearanceManager.DecodeWearableParams(wearable, ref temp);
                }
                catch (Exception e)
                {
                    errors.Add(
                        wearableAssetID + ":visual_param_decode:" +
                        e.GetType().Name);
                    continue;
                }

                foreach (KeyValuePair<AvatarTextureIndex, UUID> textureEntry
                             in wearableAsset.Textures)
                {
                    int textureIndex = (int)textureEntry.Key;
                    UUID textureID = textureEntry.Value;

                    if (!s_sourceTextureBakeSlots.TryGetValue(
                            textureIndex,
                            out string bakeSlot) ||
                        textureID.IsZero() ||
                        textureID == AppearanceManager.DEFAULT_AVATAR_TEXTURE)
                    {
                        continue;
                    }

                    if (!decodedTextures.TryGetValue(
                            textureID,
                            out AssetTexture textureAsset))
                    {
                        AssetBase rawTexture;
                        try
                        {
                            rawTexture = m_assets.Get(textureID.ToString());
                        }
                        catch
                        {
                            rawTexture = null;
                        }

                        if (rawTexture?.Data == null)
                        {
                            errors.Add(textureID + ":source_asset_missing");
                            continue;
                        }

                        textureAsset = new AssetTexture(textureID, rawTexture.Data);
                        if (!textureAsset.Decode() || textureAsset.Image == null)
                        {
                            errors.Add(textureID + ":source_j2k_decode_failed");
                            continue;
                        }

                        decodedTextures[textureID] = textureAsset;
                    }

                    AppearanceManager.TextureData textureData = temp[textureIndex];
                    textureData.TextureIndex = textureEntry.Key;
                    textureData.TextureID = textureID;
                    textureData.Texture = textureAsset;
                    textureData.Color = ResolveLayerColor(
                        wearableAsset,
                        wearableType,
                        textureIndex,
                        textureData.Color);

                    layersByBake[bakeSlot].Add(
                        new BakeLayerInput
                        {
                            BakeSlot = bakeSlot,
                            TextureIndex = textureIndex,
                            LayerOrder = GetLayerOrder(textureIndex),
                            TextureID = textureID,
                            WearableType = wearableType,
                            WearableAssetID = wearableAssetID,
                            OrderKey = orderKey ?? string.Empty,
                            TextureData = textureData
                        });
                }
            }

            foreach (List<BakeLayerInput> layers in layersByBake.Values)
                layers.Sort(BakeLayerInput.Compare);

            return errors.Count == 0;
        }

        // C4D4 NOTE:
        // DecodeWearableParams supplies the normal wearable tint/masks. Keep
        // this per-texture RGB override for modern Tattoo/Universal channels,
        // where one wearable can carry different colors for different local
        // texture indices. This modifies only the source texture, never the
        // whole bake canvas.
        private static Color4 ResolveLayerColor(
            AssetWearable wearable,
            int wearableType,
            int textureIndex,
            Color4 fallback)
        {
            if (!s_textureRgbParams.TryGetValue(
                    textureIndex,
                    out int[] ids) ||
                ids.Length == 0)
            {
                return fallback;
            }

            List<AppearanceManager.ColorParamInfo> colorParams = new();

            foreach (int id in ids)
            {
                if (!wearable.Params.TryGetValue(id, out float weight) ||
                    !VisualParams.Params.ContainsKey(id))
                {
                    continue;
                }

                VisualParam visualParam = VisualParams.Params[id];
                if (!visualParam.ColorParams.HasValue)
                    continue;

                colorParams.Add(
                    new AppearanceManager.ColorParamInfo
                    {
                        VisualParam = visualParam,
                        VisualColorParam = visualParam.ColorParams.Value,
                        Value = weight,
                        WearableType = (WearableType)wearableType
                    });
            }

            // libOpenMetaverse already implements the viewer's param_color ramp
            // interpolation and Add/Multiply/Blend operations. Never treat raw
            // slider weights as literal R/G/B.
            return colorParams.Count > 0
                ? AppearanceManager.GetColorFromParams(colorParams)
                : fallback;
        }

        private bool TryCompositeBake(
            BakeDefinition bake,
            List<BakeLayerInput> layers,
            Dictionary<int, float> visualParams,
            out byte[] encoded,
            out int width,
            out int height,
            out string error)
        {
            encoded = Array.Empty<byte>();
            error = string.Empty;

            AvatarLadProfile profile = s_avatarLadProfile.Value;
            if (!profile.Sets.TryGetValue(bake.Name, out AvatarLadSet set))
            {
                width = 0;
                height = 0;
                error = "avatar_lad_layer_set_missing:" + bake.Name;
                return false;
            }

            width = set.Width;
            height = set.Height;

            if (width <= 0 || height <= 0)
            {
                error = "avatar_lad_invalid_size:" + bake.Name;
                return false;
            }

            ManagedImage baked = new(
                width,
                height,
                ManagedImage.ImageChannels.Color |
                ManagedImage.ImageChannels.Alpha |
                ManagedImage.ImageChannels.Bump);

            FillDithered(baked, Color4.White);

            Dictionary<int, List<BakeLayerInput>> byIndex = new();
            foreach (BakeLayerInput input in layers)
            {
                if (!byIndex.TryGetValue(
                        input.TextureIndex,
                        out List<BakeLayerInput> list))
                {
                    list = new List<BakeLayerInput>();
                    byIndex[input.TextureIndex] = list;
                }

                list.Add(input);
            }

            HashSet<BakeLayerInput> consumed = new();
            List<ManagedImage> finalAlphaMasks = new();
            List<BakeLayerInput> deferredHeadSkin = new();
            List<BakeLayerInput> deferredHeadTattoos = new();

            foreach (AvatarLadLayer layer in set.Layers)
            {
                if (string.Equals(
                        layer.RenderPass,
                        "bump",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!ApplyAvatarLadBumpLayer(
                            baked,
                            layer,
                            byIndex,
                            consumed,
                            bake.Name,
                            visualParams,
                            width,
                            height,
                            out string bumpError))
                    {
                        error = bumpError;
                        return false;
                    }

                    continue;
                }

                Color4 layerColor =
                    EvaluateAvatarLadLayerColor(
                        layer,
                        visualParams);

                // LLTexLayer renders local texture first, static image second.
                foreach (AvatarLadTexture textureDef in layer.Textures)
                {
                    if (textureDef.TextureIndex < 0)
                        continue;

                    if (!byIndex.TryGetValue(
                            textureDef.TextureIndex,
                            out List<BakeLayerInput> sourceLayers))
                    {
                        continue;
                    }

                    foreach (BakeLayerInput sourceLayer in sourceLayers)
                    {
                        if (bake.Name == "head" &&
                            sourceLayer.TextureIndex == 0)
                        {
                            consumed.Add(sourceLayer);
                            if (!deferredHeadSkin.Contains(sourceLayer))
                                deferredHeadSkin.Add(sourceLayer);
                            continue;
                        }

                        if (bake.Name == "head" &&
                            (sourceLayer.TextureIndex == 26 ||
                             sourceLayer.TextureIndex == 29))
                        {
                            consumed.Add(sourceLayer);
                            if (!deferredHeadTattoos.Contains(sourceLayer))
                                deferredHeadTattoos.Add(sourceLayer);
                            continue;
                        }

                        ManagedImage source =
                            PrepareSourceLayer(
                                sourceLayer,
                                bake.Name,
                                width,
                                height,
                                out string sourceError);

                        if (source == null)
                        {
                            if (!string.IsNullOrEmpty(sourceError))
                            {
                                error = sourceError;
                                return false;
                            }

                            continue;
                        }

                        consumed.Add(sourceLayer);

                        if (layer.VisibilityMask ||
                            IsVisibilityTextureIndex(sourceLayer.TextureIndex))
                        {
                            finalAlphaMasks.Add(source);
                            continue;
                        }

                        if (textureDef.LocalTextureAlphaOnly)
                        {
                            ManagedImage colorOnly =
                                CreateSolidLayer(
                                    width,
                                    height,
                                    layerColor);

                            ApplyMaskFromImage(
                                colorOnly,
                                source);

                            source = colorOnly;
                        }

                        if (layer.HasMorphMask &&
                            source.Alpha != null)
                        {
                            CopyMorphAlpha(
                                baked,
                                source);
                        }

                        if (layer.WriteAllChannels)
                            ReplaceLayer(baked, source);
                        else
                            DrawLayer(baked, source, false);
                    }
                }

                ManagedImage staticLayer =
                    BuildAvatarLadStaticLayer(
                        layer,
                        visualParams,
                        width,
                        height);

                if (staticLayer != null)
                {
                    if (layer.VisibilityMask)
                    {
                        finalAlphaMasks.Add(staticLayer);
                    }
                    else if (layer.WriteAllChannels)
                    {
                        ReplaceLayer(baked, staticLayer);
                    }
                    else
                    {
                        DrawLayer(baked, staticLayer, false);
                    }
                }
            }

            // Preserve modern/future local entries not named by the pinned profile.
            foreach (BakeLayerInput layer in layers)
            {
                if (consumed.Contains(layer) ||
                    IsVisibilityTextureIndex(layer.TextureIndex))
                {
                    continue;
                }

                if (bake.Name == "head" &&
                    layer.TextureIndex == 0)
                {
                    consumed.Add(layer);
                    if (!deferredHeadSkin.Contains(layer))
                        deferredHeadSkin.Add(layer);
                    continue;
                }

                if (bake.Name == "head" &&
                    (layer.TextureIndex == 26 ||
                     layer.TextureIndex == 29))
                {
                    consumed.Add(layer);
                    if (!deferredHeadTattoos.Contains(layer))
                        deferredHeadTattoos.Add(layer);
                    continue;
                }

                ManagedImage source =
                    PrepareSourceLayer(
                        layer,
                        bake.Name,
                        width,
                        height,
                        out string sourceError);

                if (source == null)
                {
                    if (!string.IsNullOrEmpty(sourceError))
                    {
                        error = sourceError;
                        return false;
                    }

                    continue;
                }

                consumed.Add(layer);
                DrawLayer(baked, source, false);
            }

            foreach (BakeLayerInput layer in layers)
            {
                if (!IsVisibilityTextureIndex(layer.TextureIndex) ||
                    consumed.Contains(layer))
                {
                    continue;
                }

                ManagedImage source =
                    PrepareSourceLayer(
                        layer,
                        bake.Name,
                        width,
                        height,
                        out string sourceError);

                if (source == null)
                {
                    if (!string.IsNullOrEmpty(sourceError))
                    {
                        error = sourceError;
                        return false;
                    }

                    continue;
                }

                consumed.Add(layer);
                finalAlphaMasks.Add(source);
            }

            if (bake.Name == "head")
            {
                foreach (BakeLayerInput skin in deferredHeadSkin)
                {
                    ManagedImage source =
                        PrepareSourceLayer(
                            skin,
                            bake.Name,
                            width,
                            height,
                            out string sourceError);

                    if (source == null)
                    {
                        if (!string.IsNullOrEmpty(sourceError))
                        {
                            error = sourceError;
                            return false;
                        }

                        continue;
                    }

                    DrawLayer(baked, source, false);
                }

                deferredHeadTattoos.Sort(BakeLayerInput.Compare);

                foreach (BakeLayerInput tattoo in deferredHeadTattoos)
                {
                    ManagedImage source =
                        PrepareSourceLayer(
                            tattoo,
                            bake.Name,
                            width,
                            height,
                            out string sourceError);

                    if (source == null)
                    {
                        if (!string.IsNullOrEmpty(sourceError))
                        {
                            error = sourceError;
                            return false;
                        }

                        continue;
                    }

                    DrawLayer(baked, source, false);
                }
            }

            foreach (ManagedImage mask in finalAlphaMasks)
                AddAlpha(baked, mask);

            try
            {
                encoded = OpenJPEG.Encode(baked, false);
            }
            catch (Exception e)
            {
                error = "j2k_encode_exception:" + e.GetType().Name;
                return false;
            }

            if (encoded == null || encoded.Length == 0)
            {
                error = "j2k_encode_empty";
                return false;
            }

            return true;
        }

        private static ManagedImage PrepareSourceLayer(
            BakeLayerInput layer,
            string bakeSlot,
            int width,
            int height,
            out string error)
        {
            error = string.Empty;

            if (layer.TextureData.Texture?.Image == null)
                return null;

            ManagedImage texture = layer.TextureData.Texture.Image.Clone();

            if (texture.Width != width || texture.Height != height)
            {
                try
                {
                    texture.ResizeNearestNeighbor(width, height);
                }
                catch (Exception e)
                {
                    error =
                        "resize_failed:" + layer.TextureID + ":" +
                        e.GetType().Name;
                    return null;
                }
            }

            bool bodypaint =
                IsBodypaintTextureIndex(layer.TextureIndex);

            bool visibility =
                IsVisibilityTextureIndex(layer.TextureIndex);

            // A real skin bodypaint texture overrides skin tint and skin masks.
            if (!bodypaint && !visibility)
            {
                ApplyTint(
                    texture,
                    layer.TextureData.Color);

                ApplyParamMasks(
                    texture,
                    layer.TextureData.AlphaMasks,
                    bakeSlot);
            }

            return texture;
        }

        private static ManagedImage BuildAvatarLadStaticLayer(
            AvatarLadLayer layer,
            Dictionary<int, float> visualParams,
            int width,
            int height)
        {
            // NOVALYTH APPEARANCE C4D4
            //
            // avatar_lad color/alpha parameters on a layer containing a
            // local_texture describe how that LOCAL texture is rendered.
            // They are not an additional full-canvas solid-color layer.
            //
            // The old implementation treated any color/alpha parameter as a
            // reason to create a solid width*height image when no static TGA
            // existed. For layers such as upper_clothes, skirt, hair base and
            // iris this painted the entire bake with the wearable tint before
            // drawing the actual local texture. That is the source of the
            // blue/gray body and dress pollution seen in C4D3 testing.
            //
            // Local texture tint + alpha is already carried by
            // AppearanceManager.TextureData and applied in PrepareSourceLayer.
            bool hasStaticTexture =
                layer.Textures.Any(
                    x => x.TextureIndex < 0 &&
                         !string.IsNullOrWhiteSpace(x.TgaFile));

            bool hasLocalTexture =
                layer.Textures.Any(
                    x => x.TextureIndex >= 0);

            bool hasAnyTexture =
                layer.Textures.Count > 0;

            bool hasColorOrAlpha =
                layer.FixedColor != null ||
                !string.IsNullOrWhiteSpace(layer.GlobalColor) ||
                layer.Params.Any(
                    x => x.Color != null ||
                         x.Alpha != null);

            // Only build an independent static layer when:
            //   1) avatar_lad names a real static TGA resource, or
            //   2) the layer contains NO texture at all and is intentionally
            //      a pure color/alpha layer.
            //
            // A local_texture-only layer must return null here; its actual
            // source pixels are processed later by PrepareSourceLayer.
            bool isIntentionalColorOnlyLayer =
                !hasAnyTexture &&
                hasColorOrAlpha;

            if (!hasStaticTexture &&
                !isIntentionalColorOnlyLayer)
            {
                return null;
            }

            Color4 color =
                EvaluateAvatarLadLayerColor(
                    layer,
                    visualParams);

            ManagedImage output = null;

            foreach (AvatarLadTexture textureDef in layer.Textures)
            {
                if (textureDef.TextureIndex >= 0 ||
                    string.IsNullOrWhiteSpace(textureDef.TgaFile))
                {
                    continue;
                }

                ManagedImage resource =
                    string.Equals(
                        textureDef.TgaFile,
                        "aux_base.tga",
                        StringComparison.OrdinalIgnoreCase)
                        ? CreateSolidLayer(
                            width,
                            height,
                            new Color4(
                                128f / 255f,
                                128f / 255f,
                                128f / 255f,
                                1f))
                        : LoadBakeResource(textureDef.TgaFile);

                if (resource == null)
                    continue;

                if (resource.Width != width ||
                    resource.Height != height)
                {
                    try
                    {
                        resource.ResizeNearestNeighbor(
                            width,
                            height);
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (textureDef.FileIsMask)
                {
                    ManagedImage colored =
                        CreateSolidLayer(
                            width,
                            height,
                            color);
                    ApplyMaskFromImage(
                        colored,
                        resource);
                    resource = colored;
                }
                else
                {
                    ApplyTint(resource, color);
                }

                if (output == null)
                    output = resource;
                else
                    DrawLayer(output, resource, false);
            }

            if (output == null)
            {
                // Reaching this point means an intentional color-only layer:
                // no static TGA and no local texture exists.
                output = CreateSolidLayer(
                    width,
                    height,
                    color);
            }

            ApplyAvatarLadParamAlpha(
                output,
                layer.Params,
                visualParams);

            return output;
        }

        private static bool ApplyAvatarLadBumpLayer(
            ManagedImage baked,
            AvatarLadLayer layer,
            Dictionary<int, List<BakeLayerInput>> byIndex,
            HashSet<BakeLayerInput> consumed,
            string bakeSlot,
            Dictionary<int, float> visualParams,
            int width,
            int height,
            out string error)
        {
            error = string.Empty;

            ManagedImage contribution =
                BuildAvatarLadStaticLayer(
                    layer,
                    visualParams,
                    width,
                    height);

            Color4 layerColor =
                EvaluateAvatarLadLayerColor(
                    layer,
                    visualParams);

            foreach (AvatarLadTexture textureDef in layer.Textures)
            {
                if (textureDef.TextureIndex < 0 ||
                    !byIndex.TryGetValue(
                        textureDef.TextureIndex,
                        out List<BakeLayerInput> sourceLayers))
                {
                    continue;
                }

                foreach (BakeLayerInput sourceLayer in sourceLayers)
                {
                    ManagedImage source =
                        PrepareSourceLayer(
                            sourceLayer,
                            bakeSlot,
                            width,
                            height,
                            out string sourceError);

                    if (source == null)
                    {
                        if (!string.IsNullOrEmpty(sourceError))
                        {
                            error = sourceError;
                            return false;
                        }

                        continue;
                    }

                    consumed.Add(sourceLayer);

                    ManagedImage bumpSource = source;

                    if (textureDef.LocalTextureAlphaOnly)
                    {
                        bumpSource =
                            CreateSolidLayer(
                                width,
                                height,
                                layerColor);

                        ApplyMaskFromImage(
                            bumpSource,
                            source);
                    }

                    if (contribution == null)
                        contribution = bumpSource;
                    else
                        DrawLayer(contribution, bumpSource, false);

                    if (layer.HasMorphMask &&
                        source.Alpha != null)
                    {
                        CopyMorphAlpha(
                            baked,
                            source);
                    }
                }
            }

            if (contribution != null)
                ApplyBumpContribution(baked, contribution);

            return true;
        }

        private static void ApplyBumpContribution(
            ManagedImage baked,
            ManagedImage source)
        {
            if (baked?.Bump == null || source == null)
                return;

            if (source.Width != baked.Width ||
                source.Height != baked.Height)
            {
                try
                {
                    source.ResizeNearestNeighbor(
                        baked.Width,
                        baked.Height);
                }
                catch
                {
                    return;
                }
            }

            byte[] values =
                source.Bump ??
                source.Red;

            if (values == null)
                return;

            byte[] alpha = source.Alpha;

            for (int i = 0; i < baked.Bump.Length; i++)
            {
                int a =
                    alpha != null
                        ? alpha[i]
                        : 255;

                int inv = 255 - a;

                baked.Bump[i] =
                    (byte)((baked.Bump[i] * inv +
                            values[i] * a) >> 8);
            }
        }

        private static void CopyMorphAlpha(
            ManagedImage baked,
            ManagedImage source)
        {
            if (baked?.Bump == null ||
                source?.Alpha == null)
            {
                return;
            }

            if (source.Width != baked.Width ||
                source.Height != baked.Height)
            {
                try
                {
                    source.ResizeNearestNeighbor(
                        baked.Width,
                        baked.Height);
                }
                catch
                {
                    return;
                }
            }

            Buffer.BlockCopy(
                source.Alpha,
                0,
                baked.Bump,
                0,
                Math.Min(
                    source.Alpha.Length,
                    baked.Bump.Length));
        }

        private static ManagedImage CreateSolidLayer(
            int width,
            int height,
            Color4 color)
        {
            ManagedImage image = new(
                width,
                height,
                ManagedImage.ImageChannels.Color |
                ManagedImage.ImageChannels.Alpha);

            byte r = Utils.FloatZeroOneToByte(color.R);
            byte g = Utils.FloatZeroOneToByte(color.G);
            byte b = Utils.FloatZeroOneToByte(color.B);
            byte a = Utils.FloatZeroOneToByte(color.A);

            Array.Fill(image.Red, r);
            Array.Fill(image.Green, g);
            Array.Fill(image.Blue, b);
            Array.Fill(image.Alpha, a);

            return image;
        }

        private static void ApplyMaskFromImage(
            ManagedImage dest,
            ManagedImage mask)
        {
            if (dest == null || mask == null)
                return;

            if ((dest.Channels &
                 ManagedImage.ImageChannels.Alpha) == 0)
            {
                dest.ConvertChannels(
                    dest.Channels |
                    ManagedImage.ImageChannels.Alpha);
            }

            if (dest.Width != mask.Width ||
                dest.Height != mask.Height)
            {
                try
                {
                    mask.ResizeNearestNeighbor(
                        dest.Width,
                        dest.Height);
                }
                catch
                {
                    return;
                }
            }

            for (int i = 0; i < dest.Alpha.Length; i++)
            {
                byte m =
                    mask.Alpha != null
                        ? mask.Alpha[i]
                        : mask.Red != null
                            ? mask.Red[i]
                            : byte.MaxValue;

                dest.Alpha[i] =
                    (byte)((dest.Alpha[i] * m) >> 8);
            }
        }

        private static void ApplyAvatarLadParamAlpha(
            ManagedImage image,
            List<AvatarLadParam> parameters,
            Dictionary<int, float> visualParams)
        {
            if (image == null ||
                parameters == null)
            {
                return;
            }

            List<AvatarLadParam> alphaParams =
                parameters
                    .Where(
                        x => x.Alpha != null &&
                             !string.IsNullOrWhiteSpace(
                                 x.Alpha.TgaFile))
                    .ToList();

            if (alphaParams.Count == 0)
                return;

            ManagedImage combined = new(
                image.Width,
                image.Height,
                ManagedImage.ImageChannels.Alpha);

            int normalCount = 0;

            foreach (AvatarLadParam parameter in alphaParams)
            {
                if (parameter.Alpha.MultiplyBlend ||
                    !ApplyAvatarLadAlphaMask(
                        combined,
                        parameter,
                        visualParams,
                        false))
                {
                    continue;
                }

                normalCount++;
            }

            if (normalCount == 0 &&
                combined.Alpha != null)
            {
                Array.Fill(
                    combined.Alpha,
                    byte.MaxValue);
            }

            foreach (AvatarLadParam parameter in alphaParams)
            {
                if (!parameter.Alpha.MultiplyBlend)
                    continue;

                ApplyAvatarLadAlphaMask(
                    combined,
                    parameter,
                    visualParams,
                    true);
            }

            AddAlpha(image, combined);
        }

        private static bool ApplyAvatarLadAlphaMask(
            ManagedImage dest,
            AvatarLadParam parameter,
            Dictionary<int, float> visualParams,
            bool multiply)
        {
            AvatarLadAlpha alpha = parameter.Alpha;
            if (dest?.Alpha == null ||
                alpha == null ||
                string.IsNullOrWhiteSpace(alpha.TgaFile))
            {
                return false;
            }

            float value =
                visualParams.TryGetValue(
                    parameter.Id,
                    out float found)
                    ? found
                    : parameter.DefaultValue;

            if (alpha.SkipIfZero &&
                Math.Abs(value) < 0.00001f)
            {
                return false;
            }

            ManagedImage src =
                LoadBakeResource(alpha.TgaFile);

            if (src == null)
                return false;

            if (dest.Width != src.Width ||
                dest.Height != src.Height)
            {
                try
                {
                    src.ResizeNearestNeighbor(
                        dest.Width,
                        dest.Height);
                }
                catch
                {
                    return false;
                }
            }

            float normalized =
                NormalizeAvatarLadValue(
                    value,
                    parameter.MinValue,
                    parameter.MaxValue);

            byte threshold =
                (byte)((1f - normalized) * 255f);

            for (int i = 0; i < dest.Alpha.Length; i++)
            {
                byte source =
                    src.Alpha != null
                        ? src.Alpha[i]
                        : src.Red != null
                            ? src.Red[i]
                            : byte.MaxValue;

                byte a =
                    source <= threshold
                        ? (byte)0
                        : byte.MaxValue;

                if (multiply)
                {
                    dest.Alpha[i] =
                        (byte)((dest.Alpha[i] * a) >> 8);
                }
                else if (a > dest.Alpha[i])
                {
                    dest.Alpha[i] = a;
                }
            }

            return true;
        }

        private static Color4 EvaluateAvatarLadLayerColor(
            AvatarLadLayer layer,
            Dictionary<int, float> visualParams)
        {
            bool hasLayerColorParams =
                layer.Params.Any(
                    x => x.Color?.Values != null &&
                         x.Color.Values.Count > 0);

            if (hasLayerColorParams)
            {
                Color4 initial;

                if (!string.IsNullOrWhiteSpace(layer.GlobalColor))
                {
                    initial =
                        EvaluateAvatarLadGlobalColor(
                            layer.GlobalColor,
                            visualParams);
                }
                else if (layer.FixedColor != null &&
                         layer.FixedColor.Length >= 4 &&
                         layer.FixedColor[3] > 0)
                {
                    initial =
                        AvatarLadColor(
                            layer.FixedColor);
                }
                else
                {
                    initial =
                        new Color4(
                            0f,
                            0f,
                            0f,
                            0f);
                }

                return EvaluateAvatarLadParams(
                    layer.Params,
                    visualParams,
                    initial);
            }

            if (!string.IsNullOrWhiteSpace(layer.GlobalColor))
            {
                return EvaluateAvatarLadGlobalColor(
                    layer.GlobalColor,
                    visualParams);
            }

            if (layer.FixedColor != null &&
                layer.FixedColor.Length >= 4 &&
                layer.FixedColor[3] > 0)
            {
                return AvatarLadColor(
                    layer.FixedColor);
            }

            return Color4.White;
        }

        private static Color4 EvaluateAvatarLadGlobalColor(
            string name,
            Dictionary<int, float> visualParams)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !s_avatarLadProfile.Value.GlobalColors.TryGetValue(
                    name,
                    out List<AvatarLadParam> globalParams))
            {
                return Color4.White;
            }

            return EvaluateAvatarLadParams(
                globalParams,
                visualParams,
                new Color4(
                    0f,
                    0f,
                    0f,
                    0f));
        }

        private static Color4 EvaluateAvatarLadParams(
            List<AvatarLadParam> parameters,
            Dictionary<int, float> visualParams,
            Color4 initial)
        {
            Color4 result = initial;

            if (parameters == null)
                return result;

            foreach (AvatarLadParam parameter in parameters)
            {
                AvatarLadColorParam cp = parameter.Color;
                if (cp?.Values == null ||
                    cp.Values.Count == 0)
                {
                    continue;
                }

                float value =
                    visualParams.TryGetValue(
                        parameter.Id,
                        out float found)
                        ? found
                        : parameter.DefaultValue;

                float normalized =
                    NormalizeAvatarLadValue(
                        value,
                        parameter.MinValue,
                        parameter.MaxValue);

                Color4 sample =
                    SampleAvatarLadRamp(
                        cp.Values,
                        normalized);

                if (string.Equals(
                        cp.Operation,
                        "multiply",
                        StringComparison.OrdinalIgnoreCase))
                {
                    result = new Color4(
                        result.R * sample.R,
                        result.G * sample.G,
                        result.B * sample.B,
                        result.A * sample.A);
                }
                else if (string.Equals(
                             cp.Operation,
                             "blend",
                             StringComparison.OrdinalIgnoreCase))
                {
                    float t =
                        Math.Clamp(
                            value,
                            0f,
                            1f);

                    result = new Color4(
                        result.R + (sample.R - result.R) * t,
                        result.G + (sample.G - result.G) * t,
                        result.B + (sample.B - result.B) * t,
                        result.A + (sample.A - result.A) * t);
                }
                else
                {
                    result = new Color4(
                        result.R + sample.R,
                        result.G + sample.G,
                        result.B + sample.B,
                        result.A + sample.A);
                }

                result = new Color4(
                    Math.Clamp(result.R, 0f, 1f),
                    Math.Clamp(result.G, 0f, 1f),
                    Math.Clamp(result.B, 0f, 1f),
                    Math.Clamp(result.A, 0f, 1f));
            }

            return result;
        }

        private static float NormalizeAvatarLadValue(
            float value,
            float min,
            float max)
        {
            if (max <= min)
                return 0f;

            return Math.Clamp(
                (value - min) / (max - min),
                0f,
                1f);
        }

        private static Color4 SampleAvatarLadRamp(
            List<int[]> values,
            float normalized)
        {
            if (values == null ||
                values.Count == 0)
            {
                return Color4.White;
            }

            if (values.Count == 1)
                return AvatarLadColor(values[0]);

            float pos =
                Math.Clamp(normalized, 0f, 1f) *
                (values.Count - 1);

            int lo = (int)Math.Floor(pos);
            int hi =
                Math.Min(
                    lo + 1,
                    values.Count - 1);

            float t = pos - lo;

            Color4 a =
                AvatarLadColor(values[lo]);
            Color4 b =
                AvatarLadColor(values[hi]);

            return new Color4(
                a.R + (b.R - a.R) * t,
                a.G + (b.G - a.G) * t,
                a.B + (b.B - a.B) * t,
                a.A + (b.A - a.A) * t);
        }

        private static Color4 AvatarLadColor(
            int[] rgba)
        {
            if (rgba == null ||
                rgba.Length < 4)
            {
                return Color4.White;
            }

            return new Color4(
                rgba[0] / 255f,
                rgba[1] / 255f,
                rgba[2] / 255f,
                rgba[3] / 255f);
        }

        private static AvatarLadProfile LoadAvatarLadProfile()
        {
            if (!File.Exists(s_avatarLadPath))
            {
                throw new FileNotFoundException(
                    "Novalyth pinned avatar_lad profile missing",
                    s_avatarLadPath);
            }

            XDocument document =
                XDocument.Load(
                    s_avatarLadPath,
                    LoadOptions.None);

            XElement root =
                document.Root ??
                throw new Exception(
                    "Novalyth avatar_lad root missing");

            AvatarLadProfile profile = new();

            foreach (XElement global in
                     root.Elements("global_color"))
            {
                string name =
                    (string)global.Attribute("name") ??
                    string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                List<AvatarLadParam> parameters =
                    global.Elements("param")
                        .Select(ParseAvatarLadParam)
                        .ToList();

                profile.GlobalColors[name] = parameters;
            }

            foreach (XElement layerSet in
                     root.Elements("layer_set"))
            {
                string rawRegion =
                    (string)layerSet.Attribute("body_region") ??
                    string.Empty;

                string region =
                    NormalizeAvatarLadRegion(rawRegion);

                if (string.IsNullOrEmpty(region))
                    continue;

                AvatarLadSet set = new()
                {
                    Width = ParseAvatarLadInt(
                        layerSet.Attribute("width"),
                        region == "eyes" ? 512 : 2048),
                    Height = ParseAvatarLadInt(
                        layerSet.Attribute("height"),
                        region == "eyes" ? 512 : 2048),
                    ClearAlpha = ParseAvatarLadBool(
                        layerSet.Attribute("clear_alpha"),
                        true)
                };

                int order = 0;

                foreach (XElement layerElement in
                         layerSet.Elements("layer"))
                {
                    AvatarLadLayer layer = new()
                    {
                        Order = order++,
                        Name =
                            (string)layerElement.Attribute("name") ??
                            string.Empty,
                        FixedColor =
                            ParseAvatarLadColor(
                                (string)layerElement.Attribute(
                                    "fixed_color")),
                        GlobalColor =
                            (string)layerElement.Attribute(
                                "global_color") ??
                            string.Empty,
                        VisibilityMask =
                            ParseAvatarLadBool(
                                layerElement.Attribute(
                                    "visibility_mask"),
                                false),
                        RenderPass =
                            ((string)layerElement.Attribute(
                                "render_pass") ??
                             string.Empty).ToLowerInvariant(),
                        WriteAllChannels =
                            ParseAvatarLadBool(
                                layerElement.Attribute(
                                    "write_all_channels"),
                                false),
                        HasMorphMask =
                            layerElement.Elements(
                                "morph_mask").Any()
                    };

                    foreach (XElement textureElement in
                             layerElement.Elements("texture"))
                    {
                        string local =
                            (string)textureElement.Attribute(
                                "local_texture") ??
                            string.Empty;

                        int textureIndex = -1;

                        if (!string.IsNullOrWhiteSpace(local))
                        {
                            if (!s_avatarLadLocalTextureIndices.TryGetValue(
                                    local,
                                    out textureIndex))
                            {
                                throw new Exception(
                                    "Unknown avatar_lad local_texture: " +
                                    local);
                            }
                        }

                        layer.Textures.Add(
                            new AvatarLadTexture
                            {
                                TextureIndex = textureIndex,
                                TgaFile =
                                    (string)textureElement.Attribute(
                                        "tga_file") ??
                                    string.Empty,
                                FileIsMask =
                                    ParseAvatarLadBool(
                                        textureElement.Attribute(
                                            "file_is_mask"),
                                        false),
                                LocalTextureAlphaOnly =
                                    ParseAvatarLadBool(
                                        textureElement.Attribute(
                                            "local_texture_alpha_only"),
                                        false)
                            });
                    }

                    foreach (XElement parameterElement in
                             layerElement.Elements("param"))
                    {
                        layer.Params.Add(
                            ParseAvatarLadParam(
                                parameterElement));
                    }

                    set.Layers.Add(layer);
                }

                profile.Sets[region] = set;
            }

            string[] required =
            {
                "head", "upper", "lower", "eyes",
                "skirt", "hair", "leftarm",
                "leftleg", "aux1", "aux2", "aux3"
            };

            foreach (string slot in required)
            {
                if (!profile.Sets.ContainsKey(slot))
                {
                    throw new Exception(
                        "Missing avatar_lad layer_set: " +
                        slot);
                }
            }

            return profile;
        }

        private static string NormalizeAvatarLadRegion(
            string region)
        {
            return region switch
            {
                "upper_body" => "upper",
                "upper" => "upper",
                "lower_body" => "lower",
                "lower" => "lower",
                "head" => "head",
                "eyes" => "eyes",
                "skirt" => "skirt",
                "hair" => "hair",
                "leftarm" => "leftarm",
                "left_arm" => "leftarm",
                "leftleg" => "leftleg",
                "left_leg" => "leftleg",
                "aux1" => "aux1",
                "aux2" => "aux2",
                "aux3" => "aux3",
                _ => string.Empty
            };
        }

        private static AvatarLadParam ParseAvatarLadParam(
            XElement element)
        {
            AvatarLadParam parameter = new()
            {
                Id = ParseAvatarLadInt(
                    element.Attribute("id"),
                    -1),
                MinValue = ParseAvatarLadFloat(
                    element.Attribute("value_min"),
                    0f),
                MaxValue = ParseAvatarLadFloat(
                    element.Attribute("value_max"),
                    1f),
                DefaultValue = ParseAvatarLadFloat(
                    element.Attribute("value_default"),
                    0f)
            };

            XElement color =
                element.Element("param_color");

            if (color != null)
            {
                AvatarLadColorParam cp = new()
                {
                    Operation =
                        ((string)color.Attribute("operation") ??
                         "add").ToLowerInvariant()
                };

                foreach (XElement value in
                         color.Elements("value"))
                {
                    int[] rgba =
                        ParseAvatarLadColor(
                            (string)value.Attribute("color"));

                    if (rgba != null)
                        cp.Values.Add(rgba);
                }

                parameter.Color = cp;
            }

            XElement alpha =
                element.Element("param_alpha");

            if (alpha != null)
            {
                parameter.Alpha =
                    new AvatarLadAlpha
                    {
                        TgaFile =
                            (string)alpha.Attribute("tga_file") ??
                            string.Empty,
                        SkipIfZero =
                            ParseAvatarLadBool(
                                alpha.Attribute("skip_if_zero"),
                                false),
                        MultiplyBlend =
                            ParseAvatarLadBool(
                                alpha.Attribute("multiply_blend"),
                                false),
                        Domain =
                            ParseAvatarLadFloat(
                                alpha.Attribute("domain"),
                                0f)
                    };
            }

            return parameter;
        }

        private static int ParseAvatarLadInt(
            XAttribute attribute,
            int fallback)
        {
            if (attribute == null)
                return fallback;

            return int.TryParse(
                attribute.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                ? value
                : fallback;
        }

        private static float ParseAvatarLadFloat(
            XAttribute attribute,
            float fallback)
        {
            if (attribute == null)
                return fallback;

            return float.TryParse(
                attribute.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value)
                ? value
                : fallback;
        }

        private static bool ParseAvatarLadBool(
            XAttribute attribute,
            bool fallback)
        {
            if (attribute == null)
                return fallback;

            string value =
                attribute.Value.Trim();

            return value.Equals(
                       "true",
                       StringComparison.OrdinalIgnoreCase) ||
                   value == "1"
                ? true
                : value.Equals(
                      "false",
                      StringComparison.OrdinalIgnoreCase) ||
                  value == "0"
                    ? false
                    : fallback;
        }

        private static int[] ParseAvatarLadColor(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string[] parts =
                text.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 3)
                return null;

            int[] rgba =
            {
                255, 255, 255, 255
            };

            for (int i = 0;
                 i < Math.Min(parts.Length, 4);
                 i++)
            {
                if (int.TryParse(
                        parts[i].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int value))
                {
                    rgba[i] =
                        Math.Clamp(value, 0, 255);
                }
            }

            return rgba;
        }

        private sealed class AvatarLadProfile
        {
            public Dictionary<string, AvatarLadSet> Sets { get; } =
                new(StringComparer.Ordinal);

            public Dictionary<string, List<AvatarLadParam>>
                GlobalColors { get; } =
                    new(StringComparer.Ordinal);
        }

        private sealed class AvatarLadSet
        {
            public int Width;
            public int Height;
            public bool ClearAlpha;
            public List<AvatarLadLayer> Layers { get; } = new();
        }

        private sealed class AvatarLadLayer
        {
            public int Order;
            public string Name = string.Empty;
            public int[] FixedColor;
            public string GlobalColor = string.Empty;
            public bool VisibilityMask;
            public bool WriteAllChannels;
            public bool HasMorphMask;
            public string RenderPass = string.Empty;
            public List<AvatarLadTexture> Textures { get; } = new();
            public List<AvatarLadParam> Params { get; } = new();
        }

        private sealed class AvatarLadTexture
        {
            public int TextureIndex = -1;
            public string TgaFile = string.Empty;
            public bool FileIsMask;
            public bool LocalTextureAlphaOnly;
        }

        private sealed class AvatarLadParam
        {
            public int Id = -1;
            public float MinValue;
            public float MaxValue = 1f;
            public float DefaultValue;
            public AvatarLadColorParam Color;
            public AvatarLadAlpha Alpha;
        }

        private sealed class AvatarLadColorParam
        {
            public string Operation = "add";
            public List<int[]> Values { get; } = new();
        }

        private sealed class AvatarLadAlpha
        {
            public string TgaFile = string.Empty;
            public bool SkipIfZero;
            public bool MultiplyBlend;
            public float Domain;
        }

        private static ManagedImage LoadBakeResource(string name)
        {
            try
            {
                return Baker.LoadResourceLayer(name);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsVisibilityTextureIndex(int textureIndex)
        {
            return textureIndex >= 21 && textureIndex <= 25;
        }

        private static bool IsBodypaintTextureIndex(int textureIndex)
        {
            return textureIndex == 0 ||
                   textureIndex == 5 ||
                   textureIndex == 6;
        }

        private static int GetLayerOrder(int textureIndex)
        {
            return textureIndex switch
            {
                // Base skin/body textures first.
                0 => 100,
                5 => 100,
                6 => 100,
                3 => 100,
                4 => 100,
                18 => 100,

                // Tattoo / universal tattoo before clothing.
                26 => 200,
                27 => 200,
                28 => 200,
                29 => 210,
                30 => 210,
                31 => 210,
                32 => 210,
                33 => 210,
                34 => 210,
                35 => 210,
                36 => 210,
                37 => 210,
                38 => 210,
                39 => 210,

                // Under-layers.
                17 => 300,
                16 => 300,
                12 => 310,

                // Shoes/gloves then primary clothing.
                7 => 400,
                15 => 400,
                2 => 500,
                1 => 500,

                // Jacket is outermost classic clothing layer.
                13 => 600,
                14 => 600,

                // Visibility masks are applied after all color layers.
                21 => 1000,
                22 => 1000,
                23 => 1000,
                24 => 1000,
                25 => 1000,

                _ => 700
            };
        }

        private static bool MaskBelongsToBake(string bakeSlot, string mask)
        {
            if (string.IsNullOrEmpty(mask))
                return false;

            if (bakeSlot == "lower" &&
                (mask.Contains("upper", StringComparison.OrdinalIgnoreCase) ||
                 mask.Contains("shirt", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (bakeSlot == "upper" &&
                mask.Contains("lower", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static void ApplyParamMasks(
            ManagedImage texture,
            Dictionary<VisualAlphaParam, float> masks,
            string bakeSlot)
        {
            if (texture == null || masks == null || masks.Count == 0)
                return;

            ManagedImage combined = new(
                texture.Width,
                texture.Height,
                ManagedImage.ImageChannels.Alpha);

            int normalCount = 0;

            foreach (KeyValuePair<VisualAlphaParam, float> kvp in masks)
            {
                if (!MaskBelongsToBake(bakeSlot, kvp.Key.TGAFile) ||
                    kvp.Key.MultiplyBlend ||
                    (kvp.Value <= 0f && kvp.Key.SkipIfZero))
                {
                    continue;
                }

                ApplyAlphaMask(combined, kvp.Key, kvp.Value);
                normalCount++;
            }

            if (normalCount == 0 && combined.Alpha != null)
                Array.Fill(combined.Alpha, byte.MaxValue);

            foreach (KeyValuePair<VisualAlphaParam, float> kvp in masks)
            {
                if (!MaskBelongsToBake(bakeSlot, kvp.Key.TGAFile) ||
                    !kvp.Key.MultiplyBlend ||
                    (kvp.Value <= 0f && kvp.Key.SkipIfZero))
                {
                    continue;
                }

                ApplyAlphaMask(combined, kvp.Key, kvp.Value);
            }

            AddAlpha(texture, combined);
        }

        private static void ApplyAlphaMask(
            ManagedImage dest,
            VisualAlphaParam param,
            float value)
        {
            ManagedImage src = LoadBakeResource(param.TGAFile);
            if (dest?.Alpha == null || src?.Alpha == null)
                return;

            if (dest.Width != src.Width || dest.Height != src.Height)
            {
                try
                {
                    src.ResizeNearestNeighbor(dest.Width, dest.Height);
                }
                catch
                {
                    return;
                }
            }

            float clamped = Math.Clamp(value, 0f, 1f);
            byte threshold = (byte)((1f - clamped) * 255f);

            for (int i = 0; i < dest.Alpha.Length; i++)
            {
                byte alpha = src.Alpha[i] <= threshold
                    ? (byte)0
                    : byte.MaxValue;

                if (param.MultiplyBlend)
                {
                    dest.Alpha[i] =
                        (byte)((dest.Alpha[i] * alpha) >> 8);
                }
                else if (alpha > dest.Alpha[i])
                {
                    dest.Alpha[i] = alpha;
                }
            }
        }

        private static void ApplyTint(ManagedImage image, Color4 color)
        {
            if (image?.Red == null ||
                image.Green == null ||
                image.Blue == null)
            {
                return;
            }

            byte r = Utils.FloatZeroOneToByte(color.R);
            byte g = Utils.FloatZeroOneToByte(color.G);
            byte b = Utils.FloatZeroOneToByte(color.B);

            for (int i = 0; i < image.Red.Length; i++)
            {
                image.Red[i] = (byte)((image.Red[i] * r) >> 8);
                image.Green[i] = (byte)((image.Green[i] * g) >> 8);
                image.Blue[i] = (byte)((image.Blue[i] * b) >> 8);
            }
        }

        private static void AddAlpha(ManagedImage dest, ManagedImage src)
        {
            if (dest == null || src == null)
                return;

            if ((dest.Channels & ManagedImage.ImageChannels.Alpha) == 0)
            {
                dest.ConvertChannels(
                    dest.Channels | ManagedImage.ImageChannels.Alpha);
            }

            if ((src.Channels & ManagedImage.ImageChannels.Alpha) == 0 ||
                src.Alpha == null)
            {
                return;
            }

            if (dest.Width != src.Width || dest.Height != src.Height)
            {
                try
                {
                    src.ResizeNearestNeighbor(dest.Width, dest.Height);
                }
                catch
                {
                    return;
                }
            }

            for (int i = 0; i < dest.Alpha.Length; i++)
            {
                if (src.Alpha[i] < dest.Alpha[i])
                    dest.Alpha[i] = src.Alpha[i];
            }
        }

        private static void MultiplyLayerFromAlpha(
            ManagedImage dest,
            ManagedImage src)
        {
            if (dest?.Red == null || src?.Alpha == null)
                return;

            if (dest.Width != src.Width || dest.Height != src.Height)
            {
                try
                {
                    src.ResizeNearestNeighbor(dest.Width, dest.Height);
                }
                catch
                {
                    return;
                }
            }

            for (int i = 0; i < dest.Red.Length; i++)
            {
                dest.Red[i] = (byte)((dest.Red[i] * src.Alpha[i]) >> 8);
                dest.Green[i] =
                    (byte)((dest.Green[i] * src.Alpha[i]) >> 8);
                dest.Blue[i] =
                    (byte)((dest.Blue[i] * src.Alpha[i]) >> 8);
            }
        }

        private static void ReplaceLayer(
            ManagedImage dest,
            ManagedImage source)
        {
            if (dest == null || source == null)
                return;

            if (dest.Width != source.Width ||
                dest.Height != source.Height)
            {
                try
                {
                    source.ResizeNearestNeighbor(
                        dest.Width,
                        dest.Height);
                }
                catch
                {
                    return;
                }
            }

            if (source.Red != null &&
                source.Green != null &&
                source.Blue != null)
            {
                Buffer.BlockCopy(
                    source.Red,
                    0,
                    dest.Red,
                    0,
                    Math.Min(source.Red.Length, dest.Red.Length));

                Buffer.BlockCopy(
                    source.Green,
                    0,
                    dest.Green,
                    0,
                    Math.Min(source.Green.Length, dest.Green.Length));

                Buffer.BlockCopy(
                    source.Blue,
                    0,
                    dest.Blue,
                    0,
                    Math.Min(source.Blue.Length, dest.Blue.Length));
            }

            if (dest.Alpha != null)
            {
                if (source.Alpha != null)
                {
                    Buffer.BlockCopy(
                        source.Alpha,
                        0,
                        dest.Alpha,
                        0,
                        Math.Min(
                            source.Alpha.Length,
                            dest.Alpha.Length));
                }
                else
                {
                    Array.Fill(
                        dest.Alpha,
                        byte.MaxValue);
                }
            }
        }

        private static void DrawLayer(
            ManagedImage dest,
            ManagedImage source,
            bool addSourceAlpha)
        {
            if (dest == null || source == null)
                return;

            if (dest.Width != source.Width ||
                dest.Height != source.Height)
            {
                try
                {
                    source.ResizeNearestNeighbor(dest.Width, dest.Height);
                }
                catch
                {
                    return;
                }
            }

            bool hasColor =
                (source.Channels & ManagedImage.ImageChannels.Color) != 0 &&
                source.Red != null &&
                source.Green != null &&
                source.Blue != null;

            bool hasAlpha =
                (source.Channels & ManagedImage.ImageChannels.Alpha) != 0 &&
                source.Alpha != null;

            bool hasBump =
                (source.Channels & ManagedImage.ImageChannels.Bump) != 0 &&
                source.Bump != null;

            for (int i = 0; i < dest.Red.Length; i++)
            {
                int alpha = hasAlpha ? source.Alpha[i] : 255;
                int inverse = 255 - alpha;

                if (hasColor)
                {
                    dest.Red[i] =
                        (byte)((dest.Red[i] * inverse +
                                source.Red[i] * alpha) >> 8);
                    dest.Green[i] =
                        (byte)((dest.Green[i] * inverse +
                                source.Green[i] * alpha) >> 8);
                    dest.Blue[i] =
                        (byte)((dest.Blue[i] * inverse +
                                source.Blue[i] * alpha) >> 8);
                }

                if (addSourceAlpha && hasAlpha &&
                    source.Alpha[i] < dest.Alpha[i])
                {
                    dest.Alpha[i] = source.Alpha[i];
                }

                if (hasBump)
                    dest.Bump[i] = source.Bump[i];
            }
        }

        private static void FillDithered(
            ManagedImage image,
            Color4 color)
        {
            byte r = Utils.FloatZeroOneToByte(color.R);
            byte g = Utils.FloatZeroOneToByte(color.G);
            byte b = Utils.FloatZeroOneToByte(color.B);

            byte rAlt = r < byte.MaxValue
                ? (byte)(r + 1)
                : (byte)(r - 1);
            byte gAlt = g < byte.MaxValue
                ? (byte)(g + 1)
                : (byte)(g - 1);
            byte bAlt = b < byte.MaxValue
                ? (byte)(b + 1)
                : (byte)(b - 1);

            int i = 0;
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    if (((x ^ y) & 0x10) == 0)
                    {
                        image.Red[i] = rAlt;
                        image.Green[i] = g;
                        image.Blue[i] = b;
                    }
                    else
                    {
                        image.Red[i] = r;
                        image.Green[i] = gAlt;
                        image.Blue[i] = bAlt;
                    }

                    image.Alpha[i] = byte.MaxValue;
                    image.Bump[i] = 0;
                    i++;
                }
            }
        }

        private static OSDMap FindBakeMap(OSDArray bakes, string name)
        {
            foreach (OSD entry in bakes)
            {
                if (entry is OSDMap map &&
                    map["name"].AsString() == name)
                {
                    return map;
                }
            }

            return null;
        }

        private static UUID DeterministicBakeAssetID(
            string recipeHash,
            string bakeSlot,
            string j2kHash)
        {
            string canonical =
                "novalyth-c5-bake-asset-v1|" +
                CompositorFingerprint + "|" +
                recipeHash + "|" + bakeSlot + "|" + j2kHash;

            string hex = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();

            string id =
                hex.Substring(0, 8) + "-" +
                hex.Substring(8, 4) + "-" +
                hex.Substring(12, 4) + "-" +
                hex.Substring(16, 4) + "-" +
                hex.Substring(20, 12);

            UUID.TryParse(id, out UUID uuid);
            return uuid;
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
                        "[NOVALYTH APPEARANCE C2B2]: COF version increment failed for {0}: {1}",
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

            // NOVALYTH APPEARANCE C4D
            // This endpoint is the viewer control plane, NOT the bake worker.
            // It must return promptly even if the previous 2K bake is still
            // compositing. The Region queues the expensive bake after this
            // successful acknowledgement and tracks generations for stale
            // suppression.
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

            OSDMap accepted = new();
            accepted["success"] = true;
            accepted["version"] = authoritativeVersion;
            accepted["expected"] = authoritativeVersion;
            accepted["cof_authority"] = "Inventory Core";
            accepted["server_bake"] = "accepted";
            accepted["bake_contract_version"] = m_bakeContractVersion;

            m_log.InfoFormat(
                "[NOVALYTH APPEARANCE C4D]: UpdateAvatarAppearance accepted agent={0} cof={1}; expensive bake remains asynchronous",
                agentID,
                authoritativeVersion);

            WriteMap(response, HttpStatusCode.OK, accepted);
        }

        private static List<string> ValidateCanonicalWearables(
            List<RecipeItem> wearables)
        {
            List<string> errors = new();

            foreach (int wearableType in s_singletonBodypartWearableTypes)
            {
                int count = wearables.Count(x => x.WearableType == wearableType);
                if (count != 1)
                {
                    errors.Add(
                        "singleton_" + WearableTypeLabel(wearableType) +
                        "_count=" + count);
                }
            }

            foreach (IGrouping<int, RecipeItem> group in
                     wearables.GroupBy(x => x.WearableType))
            {
                // OpenSim AvatarWearable stores at most five entries per type.
                // Reject rather than silently truncating active appearance state.
                if (group.Count() > 5)
                {
                    errors.Add(
                        "wearable_type_" + group.Key +
                        "_count_exceeds_5=" + group.Count());
                }
            }

            return errors;
        }

        private static string WearableTypeLabel(int wearableType)
        {
            return wearableType switch
            {
                WT_SHAPE => "shape",
                WT_SKIN => "skin",
                WT_HAIR => "hair",
                WT_EYES => "eyes",
                _ => wearableType.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static bool TryBuildCanonicalAppearancePayload(
            List<RecipeItem> wearables,
            out byte[] wireVisualParams,
            out Vector3 avatarSize,
            out string error)
        {
            wireVisualParams = Array.Empty<byte>();
            avatarSize = new Vector3(0.45f, 0.6f, 1.9f);
            error = string.Empty;

            Dictionary<int, float> values = new();

            // Same precedence model used by libOpenMetaverse: first currently
            // worn wearable carrying a parameter wins. RecipeItem sorting makes
            // Shape/Skin/Hair/Eyes deterministic before clothing layers.
            foreach (RecipeItem item in wearables)
            {
                if (item.ParsedWearable == null)
                    continue;

                foreach (WearableParameter parameter in
                         item.ParsedWearable.Parameters.OrderBy(x => x.ID))
                {
                    if (!values.ContainsKey(parameter.ID))
                        values[parameter.ID] = parameter.Weight;
                }
            }

            bool wearingPhysics =
                wearables.Any(x => x.WearableType == WT_PHYSICS);

            int requiredCount = wearingPhysics ? 251 : 218;
            wireVisualParams = new byte[requiredCount];

            float height = 0f;
            float heelHeight = 0f;
            float platformHeight = 0f;
            float headSize = 0.5f;
            float legLength = 0f;
            float neckLength = 0f;
            float hipLength = 0f;

            int wireIndex = 0;

            foreach (KeyValuePair<int, VisualParam> kvp in VisualParams.Params)
            {
                VisualParam vp = kvp.Value;

                float value =
                    values.TryGetValue(vp.ParamID, out float found)
                        ? found
                        : vp.DefaultValue;

                // C4D3: if param 11000 belongs to the transmitted visual-param
                // generation, it must agree with AppearanceData.AppearanceVersion.
                if (vp.ParamID == AppearanceMessageVersionParamID)
                    value = ServerAppearanceVersion;

                if (vp.Group == 0)
                {
                    if (wireIndex >= requiredCount)
                        break;

                    wireVisualParams[wireIndex++] =
                        Utils.FloatToByte(
                            value,
                            vp.MinValue,
                            vp.MaxValue);
                }

                switch (vp.ParamID)
                {
                    case 33:
                        height = value;
                        break;
                    case 198:
                        heelHeight = value;
                        break;
                    case 503:
                        platformHeight = value;
                        break;
                    case 682:
                        headSize = value;
                        break;
                    case 692:
                        legLength = value;
                        break;
                    case 756:
                        neckLength = value;
                        break;
                    case 842:
                        hipLength = value;
                        break;
                }

                if (wireIndex == requiredCount)
                    break;
            }

            if (wireIndex != requiredCount)
            {
                error =
                    "visual_param_count_mismatch:" +
                    wireIndex + "/" + requiredCount;
                wireVisualParams = Array.Empty<byte>();
                return false;
            }

            // libOpenMetaverse AgentSetAppearance size calculation.
            double agentHeight =
                1.706 +
                (legLength * .1918) +
                (hipLength * .0375) +
                (height * .12022) +
                (headSize * .01117) +
                (neckLength * .038) +
                (heelHeight * .08) +
                (platformHeight * .07);

            if (double.IsNaN(agentHeight) ||
                double.IsInfinity(agentHeight) ||
                agentHeight < 0.5 ||
                agentHeight > 4.0)
            {
                error = "invalid_avatar_height:" +
                    agentHeight.ToString("R", CultureInfo.InvariantCulture);
                wireVisualParams = Array.Empty<byte>();
                return false;
            }

            avatarSize =
                new Vector3(
                    0.45f,
                    0.6f,
                    (float)agentHeight);

            return true;
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
                    "[NOVALYTH APPEARANCE C2B2]: COF content lookup failed for {0}: {1}",
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
                                "[NOVALYTH APPEARANCE C2B2]: wearable asset fetch failed {0}: {1}",
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

            List<string> bodypartCardinalityErrors =
                ValidateCanonicalWearables(wearables);

            // Re-read COF after resolving every link and wearable asset. If its
            // folder version changed while the recipe was being assembled, this
            // snapshot is transitional and must never be baked/published.
            InventoryFolderBase cofAfter = GetAuthoritativeCOF(agentID);
            bool cofStable =
                cofAfter != null &&
                cofAfter.ID == cof.ID &&
                cofAfter.Version == cof.Version;

            string cofStabilityError = cofStable
                ? string.Empty
                : "cof_changed_during_recipe";

            bool appearancePayloadReady = false;
            byte[] canonicalVisualParams = Array.Empty<byte>();
            Vector3 canonicalAvatarSize = new Vector3(0.45f, 0.6f, 1.9f);
            string appearancePayloadError = string.Empty;

            if (bodypartCardinalityErrors.Count == 0 &&
                missingAssets.Count == 0 &&
                wearableParseErrors.Count == 0 &&
                wearableTypeMismatches.Count == 0 &&
                cofStable)
            {
                appearancePayloadReady =
                    TryBuildCanonicalAppearancePayload(
                        wearables,
                        out canonicalVisualParams,
                        out canonicalAvatarSize,
                        out appearancePayloadError);
            }
            else
            {
                appearancePayloadError = "canonical_prerequisites_not_ready";
            }

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
            OSDArray bodypartCardinalityArray =
                ToStringArray(bodypartCardinalityErrors);

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
            manifest["recipe_format"] = "novalyth-ssa-recipe-v3";
            manifest["bake_contract_version"] = m_bakeContractVersion;
            manifest["bake_slot_count"] = s_bakeDefinitions.Length;
            manifest["generated_utc"] = DateTime.UtcNow.ToString("O");
            manifest["wearables"] = wearableArray;
            manifest["attachments"] = attachmentArray;
            manifest["broken_links"] = brokenArray;
            manifest["missing_assets"] = missingArray;
            manifest["wearable_parse_errors"] = parseErrorArray;
            manifest["wearable_type_mismatches"] = typeMismatchArray;
            manifest["bodypart_cardinality_errors"] = bodypartCardinalityArray;
            manifest["bodypart_singletons_valid"] =
                bodypartCardinalityErrors.Count == 0;
            manifest["cof_stable"] = cofStable;
            manifest["cof_stability_error"] = cofStabilityError;
            manifest["appearance_payload_ready"] = appearancePayloadReady;
            manifest["appearance_payload_error"] =
                appearancePayloadError ?? string.Empty;
            manifest["appearance_version"] = ServerAppearanceVersion;
            manifest["visual_params_b64"] =
                appearancePayloadReady
                    ? Convert.ToBase64String(canonicalVisualParams)
                    : string.Empty;
            manifest["visual_param_count"] =
                appearancePayloadReady
                    ? canonicalVisualParams.Length
                    : 0;
            manifest["avatar_size_x"] = canonicalAvatarSize.X;
            manifest["avatar_size_y"] = canonicalAvatarSize.Y;
            manifest["avatar_size_z"] = canonicalAvatarSize.Z;
            manifest["source_textures"] = sourceTextureArray;
            manifest["missing_source_assets"] = missingSourceArray;
            manifest["source_texture_count"] = sourceLayers.Count;
            manifest["source_audit_status"] = "not_run";
            manifest["pixel_compositor_status"] = "c2b2_available_on_demand";
            manifest["compositor_profile"] = CompositorProfile;
            manifest["compositor_fingerprint"] = CompositorFingerprint;
            manifest["compositor_semantics"] = CompositorSemantics;
            manifest["bake_asset_store_status"] = "not_run";
            manifest["bake_asset_authority"] = "Asset Core";
            manifest["central_bake_advertised"] = false;
            manifest["bakes"] = bakes;

            if (brokenLinks.Count > 0 ||
                missingAssets.Count > 0 ||
                wearableParseErrors.Count > 0 ||
                wearableTypeMismatches.Count > 0 ||
                missingSourceAssets.Count > 0 ||
                bodypartCardinalityErrors.Count > 0 ||
                !cofStable ||
                !appearancePayloadReady)
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

                    if (texture.TextureID.IsZero() ||
                        texture.TextureID == AppearanceManager.DEFAULT_AVATAR_TEXTURE)
                    {
                        continue;
                    }

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
                    "[NOVALYTH APPEARANCE C2B2]: source texture decode failed {0}: {1}",
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

            canonical.Append("novalyth-ssa-recipe-v3\n");
            canonical.Append("C|")
                .Append(CompositorFingerprint)
                .Append('\n');
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

        private sealed class BakeLayerInput
        {
            public string BakeSlot = string.Empty;
            public int TextureIndex;
            public int LayerOrder;
            public UUID TextureID;
            public int WearableType;
            public UUID WearableAssetID;
            public string OrderKey = string.Empty;
            public AppearanceManager.TextureData TextureData;

            public static int Compare(BakeLayerInput a, BakeLayerInput b)
            {
                int c = a.LayerOrder.CompareTo(b.LayerOrder);
                if (c != 0)
                    return c;

                c = string.Compare(
                    a.OrderKey,
                    b.OrderKey,
                    StringComparison.Ordinal);
                if (c != 0)
                    return c;

                c = a.WearableType.CompareTo(b.WearableType);
                if (c != 0)
                    return c;

                return string.Compare(
                    a.WearableAssetID.ToString(),
                    b.WearableAssetID.ToString(),
                    StringComparison.Ordinal);
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
