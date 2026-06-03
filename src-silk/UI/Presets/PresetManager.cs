// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Config;

namespace eft_dma_radar.Silk.UI.Presets
{
    /// <summary>
    /// Hardcoded baseline preset (built-in template). Used as a reset target only.
    /// User-facing presets are stored in <see cref="SilkConfig.Presets"/> as
    /// mutable <see cref="RadarPresetEntry"/> records.
    /// </summary>
    internal sealed record RadarPresetBaseline(
        string Id,
        string DisplayName,
        string Description,
        bool BattleMode,
        bool ShowLoot,
        bool ShowCorpses,
        bool ShowContainers,
        bool ShowExfils,
        bool ShowDoors,
        bool ShowAirdrops,
        bool ShowSwitches,
        bool ShowTransits,
        bool ShowAimlines,
        bool ConnectGroups,
        bool HighAlert,
        bool PlayersOnTop);

    /// <summary>
    /// Preset CRUD + apply/cycle logic. Presets are mutable user-facing records
    /// (<see cref="RadarPresetEntry"/>) persisted in <see cref="SilkConfig.Presets"/>.
    /// The 4 built-in baselines (Stealth / Loot Run / PvP / Quests) are seeded on
    /// first load and can be edited, deleted, or reset to defaults; users can
    /// also save the current toggle state as a brand-new named preset.
    /// </summary>
    internal static class PresetManager
    {
        public const string CustomId = "Custom";

        /// <summary>
        /// Hardcoded baselines. Used as seeds on first load and as the reset
        /// target when the user clicks "Reset to defaults" on a built-in preset.
        /// Each entry is intentionally distinct — Stealth = silent extract,
        /// Loot Run = max info, PvP = hunter mode, Quests = objectives-only.
        /// </summary>
        public static readonly RadarPresetBaseline[] BuiltInDefaults =
        [
            new("Stealth", "Stealth",
                "Silent extract: clean map, only what's needed to evade and exfil. " +
                "No loot / corpses / containers, doors and switches off.",
                BattleMode: true,
                ShowLoot: false, ShowCorpses: false, ShowContainers: false,
                ShowExfils: true, ShowDoors: false, ShowAirdrops: false,
                ShowSwitches: false, ShowTransits: true,
                ShowAimlines: true, ConnectGroups: true, HighAlert: true,
                PlayersOnTop: true),
            new("LootRun", "Loot Run",
                "Max info: every world entity visible. Players drawn below loot " +
                "so dots stay readable. High Alert off to reduce visual noise.",
                BattleMode: false,
                ShowLoot: true, ShowCorpses: true, ShowContainers: true,
                ShowExfils: true, ShowDoors: true, ShowAirdrops: true,
                ShowSwitches: true, ShowTransits: true,
                ShowAimlines: true, ConnectGroups: true, HighAlert: false,
                PlayersOnTop: false),
            new("PvP", "PvP",
                "Hunter mode. Corpses on (active engagement zones), airdrops on " +
                "(PvP magnets), doors on (key-room fights). Players on top so " +
                "combat callouts are always readable.",
                BattleMode: true,
                ShowLoot: false, ShowCorpses: true, ShowContainers: false,
                ShowExfils: true, ShowDoors: true, ShowAirdrops: true,
                ShowSwitches: false, ShowTransits: false,
                ShowAimlines: true, ConnectGroups: true, HighAlert: true,
                PlayersOnTop: true),
            new("Quests", "Quests",
                "Objective focus. Loot on (quest items), doors and switches on " +
                "(mechanics). Corpses and containers off to reduce visual noise. " +
                "Airdrops off because they pull people away from objectives.",
                BattleMode: false,
                ShowLoot: true, ShowCorpses: false, ShowContainers: false,
                ShowExfils: true, ShowDoors: true, ShowAirdrops: false,
                ShowSwitches: true, ShowTransits: true,
                ShowAimlines: true, ConnectGroups: true, HighAlert: true,
                PlayersOnTop: false),
        ];

        /// <summary>
        /// Seed <see cref="SilkConfig.Presets"/> with the built-in defaults if empty.
        /// Also fills in any missing built-in baseline (if a user deleted one) is NOT
        /// auto-restored — deletion is permanent until the user resets the whole list.
        /// </summary>
        public static void EnsureSeeded(SilkConfig cfg)
        {
            cfg.Presets ??= [];
            if (cfg.Presets.Count != 0)
                return;
            foreach (var b in BuiltInDefaults)
                cfg.Presets.Add(FromBaseline(b));
        }

        private static RadarPresetEntry FromBaseline(RadarPresetBaseline b) => new()
        {
            Id = b.Id,
            DisplayName = b.DisplayName,
            Description = b.Description,
            BaselineId = b.Id,
            BattleMode = b.BattleMode,
            ShowLoot = b.ShowLoot,
            ShowCorpses = b.ShowCorpses,
            ShowContainers = b.ShowContainers,
            ShowExfils = b.ShowExfils,
            ShowDoors = b.ShowDoors,
            ShowAirdrops = b.ShowAirdrops,
            ShowSwitches = b.ShowSwitches,
            ShowTransits = b.ShowTransits,
            ShowAimlines = b.ShowAimlines,
            ConnectGroups = b.ConnectGroups,
            HighAlert = b.HighAlert,
            PlayersOnTop = b.PlayersOnTop,
        };

