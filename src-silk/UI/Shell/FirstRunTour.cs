// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Numerics;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Shell
{
    /// <summary>
    /// Lightweight first-run welcome tour. Shows a short series of card-style
    /// overlays explaining the four main UX entry points (sidebar, status bar,
    /// preset selector, command palette / radial). Driven entirely by ImGui —
    /// no Skia, no input grab, no impact on the radar render path.
    ///
    /// The tour is shown exactly once per install: it auto-opens when
    /// <see cref="SilkConfig.FirstRunTourCompleted"/> is false, and persists
    /// completion (Finish or Skip) to config so it never reappears.
    /// </summary>
    internal static class FirstRunTour
    {
        public static bool IsOpen { get; private set; }

        private static int _step;
        private static bool _autoTriggered;

        private sealed record Step(string Title, string Body, string? Tip);

        private static readonly Step[] _steps =
        [
            new(
                "Welcome to the radar",
                "Quick 30-second tour of the new layout. You can skip any time — " +
                "the radar still works the way you remember.\n\n" +
                "Players, their position, and aim direction are the radar's primary signal — " +
                "everything else hangs off that.",
                "Press → / Enter to continue, Esc to skip."),
            new(
                "Left sidebar — your main controls",
                "The icon column on the left toggles the big panels:\n" +
                "  P  Players      [1]\n" +
                "  L  Loot         [2]\n" +
                "  A  Aimview      [3]\n" +
                "  Q  Quests       [4]\n" +
                "  S  Settings     [5]\n" +
                "  *  ESP overlay  [E]\n\n" +
                "Press Tab to hide / show the whole sidebar.",
                "Hover any icon to see its hotkey hint."),
            new(
                "Bottom status bar — at-a-glance vitals",
                "Big-chip readout designed for AnyDesk / TV viewing:\n" +
                "  STATUS   — In Raid / In Hideout\n" +
                "  PLAYERS  — total · teammates / PMC / scavs / AI breakdown\n" +
                "  VITALS   — energy / hydration, colored when low\n" +
                "  FPS  ·  DMA  ·  MAP   (right side)\n\n" +
                "Click the v / ^ chevron to collapse the bar entirely.",
                "Players are split T (teammate) / P (PMC) / S (player scav) / AI."),
            new(
                "Presets — switch a radar config in one click",
                "The preset combo in the top menu bar bundles every radar-layer + " +
                "player-display toggle into named profiles:\n" +
                "  Stealth  ·  Loot Run  ·  PvP  ·  Quests  ·  Custom\n\n" +
                "Bind the Previous / Next Preset hotkeys in the Hotkeys panel to cycle them " +
                "from your second keyboard.",
                "Drift from a built-in preset auto-flips you to Custom."),
            new(
                "Command palette",
                "Fast keyboard-only access to every action without hunting through menus:\n\n" +
                "  Press Ctrl+K from anywhere in the radar. Type to fuzzy-search every " +
                "hotkey action and panel, then press Enter to invoke.\n\n" +
                "Everything else can also be bound as a hotkey in the Hotkeys panel.",
                "That's it — happy hunting."),
        ];

        /// <summary>
        /// Open the tour from the start. Called automatically the first time
        /// the radar window finishes initializing if the user hasn't seen it.
        /// </summary>
        public static void Open()
        {
            IsOpen = true;
            _step = 0;
        }

        /// <summary>Close the tour without marking it complete (so it can reappear).</summary>
        public static void Close()
        {
            IsOpen = false;
            _step = 0;
        }

        /// <summary>
        /// Close the tour and persist completion so it never auto-opens again.
        /// </summary>
        public static void Finish()
        {
            IsOpen = false;
            _step = 0;
            var cfg = SilkProgram.Config;
            if (!cfg.FirstRunTourCompleted)
            {
                cfg.FirstRunTourCompleted = true;
                cfg.MarkDirty();
            }
        }

        /// <summary>
        /// Call once per frame. Auto-opens the tour on first run, then draws it
        /// while open. No-op once dismissed.
        /// </summary>
        public static void Draw()
        {
            // Auto-open on first run, but only after the radar has had a frame to settle —
            // we wait for the menu bar to be present so layout numbers are valid.
            if (!_autoTriggered)
            {
                _autoTriggered = true;
                if (!SilkProgram.Config.FirstRunTourCompleted)
                    Open();
            }

            if (!IsOpen)
                return;

            // Esc anywhere skips.
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                Finish();
                return;
            }

            var io = ImGui.GetIO();
            var viewport = ImGui.GetMainViewport();
            float scale = SilkProgram.Config.UIScale;

            // Dim full-screen backdrop.
            ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
            ImGui.SetNextWindowSize(io.DisplaySize, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.55f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            const ImGuiWindowFlags backdropFlags = ImGuiWindowFlags.NoTitleBar
                                                   | ImGuiWindowFlags.NoResize
                                                   | ImGuiWindowFlags.NoMove
                                                   | ImGuiWindowFlags.NoScrollbar
                                                   | ImGuiWindowFlags.NoSavedSettings
                                                   | ImGuiWindowFlags.NoInputs
                                                   | ImGuiWindowFlags.NoFocusOnAppearing
                                                   | ImGuiWindowFlags.NoNav
                                                   | ImGuiWindowFlags.NoBringToFrontOnFocus;

            if (ImGui.Begin("##tour_backdrop", backdropFlags))
            {
                // Empty — just paints the dim layer.
            }
            ImGui.End();
            ImGui.PopStyleVar(3);

            // Foreground card.
            int idx = Math.Clamp(_step, 0, _steps.Length - 1);
            var step = _steps[idx];

            var cardSize = new Vector2(560f * scale, 320f * scale);
            var cardPos = viewport.Pos + (viewport.Size - cardSize) * 0.5f;

            ImGui.SetNextWindowPos(cardPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(cardSize, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.98f);

            const ImGuiWindowFlags cardFlags = ImGuiWindowFlags.NoTitleBar
                                                | ImGuiWindowFlags.NoResize
                                                | ImGuiWindowFlags.NoMove
                                                | ImGuiWindowFlags.NoCollapse
                                                | ImGuiWindowFlags.NoSavedSettings;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20f * scale, 18f * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);

            if (ImGui.Begin("##tour_card", cardFlags))
            {
                // Step counter (top-right).
                string counter = $"{idx + 1} / {_steps.Length}";
                float counterW = ImGui.CalcTextSize(counter).X;
                ImGui.SetCursorPosX(cardSize.X - counterW - 20f * scale);
                ImGui.TextColored(new Vector4(0.55f, 0.58f, 0.62f, 1f), counter);

                // Title — accented cyan, bold-feeling via larger spacing.
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.30f, 0.85f, 1.00f, 1f));
                ImGui.TextUnformatted(step.Title);
                ImGui.PopStyleColor();
                ImGui.Separator();
                ImGui.Spacing();

                // Body — wraps to card width.
                ImGui.PushTextWrapPos(cardSize.X - 40f * scale);
                ImGui.TextWrapped(step.Body);
                ImGui.PopTextWrapPos();

                // Spacer, then tip + buttons pinned to bottom.
                float footerH = ImGui.GetFrameHeightWithSpacing() + (step.Tip is null ? 0f : ImGui.GetTextLineHeightWithSpacing()) + 8f;
                float avail = ImGui.GetContentRegionAvail().Y;
                if (avail > footerH)
                    ImGui.Dummy(new Vector2(0, avail - footerH));

                if (step.Tip is not null)
                {
                    ImGui.TextColored(new Vector4(0.55f, 0.60f, 0.65f, 1f), step.Tip);
                    ImGui.Spacing();
                }

                // Buttons row: Skip (left) — Back / Next / Done (right).
                float btnH = 32f * scale;
                if (ImGui.Button("Skip", new Vector2(80f * scale, btnH)))
                {
                    Finish();
                    ImGui.End();
                    ImGui.PopStyleVar(2);
                    return;
                }

                bool hasBack = idx > 0;
                bool isLast = idx >= _steps.Length - 1;

                float rightW = (isLast ? 110f : 90f) * scale + (hasBack ? (80f * scale + 8f) : 0f);
                ImGui.SameLine(cardSize.X - rightW - 20f * scale);

                if (hasBack)
                {
                    if (ImGui.Button("Back", new Vector2(80f * scale, btnH)))
                        _step = Math.Max(0, _step - 1);
                    ImGui.SameLine();
                }

                bool advance = false;
                if (isLast)
                {
                    if (ImGui.Button("Done", new Vector2(110f * scale, btnH)))
                        advance = true;
                }
                else
                {
                    if (ImGui.Button("Next  →", new Vector2(90f * scale, btnH)))
                        advance = true;
                }

                // Keyboard shortcuts: Right-arrow / Enter advance, Left-arrow goes back.
                if (!io.WantTextInput)
                {
                    if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, false) || ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.Space, false))
                        advance = true;
                    if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, false) && hasBack)
                        _step = Math.Max(0, _step - 1);
                }

                if (advance)
                {
                    if (isLast)
                        Finish();
                    else
                        _step = Math.Min(_steps.Length - 1, _step + 1);
                }
            }
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
    }
}
