// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Controls
{
    /// <summary>
    /// AnyDesk / TV-friendly ImGui control primitives.
    /// All sizes scale with <see cref="SilkConfig.UIScale"/>.
    ///
    /// Use these instead of raw <c>ImGui.Checkbox</c> / <c>SliderInt</c> for new settings
    /// rows so the UI stays consistent with the Phase 3 design language:
    ///   - large, full-width hit targets (≥36px tall, the whole row is clickable)
    ///   - +/- steppers instead of fiddly sliders for integer / quantized values
    ///   - bold section headers with a thin accent underline
    ///   - cyan accent for "on" / active states, mid-gray for idle
    /// </summary>
    internal static class UIControls
    {
        private static readonly Vector4 AccentCyan   = new(0.30f, 0.75f, 0.70f, 1.00f);
        private static readonly Vector4 RowBg        = new(1.00f, 1.00f, 1.00f, 0.03f);
        private static readonly Vector4 RowBgHover   = new(1.00f, 1.00f, 1.00f, 0.07f);
        private static readonly Vector4 RowText     = new(0.90f, 0.92f, 0.94f, 1.00f);
        private static readonly Vector4 RowTextDim  = new(0.55f, 0.58f, 0.62f, 1.00f);

        // Per-label caches for stepper value→string formatting. Settings panels redraw
        // every frame but the value usually stays constant, so caching by label cuts
        // out the per-frame string.Format / int.ToString allocations.
        private static readonly Dictionary<string, (int val, string? format, string text)> _intLabelCache = new();
        private static readonly Dictionary<string, (float val, string format, string text)> _floatLabelCache = new();

        private static float Scale => SilkProgram.Config.UIScale;

        /// <summary>Big section header — drop-in replacement for <c>ImGui.SeparatorText</c>.</summary>
        public static void Section(string label)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, AccentCyan);
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
            ImGui.Separator();
            ImGui.Spacing();
        }

        /// <summary>
        /// Full-width toggle row. The entire row is clickable / focusable — ideal for
        /// remote desktop (no need to hit the tiny checkbox).
        /// </summary>
        /// <returns>True if the value changed this frame.</returns>
        public static bool ToggleRow(string label, ref bool value, string? tooltip = null)
        {
            float rowH = 36f * Scale;
            float availW = ImGui.GetContentRegionAvail().X;

            var cursor = ImGui.GetCursorScreenPos();
            var size = new Vector2(availW, rowH);

            // Whole-row invisible button captures the click + hover.
            ImGui.PushID(label);
            bool clicked = ImGui.InvisibleButton("##row", size);
            bool hovered = ImGui.IsItemHovered();
            ImGui.PopID();

            if (tooltip is not null && hovered)
                ImGui.SetTooltip(tooltip);

            if (clicked)
                value = !value;

            // Background
            var dl = ImGui.GetWindowDrawList();
            uint bg = ImGui.GetColorU32(hovered ? RowBgHover : RowBg);
            dl.AddRectFilled(cursor, cursor + size, bg, 4f);

            // Switch (left)
            float pad = 12f * Scale;
            float switchW = 28f * Scale;
            float switchH = 16f * Scale;
            var switchMin = new Vector2(cursor.X + pad, cursor.Y + (rowH - switchH) * 0.5f);
            var switchMax = switchMin + new Vector2(switchW, switchH);

            uint trackCol = ImGui.GetColorU32(value ? AccentCyan : new Vector4(0.30f, 0.32f, 0.35f, 1f));
            dl.AddRectFilled(switchMin, switchMax, trackCol, switchH * 0.5f);

            float knobR = switchH * 0.45f;
            float knobX = value ? switchMax.X - knobR - 2f * Scale : switchMin.X + knobR + 2f * Scale;
            float knobY = (switchMin.Y + switchMax.Y) * 0.5f;
            dl.AddCircleFilled(new Vector2(knobX, knobY), knobR, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)));

            // Label
            var labelPos = new Vector2(switchMax.X + 12f * Scale, cursor.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(labelPos, ImGui.GetColorU32(value ? RowText : RowTextDim), label);

            return clicked;
        }

        /// <summary>
        /// Integer stepper with <c>– value +</c> layout. Buttons are large enough for
        /// remote use and auto-repeat while held (via <c>ImGui.PushButtonRepeat</c>)
        /// so going from 60 → 240 doesn't require a click per step.
        /// </summary>
        public static bool Stepper(string label, ref int value, int min, int max, int step = 1, string? format = null, string? tooltip = null)
        {
            float rowH = 36f * Scale;
            float btnW = 32f * Scale;
            float availW = ImGui.GetContentRegionAvail().X;

            ImGui.PushID(label);
            var cursor = ImGui.GetCursorScreenPos();

            // Background
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(cursor, cursor + new Vector2(availW, rowH), ImGui.GetColorU32(RowBg), 4f);

            // Label (left half)
            float labelW = availW * 0.55f;
            var labelPos = new Vector2(cursor.X + 12f * Scale, cursor.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(labelPos, ImGui.GetColorU32(RowText), label);

            // Stepper cluster (right half)
            float clusterRight = cursor.X + availW - 8f * Scale;
            float valueW = 64f * Scale;
            float valueX = clusterRight - btnW - valueW;
            float minusX = valueX - btnW;
            float btnY = cursor.Y + (rowH - btnW) * 0.5f;

            bool changed = false;

            ImGui.PushItemFlag(ImGuiItemFlags.ButtonRepeat, true);

            ImGui.SetCursorScreenPos(new Vector2(minusX, btnY));
            if (ImGui.Button("-", new Vector2(btnW, btnW)))
            {
                int n = Math.Max(min, value - step);
                if (n != value) { value = n; changed = true; }
            }

            ImGui.SetCursorScreenPos(new Vector2(valueX, btnY));
            string text;
            if (_intLabelCache.TryGetValue(label, out var cachedInt)
                && cachedInt.val == value
                && cachedInt.format == format)
            {
                text = cachedInt.text;
            }
            else
            {
                text = format is null ? value.ToString() : string.Format(format, value);
                _intLabelCache[label] = (value, format, text);
            }
            var textSize = ImGui.CalcTextSize(text);
            var textPos = new Vector2(valueX + (valueW - textSize.X) * 0.5f, cursor.Y + (rowH - textSize.Y) * 0.5f);
            dl.AddText(textPos, ImGui.GetColorU32(AccentCyan), text);

            ImGui.SetCursorScreenPos(new Vector2(clusterRight - btnW, btnY));
            if (ImGui.Button("+", new Vector2(btnW, btnW)))
            {
                int n = Math.Min(max, value + step);
                if (n != value) { value = n; changed = true; }
            }

            ImGui.PopItemFlag();

            // Advance cursor past the row
            ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + rowH + 4f * Scale));
            ImGui.Dummy(new Vector2(availW, 0));

            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);

            ImGui.PopID();
            return changed;
        }

        /// <summary>
        /// Float stepper variant. Use for values like UI Scale where 0.1 steps make sense.
        /// </summary>
        public static bool StepperFloat(string label, ref float value, float min, float max, float step, string format = "{0:0.0}", string? tooltip = null)
        {
            float rowH = 36f * Scale;
            float btnW = 32f * Scale;
            float availW = ImGui.GetContentRegionAvail().X;

            ImGui.PushID(label);
            var cursor = ImGui.GetCursorScreenPos();

            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(cursor, cursor + new Vector2(availW, rowH), ImGui.GetColorU32(RowBg), 4f);

            var labelPos = new Vector2(cursor.X + 12f * Scale, cursor.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(labelPos, ImGui.GetColorU32(RowText), label);

            float clusterRight = cursor.X + availW - 8f * Scale;
            float valueW = 64f * Scale;
            float valueX = clusterRight - btnW - valueW;
            float minusX = valueX - btnW;
            float btnY = cursor.Y + (rowH - btnW) * 0.5f;

            bool changed = false;

            ImGui.PushItemFlag(ImGuiItemFlags.ButtonRepeat, true);

            ImGui.SetCursorScreenPos(new Vector2(minusX, btnY));
            if (ImGui.Button("-", new Vector2(btnW, btnW)))
            {
                float n = MathF.Max(min, value - step);
                if (MathF.Abs(n - value) > 1e-4f) { value = n; changed = true; }
            }

            ImGui.SetCursorScreenPos(new Vector2(valueX, btnY));
            string text;
            if (_floatLabelCache.TryGetValue(label, out var cachedFloat)
                && cachedFloat.val == value
                && cachedFloat.format == format)
            {
                text = cachedFloat.text;
            }
            else
            {
                text = string.Format(format, value);
                _floatLabelCache[label] = (value, format, text);
            }
            var textSize = ImGui.CalcTextSize(text);
            var textPos = new Vector2(valueX + (valueW - textSize.X) * 0.5f, cursor.Y + (rowH - textSize.Y) * 0.5f);
            dl.AddText(textPos, ImGui.GetColorU32(AccentCyan), text);

            ImGui.SetCursorScreenPos(new Vector2(clusterRight - btnW, btnY));
            if (ImGui.Button("+", new Vector2(btnW, btnW)))
            {
                float n = MathF.Min(max, value + step);
                if (MathF.Abs(n - value) > 1e-4f) { value = n; changed = true; }
            }

            ImGui.PopItemFlag();

            ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + rowH + 4f * Scale));
            ImGui.Dummy(new Vector2(availW, 0));

            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);

            ImGui.PopID();
            return changed;
        }

        /// <summary>
        /// Full-width combo row. The whole row is the hit target. Inside it the
        /// user steps through options with chunky <c>&lt;</c> / <c>&gt;</c> buttons
        /// (auto-repeat while held) — far easier than a tiny dropdown over AnyDesk.
        /// Clicking the centered value opens a regular ImGui combo as a fallback for
        /// keyboard / mouse users who want random access.
        /// </summary>
        /// <returns>True if the selected index changed this frame.</returns>
        public static bool ComboRow(string label, ref int value, IReadOnlyList<string> options, string? tooltip = null)
        {
            if (options is null || options.Count == 0)
                return false;

            float rowH = 36f * Scale;
            float btnW = 32f * Scale;
            float availW = ImGui.GetContentRegionAvail().X;

            ImGui.PushID(label);
            var cursor = ImGui.GetCursorScreenPos();

            // Background
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(cursor, cursor + new Vector2(availW, rowH), ImGui.GetColorU32(RowBg), 4f);

            // Label (left)
            var labelPos = new Vector2(cursor.X + 12f * Scale, cursor.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(labelPos, ImGui.GetColorU32(RowText), label);

            // Stepper cluster (right)
            float clusterRight = cursor.X + availW - 8f * Scale;
            float valueW = 120f * Scale;
            float valueX = clusterRight - btnW - valueW;
            float minusX = valueX - btnW;
            float btnY = cursor.Y + (rowH - btnW) * 0.5f;

            int clamped = Math.Clamp(value, 0, options.Count - 1);
            bool changed = false;

            ImGui.PushItemFlag(ImGuiItemFlags.ButtonRepeat, true);

            ImGui.SetCursorScreenPos(new Vector2(minusX, btnY));
            if (ImGui.Button("<", new Vector2(btnW, btnW)))
            {
                int n = (clamped - 1 + options.Count) % options.Count;
                if (n != clamped) { value = n; clamped = n; changed = true; }
            }

            // Center value, also clickable to open a stock combo (keyboard fallback).
            ImGui.SetCursorScreenPos(new Vector2(valueX, btnY));
            string text = options[clamped];
            // Invisible click target spans the value width so users can click anywhere
            // on the label to open the dropdown.
            if (ImGui.InvisibleButton("##value", new Vector2(valueW, btnW)))
                ImGui.OpenPopup("##combo_popup");

            var textSize = ImGui.CalcTextSize(text);
            var textPos = new Vector2(valueX + (valueW - textSize.X) * 0.5f, cursor.Y + (rowH - textSize.Y) * 0.5f);
            dl.AddText(textPos, ImGui.GetColorU32(AccentCyan), text);

            ImGui.SetCursorScreenPos(new Vector2(clusterRight - btnW, btnY));
            if (ImGui.Button(">", new Vector2(btnW, btnW)))
            {
                int n = (clamped + 1) % options.Count;
                if (n != clamped) { value = n; clamped = n; changed = true; }
            }

            ImGui.PopItemFlag();

            if (ImGui.BeginPopup("##combo_popup"))
            {
                for (int i = 0; i < options.Count; i++)
                {
                    bool selected = i == clamped;
                    if (ImGui.Selectable(options[i], selected))
                    {
                        if (i != clamped) { value = i; changed = true; }
                        ImGui.CloseCurrentPopup();
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndPopup();
            }

            // Advance cursor past the row
            ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + rowH + 4f * Scale));
            ImGui.Dummy(new Vector2(availW, 0));

            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);

            ImGui.PopID();
            return changed;
        }

        /// <summary>
        /// Collapsed-by-default "Advanced" group. Use to hide rarely-used controls
        /// from new / casual users — they stay one click away but don't crowd the
        /// row-based main settings flow.
        /// </summary>
        /// <param name="label">Header label (defaults to "Advanced").</param>
        /// <returns>True when the group is open and its contents should be drawn.</returns>
        public static bool BeginAdvanced(string label = "Advanced")
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Header,        new Vector4(0.18f, 0.20f, 0.23f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.24f, 0.26f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive,  new Vector4(0.30f, 0.32f, 0.36f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          RowTextDim);
            bool open = ImGui.CollapsingHeader($"\u25be  {label}");
            ImGui.PopStyleColor(4);
            if (open)
            {
                ImGui.Indent(8f * Scale);
            }
            return open;
        }

        /// <summary>Closes a <see cref="BeginAdvanced"/> block. Only call when it returned true.</summary>
        public static void EndAdvanced()
        {
            ImGui.Unindent(8f * Scale);
            ImGui.Spacing();
        }
    }
}
