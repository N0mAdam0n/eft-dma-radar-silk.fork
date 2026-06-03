// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.DMA.Features;
using eft_dma_radar.Silk.Misc.Workers;
using eft_dma_radar.Silk.Tarkov.GameWorld.Ballistics;

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// Read-only feature that:
    ///   <list type="bullet">
    ///     <item>Tracks every in-flight <c>EFT.Ballistics.Shot</c> via <see cref="LiveShotTracker"/>.</item>
    ///     <item>Snapshots the game's G1 drag table the first time a Shot is observed.</item>
    ///     <item>Exposes the latest <see cref="ShotState"/> for the local player (populated by
    ///       <see cref="eft_dma_radar.Silk.Tarkov.GameWorld.Player.Plugins.FirearmManager"/>).</item>
    ///   </list>
    /// Runs on its own ~125Hz worker thread spawned at raid start. Toggling
    /// <see cref="Enabled"/> stops the tracker doing real work but leaves the thread alive.
    /// </summary>
    public sealed class BallisticsFeature : IFeature
    {
        public static BallisticsFeature Instance { get; }

        static BallisticsFeature()
        {
            Instance = new BallisticsFeature();
            IFeature.Register(Instance);
            Log.WriteLine("[Ballistics] Feature registered.");
        }

        public LiveShotTracker Tracker { get; } = new();

        /// <summary>Resolver for the local player's current weapon + aim → predicted ShotState.</summary>
        internal LocalShotResolver Resolver { get; } = new();

        /// <summary>Last-computed predicted shot for the local player. Null when invalid.</summary>
        public ShotState? LocalPredicted { get; private set; }

        /// <summary>Pre-sampled trajectory points for rendering. Length 0 when invalid.</summary>
        public Vector3[] LocalTrajectory { get; private set; } = Array.Empty<Vector3>();

        /// <summary>Loaded ammo's short name (for HUD).</summary>
        public string? CurrentAmmoShortName => Resolver.CurrentAmmoShortName;

        /// <summary>Loaded ammo's base velocity (m/s, before weapon/mod multipliers) — for HUD.</summary>
        public float? BaseMuzzleVelocity => Resolver.CurrentBallistics?.BulletSpeed;

        /// <summary>Effective muzzle velocity after weapon + attachment modifiers (m/s) — for HUD.</summary>
        public float? EffectiveMuzzleVelocity =>
            Resolver.EffectiveMuzzleVelocity > 0f ? Resolver.EffectiveMuzzleVelocity : null;

        public bool Enabled => SilkProgram.Config.Ballistics?.Enabled ?? false;

        public bool CanRun => Enabled && Memory.InRaid;

        private WorkerThread? _worker;

        private BallisticsFeature() { }

        public void OnGameStart() { }
        public void OnGameStop()  { ShutdownWorker(); }
        public void OnRaidEnd()
        {
            ShutdownWorker();
            Tracker.Clear();
            G1Table.Reset();
            Resolver.Reset();
            LocalPredicted = null;
            LocalTrajectory = Array.Empty<Vector3>();
        }

        public void OnRaidStart()
        {
            ShutdownWorker(); // defensive — should be a no-op

            // Apply config to runtime knobs before the worker fires.
            var cfg = SilkProgram.Config?.Ballistics;
            if (cfg is not null)
            {
                Tracker.Lifetime = TimeSpan.FromSeconds(cfg.LiveShotLifetime);
                if (!cfg.UseGameG1Table)
                    G1Table.Reset();
            }

            _worker = new WorkerThread
            {
                Name = "BallisticsTracker",
                SleepDuration = TimeSpan.FromMilliseconds(8),
                SleepMode = WorkerSleepMode.DynamicSleep,
                ThreadPriority = ThreadPriority.AboveNormal,
            };
            _worker.PerformWork += Tick;
            _worker.Start();
            Log.WriteLine("[Ballistics] Worker started.");
        }

        public void OnApply() { /* read-only feature — nothing to apply */ }

        private void Tick(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            if (!CanRun) return;
            if (Memory.Game is not LocalGameWorld game) return;

            var cfg = SilkProgram.Config.Ballistics;
            if (cfg is null) return;

            // Granular gating: only read what a downstream consumer is actually using.
            //   - Live shot tracker → drives green tracers AND the debug HUD's live count
            //   - Predicted trajectory → drives red arc AND the HUD's drop table
            // When neither consumer needs the data we skip the DMA entirely.
            bool needLiveShots = cfg.DrawLiveShots || cfg.ShowDebugHud;
            bool needPredicted = cfg.DrawPredictedTrajectory || cfg.ShowDebugHud;

            // 1) Live shot tracer history from BallisticsCalculator.Shots.
            if (needLiveShots)
                Tracker.Update(game.Base);
            else if (Tracker.TrackedCount > 0)
                Tracker.Clear(); // free memory + stop emitting stale snapshots

            // 2) Predicted trajectory for local player.
            if (!needPredicted)
            {
                LocalPredicted = null;
                LocalTrajectory = Array.Empty<Vector3>();
                return;
            }

            if (game.LocalPlayer is not LocalPlayer lp || !lp.IsAlive)
            {
                LocalPredicted = null;
                LocalTrajectory = Array.Empty<Vector3>();
                return;
            }

            if (!Resolver.TryResolve(lp, out var shot))
            {
                LocalPredicted = null;
                LocalTrajectory = Array.Empty<Vector3>();
                return;
            }

            int sampleCount = Math.Clamp(cfg.PredictedSamples, 8, 512);
            float maxDist = Math.Clamp(cfg.PredictedMaxDistance, 25f, 2000f);
            var buffer = new Vector3[sampleCount];
            int written = TrajectoryMath.BuildTrajectoryPoints(shot, buffer, maxDist);
            if (written < 2)
            {
                LocalPredicted = null;
                LocalTrajectory = Array.Empty<Vector3>();
                return;
            }
            if (written != buffer.Length)
            {
                var trimmed = new Vector3[written];
                Array.Copy(buffer, trimmed, written);
                buffer = trimmed;
            }
            LocalPredicted = shot;
            LocalTrajectory = buffer;
        }

        private void ShutdownWorker()
        {
            var w = Interlocked.Exchange(ref _worker, null);
            if (w is null) return;
            try { w.Dispose(); w.Join(TimeSpan.FromSeconds(2)); }
            catch { }
        }
    }
}
