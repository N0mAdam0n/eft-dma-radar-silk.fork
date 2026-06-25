// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;

namespace eft_dma_radar.Silk.Config
{
    /// <summary>
    /// Mode for how a hotkey triggers its action.
    /// </summary>
    public enum HotkeyMode
    {
        Toggle = 0,
        OnKey = 1
    }

    /// <summary>
    /// Individual hotkey entry for each action.
    /// </summary>
    public sealed class HotkeyEntry
    {
        /// <summary>If the hotkey is enabled.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>Hotkey trigger mode: Toggle or OnKey (Hold).</summary>
        [JsonPropertyName("mode")]
        public HotkeyMode Mode { get; set; } = HotkeyMode.Toggle;

        /// <summary>Virtual keycode (int) for the hotkey. -1 = unset.</summary>
        [JsonPropertyName("key")]
        public int Key { get; set; } = -1;
    }

    /// <summary>
    /// User-facing radar preset entry. Mutable bundle of the 13 radar-layer /
    /// player-display toggles that <see cref="UI.Presets.PresetManager"/> applies
    /// in one shot. Built-in baselines are seeded into <see cref="SilkConfig.Presets"/>
    /// on first load; user-created presets are appended.
    /// </summary>
    public sealed class RadarPresetEntry
    {
        /// <summary>Stable identifier (slug of name + collision counter).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>User-facing name (editable).</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        /// <summary>Optional explanation of what this preset is for.</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>
        /// If non-null, this preset was seeded from a hardcoded built-in baseline
        /// with the matching Id, and can be reset back to those defaults.
        /// User-created presets leave this null.
        /// </summary>
        [JsonPropertyName("baselineId")]
        public string? BaselineId { get; set; }

        [JsonPropertyName("battleMode")] public bool BattleMode { get; set; }
        [JsonPropertyName("showLoot")] public bool ShowLoot { get; set; } = true;
        [JsonPropertyName("showCorpses")] public bool ShowCorpses { get; set; } = true;
        [JsonPropertyName("showContainers")] public bool ShowContainers { get; set; } = true;
        [JsonPropertyName("showExfils")] public bool ShowExfils { get; set; } = true;
        [JsonPropertyName("showDoors")] public bool ShowDoors { get; set; } = true;
        [JsonPropertyName("showAirdrops")] public bool ShowAirdrops { get; set; } = true;
        [JsonPropertyName("showSwitches")] public bool ShowSwitches { get; set; } = true;
        [JsonPropertyName("showTransits")] public bool ShowTransits { get; set; } = true;
        [JsonPropertyName("showAimlines")] public bool ShowAimlines { get; set; } = true;
        [JsonPropertyName("connectGroups")] public bool ConnectGroups { get; set; } = true;
        [JsonPropertyName("highAlert")] public bool HighAlert { get; set; } = true;
        [JsonPropertyName("playersOnTop")] public bool PlayersOnTop { get; set; }
    }

