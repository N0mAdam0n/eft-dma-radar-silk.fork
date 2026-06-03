// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.DMA.Features;

namespace eft_dma_radar.Silk.Tarkov.Features
{
    /// <summary>
    /// Manages registration and lifecycle events for all <see cref="IFeature"/> implementations.
    /// (Memory write features have been removed; this now only handles read-only features.)
    /// </summary>
    internal static class FeatureManager
    {
        internal static void ModuleInit()
        {
            // BallisticsFeature (read-only) registers itself via its static constructor.
            RuntimeHelpers.RunClassConstructor(typeof(eft_dma_radar.Silk.Tarkov.Features.Ballistics.BallisticsFeature).TypeHandle);

            Memory.GameStarted += (_, _) => OnGameStarted();
            Memory.GameStopped += (_, _) => OnGameStopped();
            Memory.RaidStarted += (_, _) => OnRaidStarted();
            Memory.RaidStopped += (_, _) => OnRaidStopped();

            Log.WriteLine($"[FeatureManager] Initialized with {IFeature.AllFeatures.Count()} features.");
        }

        private static void OnGameStarted()
        {
            foreach (var f in IFeature.AllFeatures) f.OnGameStart();
        }

        private static void OnGameStopped()
        {
            foreach (var f in IFeature.AllFeatures) f.OnGameStop();
        }

        private static void OnRaidStarted()
        {
            foreach (var f in IFeature.AllFeatures) f.OnRaidStart();
        }

        private static void OnRaidStopped()
        {
            foreach (var f in IFeature.AllFeatures) f.OnRaidEnd();
        }
    }
}
