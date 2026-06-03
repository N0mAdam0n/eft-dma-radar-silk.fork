// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Features.Ballistics;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Widgets
{
    /// <summary>
    /// Compact debug HUD for the ballistics simulation. Shows the loaded ammo, effective
    /// muzzle velocity, predicted drop / travel time at standard ranges, G1 source, and
    /// the live shot tracker's health — everything needed to spot drift between the
    /// predicted (red) arc and the game's actual (green) tracers in the ESP overlay.
    /// </summary>
    internal static class BallisticsDebugWidget
    {
        private static readonly float[] _rangesMeters = [50f, 100f, 150f, 200f, 300f];

        public static bool IsOpenField;

        public static bool IsOpen
        {
            get => IsOpenField;
            set => IsOpenField = value;
        }

        public static void Draw()
        {
            var cfg = SilkProgram.Config?.Ballistics;
            if (cfg is null || !cfg.ShowDebugHud) return;

            bool open = IsOpen;
            ImGui.SetNextWindowSizeConstraints(new Vector2(280, 240), new Vector2(520, 600));
            using var scope = PanelWindow.Begin("Ballistics", ref open, new Vector2(360, 360));
            IsOpen = open;
            if (!scope.Visible) return;

            var feature = BallisticsFeature.Instance;
            var shot = feature.LocalPredicted;

            // ── Header / status ───────────────────────────────────────────────
            ImGui.TextColored(cfg.Enabled ? ColorOk : ColorDim,
                cfg.Enabled ? "Active" : "Disabled (toggle in ESP tab)");
            ImGui.SameLine();
            ImGui.TextColored(ColorLabel, $"raid={Memory.InRaid}");
            ImGui.Separator();

            // ── Ammo / weapon ─────────────────────────────────────────────────
            ImGui.TextColored(ColorSection, "Ammo");
            var info = feature.Resolver.CurrentBallistics;
            if (info is null)
            {
                ImGui.TextColored(ColorDim, "No weapon held / ammo not resolved.");
            }
            else
            {
                Row("Name",        feature.CurrentAmmoShortName ?? "?");
                Row("Base speed",  $"{info.BulletSpeed:F0} m/s");
                Row("Effective",   feature.EffectiveMuzzleVelocity is float v ? $"{v:F0} m/s" : "?");
                Row("Mass",        $"{info.BulletMassGrams:F2} g");
                Row("Diameter",    $"{info.BulletDiameterMillimeters:F2} mm");
                Row("BC",          $"{info.BallisticCoefficient:F3}");
                if (!info.IsAmmoValid)
                    ImGui.TextColored(ColorWarn, "Ammo values out of plausible range!");
            }
            ImGui.Spacing();

            // ── Predicted shot ────────────────────────────────────────────────
            ImGui.TextColored(ColorSection, "Predicted drop @ range");
            if (shot is not ShotState s || !s.IsValid)
            {
                ImGui.TextColored(ColorDim, "Predicted trajectory unavailable.");
            }
            else
            {
                if (ImGui.BeginTable("##balDrops", 3,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn("Range",  ImGuiTableColumnFlags.WidthFixed, 70f);
                    ImGui.TableSetupColumn("Drop",   ImGuiTableColumnFlags.WidthFixed, 70f);
                    ImGui.TableSetupColumn("Travel", ImGuiTableColumnFlags.WidthFixed, 70f);
                    ImGui.TableHeadersRow();

                    foreach (var range in _rangesMeters)
                    {
                        Vector3 start = Vector3.Zero;
                        Vector3 end = new(0, 0, range);
                        var output = BallisticsSimulation.Run(ref start, ref end, s.Ballistics);

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(ColorLabel, $"{range:F0} m");
                        ImGui.TableNextColumn();
                        ImGui.TextColored(ColorValue, $"{output.DropCompensation * 100f:F1} cm");
                        ImGui.TableNextColumn();
                        ImGui.TextColored(ColorValue, $"{output.TravelTime * 1000f:F0} ms");
                    }
                    ImGui.EndTable();
                }
            }
            ImGui.Spacing();

            // ── G1 source ────────────────────────────────────────────────────
            ImGui.TextColored(ColorSection, "G1 drag table");
            if (G1Table.UsingLiveTable)
                ImGui.TextColored(ColorOk, $"Live (from game) — {G1Table.EntryCount} entries");
            else
                ImGui.TextColored(ColorWarn, $"Fallback (hardcoded) — {G1Table.EntryCount} entries");
            ImGui.Spacing();

            // ── Live tracker ─────────────────────────────────────────────────
            ImGui.TextColored(ColorSection, "Live shot tracker");
            Row("FireIndex",       feature.Tracker.LastFireIndex.ToString());
            Row("Tracked",         feature.Tracker.TrackedCount.ToString());
            Row("Predicted pts",   feature.LocalTrajectory.Length.ToString());
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static void Row(string label, string value)
        {
            ImGui.TextColored(ColorLabel, $"{label}:");
            ImGui.SameLine(110f);
            ImGui.TextColored(ColorValue, value);
        }

        private static readonly Vector4 ColorLabel   = new(0.65f, 0.65f, 0.65f, 1f);
        private static readonly Vector4 ColorValue   = new(1f, 1f, 1f, 1f);
        private static readonly Vector4 ColorOk      = new(0.5f, 0.9f, 0.5f, 1f);
        private static readonly Vector4 ColorWarn    = new(1f, 0.7f, 0.3f, 1f);
        private static readonly Vector4 ColorDim     = new(0.5f, 0.5f, 0.5f, 1f);
        private static readonly Vector4 ColorSection = new(0.8f, 0.65f, 0.3f, 1f);
    }
}
