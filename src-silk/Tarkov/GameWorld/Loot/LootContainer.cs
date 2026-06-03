// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Loot
{
    /// <summary>
    /// A static loot container on the map (e.g. duffle-bag, toolbox, weapon box).
    /// Identified by BSG ID from <see cref="EftDataManager.AllContainers"/>.
    /// </summary>
    internal sealed class LootContainer
    {
        /// <summary>BSG ID of the container type (e.g. "578f87a3245977356274f2cb").</summary>
        public string Id { get; }

        /// <summary>Short display name (e.g. "Duffle bag", "Toolbox").</summary>
        public string Name { get; }

        /// <summary>World position of the container.</summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// True if the container has been opened/searched by any player.
        /// Mutable so the static container cache can refresh just this flag without rebuilding the object.
        /// </summary>
        private volatile bool _searched;
        public bool Searched => _searched;

        /// <summary>Updates the searched flag in place. Called from the loot worker thread.</summary>
        internal void UpdateSearched(bool searched) => _searched = searched;

        // ── Draw helpers ────────────────────────────────────────────────────

        // Stroke paint for the container square marker
        private static readonly SKPaint _markerStroke = new()
        {
            Color = SKPaints.PaintContainer.Color,
            StrokeWidth = 1.6f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };

        private static readonly SKPaint _markerOutline = new()
        {
            Color = new SKColor(0, 0, 0, 160),
            StrokeWidth = 2.8f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };

        public LootContainer(string bsgId, string name, Vector3 position, bool searched)
        {
            Id = bsgId;
            Name = name;
            Position = position;
            _searched = searched;
        }

        /// <summary>
        /// Draw this container on the radar canvas as a small square marker with name label.
        /// </summary>
        public void Draw(SKCanvas canvas, SKPoint screenPos, bool showName)
        {
            const float halfSize = 3.5f;

            // Draw square marker (outline then fill)
            var rect = new SKRect(
                screenPos.X - halfSize, screenPos.Y - halfSize,
                screenPos.X + halfSize, screenPos.Y + halfSize);
            canvas.DrawRect(rect, _markerOutline);
            canvas.DrawRect(rect, _markerStroke);

            if (showName)
            {
                float lx = screenPos.X + 7f;
                float ly = screenPos.Y + 4.5f;
                canvas.DrawText(Name, lx + 1f, ly + 1f, SKPaints.FontRegular11, SKPaints.LootShadow);
                canvas.DrawText(Name, lx, ly, SKPaints.FontRegular11, SKPaints.TextContainer);
            }
        }
    }
}
