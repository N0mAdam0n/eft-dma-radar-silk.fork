// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.QuestPlanner.Models
{
    /// <summary>
    /// Per-map entry in the session plan, ordered by priority.
    /// </summary>
    internal sealed class MapPlan
    {
        public string MapId { get; init; } = string.Empty;
        public string MapName { get; init; } = string.Empty;

        /// <summary>True for the top-ranked map.</summary>
        public bool IsRecommended { get; init; }

        public int CompletableObjectiveCount { get; init; }
        public int ActiveQuestCount { get; init; }

        public IReadOnlyList<QuestPlan> Quests { get; init; } = [];
        public IReadOnlyList<UnlockedQuest> UnlockedQuests { get; init; } = [];

        /// <summary>Deduplicated bring list across all quests.</summary>
        public IReadOnlyList<BringItem> FilteredBringList { get; init; } = [];
    }
}
