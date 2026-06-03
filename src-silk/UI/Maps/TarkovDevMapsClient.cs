// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Net.Http;

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// Downloads and persistently caches tarkov.dev map assets — both the upstream
    /// <c>maps.json</c> (re-fetched weekly) and per-map SVG files (one-time download
    /// per map). Cache root:
    /// <c>%AppData%\eft-dma-radar-silk\tarkov-dev-maps\{maps.json, svg\{NameId}.svg}</c>.
    /// </summary>
    /// <remarks>
    /// Falls back to <see cref="TarkovDevSvgCatalog"/>'s committed defaults whenever a
    /// fetch fails — the user can still render any catalog-known map offline as long
    /// as the SVG was downloaded once on a previous online session.
    /// </remarks>
    internal static class TarkovDevMapsClient
    {
        private const string MapsJsonUrl =
            "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json";
        private static readonly TimeSpan MapsJsonMaxAge = TimeSpan.FromDays(7);

        private static readonly string CacheDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "eft-dma-radar-silk",
                "tarkov-dev-maps");

        private static readonly string SvgDir = Path.Combine(CacheDir, "svg");
        private static readonly string MapsJsonPath = Path.Combine(CacheDir, "maps.json");

        // Single shared HttpClient — never disposed (per Microsoft guidance for static clients).
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("eft-dma-radar-silk/1.0 (+map-fetch)");
            return c;
        }

        // ── SVG retrieval ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the absolute path to the cached SVG for <paramref name="entry"/>,
        /// downloading it on cache miss. Returns <see langword="null"/> if the SVG
        /// cannot be obtained (offline + no cached copy).
        /// </summary>
        public static string? EnsureSvgCached(TarkovDevSvgCatalog.Entry entry)
        {
            try
            {
                Directory.CreateDirectory(SvgDir);
                var path = Path.Combine(SvgDir, $"{entry.NameId}.svg");
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return path;

                Log.WriteLine($"[TarkovDevMaps] Fetching SVG {entry.NameId} ← {entry.SvgUrl}");
                using var resp = _http.GetAsync(entry.SvgUrl).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length == 0)
                {
                    Log.WriteLine($"[TarkovDevMaps] Empty SVG response for {entry.NameId}");
                    return null;
                }

                // Atomic write — temp + move so partial download never replaces a good cache.
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
                Log.WriteLine($"[TarkovDevMaps] Cached {entry.NameId}.svg ({bytes.Length:N0} bytes)");
                return path;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[TarkovDevMaps] Failed to fetch SVG for {entry.NameId}: {ex.Message}");
                // If there's any prior cache, fall back to it.
                var path = Path.Combine(SvgDir, $"{entry.NameId}.svg");
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return path;
                return null;
            }
        }

        // ── maps.json retrieval ─────────────────────────────────────────────────
        // Currently optional: the catalog's bundled transforms are sufficient to render.
        // Exposed for future override of stale catalog values without a rebuild.

        /// <summary>
        /// Returns the absolute path to the cached <c>maps.json</c>, refreshing it
        /// from GitHub when older than <see cref="MapsJsonMaxAge"/>. Returns
        /// <see langword="null"/> if the cache miss + fetch both fail.
        /// </summary>
        public static string? EnsureMapsJsonCached()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                bool fresh = File.Exists(MapsJsonPath)
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(MapsJsonPath) < MapsJsonMaxAge;
                if (fresh)
                    return MapsJsonPath;

                Log.WriteLine($"[TarkovDevMaps] Fetching maps.json ← {MapsJsonUrl}");
                using var resp = _http.GetAsync(MapsJsonUrl).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length == 0) return File.Exists(MapsJsonPath) ? MapsJsonPath : null;

                var tmp = MapsJsonPath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, MapsJsonPath, overwrite: true);
                Log.WriteLine($"[TarkovDevMaps] Cached maps.json ({bytes.Length:N0} bytes)");
                return MapsJsonPath;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[TarkovDevMaps] Failed to fetch maps.json: {ex.Message}");
                return File.Exists(MapsJsonPath) ? MapsJsonPath : null;
            }
        }

        /// <summary>Cache directory (created lazily). Exposed for diagnostics / "open in Explorer" UI.</summary>
        public static string CacheDirectory => CacheDir;
    }
}
