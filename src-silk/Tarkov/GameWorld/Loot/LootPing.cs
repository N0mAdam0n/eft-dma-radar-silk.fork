// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Loot
{
    /// <summary>
    /// Ephemeral, in-session set of "pinged" loot keyed by item short name.
    /// Pinged loot is drawn with an extra pulsing highlight ring on the radar so a
    /// specific item type can be located quickly from the loot panel.
    /// <para>
    /// Pings are intentionally <b>not</b> persisted and do <b>not</b> affect filtering
    /// or visibility — they only add a highlight to loot that is already on the map.
    /// Keyed by short name so a panel row and every matching ground item line up.
    /// </para>
    /// </summary>
    internal static class LootPing
    {
        // Set semantics (the byte value is unused). Case-insensitive so panel rows and
        // render-time lookups match regardless of casing.
        private static readonly ConcurrentDictionary<string, byte> _pinged =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Number of distinct pinged item names.</summary>
        public static int Count => _pinged.Count;

        /// <summary>Whether any loot is currently pinged.</summary>
        public static bool Any => !_pinged.IsEmpty;

        /// <summary>
        /// Whether the given item short name is pinged. This is on the render hot path
        /// (called per visible loot item per frame); the empty short-circuit keeps it
        /// free when the feature is unused.
        /// </summary>
        public static bool IsPinged(string shortName) =>
            !_pinged.IsEmpty && _pinged.ContainsKey(shortName);

        /// <summary>Toggles ping state for an item name. Returns the new state (true = pinged).</summary>
        public static bool Toggle(string shortName)
        {
            if (string.IsNullOrEmpty(shortName))
                return false;
            if (_pinged.TryRemove(shortName, out _))
                return false;
            _pinged[shortName] = 0;
            return true;
        }

        /// <summary>Removes all pings.</summary>
        public static void Clear() => _pinged.Clear();
    }
}
