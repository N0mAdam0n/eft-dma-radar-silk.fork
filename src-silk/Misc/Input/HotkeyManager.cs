// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc.Input;

/// <summary>
/// Describes one possible hotkey action: unique ID, display name, category, and handler delegate.
/// The catalog of all available actions is defined in <see cref="HotkeyManager.AvailableActions"/>.
/// </summary>
internal sealed class HotkeyActionDef(
    string id,
    string displayName,
    string category,
    string tooltip,
    Action<InputManager.KeyInputEventArgs> handler)
{
    /// <summary>Unique action identifier, used as dictionary key in config.</summary>
    public string Id { get; } = id;

    /// <summary>Human-readable name shown in the UI.</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>Category grouping for the UI (e.g. "General", "Loot").</summary>
    public string Category { get; } = category;

    /// <summary>Tooltip description for the settings panel.</summary>
    public string Tooltip { get; } = tooltip;

    /// <summary>The handler invoked when the key state changes.</summary>
    public Action<InputManager.KeyInputEventArgs> Handler { get; } = handler;
}

/// <summary>
/// Manages hotkey bindings between <see cref="SilkConfig.Hotkeys"/> entries and
/// <see cref="InputManager"/> actions. Supports dynamic add/remove and live rebinding.
/// </summary>
internal static class HotkeyManager
{
    /// <summary>
    /// Catalog of all available hotkey actions. Each entry defines what the action
    /// does — binding to a key is handled via the config dictionary.
    /// </summary>
    internal static readonly HotkeyActionDef[] AvailableActions =
    [
        // General
        new("BattleMode", Chinese.T("Battle Mode"), Chinese.T("General"),
            Chinese.T("Toggle battle mode (hide loot, focus players)"),
            static e => { if (e.IsDown) SilkProgram.Config.SetBattleMode(!SilkProgram.Config.BattleMode); }),

        new("FreeMode", Chinese.T("Free Mode"), Chinese.T("General"),
            Chinese.T("Toggle between player-follow and free-pan"),
            static e => { if (e.IsDown) RadarWindow.FreeMode = !RadarWindow.FreeMode; }),

        new("ZoomIn", Chinese.T("Zoom In"), Chinese.T("General"),
            Chinese.T("Zoom in on the radar map"),
            static e => { if (e.IsDown) RadarWindow.Zoom = Math.Min(RadarWindow.Zoom + 5, 200); }),

        new("ZoomOut", Chinese.T("Zoom Out"), Chinese.T("General"),
            Chinese.T("Zoom out on the radar map"),
            static e => { if (e.IsDown) RadarWindow.Zoom = Math.Max(RadarWindow.Zoom - 5, 1); }),

        // Loot
        new("ToggleLoot", Chinese.T("Toggle Loot"), Chinese.T("Loot"),
            Chinese.T("Toggle loot overlay visibility"),
            static e => { if (e.IsDown) SilkProgram.Config.ShowLoot = !SilkProgram.Config.ShowLoot; }),

        new("ToggleContainers", Chinese.T("Toggle Containers"), Chinese.T("Loot"),
            Chinese.T("Toggle static container rendering on the radar"),
            static e => { if (e.IsDown) SilkProgram.Config.ShowContainers = !SilkProgram.Config.ShowContainers; }),

        new("ToggleCorpses", Chinese.T("Toggle Corpses"), Chinese.T("Loot"),
            Chinese.T("Toggle corpse marker rendering on the radar"),
            static e => { if (e.IsDown) SilkProgram.Config.ShowCorpses = !SilkProgram.Config.ShowCorpses; }),

        // Map
        new("ToggleExfils", Chinese.T("Toggle Exfils"), Chinese.T("Map"),
            Chinese.T("Toggle exfil point rendering on the radar"),
            static e => { if (e.IsDown) SilkProgram.Config.ShowExfils = !SilkProgram.Config.ShowExfils; }),

        new("ToggleDoors", Chinese.T("Toggle Doors"), Chinese.T("Map"),
            Chinese.T("Toggle keyed door rendering on the radar"),
            static e => { if (e.IsDown) SilkProgram.Config.ShowDoors = !SilkProgram.Config.ShowDoors; }),

        // Widgets
        new("ToggleAimview", Chinese.T("Toggle Aimview"), Chinese.T("Widgets"),
            Chinese.T("Show/hide the aimview widget"),
            static e => { if (e.IsDown) SilkProgram.Config.ShowAimview = !SilkProgram.Config.ShowAimview; }),

        new("TogglePlayers", Chinese.T("Toggle Players On Top"), Chinese.T("Widgets"),
            Chinese.T("Toggle drawing players above all other entities"),
            static e => { if (e.IsDown) SilkProgram.Config.PlayersOnTop = !SilkProgram.Config.PlayersOnTop; }),

        new("ConnectGroups", Chinese.T("Connect Groups"), Chinese.T("Widgets"),
            Chinese.T("Toggle squad connection lines"),
            static e => { if (e.IsDown) SilkProgram.Config.ConnectGroups = !SilkProgram.Config.ConnectGroups; }),

        // ESP
        new("ToggleEspWindow", Chinese.T("Toggle ESP Window"), Chinese.T("ESP"),
            Chinese.T("Open/close the ESP overlay window"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.ESP.EspWindow.Toggle(); }),

        new("EspCycleRenderMode", Chinese.T("Cycle ESP Render Mode"), Chinese.T("ESP"),
            Chinese.T("Cycle player render mode: None → Bones → Box → HeadDot"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.ESP.EspWindow.CycleRenderMode(); }),

        new("EspToggleCrosshair", Chinese.T("Toggle ESP Crosshair"), Chinese.T("ESP"),
            Chinese.T("Show/hide the center crosshair overlay on the ESP window"),
            static e => { if (e.IsDown) { SilkProgram.Config.EspShowCrosshair = !SilkProgram.Config.EspShowCrosshair; SilkProgram.Config.MarkDirty(); } }),

        // Presets
        new("PresetCycleNext", Chinese.T("Next Preset"), Chinese.T("Presets"),
            Chinese.T("Cycle to the next radar preset in your saved list, then Custom"),
            static e => { if (e.IsDown) PresetManager.Cycle(+1, SilkProgram.Config); }),

        new("PresetCyclePrev", Chinese.T("Previous Preset"), Chinese.T("Presets"),
            Chinese.T("Cycle to the previous radar preset"),
            static e => { if (e.IsDown) PresetManager.Cycle(-1, SilkProgram.Config); }),

        // Layout
        new("ToggleSidebar", Chinese.T("Toggle Sidebar"), Chinese.T("Layout"),
            Chinese.T("Show/hide the left icon sidebar"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.Shell.Sidebar.ToggleVisibility(); }),

        new("ToggleSidePanelsDock", Chinese.T("Toggle Side Panels Dock"), Chinese.T("Layout"),
            Chinese.T("Toggle the fixed right-edge dock for Players / Loot / Quests panels"),
            static e => { if (e.IsDown) { var c = SilkProgram.Config; c.DockSidePanels = !c.DockSidePanels; c.MarkDirty(); } }),

        new("SidebarSlot1", Chinese.T("Sidebar: Players"), Chinese.T("Layout"),
            Chinese.T("Toggle the Players widget (sidebar slot 1)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.Shell.Sidebar.Toggle(0); }),

        new("SidebarSlot2", Chinese.T("Sidebar: Loot"), Chinese.T("Layout"),
            Chinese.T("Toggle the Loot widget (sidebar slot 2)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.Shell.Sidebar.Toggle(1); }),

        new("SidebarSlot3", Chinese.T("Sidebar: Aimview"), Chinese.T("Layout"),
            Chinese.T("Toggle the Aimview widget (sidebar slot 3)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.Shell.Sidebar.Toggle(2); }),

        new("SidebarSlot4", Chinese.T("Sidebar: Quests"), Chinese.T("Layout"),
            Chinese.T("Toggle the Quests panel (sidebar slot 4)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.Shell.Sidebar.Toggle(3); }),

        new("SidebarSlot5", Chinese.T("Sidebar: Settings"), Chinese.T("Layout"),
            Chinese.T("Toggle the Settings panel (sidebar slot 5)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.Shell.Sidebar.Toggle(4); }),

        // Aimview PiP
        new("AimviewCycleSize", Chinese.T("Cycle Aimview Size"), Chinese.T("Widgets"),
            Chinese.T("Cycle aimview PiP size: Floating → Small → Medium → Large"),
            static e => { if (e.IsDown) { var c = SilkProgram.Config; c.AimviewPipSize = (c.AimviewPipSize + 1) % 4; c.MarkDirty(); eft_dma_radar.Silk.UI.Widgets.AimviewWidget.RequestPipResnap(); } }),

        new("AimviewCycleCorner", Chinese.T("Cycle Aimview Corner"), Chinese.T("Widgets"),
            Chinese.T("Cycle aimview PiP corner: TL → TR → BR → BL"),
            static e => { if (e.IsDown) { var c = SilkProgram.Config; c.AimviewPipCorner = (c.AimviewPipCorner + 1) % 4; c.MarkDirty(); eft_dma_radar.Silk.UI.Widgets.AimviewWidget.RequestPipResnap(); } }),

        new("ResetSidePanels", Chinese.T("Reset Side Panels"), Chinese.T("Layout"),
            Chinese.T("Snap Players / Loot / Quests back into the right dock layout"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.UI.Shell.RightDock.RequestResnap(); }),

        // VisCheck (PhysX visibility) — no default keys, user binds via the
        // Hotkey panel. Listed in a dedicated category so they're discoverable.
        new("VisCheckDebugToggle", Chinese.T("VisCheck Debug Window"), Chinese.T("VisCheck"),
            Chinese.T("Toggle the VisCheck debug overlay (worker stats, Top Blockers, classifier rules)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.Tarkov.Unity.PhysX.VisCheckDebugWindow.Toggle(); }),

        new("VisCheckCacheViewToggle", Chinese.T("VisCheck Cache View"), Chinese.T("VisCheck"),
            Chinese.T("Toggle the 3D wireframe Cache View (PhysX scene + live player overlay)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.Tarkov.Unity.PhysX.CacheViewWindow.Toggle(); }),

        new("VisCheckRebuild", Chinese.T("VisCheck Rebuild Snapshot"), Chinese.T("VisCheck"),
            Chinese.T("Force-rebuild the PhysX scene snapshot for the current map"),
            static e => {
                if (!e.IsDown) return;
                var map = Memory.Game?.MapID;
                if (!string.IsNullOrEmpty(map))
                    eft_dma_radar.Silk.Tarkov.Unity.PhysX.SceneCache.TriggerBuild(map);
            }),

        new("VisCheckGenerateMap", Chinese.T("VisCheck Generate Map"), Chinese.T("VisCheck"),
            Chinese.T("Generate a top-down SVG map + config from the current PhysX snapshot (writes to generated_maps/)"),
            static e => {
                if (!e.IsDown) return;
                var snap = eft_dma_radar.Silk.Tarkov.Unity.PhysX.SceneCache.Snapshot;
                var map = Memory.Game?.MapID ?? "unknown";
                if (eft_dma_radar.Silk.Tarkov.Unity.PhysX.MapImageGenerator.Generate(snap, map, null, out var dir, out var err))
                    Log.WriteLine($"[MapGen] Map written to {dir}");
                else
                    Log.WriteLine($"[MapGen] Failed: {err}");
            }),

        new("VisCheckMapGenWindow", Chinese.T("VisCheck Map Generator"), Chinese.T("VisCheck"),
            Chinese.T("Toggle the Map Generator panel (live preview + tuning for the top-down map export)"),
            static e => { if (e.IsDown) eft_dma_radar.Silk.Tarkov.Unity.PhysX.MapGenWindow.Toggle(); }),
    ];

    /// <summary>Lookup from action ID to definition, for fast access.</summary>
    private static readonly Dictionary<string, HotkeyActionDef> _actionLookup;

    /// <summary>
    /// The action ID currently being captured for key binding, or <c>null</c>.
    /// </summary>
    internal static string? CapturingActionId { get; set; }

    static HotkeyManager()
    {
        _actionLookup = new(AvailableActions.Length, StringComparer.Ordinal);
        foreach (var a in AvailableActions)
            _actionLookup[a.Id] = a;
    }

    /// <summary>
    /// Gets the action definition by ID, or <c>null</c> if not found.
    /// </summary>
    public static HotkeyActionDef? GetAction(string id)
        => _actionLookup.GetValueOrDefault(id);

    /// <summary>
    /// Returns the bound key name for the given action (e.g. "F"), or <c>null</c>
    /// if the action is unbound. Used by toolbar tooltips so the displayed hint
    /// always matches the user's actual binding rather than a hard-coded letter.
    /// </summary>
    public static string? GetBindingDisplay(string actionId)
    {
        if (!SilkProgram.Config.Hotkeys.TryGetValue(actionId, out var entry))
            return null;
        if (!entry.Enabled || entry.Key < 1)
            return null;
        return VK.GetName(entry.Key);
    }

    /// <summary>
    /// Formats a tooltip line with an optional bracketed hotkey hint when bound,
    /// e.g. "Toggle loot  [L]". Returns the bare description if unbound.
    /// </summary>
    public static string WithHint(string description, string actionId)
    {
        var key = GetBindingDisplay(actionId);
        return key is null ? description : $"{description}  [{key}]";
    }

    /// <summary>
    /// Registers all enabled hotkeys from config with <see cref="InputManager"/>.
    /// Safe to call multiple times — re-registers with current config values.
    /// </summary>
    public static void RegisterAll()
    {
        if (!InputManager.IsReady)
            return;

        var hotkeys = SilkProgram.Config.Hotkeys;
        foreach (var (id, entry) in hotkeys)
        {
            if (!entry.Enabled || entry.Key < 1)
                continue;

            if (_actionLookup.TryGetValue(id, out var def))
            {
                InputManager.RegisterKeyAction(entry.Key, id, def.Handler);
                Log.WriteLine($"[HotkeyManager] Registered '{def.DisplayName}' on {VK.GetName(entry.Key)}");
            }
        }
    }

    /// <summary>
    /// Unregisters all hotkeys from <see cref="InputManager"/>.
    /// </summary>
    public static void UnregisterAll()
    {
        var hotkeys = SilkProgram.Config.Hotkeys;
        foreach (var (id, entry) in hotkeys)
        {
            if (entry.Key > 0)
                InputManager.UnregisterKeyAction(entry.Key, id);
        }
    }

    /// <summary>
    /// Adds or updates a hotkey binding for the given action.
    /// Unregisters any previous key and registers the new one.
    /// </summary>
    public static void AddOrUpdate(string actionId, int vk, HotkeyMode mode)
    {
        var config = SilkProgram.Config;
        var hotkeys = config.Hotkeys;

        // Unregister old binding if present
        if (hotkeys.TryGetValue(actionId, out var existing) && existing.Key > 0)
            InputManager.UnregisterKeyAction(existing.Key, actionId);

        hotkeys[actionId] = new HotkeyEntry
        {
            Enabled = true,
            Key = vk,
            Mode = mode,
        };

        // Register new binding
        if (vk > 0 && InputManager.IsReady && _actionLookup.TryGetValue(actionId, out var def))
            InputManager.RegisterKeyAction(vk, actionId, def.Handler);

        config.MarkDirty();
    }

    /// <summary>
    /// Removes a hotkey binding for the given action.
    /// </summary>
    public static void Remove(string actionId)
    {
        var config = SilkProgram.Config;
        var hotkeys = config.Hotkeys;

        if (hotkeys.TryGetValue(actionId, out var entry))
        {
            if (entry.Key > 0)
                InputManager.UnregisterKeyAction(entry.Key, actionId);

            hotkeys.Remove(actionId);
            config.MarkDirty();
        }
    }

    /// <summary>
    /// Called from the render loop to check if a key capture is active.
    /// If the user pressed any key, completes the capture.
    /// Returns <c>true</c> if a capture was completed this frame.
    /// </summary>
    public static bool TryCaptureKey(out int capturedVk)
    {
        capturedVk = -1;

        if (CapturingActionId is null || !InputManager.IsReady)
            return false;

        for (int vk = 1; vk < 255; vk++)
        {
            if (vk is VK.LBUTTON or VK.RBUTTON or VK.MBUTTON or VK.XBUTTON1 or VK.XBUTTON2)
                continue;

            if (InputManager.IsKeyPressed(vk))
            {
                if (vk == VK.ESCAPE)
                {
                    CapturingActionId = null;
                    return true;
                }

                capturedVk = vk;
                CapturingActionId = null;
                return true;
            }
        }

        return false;
    }
}
