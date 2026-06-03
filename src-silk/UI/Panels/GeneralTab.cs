// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        private static async Task ToggleWebRadarAsync(bool enable)
        {
            try
            {
                if (enable)
                {
                    await eft_dma_radar.Silk.Web.WebRadarServer.StartAsync(
                        Config.WebRadarPort,
                        TimeSpan.FromMilliseconds(Config.WebRadarTickMs),
                        Config.WebRadarUPnP);
                }
                else
                {
                    await eft_dma_radar.Silk.Web.WebRadarServer.StopAsync();
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[WebRadar] Toggle error: {ex.Message}");
            }
        }

        private static void DrawGeneralTab()
        {
            ImGui.Spacing();

            UIControls.Section("显示");

            float uiScale = Config.UIScale;
            if (UIControls.StepperFloat("界面缩放", ref uiScale, 0.5f, 2.0f, 0.1f, "{0:0.0}x",
                "缩放雷达画布渲染"))
            {
                Config.UIScale = uiScale;
                Config.MarkDirty();
            }

            int fps = Config.TargetFps;
            if (UIControls.Stepper("目标帧率", ref fps, 0, 360, 5,
                tooltip: "最大每秒帧数（0 = 无限制）。长按 +/- 快速调整。"))
            {
                Config.TargetFps = fps;
                RadarWindow.Window.FramesPerSecond = fps;
                Config.MarkDirty();
            }

            UIControls.Section("模式");

            bool battleMode = Config.BattleMode;
            if (UIControls.ToggleRow("战斗模式 [B]", ref battleMode,
                "隐藏物资和杂物；仅关注玩家"))
            {
                Config.SetBattleMode(battleMode);
            }

            ImGui.Spacing();
            ImGui.SeparatorText("藏身处");

            bool hideoutEnabled = Config.HideoutEnabled;
            if (ImGui.Checkbox("启用藏身处", ref hideoutEnabled))
            {
                Config.HideoutEnabled = hideoutEnabled;
                Config.MarkDirty();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("进入藏身处时读取保险箱物品和区域升级");

            if (Config.HideoutEnabled)
            {
                ImGui.Indent(16);
                bool autoRefresh = Config.HideoutAutoRefresh;
                if (ImGui.Checkbox("自动刷新", ref autoRefresh))
                {
                    Config.HideoutAutoRefresh = autoRefresh;
                    Config.MarkDirty();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("进入藏身处时自动刷新保险箱和区域数据");
                ImGui.Unindent(16);
            }

            ImGui.Spacing();
            ImGui.SeparatorText("对局导出");

            bool matchDump = Config.EnableMatchDump;
            if (ImGui.Checkbox("启用对局导出", ref matchDump))
            {
                Config.EnableMatchDump = matchDump;
                Config.MarkDirty();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Chinese.S("Serialize all radar data (players, loot, corpses, exfils…) to a JSON file in the dumps\\ folder.\nUse the button below to trigger a snapshot manually."));

            if (Config.EnableMatchDump)
            {
                ImGui.Indent(16);
                bool canDump = Memory.InRaid;
                if (!canDump)
                    ImGui.BeginDisabled();
                if (ImGui.Button("\u21a7 立即导出对局"))
                    Memory.Game?.DumpMatchNow();
                    //Memory.Game?.DumpContainersNow();
                if (!canDump)
                    ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(canDump
                        ? "立即将完整对局快照写入 dumps\\ "
                        : "仅在活动对局中可用");
                ImGui.Unindent(16);
            }

            ImGui.Spacing();
            ImGui.SeparatorText(Chinese.S("Radar"));

            {
                bool canRestart = Memory.InRaid || Memory.InHideout;
                if (!canRestart)
                    ImGui.BeginDisabled();

                if (ImGui.Button("\u21bb " + Chinese.S("Restart Radar")))
                    Memory.RestartRadar = true;

                if (!canRestart)
                    ImGui.EndDisabled();

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(canRestart
                        ? Chinese.S("Restart the radar (re-detect game world, players, loot)")
                        : Chinese.S("Only available during a raid or in the hideout"));

                ImGui.SameLine();
                if (ImGui.Button("\u2728 显示欢迎导览"))
                    eft_dma_radar.Silk.UI.Shell.FirstRunTour.Open();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("重放首次运行的用户体验导览（侧边栏、状态栏、预设、命令面板）。");
            }

            ImGui.Spacing();
            ImGui.SeparatorText("Web 雷达");

            bool webEnabled = Config.WebRadarEnabled;
            if (ImGui.Checkbox("启用 Web 雷达", ref webEnabled))
            {
                Config.WebRadarEnabled = webEnabled;
                Config.MarkDirty();
                _ = ToggleWebRadarAsync(webEnabled);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("启动/停止 Web 雷达 HTTP 服务器。\n可在网络上任何设备的浏览器中访问。");

            if (Config.WebRadarEnabled)
            {
                ImGui.Indent(16);

                ImGui.SetNextItemWidth(120);
                int port = Config.WebRadarPort;
                if (ImGui.InputInt(Chinese.S("Port"), ref port, 0, 0))
                {
                    if (port is >= 1024 and <= 65535)
                    {
                        Config.WebRadarPort = port;
                        Config.MarkDirty();
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Chinese.S("HTTP port (requires restart to take effect)"));

                ImGui.SetNextItemWidth(120);
                int tickMs = Config.WebRadarTickMs;
                if (ImGui.SliderInt(Chinese.S("Tick (ms)"), ref tickMs, 20, 200))
                {
                    Config.WebRadarTickMs = tickMs;
                    Config.MarkDirty();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Chinese.S("Update interval for the web radar data"));

                bool upnp = Config.WebRadarUPnP;
                if (ImGui.Checkbox(Chinese.S("UPnP / NAT-PMP"), ref upnp))
                {
                    Config.WebRadarUPnP = upnp;
                    Config.MarkDirty();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Chinese.S("Automatically forward the port on your router via UPnP.\nEnables access from outside your network.\nTakes effect on next server start."));

                if (eft_dma_radar.Silk.Web.WebRadarServer.IsRunning)
                {
                    ImGui.TextColored(new Vector4(0.26f, 0.84f, 0.50f, 1f),
                        $"\u25cf {Chinese.S("Running on port")} {Config.WebRadarPort}");

                    // Private address
                    ImGui.Spacing();
                    var privateAddr = eft_dma_radar.Silk.Web.WebRadarServer.PrivateAddress;
                    if (!string.IsNullOrEmpty(privateAddr))
                    {
                        ImGui.Text(Chinese.S("Private:"));
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.55f, 0.83f, 1f, 1f), privateAddr);
                        ImGui.SameLine();
                        if (ImGui.SmallButton("\uf0c5 Copy##private"))
                            ImGui.SetClipboardText(privateAddr);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(Chinese.S("Copy private (LAN) address to clipboard"));
                        ImGui.SameLine();
                        if (ImGui.SmallButton("\u2197 Open##private"))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(privateAddr) { UseShellExecute = true });
                            }
                            catch { }
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(Chinese.S("Open in default browser"));
                    }

                    // Public address
                    var publicAddr = eft_dma_radar.Silk.Web.WebRadarServer.PublicAddress;
                    if (!string.IsNullOrEmpty(publicAddr))
                    {
                        ImGui.Text(Chinese.S("Public:") + " ");
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(1f, 0.85f, 0.40f, 1f), publicAddr);
                        ImGui.SameLine();
                        if (ImGui.SmallButton("\uf0c5 Copy##public"))
                            ImGui.SetClipboardText(publicAddr);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(Chinese.S("Copy public (WAN) address to clipboard"));
                        ImGui.SameLine();
                        if (ImGui.SmallButton("\u2197 Open##public"))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(publicAddr) { UseShellExecute = true });
                            }
                            catch { }
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(Chinese.S("Open in default browser"));
                    }
                    else if (string.IsNullOrEmpty(publicAddr) && !string.IsNullOrEmpty(privateAddr))
                    {
                        ImGui.TextColored(new Vector4(0.60f, 0.60f, 0.60f, 1f),
                            Chinese.S("Private:  Detecting..."));
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.60f, 0.60f, 0.60f, 1f),
                        "\u25cb " + Chinese.S("Stopped"));
                }

                ImGui.Unindent(16);
            }
        }
    }
}
