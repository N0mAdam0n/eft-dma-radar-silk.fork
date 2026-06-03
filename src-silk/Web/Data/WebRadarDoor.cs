// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.GameWorld.Interactables;

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Flattened door snapshot for the buddy web radar.
    /// Only keyed doors with a valid state are emitted.
    /// </summary>
    public sealed class WebRadarDoor
    {
        public string Id { get; set; } = string.Empty;
        public string? KeyId { get; set; }
        public string? KeyName { get; set; }

        /// <summary>EDoorState raw value: 0=None, 1=Locked, 2=Shut, 4=Open, 8=Interacting, 16=Breaching.</summary>
        public int State { get; set; }

        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }

        internal static WebRadarDoor? Create(Door d)
        {
            if (d.DoorState == EDoorState.None || d.KeyName is null)
                return null;

            var pos = d.Position;
            return new WebRadarDoor
            {
                Id = d.Id,
                KeyId = d.KeyId,
                KeyName = d.KeyName,
                State = (int)d.DoorState,
                WorldX = pos.X,
                WorldY = pos.Y,
                WorldZ = pos.Z,
            };
        }
    }
}
