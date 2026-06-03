// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.QuestPlanner.Models
{
    /// <summary>
    /// Mutable accumulator used internally during session plan scoring. Not exposed in the summary.
    /// </summary>
    internal sealed class MapScore
    {
        public string MapId { get; }
        public string MapName { get; }
        public int ObjectiveCount { get; set; }
        public int UnlockCount { get; set; }
        public HashSet<string> QuestIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FinishableQuestIds { get; } = new(StringComparer.Ordinal);

        public MapScore(string mapId, string mapName)
        {
            MapId = mapId;
            MapName = mapName;
        }
    }
}
