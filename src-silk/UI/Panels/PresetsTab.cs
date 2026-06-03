// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.UI.Presets;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        // ── Input state for "Save current as new preset" form ──
        private static string _newPresetName = string.Empty;
        private static string _newPresetDescription = string.Empty;
        private static string? _saveError;

        // ── Per-row deletion confirmation ──
        private static string? _pendingDeleteId;

        // ── Description tooltip text for each toggle row ──
        private static readonly (string Label, string Tooltip, System.Func<RadarPresetEntry, bool> Get, System.Action<RadarPresetEntry, bool> Set)[] _presetToggles =
        [
            (Chinese.P("Battle Mode"),     Chinese.P("Hide non-combat clutter (loot / corpses / containers)."),
                p => p.BattleMode,     (p, v) => p.BattleMode = v),
            (Chinese.P("Show Loot"),       Chinese.P("Show loose loot items on the radar."),
                p => p.ShowLoot,       (p, v) => p.ShowLoot = v),
            (Chinese.P("Show Corpses"),    Chinese.P("Show dead player corpses (recent kills)."),
                p => p.ShowCorpses,    (p, v) => p.ShowCorpses = v),
            (Chinese.P("Show Containers"), Chinese.P("Show static loot containers."),
                p => p.ShowContainers, (p, v) => p.ShowContainers = v),
            (Chinese.P("Show Exfils"),     Chinese.P("Show extraction points."),
                p => p.ShowExfils,     (p, v) => p.ShowExfils = v),
            (Chinese.P("Show Doors"),      Chinese.P("Show keyed / mechanical doors."),
                p => p.ShowDoors,      (p, v) => p.ShowDoors = v),
            (Chinese.P("Show Airdrops"),   Chinese.P("Show airdrop crates."),
                p => p.ShowAirdrops,   (p, v) => p.ShowAirdrops = v),
            (Chinese.P("Show Switches"),   Chinese.P("Show interactable switches / levers / buttons."),
                p => p.ShowSwitches,   (p, v) => p.ShowSwitches = v),
            (Chinese.P("Show Transits"),   Chinese.P("Show map-to-map transit points."),
                p => p.ShowTransits,   (p, v) => p.ShowTransits = v),
            (Chinese.P("Show Aimlines"),   Chinese.P("Show direction lines on player markers."),
                p => p.ShowAimlines,   (p, v) => p.ShowAimlines = v),
            (Chinese.P("Connect Groups"),  Chinese.P("Draw lines between players in the same group."),
                p => p.ConnectGroups,  (p, v) => p.ConnectGroups = v),
            (Chinese.P("High Alert"),      Chinese.P("Extend aimline when an enemy is aiming at you."),
                p => p.HighAlert,      (p, v) => p.HighAlert = v),
            (Chinese.P("Players On Top"),  Chinese.P("Draw players above loot / containers so dots stay visible."),
                p => p.PlayersOnTop,   (p, v) => p.PlayersOnTop = v),
        ];

        private static void DrawPresetsTab()
        {
            ImGui.Spacing();
            PresetManager.EnsureSeeded(Config);

            // ── Header: active preset summary ──
            string activeName = PresetManager.DisplayNameFor(Config, Config.ActivePresetId);
            ImGui.TextColored(UITheme.Cyan, $"{Chinese.P("Active:")}{activeName}");
            if (Config.ActivePresetId == PresetManager.CustomId)
                ImGui.TextDisabled(Chinese.P("(drift — current toggles don't match any saved preset)"));
            else
                ImGui.TextDisabled(Chinese.P("Click Apply on any preset to switch. Edit toggles below to tweak."));

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // ── Preset list ──
            // Iterate over a snapshot since deletes mutate the list mid-loop.
            var snapshot = new List<RadarPresetEntry>(Config.Presets);
            foreach (var preset in snapshot)
                DrawPresetCard(preset);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawSaveCurrentForm();
        }

        private static void DrawPresetCard(RadarPresetEntry preset)
        {
            bool isActive = Config.ActivePresetId == preset.Id;
            bool isBuiltin = PresetManager.IsBuiltin(preset);

            ImGui.PushID(preset.Id);

            // Header row
            string title = isActive ? $"◉ {preset.DisplayName}" : preset.DisplayName;
            string subtitle = isBuiltin ? Chinese.P("built-in") : Chinese.P("user");
            if (isActive)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, UITheme.Cyan);
                ImGui.TextUnformatted(title);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextUnformatted(title);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"  ({subtitle})");

            // Description preview
            if (!string.IsNullOrEmpty(preset.Description))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.68f, 0.72f, 1f));
                ImGui.TextWrapped(preset.Description);
                ImGui.PopStyleColor();
            }

            // Buttons row
            float btnH = 26f * Config.UIScale;
            if (!isActive)
            {
                if (ImGui.Button(Chinese.P("Apply"), new Vector2(80f * Config.UIScale, btnH)))
                    PresetManager.Apply(preset.Id, Config);
                ImGui.SameLine();
            }
            else
            {
                // Active preset — offer "Bake current toggles into this preset" if drifted
                if (!PresetManager.MatchesActive(Config))
                {
                    if (ImGui.Button(Chinese.P("Save current toggles"), new Vector2(160f * Config.UIScale, btnH)))
                    {
                        PresetManager.UpdateInPlace(Config, preset.Id);
                        Shell.ToastManager.Info($"已保存当前开关到 '{preset.DisplayName}'");
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Chinese.P("Overwrite this preset's stored toggle values\nwith the radar's current live state."));
                    ImGui.SameLine();
                }
            }

            if (isBuiltin)
            {
                if (ImGui.Button(Chinese.P("Reset to defaults"), new Vector2(140f * Config.UIScale, btnH)))
                {
                    PresetManager.ResetToDefaults(Config, preset.Id);
                    if (isActive)
                        PresetManager.Apply(preset.Id, Config);
                    Shell.ToastManager.Info($"已将 '{preset.DisplayName}' 重置为默认");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Chinese.P("Restore name, description, and toggle values\nto the hardcoded baseline for this preset."));
                ImGui.SameLine();
            }
            else
            {
                if (_pendingDeleteId == preset.Id)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
                    if (ImGui.Button(Chinese.P("Confirm delete"), new Vector2(120f * Config.UIScale, btnH)))
                    {
                        var name = preset.DisplayName;
                        PresetManager.Delete(Config, preset.Id);
                        _pendingDeleteId = null;
                        Shell.ToastManager.Info($"已删除预设 '{name}'");
                        ImGui.PopStyleColor();
                        ImGui.PopID();
                        ImGui.Separator();
                        return;
                    }
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    if (ImGui.Button(Chinese.P("Cancel"), new Vector2(80f * Config.UIScale, btnH)))
                        _pendingDeleteId = null;
                }
                else
                {
                    if (ImGui.Button(Chinese.P("Delete"), new Vector2(80f * Config.UIScale, btnH)))
                        _pendingDeleteId = preset.Id;
                }
                ImGui.SameLine();
            }

            // Foldout for details (name / description / toggles)
            bool open = ImGui.TreeNodeEx("##details", ImGuiTreeNodeFlags.None, Chinese.P("Edit"));
            if (open)
            {
                ImGui.Spacing();
                ImGui.PushItemWidth(280f * Config.UIScale);

                string name = preset.DisplayName;
                if (ImGui.InputText(Chinese.P("Name"), ref name, 64) && name != preset.DisplayName)
                    PresetManager.Rename(Config, preset.Id, name);

                string desc = preset.Description;
                if (ImGui.InputTextMultiline(Chinese.P("Description"), ref desc, 256,
                        new Vector2(280f * Config.UIScale, 60f * Config.UIScale))
                    && desc != preset.Description)
                {
                    PresetManager.SetDescription(Config, preset.Id, desc);
                }
                ImGui.PopItemWidth();

                ImGui.Spacing();
                ImGui.TextDisabled(Chinese.P("Toggles (saved in this preset):"));

                // Two-column layout of toggle checkboxes
                int n = _presetToggles.Length;
                int rows = (n + 1) / 2;
                if (ImGui.BeginTable("##presetToggles", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    for (int r = 0; r < rows; r++)
                    {
                        ImGui.TableNextRow();
                        for (int c = 0; c < 2; c++)
                        {
                            int i = r + c * rows;
                            if (i >= n) { ImGui.TableNextColumn(); continue; }
                            ImGui.TableNextColumn();
                            var t = _presetToggles[i];
                            bool v = t.Get(preset);
                            if (ImGui.Checkbox($"{t.Label}##{preset.Id}_{i}", ref v))
                            {
                                t.Set(preset, v);
                                Config.MarkDirty();
                                // If this preset is currently active, re-apply so the
                                // live radar state immediately reflects the new toggle.
                                if (isActive)
                                    PresetManager.Apply(preset.Id, Config);
                            }
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(t.Tooltip);
                        }
                    }
                    ImGui.EndTable();
                }

                ImGui.TreePop();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PopID();
        }

        private static void DrawSaveCurrentForm()
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UITheme.Cyan);
            ImGui.TextUnformatted(Chinese.P("Save current radar state as a new preset"));
            ImGui.PopStyleColor();
            ImGui.TextDisabled(Chinese.P("Captures all 13 toggles from the live radar into a new named preset."));
            ImGui.Spacing();

            ImGui.PushItemWidth(280f * Config.UIScale);
            ImGui.InputText(Chinese.P("Name") + "##newPreset", ref _newPresetName, 64);
            ImGui.InputText(Chinese.P("Description") + "##newPreset", ref _newPresetDescription, 256);
            ImGui.PopItemWidth();

            bool canSave = !string.IsNullOrWhiteSpace(_newPresetName);
            if (!canSave) ImGui.BeginDisabled();
            if (ImGui.Button(Chinese.P("Save as new preset"), new Vector2(180f * Config.UIScale, 30f * Config.UIScale)))
            {
                var entry = PresetManager.SaveCurrentAsNew(Config, _newPresetName.Trim(), _newPresetDescription.Trim());
                if (entry is null)
                {
                    _saveError = Chinese.P("Name is required.");
                }
                else
                {
                    _saveError = null;
                    Shell.ToastManager.Info($"已保存预设 '{entry.DisplayName}'");
                    _newPresetName = string.Empty;
                    _newPresetDescription = string.Empty;
                }
            }
            if (!canSave) ImGui.EndDisabled();

            if (_saveError is not null)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.90f, 0.45f, 0.45f, 1f));
                ImGui.TextUnformatted(_saveError);
                ImGui.PopStyleColor();
            }
        }
    }
}