        public static RadarPresetEntry? Find(SilkConfig cfg, string id)
        {
            if (cfg.Presets is null) return null;
            for (int i = 0; i < cfg.Presets.Count; i++)
                if (cfg.Presets[i].Id == id) return cfg.Presets[i];
            return null;
        }

        /// <summary>Display name for any id, including <c>Custom</c>.</summary>
        public static string DisplayNameFor(SilkConfig cfg, string id)
            => id == CustomId ? "Custom" : (Find(cfg, id)?.DisplayName ?? id);

        /// <summary>
        /// All selectable preset ids in cycle order (saved presets + Custom).
        /// </summary>
        public static IReadOnlyList<string> AllIds(SilkConfig cfg)
        {
            EnsureSeeded(cfg);
            var ids = new List<string>(cfg.Presets!.Count + 1);
            foreach (var p in cfg.Presets!) ids.Add(p.Id);
            ids.Add(CustomId);
            return ids;
        }

        /// <summary>
        /// Apply the named preset to the given config and persist it.
        /// <paramref name="cfg"/>'s <see cref="SilkConfig.ActivePresetId"/> is updated.
        /// "Custom" is a no-op — it represents the user's current drift state.
        /// </summary>
        public static void Apply(string id, SilkConfig cfg)
        {
            if (id == CustomId)
            {
                cfg.ActivePresetId = CustomId;
                cfg.MarkDirty();
                return;
            }

            var preset = Find(cfg, id);
            if (preset is null)
                return;

            cfg.BattleMode = preset.BattleMode;
            cfg.ShowLoot = preset.ShowLoot;
            cfg.ShowCorpses = preset.ShowCorpses;
            cfg.ShowContainers = preset.ShowContainers;
            cfg.ShowExfils = preset.ShowExfils;
            cfg.ShowDoors = preset.ShowDoors;
            cfg.ShowAirdrops = preset.ShowAirdrops;
            cfg.ShowSwitches = preset.ShowSwitches;
            cfg.ShowTransits = preset.ShowTransits;
            cfg.ShowAimlines = preset.ShowAimlines;
            cfg.ConnectGroups = preset.ConnectGroups;
            cfg.HighAlert = preset.HighAlert;
            cfg.PlayersOnTop = preset.PlayersOnTop;
            cfg.ActivePresetId = preset.Id;
            cfg.MarkDirty();
            eft_dma_radar.Silk.UI.Shell.ToastManager.Info($"Preset: {preset.DisplayName}");
        }

