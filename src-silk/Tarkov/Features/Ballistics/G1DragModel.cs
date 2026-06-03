// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// One entry in the G1 ballistic-coefficient lookup table.
    /// Mirrors <c>EFT.Ballistics.BallisticCoefficientValues</c>.
    /// </summary>
    public readonly struct G1DragModel
    {
        public readonly float Mach;
        public readonly float Ballist;

        public G1DragModel(float mach, float ballist)
        {
            Mach = mach;
            Ballist = ballist;
        }
    }
}
