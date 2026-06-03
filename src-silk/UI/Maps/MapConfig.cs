// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// JSON-deserializable map configuration.
    /// </summary>
    internal sealed class MapConfig
    {
        [JsonPropertyName("mapID")]
        public List<string> MapID { get; init; } = [];

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("scale")]
        public float Scale { get; set; }

        [JsonPropertyName("svgScale")]
        public float SvgScale { get; init; } = 1f;

        [JsonPropertyName("disableDimming")]
        public bool DisableDimming { get; init; }

        /// <summary>
        /// Visual rotation applied to the rasterized map bitmap, in degrees.
        /// Supported values: 0, 90, 180, 270. Used by tarkov.dev SVG maps so the
        /// projection (<see cref="MapParams.ToMapPos"/>) swaps/negates axes to
        /// match the rotated bitmap. Local SVGs / satellite tiles leave this at 0
        /// and encode rotation by sign-flipping <see cref="Scale"/> instead.
        /// </summary>
        [JsonPropertyName("rotation")]
        public int Rotation { get; init; }

        [JsonPropertyName("mapLayers")]
        public List<MapLayer> MapLayers { get; init; } = [];

        /// <summary>
        /// Display name derived from the primary map ID.
        /// </summary>
        [JsonIgnore]
        public string Name => MapID.Count > 0 && _names.TryGetValue(MapID[0], out var n) ? n : MapID.FirstOrDefault() ?? "Unknown";

        private static readonly FrozenDictionary<string, string> _names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bigmap"]          = "Customs",
                ["interchange"]     = "Interchange",
                ["rezervbase"]      = "Reserve",
                ["woods"]           = "Woods",
                ["lighthouse"]      = "Lighthouse",
                ["shoreline"]       = "Shoreline",
                ["tarkovstreets"]   = "Streets of Tarkov",
                ["sandbox"]         = "Ground Zero",
                ["sandbox_high"]    = "Ground Zero (High)",
                ["factory4_day"]    = "Factory (Day)",
                ["factory4_night"]  = "Factory (Night)",
                ["laboratory"]      = "The Lab",
                ["terminal"]        = "Terminal",
                ["suburbs"]         = "Suburbs",
                ["city"]            = "City",
                ["labyrinth"]       = "Labyrinth",
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A single height-constrained layer within a map.
    /// </summary>
    internal sealed class MapLayer
    {
        [JsonPropertyName("minHeight")]
        public float? MinHeight { get; init; }

        [JsonPropertyName("maxHeight")]
        public float? MaxHeight { get; init; }

        [JsonPropertyName("filename")]
        public string Filename { get; init; } = string.Empty;

        [JsonPropertyName("dimBaseLayer")]
        public bool DimBaseLayer { get; init; }

        /// <summary>
        /// A layer is the base layer when it has no height constraints.
        /// </summary>
        [JsonIgnore]
        public bool IsBaseLayer => MinHeight is null && MaxHeight is null;

        [JsonIgnore]
        public float SortHeight => MinHeight ?? float.MinValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsHeightInRange(float height)
        {
            if (IsBaseLayer)
                return true;
            if (MinHeight.HasValue && height < MinHeight.Value)
                return false;
            if (MaxHeight.HasValue && height > MaxHeight.Value)
                return false;
            return true;
        }
    }
}
