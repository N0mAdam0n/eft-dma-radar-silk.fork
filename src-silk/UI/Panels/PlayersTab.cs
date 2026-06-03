// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        private static void DrawPlayersTab()
        {
            ImGui.Spacing();

            UIControls.Section("渲染");

            bool playersOnTop = Config.PlayersOnTop;
            if (UIControls.ToggleRow("玩家置顶", ref playersOnTop, "将玩家绘制在所有其他实体之上"))
            {
                Config.PlayersOnTop = playersOnTop;
                Config.MarkDirty();
            }

            bool connectGroups = Config.ConnectGroups;
            if (UIControls.ToggleRow("连接组", ref connectGroups, "为同一组的玩家绘制连接线"))
            {
                Config.ConnectGroups = connectGroups;
                Config.MarkDirty();
            }

            UIControls.Section("瞄准线");

            bool showAimlines = Config.ShowAimlines;
            if (UIControls.ToggleRow("显示瞄准线", ref showAimlines, "在玩家标记上显示朝向方向线"))
            {
                Config.ShowAimlines = showAimlines;
                Config.MarkDirty();
            }

            if (Config.ShowAimlines)
            {
                ImGui.Indent(16);

                int aimlineLength = Config.AimlineLength;
                if (UIControls.Stepper(Chinese.E("Length"), ref aimlineLength, 0, 100, 5, tooltip: Chinese.E("Aimline length in pixels (human players)")))
                {
                    Config.AimlineLength = aimlineLength;
                    Config.MarkDirty();
                }

                bool highAlert = Config.HighAlert;
                if (UIControls.ToggleRow(Chinese.E("High Alert"), ref highAlert, Chinese.E("Extend aimline when an enemy is aiming at you")))
                {
                    Config.HighAlert = highAlert;
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }

            UIControls.Section(Chinese.E("Aimview"));

            bool showAimview = Config.ShowAimview;
            if (UIControls.ToggleRow(Chinese.E("Show Aimview"), ref showAimview, Chinese.E("First-person projection widget showing nearby players")))
            {
                Config.ShowAimview = showAimview;
                Config.MarkDirty();
            }

            if (Config.ShowAimview)
            {
                ImGui.Indent(16);

                bool aimviewLoot = Config.AimviewShowLoot;
                if (UIControls.ToggleRow(Chinese.E("Show Loot"), ref aimviewLoot, Chinese.E("Show nearby filtered loot items in the aimview")))
                {
                    Config.AimviewShowLoot = aimviewLoot;
                    Config.MarkDirty();
                }

                bool aimviewCorpses = Config.AimviewShowCorpses;
                if (UIControls.ToggleRow(Chinese.E("Show Corpses"), ref aimviewCorpses, Chinese.E("Show nearby corpses with gear value in the aimview")))
                {
                    Config.AimviewShowCorpses = aimviewCorpses;
                    Config.MarkDirty();
                }

                bool aimviewContainers = Config.AimviewShowContainers;
                if (UIControls.ToggleRow(Chinese.E("Show Containers"), ref aimviewContainers, Chinese.E("Show nearby static containers in the aimview")))
                {
                    Config.AimviewShowContainers = aimviewContainers;
                    Config.MarkDirty();
                }

                bool aimviewSkeleton = Config.AimviewShowSkeleton;
                if (UIControls.ToggleRow(Chinese.E("Show Skeleton"), ref aimviewSkeleton, Chinese.E("Draw bone skeleton for players (advanced aimview only).\nFalls back to a dot when off or skeleton data isn't ready yet.")))
                {
                    Config.AimviewShowSkeleton = aimviewSkeleton;
                    Config.MarkDirty();
                }

                bool aimviewPlayerLabels = Config.AimviewShowPlayerLabels;
                if (UIControls.ToggleRow(Chinese.E("Show Player Labels"), ref aimviewPlayerLabels, Chinese.E("Show \"Name (distance)\" labels under each player")))
                {
                    Config.AimviewShowPlayerLabels = aimviewPlayerLabels;
                    Config.MarkDirty();
                }

                bool aimviewItemLabels = Config.AimviewShowItemLabels;
                if (UIControls.ToggleRow(Chinese.E("Show Item Labels"), ref aimviewItemLabels, Chinese.E("Show labels under loot, corpse, and container markers.\nTurn off for a less cluttered view — markers stay visible.")))
                {
                    Config.AimviewShowItemLabels = aimviewItemLabels;
                    Config.MarkDirty();
                }

                bool aimviewHideAI = Config.AimviewHideAIPlayers;
                if (UIControls.ToggleRow(Chinese.E("Hide AI Players"), ref aimviewHideAI, Chinese.E("Hide Scav / Raider / Boss AI from the aimview.\nUseful on raids with many AI.")))
                {
                    Config.AimviewHideAIPlayers = aimviewHideAI;
                    Config.MarkDirty();
                }

                if (UIControls.BeginAdvanced(Chinese.E("Advanced Aimview")))
                {

                    float playerDist = Config.AimviewPlayerDistance;
                    if (UIControls.StepperFloat(Chinese.E("Player Range"), ref playerDist, 50f, 500f, 10f, "{0:0}m",
                        "玩家在瞄准视角中出现的最大距离"))
                    {
                        Config.AimviewPlayerDistance = playerDist;
                        Config.MarkDirty();
                    }

                    float lootDist = Config.AimviewLootDistance;
                    if (UIControls.StepperFloat(Chinese.E("Loot Range"), ref lootDist, 5f, 50f, 1f, "{0:0}m",
                        "瞄准视角中物资和尸体的最大距离"))
                    {
                        Config.AimviewLootDistance = lootDist;
                        Config.MarkDirty();
                    }

                    float eyeHeight = Config.AimviewEyeHeight;
                    if (UIControls.StepperFloat(Chinese.E("Eye Height"), ref eyeHeight, 0.5f, 2.0f, 0.05f, "{0:0.00}m",
                        "相机在身体根部上方的高度 — 如果物资显得太高或太低请调整（默认：1.50m）"))
                    {
                        Config.AimviewEyeHeight = eyeHeight;
                        Config.MarkDirty();
                    }

                    float zoom = Config.AimviewZoom;
                    if (UIControls.StepperFloat(Chinese.E("Zoom"), ref zoom, 0.5f, 3.0f, 0.1f, "{0:0.0}x",
                        "缩放级别（1.0 ≈ 90° FOV，越大越近）"))
                    {
                        Config.AimviewZoom = zoom;
                        Config.MarkDirty();
                    }

                    int minLootValue = Config.AimviewMinLootValue;
                    if (UIControls.Stepper(Chinese.E("Min Loot \u20bd"), ref minLootValue, 0, 10_000_000, 5000, "{0:N0}",
                        "隐藏低于此价格的物资以减少杂乱。\n愿望单物品始终显示。0 = 无过滤。"))
                    {
                        Config.AimviewMinLootValue = Math.Max(minLootValue, 0);
                        Config.MarkDirty();
                    }

                    int maxLoot = Config.AimviewMaxLoot;
                    if (UIControls.Stepper(Chinese.E("Max Loot"), ref maxLoot, 0, 64, 1,
                        tooltip: "同时绘制的最大物资标记数量"))
                    {
                        Config.AimviewMaxLoot = maxLoot;
                        Config.MarkDirty();
                    }

                    int maxCorpses = Config.AimviewMaxCorpses;
                    if (UIControls.Stepper(Chinese.E("Max Corpses"), ref maxCorpses, 0, 32, 1,
                        tooltip: "同时绘制的最大尸体标记数量"))
                    {
                        Config.AimviewMaxCorpses = maxCorpses;
                        Config.MarkDirty();
                    }

                    int maxContainers = Config.AimviewMaxContainers;
                    if (UIControls.Stepper(Chinese.E("Max Containers"), ref maxContainers, 0, 32, 1,
                        tooltip: "同时绘制的最大容器标记数量"))
                    {
                        Config.AimviewMaxContainers = maxContainers;
                        Config.MarkDirty();
                    }

                    UIControls.EndAdvanced();
                }

                UIControls.Section(Chinese.E("Advanced Aimview"));

                bool advancedAimview = Config.UseAdvancedAimview;
                if (UIControls.ToggleRow(Chinese.E("Use Advanced Aimview"), ref advancedAimview, Chinese.E("Use real game camera data (ViewMatrix) for pixel-accurate\nprojection. Requires CameraManager — falls back to synthetic\ncamera if unavailable.")))
                {
                    Config.UseAdvancedAimview = advancedAimview;
                    Config.MarkDirty();
                }

                if (Config.UseAdvancedAimview)
                {
                    ImGui.SetNextItemWidth(160);
                    int monW = Config.GameMonitorWidth;
                    if (ImGui.InputInt("游戏显示器宽度", ref monW, 0, 0))
                    {
                        monW = Math.Clamp(monW, 640, 7680);
                        Config.GameMonitorWidth = monW;
                        Config.MarkDirty();
                        CameraManager.UpdateViewportRes(Config.GameMonitorWidth, Config.GameMonitorHeight);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Chinese.E("Width of the monitor running EFT (pixels)"));

                    ImGui.SetNextItemWidth(160);
                    int monH = Config.GameMonitorHeight;
                    if (ImGui.InputInt("游戏显示器高度", ref monH, 0, 0))
                    {
                        monH = Math.Clamp(monH, 480, 4320);
                        Config.GameMonitorHeight = monH;
                        Config.MarkDirty();
                        CameraManager.UpdateViewportRes(Config.GameMonitorWidth, Config.GameMonitorHeight);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Chinese.E("Height of the monitor running EFT (pixels)"));

                    if (!CameraManager.IsActive)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), Chinese.E("CameraManager not active — waiting for raid"));
                    }
                }

                ImGui.Unindent(16);
            }

            UIControls.Section("玩家资料");

            bool profileLookups = Config.ProfileLookups;
            if (UIControls.ToggleRow("玩家数据查询", ref profileLookups, "从 tarkov.dev 获取玩家数据（K/D、小时、存活率）"))
            {
                Config.ProfileLookups = profileLookups;
                Config.MarkDirty();
            }

            UIControls.Section("性能");

            bool distAware = Config.DistanceAwareRefresh;
            if (UIControls.ToggleRow("距离感知装备/手持刷新", ref distAware,
                "对远距离玩家减少读取装备和手部频率以降低 DMA 负载。\n" +
                "安全保证：\n" +
                "  \u2022 每个玩家的首次读取始终使用完整频率（因此你仍可在任何距离识别新目标）。\n" +
                "  \u2022 节流会在你开镜（ADS）时自动绕过，因此狙击手在交火中能看到远距离目标的最新装备/武器数据。\n" +
                "如果你希望在所有地方都最大程度新鲜，可禁用此项（代价是更高 DMA 开销）。"))
            {
                Config.DistanceAwareRefresh = distAware;
                Config.MarkDirty();
            }

            if (Config.DistanceAwareRefresh && UIControls.BeginAdvanced("距离感知调优"))
            {
                float nearM = Config.DistanceRefreshNearMeters;
                if (UIControls.StepperFloat("近距离范围", ref nearM, 25f, 500f, 25f, "{0:0} m",
                    "低于此距离的玩家始终使用完整刷新频率（1×）。\n默认：150 m。"))
                {
                    Config.DistanceRefreshNearMeters = nearM;
                    if (Config.DistanceRefreshMidMeters < nearM + 25f)
                        Config.DistanceRefreshMidMeters = nearM + 25f;
                    Config.MarkDirty();
                }

                float midM = Config.DistanceRefreshMidMeters;
                float midMin = MathF.Max(50f, Config.DistanceRefreshNearMeters + 25f);
                if (UIControls.StepperFloat("中距离范围", ref midM, midMin, 1500f, 25f, "{0:0} m",
                    "介于「近」和「中」之间的玩家使用「中」倍率。\n超出「中」的距离使用「远」倍率。\n默认：300 m。"))
                {
                    Config.DistanceRefreshMidMeters = midM;
                    Config.MarkDirty();
                }

                ImGui.Spacing();
                ImGui.TextDisabled("倍率（1× = 不节流）");

                float gearMid = Config.GearRefreshMidMul;
                if (UIControls.StepperFloat("装备 中", ref gearMid, 1f, 10f, 0.1f, "{0:0.0}\u00d7",
                    "中距离装备重新读取的倍率。\n基础装备间隔 10 s，2.0× = 20 s 间隔。\n默认：2.0×。"))
                {
                    Config.GearRefreshMidMul = gearMid;
                    if (Config.GearRefreshFarMul < gearMid)
                        Config.GearRefreshFarMul = gearMid;
                    Config.MarkDirty();
                }

                float gearFar = Config.GearRefreshFarMul;
                if (UIControls.StepperFloat("装备 远", ref gearFar, Config.GearRefreshMidMul, 20f, 0.5f, "{0:0.0}\u00d7",
                    "远距离（超出中）装备重新读取的倍率。\n基础 10 s，3.0× = 30 s 间隔。\n默认：3.0×。"))
                {
                    Config.GearRefreshFarMul = gearFar;
                    Config.MarkDirty();
                }

                float handsMid = Config.HandsRefreshMidMul;
                if (UIControls.StepperFloat("手持 中", ref handsMid, 1f, 10f, 0.1f, "{0:0.0}\u00d7",
                    "中距离手持武器重新读取的倍率。\n基础 3 s，2.0× = 6 s 间隔。\n默认：2.0×。"))
                {
                    Config.HandsRefreshMidMul = handsMid;
                    if (Config.HandsRefreshFarMul < handsMid)
                        Config.HandsRefreshFarMul = handsMid;
                    Config.MarkDirty();
                }

                float handsFar = Config.HandsRefreshFarMul;
                if (UIControls.StepperFloat("手持 远", ref handsFar, Config.HandsRefreshMidMul, 20f, 0.5f, "{0:0.0}\u00d7",
                    "远距离手持武器重新读取的倍率。\n基础 3 s，4.0× = 12 s 间隔。\n默认：4.0×。"))
                {
                    Config.HandsRefreshFarMul = handsFar;
                    Config.MarkDirty();
                }

                ImGui.Spacing();
                ImGui.TextDisabled("提示：开镜（ADS）时节流会自动绕过。");

                UIControls.EndAdvanced();
            }
        }
    }
}
