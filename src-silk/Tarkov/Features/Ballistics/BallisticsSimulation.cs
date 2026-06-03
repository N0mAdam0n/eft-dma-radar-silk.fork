// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// Bullet physics integrator. Iterates step-by-step from muzzle along +Z until the
    /// projectile is past <paramref name="endPosition"/>; returns drop (m) and travel time (s).
    /// Drag uses <see cref="G1Table.CalculateDragCoefficient"/>.
    /// </summary>
    public static class BallisticsSimulation
    {
        private const int   _maxIterations = 1300;
        private const float _simTimeStep = 0.01f;
        private const float _optimalLerpTolerance = 0.001f;
        private static readonly Vector3 _gravity = new(0f, -9.81f, 0f);
        private static readonly Vector3 _forwardVector = new(0f, 0f, 1f);

        public static BallisticSimulationOutput Run(ref Vector3 startPosition, ref Vector3 endPosition, BallisticsInfo ballistics)
        {
            float shotDistance = Vector3.Distance(startPosition, endPosition);

            float massKg = ballistics.BulletMassGrams / 1000f;
            float massX2 = massKg * 2f;
            float diameterM = ballistics.BulletDiameterMillimeters / 1000f;
            float slowdown = massKg * 0.0014223f / (diameterM * diameterM * ballistics.BallisticCoefficient);
            float area = diameterM * diameterM * 3.1415927f / 4f;
            float airArea = 1.2f * area;

            float time = 0f;
            float lastTravelTime = 0f;
            Vector3 lastPosition = Vector3.Zero;
            Vector3 lastVelocity = _forwardVector * ballistics.BulletSpeed;

            float bulletDrop = 0f;
            float travelTime = 0f;

            for (int i = 1; i < _maxIterations; i++)
            {
                float magnitude = lastVelocity.Length();
                float dragCoefficient = G1Table.CalculateDragCoefficient(magnitude) * slowdown;
                Vector3 translation = _gravity + airArea * -dragCoefficient * magnitude * magnitude / massX2 * Vector3.Normalize(lastVelocity);

                Vector3 currentPosition = lastPosition + lastVelocity * _simTimeStep + 5E-05f * translation;
                Vector3 currentVelocity = lastVelocity + translation * _simTimeStep;

                float currentDistance = Vector3.Distance(currentPosition, Vector3.Zero);
                if (currentDistance >= shotDistance)
                {
                    float optimalLerp = FindOptimalLerp(lastPosition, currentPosition, shotDistance);
                    bulletDrop = Math.Abs(Vector3.Lerp(lastPosition, currentPosition, optimalLerp).Y);
                    travelTime = float.Lerp(lastTravelTime, time += _simTimeStep, optimalLerp);
                    break;
                }

                lastTravelTime = time += _simTimeStep;
                lastPosition = currentPosition;
                lastVelocity = currentVelocity;
            }

            return new BallisticSimulationOutput(bulletDrop, travelTime);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FindOptimalLerp(Vector3 posBefore, Vector3 posAfter, float shotDistance)
        {
            float lerpMin = 0f;
            float lerpMax = 1f;
            float lerpMid = 0.5f;

            while (lerpMax - lerpMin > _optimalLerpTolerance)
            {
                Vector3 lerped = Vector3.Lerp(posBefore, posAfter, lerpMid);
                if (lerped.Z < shotDistance) lerpMin = lerpMid;
                else lerpMax = lerpMid;
                lerpMid = (lerpMin + lerpMax) / 2f;
            }
            return lerpMid;
        }
    }
}
