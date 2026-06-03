// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Features.Ballistics;
using eft_dma_radar.Silk.Tarkov.GameWorld.Ballistics;

namespace eft_dma_radar.Silk.UI.ESP
{
    /// <summary>
    /// Skia ESP overlay for the ballistics debug view. Draws:
    ///   <list type="bullet">
    ///     <item>Predicted shot polyline (red) sampled from <see cref="BallisticsFeature.LocalTrajectory"/>.</item>
    ///     <item>Live in-flight bullet trails (green) from <see cref="LiveShotTracker.GetSnapshot"/>.</item>
    ///   </list>
    /// All world points are projected via <see cref="CameraManager.WorldToScreen"/> in viewport space —
    /// the caller is expected to have already applied the viewport→window scale.
    /// </summary>
    internal static class BallisticsRenderer
    {
        public static void Draw(SKCanvas canvas)
        {
            var cfg = SilkProgram.Config?.Ballistics;
            if (cfg is null || !cfg.Enabled) return;
            if (!CameraManager.IsActive) return;

            var feature = BallisticsFeature.Instance;

            // Configure paint widths from config (cheap — Skia caches the dirty flag itself).
            EspPaints.PredictedTrajectory.StrokeWidth = cfg.LineWidth;
            EspPaints.LiveShotTrail.StrokeWidth = cfg.LineWidth;

            if (cfg.DrawPredictedTrajectory)
                DrawPredictedTrajectory(canvas, feature.LocalTrajectory);

            if (cfg.DrawLiveShots)
                DrawLiveShots(canvas, feature.Tracker.GetSnapshot());
        }

        private static void DrawPredictedTrajectory(SKCanvas canvas, Vector3[] points)
        {
            if (points is null || points.Length < 2) return;

            using var path = new SKPath();
            bool started = false;
            for (int i = 0; i < points.Length; i++)
            {
                var p = points[i];
                if (!IsFinite(p)) continue;
                if (!CameraManager.WorldToScreen(ref p, out var scr, onScreenCheck: false)) continue;

                if (!started) { path.MoveTo(scr.X, scr.Y); started = true; }
                else          { path.LineTo(scr.X, scr.Y); }
            }
            if (!started) return;

            canvas.DrawPath(path, EspPaints.PredictedTrajectory);

            // Muzzle origin marker.
            var origin = points[0];
            if (IsFinite(origin) && CameraManager.WorldToScreen(ref origin, out var muzzle))
                canvas.DrawCircle(muzzle.X, muzzle.Y, 3.5f, EspPaints.MuzzleDot);
        }

        private static void DrawLiveShots(SKCanvas canvas, LiveShot[] shots)
        {
            if (shots is null || shots.Length == 0) return;

            using var path = new SKPath();
            foreach (var shot in shots)
            {
                var trail = shot.Trail;
                if (trail.Count < 2) continue;
                path.Reset();

                bool started = false;
                for (int i = 0; i < trail.Count; i++)
                {
                    var p = trail[i];
                    if (!IsFinite(p)) continue;
                    if (!CameraManager.WorldToScreen(ref p, out var scr, onScreenCheck: false)) continue;
                    if (!started) { path.MoveTo(scr.X, scr.Y); started = true; }
                    else          { path.LineTo(scr.X, scr.Y); }
                }
                if (!started) continue;

                canvas.DrawPath(path, EspPaints.LiveShotTrail);

                // Bullet head dot.
                var head = shot.CurrentPosition;
                if (IsFinite(head) && CameraManager.WorldToScreen(ref head, out var headScr))
                    canvas.DrawCircle(headScr.X, headScr.Y, 2.5f, EspPaints.LiveShotHead);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
