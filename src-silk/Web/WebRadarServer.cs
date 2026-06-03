// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

using eft_dma_radar.Silk.Misc.Data;
using eft_dma_radar.Silk.Misc.Workers;
using eft_dma_radar.Silk.Web.Data;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;
using static SDK.Offsets;

using Open.Nat;

namespace eft_dma_radar.Silk.Web
{
    /// <summary>
    /// Lightweight HTTP server for the web radar.
    /// Serves static files (HTML/JS/CSS/SVG maps) and exposes <c>/api/radar</c>
    /// as a JSON polling endpoint updated by a background worker thread.
    /// Supports UPnP/NAT-PMP automatic port forwarding and external IP detection.
    /// </summary>
    internal static class WebRadarServer
    {
        private static WebRadarUpdate _latest = new();
        // Pre-serialized radar payload, published by the worker once per tick.
        // Requests serve these bytes directly, avoiding per-request JsonSerializer cost
        // and removing races between the worker mutating _latest and the request thread
        // serializing it. Volatile read/write guarantees the reference flip is visible.
        private static byte[] _latestJson = Array.Empty<byte>();
        private static WebRadarQuestData? _questDataCache;
        private static WebApplication? _host;
        private static WorkerThread? _worker;
        private static TimeSpan _tickRate;
        private static int _upnpPort = -1;

        // Cached serialized payload for /api/items. The catalog comes from
        // EftDataManager.AllItems which is loaded once at startup and is
        // effectively immutable, so we serialize it on first request and
        // reuse the bytes for every subsequent request.
        private static byte[]? _itemCatalogJson;
        private static readonly Lock _catalogLock = new();

        // Reuse the shared pooled HttpClient (SocketsHttpHandler + connection pooling).
        // We still want a short timeout for external IP probes, so requests below
        // pass a CancellationToken with a 5-second deadline.

        public static bool IsRunning => _host is not null;

        /// <summary>
        /// The private (LAN) address for the web radar, e.g. "http://192.168.1.100:7224".
        /// Populated after <see cref="StartAsync"/> succeeds. Empty when stopped.
        /// </summary>
        public static string PrivateAddress { get; private set; } = string.Empty;

