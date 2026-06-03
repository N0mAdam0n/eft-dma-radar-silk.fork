// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Exits
{
    /// <summary>
    /// Static map ID → friendly display name mapping.
    /// Used by transit points, quest planner, and any UI that shows a human-readable map name.
    /// </summary>
    internal static class MapNames
    {
        public static readonly FrozenDictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"]         = "Default",
                ["Labyrinth"]       = "The Labyrinth",
                ["Terminal"]        = "Terminal",
                ["woods"]           = "Woods",
                ["shoreline"]       = "Shoreline",
                ["rezervbase"]      = "Reserve",
                ["laboratory"]      = "Labs",
                ["interchange"]     = "Interchange",
                ["factory4_day"]    = "Factory",
                ["factory4_night"]  = "Factory",
                ["bigmap"]          = "Customs",
                ["lighthouse"]      = "Lighthouse",
                ["tarkovstreets"]   = "Streets",
                ["Sandbox"]         = "Ground Zero",
                ["Sandbox_high"]    = "Ground Zero",
                ["ground-zero"]     = "Ground Zero",
                ["ground-zero-21"]  = "Ground Zero",
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the friendly display name for a map ID, or
        /// <paramref name="fallback"/> (defaults to the raw ID) if not found.
        /// </summary>
        public static string GetDisplayName(string mapId, string? fallback = null) =>
            Names.TryGetValue(mapId, out var name) ? name : (fallback ?? mapId);

        /// <summary>
        /// Normalises variant IDs to a canonical form used internally
        /// (e.g. "ground-zero-21" → "ground-zero", "Sandbox_high" → "Sandbox").
        /// </summary>
        public static string Normalize(string mapId) => mapId switch
        {
            "ground-zero-21" => "ground-zero",
            "Sandbox_high"   => "Sandbox",
            _                => mapId,
        };
    }
}
