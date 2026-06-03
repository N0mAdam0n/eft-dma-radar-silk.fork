// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    public readonly ref struct BallisticSimulationOutput
    {
        public readonly float DropCompensation;
        public readonly float TravelTime;

        public BallisticSimulationOutput(float dropCompensation, float travelTime)
        {
            DropCompensation = dropCompensation;
            TravelTime = travelTime;
        }
    }
}