        /// <summary>
        /// Cycle the active preset by <paramref name="delta"/> slots
        /// (e.g. +1 for next, -1 for previous). Wraps around.
        /// </summary>
        public static void Cycle(int delta, SilkConfig cfg)
        {
            var ids = AllIds(cfg);
            int idx = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == cfg.ActivePresetId)
                {
                    idx = i;
                    break;
                }
            }
            int next = ((idx + delta) % ids.Count + ids.Count) % ids.Count;
            Apply(ids[next], cfg);
        }

        /// <summary>
        /// Returns true if the current live toggles still match the stored values
        /// of the active preset. If they don't, the user has drifted into "Custom".
        /// </summary>
        public static bool MatchesActive(SilkConfig cfg)
        {
            if (cfg.ActivePresetId == CustomId)
                return true;

            var p = Find(cfg, cfg.ActivePresetId);
            if (p is null)
                return false;

            return cfg.BattleMode == p.BattleMode
                && cfg.ShowLoot == p.ShowLoot
                && cfg.ShowCorpses == p.ShowCorpses
                && cfg.ShowContainers == p.ShowContainers
                && cfg.ShowExfils == p.ShowExfils
                && cfg.ShowDoors == p.ShowDoors
                && cfg.ShowAirdrops == p.ShowAirdrops
                && cfg.ShowSwitches == p.ShowSwitches
                && cfg.ShowTransits == p.ShowTransits
                && cfg.ShowAimlines == p.ShowAimlines
                && cfg.ConnectGroups == p.ConnectGroups
                && cfg.HighAlert == p.HighAlert
                && cfg.PlayersOnTop == p.PlayersOnTop;
        }

        /// <summary>
        /// Demotes the active preset to "Custom" if the user has drifted
        /// from the stored values. Called once per frame from the UI.
        /// </summary>
        public static void ReconcileDrift(SilkConfig cfg)
        {
            if (cfg.ActivePresetId == CustomId)
                return;
            if (!MatchesActive(cfg))
            {
                cfg.ActivePresetId = CustomId;
                cfg.MarkDirty();
            }
        }

        // ── Management ────────────────────────────────────────────────────────

        /// <summary>True if the preset originated from a hardcoded baseline (can be reset).</summary>
        public static bool IsBuiltin(RadarPresetEntry p) => !string.IsNullOrEmpty(p.BaselineId);

        /// <summary>
        /// Copy the current radar toggle state into a new preset with the given name.
        /// Returns the new entry (or null if name is empty / collides).
        /// </summary>
        public static RadarPresetEntry? SaveCurrentAsNew(SilkConfig cfg, string name, string description = "")
        {
            EnsureSeeded(cfg);
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();

            string id = GenerateUniqueId(cfg, name);
            var entry = new RadarPresetEntry
            {
                Id = id,
                DisplayName = name,
                Description = description ?? "",
                BaselineId = null, // user-created — no reset target
                BattleMode = cfg.BattleMode,
                ShowLoot = cfg.ShowLoot,
                ShowCorpses = cfg.ShowCorpses,
                ShowContainers = cfg.ShowContainers,
                ShowExfils = cfg.ShowExfils,
                ShowDoors = cfg.ShowDoors,
                ShowAirdrops = cfg.ShowAirdrops,
                ShowSwitches = cfg.ShowSwitches,
                ShowTransits = cfg.ShowTransits,
                ShowAimlines = cfg.ShowAimlines,
                ConnectGroups = cfg.ConnectGroups,
                HighAlert = cfg.HighAlert,
                PlayersOnTop = cfg.PlayersOnTop,
            };
            cfg.Presets!.Add(entry);
            cfg.ActivePresetId = id;
            cfg.MarkDirty();
            return entry;
        }

        /// <summary>
        /// Overwrite the stored toggle values of <paramref name="id"/> with the
        /// current live radar state. Used by "Save changes" after the user
        /// tweaks live toggles and wants to bake them into the active preset.
        /// </summary>
        public static bool UpdateInPlace(SilkConfig cfg, string id)
        {
            var p = Find(cfg, id);
            if (p is null) return false;
            p.BattleMode = cfg.BattleMode;
            p.ShowLoot = cfg.ShowLoot;
            p.ShowCorpses = cfg.ShowCorpses;
            p.ShowContainers = cfg.ShowContainers;
            p.ShowExfils = cfg.ShowExfils;
            p.ShowDoors = cfg.ShowDoors;
            p.ShowAirdrops = cfg.ShowAirdrops;
            p.ShowSwitches = cfg.ShowSwitches;
            p.ShowTransits = cfg.ShowTransits;
            p.ShowAimlines = cfg.ShowAimlines;
            p.ConnectGroups = cfg.ConnectGroups;
            p.HighAlert = cfg.HighAlert;
            p.PlayersOnTop = cfg.PlayersOnTop;
            cfg.ActivePresetId = id;
            cfg.MarkDirty();
            return true;
        }

        public static bool Rename(SilkConfig cfg, string id, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;
            var p = Find(cfg, id);
            if (p is null) return false;
            p.DisplayName = newName.Trim();
            cfg.MarkDirty();
            return true;
        }

        public static bool SetDescription(SilkConfig cfg, string id, string description)
        {
            var p = Find(cfg, id);
            if (p is null) return false;
            p.Description = description ?? "";
            cfg.MarkDirty();
            return true;
        }

        public static bool Delete(SilkConfig cfg, string id)
        {
            if (cfg.Presets is null) return false;
            int idx = cfg.Presets.FindIndex(e => e.Id == id);
            if (idx < 0) return false;
            cfg.Presets.RemoveAt(idx);
            if (cfg.ActivePresetId == id)
                cfg.ActivePresetId = CustomId;
            cfg.MarkDirty();
            return true;
        }

        /// <summary>
        /// Restore a preset's stored values from its hardcoded baseline.
        /// Returns false if the preset has no baseline (i.e. it was user-created).
        /// </summary>
        public static bool ResetToDefaults(SilkConfig cfg, string id)
        {
            var p = Find(cfg, id);
            if (p is null || string.IsNullOrEmpty(p.BaselineId)) return false;
            var b = System.Array.Find(BuiltInDefaults, x => x.Id == p.BaselineId);
            if (b is null) return false;
            p.DisplayName = b.DisplayName;
            p.Description = b.Description;
            p.BattleMode = b.BattleMode;
            p.ShowLoot = b.ShowLoot;
            p.ShowCorpses = b.ShowCorpses;
            p.ShowContainers = b.ShowContainers;
            p.ShowExfils = b.ShowExfils;
            p.ShowDoors = b.ShowDoors;
            p.ShowAirdrops = b.ShowAirdrops;
            p.ShowSwitches = b.ShowSwitches;
            p.ShowTransits = b.ShowTransits;
            p.ShowAimlines = b.ShowAimlines;
            p.ConnectGroups = b.ConnectGroups;
            p.HighAlert = b.HighAlert;
            p.PlayersOnTop = b.PlayersOnTop;
            cfg.MarkDirty();
            return true;
        }

        private static string GenerateUniqueId(SilkConfig cfg, string baseName)
        {
            // Slug from name, then append a counter if it collides.
            string slug = new(baseName.Where(char.IsLetterOrDigit).ToArray());
            if (slug.Length == 0) slug = "Preset";
            string id = slug;
            int n = 2;
            while (cfg.Presets!.Any(p => p.Id == id) || id == CustomId)
            {
                id = $"{slug}{n++}";
            }
            return id;
        }
    }
}
