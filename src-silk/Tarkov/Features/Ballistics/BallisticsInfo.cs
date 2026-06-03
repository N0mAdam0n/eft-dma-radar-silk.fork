// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// Ammunition properties needed by <see cref="BallisticsSimulation"/>.
    /// Populated by reading <c>AmmoTemplate</c> + applying weapon/attachment velocity modifiers.
    /// </summary>
    public sealed class BallisticsInfo
    {
        /// <summary>Effective muzzle velocity (m/s) — base + weapon + attachment modifiers.</summary>
        public float BulletSpeed { get; set; }
        public float BulletMassGrams { get; set; }
        public float BulletDiameterMillimeters { get; set; }
        public float BallisticCoefficient { get; set; }

        /// <summary>Sanity-check that values are within physically plausible EFT ranges.</summary>
        public bool IsAmmoValid =>
            BulletMassGrams > 0f && BulletMassGrams < 2000f &&
            BulletSpeed > 1f && BulletSpeed < 2500f &&
            BallisticCoefficient >= 0f && BallisticCoefficient <= 3f &&
            BulletDiameterMillimeters > 0f && BulletDiameterMillimeters <= 100f;
    }
}
