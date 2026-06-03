// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// Snapshot of everything needed to draw / aim a single predicted shot:
    /// the resolved ammo physics, the effective muzzle velocity (after weapon +
    /// attachment modifiers), the world-space firing point and direction.
    /// </summary>
    public readonly struct ShotState
    {
        public readonly BallisticsInfo Ballistics;
        public readonly float MuzzleSpeed;
        public readonly Vector3 SourcePosition;
        public readonly Vector3 InitialDirection;

        public ShotState(BallisticsInfo ballistics, float muzzleSpeed, Vector3 sourcePosition, Vector3 initialDirection)
        {
            Ballistics = ballistics;
            MuzzleSpeed = muzzleSpeed;
            SourcePosition = sourcePosition;
            InitialDirection = initialDirection;
        }

        public bool IsValid =>
            Ballistics is not null &&
            Ballistics.IsAmmoValid &&
            MuzzleSpeed > 1f &&
            InitialDirection.LengthSquared() > 0.0001f;
    }
}