    /// <summary>
    /// User override for a single map's calibration (the X / Y / Scale fields of
    /// <see cref="UI.Maps.MapConfig"/>). Persisted per-map-id so the user can fine-tune
    /// alignment once and have it stick across app restarts, and so the web radar
    /// serves the corrected projection.
    /// </summary>
    public sealed class MapCalibration
    {
        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("scale")]
        public float Scale { get; set; } = 1f;
    }

    /// <summary>
    /// Ballistics simulation + debug overlay settings.
    /// Read by <c>BallisticsFeature</c>, <c>BallisticsRenderer</c>, and the debug HUD.
    /// </summary>
    public sealed class BallisticsConfig
    {
        /// <summary>Master toggle. When false the worker still runs but does no work.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>Draw the predicted shot arc (red) overlaid on the ESP world.</summary>
        [JsonPropertyName("drawPredictedTrajectory")]
        public bool DrawPredictedTrajectory { get; set; } = true;

        /// <summary>Draw live in-flight bullet trails (green) read from the game's Shots list.</summary>
        [JsonPropertyName("drawLiveShots")]
        public bool DrawLiveShots { get; set; } = true;

        /// <summary>Show the ammo / muzzle-velocity / drop HUD widget in the radar window.</summary>
        [JsonPropertyName("showDebugHud")]
        public bool ShowDebugHud { get; set; } = true;

        /// <summary>When true, replace the hardcoded G1 table with the game's own as soon as a shot is observed.</summary>
        [JsonPropertyName("useGameG1Table")]
        public bool UseGameG1Table { get; set; } = true;

        /// <summary>Hard cap on simultaneously-tracked in-flight bullets.</summary>
        [JsonPropertyName("maxLiveShots")]
        public int MaxLiveShots { get; set; } = 256;

        /// <summary>Live shot trail fade-out / GC time (seconds).</summary>
        [JsonPropertyName("liveShotLifetime")]
        public float LiveShotLifetime { get; set; } = 4.5f;

        /// <summary>Skia stroke width for trajectory + live shot lines.</summary>
        [JsonPropertyName("lineWidth")]
        public float LineWidth { get; set; } = 2.0f;

        /// <summary>Sample count for predicted trajectory polyline (8..512).</summary>
        [JsonPropertyName("predictedSamples")]
        public int PredictedSamples { get; set; } = 128;

        /// <summary>Max predicted-trajectory distance from muzzle (meters).</summary>
        [JsonPropertyName("predictedMaxDistance")]
        public float PredictedMaxDistance { get; set; } = 400f;
    }

    /// <summary>
    /// ESP grenade trajectory (drag tail + bold dot) settings.
    /// The trail is the *actual* path sampled from live grenade positions (not a prediction).
    /// Trails remain visible for a short time after the grenade detonates/lands (user-settable retention).
    /// Settings live under 透视设置 (EspTab).
    /// </summary>
    public sealed class EspGrenadeConfig
    {
        /// <summary>Master switch for grenade trails + dots on the ESP overlay.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Only grenades (or their lingering markers) within this world distance from local player are drawn.</summary>
        [JsonPropertyName("maxDistance")]
        public float MaxDistance { get; set; } = 250f;

        /// <summary>
        /// Seconds to keep the trail polyline + final bold dot visible after the grenade is no longer
        /// being updated by the game (detonated, destroyed, or expired). This is the "briefly persist" / 拖尾留存时间.
        /// </summary>
        [JsonPropertyName("trailLifetime")]
        public float TrailLifetime { get; set; } = 4.0f;

        /// <summary>Radius (in viewport pixels) of the bold dot drawn at the grenade's current/last position.</summary>
        [JsonPropertyName("dotRadius")]
        public float DotRadius { get; set; } = 4.5f;

        /// <summary>Skia stroke width for the trajectory trail line.</summary>
        [JsonPropertyName("trailWidth")]
        public float TrailWidth { get; set; } = 2.2f;
    }

    /// <summary>
    /// Minimal configuration for the Silk.NET radar.
    /// Loaded from / saved to a JSON file in %AppData%\eft-dma-radar-silk\.
    /// </summary>
    public sealed class SilkConfig
    {
        private static readonly string _configDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eft-dma-radar-silk");

        private static readonly string _configPath =
            Path.Combine(_configDir, "config.json");

        private static readonly JsonSerializerOptions _jsonWriteOptions = new() { WriteIndented = true };

        // Debounced save: dirty flag + timestamp
        [JsonIgnore]
        private volatile bool _dirty;
        [JsonIgnore]
        private long _dirtyTimestamp;
        private const long DebounceSaveMs = 500;

        // ── Debug ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Enable debug logging at startup (same effect as passing <c>-debug</c> on the command line).
        /// All <see cref="AppLogLevel.Debug"/> messages and IL2CPP hierarchy dumps are gated by this flag.
        /// Can also be toggled at runtime with <b>F8</b> (F8 also immediately calls DumpAll on the live session).
        /// </summary>
        [JsonPropertyName("debugLogging")]
        public bool DebugLogging { get; set; } = false;

        // ── Match Dump ──────────────────────────────────────────────────────────

        /// <summary>
        /// Enables the match-data dump feature.
        /// When true, pressing the Dump hotkey (or calling <see cref="MatchDumper.DumpAsync"/>
        /// at raid start) writes a full JSON snapshot of all radar data to the <c>dumps\</c>
        /// folder next to the executable.
        /// </summary>
        [JsonPropertyName("enableMatchDump")]
        public bool EnableMatchDump { get; set; } = true;

        // ── DMA ─────────────────────────────────────────────────────────────────

        /// <summary>FPGA device string passed to MemProcFS (e.g. "fpga", "usb3380").</summary>
        public string DeviceStr { get; set; } = "fpga";

        /// <summary>Use a persisted memory map file for faster DMA init.</summary>
        public bool MemMapEnabled { get; set; } = true;

        // ── UI ──────────────────────────────────────────────────────────────────

        /// <summary>UI scaling factor (1.0 = 100%).</summary>
        public float UIScale { get; set; } = 1.0f;

        /// <summary>Target frames per second for the radar window.</summary>
        public int TargetFps { get; set; } = 60;

        /// <summary>Radar window width in pixels.</summary>
        public int WindowWidth { get; set; } = 1600;

        /// <summary>Radar window height in pixels.</summary>
        public int WindowHeight { get; set; } = 900;

        /// <summary>Whether the radar window starts maximized.</summary>
        public bool WindowMaximized { get; set; } = false;

        /// <summary>Hide loot and other clutter; show only players.</summary>
        public bool BattleMode { get; set; } = false;

        /// <summary>
        /// Toggle Battle Mode safely. When Battle Mode is turned OFF, also restore
        /// <see cref="ShowLoot"/> to <c>true</c> so loot reappears — without this,
        /// any preset (Stealth / PvP) that turned Battle Mode on while also clearing
        /// ShowLoot would leave loot hidden after the user disables Battle Mode,
        /// which doesn't match the user's mental model of "Battle Mode off = loot back".
        /// </summary>
        public void SetBattleMode(bool enabled)
        {
            if (BattleMode == enabled)
                return;
            BattleMode = enabled;
            if (!enabled && !ShowLoot)
                ShowLoot = true;
            MarkDirty();
        }

        /// <summary>Draw players above all other entities.</summary>
        public bool PlayersOnTop { get; set; } = false;

        /// <summary>Draw lines connecting squad members.</summary>
        public bool ConnectGroups { get; set; } = true;

        /// <summary>Show aimlines extending from player markers indicating facing direction.</summary>
        public bool ShowAimlines { get; set; } = true;

        /// <summary>Show the aimview widget (first-person projection of nearby players).</summary>
        public bool ShowAimview { get; set; } = true;

        /// <summary>Show filtered loot items in the aimview widget.</summary>
        public bool AimviewShowLoot { get; set; } = true;

        /// <summary>Show nearby corpses with gear value in the aimview widget.</summary>
        public bool AimviewShowCorpses { get; set; } = true;

        /// <summary>Show nearby static containers in the aimview widget.</summary>
        public bool AimviewShowContainers { get; set; } = true;

        /// <summary>Draw skeleton bones for players in the aimview (advanced mode only; falls back to dot when off or unavailable).</summary>
        public bool AimviewShowSkeleton { get; set; } = true;

        /// <summary>Draw "Name (Xm)" labels under players in the aimview.</summary>
        public bool AimviewShowPlayerLabels { get; set; } = true;

        /// <summary>Draw labels under loot / corpse / container markers in the aimview.</summary>
        public bool AimviewShowItemLabels { get; set; } = true;

        /// <summary>Hide AI players (AIScav / AIRaider / AIBoss) from the aimview to reduce clutter.</summary>
        public bool AimviewHideAIPlayers { get; set; } = false;

        /// <summary>Minimum item price (₽) for loot to appear in the aimview. 0 = show everything the loot filter already allows.</summary>
        public int AimviewMinLootValue { get; set; } = 0;

        /// <summary>Maximum number of loot markers drawn at once.</summary>
        public int AimviewMaxLoot { get; set; } = 12;

        /// <summary>Maximum number of corpse markers drawn at once.</summary>
        public int AimviewMaxCorpses { get; set; } = 6;

        /// <summary>Maximum number of container markers drawn at once.</summary>
        public int AimviewMaxContainers { get; set; } = 8;

        /// <summary>Max distance (meters) for players to appear in the aimview.</summary>
        public float AimviewPlayerDistance { get; set; } = 300f;

        /// <summary>Max distance (meters) for loot/corpses to appear in the aimview.</summary>
        public float AimviewLootDistance { get; set; } = 15f;

        /// <summary>Eye height offset (meters) above body root for the aimview camera.</summary>
        public float AimviewEyeHeight { get; set; } = 1.35f;

        /// <summary>Zoom level for the aimview (1.0 = ~90° FOV, higher = narrower/zoomed in).</summary>
        public float AimviewZoom { get; set; } = 1.0f;

        /// <summary>
        /// Use the advanced aimview mode that reads the game's real camera ViewMatrix
        /// via <see cref="CameraManager"/> for pixel-accurate W2S projection.
        /// When false (default), the aimview uses a synthetic camera built from
        /// the local player's position + rotation.
        /// </summary>
        public bool UseAdvancedAimview { get; set; } = false;

        /// <summary>Game monitor width (pixels) — used by CameraManager for W2S viewport math.</summary>
        public int GameMonitorWidth { get; set; } = 2560;

        /// <summary>Game monitor height (pixels) — used by CameraManager for W2S viewport math.</summary>
        public int GameMonitorHeight { get; set; } = 1440;

        /// <summary>Aimline length in pixels for human players (PMC/PScav).</summary>
        public int AimlineLength { get; set; } = 15;

        /// <summary>Extend aimline when an enemy is facing the local player (High Alert).</summary>
        public bool HighAlert { get; set; } = true;

        // ── Widget Visibility

        /// <summary>Whether the Players widget is open.</summary>
        public bool ShowPlayersWidget { get; set; } = true;

        /// <summary>Whether the Loot widget is open.</summary>
        public bool ShowLootWidget { get; set; } = false;

        /// <summary>Whether the Aimview widget is open.</summary>
        public bool ShowAimviewWidget { get; set; } = true;

        /// <summary>
        /// Aimview picture-in-picture size preset.
        /// 0 = Floating (legacy free-window mode), 1 = Small, 2 = Medium, 3 = Large.
        /// Cycle with the "Aimview Size" hotkey.
        /// </summary>
        [JsonPropertyName("aimviewPipSize")]
        public int AimviewPipSize { get; set; } = 2;

        /// <summary>
        /// Aimview PiP corner: 0 = Top-Left, 1 = Top-Right, 2 = Bottom-Right, 3 = Bottom-Left.
        /// Cycle with the "Aimview Corner" hotkey.
        /// </summary>
        [JsonPropertyName("aimviewPipCorner")]
        public int AimviewPipCorner { get; set; } = 2;

        /// <summary>Whether the left icon sidebar is visible.</summary>
        [JsonPropertyName("showSidebar")]
        public bool ShowSidebar { get; set; } = true;

        /// <summary>Whether the bottom status bar is visible.</summary>
        [JsonPropertyName("showStatusBar")]
        public bool ShowStatusBar { get; set; } = true;

        /// <summary>
        /// When true, the Players / Loot / Quests panels are forced into a right-edge dock
        /// (stacked column, fixed width). When false, they remain free-floating like before.
        /// </summary>
        [JsonPropertyName("dockSidePanels")]
        public bool DockSidePanels { get; set; } = true;

        /// <summary>Width (unscaled px) reserved for the right dock when <see cref="DockSidePanels"/> is enabled.</summary>
        [JsonPropertyName("rightDockWidth")]
        public float RightDockWidth { get; set; } = 460f;

        /// <summary>
        /// Becomes true after the user dismisses (or finishes) the welcome / first-run tour.
        /// Defaults to false so brand-new installs see the tour exactly once.
        /// </summary>
        [JsonPropertyName("firstRunTourCompleted")]
        public bool FirstRunTourCompleted { get; set; } = false;

        /// <summary>Whether the unified settings overlay is open.</summary>
        public bool ShowSettingsOverlay { get; set; } = false;

        /// <summary>Whether the Loot Filters panel is open.</summary>
        public bool ShowLootFiltersPanel { get; set; } = false;

        /// <summary>Whether the Hotkey Manager panel is open.</summary>
        public bool ShowHotkeyPanel { get; set; } = false;

        /// <summary>Whether the Hideout panel is open.</summary>
        public bool ShowHideoutPanel { get; set; } = false;

        /// <summary>Whether the Quest Info panel is open.</summary>
        public bool ShowQuestPanel { get; set; } = false;

        /// <summary>Whether the Quest Planner panel is open.</summary>
        public bool ShowQuestPlannerPanel { get; set; } = false;

        /// <summary>Whether the Player History panel is open.</summary>
        public bool ShowPlayerHistoryPanel { get; set; } = false;

        /// <summary>Whether the Player Watchlist panel is open.</summary>
        public bool ShowPlayerWatchlistPanel { get; set; } = false;

        /// <summary>Whether the ESP overlay widget is open.</summary>
        public bool ShowEspWidget { get; set; } = false;

        /// <summary>Show player boxes/labels on the ESP overlay.</summary>
        public bool EspShowPlayers { get; set; } = true;

        /// <summary>Show loot labels on the ESP overlay.</summary>
        public bool EspShowLoot { get; set; } = true;

        /// <summary>Show corpses (dead players / gear) labels on the ESP overlay.</summary>
        public bool EspShowCorpses { get; set; } = true;

        /// <summary>Show tripwires (拌线陷阱 / 绊索) on the ESP overlay. Draws line between endpoints + markers + distance.</summary>
        public bool EspShowTripwires { get; set; } = true;

        /// <summary>Show skeleton bones for players on the ESP overlay. Now independent checkbox (can combine with box or head dot).</summary>
        public bool EspShowBones { get; set; } = true;

        /// <summary>Show 3D player bounding box (corner style) on the ESP overlay.</summary>
        public bool EspShowBox { get; set; } = true;

        /// <summary>Show head dot marker on the ESP overlay (used for small/far players or standalone).</summary>
        public bool EspShowHeadDot { get; set; } = false;

        /// <summary>
        /// Legacy ESP per-player render mode (kept for hotkey compatibility).
        /// 0 = None (labels only), 1 = Bones, 2 = Box, 3 = HeadDot.
        /// The hotkey "EspCycleRenderMode" cycles this and maps to the checkbox states below.
        /// Primary control is now via independent checkboxes in 透视设置 (Show Bones / Show Box / Show Head Dot).
        /// </summary>
        public int EspRenderMode { get; set; } = 2;

        /// <summary>Show a center crosshair overlay on the ESP window.</summary>
        public bool EspShowCrosshair { get; set; } = false;

        /// <summary>Crosshair style: 0 = Plus, 1 = Cross, 2 = Circle, 3 = Dot, 4 = Square, 5 = Diamond.</summary>
        public int EspCrosshairType { get; set; } = 0;

        /// <summary>Crosshair scale multiplier.</summary>
        public float EspCrosshairScale { get; set; } = 1f;

        /// <summary>
        /// General UI scale multiplier for the ESP overlay (affects font sizes, box/line widths,
        /// dot radii, health/energy bar sizes, label offsets, grenade/ballistics elements etc.).
        /// Helps with high-resolution setups or when elements feel too small on the overlay.
        /// Crosshair has its own dedicated scale.
        /// </summary>
        public float EspUIScale { get; set; } = 1f;

        /// <summary>Show FPS counter in the top-left of the ESP window.</summary>
        public bool EspShowFps { get; set; } = true;

        /// <summary>Target FPS for the ESP window (independent of the radar FPS).</summary>
        public int EspTargetFps { get; set; } = 144;

        /// <summary>Show the status text banner at the top-center of the ESP window.</summary>
        public bool EspShowStatusText { get; set; } = true;

        /// <summary>Show local player energy/hydration bars on the ESP window.</summary>
        public bool EspShowEnergyHydration { get; set; } = false;

        /// <summary>Show held weapon name for players on the ESP overlay.</summary>
        public bool EspShowWeapon { get; set; } = true;

        /// <summary>Show current magazine ammo count (and capacity if known) for players holding weapons.</summary>
        public bool EspShowAmmo { get; set; } = true;

        /// <summary>Show additional player status info (e.g. health state, fire mode) on ESP labels.</summary>
        public bool EspShowPlayerStatus { get; set; } = true;

        /// <summary>Maximum distance (meters) for ESP player rendering.</summary>
        public float EspPlayerDistance { get; set; } = 500f;

        /// <summary>Maximum distance (meters) for ESP loot rendering.</summary>
        public float EspLootDistance { get; set; } = 100f;

        /// <summary>Maximum distance (meters) for ESP corpse rendering.</summary>
        public float EspCorpseDistance { get; set; } = 250f;

        /// <summary>Maximum distance (meters) for ESP tripwire (拌线陷阱) rendering.</summary>
        public float EspTripwireDistance { get; set; } = 250f;

        /// <summary>Target monitor index (0-based) for the ESP window. 0 = primary monitor.</summary>
        public int EspTargetScreen { get; set; } = 0;

        // ── Hideout

        /// <summary>Enable hideout stash/area reading when entering the hideout scene.</summary>
        public bool HideoutEnabled { get; set; } = true;

        /// <summary>Automatically refresh stash and area data on hideout entry.</summary>
        public bool HideoutAutoRefresh { get; set; } = true;

        // ── Exfils ──────────────────────────────────────────────────────────────

        /// <summary>Master toggle for exfil rendering on the radar.</summary>
        public bool ShowExfils { get; set; } = true;

        /// <summary>Hide exfils that are closed or not available to the local player.</summary>
        public bool HideInactiveExfils { get; set; } = true;

        // ── Quests ──────────────────────────────────────────────────────────────

        /// <summary>Master toggle for quest zone rendering on the radar.</summary>
        public bool ShowQuests { get; set; } = true;

        /// <summary>Only show quests required for Kappa container.</summary>
        public bool QuestKappaFilter { get; set; } = false;

        /// <summary>Show optional quest objectives.</summary>
        public bool QuestShowOptional { get; set; } = true;

        /// <summary>Show quest zone names on the radar.</summary>
        public bool QuestShowNames { get; set; } = true;

        /// <summary>Show quest zone distances on the radar.</summary>
        public bool QuestShowDistance { get; set; } = true;

        /// <summary>Draw zone outline polygons on the radar.</summary>
        public bool QuestShowOutlines { get; set; } = true;

        /// <summary>Maximum distance (metres) at which a quest zone is drawn. 0 = unlimited.</summary>
        public float QuestMaxDistance { get; set; } = 0f;

        /// <summary>Show "Kill" objective zones (e.g. kill X in zone Y).</summary>
        public bool QuestShowKillZones { get; set; } = true;

        /// <summary>Show "Find / Collect" objective zones.</summary>
        public bool QuestShowFindZones { get; set; } = true;

        /// <summary>Show "Place / Plant" objective zones.</summary>
        public bool QuestShowPlaceZones { get; set; } = true;

        /// <summary>Show "Reach / Visit" / other objective zones.</summary>
        public bool QuestShowReachZones { get; set; } = true;

        /// <summary>Quest IDs blacklisted from display (user-hidden).</summary>
        public List<string> QuestBlacklist { get; set; } = [];

        /// <summary>When non-empty, only this quest's items/zones are shown on the radar.</summary>
        public string QuestSelectedId { get; set; } = "";

        /// <summary>When true, only the selected quest's items/zones are drawn on the radar.</summary>
        public bool QuestSelectedOnly { get; set; } = false;

        /// <summary>
        /// Highlight loose loot items required by active quests (find-in-raid, hand-over, etc.).
        /// Independent of <see cref="ShowQuests"/> which controls zone marker rendering.
        /// </summary>
        public bool QuestHighlightLootItems { get; set; } = true;

        /// <summary>
        /// Highlight loose loot items needed for pending hideout upgrades (any condition).
        /// Uses the last-known planner data so the filter works during raids too.
        /// </summary>
        public bool HideoutHighlightLootItems { get; set; } = true;

        /// <summary>
        /// When <see cref="HideoutHighlightLootItems"/> is enabled, also separately
        /// highlight items that specifically need to be Found in Raid.
        /// </summary>
        public bool HideoutHighlightFiRItems { get; set; } = true;

        // ── Transits ────────────────────────────────────────────────────────────

        /// <summary>Master toggle for transit point rendering on the radar.</summary>
        public bool ShowTransits { get; set; } = true;

        // ── Killfeed ────────────────────────────────────────────────────────────

        /// <summary>Show the killfeed overlay on the radar canvas.</summary>
        public bool ShowKillFeed { get; set; } = true;

        /// <summary>Maximum number of killfeed entries shown at once (1–10).</summary>
        public int KillFeedMaxEntries { get; set; } = 6;

        /// <summary>Seconds before a killfeed entry expires and disappears (10–600).</summary>
        public int KillFeedTtlSeconds { get; set; } = 120;

        /// <summary>Killfeed overlay X position in canvas pixels. -1 = anchor top-right (default).</summary>
        public float KillFeedPosX { get; set; } = -1f;

        /// <summary>Killfeed overlay Y position in canvas pixels. -1 = anchor top-right (default).</summary>
        public float KillFeedPosY { get; set; } = -1f;

        /// <summary>Player counter overlay X position in canvas pixels. -1 = anchor top-left (default).</summary>
        public float PlayerCounterPosX { get; set; } = -1f;

        /// <summary>Player counter overlay Y position in canvas pixels. -1 = anchor top-left (default).</summary>
        public float PlayerCounterPosY { get; set; } = -1f;

        // ── Explosives ──────────────────────────────────────────────────────────

        /// <summary>Master toggle for explosive rendering on the radar (grenades, tripwires, mortars).</summary>
        public bool ShowExplosives { get; set; } = true;

        /// <summary>Draw the tripwire line between endpoints (when explosives are enabled).</summary>
        public bool ShowTripwireLines { get; set; } = true;

        // ── BTR ─────────────────────────────────────────────────────────────────

        /// <summary>Show the BTR vehicle marker on the radar (Streets/Woods only).</summary>
        public bool ShowBTR { get; set; } = true;

        /// <summary>Show BTR route stop markers on the radar.</summary>
        public bool ShowBTRRoute { get; set; } = true;

        // ── Airdrops ────────────────────────────────────────────────────────────

        /// <summary>Show airdrop markers on the radar.</summary>
        public bool ShowAirdrops { get; set; } = true;

        // ── Switches ────────────────────────────────────────────────────────────

        /// <summary>Show switch markers on the radar (power switches, etc.).</summary>
        public bool ShowSwitches { get; set; } = true;

        // ── Doors ───────────────────────────────────────────────────────────────

        /// <summary>Master toggle for keyed door rendering on the radar.</summary>
        public bool ShowDoors { get; set; } = true;

        /// <summary>Show locked doors on the radar.</summary>
        public bool ShowLockedDoors { get; set; } = true;

        /// <summary>Show unlocked (open/shut) doors on the radar.</summary>
        public bool ShowUnlockedDoors { get; set; } = true;

        /// <summary>Only show doors that are near important (high-value) loot items.</summary>
        public bool DoorsOnlyNearLoot { get; set; } = true;

        /// <summary>Maximum distance (meters) from a door to important loot for it to be shown.</summary>
        public float DoorLootProximity { get; set; } = 25f;

        // ── Map Style ──────────────────────────────────────────────────────────

        /// <summary>
        /// Use satellite tile maps from <c>assets.tarkov.dev</c> instead of the bundled SVG layers.
        /// Maps without a supported tarkov.dev rotation (Factory/Labs/Labyrinth) fall back to SVG.
        /// </summary>
        [JsonPropertyName("useSatelliteMap")]
        public bool UseSatelliteMap { get; set; } = false;

        /// <summary>
        /// When true, fetch the map's SVG from <c>assets.tarkov.dev</c> instead of using
        /// the bundled local SVG files. Downloads are cached persistently under
        /// <c>%AppData%\eft-dma-radar-silk\tarkov-dev-maps\</c> — one network round-trip
        /// per map per install, then offline-friendly. Mutually exclusive with
        /// <see cref="UseSatelliteMap"/> (satellite wins if both are on).
        /// </summary>
        [JsonPropertyName("useTarkovDevMap")]
        public bool UseTarkovDevMap { get; set; } = false;

        /// <summary>
        /// Visual rotation (in degrees: 0, 90, 180, 270) applied to tarkov.dev SVG
        /// maps when they are active. tarkov.dev SVGs are authored in a raw coord
        /// space that doesn't line up with silk's bundled SVG orientation; this
        /// setting lets the user pick the orientation they prefer.
        /// </summary>
        [JsonPropertyName("tarkovDevMapRotation")]
        public int TarkovDevMapRotation { get; set; } = 90;

        /// <summary>
        /// Per-map calibration overrides keyed by primary map ID (e.g. "bigmap",
        /// "interchange"). When present, <see cref="UI.Maps.MapManager"/> applies the
        /// stored X / Y / Scale to the loaded <see cref="UI.Maps.MapConfig"/>,
        /// overriding the bundled defaults. The user saves an override by clicking
        /// "Save Calibration" in the Map Setup panel; reset removes the entry.
        /// </summary>
        [JsonPropertyName("mapCalibrationOverrides")]
        public Dictionary<string, MapCalibration> MapCalibrationOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // ── Loot ────────────────────────────────────────────────────────────────

        /// <summary>Master toggle for loot rendering on the radar.</summary>
        public bool ShowLoot { get; set; } = true;        /// <summary>Show corpse X markers on the radar (when loot is enabled).</summary>
        public bool ShowCorpses { get; set; } = true;

        /// <summary>Show total gear value on corpse tooltip and aimview label.</summary>
        public bool ShowCorpseValue { get; set; } = true;

        /// <summary>Show per-slot equipment list on corpse tooltip.</summary>
        public bool ShowCorpseInventory { get; set; } = true;

        /// <summary>Show static loot containers on the radar (when loot is enabled).</summary>
        public bool ShowContainers { get; set; } = true;

        /// <summary>Show container name labels next to container markers.</summary>
        public bool ShowContainerNames { get; set; } = true;

        /// <summary>Minimum price (roubles) below which loot is hidden from the radar.</summary>
        public int LootMinPrice { get; set; } = 50_000;

        /// <summary>Price threshold (roubles) above which loot is highlighted as important.</summary>
        public int LootImportantPrice { get; set; } = 200_000;

        /// <summary>Show prices as price-per-slot instead of total price.</summary>
        public bool LootPricePerSlot { get; set; } = false;

        /// <summary>Price source for loot values (0 = Best, 1 = Flea, 2 = Trader).</summary>
        public int LootPriceSource { get; set; } = 0;

        /// <summary>Base radius (px) used for loot dot rendering on the radar.</summary>
        public float LootDotSize { get; set; } = 3f;

        /// <summary>Font size (px) used for loot labels on the radar.</summary>
        public float LootLabelFontSize { get; set; } = 11f;

        /// <summary>Show a small up/down arrow next to loot items above/below the local player.</summary>
        public bool LootShowHeightArrows { get; set; } = true;

        /// <summary>Meters of vertical offset (±) before an up/down arrow is drawn on a loot item.</summary>
        public float LootHeightArrowThreshold { get; set; } = 1.8f;

        /// <summary>Append the exact vertical delta (e.g. "+4m") to the loot label when above threshold.</summary>
        public bool LootShowHeightDelta { get; set; } = false;

        // ── Loot Category Toggles ──────────────────────────────────────────────

        /// <summary>Always show medical items (bypasses price filter).</summary>
        public bool LootShowMeds { get; set; } = false;

        /// <summary>Always show food/drink items (bypasses price filter).</summary>
        public bool LootShowFood { get; set; } = false;

        /// <summary>Always show backpacks (bypasses price filter).</summary>
        public bool LootShowBackpacks { get; set; } = false;

        /// <summary>Always show keys/keycards (bypasses price filter).</summary>
        public bool LootShowKeys { get; set; } = false;

        /// <summary>Always show wishlisted items (bypasses all filters).</summary>
        public bool LootShowWishlist { get; set; } = true;

        /// <summary>
        /// Show loose quest items on the radar (items flagged with
        /// <c>ItemTemplate.QuestItem</c> such as pocket watches, Jaeger's letter, etc.).
        /// Independent of <see cref="ShowQuests"/> which controls active-quest highlighting.
        /// </summary>
        public bool LootShowQuestItems { get; set; } = true;

        /// <summary>
        /// Show loose items whose BSG ID is not present in the tarkov.dev database
        /// (e.g. items added by a recent patch that aren't catalogued yet). When enabled,
        /// these render with a placeholder name showing the BSG ID prefix so they can be
        /// identified in the field. Off by default to avoid radar clutter.
        /// </summary>
        public bool LootShowUnknownItems { get; set; } = false;

        /// <summary>
        /// Quick filter: when enabled, only Important (tier ≥ 1), wishlisted,
        /// quest-required, or category-toggled items are shown — all price-only
        /// loot below the importance threshold is hidden regardless of LootMinPrice.
        /// </summary>
        public bool LootImportantOnly { get; set; } = false;

        /// <summary>Include the in-game WishlistManager (favorites set inside Tarkov itself).</summary>
        public bool LootUseIngameWishlist { get; set; } = true;

        /// <summary>Show in-game wishlist items in the Quests group (EWishlistGroup=0).</summary>
        public bool LootWishlistGroupQuests { get; set; } = true;

        /// <summary>Show in-game wishlist items in the Hideout group (EWishlistGroup=1).</summary>
        public bool LootWishlistGroupHideout { get; set; } = true;

        /// <summary>Show in-game wishlist items in the Trading group (EWishlistGroup=2).</summary>
        public bool LootWishlistGroupTrading { get; set; } = true;

        /// <summary>Show in-game wishlist items in the Equipment group (EWishlistGroup=3).</summary>
        public bool LootWishlistGroupEquipment { get; set; } = true;

        /// <summary>Show in-game wishlist items in the Other group (EWishlistGroup=4).</summary>
        public bool LootWishlistGroupOther { get; set; } = true;

        // ── Profiles ────────────────────────────────────────────────────────────

        /// <summary>Enable tarkov.dev profile lookups for human players (KD, hours, etc.).</summary>
        public bool ProfileLookups { get; set; } = true;

        // ── Performance: distance-aware player refresh ──────────────────────────

        /// <summary>
        /// When enabled, gear/hands re-reads are throttled for far-away players to reduce
        /// DMA load. The very first read for each player always happens at full cadence,
        /// and throttling is fully bypassed while the local player is ADS (sniping).
        /// </summary>
        public bool DistanceAwareRefresh { get; set; } = true;

        /// <summary>Distance (m) below which players always use the full refresh cadence.</summary>
        public float DistanceRefreshNearMeters { get; set; } = 150f;

        /// <summary>Distance (m) up to which players use the "mid" refresh multiplier. Beyond this = "far".</summary>
        public float DistanceRefreshMidMeters { get; set; } = 300f;

        /// <summary>Mid-range gear refresh multiplier (e.g. 2.0 = 10s → 20s between re-reads).</summary>
        public float GearRefreshMidMul { get; set; } = 2.0f;

        /// <summary>Far-range gear refresh multiplier (e.g. 3.0 = 10s → 30s between re-reads).</summary>
        public float GearRefreshFarMul { get; set; } = 3.0f;

        /// <summary>Mid-range hands refresh multiplier (e.g. 2.0 = 3s → 6s between re-reads).</summary>
        public float HandsRefreshMidMul { get; set; } = 2.0f;

        /// <summary>Far-range hands refresh multiplier (e.g. 4.0 = 3s → 12s between re-reads).</summary>
        public float HandsRefreshFarMul { get; set; } = 4.0f;

        /// <summary>Ballistics simulation + debug overlay settings.</summary>
        public BallisticsConfig Ballistics { get; set; } = new();

        /// <summary>ESP grenade trajectory (trails + dots) settings. See <see cref="EspGrenadeConfig"/>.</summary>
        public EspGrenadeConfig EspGrenades { get; set; } = new();

        /// <summary>
        /// Seconds between full loot list refreshes. Default 10s — ground loot is static
        /// once spawned and the LootManager caches the resolved <c>LootItem</c> for the
        /// entire raid, so the only DMA cost per refresh is verifying which entries are
        /// still present. Lower to 5s if you want faster pickup of dropped items at the
        /// cost of ~2× the (already cached-heavy) loot bandwidth. Clamped to [2, 60].
        /// </summary>
        public float LootRefreshIntervalSeconds { get; set; } = 10f;

        // ── Web Radar ───────────────────────────────────────────────────────────

        /// <summary>Enable the web radar HTTP server on startup.</summary>
        public bool WebRadarEnabled { get; set; } = false;

        /// <summary>HTTP port for the web radar server.</summary>
        public int WebRadarPort { get; set; } = 7224;

        /// <summary>Web radar update interval in milliseconds.</summary>
        public int WebRadarTickMs { get; set; } = 50;

        /// <summary>Enable UPnP/NAT-PMP automatic port forwarding for the web radar.</summary>
        public bool WebRadarUPnP { get; set; } = false;

        // ── Presets ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Identifier of the currently active radar preset (e.g. "Stealth", "LootRun",
        /// "PvP", "Quests", "Custom"). Presets bundle radar-layer and player-display
        /// toggles. See <see cref="UI.Presets.PresetManager"/>.
        /// </summary>
        [JsonPropertyName("activePresetId")]
        public string ActivePresetId { get; set; } = "Custom";

        /// <summary>
        /// All persisted radar presets — built-in baselines (Stealth / Loot Run / PvP /
        /// Quests) are seeded on first load by <see cref="UI.Presets.PresetManager.EnsureSeeded"/>,
        /// and any user-created presets are appended. Edits live here too; the hardcoded
        /// baselines in PresetManager are only used as reset targets.
        /// </summary>
        [JsonPropertyName("presets")]
        public List<RadarPresetEntry> Presets { get; set; } = [];

        // ── Hotkeys ─────────────────────────────────────────────────────────────

        /// <summary>
        /// All configured hotkeys keyed by action ID (e.g. "BattleMode", "ZoomIn").
        /// Only enabled entries with a valid key code are active.
        /// </summary>
        public Dictionary<string, HotkeyEntry> Hotkeys { get; set; } = [];

        // ── Containers ──────────────────────────────────────────────────────────

        /// <summary>
        /// BSG IDs of the container types the user has selected to display.
        /// Empty = show none. Populated by the container selection UI.
        /// </summary>
        public List<string> SelectedContainers { get; set; } = [];

        /// <summary>Hide containers that have been searched/opened.</summary>
        public bool HideSearchedContainers { get; set; } = true;

        // ── Persistence ─────────────────────────────────────────────────────────

        /// <summary>
        /// Clamps all numeric properties to safe ranges, preventing corrupt/hand-edited
        /// config files from producing invalid state.
        /// </summary>
        private void Validate()
        {
            UIScale = Math.Clamp(UIScale, 0.5f, 3.0f);
            TargetFps = Math.Clamp(TargetFps, 30, 300);
            WindowWidth = Math.Clamp(WindowWidth, 800, 7680);
            WindowHeight = Math.Clamp(WindowHeight, 600, 4320);

            AimviewPlayerDistance = Math.Clamp(AimviewPlayerDistance, 1f, 2000f);
            AimviewLootDistance = Math.Clamp(AimviewLootDistance, 1f, 500f);
            AimviewEyeHeight = Math.Clamp(AimviewEyeHeight, 0f, 5f);
            AimviewZoom = Math.Clamp(AimviewZoom, 0.5f, 5.0f);
            AimviewMinLootValue = Math.Max(AimviewMinLootValue, 0);
            AimviewMaxLoot = Math.Clamp(AimviewMaxLoot, 0, 128);
            AimviewMaxCorpses = Math.Clamp(AimviewMaxCorpses, 0, 32);
            AimviewMaxContainers = Math.Clamp(AimviewMaxContainers, 0, 64);
            AimlineLength = Math.Clamp(AimlineLength, 0, 500);

            GameMonitorWidth = Math.Clamp(GameMonitorWidth, 640, 7680);
            GameMonitorHeight = Math.Clamp(GameMonitorHeight, 480, 4320);

            DoorLootProximity = Math.Clamp(DoorLootProximity, 1f, 200f);

            EspPlayerDistance = Math.Clamp(EspPlayerDistance, 10f, 2000f);
            EspLootDistance = Math.Clamp(EspLootDistance, 10f, 500f);
            EspCorpseDistance = Math.Clamp(EspCorpseDistance, 10f, 1000f);
            EspTripwireDistance = Math.Clamp(EspTripwireDistance, 10f, 500f);
            EspRenderMode = Math.Clamp(EspRenderMode, 0, 3);

            // Note: legacy EspRenderMode is still used by the "EspCycleRenderMode" hotkey.
            // The hotkey now also syncs the values to the checkbox flags (EspShowBox / EspShowBones / EspShowHeadDot).
            // New primary UI is checkboxes in 透视设置. Property initializers match the old default (mode=2 = box).
            EspCrosshairType = Math.Clamp(EspCrosshairType, 0, 5);
            EspCrosshairScale = Math.Clamp(EspCrosshairScale, 0.5f, 5f);
            EspUIScale = Math.Clamp(EspUIScale, 0.5f, 4f);
            EspTargetFps = Math.Clamp(EspTargetFps, 0, 360);
            // no-op clamps for bools

            var g = EspGrenades ??= new EspGrenadeConfig();
            g.MaxDistance = Math.Clamp(g.MaxDistance, 20f, 1000f);
            g.TrailLifetime = Math.Clamp(g.TrailLifetime, 0.5f, 30f);
            g.DotRadius = Math.Clamp(g.DotRadius, 1f, 15f);
            g.TrailWidth = Math.Clamp(g.TrailWidth, 0.5f, 10f);

            LootMinPrice = Math.Max(LootMinPrice, 0);
            LootImportantPrice = Math.Max(LootImportantPrice, 0);
            LootPriceSource = Math.Clamp(LootPriceSource, 0, 2);

            WebRadarPort = Math.Clamp(WebRadarPort, 1024, 65535);
            WebRadarTickMs = Math.Clamp(WebRadarTickMs, 16, 1000);

            // Distance-aware refresh: keep tiers ordered and multipliers sane.
            DistanceRefreshNearMeters = Math.Clamp(DistanceRefreshNearMeters, 25f, 1000f);
            DistanceRefreshMidMeters = Math.Clamp(DistanceRefreshMidMeters, DistanceRefreshNearMeters + 25f, 1500f);
            GearRefreshMidMul = Math.Clamp(GearRefreshMidMul, 1f, 10f);
            GearRefreshFarMul = Math.Clamp(GearRefreshFarMul, GearRefreshMidMul, 20f);
            HandsRefreshMidMul = Math.Clamp(HandsRefreshMidMul, 1f, 10f);
            HandsRefreshFarMul = Math.Clamp(HandsRefreshFarMul, HandsRefreshMidMul, 20f);

            Hotkeys ??= [];

            // Strict F2 reservation for "display toggle inside an *already open* ESP window".
            // F2 must NEVER control opening or closing the window itself (user requirement).
            // "ToggleEspWindow" action (if user binds a key to it) is only for open/close via hotkey.
            // "ToggleEspRender" (F2 default) is purely for toggling _renderEnabled (show/hide perspective content) while window stays open.
            // Double-click on the ESP window itself (local input) is for fullscreen <-> windowed mode switch.
            bool hotkeyChanged = false;
            int f2 = VK.F2;

            // If "ToggleEspWindow" is (still) bound to F2, unbind it. F2 is reserved exclusively for inside-window display toggle.
            if (Hotkeys.TryGetValue("ToggleEspWindow", out var winEntry) &&
                winEntry is { Enabled: true, Key: var wk } && wk == f2)
            {
                winEntry.Key = 0;
                hotkeyChanged = true;
                Log.WriteLine("[Config] Unbound F2 from ToggleEspWindow (F2 reserved exclusively for inside-window display toggle)");
            }

            // Force F2 onto ToggleEspRender. This guarantees that the physical F2 key is *always* the action for
            // "toggle display / not display inside an already open ESP window" (never for open/close the window).
            // If the user had rebound the render action to another key, this will move F2 back to it on load (to honor
            // the explicit "f2绑定的hotkey" requirement). User can rebind in the hotkey panel afterwards if desired.
            if (!Hotkeys.TryGetValue("ToggleEspRender", out var rendEntry) || rendEntry.Key != f2)
            {
                Hotkeys["ToggleEspRender"] = new HotkeyEntry
                {
                    Enabled = true,
                    Key = f2,
                    Mode = HotkeyMode.Toggle,
                };
                hotkeyChanged = true;
                Log.WriteLine("[Config] Ensured F2 is bound to ToggleEspRender (inside-window display toggle only)");
            }

            // Make sure the window action entry exists (key defaults to 0 — user can bind a *different* key if they want hotkey-controlled open/close).
            if (!Hotkeys.ContainsKey("ToggleEspWindow"))
            {
                Hotkeys["ToggleEspWindow"] = new HotkeyEntry
                {
                    Enabled = true,
                    Key = 0,
                    Mode = HotkeyMode.Toggle,
                };
                hotkeyChanged = true;
            }

            if (hotkeyChanged)
            {
                MarkDirty(); // persist the F2 reservation / unbind so the correct behavior survives restarts and is saved to disk
            }
            SelectedContainers ??= [];
            QuestBlacklist ??= [];
            MapCalibrationOverrides ??= new(StringComparer.OrdinalIgnoreCase);

            RightDockWidth = Math.Clamp(RightDockWidth, 260f, 720f);

            // Snap TarkovDevMapRotation to the nearest supported quadrant.
            TarkovDevMapRotation = ((TarkovDevMapRotation % 360) + 360) % 360;
            TarkovDevMapRotation = TarkovDevMapRotation switch
            {
                >= 45 and < 135  => 90,
                >= 135 and < 225 => 180,
                >= 225 and < 315 => 270,
                _ => 0,
            };

            // Seed built-in preset baselines on first load (or after a fresh install).
            eft_dma_radar.Silk.UI.Presets.PresetManager.EnsureSeeded(this);

            if (string.IsNullOrWhiteSpace(DeviceStr))
                DeviceStr = "fpga";
        }

        /// <summary>
        /// Load config from disk. Returns a default instance if the file does not exist or is corrupt.
        /// All values are clamped to safe ranges after deserialization.
        /// </summary>
        public static SilkConfig Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var cfg = JsonSerializer.Deserialize<SilkConfig>(json) ?? new SilkConfig();
                    cfg.Validate();
                    Log.WriteLine("[SilkConfig] Config loaded OK.");
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[SilkConfig] Failed to load config, using defaults: {ex.Message}");
            }

            Log.WriteLine("[SilkConfig] No config found, using defaults.");
            var defaultCfg = new SilkConfig();
            defaultCfg.Validate();
            return defaultCfg;
        }

        /// <summary>
        /// Marks the config as dirty. The next call to <see cref="FlushIfDirty"/>
        /// (after the debounce interval) will persist it to disk.
        /// </summary>
        public void MarkDirty()
        {
            _dirty = true;
            Interlocked.Exchange(ref _dirtyTimestamp, Environment.TickCount64);
        }

        // ── VisCheck (PhysX visibility) ──────────────────────────────────────
        // All visibility-classifier settings persist here so the user's
        // see-through / force-blocker rule edits survive restart. The layer
        // mask is conservative (only layer 16 = player colliders) to avoid
        // silently disabling vischeck if the user forgets to tune it.

        [JsonPropertyName("visCheckSeeThroughLayerMask")]
        public uint VisCheckSeeThroughLayerMask { get; set; } = 1u << 16;

        [JsonPropertyName("visCheckGlobalNamePatterns")]
        public string[] VisCheckGlobalNamePatterns { get; set; } = ["Glass", "Cube (", "PlayerSuperior"];

        [JsonPropertyName("visCheckMapNamePatterns")]
        public Dictionary<string, string[]> VisCheckMapNamePatterns { get; set; } = new();

        [JsonPropertyName("visCheckGlobalBlockerPatterns")]
        public string[] VisCheckGlobalBlockerPatterns { get; set; } = [];

        [JsonPropertyName("visCheckMapBlockerPatterns")]
        public Dictionary<string, string[]> VisCheckMapBlockerPatterns { get; set; } = new();

        [JsonPropertyName("visCheckMaxRayDistance")]
        public float VisCheckMaxRayDistance { get; set; } = 200f;

        [JsonPropertyName("visCheckBoneHead")]   public bool VisCheckBoneHead   { get; set; } = true;
        [JsonPropertyName("visCheckBoneChest")]  public bool VisCheckBoneChest  { get; set; } = true;
        [JsonPropertyName("visCheckBonePelvis")] public bool VisCheckBonePelvis { get; set; } = true;

        [JsonPropertyName("visCheckDiagDumpSnapshot")]  public bool VisCheckDiagDumpSnapshot  { get; set; } = false;
        [JsonPropertyName("visCheckDiagLogTicks")]      public bool VisCheckDiagLogTicks      { get; set; } = false;
        [JsonPropertyName("visCheckDiagLogClassifier")] public bool VisCheckDiagLogClassifier { get; set; } = false;

        [JsonPropertyName("traceDmaExceptions")] public bool TraceDmaExceptions { get; set; } = false;

        /// <summary>
        /// Persists the config to disk if it has been marked dirty and the debounce
        /// interval has elapsed. Call periodically from the render loop or a timer.
        /// </summary>
        public void FlushIfDirty()
        {
            if (!_dirty)
                return;
            if (Environment.TickCount64 - Interlocked.Read(ref _dirtyTimestamp) < DebounceSaveMs)
                return;
            _dirty = false;
            Save();
        }

        /// <summary>
        /// Save config to disk immediately.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                var json = JsonSerializer.Serialize(this, _jsonWriteOptions);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[SilkConfig] Failed to save config: {ex.Message}");
            }
        }
    }
}
