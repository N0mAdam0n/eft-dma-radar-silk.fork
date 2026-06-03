// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// Static catalog mapping Silk in-game map IDs to tarkov.dev satellite tile metadata.
    /// Source: <c>https://github.com/the-hideout/tarkov-dev/blob/main/src/data/maps.json</c>.
    /// </summary>
    /// <remarks>
    /// Coordinate convention (tarkov.dev / Leaflet CRS.Simple with optional rotation):
    /// <code>
    ///   latLng       = (lat = worldZ, lng = worldX)
    ///   rotated      = R(rotation)·latLng
    ///   pixelAtZ0    = (scaleX·rotated.lng + marginX, -scaleY·rotated.lat + marginY)
    ///   pixelAtZoom  = pixelAtZ0 · 2^z
    /// </code>
    /// Only 0° and 180° rotations are supported by the satellite renderer because the existing
    /// <see cref="MapParams.ToMapPos"/> math is axis-aligned (single shared scale, Y inverted).
    /// Maps with 90°/270° rotation (Factory, Labs, Labyrinth) silently fall back to SVG.
    /// </remarks>
    internal static class SatelliteMapCatalog
    {
        /// <summary>Tarkov.dev satellite-tile entry for a single in-game map.</summary>
        internal sealed record Entry(
            string CacheKey,
            string TileUrlTemplate,
            int TileSize,
            int MinZoom,
            int MaxZoom,
            int LoadZoom,
            float ScaleX,
            float ScaleY,
            float MarginX,
            float MarginY,
            int Rotation,
            float BoundsLat1,
            float BoundsLng1,
            float BoundsLat2,
            float BoundsLng2);

        // Silk map IDs → tarkov.dev tile entries. Only includes 0°/180°-rotation maps.
        private static readonly FrozenDictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
            {
                // Woods — woods/main_0.16, rotation 180, transform=[0.1855, 112.95, 0.1855, 167.85]
                ["woods"] = new Entry(
                    CacheKey: "woods_main_0.16",
                    TileUrlTemplate: "https://assets.tarkov.dev/maps/woods/main_0.16/{z}/{x}/{y}.png",
                    TileSize: 256, MinZoom: 2, MaxZoom: 6, LoadZoom: 6,
                    ScaleX: 0.1855f, ScaleY: 0.1855f, MarginX: 112.95f, MarginY: 167.85f,
                    Rotation: 180,
                    BoundsLat1: 646f, BoundsLng1: -914f, BoundsLat2: -761f, BoundsLng2: 442f),

                // Customs — bigmap → customs_0.16/main, rotation 180
                ["bigmap"] = new Entry(
                    CacheKey: "customs_0.16_main",
                    TileUrlTemplate: "https://assets.tarkov.dev/maps/customs_0.16/main/{z}/{x}/{y}.png",
                    TileSize: 256, MinZoom: 2, MaxZoom: 6, LoadZoom: 6,
                    ScaleX: 0.239f, ScaleY: 0.239f, MarginX: 168.65f, MarginY: 136.35f,
                    Rotation: 180,
                    BoundsLat1: 698f, BoundsLng1: -307f, BoundsLat2: -372f, BoundsLng2: 237f),

                // Interchange, rotation 180
                ["interchange"] = new Entry(
                    CacheKey: "interchange_main",
                    TileUrlTemplate: "https://assets.tarkov.dev/maps/interchange/main/{z}/{x}/{y}.png",
                    TileSize: 256, MinZoom: 1, MaxZoom: 6, LoadZoom: 6,
                    ScaleX: 0.265f, ScaleY: 0.265f, MarginX: 150.6f, MarginY: 134.6f,
                    Rotation: 180,
                    BoundsLat1: 598f, BoundsLng1: -442f, BoundsLat2: -433f, BoundsLng2: 426f),

                // Reserve — rezervbase, rotation 180
                ["rezervbase"] = new Entry(
                    CacheKey: "reserve_main",
                    TileUrlTemplate: "https://assets.tarkov.dev/maps/reserve/main/{z}/{x}/{y}.png",
                    TileSize: 256, MinZoom: 2, MaxZoom: 6, LoadZoom: 6,
                    ScaleX: 0.395f, ScaleY: 0.395f, MarginX: 122.0f, MarginY: 137.65f,
                    Rotation: 180,
                    BoundsLat1: 289f, BoundsLng1: -293f, BoundsLat2: -303f, BoundsLng2: 244f),

                // Shoreline, rotation 180
                ["shoreline"] = new Entry(
                    CacheKey: "shoreline_main_summer",
                    TileUrlTemplate: "https://assets.tarkov.dev/maps/shoreline/main_summer/{z}/{x}/{y}.png",
                    TileSize: 256, MinZoom: 2, MaxZoom: 6, LoadZoom: 6,
                    ScaleX: 0.16f, ScaleY: 0.16f, MarginX: 83.2f, MarginY: 111.1f,
                    Rotation: 180,
                    BoundsLat1: 504f, BoundsLng1: -415f, BoundsLat2: -1056f, BoundsLng2: 618f),

                // Ground Zero — sandbox / sandbox_high, rotation 180
                ["sandbox"] = new Entry(
                    CacheKey: "groundzero_main_summer",
                    TileUrlTemplate: "https://assets.tarkov.dev/maps/groundzero/main_summer/{z}/{x}/{y}.png",
                    TileSize: 256, MinZoom: 1, MaxZoom: 6, LoadZoom: 6,
                    ScaleX: 0.524f, ScaleY: 0.524f, MarginX: 167.3f, MarginY: 65.1f,
                    Rotation: 180,
                    BoundsLat1: 249f, BoundsLng1: -124f, BoundsLat2: -99f, BoundsLng2: 364f),
                ["sandbox_high"] = new Entry(
                    CacheKey: "groundzero_main_summer",
                    TileUrlTemplate: "https://assets.tarkov.dev/maps/groundzero/main_summer/{z}/{x}/{y}.png",
                    TileSize: 256, MinZoom: 1, MaxZoom: 6, LoadZoom: 6,
                    ScaleX: 0.524f, ScaleY: 0.524f, MarginX: 167.3f, MarginY: 65.1f,
                    Rotation: 180,
                    BoundsLat1: 249f, BoundsLng1: -124f, BoundsLat2: -99f, BoundsLng2: 364f),
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string mapId, out Entry entry) =>
            _entries.TryGetValue(mapId, out entry!);

        public static bool IsSupported(string mapId) => _entries.ContainsKey(mapId);

        /// <summary>
        /// Resolves a tile-URL template by its catalog <see cref="Entry.CacheKey"/>.
        /// Used by the web radar's tile proxy so buddies can request tiles by a
        /// stable key without exposing the upstream CDN URL — and so we can reject
        /// path-traversal attempts before they reach <see cref="TileCache"/>.
        /// </summary>
        public static bool TryGetTemplateByCacheKey(string cacheKey, out string template)
        {
            foreach (var kv in _entries)
            {
                if (string.Equals(kv.Value.CacheKey, cacheKey, StringComparison.Ordinal))
                {
                    template = kv.Value.TileUrlTemplate;
                    return true;
                }
            }
            template = string.Empty;
            return false;
        }

        /// <summary>
        /// Builds a <see cref="MapConfig"/> for satellite rendering: encodes the
        /// tarkov.dev transform into <see cref="MapConfig.X"/>/<see cref="MapConfig.Y"/>/
        /// <see cref="MapConfig.Scale"/>/<see cref="MapConfig.SvgScale"/> so the existing
        /// <see cref="MapParams.ToMapPos"/> math projects world → tile-pixel correctly.
        /// </summary>
        public static MapConfig BuildConfig(string mapId, MapConfig svgConfig, Entry e)
        {
            float svgScale = 1 << e.LoadZoom; // 2^loadZoom
            float scale = e.ScaleX;
            // tarkov.dev's `coordinateRotation` is already baked into the rendered
            // tile bitmaps. To put entity projection into the same coordinate space
            // as the tiles we negate Scale for 180°-rotated maps. ToMapPos already
            // negates Z, so flipping Scale flips both axes for free.
            if (e.Rotation == 180)
                scale = -scale;

            return new MapConfig
            {
                MapID = svgConfig.MapID,
                X = e.MarginX,
                Y = e.MarginY,
                Scale = scale,
                SvgScale = svgScale,
                DisableDimming = true,
                MapLayers = svgConfig.MapLayers,
            };
        }
    }
}
