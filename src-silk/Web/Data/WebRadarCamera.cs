// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Snapshot of the local game camera state, surfaced to the web client so the
    /// buddy aimview can mirror the host's actual FOV / aim state.
    /// </summary>
    /// <remarks>
    /// Scope zoom value cannot currently be read from memory, so the web client
    /// applies a user-configurable scope-zoom multiplier whenever <see cref="IsScoped"/>
    /// is true.
    /// </remarks>
    public sealed class WebRadarCamera
    {
        /// <summary>True when <see cref="Tarkov.GameWorld.CameraManager"/> is initialized.</summary>
        public bool IsActive { get; set; }

        /// <summary>True if the local player is aiming down sights (any weapon).</summary>
        public bool IsADS { get; set; }

        /// <summary>True if the local player is currently looking through an optic camera (scope).</summary>
        public bool IsScoped { get; set; }

        /// <summary>Active camera vertical FOV in degrees (0 if unknown).</summary>
        public float Fov { get; set; }

        /// <summary>Active camera aspect ratio W/H (0 if unknown).</summary>
        public float Aspect { get; set; }

        /// <summary>Game viewport width in pixels (0 if unknown).</summary>
        public int ViewportWidth { get; set; }

        /// <summary>Game viewport height in pixels (0 if unknown).</summary>
        public int ViewportHeight { get; set; }

        internal static WebRadarCamera Capture()
        {
            return new WebRadarCamera
            {
                IsActive = Tarkov.GameWorld.CameraManager.IsActive,
                IsADS = Tarkov.GameWorld.CameraManager.IsADS,
                IsScoped = Tarkov.GameWorld.CameraManager.IsScoped,
                Fov = Tarkov.GameWorld.CameraManager.CurrentFov,
                Aspect = Tarkov.GameWorld.CameraManager.CurrentAspect,
                ViewportWidth = Tarkov.GameWorld.CameraManager.ViewportWidth,
                ViewportHeight = Tarkov.GameWorld.CameraManager.ViewportHeight,
            };
        }
    }
}
