// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Player
{
    /// <summary>
    /// A single piece of player equipment (armor, weapon, headwear, etc.).
    /// </summary>
    internal sealed class GearItem
    {
        /// <summary>Full item name.</summary>
        public required string Long { get; init; }

        /// <summary>Short item name.</summary>
        public required string Short { get; init; }

        /// <summary>Best rouble price (max of flea/trader).</summary>
        public int Price { get; init; }
    }
}
