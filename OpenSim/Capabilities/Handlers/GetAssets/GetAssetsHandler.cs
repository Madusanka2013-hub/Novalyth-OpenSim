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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Capabilities.Handlers
{
    public class GetAssetsHandler
    {
        private static readonly ILog m_log =
                   LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly Dictionary<string, AssetType> queryTypes = new()
        {
            {"texture_id", AssetType.Texture},
            {"sound_id", AssetType.Sound},
            {"callcard_id", AssetType.CallingCard},
            {"landmark_id", AssetType.Landmark},
            {"script_id", AssetType.LSLText},
            {"clothing_id", AssetType.Clothing},
            {"object_id", AssetType.Object},
            {"notecard_id", AssetType.Notecard},
            {"lsltext_id", AssetType.LSLText},
            {"lslbyte_id", AssetType.LSLBytecode},
            {"txtr_tga_id", AssetType.TextureTGA},
            {"bodypart_id", AssetType.Bodypart},
            {"snd_wav_id", AssetType.SoundWAV},
            {"img_tga_id", AssetType.ImageTGA},
            {"jpeg_id", AssetType.ImageJPEG},
            {"animatn_id", AssetType.Animation},
            {"gesture_id", AssetType.Gesture},
            {"mesh_id", AssetType.Mesh},
            {"settings_id", AssetType.Settings},
            {"material_id", AssetType.Material}
        };

        // NOVALYTH TEXTURE C4B
        // Viewer texture requests are progressive HTTP Range requests. Keeping the
        // immutable J2K asset in-process means subsequent ranges do not traverse
        // Region -> Asset Core again. Server-baked avatar textures can be prewarmed
        // before the new AvatarAppearance is published, making the first viewer
        // range request a RAM hit as well.
        private sealed class TextureHotCacheEntry
        {
            public AssetBase Asset;
            public long LastAccessMs;
            public int Bytes;
            public bool BakePrime;
        }

        private static readonly object s_textureHotCacheLock = new();
        private static readonly Dictionary<UUID, TextureHotCacheEntry>
            s_textureHotCache = new();
        private static readonly object[] s_textureFetchLocks =
            CreateTextureFetchLocks();

        private static long s_textureHotCacheMaxBytes = 512L * 1024L * 1024L;
        private static long s_textureHotCacheTtlMs = 900L * 1000L;
        private static int s_textureHotCacheMaxAssetBytes = 16 * 1024 * 1024;
        private static long s_textureHotCacheBytes;

        private static long s_textureHotCacheHits;
        private static long s_textureHotCacheMisses;
        private static long s_textureHotCacheStores;
        private static long s_textureHotCachePrimes;
        private static long s_textureHotCacheEvictions;
        private static long s_textureHotCacheExpired;
        private static long s_textureHotCacheOversizeBypass;

        private static object[] CreateTextureFetchLocks()
        {
            object[] locks = new object[256];
            for (int i = 0; i < locks.Length; i++)
                locks[i] = new object();
            return locks;
        }

        private static object GetTextureFetchLock(UUID assetID)
        {
            int hash = assetID.GetHashCode() & 0x7fffffff;
            return s_textureFetchLocks[hash % s_textureFetchLocks.Length];
        }

        public static void ConfigureTextureHotCache(
            int maxMegabytes,
            int ttlSeconds,
            int maxAssetMegabytes)
        {
            long maxBytes =
                Math.Clamp(maxMegabytes, 0, 4096) * 1024L * 1024L;
            long ttlMs =
                Math.Clamp(ttlSeconds, 30, 86400) * 1000L;
            int maxAssetBytes =
                Math.Clamp(maxAssetMegabytes, 1, 64) * 1024 * 1024;

            lock (s_textureHotCacheLock)
            {
                s_textureHotCacheMaxBytes = maxBytes;
                s_textureHotCacheTtlMs = ttlMs;
                s_textureHotCacheMaxAssetBytes = maxAssetBytes;

                if (maxBytes <= 0)
                {
                    s_textureHotCache.Clear();
                    s_textureHotCacheBytes = 0;
                }
                else
                {
                    TrimTextureHotCacheLocked(Environment.TickCount64);
                }
            }
        }

        public static bool PrimeTextureAsset(AssetBase asset)
        {
            return StoreTextureHotCache(asset, true);
        }

        public static string GetTextureHotCacheReport()
        {
            lock (s_textureHotCacheLock)
            {
                long requests = s_textureHotCacheHits + s_textureHotCacheMisses;
                double hitRate = requests > 0
                    ? (100.0 * s_textureHotCacheHits / requests)
                    : 0.0;

                return string.Format(
                    "=== NOVALYTH TEXTURE HOT CACHE C4B ===\n" +
                    "config: max_mb={0} ttl_s={1} max_asset_mb={2}\n" +
                    "state: entries={3} bytes={4} mb={5:F1}\n" +
                    "traffic: hits={6} misses={7} hit_rate={8:F1}% stores={9} bake_primes={10} evictions={11} expired={12} oversize_bypass={13}",
                    s_textureHotCacheMaxBytes / (1024L * 1024L),
                    s_textureHotCacheTtlMs / 1000L,
                    s_textureHotCacheMaxAssetBytes / (1024 * 1024),
                    s_textureHotCache.Count,
                    s_textureHotCacheBytes,
                    s_textureHotCacheBytes / (1024.0 * 1024.0),
                    s_textureHotCacheHits,
                    s_textureHotCacheMisses,
                    hitRate,
                    s_textureHotCacheStores,
                    s_textureHotCachePrimes,
                    s_textureHotCacheEvictions,
                    s_textureHotCacheExpired,
                    s_textureHotCacheOversizeBypass);
            }
        }

        public static void ResetTextureHotCacheMetrics()
        {
            lock (s_textureHotCacheLock)
            {
                s_textureHotCacheHits = 0;
                s_textureHotCacheMisses = 0;
                s_textureHotCacheStores = 0;
                s_textureHotCachePrimes = 0;
                s_textureHotCacheEvictions = 0;
                s_textureHotCacheExpired = 0;
                s_textureHotCacheOversizeBypass = 0;
            }
        }

        private static bool TextureHotCacheEnabled =>
            Interlocked.Read(ref s_textureHotCacheMaxBytes) > 0;

        private static bool TryGetTextureHotCache(
            UUID assetID,
            out AssetBase asset,
            bool countMiss = true)
        {
            asset = null;
            long now = Environment.TickCount64;

            lock (s_textureHotCacheLock)
            {
                if (s_textureHotCacheMaxBytes <= 0)
                {
                    if (countMiss)
                        ++s_textureHotCacheMisses;
                    return false;
                }

                if (!s_textureHotCache.TryGetValue(
                        assetID,
                        out TextureHotCacheEntry entry))
                {
                    if (countMiss)
                        ++s_textureHotCacheMisses;
                    return false;
                }

                if (now - entry.LastAccessMs > s_textureHotCacheTtlMs)
                {
                    RemoveTextureHotCacheEntryLocked(assetID, entry, expired: true);
                    if (countMiss)
                        ++s_textureHotCacheMisses;
                    return false;
                }

                entry.LastAccessMs = now;
                ++s_textureHotCacheHits;
                asset = entry.Asset;
                return true;
            }
        }

        private static bool StoreTextureHotCache(
            AssetBase asset,
            bool bakePrime)
        {
            if (asset == null ||
                asset.Data == null ||
                asset.Data.Length == 0 ||
                asset.Type != (sbyte)AssetType.Texture)
            {
                return false;
            }

            lock (s_textureHotCacheLock)
            {
                if (s_textureHotCacheMaxBytes <= 0)
                    return false;

                if (asset.Data.Length > s_textureHotCacheMaxAssetBytes ||
                    asset.Data.Length > s_textureHotCacheMaxBytes)
                {
                    ++s_textureHotCacheOversizeBypass;
                    return false;
                }

                if (s_textureHotCache.TryGetValue(
                        asset.FullID,
                        out TextureHotCacheEntry old))
                {
                    s_textureHotCacheBytes -= old.Bytes;
                }

                TextureHotCacheEntry entry = new()
                {
                    Asset = asset,
                    LastAccessMs = Environment.TickCount64,
                    Bytes = asset.Data.Length,
                    BakePrime = bakePrime || (old?.BakePrime ?? false)
                };

                s_textureHotCache[asset.FullID] = entry;
                s_textureHotCacheBytes += entry.Bytes;
                ++s_textureHotCacheStores;
                if (bakePrime)
                    ++s_textureHotCachePrimes;

                TrimTextureHotCacheLocked(entry.LastAccessMs);
                return s_textureHotCache.ContainsKey(asset.FullID);
            }
        }

        private static void TrimTextureHotCacheLocked(long now)
        {
            if (s_textureHotCache.Count == 0)
                return;

            List<UUID> expired = null;
            foreach (KeyValuePair<UUID, TextureHotCacheEntry> kvp in s_textureHotCache)
            {
                if (now - kvp.Value.LastAccessMs > s_textureHotCacheTtlMs)
                {
                    expired ??= new List<UUID>();
                    expired.Add(kvp.Key);
                }
            }

            if (expired != null)
            {
                foreach (UUID id in expired)
                {
                    if (s_textureHotCache.TryGetValue(id, out TextureHotCacheEntry entry))
                        RemoveTextureHotCacheEntryLocked(id, entry, expired: true);
                }
            }

            while (s_textureHotCacheBytes > s_textureHotCacheMaxBytes &&
                   s_textureHotCache.Count > 0)
            {
                UUID victimID = UUID.Zero;
                TextureHotCacheEntry victim = null;

                foreach (KeyValuePair<UUID, TextureHotCacheEntry> kvp in s_textureHotCache)
                {
                    TextureHotCacheEntry candidate = kvp.Value;
                    if (victim == null ||
                        (victim.BakePrime && !candidate.BakePrime) ||
                        (victim.BakePrime == candidate.BakePrime &&
                         candidate.LastAccessMs < victim.LastAccessMs))
                    {
                        victimID = kvp.Key;
                        victim = candidate;
                    }
                }

                if (victim == null)
                    break;

                RemoveTextureHotCacheEntryLocked(victimID, victim, expired: false);
            }
        }

        private static void RemoveTextureHotCacheEntryLocked(
            UUID assetID,
            TextureHotCacheEntry entry,
            bool expired)
        {
            if (!s_textureHotCache.Remove(assetID))
                return;

            s_textureHotCacheBytes -= entry.Bytes;
            if (s_textureHotCacheBytes < 0)
                s_textureHotCacheBytes = 0;

            if (expired)
                ++s_textureHotCacheExpired;
            else
                ++s_textureHotCacheEvictions;
        }

        private IAssetService m_assetService;
        private readonly int m_assetFetchTimeoutMs;

        public GetAssetsHandler(IAssetService assService, int assetFetchTimeoutMs = 15000)
        {
            m_assetService = assService;
            m_assetFetchTimeoutMs = Math.Clamp(assetFetchTimeoutMs, 1000, 120000);
        }

        private bool TryFetchAssetFromBackend(
            UUID assetID,
            string serviceURL,
            OSHttpResponse response,
            out AssetBase asset)
        {
            asset = null;
            long fetchStartedAtMs = Environment.TickCount64;
            NovalythAssetPipelineMetrics.BackendStarted();

            TaskCompletionSource<AssetBase> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                m_assetService.Get(assetID.ToString(), serviceURL, false,
                    (AssetBase a) => completion.TrySetResult(a));
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[GETASSET]: asset service threw while requesting {0}: {1}",
                    assetID, e);
                NovalythAssetPipelineMetrics.BackendFinished(
                    Environment.TickCount64 - fetchStartedAtMs,
                    false, true, false);
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.KeepAlive = false;
                return false;
            }

            if (!completion.Task.Wait(m_assetFetchTimeoutMs))
            {
                m_log.WarnFormat(
                    "[GETASSET]: timed out after {0} ms requesting asset {1}",
                    m_assetFetchTimeoutMs, assetID);
                NovalythAssetPipelineMetrics.BackendFinished(
                    Environment.TickCount64 - fetchStartedAtMs,
                    true, false, false);
                response.StatusCode = (int)HttpStatusCode.GatewayTimeout;
                response.KeepAlive = false;
                return false;
            }

            asset = completion.Task.Result;
            if (asset == null)
            {
                NovalythAssetPipelineMetrics.BackendFinished(
                    Environment.TickCount64 - fetchStartedAtMs,
                    false, false, true);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return false;
            }

            NovalythAssetPipelineMetrics.BackendFinished(
                Environment.TickCount64 - fetchStartedAtMs,
                false, false, false);
            return true;
        }

        public void Handle(OSHttpRequest req, OSHttpResponse response, string serviceURL = null)
        {
            response.ContentType = "text/plain";

            if (m_assetService == null)
            {
                //m_log.Warn("[GETASSET]: no service");
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.KeepAlive = false;
                return;
            }

            response.StatusCode = (int)HttpStatusCode.BadRequest;

            Dictionary<string, string> queries = req.QueryAsDictionary;
            if(queries.Count == 0)
                return;

            AssetType type = AssetType.Unknown;
            string assetStr = string.Empty;
            foreach (KeyValuePair<string,string> kvp in queries)
            {
                if (queryTypes.TryGetValue(kvp.Key, out type))
                {
                    assetStr = kvp.Value;
                    break;
                }
            }

            if(type == AssetType.Unknown)
            {
                //m_log.Warn("[GETASSET]: Unknown type: " + query);
                m_log.Warn("[GETASSET]: Unknown type");
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (string.IsNullOrEmpty(assetStr))
                return;

            if(!UUID.TryParse(assetStr, out UUID assetID))
                return;

            AssetBase asset = null;
            bool textureHotHit = false;

            if (type == AssetType.Texture &&
                TryGetTextureHotCache(assetID, out asset))
            {
                textureHotHit = true;
            }
            else if (type == AssetType.Texture &&
                     TextureHotCacheEnabled &&
                     string.IsNullOrEmpty(serviceURL))
            {
                // Striped single-flight: concurrent ranges for the same local
                // texture cannot all trigger the first backend fetch.
                lock (GetTextureFetchLock(assetID))
                {
                    if (TryGetTextureHotCache(
                            assetID,
                            out asset,
                            countMiss: false))
                    {
                        textureHotHit = true;
                    }
                    else
                    {
                        if (!TryFetchAssetFromBackend(
                                assetID,
                                serviceURL,
                                response,
                                out asset))
                        {
                            return;
                        }

                        StoreTextureHotCache(asset, false);
                    }
                }
            }
            else
            {
                if (!TryFetchAssetFromBackend(
                        assetID,
                        serviceURL,
                        response,
                        out asset))
                {
                    return;
                }
            }

            int len = asset.Data.Length;

            if (len == 0)
            {
                m_log.Warn("[GETASSET]: asset with empty data: " + assetStr + " type " + asset.Type.ToString());
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (asset.Type != (sbyte)type)
            {
                m_log.Warn("[GETASSET]: asset with wrong type: " + assetStr + " " + asset.Type.ToString() + " != " + ((sbyte)type).ToString());
                //response.StatusCode = (int)HttpStatusCode.NotFound;
                //return;
            }

            // range request
            if (Util.TryParseHttpRange(req.Headers["range"], out int start, out int end))
            {
                // viewers do send broken start, then flag good assets as bad
                if (start >= len)
                {
                    //m_log.Warn("[GETASSET]: bad start: " + range);
                    response.StatusCode = (int)HttpStatusCode.OK;
                }
                else
                {
                    if (end == -1)
                        end = len - 1;
                    else
                        end = Utils.Clamp(end, 0, len - 1);

                    start = Utils.Clamp(start, 0, end);
                    len = end - start + 1;

                    //m_log.Debug("Serving " + start + " to " + end + " of " + texture.Data.Length + " bytes for texture " + texture.ID);
                    response.AddHeader("Content-Range", string.Format("bytes {0}-{1}/{2}", start, end, asset.Data.Length));
                    response.StatusCode = (int)HttpStatusCode.PartialContent;
                    response.RawBufferStart = start;
                }
            }
            else
                response.StatusCode = (int)HttpStatusCode.OK;

            response.ContentType = asset.Metadata.ContentType;
            response.RawBuffer = asset.Data;
            response.RawBufferLen = len;

            if (type == AssetType.Texture)
            {
                // Texture UUIDs are immutable. Keep the connection alive, tell
                // Firestorm ranges are supported and make intermediaries/viewer
                // caches free to retain the result aggressively.
                response.KeepAlive = true;
                response.Priority = 4;
                response.AddHeader("Accept-Ranges", "bytes");
                response.AddHeader(
                    "Cache-Control",
                    "public, max-age=31536000, immutable");
                response.AddHeader("ETag", "\"" + assetID + "\"");
                response.AddHeader(
                    "X-Novalyth-Texture-Hot",
                    textureHotHit ? "HIT" : "MISS");
            }
            else if (type == AssetType.Mesh)
            {
                response.Priority = 2;
            }
            else
            {
                response.Priority = 1;
            }
        }
    }
}