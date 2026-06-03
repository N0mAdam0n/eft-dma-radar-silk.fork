// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// Resolves a <see cref="ShotState"/> for the local player's current weapon + aim.
    /// Caches the held weapon's <see cref="BallisticsInfo"/> and only re-resolves when the
    /// weapon's <c>Item.Version</c> changes (= ammo swap / reload / weapon swap).
    /// </summary>
    /// <remarks>
    /// v1 keeps muzzle resolution intentionally simple: it uses <c>LocalPlayer.LookPosition</c>
    /// as the source and the player's yaw/pitch as the direction. This is intentionally a
    /// coarse stand-in — the side-by-side live tracer overlay will reveal any drift, and the
    /// resolver can later be upgraded to walk <c>FirearmController.Fireport</c>'s transform chain.
    /// </remarks>
    internal sealed class LocalShotResolver
    {
        private const float DefaultBcWhenMissing = 0.3f;
        private const float MinBallisticsRebuildIntervalSec = 0.5f;

        private ulong _cachedWeapon;
        private int _cachedItemVersion = -1;
        private DateTime _lastResolveAttempt = DateTime.MinValue;
        private BallisticsInfo? _cachedBallistics;
        private float _cachedEffectiveVelocity;

        /// <summary>Cached ammo / weapon name for HUD display.</summary>
        public string? CurrentAmmoShortName { get; private set; }

        /// <summary>Effective muzzle velocity after weapon + attachment modifiers (m/s).</summary>
        public float EffectiveMuzzleVelocity => _cachedEffectiveVelocity;

        public BallisticsInfo? CurrentBallistics => _cachedBallistics;

        public void Reset()
        {
            _cachedWeapon = 0;
            _cachedItemVersion = -1;
            _cachedBallistics = null;
            _cachedEffectiveVelocity = 0;
            CurrentAmmoShortName = null;
        }

        /// <summary>
        /// Build a <see cref="ShotState"/> for the local player's current shot.
        /// Returns false when the player isn't in raid, weapon data is invalid, or ammo is missing.
        /// </summary>
        public bool TryResolve(LocalPlayer lp, out ShotState state)
        {
            state = default;
            if (lp is null) return false;
            if (!lp.IsWeaponInHands) return false;

            // Resolve / refresh BallisticsInfo if needed (cheap when cached).
            if (!TryUpdateBallisticsCache(lp))
                return false;

            var info = _cachedBallistics;
            if (info is null || !info.IsAmmoValid)
                return false;

            // Source: prefer eye-level look position, fall back to player position.
            Vector3 source = lp.HasLookPosition ? lp.LookPosition : lp.Position;

            // Direction from player yaw/pitch (degrees). Game convention: yaw rotates around Y,
            // pitch tilts up/down. Match Unity's left-handed system used elsewhere in silk.
            float yawRad = lp.RotationYaw * (MathF.PI / 180f);
            float pitchRad = lp.RotationPitch * (MathF.PI / 180f);
            float cosP = MathF.Cos(pitchRad);
            Vector3 forward = new(
                MathF.Sin(yawRad) * cosP,
                -MathF.Sin(pitchRad),
                MathF.Cos(yawRad) * cosP
            );

            // Build a copy of BallisticsInfo with effective velocity (the cached one keeps the
            // ammo's base InitialSpeed so we can show both in the HUD).
            var effective = new BallisticsInfo
            {
                BulletSpeed = _cachedEffectiveVelocity > 1f ? _cachedEffectiveVelocity : info.BulletSpeed,
                BulletMassGrams = info.BulletMassGrams,
                BulletDiameterMillimeters = info.BulletDiameterMillimeters,
                BallisticCoefficient = info.BallisticCoefficient,
            };

            state = new ShotState(effective, effective.BulletSpeed, source, forward);
            return state.IsValid;
        }

        private bool TryUpdateBallisticsCache(LocalPlayer lp)
        {
            // Throttle resolve attempts when the weapon read fails — avoid hammering DMA.
            var now = DateTime.UtcNow;
            bool throttled = (now - _lastResolveAttempt).TotalSeconds < MinBallisticsRebuildIntervalSec;

            ulong weapon = HandsManager.GetCachedItem(lp.Base);
            if (weapon == 0)
            {
                if (!throttled) _lastResolveAttempt = now;
                return false;
            }

            // Cache hit: same weapon + same item version.
            if (weapon == _cachedWeapon
                && Memory.TryReadValue<int>(weapon + Offsets.LootItem.Version, out var ver)
                && ver == _cachedItemVersion
                && _cachedBallistics is not null)
            {
                return true;
            }

            if (throttled) return _cachedBallistics is not null;
            _lastResolveAttempt = now;

            try
            {
                _cachedWeapon = weapon;
                Memory.TryReadValue<int>(weapon + Offsets.LootItem.Version, out _cachedItemVersion);

                if (!TryResolveChamberedAmmoTemplate(weapon, out var ammoTemplate) || ammoTemplate == 0)
                    return false;

                if (!Memory.TryReadValue<float>(ammoTemplate + Offsets.AmmoTemplate.InitialSpeed, out var initialSpeed))
                    return false;
                if (!Memory.TryReadValue<float>(ammoTemplate + Offsets.AmmoTemplate.BulletMassGram, out var mass))
                    return false;
                if (!Memory.TryReadValue<float>(ammoTemplate + Offsets.AmmoTemplate.BulletDiameterMilimeters, out var diameter))
                    return false;
                if (!Memory.TryReadValue<float>(ammoTemplate + Offsets.AmmoTemplate.BallisticCoeficient, out var bc) || bc <= 0f)
                    bc = DefaultBcWhenMissing;

                _cachedBallistics = new BallisticsInfo
                {
                    BulletSpeed = initialSpeed,
                    BulletMassGrams = mass,
                    BulletDiameterMillimeters = diameter,
                    BallisticCoefficient = bc,
                };

                // Weapon velocity modifier (%-based).
                float weaponMul = 1f;
                if (Memory.TryReadPtr(weapon + Offsets.LootItem.Template, out var weaponTemplate)
                    && weaponTemplate.IsValidVirtualAddress()
                    && Memory.TryReadValue<float>(weaponTemplate + Offsets.WeaponTemplate.Velocity, out var weaponVelPct))
                {
                    weaponMul = 1f + weaponVelPct / 100f;
                }
                _cachedEffectiveVelocity = initialSpeed * weaponMul;

                // Resolve short name from EftDataManager for the HUD via embedded MongoID.StringID.
                try
                {
                    if (Memory.TryReadValue<Types.MongoID>(ammoTemplate + Offsets.ItemTemplate._id, out var mongo)
                        && mongo.StringID != 0
                        && Memory.TryReadUnityString(mongo.StringID, out var tpl)
                        && tpl is not null
                        && EftDataManager.AllItems.TryGetValue(tpl, out var item))
                    {
                        CurrentAmmoShortName = item.ShortName;
                    }
                }
                catch { /* HUD nicety — never fatal */ }

                return _cachedBallistics.IsAmmoValid;
            }
            catch
            {
                _cachedBallistics = null;
                return false;
            }
        }

        /// <summary>
        /// Walk the weapon to find the loaded round's <c>AmmoTemplate</c>. Tries chamber slots
        /// first, then the magazine cartridge stack. Returns the template pointer if found.
        /// </summary>
        private static bool TryResolveChamberedAmmoTemplate(ulong weapon, out ulong ammoTemplate)
        {
            ammoTemplate = 0;

            // 1) Chamber path (most accurate — this is the round about to fire).
            if (Memory.TryReadPtr(weapon + Offsets.LootItemWeapon.Chambers, out var chambersArr)
                && chambersArr.IsValidVirtualAddress()
                && TryFindFirstLoadedItem(chambersArr, out var chamberedItem))
            {
                if (TryResolveTemplate(chamberedItem, out ammoTemplate)) return true;
            }

            // 2) Magazine top round path.
            if (!Memory.TryReadPtr(weapon + Offsets.LootItemWeapon._magSlotCache, out var magSlot)
                || !magSlot.IsValidVirtualAddress())
                return false;
            if (!Memory.TryReadPtr(magSlot + Offsets.Slot.ContainedItem, out var magItem)
                || !magItem.IsValidVirtualAddress())
                return false;
            if (!Memory.TryReadPtr(magItem + Offsets.LootItemMagazine.Cartridges, out var cartridges)
                || !cartridges.IsValidVirtualAddress())
                return false;
            if (!Memory.TryReadPtr(cartridges + Offsets.StackSlot._items, out var stackListObj)
                || !stackListObj.IsValidVirtualAddress())
                return false;
            if (!Memory.TryReadPtr(stackListObj + 0x10, out var stackArr) || stackArr == 0)
                return false;
            if (!Memory.TryReadValue<int>(stackListObj + 0x18, out var stackCount) || stackCount <= 0)
                return false;

            // Top round = highest-index item in the stack.
            int topIdx = Math.Min(stackCount, 8) - 1;
            if (!Memory.TryReadPtr(stackArr + 0x20 + (ulong)topIdx * 0x8, out var topItem) || topItem == 0)
                return false;

            return TryResolveTemplate(topItem, out ammoTemplate);
        }

        private static bool TryFindFirstLoadedItem(ulong slotsArr, out ulong item)
        {
            item = 0;
            if (!Memory.TryReadValue<int>(slotsArr + 0x18, out var arrCount) || arrCount <= 0 || arrCount > 16)
                return false;
            for (int i = 0; i < arrCount; i++)
            {
                if (!Memory.TryReadPtr(slotsArr + 0x20 + (ulong)i * 0x8, out var slot) || slot == 0)
                    continue;
                if (Memory.TryReadPtr(slot + Offsets.Slot.ContainedItem, out var contained)
                    && contained.IsValidVirtualAddress())
                {
                    item = contained;
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveTemplate(ulong item, out ulong template)
        {
            template = 0;
            if (!Memory.TryReadPtr(item + Offsets.LootItem.Template, out var t) || !t.IsValidVirtualAddress())
                return false;
            template = t;
            return true;
        }
    }
}
