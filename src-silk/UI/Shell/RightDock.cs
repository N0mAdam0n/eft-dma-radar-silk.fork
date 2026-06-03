// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Shell
{
    /// <summary>
    /// Forces the side panels (Players, Loot, Quests) into a fixed right-edge dock when
    /// <see cref="SilkConfig.DockSidePanels"/> is enabled. The dock is split vertically
    /// between the currently-open panels so nothing overlaps the radar map.
    ///
    /// This is layout-only — it never opens or closes a panel. Each widget still owns
    /// its own content, visibility flag, and ImGui window. The layout simply calls
    /// <see cref="ImGui.SetNextWindowPos(Vector2, ImGuiCond)"/> / SetNextWindowSize before
    /// each panel's Draw() so the user can't drag them off-screen and so they always sit
    /// inside the area reserved by <see cref="ReservedWidth"/>.
    /// </summary>
    internal static class RightDock
    {
        private const float UnscaledMinPanelHeight = 140f;
        private const float PanelGap = 4f;

        /// <summary>Horizontal pixels reserved on the right for the dock (0 when disabled or no panels open).</summary>
        public static float ReservedWidth
        {
            get
            {
                var cfg = SilkProgram.Config;
                if (!cfg.DockSidePanels)
                    return 0f;
                if (CountOpenPanels(cfg) == 0)
                    return 0f;
                return cfg.RightDockWidth * cfg.UIScale;
            }
        }

        private static int CountOpenPanels(SilkConfig cfg)
        {
            int n = 0;
            if (Widgets.PlayerInfoWidget.IsOpen) n++;
            if (Widgets.LootWidget.IsOpen) n++;
            if (Panels.QuestPanel.IsOpen) n++;
            return n;
        }

        private static Vector2 _viewportPos;
        private static Vector2 _viewportSize;
        private static float _menuH;
        private static float _statusH;
        private static float _dockX;
        private static float _dockTop;
        private static float _dockHeight;
        private static float _panelHeight;
        private static int _placedIndex;
        private static bool _active;

        /// <summary>
        /// Computes the dock geometry for this frame. Must be called once per frame
        /// before any side-panel <c>Draw()</c>. Cheap when docking is disabled.
        /// </summary>
        public static void BeginFrame()
        {
            _active = false;
            _placedIndex = 0;

            var cfg = SilkProgram.Config;
            if (!cfg.DockSidePanels)
                return;

            int openCount = CountOpenPanels(cfg);
            if (openCount == 0)
                return;

            var viewport = ImGui.GetMainViewport();
            _viewportPos = viewport.Pos;
            _viewportSize = viewport.Size;
            _menuH = ImGui.GetFrameHeight();
            _statusH = (Memory.InRaid || Memory.InHideout) ? ImGui.GetFrameHeight() : 0f;

            float width = cfg.RightDockWidth * cfg.UIScale;
            _dockX = _viewportPos.X + _viewportSize.X - width;
            _dockTop = _viewportPos.Y + _menuH;
            _dockHeight = Math.Max(UnscaledMinPanelHeight, _viewportSize.Y - _menuH - _statusH);

            float gapTotal = PanelGap * Math.Max(0, openCount - 1);
            _panelHeight = (_dockHeight - gapTotal) / openCount;
            _active = true;
        }

        /// <summary>
        /// When true, the next call to <see cref="PlaceNext"/> will force-snap that panel
        /// back into its dock slot (used by the "Reset dock" hotkey). One-shot — auto-clears.
        /// </summary>
        private static bool _forceSnapAll;

        /// <summary>Re-snap all dock panels to their default slots on the next frame.</summary>
        public static void RequestResnap() => _forceSnapAll = true;

        /// <summary>
        /// Call immediately before a side-panel's <c>Draw()</c> to dock it. The panel is
        /// placed in the next free vertical slot of the dock the FIRST time, after which
        /// the user can freely drag / resize it. Use <see cref="RequestResnap"/> to force
        /// it back into the slot.
        /// </summary>
        public static void PlaceNext()
        {
            if (!_active)
            {
                _placedIndex++;
                return;
            }

            var cfg = SilkProgram.Config;
            float width = cfg.RightDockWidth * cfg.UIScale;
            float y = _dockTop + (_panelHeight + PanelGap) * _placedIndex;

            var cond = _forceSnapAll ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
            ImGui.SetNextWindowPos(new Vector2(_dockX, y), cond);
            ImGui.SetNextWindowSize(new Vector2(width, _panelHeight), cond);

            _placedIndex++;
        }

        /// <summary>
        /// Called once per frame after all panels' Draw() — clears the one-shot resnap flag.
        /// (Previously also drew a splitter; panels are now freely draggable so the splitter
        /// is no longer needed. The resnap hotkey returns them to the docked layout.)
        /// </summary>
        public static void EndFrame()
        {
            _forceSnapAll = false;
        }
    }
}
