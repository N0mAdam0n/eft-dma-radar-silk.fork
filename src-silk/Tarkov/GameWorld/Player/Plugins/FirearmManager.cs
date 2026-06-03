// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Player.Plugins
{
    /// <summary>
    /// Reads held-weapon firearm state: fire mode, magazine ammo count and capacity.
    /// Chambered ammo shortname is owned by <see cref="HandsManager"/>.
    /// </summary>
    internal static class FirearmManager
    {
        internal static void Refresh(ulong playerBase, Player player)
        {
            if (!player.IsWeaponInHands)
            {
                ClearFirearmState(player);
                return;
            }

            try
            {
                ulong weaponBase = HandsManager.GetCachedItem(playerBase);
                if (weaponBase == 0)
                {
                    ClearFirearmState(player);
                    return;
                }

                player.FireMode = TryReadFireMode(weaponBase);

                if (TryReadMagazineCounts(weaponBase, out int current, out int capacity))
                {
                    player.AmmoInMag = current;
                    player.MagCapacity = capacity;
                }
                else
                {
                    player.AmmoInMag = -1;
                    player.MagCapacity = -1;
                }
            }
            catch
            {
                ClearFirearmState(player);
            }
        }

        private static void ClearFirearmState(Player player)
        {
            player.FireMode = null;
            player.AmmoInMag = -1;
            player.MagCapacity = -1;
        }

        private static string? TryReadFireMode(ulong weaponBase)
        {
            if (!Memory.TryReadPtr(weaponBase + Offsets.LootItemWeapon.FireMode, out var fireModePtr, false) || fireModePtr == 0)
                return null;
            if (!Memory.TryReadValue<byte>(fireModePtr + Offsets.FireModeComponent.FireMode, out var raw, false))
                return null;
            return raw switch
            {
                0 => "single",
                1 => "burst",
                2 => "auto",
                3 => "semi",
                4 => "doublesemi",
                _ => null,
            };
        }

        private static bool TryReadMagazineCounts(ulong weaponBase, out int current, out int capacity)
        {
            current = 0;
            capacity = 0;
            int chamberSlotCount = 0;

            // Chambers
            if (Memory.TryReadPtr(weaponBase + Offsets.LootItemWeapon.Chambers, out var chambersPtr, false) && chambersPtr != 0)
            {
                if (TryReadChamberArray(chambersPtr, out int chambers, out int loaded))
                {
                    chamberSlotCount = chambers;
                    capacity += chambers;
                    current += loaded;
                }
            }

            // Magazine slot
            if (!Memory.TryReadPtr(weaponBase + Offsets.LootItemWeapon._magSlotCache, out var magSlot, false) || magSlot == 0)
                return capacity > 0;
            if (!Memory.TryReadPtr(magSlot + Offsets.Slot.ContainedItem, out var magItem, false) || magItem == 0)
                return capacity > 0;

            // Revolver/chamber path
            if (Memory.TryReadPtr(magItem + Offsets.LootItemMod.Slots, out var magChambersPtr, false)
                && magChambersPtr != 0
                && TryReadChamberArray(magChambersPtr, out int magChambers, out int magLoaded)
                && magChambers > 0)
            {
                capacity += magChambers;
                current += magLoaded;
                return true;
            }

            // Standard magazine cartridges path
            if (!Memory.TryReadPtr(magItem + Offsets.LootItemMagazine.Cartridges, out var cartridgesPtr, false) || cartridgesPtr == 0)
                return capacity > 0;

            if (chamberSlotCount > 0)
                capacity -= chamberSlotCount;

            if (!Memory.TryReadValue<int>(cartridgesPtr + Offsets.StackSlot.MaxCount, out var slotMax, false))
                return capacity > 0;
            capacity += slotMax;

            if (!Memory.TryReadPtr(cartridgesPtr + Offsets.StackSlot._items, out var stackListPtr, false) || stackListPtr == 0)
                return capacity > 0;

            if (!Memory.TryReadPtr(stackListPtr + 0x10, out var stackArr, false) || stackArr == 0)
                return capacity > 0;
            if (!Memory.TryReadValue<int>(stackListPtr + 0x18, out var stackCount, false) || stackCount <= 0)
                return capacity > 0;

            int safeCount = Math.Min(stackCount, 8);
            for (int i = 0; i < safeCount; i++)
            {
                if (Memory.TryReadPtr(stackArr + 0x20 + (ulong)i * 0x8, out var sp, false) && sp != 0
                    && Memory.TryReadValue<int>(sp + Offsets.MagazineClass.StackObjectsCount, out var cnt, false))
                    current += cnt;
            }

            return capacity > 0;
        }

        private static bool TryReadChamberArray(ulong arrayPtr, out int count, out int loaded)
        {
            count = 0;
            loaded = 0;

            if (!Memory.TryReadValue<int>(arrayPtr + 0x18, out var arrCount, false) || arrCount <= 0 || arrCount > 16)
                return false;
            count = arrCount;

            for (int i = 0; i < arrCount; i++)
            {
                if (!Memory.TryReadPtr(arrayPtr + 0x20 + (ulong)i * 0x8, out var slotPtr, false) || slotPtr == 0)
                    continue;
                if (Memory.TryReadPtr(slotPtr + Offsets.Slot.ContainedItem, out var item, false) && item != 0)
                    loaded++;
            }

            return true;
        }
    }
}
