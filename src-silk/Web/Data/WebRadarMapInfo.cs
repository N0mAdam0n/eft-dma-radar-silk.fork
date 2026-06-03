// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Map geometry snapshot for the web radar client.
    /// Always describes the SVG projection; satellite tile metadata (when supported)
    /// is provided in <see cref="Satellite"/> so the buddy can opt in independently
    /// of the host's local satellite toggle.
    /// </summary>
    public sealed class WebRadarMapInfo
    {
        public string Name { get; set; } = string.Empty;
        public string MapId { get; set; } = string.Empty;

        // SVG projection (default for the web client)
        public float OriginX { get; set; }
        public float OriginY { get; set; }
        public float Scale { get; set; }
        public float SvgScale { get; set; }
        public bool DisableDimming { get; set; }

        public List<WebRadarMapLayer>? Layers { get; set; }

        /// <summary>
        /// Tarkov.dev satellite-tile projection + tile metadata when the current map
        /// is supported by the satellite catalog. <see langword="null"/> otherwise.
        /// </summary>
        public WebRadarSatelliteInfo? Satellite { get; set; }
    }

    /// <summary>
    /// A single height-constrained map layer.
    /// </summary>
    public sealed class WebRadarMapLayer
    {
        public float? MinHeight { get; set; }
        public float? MaxHeight { get; set; }
        public bool DimBaseLayer { get; set; }
        public string Filename { get; set; } = string.Empty;
    }

    /// <summary>
    /// Satellite-tile projection + CDN tile metadata for buddy-side rendering.
    /// </summary>
    public sealed class WebRadarSatelliteInfo
    {
        // Satellite-space projection (mirrors SatelliteMapCatalog.BuildConfig output)
        public float OriginX { get; set; }
        public float OriginY { get; set; }
        public float Scale { get; set; }
        public float SvgScale { get; set; }

        // Tile metadata (mirrors SatelliteMapCatalog.Entry)
        public string TileUrlTemplate { get; set; } = string.Empty;
        public int TileSize { get; set; }
        public int MinZoom { get; set; }
        public int MaxZoom { get; set; }
        public int LoadZoom { get; set; }

        // Catalog bounds (used to derive default tile-coverage rect at LoadZoom)
        public float BoundsLat1 { get; set; }
        public float BoundsLng1 { get; set; }
        public float BoundsLat2 { get; set; }
        public float BoundsLng2 { get; set; }
    }
}
