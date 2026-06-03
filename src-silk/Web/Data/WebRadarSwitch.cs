// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Flattened switch snapshot for the buddy web radar.
    /// </summary>
    public sealed class WebRadarSwitch
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>0=Generic, 1=Power, 2=Alarm, 3=Door, 4=Extraction, 5=Elevator, 6=Trap.</summary>
        public int Type { get; set; }

        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }

        internal static WebRadarSwitch Create(eft_dma_radar.Silk.Tarkov.GameWorld.Interactables.Switch s)
        {
            var pos = s.Position;
            return new WebRadarSwitch
            {
                Name = s.Name,
                Type = (int)s.Type,
                WorldX = pos.X,
                WorldY = pos.Y,
                WorldZ = pos.Z,
            };
        }
    }
}
