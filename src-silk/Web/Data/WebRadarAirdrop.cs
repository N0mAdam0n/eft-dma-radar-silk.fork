// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.GameWorld.Loot;

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Flattened airdrop snapshot for the buddy web radar.
    /// </summary>
    public sealed class WebRadarAirdrop
    {
        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }

        internal static WebRadarAirdrop Create(LootAirdrop a)
        {
            var pos = a.Position;
            return new WebRadarAirdrop
            {
                WorldX = pos.X,
                WorldY = pos.Y,
                WorldZ = pos.Z,
            };
        }
    }
}
