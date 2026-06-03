// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.GameWorld.Exits;

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Flattened transit-point snapshot for the buddy web radar.
    /// </summary>
    public sealed class WebRadarTransit
    {
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }

        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }

        internal static WebRadarTransit Create(TransitPoint t)
        {
            var pos = t.Position;
            return new WebRadarTransit
            {
                Name = t.Name,
                IsActive = t.IsActive,
                WorldX = pos.X,
                WorldY = pos.Y,
                WorldZ = pos.Z,
            };
        }
    }
}
