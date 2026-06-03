// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        private static SilkConfig Config => SilkProgram.Config;

        /// <summary>
        /// Settings categories shown in the left nav. The order here drives the visible order.
        /// Glyphs are bold letters so they render with the default ImGui font (no extra glyph ranges needed).
        /// </summary>
        private static readonly (string Glyph, string Label, Action Draw)[] _categories =
        [
            ("G", "通用",     DrawGeneralTab),
            ("R", "预设",     DrawPresetsTab),
            ("P", "玩家",     DrawPlayersTab),
            ("E", "透视",     DrawEspTab),
            ("M", "地图",     DrawMapTab),
            ("Q", "任务区域", DrawQuestZonesTab),
            ("K", "热键",     DrawHotkeysTab),
        ];

        /// <summary>
        /// Currently selected category index. Persists for the session (not saved to disk —
        /// the user almost always wants to land on General first).
        /// </summary>
        private static int _activeCategory;

        /// <summary>
        /// Whether the settings panel is open.
        /// </summary>
        public static bool IsOpen { get; set; }

        /// <summary>
        /// When Settings is open, the category-letter glyphs (G/P/E/M/Q/K)
        /// switch tabs instead of triggering global panel toggles. Returns
        /// true if the key was consumed.
        /// </summary>
        public static bool HandleCategoryShortcuts()
        {
            if (!IsOpen)
                return false;

            for (int i = 0; i < _categories.Length; i++)
            {
                var glyph = _categories[i].Glyph;
                if (glyph.Length != 1)
                    continue;

                // Map the single-letter glyph to its ImGuiKey enum value.
                char c = char.ToUpperInvariant(glyph[0]);
                if (c < 'A' || c > 'Z')
                    continue;

                var key = (ImGuiKey)((int)ImGuiKey.A + (c - 'A'));
                if (ImGui.IsKeyPressed(key, false))
                {
                    _activeCategory = i;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Draw the settings panel using the Phase 3 shell:
        ///   [ left nav (icon + label, one per category) ] [ scrolling content pane ]
        /// </summary>
        public static void Draw()
        {
            bool isOpen = IsOpen;
            using (var scope = PanelWindow.Begin("\u2699 设置", ref isOpen, new Vector2(720, 640)))
            {
                IsOpen = isOpen;
                if (!scope.Visible)
                    return;

                DrawCategoryShell();

                // Config auto-saves via SilkConfig.MarkDirty + FlushIfDirty —
                // no "Save" button needed. A small footer hint reinforces this.
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextColored(UITheme.AccentGreen, "\u2713 " + Chinese.S("Changes auto-saved"));
            }
        }

        private static void DrawCategoryShell()
        {
            float scale = Config.UIScale;
            float navW = 160f * scale;
            float rowH = 36f * scale;

            // Footer (separator + "auto-saved") needs room — reserve it.
            float footerH = ImGui.GetFrameHeightWithSpacing() + 8f;
            float contentH = ImGui.GetContentRegionAvail().Y - footerH;
            if (contentH < 100f) contentH = 100f;

            // Left nav child
            if (ImGui.BeginChild("##settings-nav", new Vector2(navW, contentH), ImGuiChildFlags.Borders))
            {
                for (int i = 0; i < _categories.Length; i++)
                {
                    var (glyph, label, _) = _categories[i];
                    bool active = i == _activeCategory;

                    if (active)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.45f, 0.45f, 1f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.55f, 0.55f, 1f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.65f, 0.65f, 1f));
                    }
                    else
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.06f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.10f));
                    }

                    ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));
                    if (ImGui.Button($"  {glyph}   {label}##cat{i}", new Vector2(-1, rowH)))
                        _activeCategory = i;
                    ImGui.PopStyleVar();

                    ImGui.PopStyleColor(3);
                }
            }
            ImGui.EndChild();

            ImGui.SameLine();

            // Content pane
            if (ImGui.BeginChild("##settings-content", new Vector2(0, contentH), ImGuiChildFlags.Borders))
            {
                int idx = _activeCategory;
                if (idx < 0 || idx >= _categories.Length) idx = 0;
                _categories[idx].Draw();
            }
            ImGui.EndChild();
        }

        /// <summary>
        /// Opens the Settings panel directly to the ESP (透视) tab.
        /// Useful for quick access to ESP configuration without navigating tabs.
        /// </summary>
        public static void OpenEspSettings()
        {
            IsOpen = true;
            for (int i = 0; i < _categories.Length; i++)
            {
                if (_categories[i].Glyph == "E" || _categories[i].Label.Contains("透视"))
                {
                    _activeCategory = i;
                    return;
                }
            }
            _activeCategory = 3; // fallback to ESP position
        }
    }
}
