// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Numerics;

using eft_dma_radar.Silk.Tarkov.GameWorld.Player;

namespace eft_dma_radar.Silk.UI
{
    /// <summary>
    /// Shared ImGui style tokens — colors, radii, border thicknesses, focus
    /// styling. Single source of truth so the desktop UI has one accent, one
    /// radius family, one border weight, and one focus style as called for in
    /// Phase 6 of the UX modernization plan.
    /// </summary>
    internal static class UITheme
    {
        // ── Accent (active state) ───────────────────────────────────────────
        /// <summary>The one and only "on" accent. Used by toggles, focus ring,
        /// active sidebar slot, top-bar pill on-state, etc.</summary>
        public static readonly Vector4 Accent       = new(0.00f, 0.80f, 0.80f, 1f);
        public static readonly Vector4 AccentSoft   = new(0.00f, 0.80f, 0.80f, 0.35f);
        public static readonly Vector4 AccentFaint  = new(0.00f, 0.80f, 0.80f, 0.18f);

        // ── Radii (one family, three sizes) ─────────────────────────────────
        /// <summary>Inner controls (steppers, small buttons, chips).</summary>
        public const float RadiusSmall  = 4f;
        /// <summary>Generic frames, toggle rows, pills.</summary>
        public const float RadiusMedium = 6f;
        /// <summary>Windows, popups, panels.</summary>
        public const float RadiusLarge  = 10f;

        // ── Border weights ──────────────────────────────────────────────────
        /// <summary>Thin border for sections / dividers.</summary>
        public const float BorderThin   = 1f;
        /// <summary>Default border for windows, popups, focus ring.</summary>
        public const float BorderDefault = 1.5f;
        /// <summary>Emphasis border for selected / focused elements.</summary>
        public const float BorderFocus  = 2f;

        // ── Focus ring color (matches Accent so kbd/gamepad nav matches
        // the same active-state language) ───────────────────────────────────
        public static readonly Vector4 FocusRing = Accent;

        // ── Status / semantic colors ────────────────────────────────────────
        public static readonly Vector4 Green   = new(0.30f, 0.69f, 0.31f, 1f);
        public static readonly Vector4 Red     = new(0.94f, 0.33f, 0.31f, 1f);
        public static readonly Vector4 Orange  = new(1.00f, 0.60f, 0.00f, 1f);
        public static readonly Vector4 Yellow  = new(1.00f, 0.84f, 0.00f, 1f);
        public static readonly Vector4 Gold    = new(1.00f, 0.84f, 0.00f, 1f);
        public static readonly Vector4 Cyan    = Accent;
        public static readonly Vector4 Blue    = new(0.40f, 0.60f, 1.00f, 1f);
        public static readonly Vector4 Magenta = new(0.85f, 0.44f, 0.84f, 1f);
        public static readonly Vector4 Kappa   = new(0.58f, 0.44f, 0.86f, 1f);
        public static readonly Vector4 White   = new(1f, 1f, 1f, 1f);

        // ── Neutrals ────────────────────────────────────────────────────────
        public static readonly Vector4 Grey    = new(0.62f, 0.62f, 0.62f, 1f);
        public static readonly Vector4 Slate   = new(0.47f, 0.56f, 0.61f, 1f);
        public static readonly Vector4 Dim     = new(1f, 1f, 1f, 0.38f);

        // ── Accent (auto-save hint, highlight) ──────────────────────────────
        public static readonly Vector4 AccentGreen = new(0.55f, 0.75f, 0.55f, 1f);

        // ── Player-type colors (ImGui, mirrors SKPaints) ────────────────────
        public static readonly Vector4 PlayerTeammate    = new(0.31f, 0.86f, 0.31f, 1f);
        public static readonly Vector4 PlayerUSEC        = new(0.90f, 0.24f, 0.24f, 1f);
        public static readonly Vector4 PlayerBEAR        = new(0.27f, 0.51f, 0.90f, 1f);
        public static readonly Vector4 PlayerPScav       = new(0.86f, 0.86f, 0.86f, 1f);
        public static readonly Vector4 PlayerAIScav      = new(0.86f, 0.86f, 0.86f, 1f);
        public static readonly Vector4 PlayerAIBoss      = new(0.94f, 0.50f, 0.10f, 1f);
        public static readonly Vector4 PlayerAIRaider    = new(0.70f, 0.35f, 0.35f, 1f);
        public static readonly Vector4 PlayerStreamer     = new(0.85f, 0.44f, 0.84f, 1f);
        public static readonly Vector4 PlayerDefault     = new(0.94f, 0.90f, 0.24f, 1f);

        // ── Aimview / overlay ───────────────────────────────────────────────
        public static readonly Vector4 OverlayCrosshair  = new(1f, 1f, 1f, 0.40f);
        public static readonly Vector4 OverlayBackground = new(0f, 0f, 0f, 0.75f);
        public static readonly Vector4 OverlayDotOutline = new(0f, 0f, 0f, 0.60f);
        public static readonly Vector4 OverlayShadow     = new(0f, 0f, 0f, 0.80f);
        public static readonly Vector4 OverlayBorder     = new(0.4f, 0.4f, 0.4f, 0.60f);
        public static readonly Vector4 OverlayCorpse     = new(0.85f, 0.55f, 0.20f, 0.90f);
        public static readonly Vector4 OverlayContainer  = new(0.39f, 0.78f, 0.86f, 0.85f);
        public static readonly Vector4 OverlayLoot         = new(0.78f, 0.78f, 0.78f, 0.85f);
        public static readonly Vector4 OverlayLootImportant = new(0.20f, 1.0f, 0.20f, 1.0f);
        public static readonly Vector4 OverlayLootWishlist  = new(0.00f, 0.90f, 1.0f, 1.0f);
        public static Vector4 ForPlayerType(PlayerType type) => type switch
        {
            PlayerType.Teammate     => PlayerTeammate,
            PlayerType.USEC         => PlayerUSEC,
            PlayerType.BEAR         => PlayerBEAR,
            PlayerType.PScav        => PlayerPScav,
            PlayerType.AIScav       => PlayerAIScav,
            PlayerType.AIBoss       => PlayerAIBoss,
            PlayerType.AIRaider     => PlayerAIRaider,
            PlayerType.Streamer     => PlayerStreamer,
            _                       => PlayerDefault,
        };
    }
}
