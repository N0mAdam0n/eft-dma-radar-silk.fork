// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using Svg.Skia;
using Catalog = eft_dma_radar.Silk.UI.Maps.TarkovDevSvgCatalog;

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// Renderer for a tarkov.dev-hosted SVG map. Loads the SVG once from the
    /// <see cref="TarkovDevMapsClient"/> cache (downloading on first use), rasterizes
    /// it to a single <see cref="SKImage"/>, and serves it through the
    /// <see cref="IRadarMap"/> draw API. Coordinate projection uses the tarkov.dev
    /// <c>baseTransform</c>+<c>coordinateRotation</c> baked into <see cref="MapConfig"/>
    /// by <see cref="Catalog.BuildConfig"/>.
    /// </summary>
    /// <remarks>
    /// V1 limitations:
    ///   <list type="bullet">
    ///     <item>Single flat layer — tarkov.dev SVGs have named <c>&lt;g&gt;</c> groups
    ///       for each floor but we render all of them at once. Floor switching can be
    ///       added later by parsing the SVG and rasterizing per-group separately.</item>
    ///     <item>Only 0°/180° rotations supported (same as <see cref="SatelliteMap"/>);
    ///       maps with 90°/270° rotation fall back to the bundled local renderer at the
    ///       catalog level — they never reach this class.</item>
    ///   </list>
    /// </remarks>
    internal sealed class TarkovDevSvgMap : IRadarMap
    {
        private readonly SKImage _image;
        private readonly float _mapWidth;
        private readonly float _mapHeight;
        private bool _disposed;

        public string ID { get; }
        public MapConfig Config { get; }

        private static readonly SKPaint _svgPaint = new() { IsAntialias = true };
        private static readonly SKPaint _paintBitmap = new() { IsAntialias = true };

        /// <summary>
        /// Constructs the renderer for an already-cached SVG. Throws if the SVG fails
        /// to rasterize — <see cref="MapManager"/> catches and falls back to the local
        /// bundled renderer in that case. The <see cref="MapConfig"/> is computed from
        /// the catalog entry's world bounds and the rasterized image dimensions.
        /// </summary>
        public TarkovDevSvgMap(string id, MapConfig svgConfig, Catalog.Entry entry, string svgPath, int rotationDeg)
        {
            ID = id;

            _image = RasterizeSvg(svgPath, rotationDeg)
                ?? throw new InvalidOperationException($"Failed to rasterize SVG '{svgPath}' for map '{id}'.");
            _mapWidth = _image.Width;
            _mapHeight = _image.Height;

            // Build the projection-space config now that we know the actual image dims.
            Config = Catalog.BuildConfig(id, svgConfig, entry, _image.Width, _image.Height, rotationDeg);

            Log.WriteLine($"[TarkovDevSvgMap] '{id}' loaded: image={_mapWidth}x{_mapHeight}, rotation={rotationDeg}°, bounds=lng[{entry.LngMin}..{entry.LngMax}] lat[{entry.LatMin}..{entry.LatMax}] → cfg X={Config.X:0.00} Y={Config.Y:0.00} Scale={Config.Scale:0.0000}");
        }

        /// <summary>
        /// Loads the SVG file and rasterizes the entire picture to an SKImage on the CPU.
        /// Applies a CCW rotation (0, 90, 180, or 270 degrees) so the user can pick the
        /// orientation they prefer for tarkov.dev SVGs. The catalog's BuildConfig
        /// produces a MapConfig that compensates the projection accordingly so markers
        /// land on the correct rotated pixels.
        /// </summary>
        private static SKImage? RasterizeSvg(string svgPath, int rotationDeg)
        {
            try
            {
                using var stream = File.OpenRead(svgPath);
                using var svg = SKSvg.CreateFromStream(stream);
                if (svg is null) return null;

                var picture = svg.Picture;
                if (picture is null) return null;

                var cull = picture.CullRect;
                if (cull.Width <= 0 || cull.Height <= 0) return null;

                int rawW = (int)cull.Width;
                int rawH = (int)cull.Height;

                // 90° / 270° rotations swap the bitmap dimensions; 0° / 180° keep them.
                bool swap = rotationDeg == 90 || rotationDeg == 270;
                int outW = swap ? rawH : rawW;
                int outH = swap ? rawW : rawH;

                var info = new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);

                using var surface = SKSurface.Create(info);
                if (surface is null)
                {
                    Log.WriteLine($"[TarkovDevSvgMap] SKSurface.Create failed ({outW}x{outH}) for '{svgPath}'");
                    return null;
                }

                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);

                // CCW rotation by R degrees around image center. Translation places the
                // post-rotation bounding box inside (0, 0, outW, outH).
                switch (rotationDeg)
                {
                    case 90:
                        canvas.Translate(0, outH);
                        canvas.RotateDegrees(-90f); // CCW
                        break;
                    case 180:
                        canvas.Translate(outW, outH);
                        canvas.RotateDegrees(180f);
                        break;
                    case 270:
                        canvas.Translate(outW, 0);
                        canvas.RotateDegrees(90f); // CCW 270° == CW 90°
                        break;
                    // 0°: no transform
                }

                canvas.DrawPicture(picture, _svgPaint);
                return surface.Snapshot();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[TarkovDevSvgMap] Rasterize failed for '{svgPath}': {ex.Message}");
                return null;
            }
        }

        public void Draw(SKCanvas canvas, float playerHeight, SKRect mapBounds, SKRect windowBounds)
        {
            // Draw the SVG image at its exact projected position on the canvas.
            // mapBounds is in image-pixel space (centered on player), so the image's
            // (0,0)→(width,height) corners map to a specific dst rect on the canvas.
            // This avoids SkiaSharp's ambiguous behavior when src extends past the
            // image bounds (which can happen when the player is near the image edge).
            float sx = windowBounds.Width  / mapBounds.Width;
            float sy = windowBounds.Height / mapBounds.Height;
            var imageDst = new SKRect(
                (0           - mapBounds.Left) * sx + windowBounds.Left,
                (0           - mapBounds.Top ) * sy + windowBounds.Top,
                (_mapWidth   - mapBounds.Left) * sx + windowBounds.Left,
                (_mapHeight  - mapBounds.Top ) * sy + windowBounds.Top);
            canvas.DrawImage(_image, imageDst, _paintBitmap);
        }

        public MapParams GetParameters(SKSize canvasSize, int zoom, ref Vector2 centerMapPos)
        {
            zoom = Math.Clamp(zoom, 1, 800);

            float zoomMul = 0.01f * zoom;
            float zoomWidth = _mapWidth * zoomMul;
            float zoomHeight = _mapHeight * zoomMul;

            var bounds = new SKRect(
                centerMapPos.X - zoomWidth * 0.5f,
                centerMapPos.Y - zoomHeight * 0.5f,
                centerMapPos.X + zoomWidth * 0.5f,
                centerMapPos.Y + zoomHeight * 0.5f);

            bounds = AspectFill(bounds, canvasSize);

            return new MapParams(
                Config,
                bounds,
                canvasSize.Width / bounds.Width,
                canvasSize.Height / bounds.Height);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKRect AspectFill(SKRect rect, SKSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return rect;

            float rectAspect = rect.Width / rect.Height;
            float targetAspect = size.Width / size.Height;

            float cx = rect.MidX;
            float cy = rect.MidY;
            float hw, hh;

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
            _image.Dispose();
        }
    }
}
