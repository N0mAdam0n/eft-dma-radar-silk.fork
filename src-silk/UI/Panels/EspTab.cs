// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Features.Ballistics;
using eft_dma_radar.Silk.UI.Widgets;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        private static readonly string[] _espRenderModes = ["无", "骨骼", "方框", "头点"];
        private static readonly string[] _espCrosshairTypes = ["加号", "十字", "圆圈", "点", "方块", "菱形"];

        private static List<MonitorInfo>? _monitors;
        private static string[]? _monitorNames;

        private static void RefreshMonitors()
        {
            _monitors = MonitorInfo.GetAllMonitors();
            _monitorNames = _monitors.Select(m => m.DisplayName).ToArray();
        }

        private static void DrawEspTab()
        {
            ImGui.Spacing();

            // ── Window state ──
            bool open = eft_dma_radar.Silk.UI.ESP.EspWindow.IsOpen;
            if (UIControls.ToggleRow("开启透视窗口", ref open))
            {
                eft_dma_radar.Silk.UI.ESP.EspWindow.Toggle();
                Config.ShowEspWidget = eft_dma_radar.Silk.UI.ESP.EspWindow.IsOpen;
                Config.MarkDirty();
            }

            int espFps = Config.EspTargetFps;
            if (UIControls.Stepper("透视目标帧率", ref espFps, 0, 360, 5,
                tooltip: "透视窗口的渲染帧率（0 = 无限制）。\n独立于雷达帧率。"))
            {
                Config.EspTargetFps = espFps;
                eft_dma_radar.Silk.UI.ESP.EspWindow.ApplyTargetFps();
                Config.MarkDirty();
            }

            UIControls.Section("显示器");

            if (_monitors is null || _monitorNames is null)
                RefreshMonitors();

            int targetScreen = Config.EspTargetScreen;
            if (UIControls.ComboRow("目标显示器", ref targetScreen, _monitorNames!,
                "透视窗口在哪个显示器上打开。\n使用「将透视移动到显示器」来重新定位正在运行的窗口。"))
            {
                Config.EspTargetScreen = targetScreen;
                Config.MarkDirty();
            }

            if (ImGui.SmallButton("刷新显示器"))
                RefreshMonitors();

            if (eft_dma_radar.Silk.UI.ESP.EspWindow.IsOpen)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("将透视移动到显示器"))
                    eft_dma_radar.Silk.UI.ESP.EspWindow.ApplyTargetMonitor();
            }

            UIControls.Section(Chinese.E("Players"));

            bool showPlayers = Config.EspShowPlayers;
            if (UIControls.ToggleRow(Chinese.E("Show Players"), ref showPlayers))
            {
                Config.EspShowPlayers = showPlayers;
                Config.MarkDirty();
            }

            int mode = Config.EspRenderMode;
            if (UIControls.ComboRow(Chinese.E("Render Mode"), ref mode, _espRenderModes,
                "每个玩家的绘制方式。\n也可以通过热键循环切换。"))
            {
                Config.EspRenderMode = mode;
                Config.MarkDirty();
            }

            if (mode == 2) // Box
            {
                bool bones = Config.EspShowBones;
                if (UIControls.ToggleRow(Chinese.E("Show Bones Inside Box"), ref bones))
                {
                    Config.EspShowBones = bones;
                    Config.MarkDirty();
                }
            }

            float pDist = Config.EspPlayerDistance;
            if (UIControls.StepperFloat(Chinese.E("Max Distance"), ref pDist, 10f, 2000f, 10f, "{0:0}m",
                "超出此距离的玩家不会被绘制"))
            {
                Config.EspPlayerDistance = pDist;
                Config.MarkDirty();
            }

            UIControls.Section(Chinese.E("Loot"));

            bool showLoot = Config.EspShowLoot;
            if (UIControls.ToggleRow(Chinese.E("Show Loot"), ref showLoot))
            {
                Config.EspShowLoot = showLoot;
                Config.MarkDirty();
            }

            float lDist = Config.EspLootDistance;
            if (UIControls.StepperFloat(Chinese.E("Max Distance"), ref lDist, 10f, 500f, 5f, "{0:0}m",
                "超出此距离的物资不会被绘制"))
            {
                Config.EspLootDistance = lDist;
                Config.MarkDirty();
            }

            UIControls.Section(Chinese.E("Crosshair"));

            bool crosshair = Config.EspShowCrosshair;
            if (UIControls.ToggleRow(Chinese.E("Show Crosshair"), ref crosshair))
            {
                Config.EspShowCrosshair = crosshair;
                Config.MarkDirty();
            }

            if (Config.EspShowCrosshair)
            {
                ImGui.Indent(16);

                int cType = Config.EspCrosshairType;
                if (UIControls.ComboRow(Chinese.E("Style"), ref cType, _espCrosshairTypes,
                    Chinese.E("Crosshair Style Tooltip")))
                {
                    Config.EspCrosshairType = cType;
                    Config.MarkDirty();
                }

                float cScale = Config.EspCrosshairScale;
                if (UIControls.StepperFloat(Chinese.E("Scale"), ref cScale, 0.5f, 5f, 0.1f, "{0:0.0}x",
                    Chinese.E("Crosshair Scale Tooltip")))
                {
                    Config.EspCrosshairScale = cScale;
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }

            UIControls.Section(Chinese.E("HUD"));

            bool showFps = Config.EspShowFps;
            if (UIControls.ToggleRow(Chinese.E("Show FPS"), ref showFps))
            {
                Config.EspShowFps = showFps;
                Config.MarkDirty();
            }

            bool showStatus = Config.EspShowStatusText;
            if (UIControls.ToggleRow(Chinese.E("Show Status Text"), ref showStatus, "显示状态文本叠加层（当前因移除内存写入功能而为空）"))
            {
                Config.EspShowStatusText = showStatus;
                Config.MarkDirty();
            }

            bool showEnergyHydration = Config.EspShowEnergyHydration;
            if (UIControls.ToggleRow(Chinese.E("Show Energy / Hydration"), ref showEnergyHydration, "右下角显示本地玩家的能量和水分条"))
            {
                Config.EspShowEnergyHydration = showEnergyHydration;
                Config.MarkDirty();
            }

            UIControls.Section(Chinese.E("Ballistics (debug)"));

            var bcfg = Config.Ballistics ??= new BallisticsConfig();

            bool ballEnabled = bcfg.Enabled;
            if (UIControls.ToggleRow(Chinese.E("Enable Ballistics"), ref ballEnabled,
                "弹道模拟 + 调试叠加的总开关。"))
            {
                bcfg.Enabled = ballEnabled;
                Config.MarkDirty();
            }

            if (bcfg.Enabled)
            {
                ImGui.Indent(16);

                bool drawPredicted = bcfg.DrawPredictedTrajectory;
                if (UIControls.ToggleRow(Chinese.E("Predicted Arc (red)"), ref drawPredicted,
                    "从枪口到预测撞击点的模拟轨迹。"))
                {
                    bcfg.DrawPredictedTrajectory = drawPredicted;
                    Config.MarkDirty();
                }

                bool drawLive = bcfg.DrawLiveShots;
                if (UIControls.ToggleRow(Chinese.E("Live Tracers (green)"), ref drawLive,
                    "从游戏 BallisticsCalculator.Shots 列表读取的实时子弹轨迹。"))
                {
                    bcfg.DrawLiveShots = drawLive;
                    Config.MarkDirty();
                }

                bool showHud = bcfg.ShowDebugHud;
                if (UIControls.ToggleRow(Chinese.E("Debug HUD Window"), ref showHud,
                    "弹药/枪口速度/落点表/G1 来源的浮动窗口。"))
                {
                    bcfg.ShowDebugHud = showHud;
                    BallisticsDebugWidget.IsOpen = showHud;
                    Config.MarkDirty();
                }

                bool liveG1 = bcfg.UseGameG1Table;
                if (UIControls.ToggleRow(Chinese.E("Use Live G1 Table"), ref liveG1,
                    "观察到子弹后，用游戏自己的 G1 表替换硬编码表。"))
                {
                    bcfg.UseGameG1Table = liveG1;
                    if (!liveG1) G1Table.Reset();
                    Config.MarkDirty();
                }

                float lineWidth = bcfg.LineWidth;
                if (UIControls.StepperFloat(Chinese.E("Line Width"), ref lineWidth, 0.5f, 6f, 0.25f, "{0:0.0}px",
                    "预测和实时弹道线的描边宽度。"))
                {
                    bcfg.LineWidth = lineWidth;
                    Config.MarkDirty();
                }

                int samples = bcfg.PredictedSamples;
                if (UIControls.Stepper(Chinese.E("Predicted Samples"), ref samples, 8, 512, 8,
                    tooltip: "沿预测弧采样的点数。"))
                {
                    bcfg.PredictedSamples = samples;
                    Config.MarkDirty();
                }

                float maxDist = bcfg.PredictedMaxDistance;
                if (UIControls.StepperFloat(Chinese.E("Predicted Max Distance"), ref maxDist, 25f, 2000f, 25f, "{0:0}m",
                    "从枪口开始，预测弧在此距离后停止。"))
                {
                    bcfg.PredictedMaxDistance = maxDist;
                    Config.MarkDirty();
                }

                float lifetime = bcfg.LiveShotLifetime;
                if (UIControls.StepperFloat(Chinese.E("Live Shot Lifetime"), ref lifetime, 0.5f, 15f, 0.5f, "{0:0.0}s",
                    "子弹停止移动后，弹道轨迹保持可见的时间。"))
                {
                    bcfg.LiveShotLifetime = lifetime;
                    BallisticsFeature.Instance.Tracker.Lifetime = TimeSpan.FromSeconds(lifetime);
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }

            // ── Grenade trajectory on ESP (actual sampled trails + bold dot + brief persistence) ──
            UIControls.Section(Chinese.E("Grenades (ESP)"));

            var gcfg = Config.EspGrenades ??= new EspGrenadeConfig();

            bool gEnabled = gcfg.Enabled;
            if (UIControls.ToggleRow(Chinese.E("Enable Grenade ESP"), ref gEnabled,
                "在透视窗口上绘制手雷的实际飞行轨迹拖尾（采样路径）以及当前位置加粗点。\n拖尾会在手雷爆炸/落地后按设定时间短暂留存。"))
            {
                gcfg.Enabled = gEnabled;
                Config.MarkDirty();
            }

            if (gcfg.Enabled)
            {
                ImGui.Indent(16);

                float gDist = gcfg.MaxDistance;
                if (UIControls.StepperFloat(Chinese.E("Grenade Max Distance"), ref gDist, 20f, 1000f, 10f, "{0:0}m",
                    "仅显示距离本地玩家此范围内的手雷轨迹和点。"))
                {
                    gcfg.MaxDistance = gDist;
                    Config.MarkDirty();
                }

                float gLife = gcfg.TrailLifetime;
                if (UIControls.StepperFloat(Chinese.E("Grenade Trail Lifetime"), ref gLife, 0.5f, 30f, 0.5f, "{0:0.0}s",
                    "手雷拖尾和最终点在最后一次位置更新（或爆炸）后继续显示的秒数。\n这就是“拖尾并短暂留存”。"))
                {
                    gcfg.TrailLifetime = gLife;
                    Config.MarkDirty();
                }

                float gDot = gcfg.DotRadius;
                if (UIControls.StepperFloat(Chinese.E("Grenade Dot Size"), ref gDot, 1f, 15f, 0.5f, "{0:0.0}px",
                    "手雷当前位置的加粗实心点半径。"))
                {
                    gcfg.DotRadius = gDot;
                    Config.MarkDirty();
                }

                float gWidth = gcfg.TrailWidth;
                if (UIControls.StepperFloat(Chinese.E("Grenade Trail Width"), ref gWidth, 0.5f, 10f, 0.25f, "{0:0.0}px",
                    "轨迹拖尾线的描边宽度。"))
                {
                    gcfg.TrailWidth = gWidth;
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }
        }
    }
}