        /// <summary>
        /// The public (WAN) address for the web radar, e.g. "http://203.0.113.50:7224".
        /// Populated after <see cref="StartAsync"/> succeeds (async, may take a few seconds).
        /// Empty when stopped or if external IP detection failed.
        /// </summary>
        public static string PublicAddress { get; private set; } = string.Empty;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        /// <summary>
        /// Start the web radar HTTP server.
        /// </summary>
        public static async Task StartAsync(int port, TimeSpan tickRate, bool enableUpnp = false)
        {
            await StopAsync().ConfigureAwait(false);

            _tickRate = tickRate;

            ThrowIfPortInvalid(port);

            // UPnP port mapping (if enabled)
            if (enableUpnp)
            {
                var ok = await TryConfigureUPnPAsync(port);
                if (ok)
                    Log.WriteLine($"[WebRadar] UPnP port mapped: TCP {port} -> TCP {port}");
                else
                    Log.WriteLine("[WebRadar] UPnP failed (router may not support it / disabled / CGNAT). Continuing without UPnP.");
            }

            var builder = WebApplication.CreateBuilder();

            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Any, port);
            });

            _host = builder.Build();

            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

            _host.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot)
            });

            _host.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                RequestPath = "",
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                    ctx.Context.Response.Headers.Pragma = "no-cache";
                    ctx.Context.Response.Headers.Expires = "0";
                }
            });

            _host.MapGet("/api/radar", (HttpContext ctx) =>
            {
                var bytes = Volatile.Read(ref _latestJson);
                if (bytes.Length == 0)
                    return Results.Json(_latest, _jsonOpts);

                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength = bytes.Length;
                return Results.Bytes(bytes, "application/json; charset=utf-8");
            });
            _host.MapGet("/api/containers", (HttpContext ctx) =>
            {
                // Selected-container set can change at runtime via the desktop UI,
                // so rebuild every request. Container count is small (~dozens) so
                // the per-request cost is negligible — no need to cache.
                ctx.Response.Headers.CacheControl = "no-cache";
                return Results.Json(GetAvailableContainers(), _jsonOpts);
            });
            _host.MapGet("/api/items", (HttpContext ctx) =>
            {
                // Buddy item catalog. Used by the web radar's searchable
                // wishlist/blacklist panel — mirrors the local LootFiltersPanel UX.
                // Catalog is immutable after startup, so serialize once and reuse.
                var bytes = GetItemCatalogJson();
                ctx.Response.Headers.CacheControl = "public, max-age=3600";
                return Results.Bytes(bytes, "application/json; charset=utf-8");
            });
            _host.MapGet("/api/questdata", (HttpContext ctx) =>
            {
                // Buddy quest tracker source. Bundled tarkov.dev snapshot from EftDataManager.TaskData.
                // Cached aggressively — schema is stable across builds and the buddy persists locally.
                var data = _questDataCache ??= WebRadarQuestData.Build();
                ctx.Response.Headers.CacheControl = "public, max-age=3600";
                return Results.Json(data, _jsonOpts);
            });
            _host.MapGet("/api/tile/{cacheKey}/{z:int}/{x:int}/{y:int}.png",
                async (HttpContext ctx, string cacheKey, int z, int x, int y) =>
            {
                // Server-side proxy for tarkov.dev satellite tiles. Buddies on the web
                // radar can't fetch the upstream CDN directly because tarkov.dev doesn't
                // serve CORS headers; routing through the host puts every request on the
                // same origin. cacheKey must resolve to a known catalog entry — anything
                // else returns 404 (also blocks path-traversal attempts at the disk cache).
                if (!SatelliteMapCatalog.TryGetTemplateByCacheKey(cacheKey, out var template))
                    return Results.NotFound();

                var bytes = await TileCache.GetTileAsync(template, cacheKey, z, x, y, ctx.RequestAborted)
                    .ConfigureAwait(false);
                if (bytes is null)
                    return Results.NotFound();

                ctx.Response.Headers.CacheControl = "public, max-age=86400, immutable";
                return Results.Bytes(bytes, "image/png");
            });
            _host.MapGet("/health", () => Results.Text("OK"));

            await _host.StartAsync().ConfigureAwait(false);
            StartWorker();

            // Resolve addresses
            var localIP = GetLocalIPAddress();
            PrivateAddress = !string.IsNullOrEmpty(localIP) ? $"http://{localIP}:{port}" : $"http://localhost:{port}";

            Log.WriteLine($"[WebRadar] HTTP server running on port {port}");
            Log.WriteLine($"[WebRadar] Private address: {PrivateAddress}");

            // Resolve public address in the background
            _ = Task.Run(async () =>
            {
                try
                {
                    var externalIP = await GetExternalIPAsync();
                    PublicAddress = $"http://{externalIP}:{port}";
                    Log.WriteLine($"[WebRadar] Public address: {PublicAddress}");
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[WebRadar] Could not detect public IP: {ex.Message}");
                    PublicAddress = string.Empty;
                }
            });
        }

        /// <summary>
        /// Stop the web radar HTTP server and worker thread.
        /// </summary>
        public static async Task StopAsync()
        {
            var w = _worker;
            _worker = null;
            w?.Dispose();
            w?.Join(TimeSpan.FromSeconds(2));

            if (_host is not null)
            {
                await _host.StopAsync().ConfigureAwait(false);
                await _host.DisposeAsync().ConfigureAwait(false);
                _host = null;
            }

            // Clean up UPnP mapping if we created one
            if (_upnpPort > 0)
            {
                await CleanupUPnPAsync(_upnpPort);
                _upnpPort = -1;
            }

            PrivateAddress = string.Empty;
            PublicAddress = string.Empty;

            Log.WriteLine("[WebRadar] Server stopped.");
        }

        private static void StartWorker()
        {
            _worker = new WorkerThread
            {
                Name = "WebRadarWorker",
                ThreadPriority = ThreadPriority.Normal,
                SleepDuration = _tickRate,
                SleepMode = WorkerSleepMode.DynamicSleep,
            };
            _worker.PerformWork += Tick;
            _worker.Start();
        }

        private static void Tick(CancellationToken ct)
        {
            try
            {
                var update = _latest;
                    update.InGame = Memory.InRaid;
                    update.InRaid = Memory.InRaid;
                    update.InHideout = Memory.InHideout;
                    update.MapID = Memory.MapID;
                    update.SendTime = DateTime.UtcNow;
                    update.ActivePreset = SilkProgram.Config.ActivePresetId;
                    update.Version++;

                    // Status string — mirrors local radar overlay logic
                    if (Memory.InRaid)
                        update.Status = "In Raid";
                    else if (Memory.InHideout)
                        update.Status = "In Hideout";
                    else
                    {
                        var stage = MatchingProgressResolver.GetCachedStage();
                        update.Status = stage != EMatchingStage.None
                            ? stage.ToDisplayString()
                            : "Waiting for Raid Start";
                    }

                    // Map — always emit the raw SVG config so the buddy projection
                    // is stable regardless of the host's local satellite toggle.
                    // Satellite metadata rides along as a side-car for buddies that
                    // opt in client-side.
                    update.Map = WebRadarMapConverter.Convert(
                        MapManager.RawConfig,
                        MapManager.SatelliteConfig,
                        MapManager.SatelliteEntry);

                    // Players
                    var players = Memory.Players;
                    if (players is not null)
                    {
                        var count = players.Count;
                        var arr = new WebRadarPlayer[count];
                        int idx = 0;
                        foreach (var p in players)
                        {
                            if (idx >= arr.Length)
                                break;
                            arr[idx++] = WebRadarPlayer.CreateFromPlayer(p);
                        }

                        // Trim if iterator produced fewer than Count
                        if (idx < arr.Length)
                            Array.Resize(ref arr, idx);

                        update.Players = arr;
                    }
                    else
                    {
                        update.Players = null;
                    }

                    // Loot — emit unfiltered; the buddy web client decides what to show.
                    var loot = Memory.Loot;
                    if (loot is not null && loot.Count > 0)
                    {
                        var lootArr = new WebRadarLootItem[loot.Count];
                        for (int i = 0; i < loot.Count; i++)
                            lootArr[i] = WebRadarLootItem.Create(loot[i]);
                        update.Loot = lootArr;
                    }
                    else
                    {
                        update.Loot = null;
                    }

                    // Corpses
                    var corpses = Memory.Corpses;
                    if (corpses is not null && corpses.Count > 0)
                    {
                        var corpseArr = new WebRadarCorpse[corpses.Count];
                        for (int i = 0; i < corpses.Count; i++)
                            corpseArr[i] = WebRadarCorpse.Create(corpses[i]);
                        update.Corpses = corpseArr;
                    }
                    else
                    {
                        update.Corpses = null;
                    }

                    // Containers — emit all containers unfiltered; the buddy decides
                    // which container types to display and whether to hide searched ones.
                    var containers = Memory.Containers;
                    if (containers is not null && containers.Count > 0)
                    {
                        var containerArr = new WebRadarContainer[containers.Count];
                        for (int i = 0; i < containers.Count; i++)
                            containerArr[i] = WebRadarContainer.Create(containers[i]);
                        update.Containers = containerArr;
                    }
                    else
                    {
                        update.Containers = null;
                    }

                    // Exfils
                    var exfils = Memory.Exfils;
                    if (exfils is not null && exfils.Count > 0)
                    {
                        var exfilArr = new WebRadarExfil[exfils.Count];
                        for (int i = 0; i < exfils.Count; i++)
                            exfilArr[i] = WebRadarExfil.Create(exfils[i]);
                        update.Exfils = exfilArr;
                    }
                    else
                    {
                        update.Exfils = null;
                    }

                    // Killfeed
                    var killfeedSnap = Tarkov.GameWorld.Loot.KillfeedManager.Entries;
                    if (killfeedSnap.Length > 0)
                    {
                        var kfArr = new WebRadarKillfeedEntry[killfeedSnap.Length];
                        for (int i = 0; i < killfeedSnap.Length; i++)
                            kfArr[i] = WebRadarKillfeedEntry.Create(killfeedSnap[i]);
                        update.Killfeed = kfArr;
                    }
                    else
                    {
                        update.Killfeed = null;
                    }

                    // Switches (static — emitted every tick, payload is small)
                    var switches = Memory.Switches;
                    if (switches is not null && switches.Count > 0)
                    {
                        var swArr = new WebRadarSwitch[switches.Count];
                        for (int i = 0; i < switches.Count; i++)
                            swArr[i] = WebRadarSwitch.Create(switches[i]);
                        update.Switches = swArr;
                    }
                    else
                    {
                        update.Switches = null;
                    }

                    // Doors (only keyed doors with a valid state are emitted)
                    var doors = Memory.Doors;
                    if (doors is not null && doors.Count > 0)
                    {
                        var doorList = new List<WebRadarDoor>(doors.Count);
                        for (int i = 0; i < doors.Count; i++)
                        {
                            var dto = WebRadarDoor.Create(doors[i]);
                            if (dto is not null)
                                doorList.Add(dto);
                        }
                        update.Doors = doorList.Count > 0 ? [.. doorList] : null;
                    }
                    else
                    {
                        update.Doors = null;
                    }

                    // Transits
                    var transits = Memory.Transits;
                    if (transits is not null && transits.Count > 0)
                    {
                        var trArr = new WebRadarTransit[transits.Count];
                        for (int i = 0; i < transits.Count; i++)
                            trArr[i] = WebRadarTransit.Create(transits[i]);
                        update.Transits = trArr;
                    }
                    else
                    {
                        update.Transits = null;
                    }

                    // BTR (only emitted when active)
                    var btr = Memory.Btr;
                    update.Btr = btr is not null ? WebRadarBtr.Create(btr) : null;

                    // Airdrops
                    var airdrops = Memory.Airdrops;
                    if (airdrops is not null && airdrops.Count > 0)
                    {
                        var adArr = new WebRadarAirdrop[airdrops.Count];
                        for (int i = 0; i < airdrops.Count; i++)
                            adArr[i] = WebRadarAirdrop.Create(airdrops[i]);
                        update.Airdrops = adArr;
                    }
                    else
                    {
                        update.Airdrops = null;
                    }

                    // Live camera state (FOV / ADS / scoped) — lets the buddy
                    // aimview mirror the host's actual zoom whenever possible.
                    update.Camera = WebRadarCamera.Capture();

                    // Serialize once per tick and publish for /api/radar requests to
                    // copy out directly. This avoids JsonSerializer work on the request
                    // hot path and prevents tearing while the worker mutates _latest.
                    try
                    {
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(update, _jsonOpts);
                        Volatile.Write(ref _latestJson, bytes);
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine($"[WebRadar] Serialize error: {ex.Message}");
                    }
                }
            catch (Exception ex)
            {
                Log.WriteLine($"[WebRadar] Worker error: {ex.Message}");
            }
        }

        private static void ThrowIfPortInvalid(int port)
        {
            if (port is < 1024 or > 65535)
                throw new ArgumentException($"Invalid port: {port}. Must be between 1024 and 65535.");

            // Verify the port is available
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Any, port));
        }

        /// <summary>
        /// Returns the list of all known container types for the web UI selection list.
        /// </summary>
        private static object[] GetAvailableContainers()
        {
            var all = EftDataManager.AllContainers;
            var selected = SilkProgram.Config.SelectedContainers;
            var result = new object[all.Count];
            int idx = 0;
            foreach (var kvp in all)
            {
                result[idx++] = new
                {
                    id = kvp.Key,
                    name = kvp.Value.ShortName,
                    selected = selected.Count == 0 || selected.Contains(kvp.Key),
                };
            }
            if (idx < result.Length)
                Array.Resize(ref result, idx);
            return result;
        }

        /// <summary>
        /// Snapshot of the loot item catalog (excludes static containers) sent to the
        /// buddy web client so it can present a searchable wishlist/blacklist UI
        /// without needing live raid loot to find an item.
        /// </summary>
        private static object[] GetItemCatalog()
        {
            var all = EftDataManager.AllItems;
            var result = new object[all.Count];
            int idx = 0;
            foreach (var kvp in all)
            {
                var item = kvp.Value;
                result[idx++] = new
                {
                    bsgId = kvp.Key,
                    name = item.Name,
                    shortName = item.ShortName,
                    price = item.BestPrice,
                };
            }
            if (idx < result.Length)
                Array.Resize(ref result, idx);
            return result;
        }

        /// <summary>
        /// Returns the cached UTF-8 JSON bytes of the item catalog. Built lazily
        /// on first call and reused thereafter — the catalog is immutable after
        /// <see cref="EftDataManager"/> finishes loading.
        /// </summary>
        private static byte[] GetItemCatalogJson()
        {
            var cached = Volatile.Read(ref _itemCatalogJson);
            if (cached is not null)
                return cached;

            lock (_catalogLock)
            {
                cached = _itemCatalogJson;
                if (cached is not null)
                    return cached;

                var bytes = JsonSerializer.SerializeToUtf8Bytes(GetItemCatalog(), _jsonOpts);
                Volatile.Write(ref _itemCatalogJson, bytes);
                return bytes;
            }
        }

        // ── UPnP / NAT ────────────────────────────────────────────────────────────

        /// <summary>
        /// Discover a NAT device, trying UPnP first then NAT-PMP fallback.
        /// </summary>
        private static async Task<NatDevice?> TryDiscoverNatAsync()
        {
            try
            {
                var d = new NatDiscoverer();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                return await d.DiscoverDeviceAsync(PortMapper.Upnp, cts);
            }
            catch { /* ignore */ }

            try
            {
                var d = new NatDiscoverer();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                return await d.DiscoverDeviceAsync(PortMapper.Pmp, cts);
            }
            catch { /* ignore */ }

            return null;
        }

        /// <summary>
        /// Create a UPnP/NAT-PMP TCP port mapping.
        /// </summary>
        private static async Task<bool> TryConfigureUPnPAsync(int port)
        {
            try
            {
                var nat = await TryDiscoverNatAsync();
                if (nat is null)
                    return false;

                await nat.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, 86400, "EFT WebRadar"));
                _upnpPort = port;

                var maps = await nat.GetAllMappingsAsync();
                foreach (var m in maps)
                    Log.WriteLine($"[UPnP MAP] {m.Protocol} {m.PublicPort} -> {m.PrivateIP}:{m.PrivatePort}");

                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[WebRadar] UPnP map error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Remove a previously created UPnP/NAT-PMP port mapping (best-effort).
        /// </summary>
        private static async Task CleanupUPnPAsync(int port)
        {
            try
            {
                var nat = await TryDiscoverNatAsync();
                if (nat is null)
                    return;

                await nat.DeletePortMapAsync(new Mapping(Protocol.Tcp, port, port));
                Log.WriteLine($"[WebRadar] UPnP mapping removed for port {port}");
            }
            catch
            {
                // best-effort cleanup
            }
        }

        // ── Address Detection ──────────────────────────────────────────────────────

        /// <summary>
        /// Get the external (WAN) IP address. Tries UPnP first, then HTTP fallback services.
        /// </summary>
        public static async Task<string> GetExternalIPAsync()
        {
            var errors = new StringBuilder();

            // Try UPnP query first
            try
            {
                var nat = await TryDiscoverNatAsync();
                if (nat is not null)
                {
                    var ip = await nat.GetExternalIPAsync();
                    var ipStr = ip.ToString();
                    if (!string.IsNullOrWhiteSpace(ipStr))
                        return ipStr;
                }
            }
            catch (Exception ex)
            {
                errors.AppendLine($"[UPnP] {ex.Message}");
            }

            // HTTP fallback services
            string[] services = [
                "https://api.ipify.org",
                "https://icanhazip.com",
                "https://ifconfig.me/ip"
            ];

            foreach (var service in services)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var response = await SilkProgram.HttpClient.GetStringAsync(service, cts.Token);
                    var ip = response.Trim();
                    if (IPAddress.TryParse(ip, out _))
                        return ip;
                }
                catch (Exception ex)
                {
                    errors.AppendLine($"[{service}] {ex.Message}");
                }
            }

            throw new Exception($"Failed to obtain external IP: {errors}");
        }

        /// <summary>
        /// Get the local LAN IPv4 address of this machine.
        /// </summary>
        public static string? GetLocalIPAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());

                // Prefer private IPs first
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        var bytes = ip.GetAddressBytes();
                        if (IsPrivateIP(bytes))
                            return ip.ToString();
                    }
                }

                // Fall back to any non-loopback IPv4
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[WebRadar] GetLocalIPAddress error: {ex.Message}");
                return null;
            }
        }

        private static bool IsPrivateIP(byte[] ip)
        {
            if (ip[0] == 192 && ip[1] == 168) return true;
            if (ip[0] == 10) return true;
            if (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31) return true;
            return false;
        }
    }
}
