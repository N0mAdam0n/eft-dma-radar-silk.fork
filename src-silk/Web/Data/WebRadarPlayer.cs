// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Flattened player snapshot for the web radar client.
    /// All coordinates are world-space — the JS client computes map-space position.
    /// </summary>
    public sealed class WebRadarPlayer
    {
        public string Name { get; set; } = string.Empty;
        public WebPlayerType Type { get; set; }
        public bool IsActive { get; set; }
        public bool IsAlive { get; set; }
        public bool IsLocal { get; set; }
        public bool IsFriendly { get; set; }
        public bool IsHuman { get; set; }
        public int GroupId { get; set; }
        public int GearValue { get; set; }

        // ── Loadout detail (mirrors the desktop player tooltip: hands / gear / health) ──

        /// <summary>Short name of the item currently in hands (null until read).</summary>
        public string? InHandsItem { get; set; }

        /// <summary>Chambered ammo short name when a weapon is held (null otherwise).</summary>
        public string? InHandsAmmo { get; set; }

        /// <summary>Whether a thermal optic/device is equipped.</summary>
        public bool HasThermal { get; set; }

        /// <summary>Whether night-vision is equipped.</summary>
        public bool HasNVG { get; set; }

        /// <summary>
        /// Observed health tier: 0 = Healthy, 1 = Injured, 2 = Badly Injured, 3 = Dying.
        /// Null for the local player (health is not observed for ourselves).
        /// </summary>
        public int? Health { get; set; }

        /// <summary>Per-slot equipment breakdown. Null until gear has been read.</summary>
        public WebRadarGearEntry[]? Gear { get; set; }

        // World-space position (body root / feet reference used by the radar map)
        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }

        // Eye-level position (for aimview projection origin). Equals the in-game
        // look-transform position for the local player, otherwise WorldY + a small
        // pose-aware fallback eye offset. World-space, same coordinate system as WorldX/Y/Z.
        public float EyeX { get; set; }
        public float EyeY { get; set; }
        public float EyeZ { get; set; }

        // Rotation (radians, pre-converted for JS canvas — map rotation, yaw-90°)
        public float Yaw { get; set; }

        // Raw game rotation in radians — used by aimview 3D projection
        public float RawYaw { get; set; }
        public float Pitch { get; set; }

        // Observed-player movement/body state (radians for yaws, m/s for velocity).
        // 0/default for the local player and AIs that don't supply these fields.
        public float BodyYaw { get; set; }
        public float VelocityX { get; set; }
        public float VelocityY { get; set; }
        public float VelocityZ { get; set; }
        public float LinearSpeed { get; set; }
        public float Tilt { get; set; }
        public int Pose { get; set; }
        public float PoseLevel { get; set; }
        public bool IsGrounded { get; set; }

        /// <summary>Whether this player is aiming down sights (local _isAiming or observed _isAimingObs).</summary>
        public bool IsADS { get; set; }

        /// <summary>
        /// Flattened skeleton bone world positions (X,Y,Z triples) in
        /// <see cref="Skeleton.SerializedBoneOrder"/> order. Length is either 0 (no skeleton
        /// available) or <c>3 * Skeleton.SerializedBoneCount</c>. Missing bones are encoded
        /// as <see cref="float.NaN"/> so the client can skip them.
        /// </summary>
        public float[]? Bones { get; set; }

        /// <summary>
        /// Creates a web radar player snapshot from a Silk player instance.
        /// </summary>
        internal static WebRadarPlayer CreateFromPlayer(Player player)
        {
            var isLocal = player.IsLocalPlayer;

            var webType = isLocal ? WebPlayerType.LocalPlayer :
                player.Type switch
                {
                    PlayerType.Teammate => WebPlayerType.Teammate,
                    PlayerType.USEC or PlayerType.BEAR => WebPlayerType.Player,
                    PlayerType.PScav => WebPlayerType.PlayerScav,
                    PlayerType.AIBoss => WebPlayerType.Boss,
                    PlayerType.AIRaider => WebPlayerType.Raider,
                    _ => WebPlayerType.Bot
                };

            var pos = player.Position;
            var mapYawRad = player.MapRotation * (MathF.PI / 180f);
            var rawYawRad = player.RotationYaw * (MathF.PI / 180f);
            var pitchRad = player.RotationPitch * (MathF.PI / 180f);
            var bodyYawRad = player.BodyYaw * (MathF.PI / 180f);
            var vel = player.Velocity;

            // Eye position: prefer the in-game look transform (LocalPlayer only).
            // For other players, prefer the resolved head-bone world position (most
            // accurate, matches what's actually rendered) and only fall back to a
            // pose-adjusted body height when the skeleton head bone isn't available.
            Vector3 eye;
            if (player is LocalPlayer lp && lp.HasLookPosition)
            {
                eye = lp.LookPosition;
            }
            else
            {
                Vector3? headBone = player.Skeleton?.GetBonePosition(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanHead);
                if (headBone is Vector3 hb && !float.IsNaN(hb.X))
                {
                    // Eye sits ~0.10m above the head joint on the actual rig.
                    eye = new Vector3(hb.X, hb.Y + 0.10f, hb.Z);
                }
                else
                {
                    // Standing ≈ 1.62m, crouch ≈ 1.05m, prone ≈ 0.35m
                    float poseLvl = MathF.Max(0.2f, MathF.Min(1f, player.PoseLevel <= 0f ? 1f : player.PoseLevel));
                    bool isProne = player.Pose == 2;
                    float eyeOff = isProne ? 0.35f : (0.55f + 1.07f * poseLvl);
                    eye = new Vector3(pos.X, pos.Y + eyeOff, pos.Z);
                }
            }

            // Skeleton bones — only serialized when the camera worker has produced a
            // skeleton (UseAdvancedAimview enabled OR web radar running). NaN encodes
            // missing bones so the JS client can skip individual joints.
            float[]? bones = null;
            var skel = player.Skeleton;
            if (skel is not null)
            {
                int count = Skeleton.SerializedBoneCount;
                Span<Vector3> tmp = stackalloc Vector3[count];
                skel.CopyBoneWorldPositions(tmp);
                bones = new float[count * 3];
                bool any = false;
                for (int i = 0; i < count; i++)
                {
                    var v = tmp[i];
                    bones[i * 3 + 0] = v.X;
                    bones[i * 3 + 1] = v.Y;
                    bones[i * 3 + 2] = v.Z;
                    if (!float.IsNaN(v.X)) any = true;
                }
                if (!any) bones = null;
            }

            return new WebRadarPlayer
            {
                Name = player.Name,
                Type = webType,
                IsActive = player.IsActive,
                IsAlive = player.IsAlive,
                IsLocal = isLocal,
                IsFriendly = player.Type is PlayerType.Teammate,
                IsHuman = player.IsHuman,
                GroupId = player.SpawnGroupID,
                GearValue = player.GearValue,
                InHandsItem = player.HandsReady ? player.InHandsItem : null,
                InHandsAmmo = player.HandsReady ? player.InHandsAmmo : null,
                HasThermal = player.HasThermal,
                HasNVG = player.HasNVG,
                Health = isLocal ? null : (int)player.HealthStatus,
                Gear = BuildGear(player),
                WorldX = pos.X,
                WorldY = pos.Y,
                WorldZ = pos.Z,
                EyeX = eye.X,
                EyeY = eye.Y,
                EyeZ = eye.Z,
                Yaw = mapYawRad,
                RawYaw = rawYawRad,
                Pitch = pitchRad,
                BodyYaw = bodyYawRad,
                VelocityX = vel.X,
                VelocityY = vel.Y,
                VelocityZ = vel.Z,
                LinearSpeed = player.LinearSpeed,
                Tilt = player.Tilt,
                Pose = player.Pose,
                PoseLevel = player.PoseLevel,
                IsGrounded = player.IsGrounded,
                IsADS = player.IsADS,
                Bones = bones,
            };
        }

        /// <summary>
        /// Flattens a player's equipment dictionary into serializable slot/name/price
        /// entries for the web tooltip. Returns null until gear has been read.
        /// </summary>
        private static WebRadarGearEntry[]? BuildGear(Player player)
        {
            if (!player.GearReady)
                return null;

            var equip = player.Equipment;
            if (equip.Count == 0)
                return null;

            var gear = new WebRadarGearEntry[equip.Count];
            int i = 0;
            foreach (var kvp in equip)
            {
                gear[i++] = new WebRadarGearEntry
                {
                    Slot = kvp.Key,
                    Name = kvp.Value.Short,
                    Price = kvp.Value.Price,
                };
            }
            return gear;
        }
    }

    /// <summary>
    /// A single equipped item, flattened for the web radar gear breakdown.
    /// </summary>
    public sealed class WebRadarGearEntry
    {
        /// <summary>Raw equipment slot key (e.g. "FirstPrimaryWeapon"). Formatted client-side.</summary>
        public string Slot { get; set; } = string.Empty;

        /// <summary>Short item name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Best rouble price (0 if unknown).</summary>
        public int Price { get; set; }
    }
}
