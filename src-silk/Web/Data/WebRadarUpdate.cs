// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Top-level radar state snapshot sent to the web client via <c>/api/radar</c>.
    /// </summary>
    public sealed class WebRadarUpdate
    {
        public uint Version { get; set; }
        public bool InGame { get; set; }
        public bool InRaid { get; set; }
        public bool InHideout { get; set; }

        /// <summary>
        /// Human-readable status string matching what the local radar overlay shows:
        /// matching stage name, "In Hideout", "In Raid", or "Waiting for Raid Start".
        /// </summary>
        public string Status { get; set; } = "Waiting for Raid Start";

        public string? MapID { get; set; }
        public DateTime SendTime { get; set; }

        /// <summary>
        /// ID of the radar preset currently active on the desktop host
        /// (Stealth / LootRun / PvP / Quests / Custom). Web clients show this in
        /// the bottom tab bar so buddies see what mode the host is in.
        /// </summary>
        public string? ActivePreset { get; set; }

        public WebRadarMapInfo? Map { get; set; }
        public WebRadarPlayer[]? Players { get; set; }
        public WebRadarLootItem[]? Loot { get; set; }
        public WebRadarCorpse[]? Corpses { get; set; }
        public WebRadarContainer[]? Containers { get; set; }
        public WebRadarExfil[]? Exfils { get; set; }
        public WebRadarKillfeedEntry[]? Killfeed { get; set; }
        public WebRadarSwitch[]? Switches { get; set; }
        public WebRadarDoor[]? Doors { get; set; }
        public WebRadarTransit[]? Transits { get; set; }
        public WebRadarBtr? Btr { get; set; }
        public WebRadarAirdrop[]? Airdrops { get; set; }

        /// <summary>Live camera state (FOV, ADS, scoped, viewport) from the host.</summary>
        public WebRadarCamera? Camera { get; set; }
    }
}
