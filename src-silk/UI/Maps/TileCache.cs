// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;
using System.Net.Http;

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// HTTP fetch + on-disk cache for satellite map tiles served by
    /// <c>https://assets.tarkov.dev/maps/...</c>. Concurrent downloads are throttled
    /// by a shared semaphore. Cached files live under
    /// <c>%LocalAppData%\eft-dma-radar-silk\tilecache</c>.
    /// </summary>
    internal static class TileCache
    {
        private static readonly HttpClient _http = CreateHttpClient();
        private static readonly SemaphoreSlim _gate = new(8, 8);

        private static readonly string _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eft-dma-radar-silk",
            "tilecache");

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };
            var c = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("eft-dma-radar-silk/1.0");
            return c;
        }

        /// <summary>
        /// Downloads a tile (or returns the cached PNG bytes). <paramref name="urlTemplate"/>
        /// must contain <c>{z}</c>, <c>{x}</c>, <c>{y}</c> placeholders.
        /// </summary>
        /// <returns>Encoded PNG bytes, or <see langword="null"/> if the tile is unavailable.</returns>
        public static async Task<byte[]?> GetTileAsync(
            string urlTemplate,
            string cacheKey,
            int z,
            int x,
            int y,
            CancellationToken ct)
        {
            string dir = Path.Combine(_cacheRoot, cacheKey, z.ToString());
            string path = Path.Combine(dir, $"{x}_{y}.png");

            if (File.Exists(path))
            {
                try { return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
                catch { /* fall through to re-download */ }
            }

            string url = urlTemplate
                .Replace("{z}", z.ToString())
                .Replace("{x}", x.ToString())
                .Replace("{y}", y.ToString());

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                        Log.WriteLine($"[TileCache] {(int)resp.StatusCode} {url}");
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

                try
                {
                    Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[TileCache] Cache write failed for {path}: {ex.Message}");
                }

                return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[TileCache] Fetch failed {url}: {ex.Message}");
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
