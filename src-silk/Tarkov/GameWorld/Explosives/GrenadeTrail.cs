// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Numerics;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Explosives
{
    /// <summary>
    /// Snapshot of a grenade's sampled flight trail for ESP rendering.
    /// Trail points are historical positions (oldest first). Used for drag-trail visualization.
    /// </summary>
    public sealed class GrenadeTrail
    {
        /// <summary>Stable key (game object address of the Grenade).</summary>
        public ulong Id { get; init; }

        /// <summary>Short name (e.g. "F-1", "M67").</summary>
        public string Name { get; internal set; } = "Grenade";

        /// <summary>Effective blast radius if known (for future danger highlight).</summary>
        public float EffectiveDistance { get; internal set; }

        /// <summary>Historical world positions of this grenade (oldest → newest). Capped in tracker.</summary>
        public List<Vector3> Trail { get; } = new(16);

        /// <summary>Most recent known world position (may be after the grenade detonated, for lingering dot).</summary>
        public Vector3 CurrentPosition { get; internal set; }

        /// <summary>UTC time of last successful position sample (used for lifetime culling).</summary>
        public DateTime LastSeen { get; internal set; }
    }
}
