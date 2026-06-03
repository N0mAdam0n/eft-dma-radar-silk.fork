// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// Forward-step trajectory sampler used by the debug overlay. Fills
    /// <paramref name="outPoints"/> with world-space positions along the predicted arc,
    /// stopping at <paramref name="maxDistance"/> or after the bullet hits the ground.
    /// </summary>
    public static class TrajectoryMath
    {
        private const float StepSeconds = 0.02f;
        private const float Gravity = -9.81f;

        /// <summary>
        /// Returns the number of samples actually written. Uses the same drag model as
        /// <see cref="BallisticsSimulation"/> so the arc matches what aim-prediction would compute.
        /// </summary>
        public static int BuildTrajectoryPoints(in ShotState shot, Span<Vector3> outPoints, float maxDistance)
        {
            if (outPoints.IsEmpty || !shot.IsValid) return 0;

            var info = shot.Ballistics;
            float massKg = info.BulletMassGrams / 1000f;
            float massX2 = massKg * 2f;
            float diameterM = info.BulletDiameterMillimeters / 1000f;
            float slowdown = massKg * 0.0014223f / (diameterM * diameterM * info.BallisticCoefficient);
            float area = diameterM * diameterM * 3.1415927f / 4f;
            float airArea = 1.2f * area;

            Vector3 pos = shot.SourcePosition;
            Vector3 vel = Vector3.Normalize(shot.InitialDirection) * shot.MuzzleSpeed;
            outPoints[0] = pos;
            int count = 1;

            float maxDistSq = maxDistance * maxDistance;
            for (int i = 1; i < outPoints.Length; i++)
            {
                float magnitude = vel.Length();
                if (magnitude < 1f) break;

                float dragCoefficient = G1Table.CalculateDragCoefficient(magnitude) * slowdown;
                Vector3 dragAccel = airArea * -dragCoefficient * magnitude * magnitude / massX2 * Vector3.Normalize(vel);
                Vector3 accel = new(dragAccel.X, dragAccel.Y + Gravity, dragAccel.Z);

                vel += accel * StepSeconds;
                pos += vel * StepSeconds;
                outPoints[i] = pos;
                count = i + 1;

                if ((pos - shot.SourcePosition).LengthSquared() >= maxDistSq) break;
            }
            return count;
        }
    }
}
