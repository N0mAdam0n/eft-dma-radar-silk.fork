// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Concurrent;
using Catalog = eft_dma_radar.Silk.UI.Maps.SatelliteMapCatalog;

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// Adaptive tile-pyramid map renderer that fetches PNG tiles from
    /// <c>assets.tarkov.dev</c>. The entity-projection space is fixed at the
    /// catalog's <see cref="Catalog.Entry.LoadZoom"/>, but tiles are loaded and
    /// drawn from whatever pyramid level best matches the current screen scale,
    /// so we don't pull MaxZoom imagery when zoomed all the way out.
    /// Tiles load lazily on-demand for the visible area only.
    /// </summary>
    internal sealed class SatelliteMap : IRadarMap
    {
        private readonly Catalog.Entry _entry;
        private readonly int _baseZ;                 // projection / mapPos zoom (== entry.LoadZoom)
        private readonly int _minTileX, _maxTileX;   // tile coverage at _baseZ
        private readonly int _minTileY, _maxTileY;
        private readonly float _mapWidth, _mapHeight;

        // Tile cache keyed by (z, x, y) packed into a long.
        private readonly ConcurrentDictionary<long, SKImage?> _tiles = new();
        // Tracks in-flight loads so we don't fire duplicates per frame.
        private readonly ConcurrentDictionary<long, byte> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _disposed;

        public string ID { get; }
        public MapConfig Config { get; }

        private static readonly SKPaint _paint = new() { IsAntialias = false };
        private static readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
        private static readonly SKPaint _bgPaint = new() { Color = new SKColor(20, 20, 24), Style = SKPaintStyle.Fill };

        public SatelliteMap(string id, MapConfig satConfig, Catalog.Entry entry)
        {
            ID = id;
            Config = satConfig;
            _entry = entry;
            _baseZ = entry.LoadZoom;

            int n = 1 << _baseZ;
            int ts = entry.TileSize;

            // Map-pixel bounds at LoadZoom: project the catalog's lat/lng bounds
            // through the same transform Config uses (svgScale = 2^LoadZoom).
            var p1 = MapParams.ToMapPos(new Vector3(entry.BoundsLng1, 0f, entry.BoundsLat1), satConfig);
            var p2 = MapParams.ToMapPos(new Vector3(entry.BoundsLng2, 0f, entry.BoundsLat2), satConfig);

            float minPx = MathF.Min(p1.X, p2.X);
            float maxPx = MathF.Max(p1.X, p2.X);
            float minPy = MathF.Min(p1.Y, p2.Y);
            float maxPy = MathF.Max(p1.Y, p2.Y);

            _minTileX = Math.Clamp((int)Math.Floor(minPx / ts), 0, n - 1);
            _maxTileX = Math.Clamp((int)Math.Floor(maxPx / ts), 0, n - 1);
            _minTileY = Math.Clamp((int)Math.Floor(minPy / ts), 0, n - 1);
            _maxTileY = Math.Clamp((int)Math.Floor(maxPy / ts), 0, n - 1);

            _mapWidth  = n * ts;
            _mapHeight = n * ts;

            // Kick off an async preload of the lowest-zoom tiles so something is
            // visible immediately even before the user pans/zooms.
            _ = Task.Run(() => PreloadLowZoomAsync(_entry.MinZoom));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long Key(int z, int x, int y) =>
            ((long)z << 56) | ((long)(uint)x << 28) | (uint)y;

        private async Task PreloadLowZoomAsync(int z)
        {
            z = Math.Clamp(z, _entry.MinZoom, _entry.MaxZoom);
            int n = 1 << z;
            var tasks = new List<Task>();
            for (int tx = 0; tx < n; tx++)
            for (int ty = 0; ty < n; ty++)
                tasks.Add(EnsureTileAsync(z, tx, ty));

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.WriteLine($"[SatelliteMap] Preload error: {ex.Message}"); }
        }

        private Task EnsureTileAsync(int z, int x, int y)
        {
            long k = Key(z, x, y);
            if (_tiles.ContainsKey(k)) return Task.CompletedTask;
            if (!_pending.TryAdd(k, 0)) return Task.CompletedTask;

            return Task.Run(async () =>
            {
                try
                {
                    var bytes = await TileCache.GetTileAsync(
                        _entry.TileUrlTemplate, _entry.CacheKey, z, x, y, _cts.Token)
                        .ConfigureAwait(false);

                    SKImage? img = null;
                    if (bytes is not null && !_disposed)
                        img = SKImage.FromEncodedData(bytes);

                    if (_disposed)
                    {
                        img?.Dispose();
                        return;
                    }

                    // Store even nulls so we don't retry 404s forever this session.
                    if (!_tiles.TryAdd(k, img))
                        img?.Dispose();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.WriteLine($"[SatelliteMap] Tile {z}/{x}/{y} failed: {ex.Message}");
                }
                finally
                {
                    _pending.TryRemove(k, out _);
                }
            });
        }

        /// <summary>
        /// Picks the pyramid zoom level that gives roughly 1:1 (or slightly
        /// oversampled) tile pixels on screen, given the current worldâ†’screen
        /// scale at the base projection zoom.
        /// </summary>
        private int PickZoom(float screenPixelsPerBasePixel)
        {
            // A tile at zoom z covers (TileSize << (_baseZ - z)) base-pixels.
            // On screen each tile then renders as
            //   screenSize = TileSize * (1 << (_baseZ - z)) * screenPixelsPerBasePixel
            // We want screenSize >= TileSize, i.e.
            //   (1 << (_baseZ - z)) * screenPixelsPerBasePixel >= 1
            //   _baseZ - z >= -log2(screenPixelsPerBasePixel)
            //   z <= _baseZ + log2(screenPixelsPerBasePixel)
            if (screenPixelsPerBasePixel <= 0f) return _entry.MinZoom;
            float log2 = MathF.Log2(screenPixelsPerBasePixel);
            int z = _baseZ + (int)MathF.Ceiling(log2);
            return Math.Clamp(z, _entry.MinZoom, _entry.MaxZoom);
        }

        public void Draw(SKCanvas canvas, float playerHeight, SKRect mapBounds, SKRect windowBounds)
        {
            if (_disposed) return;

            canvas.DrawRect(windowBounds, _bgPaint);

            int baseTs = _entry.TileSize;
            float sx = windowBounds.Width  / mapBounds.Width;
            float sy = windowBounds.Height / mapBounds.Height;

            int z = PickZoom(sx); // sx ~ sy (AspectFill keeps them equal)
            int zShift = _baseZ - z;          // >= 0 â€” base px per tile-px
            int tilePxAtBase = baseTs << zShift; // size of one tile in base mapPos px

            int firstX = Math.Max(0, (int)Math.Floor(mapBounds.Left   / tilePxAtBase));
            int lastX  = Math.Min((1 << z) - 1, (int)Math.Floor(mapBounds.Right  / tilePxAtBase));
            int firstY = Math.Max(0, (int)Math.Floor(mapBounds.Top    / tilePxAtBase));
            int lastY  = Math.Min((1 << z) - 1, (int)Math.Floor(mapBounds.Bottom / tilePxAtBase));

            for (int tx = firstX; tx <= lastX; tx++)
            for (int ty = firstY; ty <= lastY; ty++)
            {
                long k = Key(z, tx, ty);
                if (!_tiles.TryGetValue(k, out var img))
                {
                    _ = EnsureTileAsync(z, tx, ty);

                    // Fallback: try to draw a coarser tile while this one loads.
                    DrawFallback(canvas, tx, ty, z, tilePxAtBase, mapBounds, windowBounds, sx, sy);
                    continue;
                }
                if (img is null) continue;

                float px = tx * tilePxAtBase;
                float py = ty * tilePxAtBase;
                float screenLeft   = (px                  - mapBounds.Left) * sx + windowBounds.Left;
                float screenTop    = (py                  - mapBounds.Top ) * sy + windowBounds.Top;
                float screenRight  = (px + tilePxAtBase   - mapBounds.Left) * sx + windowBounds.Left;
                float screenBottom = (py + tilePxAtBase   - mapBounds.Top ) * sy + windowBounds.Top;

                var dst = new SKRect(screenLeft, screenTop, screenRight, screenBottom);
                canvas.DrawImage(img, dst, _sampling, _paint);
            }
        }

        /// <summary>
        /// Walks up the pyramid (lower z) looking for an already-loaded ancestor
        /// tile to draw as a low-res placeholder for the missing tile at (z,tx,ty).
        /// </summary>
        private void DrawFallback(SKCanvas canvas, int tx, int ty, int z, int tilePxAtBase,
                                  SKRect mapBounds, SKRect windowBounds, float sx, float sy)
        {
            int curX = tx, curY = ty, curZ = z;
            while (curZ > _entry.MinZoom)
            {
                curZ--;
                curX >>= 1;
                curY >>= 1;
                long k = Key(curZ, curX, curY);
                if (!_tiles.TryGetValue(k, out var img) || img is null)
                    continue;

                // Crop the ancestor tile to the sub-region representing (tx,ty,z).
                int levels = z - curZ;
                int sub = 1 << levels;
                int subX = tx & (sub - 1);
                int subY = ty & (sub - 1);
                float srcSize = (float)_entry.TileSize / sub;
                var src = new SKRect(
                    subX * srcSize, subY * srcSize,
                    (subX + 1) * srcSize, (subY + 1) * srcSize);

                float px = tx * tilePxAtBase;
                float py = ty * tilePxAtBase;
                var dst = new SKRect(
                    (px                - mapBounds.Left) * sx + windowBounds.Left,
                    (py                - mapBounds.Top ) * sy + windowBounds.Top,
                    (px + tilePxAtBase - mapBounds.Left) * sx + windowBounds.Left,
                    (py + tilePxAtBase - mapBounds.Top ) * sy + windowBounds.Top);
                canvas.DrawImage(img, src, dst, _sampling, _paint);
                return;
            }
        }

        public MapParams GetParameters(SKSize canvasSize, int zoom, ref Vector2 centerMapPos)
        {
            zoom = Math.Clamp(zoom, 1, 800);

            if (centerMapPos == default)
            {
                centerMapPos = new Vector2(
                    (_minTileX + _maxTileX + 1) * 0.5f * _entry.TileSize,
                    (_minTileY + _maxTileY + 1) * 0.5f * _entry.TileSize);
            }

            float zoomMul = 0.01f * zoom;
            float zw = _mapWidth  * zoomMul;
            float zh = _mapHeight * zoomMul;

            var bounds = new SKRect(
                centerMapPos.X - zw * 0.5f,
                centerMapPos.Y - zh * 0.5f,
                centerMapPos.X + zw * 0.5f,
                centerMapPos.Y + zh * 0.5f);

            bounds = AspectFill(bounds, canvasSize);

            return new MapParams(
                Config,
                bounds,
                canvasSize.Width  / bounds.Width,
                canvasSize.Height / bounds.Height);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKRect AspectFill(SKRect rect, SKSize size)
        {
            if (size.Width <= 0 || size.Height <= 0) return rect;
            float rectAspect = rect.Width / rect.Height;
            float targetAspect = size.Width / size.Height;
            float cx = rect.MidX, cy = rect.MidY, hw, hh;
            if (rectAspect > targetAspect)
            {
                hw = rect.Width * 0.5f;
                hh = hw / targetAspect;
            }
            else
            {
                hh = rect.Height * 0.5f;
                hw = hh * targetAspect;
            }
            return new SKRect(cx - hw, cy - hh, cx + hw, cy + hh);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            foreach (var img in _tiles.Values)
                img?.Dispose();
            _tiles.Clear();
            _pending.Clear();
            _cts.Dispose();
        }
    }
}
