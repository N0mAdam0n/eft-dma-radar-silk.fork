// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.UI.ESP;
using eft_dma_radar.Silk.UI.Panels;
using eft_dma_radar.Silk.UI.Widgets;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Shell
{
    /// <summary>
    /// Left-edge icon sidebar. Single-click toggles for the primary panels —
    /// the main path for AnyDesk users who shouldn't have to hunt through menus.
    /// Hotkeys: 1 Players, 2 Loot, 3 Aimview, 4 Quests,
    /// 5 Settings, Tab to hide the sidebar entirely.
    ///
    /// The map is sized to start to the right of this sidebar (see
    /// <see cref="ReservedWidth"/>) so panels never sit on top of the radar.
    /// </summary>
    internal static class Sidebar
    {
        /// <summary>Width of the docked sidebar in unscaled pixels.</summary>
        private const float UnscaledWidth = 56f;

        /// <summary>Width of the small "show" handle shown when the sidebar is collapsed.</summary>
        private const float CollapsedHandleWidth = 14f;

        /// <summary>Horizontal pixels reserved on the left for the sidebar (collapsed handle width when hidden).</summary>
        public static float ReservedWidth
        {
            get
            {
                var cfg = SilkProgram.Config;
                return (cfg.ShowSidebar ? UnscaledWidth : CollapsedHandleWidth) * cfg.UIScale;
            }
        }

        /// <summary>Height (scaled) of the bottom status bar in the current frame, or 0 when hidden.</summary>
        public static float StatusBarHeight { get; set; }

        private readonly record struct Item(
            string Icon,
            string Label,
            string Hotkey,
            Func<bool> IsActive,
            Action Toggle);

        private static readonly Item[] _items =
        [
            new Item("P", "玩家", "1",
                static () => PlayerInfoWidget.IsOpen,
                static () => PlayerInfoWidget.IsOpen = !PlayerInfoWidget.IsOpen),

            new Item("L", "物资",    "2",
                static () => LootWidget.IsOpen,
                static () => LootWidget.IsOpen = !LootWidget.IsOpen),

            new Item("A", "瞄准视角", "3",
                static () => AimviewWidget.IsOpen,
                static () => AimviewWidget.IsOpen = !AimviewWidget.IsOpen),

            new Item("Q", "任务",  "4",
                static () => QuestPanel.IsOpen,
                static () => QuestPanel.IsOpen = !QuestPanel.IsOpen),

            new Item("S", "设置", "5",
                static () => SettingsPanel.IsOpen,
                static () => SettingsPanel.IsOpen = !SettingsPanel.IsOpen),
        ];

        /// <summary>
        /// Secondary slots — less-frequently-used panels surfaced as smaller
        /// icon buttons below the primary five. Drawn at half the height so the
        /// sidebar still fits on 720p / scaled displays.
        ///
        /// Hotkey hints come from `HotkeyManager.GetBindingDisplay` so they
        /// reflect the user's actual bindings; the few panels without a
        /// dedicated hotkey action fall back to the local letter shortcut
        /// (e.g. `L` for Loot Filters via <see cref="RadarWindow"/>'s
        /// `HandleLocalShortcuts`).
        /// </summary>
        private static readonly Item[] _secondaryItems =
        [
            new Item("F", "物资过滤", "L",
                static () => LootFiltersPanel.IsOpen,
                static () => LootFiltersPanel.IsOpen = !LootFiltersPanel.IsOpen),

            new Item("K", "击杀播报", "",
                static () => KillfeedPanel.IsOpen,
                static () => KillfeedPanel.IsOpen = !KillfeedPanel.IsOpen),

            new Item("H", "藏身处", "H",
                static () => HideoutPanel.IsOpen,
                static () => HideoutPanel.IsOpen = !HideoutPanel.IsOpen),

            new Item("*", "任务规划器", "",
                static () => QuestPlannerPanel.IsOpen,
                static () => QuestPlannerPanel.IsOpen = !QuestPlannerPanel.IsOpen),

            new Item("O", "玩家历史", "",
                static () => PlayerHistoryPanel.IsOpen,
                static () => PlayerHistoryPanel.IsOpen = !PlayerHistoryPanel.IsOpen),

            new Item("W", "监视列表", "",
                static () => PlayerWatchlistPanel.IsOpen,
                static () => PlayerWatchlistPanel.IsOpen = !PlayerWatchlistPanel.IsOpen),

            new Item("K", "热键", "",
                static () => HotkeyManagerPanel.IsOpen,
                static () => HotkeyManagerPanel.IsOpen = !HotkeyManagerPanel.IsOpen),

            new Item("E", "透视设置", "",
                static () => false, // settings tab, not a standalone toggle
                static () => SettingsPanel.OpenEspSettings()),
        ];

        // Colors (UITheme-aligned)
        private static readonly Vector4 BgColor       = new(0.08f, 0.08f, 0.10f, 0.96f);
        private static readonly Vector4 BorderColor   = new(0.25f, 0.28f, 0.30f, 0.60f);
        private static readonly Vector4 IdleColor     = new(0.55f, 0.60f, 0.65f, 1.00f);
        private static readonly Vector4 HoverColor    = new(0.28f, 0.65f, 0.65f, 1.00f);
        private static readonly Vector4 ActiveColor   = new(0.30f, 0.75f, 0.70f, 1.00f);
        private static readonly Vector4 HotkeyColor   = new(0.45f, 0.47f, 0.50f, 1.00f);

        /// <summary>Draws the sidebar inside the main viewport, below the menu bar.</summary>
        public static void Draw()
        {
            var cfg = SilkProgram.Config;
            var viewport = ImGui.GetMainViewport();
            float menuH = ImGui.GetFrameHeight();
            float statusH = StatusBarHeight;

            if (!cfg.ShowSidebar)
            {
                DrawCollapsedHandle(viewport, menuH, statusH, cfg.UIScale);
                return;
            }

            float width = UnscaledWidth * cfg.UIScale;

            var pos = new Vector2(viewport.Pos.X, viewport.Pos.Y + menuH);
            var size = new Vector2(width, viewport.Size.Y - menuH - statusH);

            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size);

            var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus |
                        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 6));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 4));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgColor);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.12f));

            if (ImGui.Begin("##Sidebar", flags))
            {
                float btnH = 44f * cfg.UIScale;
                float btnW = width;

                foreach (var item in _items)
                {
                    bool active = item.IsActive();

                    // Active marker (left edge cyan bar)
                    var drawList = ImGui.GetWindowDrawList();
                    var btnPos = ImGui.GetCursorScreenPos();
                    if (active)
                    {
                        drawList.AddRectFilled(
                            btnPos,
                            new Vector2(btnPos.X + 3f * cfg.UIScale, btnPos.Y + btnH),
                            ImGui.GetColorU32(ActiveColor));
                    }

                    ImGui.PushID(item.Label);
                    if (ImGui.Button($"##sb_{item.Label}", new Vector2(btnW, btnH)))
                        item.Toggle();

                    bool hovered = ImGui.IsItemHovered();
                    if (hovered)
                        ImGui.SetTooltip($"{item.Label}  [{item.Hotkey}]");

                    // Overlay icon + hotkey centered in the button
                    var color = active ? ActiveColor : (hovered ? HoverColor : IdleColor);
                    var iconSize = ImGui.CalcTextSize(item.Icon);
                    var hotkeySize = ImGui.CalcTextSize(item.Hotkey);

                    var iconPos = new Vector2(
                        btnPos.X + (btnW - iconSize.X) * 0.5f,
                        btnPos.Y + btnH * 0.5f - iconSize.Y - 1);
                    var hkPos = new Vector2(
                        btnPos.X + (btnW - hotkeySize.X) * 0.5f,
                        btnPos.Y + btnH * 0.5f + 1);

                    drawList.AddText(iconPos, ImGui.GetColorU32(color), item.Icon);
                    drawList.AddText(hkPos, ImGui.GetColorU32(HotkeyColor), item.Hotkey);

                    ImGui.PopID();
                }

                // ── Secondary slots ─────────────────────────────────────────
                // Smaller buttons for less-frequently-used panels (Loot
                // Filters, Killfeed, Hideout, etc.). Drawn directly below
                // the primary five with a thin divider.
                if (_secondaryItems.Length > 0)
                {
                    float sepY = ImGui.GetCursorScreenPos().Y + 2f * cfg.UIScale;
                    float sepInset = 12f * cfg.UIScale;
                    var sepStart = new Vector2(ImGui.GetCursorScreenPos().X + sepInset, sepY);
                    var sepEnd   = new Vector2(ImGui.GetCursorScreenPos().X + btnW - sepInset, sepY);
                    ImGui.GetWindowDrawList().AddLine(sepStart, sepEnd, ImGui.GetColorU32(BorderColor), 1f);
                    ImGui.Dummy(new Vector2(0, 6f * cfg.UIScale));

                    float secH = 30f * cfg.UIScale;
                    foreach (var item in _secondaryItems)
                    {
                        bool active = item.IsActive();
                        var drawList = ImGui.GetWindowDrawList();
                        var btnPos = ImGui.GetCursorScreenPos();
                        if (active)
                        {
                            drawList.AddRectFilled(
                                btnPos,
                                new Vector2(btnPos.X + 3f * cfg.UIScale, btnPos.Y + secH),
                                ImGui.GetColorU32(ActiveColor));
                        }

                        ImGui.PushID("sec_" + item.Label);
                        if (ImGui.Button("##sb_sec", new Vector2(btnW, secH)))
                            item.Toggle();

                        bool hovered = ImGui.IsItemHovered();
                        if (hovered)
                        {
                            string hint = string.IsNullOrEmpty(item.Hotkey)
                                ? item.Label
                                : $"{item.Label}  [{item.Hotkey}]";
                            ImGui.SetTooltip(hint);
                        }

                        // Icon centered — secondary buttons skip the hotkey
                        // label (saves vertical space; tooltip carries the hint).
                        var color = active ? ActiveColor : (hovered ? HoverColor : IdleColor);
                        var iconSize = ImGui.CalcTextSize(item.Icon);
                        var iconPos = new Vector2(
                            btnPos.X + (btnW - iconSize.X) * 0.5f,
                            btnPos.Y + (secH - iconSize.Y) * 0.5f);
                        drawList.AddText(iconPos, ImGui.GetColorU32(color), item.Icon);
                        ImGui.PopID();
                    }
                }

                // Bottom: ESP toggle (overlay overlay window)
                float collapseReserve = 22f * cfg.UIScale + 4f;
                float remaining = ImGui.GetContentRegionAvail().Y;
                if (remaining > btnH + collapseReserve + 8)
                    ImGui.Dummy(new Vector2(0, remaining - btnH - collapseReserve - 8));

                {
                    bool active = EspWindow.IsOpen;
                    var drawList = ImGui.GetWindowDrawList();
                    var btnPos = ImGui.GetCursorScreenPos();
                    if (active)
                    {
                        drawList.AddRectFilled(
                            btnPos,
                            new Vector2(btnPos.X + 3f * cfg.UIScale, btnPos.Y + btnH),
                            ImGui.GetColorU32(ActiveColor));
                    }

                    ImGui.PushID("ESP");
                    if (ImGui.Button("##sb_ESP", new Vector2(btnW, btnH)))
                        EspWindow.Toggle();

                    bool hovered = ImGui.IsItemHovered();
                    if (hovered)
                        ImGui.SetTooltip("透视叠加 [E]  |  F2 切换显示/隐藏内容（不关窗）");

                    var color = active ? ActiveColor : (hovered ? HoverColor : IdleColor);
                    const string icon = "*";
                    const string hk = "E";
                    var iconSize = ImGui.CalcTextSize(icon);
                    var hotkeySize = ImGui.CalcTextSize(hk);
                    var iconPos = new Vector2(
                        btnPos.X + (btnW - iconSize.X) * 0.5f,
                        btnPos.Y + btnH * 0.5f - iconSize.Y - 1);
                    var hkPos = new Vector2(
                        btnPos.X + (btnW - hotkeySize.X) * 0.5f,
                        btnPos.Y + btnH * 0.5f + 1);
                    drawList.AddText(iconPos, ImGui.GetColorU32(color), icon);
                    drawList.AddText(hkPos, ImGui.GetColorU32(HotkeyColor), hk);
                    ImGui.PopID();
                }

                // Bottom: collapse chevron
                {
                    float collapseH = 22f * cfg.UIScale;
                    var btnPos = ImGui.GetCursorScreenPos();
                    ImGui.PushID("SidebarCollapse");
                    if (ImGui.Button("##sb_collapse", new Vector2(btnW, collapseH)))
                        ToggleVisibility();
                    bool hovered = ImGui.IsItemHovered();
                    if (hovered)
                        ImGui.SetTooltip(Chinese.T("Hide sidebar") + "  [Tab]");
                    var col = hovered ? HoverColor : IdleColor;
                    const string chevron = "<";
                    var sz = ImGui.CalcTextSize(chevron);
                    ImGui.GetWindowDrawList().AddText(
                        new Vector2(btnPos.X + (btnW - sz.X) * 0.5f, btnPos.Y + (collapseH - sz.Y) * 0.5f),
                        ImGui.GetColorU32(col), chevron);
                    ImGui.PopID();
                }
            }

            ImGui.End();
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar(3);
        }

        /// <summary>Tiny right-pointing handle drawn on the left edge when the sidebar is collapsed.</summary>
        private static void DrawCollapsedHandle(ImGuiViewportPtr viewport, float menuH, float statusH, float uiScale)
        {
            float width = CollapsedHandleWidth * uiScale;
            var pos = new Vector2(viewport.Pos.X, viewport.Pos.Y + menuH);
            var size = new Vector2(width, viewport.Size.Y - menuH - statusH);

            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size);

            var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus |
                        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(BgColor.X, BgColor.Y, BgColor.Z, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.10f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.18f));

            if (ImGui.Begin("##SidebarHandle", flags))
            {
                if (ImGui.Button("##sb_show", size))
                    ToggleVisibility();
                bool hovered = ImGui.IsItemHovered();
                if (hovered)
                    ImGui.SetTooltip(Chinese.T("Show sidebar") + "  [Tab]");

                const string chevron = ">";
                var sz = ImGui.CalcTextSize(chevron);
                var col = hovered ? HoverColor : IdleColor;
                ImGui.GetWindowDrawList().AddText(
                    new Vector2(pos.X + (size.X - sz.X) * 0.5f, pos.Y + (size.Y - sz.Y) * 0.5f),
                    ImGui.GetColorU32(col), chevron);
            }

            ImGui.End();
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(2);
        }

        /// <summary>Hotkey handler: toggle a sidebar slot by 0-based index.</summary>
        public static void Toggle(int index)
        {
            if (index < 0 || index >= _items.Length)
                return;
            _items[index].Toggle();
        }

        /// <summary>Hotkey handler: show/hide the entire sidebar.</summary>
        public static void ToggleVisibility()
        {
            var cfg = SilkProgram.Config;
            cfg.ShowSidebar = !cfg.ShowSidebar;
            cfg.MarkDirty();
        }
    }
}
