// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// Precomputed coordinate parameters for drawing the map on screen.
    /// Coordinate-conversion helpers for world → screen mapping.
    /// </summary>
    internal readonly struct MapParams
    {
        public readonly MapConfig Config;
        public readonly SKRect Bounds;
        public readonly float XScale;
        public readonly float YScale;

        internal MapParams(MapConfig config, SKRect bounds, float xScale, float yScale)
        {
            Config = config;
            Bounds = bounds;
            XScale = xScale;
            YScale = yScale;
        }

        /// <summary>
        /// Projects a Unity world position to an unzoomed map position.
        /// Handles <see cref="MapConfig.Rotation"/> by swapping/negating the X and Z
        /// axes for 90° / 180° / 270° rotations (used by tarkov.dev SVG maps when the
        /// user has chosen to rotate the bitmap perpendicular to its raw orientation).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 ToMapPos(Vector3 unityPos, MapConfig cfg)
        {
            float s = cfg.Scale * cfg.SvgScale;
            float bx = cfg.X * cfg.SvgScale;
            float by = cfg.Y * cfg.SvgScale;
            float wx = unityPos.X;
            float wz = unityPos.Z;

            // tarkov.dev's baseTransform projection (matching their Leaflet getCRS):
            //   raw_px =  scaleX * lng + marginX                 // lng = worldX
            //   raw_py = -scaleY * lat + marginY                 // lat = worldZ
            //
            // After rotating the rasterized bitmap by R degrees CCW around its center
            // (90° / 270° swap the bitmap dimensions; 180° keeps them):
            //   rot 0  : new_px = raw_px                              ; new_py = raw_py
            //   rot 90 : new_px = raw_py                              ; new_py = rawW - raw_px
            //   rot 180: new_px = rawW - raw_px                       ; new_py = rawH - raw_py
            //   rot 270: new_px = rawH - raw_py                       ; new_py = raw_px
            //
            // The catalog's BuildConfig folds rawW/H + margins into cfg.X / cfg.Y, so
            // here we only need the sign/axis pattern below. Scale stays POSITIVE.
            return cfg.Rotation switch
            {
                90  => new Vector2(bx - wz * s, by - wx * s),
                180 => new Vector2(bx - wx * s, by + wz * s),
                270 => new Vector2(bx + wz * s, by + wx * s),
                _   => new Vector2(bx + wx * s, by - wz * s),
            };
        }

        /// <summary>
        /// Projects a map position to a screen point given the current <see cref="MapParams"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SKPoint ToScreenPos(Vector2 mapPos)
        {
            return new SKPoint(
                (mapPos.X - Bounds.Left) * XScale,
                (mapPos.Y - Bounds.Top) * YScale);
        }

        /// <summary>
        /// Computes world-space X/Z bounds for the visible map area (with margin).
        /// Use for fast world-space pre-culling before ToMapPos/ToScreenPos.
        /// </summary>
        public WorldBounds GetWorldBounds(float screenMargin)
        {
            // Convert screen margin to map-space margin
            float mapMarginX = screenMargin / XScale;
            float mapMarginY = screenMargin / YScale;

            // Expand map bounds by margin
            float mapLeft   = Bounds.Left   - mapMarginX;
            float mapRight  = Bounds.Right  + mapMarginX;
            float mapTop    = Bounds.Top    - mapMarginY;
            float mapBottom = Bounds.Bottom + mapMarginY;

            // Inverse of ToMapPos. The 4 rotation cases swap which world axis
            // contributes to mapX vs mapY (and the sign on each).
            float s = Config.Scale * Config.SvgScale;
            float invS = 1f / s;
            float offsetX = Config.X * Config.SvgScale;
            float offsetY = Config.Y * Config.SvgScale;

            float worldMinX, worldMaxX, worldMinZ, worldMaxZ;
            switch (Config.Rotation)
            {
                case 90:
                    // mapX = offX - Z*s   ⇒ Z = -(mapX - offX)/s
                    // mapY = offY - X*s   ⇒ X = -(mapY - offY)/s
                    worldMinZ = -(mapRight  - offsetX) * invS;
                    worldMaxZ = -(mapLeft   - offsetX) * invS;
                    worldMinX = -(mapBottom - offsetY) * invS;
                    worldMaxX = -(mapTop    - offsetY) * invS;
                    break;
                case 180:
                    // mapX = offX - X*s   ⇒ X = -(mapX - offX)/s
                    // mapY = offY + Z*s   ⇒ Z = (mapY - offY)/s
                    worldMinX = -(mapRight  - offsetX) * invS;
                    worldMaxX = -(mapLeft   - offsetX) * invS;
                    worldMinZ = (mapTop    - offsetY) * invS;
                    worldMaxZ = (mapBottom - offsetY) * invS;
                    break;
                case 270:
                    // mapX = offX + Z*s   ⇒ Z = (mapX - offX)/s
                    // mapY = offY + X*s   ⇒ X = (mapY - offY)/s
                    worldMinZ = (mapLeft   - offsetX) * invS;
                    worldMaxZ = (mapRight  - offsetX) * invS;
                    worldMinX = (mapTop    - offsetY) * invS;
                    worldMaxX = (mapBottom - offsetY) * invS;
                    break;
                default:
                    // mapX = offX + X*s   ⇒ X = (mapX - offX)/s
                    // mapY = offY - Z*s   ⇒ Z = -(mapY - offY)/s
                    worldMinX = (mapLeft   - offsetX) * invS;
                    worldMaxX = (mapRight  - offsetX) * invS;
                    worldMinZ = -(mapBottom - offsetY) * invS;
                    worldMaxZ = -(mapTop    - offsetY) * invS;
                    break;
            }

            // Scale may be negative (e.g. 180°-rotated satellite maps), which swaps
            // min/max. Normalise so Contains() works regardless of sign.
            if (worldMinX > worldMaxX) (worldMinX, worldMaxX) = (worldMaxX, worldMinX);
            if (worldMinZ > worldMaxZ) (worldMinZ, worldMaxZ) = (worldMaxZ, worldMinZ);

            return new WorldBounds(worldMinX, worldMaxX, worldMinZ, worldMaxZ);
        }
    }

    /// <summary>
    /// World-space X/Z bounds for fast entity culling.
    /// </summary>
    internal readonly struct WorldBounds
    {
        public readonly float MinX, MaxX, MinZ, MaxZ;

        public WorldBounds(float minX, float maxX, float minZ, float maxZ)
        {
            MinX = minX;
            MaxX = maxX;
            MinZ = minZ;
            MaxZ = maxZ;
        }

        /// <summary>
        /// Returns true if the world position is inside the visible bounds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(Vector3 worldPos)
        {
            return worldPos.X >= MinX && worldPos.X <= MaxX
                && worldPos.Z >= MinZ && worldPos.Z <= MaxZ;
        }
    }
}
