// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.UI.Maps;

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Converts a raw SVG <see cref="MapConfig"/> (plus optional satellite catalog
    /// entry) into a <see cref="WebRadarMapInfo"/> payload for the web client.
    /// Always emits the SVG projection so the buddy can render the SVG map even
    /// when the host has the local satellite toggle on; satellite tile metadata
    /// is attached as an opt-in side-car.
    /// </summary>
    internal static class WebRadarMapConverter
    {
        // Cache the last conversion. MapConfig is immutable; reference equality is
        // sufficient to detect a map switch, and avoids rebuilding the layer list
        // every web-radar tick. Cache key combines the raw config and the satellite
        // config (latter may be null) so toggling host satellite mode mid-raid
        // doesn't poison the cache.
        private static MapConfig? _cachedRaw;
        private static MapConfig? _cachedSatConfig;
        private static WebRadarMapInfo? _cachedInfo;

        public static WebRadarMapInfo? Convert(
            MapConfig? rawCfg,
            MapConfig? satCfg,
            SatelliteMapCatalog.Entry? satEntry)
        {
            if (rawCfg is null)
                return null;

            if (ReferenceEquals(rawCfg, _cachedRaw)
                && ReferenceEquals(satCfg, _cachedSatConfig))
                return _cachedInfo;

            var info = new WebRadarMapInfo
            {
                Name = rawCfg.Name,
                MapId = rawCfg.MapID.FirstOrDefault() ?? string.Empty,
                OriginX = rawCfg.X,
                OriginY = rawCfg.Y,
                Scale = rawCfg.Scale,
                SvgScale = rawCfg.SvgScale,
                DisableDimming = rawCfg.DisableDimming,
                Layers = rawCfg.MapLayers.Select(static l => new WebRadarMapLayer
                {
                    MinHeight = l.MinHeight,
                    MaxHeight = l.MaxHeight,
                    DimBaseLayer = l.DimBaseLayer,
                    Filename = l.Filename
                }).ToList()
            };

            if (satCfg is not null && satEntry is not null)
            {
                info.Satellite = new WebRadarSatelliteInfo
                {
                    OriginX = satCfg.X,
                    OriginY = satCfg.Y,
                    Scale = satCfg.Scale,
                    SvgScale = satCfg.SvgScale,
                    // Route through the host's own tile proxy (same origin → no CORS).
                    // The proxy resolves cacheKey back to the upstream tarkov.dev URL.
                    TileUrlTemplate = $"/api/tile/{satEntry.CacheKey}/{{z}}/{{x}}/{{y}}.png",
                    TileSize = satEntry.TileSize,
                    MinZoom = satEntry.MinZoom,
                    MaxZoom = satEntry.MaxZoom,
                    LoadZoom = satEntry.LoadZoom,
                    BoundsLat1 = satEntry.BoundsLat1,
                    BoundsLng1 = satEntry.BoundsLng1,
                    BoundsLat2 = satEntry.BoundsLat2,
                    BoundsLng2 = satEntry.BoundsLng2,
                };
            }

            _cachedRaw = rawCfg;
            _cachedSatConfig = satCfg;
            _cachedInfo = info;
            return info;
        }
    }
}
