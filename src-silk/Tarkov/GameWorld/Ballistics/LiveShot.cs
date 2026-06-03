// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Ballistics
{
    /// <summary>
    /// Trail history for a single in-flight bullet read from
    /// <c>EFT.Ballistics.BallisticsCalculator.Shots</c>.
    /// </summary>
    public sealed class LiveShot
    {
        /// <summary>Stable identity — the game's pointer to the <c>EFT.Ballistics.Shot</c> instance.</summary>
        public ulong Id { get; init; }
        /// <summary>Owning player pointer (matches <c>Player.Base</c>).</summary>
        public ulong OwnerPlayer { get; internal set; }
        /// <summary>Game time the shot was last successfully sampled (used for fade + GC).</summary>
        public DateTime LastSeen { get; internal set; }
        /// <summary><c>Shot.TimeSinceShot</c> (seconds since muzzle exit).</summary>
        public float TimeSinceShot { get; internal set; }
        /// <summary>Latest velocity vector (m/s).</summary>
        public Vector3 Velocity { get; internal set; }
        /// <summary>Latest world position.</summary>
        public Vector3 CurrentPosition { get; internal set; }
        /// <summary>World position at fire (read once when the shot first appeared).</summary>
        public Vector3 StartPosition { get; internal set; }
        /// <summary>Trail point history (oldest first). Tracker decides when to append.</summary>
        public List<Vector3> Trail { get; } = new(32);
    }
}
