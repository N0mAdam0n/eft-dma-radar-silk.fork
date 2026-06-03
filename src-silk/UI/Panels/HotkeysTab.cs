// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        private static void DrawHotkeysTab()
        {
            ImGui.Spacing();

            if (!InputManager.IsReady)
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
                    "\u26a0 " + Chinese.H("Input manager not initialized."));
                ImGui.TextWrapped(Chinese.H("Hotkeys require an active DMA connection. They will activate once a raid starts."));
                return;
            }

            ImGui.TextWrapped(Chinese.H("Manage hotkeys in the dedicated Hotkeys panel."));
            ImGui.Spacing();

            if (ImGui.Button("\u2328 " + Chinese.H("Open Hotkey Manager"), new Vector2(200, 0)))
                HotkeyManagerPanel.IsOpen = true;

            UIControls.Section(Chinese.H("Active Hotkeys"));

            var hotkeys = Config.Hotkeys;
            if (hotkeys.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Chinese.H("No hotkeys configured."));
            }
            else
            {
                foreach (var (id, entry) in hotkeys)
                {
                    if (!entry.Enabled || entry.Key < 1)
                        continue;

                    var def = HotkeyManager.GetAction(id);
                    string name = def?.DisplayName ?? id;
                    string mode = entry.Mode == HotkeyMode.Toggle ? Chinese.H("Toggle") : Chinese.H("OnKey");

                    ImGui.BulletText($"{name}  [{VK.GetName(entry.Key)}]  ({mode})");
                }
            }
        }
    }
}
