// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.DMA;
using eft_dma_radar.Silk.UI.Maps;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        private static void DrawMapTab()
        {
            ImGui.Spacing();

            int zoom = RadarWindow.Zoom;
            if (UIControls.Stepper("缩放", ref zoom, 1, 200, 5,
                tooltip: "雷达缩放级别（越小越放大）"))
                RadarWindow.Zoom = zoom;

            bool freeMode = RadarWindow.FreeMode;
            if (UIControls.ToggleRow("自由模式 [F]", ref freeMode, "在跟随玩家和自由平移之间切换"))
                RadarWindow.FreeMode = freeMode;

            bool useSatellite = Config.UseSatelliteMap;
            if (UIControls.ToggleRow("卫星地图", ref useSatellite, "使用 assets.tarkov.dev 的卫星图像（不支持的地图回退到 SVG）"))
            {
                Config.UseSatelliteMap = useSatellite;
                // Mutual exclusion — enabling satellite turns off tarkov.dev SVG (satellite wins).
                if (useSatellite) Config.UseTarkovDevMap = false;
                Config.MarkDirty();
                Maps.MapManager.ReloadCurrent();
            }

            bool useTarkovDevSvg = Config.UseTarkovDevMap;
            if (UIControls.ToggleRow(Chinese.M("Tarkov.dev SVG 地图"), ref useTarkovDevSvg,
                "从 assets.tarkov.dev 下载地图（Customs.svg, Woods.svg, ...）而不是使用内置 SVG。\n持久缓存于 %AppData%\\eft-dma-radar-silk\\tarkov-dev-maps\\。\n卫星地图开启时忽略。下载失败时回退到内置 SVG。"))
            {
                Config.UseTarkovDevMap = useTarkovDevSvg;
                if (useTarkovDevSvg) Config.UseSatelliteMap = false;
                Config.MarkDirty();
                Maps.MapManager.ReloadCurrent();
            }

            // Rotation control for tarkov.dev SVG maps (only meaningful when active).
            if (Config.UseTarkovDevMap)
            {
                ImGui.Indent(16);
                int rot = Config.TarkovDevMapRotation;
                int rotIdx = rot switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };
                string[] rotLabels = [
                    Chinese.M("0° (raw)"),
                    Chinese.M("90° (left)"),
                    "180°",
                    Chinese.M("270° (right)")
                ];
                ImGui.SetNextItemWidth(160);
                if (ImGui.Combo(Chinese.M("Rotation") + "##TarkovDevRot", ref rotIdx, rotLabels, rotLabels.Length))
                {
                    int[] rotValues = [0, 90, 180, 270];
                    Config.TarkovDevMapRotation = rotValues[rotIdx];
                    Config.MarkDirty();
                    Maps.MapManager.ReloadCurrent();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("应用于 tarkov.dev SVG 地图的视觉旋转。tarkov.dev 网站\n通过 Leaflet 的 CRS 将其旋转 180° 显示；在此选择你偏好的方向。\n标记会自动重新对齐。");
                ImGui.Unindent(16);
            }

            DrawMapSetupSection();

            UIControls.Section(Chinese.M("Corpses"));

            bool showCorpses = Config.ShowCorpses;
            if (UIControls.ToggleRow(Chinese.M("Show Corpses"), ref showCorpses, Chinese.M("Show corpse X markers on the radar")))
            {
                Config.ShowCorpses = showCorpses;
                Config.MarkDirty();
            }

            UIControls.Section(Chinese.M("Loot Markers"));

            float dotSize = Config.LootDotSize;
            if (UIControls.StepperFloat(Chinese.M("Dot Size"), ref dotSize, 1.5f, 8f, 0.5f, "{0:0.0} px",
                Chinese.M("Base radius of loot dots. Tier/important bumps are added on top.")))
            {
                Config.LootDotSize = dotSize;
                Config.MarkDirty();
            }

            float labelFont = Config.LootLabelFontSize;
            if (UIControls.StepperFloat(Chinese.M("Label Font"), ref labelFont, 8f, 22f, 1f, "{0:0} px",
                Chinese.M("Font size of loot labels on the radar.")))
            {
                Config.LootLabelFontSize = labelFont;
                Config.MarkDirty();
            }

            bool heightArrows = Config.LootShowHeightArrows;
            if (UIControls.ToggleRow(Chinese.M("Height Arrows (▲/▼)"), ref heightArrows, Chinese.M("Show an up/down arrow on loot that is above or below your floor.")))
            {
                Config.LootShowHeightArrows = heightArrows;
                Config.MarkDirty();
            }

            if (Config.LootShowHeightArrows)
            {
                ImGui.Indent(16);
                float thr = Config.LootHeightArrowThreshold;
                if (UIControls.StepperFloat(Chinese.M("Height Threshold"), ref thr, 0.5f, 5f, 0.25f, "{0:0.00} m",
                    Chinese.M("Vertical distance (±m) before an arrow is drawn.")))
                {
                    Config.LootHeightArrowThreshold = thr;
                    Config.MarkDirty();
                }

                bool showDelta = Config.LootShowHeightDelta;
                if (UIControls.ToggleRow(Chinese.M("Show Height (+/-m)"), ref showDelta, Chinese.M("Append the exact vertical offset in meters to the loot label.")))
                {
                    Config.LootShowHeightDelta = showDelta;
                    Config.MarkDirty();
                }
                ImGui.Unindent(16);
            }

            UIControls.Section(Chinese.M("Containers"));

            bool showContainers = Config.ShowContainers;
            if (UIControls.ToggleRow(Chinese.M("Show Containers"), ref showContainers, Chinese.M("Show static loot containers on the radar (duffle bags, toolboxes, etc.)")))
            {
                Config.ShowContainers = showContainers;
                Config.MarkDirty();
            }

            if (Config.ShowContainers)
            {
                ImGui.Indent(16);
                bool showContainerNames = Config.ShowContainerNames;
                if (UIControls.ToggleRow(Chinese.M("Show Names"), ref showContainerNames, Chinese.M("Show container name labels next to markers")))
                {
                    Config.ShowContainerNames = showContainerNames;
                    Config.MarkDirty();
                }

                bool hideSearched = Config.HideSearchedContainers;
                if (UIControls.ToggleRow(Chinese.M("Hide Searched"), ref hideSearched, Chinese.M("Hide containers that have been opened/searched")))
                {
                    Config.HideSearchedContainers = hideSearched;
                    Config.MarkDirty();
                }

                ImGui.Spacing();
                DrawContainerSelection();
                ImGui.Unindent(16);
            }

            UIControls.Section(Chinese.M("Exfils"));

            bool showExfils = Config.ShowExfils;
            if (UIControls.ToggleRow(Chinese.M("Show Exfils"), ref showExfils, Chinese.M("Show exfiltration points on the radar")))
            {
                Config.ShowExfils = showExfils;
                Config.MarkDirty();
            }

            if (Config.ShowExfils)
            {
                ImGui.Indent(16);

                bool hideInactive = Config.HideInactiveExfils;
                if (UIControls.ToggleRow(Chinese.M("Hide Inactive"), ref hideInactive, Chinese.M("Hide closed or unavailable exfils")))
                {
                    Config.HideInactiveExfils = hideInactive;
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }

            UIControls.Section(Chinese.M("Transits"));

            bool showTransits = Config.ShowTransits;
            if (UIControls.ToggleRow(Chinese.M("Show Transits"), ref showTransits, Chinese.M("Show transit points (map-to-map travel) on the radar")))
            {
                Config.ShowTransits = showTransits;
                Config.MarkDirty();
            }

            UIControls.Section(Chinese.M("Doors"));

            bool showDoors = Config.ShowDoors;
            if (UIControls.ToggleRow(Chinese.M("Show Doors"), ref showDoors, Chinese.M("Show keyed doors on the radar")))
            {
                Config.ShowDoors = showDoors;
                Config.MarkDirty();
            }

            if (Config.ShowDoors)
            {
                ImGui.Indent(16);

                bool showLocked = Config.ShowLockedDoors;
                if (UIControls.ToggleRow(Chinese.M("Show Locked"), ref showLocked, Chinese.M("Show locked doors (red)")))
                {
                    Config.ShowLockedDoors = showLocked;
                    Config.MarkDirty();
                }

                bool showUnlocked = Config.ShowUnlockedDoors;
                if (UIControls.ToggleRow(Chinese.M("Show Unlocked"), ref showUnlocked, Chinese.M("Show open or shut doors (green/orange)")))
                {
                    Config.ShowUnlockedDoors = showUnlocked;
                    Config.MarkDirty();
                }

                bool onlyNearLoot = Config.DoorsOnlyNearLoot;
                if (UIControls.ToggleRow(Chinese.M("Only Near Valuable Loot"), ref onlyNearLoot, Chinese.M("Only show doors near important (high-value) loot items")))
                {
                    Config.DoorsOnlyNearLoot = onlyNearLoot;
                    Config.MarkDirty();
                }

                if (Config.DoorsOnlyNearLoot)
                {
                    ImGui.Indent(16);

                    float proximity = Config.DoorLootProximity;
                    if (UIControls.StepperFloat(Chinese.M("Proximity"), ref proximity, 5f, 100f, 5f, "{0:0} m",
                        Chinese.M("Max distance from door to valuable loot")))
                    {
                        Config.DoorLootProximity = proximity;
                        Config.MarkDirty();
                    }

                    ImGui.Unindent(16);
                }

                ImGui.Unindent(16);
            }

            UIControls.Section(Chinese.M("Explosives & BTR"));

            bool showExplosives = Config.ShowExplosives;
            if (UIControls.ToggleRow(Chinese.M("Show Explosives"), ref showExplosives, Chinese.M("Show grenades, tripwires, and mortar projectiles on the radar")))
            {
                Config.ShowExplosives = showExplosives;
                Config.MarkDirty();
            }

            if (Config.ShowExplosives)
            {
                ImGui.Indent(16);

                bool showTripwireLines = Config.ShowTripwireLines;
                if (UIControls.ToggleRow(Chinese.M("Show Tripwire Lines"), ref showTripwireLines, Chinese.M("Draw a line between tripwire endpoints")))
                {
                    Config.ShowTripwireLines = showTripwireLines;
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }

            bool showBtr = Config.ShowBTR;
            if (UIControls.ToggleRow(Chinese.M("Show BTR"), ref showBtr, Chinese.M("Show the BTR armored vehicle on the radar (Streets/Woods)")))
            {
                Config.ShowBTR = showBtr;
                Config.MarkDirty();
            }

            ImGui.Indent(16);
            bool showBtrRoute = Config.ShowBTRRoute;
            if (UIControls.ToggleRow(Chinese.M("Show BTR Route Stops"), ref showBtrRoute, Chinese.M("Show BTR route stop markers on the radar")))
            {
                Config.ShowBTRRoute = showBtrRoute;
                Config.MarkDirty();
            }
            ImGui.Unindent(16);

            UIControls.Section(Chinese.M("Killfeed Overlay"));

            bool showKf = Config.ShowKillFeed;
            if (UIControls.ToggleRow(Chinese.M("Show Killfeed Overlay"), ref showKf, Chinese.M("Draw kill events on the radar canvas (top-right corner).\nOpen the Killfeed panel (\u2620) for the full table and settings.")))
            {
                Config.ShowKillFeed = showKf;
                Config.MarkDirty();
            }

            if (Config.ShowKillFeed)
            {
                ImGui.Indent(16);

                int maxEnt = Config.KillFeedMaxEntries;
                if (UIControls.Stepper(Chinese.M("Max Entries"), ref maxEnt, 1, 10, 1,
                    tooltip: Chinese.M("Maximum number of kill events visible at once")))
                {
                    Config.KillFeedMaxEntries = maxEnt;
                    Config.MarkDirty();
                }

                int ttl = Config.KillFeedTtlSeconds;
                if (UIControls.Stepper(Chinese.M("Entry TTL"), ref ttl, 5, 600, 5, "{0} s",
                    tooltip: Chinese.M("Seconds before a killfeed entry fades out (5–600).")))
                {
                    Config.KillFeedTtlSeconds = ttl;
                    Config.MarkDirty();
                }

                if (ImGui.Button(Chinese.M("Reset Killfeed Position")))
                {
                    Config.KillFeedPosX = -1f;
                    Config.KillFeedPosY = -1f;
                    Config.MarkDirty();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Chinese.M("Snap the killfeed overlay back to the top-right corner."));

                ImGui.Unindent(16);
            }
        }

        private static void DrawMapSetupSection()
        {
            if (!UIControls.BeginAdvanced(Chinese.M("Map Setup (Calibration)")))
                return;

            var map = MapManager.Map;
            if (map is null)
            {
                ImGui.TextDisabled(Chinese.M("No map loaded."));
                UIControls.EndAdvanced();
                return;
            }

            var cfg = map.Config;

            // Live player position readout (X / Z / Y in EFT world space — Z&Y swapped for display)
            var lp = Memory.LocalPlayer;
            if (lp is not null)
            {
                var pos = lp.Position;
                ImGui.Text($"玩家  X: {pos.X:0.000}   Y: {pos.Z:0.000}   Z: {pos.Y:0.000}");
            }
            else
            {
                ImGui.TextDisabled(Chinese.M("Player position unavailable."));
            }

            ImGui.SetNextItemWidth(160);
            float x = cfg.X;
            if (ImGui.DragFloat(Chinese.M("Map X"), ref x, 1.0f, -10000f, 10000f, "%.2f"))
                cfg.X = x;

            ImGui.SetNextItemWidth(160);
            float y = cfg.Y;
            if (ImGui.DragFloat(Chinese.M("Map Y"), ref y, 1.0f, -10000f, 10000f, "%.2f"))
                cfg.Y = y;

            ImGui.SetNextItemWidth(160);
            float scale = cfg.Scale;
            if (ImGui.DragFloat(Chinese.M("Map Scale"), ref scale, 0.001f, 0.001f, 100f, "%.4f"))
                cfg.Scale = scale;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("运行时校准 — 调整直到你的玩家标记与真实世界位置对齐。\n点击下方的「保存校准」以在重启后保持这些值。");

            // Save / Reset buttons: persist (X, Y, Scale) per primary map ID. The web
            // radar serves the live in-memory MapConfig each tick, so saved values flow
            // to web clients on their next update without a manual refresh.
            ImGui.Spacing();
            string? primaryId = cfg.MapID.Count > 0 ? cfg.MapID[0] : null;
            bool hasOverride = !string.IsNullOrWhiteSpace(primaryId)
                && Config.MapCalibrationOverrides.ContainsKey(primaryId!);

            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(primaryId));
            if (ImGui.Button(Chinese.M("Save Calibration")))
            {
                Config.MapCalibrationOverrides[primaryId!] = new MapCalibration
                {
                    X = cfg.X,
                    Y = cfg.Y,
                    Scale = cfg.Scale,
                };
                Config.MarkDirty();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format("将当前地图 X / Y / 缩放保存到磁盘，键为地图 ID '{0}'。\n下次启动时自动重新加载。Web 雷达客户端会在下一次更新时获取新值。", primaryId));
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(!hasOverride);
            if (ImGui.Button(Chinese.M("Reset to Bundled")))
            {
                Config.MapCalibrationOverrides.Remove(primaryId!);
                Config.MarkDirty();
                // Reload the map so the bundled defaults from the JSON file replace
                // the in-memory overridden values.
                Maps.MapManager.ReloadCurrent();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format("移除 '{0}' 的已保存校准，并从地图 JSON 文件重新加载内置默认值。", primaryId));
            ImGui.EndDisabled();

            if (hasOverride)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(Chinese.M("(saved)"));
            }

            UIControls.EndAdvanced();
        }
    }
}
