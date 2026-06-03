// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.GameWorld.Loot;

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Flattened loot item snapshot for the buddy web radar client.
    /// All loose items are emitted unfiltered with raw facts only — the buddy
    /// owns its own wishlist, importance threshold, and tier multipliers.
    /// The host never imposes its local UI filters on the web feed.
    /// </summary>
    public sealed class WebRadarLootItem
    {
        public string Name { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public string BsgId { get; set; } = string.Empty;

        /// <summary>Best display price in roubles (flea/trader, whichever the host resolved).</summary>
        public int Price { get; set; }

        /// <summary>True when the item carries the BSG <c>ItemTemplate.QuestItem</c> flag (watches, letters, etc.).</summary>
        public bool QuestItem { get; set; }

        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }

        internal static WebRadarLootItem Create(LootItem item)
        {
            var pos = item.Position;
            return new WebRadarLootItem
            {
                Name = item.Name,
                ShortName = item.ShortName,
                BsgId = item.Id,
                Price = item.DisplayPrice,
                QuestItem = item.IsQuestItem,
                WorldX = pos.X,
                WorldY = pos.Y,
                WorldZ = pos.Z,
            };
        }
    }
}

