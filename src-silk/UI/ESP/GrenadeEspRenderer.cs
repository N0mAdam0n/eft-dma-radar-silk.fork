// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Numerics;
using System.Runtime.CompilerServices;
using eft_dma_radar.Silk.Tarkov.GameWorld.Explosives;

namespace eft_dma_radar.Silk.UI.ESP
{
    /// <summary>
    /// ESP renderer for grenade trajectories:
    /// - Actual sampled flight path as a visible drag trail (拖尾).
    /// - Current (or last known) grenade position drawn as a bold dot.
    /// - Trails linger for a user-configurable time after the grenade detonates/lands/stops.
    /// - In-hand prediction arc (if a grenade is equipped) is also drawn for preview.
    /// All drawing is in viewport space (caller applies scale).
    /// Controlled by <see cref="SilkConfig.EspGrenades"/>.
    /// </summary>
    internal static class GrenadeEspRenderer
    {
        public static void Draw(SKCanvas canvas)
        {
            var cfg = SilkProgram.Config?.EspGrenades;
            if (cfg is null || !cfg.Enabled) return;
            if (!CameraManager.IsActive) return;

            // Live-update paint properties from config (Skia is cheap about this).
            EspPaints.GrenadeTrail.StrokeWidth = cfg.TrailWidth;

            // Draw actual grenade trails + bold dots from the tracker.
            var trails = GrenadeEspTracker.Instance.GetSnapshot();
            if (trails.Length > 0)
                DrawTrails(canvas, trails, cfg);

            // Draw in-hand throw prediction arc (yellowish) if player is holding a grenade.
            // This gives a live "where will it land" line in the 3D ESP view.
            try
            {
                var pred = Memory.InHandGrenadePrediction;
                if (pred is not null && pred.Arc is { Count: > 1 })
                    DrawInHandArc(canvas, pred);
            }
            catch { /* best effort, never break ESP render */ }
        }

        private static void DrawTrails(SKCanvas canvas, GrenadeTrail[] trails, EspGrenadeConfig cfg)
        {
            var local = Memory.LocalPlayer?.Position ?? Vector3.Zero;
            float maxDistSq = cfg.MaxDistance * cfg.MaxDistance;

            foreach (var t in trails)
            {
                // Range filter using last known position (allows lingering dots just outside to still show briefly).
                if (Vector3.DistanceSquared(local, t.CurrentPosition) > maxDistSq)
                    continue;

                // Draw the historical trail as a polyline.
                if (t.Trail.Count >= 2)
                {
                    using var path = new SKPath();
                    bool started = false;

                    for (int i = 0; i < t.Trail.Count; i++)
                    {
                        var wp = t.Trail[i];
                        if (!IsFinite(wp)) continue;
                        if (!CameraManager.WorldToScreen(ref wp, out var scr, onScreenCheck: false)) continue;

                        if (!started) { path.MoveTo(scr.X, scr.Y); started = true; }
                        else { path.LineTo(scr.X, scr.Y); }
                    }

                    if (started)
                        canvas.DrawPath(path, EspPaints.GrenadeTrail);
                }

                // Bold current-position dot (the "hand雷以一个加粗dot形式显示").
                var cur = t.CurrentPosition;
                if (IsFinite(cur) && CameraManager.WorldToScreen(ref cur, out var dot, onScreenCheck: false))
                {
                    float r = cfg.DotRadius;
                    canvas.DrawCircle(dot.X, dot.Y, r, EspPaints.GrenadeDot);
                    // Subtle outer ring for visibility (uses a stroke paint we already have).
                    canvas.DrawCircle(dot.X, dot.Y, r + 1.5f, EspPaints.BoxOutline);
                }
            }
        }

        private static void DrawInHandArc(SKCanvas canvas, PredictedArc arc)
        {
            // Draw the predicted throw arc (preview while holding nade).
            using var path = new SKPath();
            bool started = false;

            var arcPts = arc.Arc;
            for (int i = 0; i < arcPts.Count; i++)
            {
                var wp = arcPts[i];
                if (!IsFinite(wp)) continue;
                if (!CameraManager.WorldToScreen(ref wp, out var scr, onScreenCheck: false)) continue;

                if (!started) { path.MoveTo(scr.X, scr.Y); started = true; }
                else { path.LineTo(scr.X, scr.Y); }
            }

            if (started)
                canvas.DrawPath(path, EspPaints.GrenadePrediction);

            // Landing marker dot (slightly larger than normal for preview).
            var land = arc.Landing;
            if (IsFinite(land) && CameraManager.WorldToScreen(ref land, out var lscr, onScreenCheck: false))
            {
                canvas.DrawCircle(lscr.X, lscr.Y, 3.5f, EspPaints.GrenadeLanding);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
