// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.QuestPlanner.Models
{
    /// <summary>
    /// Type of item required for quest completion.
    /// </summary>
    internal enum BringItemType
    {
        Key,
        QuestItem,
    }

    /// <summary>
    /// Single bring-list entry representing an item to bring into a raid.
    /// </summary>
    internal sealed class BringItem
    {
        /// <summary>
        /// Item or key alternatives — any one suffices (e.g. ["Key A", "Key B"]).
        /// </summary>
        public IReadOnlyList<string> Alternatives { get; init; } = [];

        public string QuestName { get; init; } = string.Empty;

        public BringItemType Type { get; init; }

        public int Count { get; init; } = 1;
    }
}
