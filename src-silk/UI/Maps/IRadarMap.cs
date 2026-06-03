// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// Interface for a radar map that can draw itself and provide coordinate parameters.
    /// Radar map interface.
    /// </summary>
    internal interface IRadarMap : IDisposable
    {
        string ID { get; }
        MapConfig Config { get; }
        void Draw(SKCanvas canvas, float playerHeight, SKRect mapBounds, SKRect windowBounds);
        MapParams GetParameters(SKSize canvasSize, int zoom, ref Vector2 centerMapPos);
    }
}
