// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.GameWorld.Btr;

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Flattened BTR snapshot for the buddy web radar. Only emitted when active.
    /// </summary>
    public sealed class WebRadarBtr
    {
        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }

        /// <summary>Current speed (m/s).</summary>
        public float Speed { get; set; }

        /// <summary>Raw EBtrState byte.</summary>
        public int State { get; set; }

        /// <summary>Raw EBtrRouteState byte (approach/at-stop/leaving).</summary>
        public int RouteState { get; set; }

        /// <summary>Remaining pause time at current stop in milliseconds.</summary>
        public int TimeToEndPauseMs { get; set; }

        /// <summary>True when a player has paid for taxi service this raid.</summary>
        public bool IsPaid { get; set; }

        /// <summary>Turret yaw in world degrees (0..360).</summary>
        public float TurretYawDeg { get; set; }

        /// <summary>Ordered route stops (named passenger stops only — unnamed depots omitted).</summary>
        public WebRadarBtrStop[]? RouteStops { get; set; }

        internal static WebRadarBtr? Create(BtrTracker btr)
        {
            if (!btr.IsActive)
                return null;

            var pos = btr.Position;

            WebRadarBtrStop[]? stops = null;
            var routeStops = btr.RouteStops;
            if (routeStops is { Count: > 0 })
            {
                var list = new List<WebRadarBtrStop>(routeStops.Count);
                for (int i = 0; i < routeStops.Count; i++)
                {
                    var s = routeStops[i];
                    if (s.Name is null)
                        continue; // skip unnamed depot waypoints
                    list.Add(new WebRadarBtrStop
                    {
                        Id = s.Id,
                        Name = s.Name,
                        WorldX = s.Position.X,
                        WorldY = s.Position.Y,
                        WorldZ = s.Position.Z,
                    });
                }
                if (list.Count > 0)
                    stops = [.. list];
            }

            return new WebRadarBtr
            {
                WorldX = pos.X,
                WorldY = pos.Y,
                WorldZ = pos.Z,
                Speed = btr.CurrentSpeed,
                State = btr.State,
                RouteState = btr.RouteState,
                TimeToEndPauseMs = btr.TimeToEndPauseMs,
                IsPaid = btr.IsPaid,
                TurretYawDeg = btr.TurretYawDeg,
                RouteStops = stops,
            };
        }
    }

    public sealed class WebRadarBtrStop
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }
    }
}
