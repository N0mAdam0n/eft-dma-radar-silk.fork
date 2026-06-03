// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using VmmSharpEx.Options;
using static SDK.Offsets;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Player
{
    /// <summary>
    /// Reads player equipment from memory and builds gear data.
    /// All slot pointer/name/template/BSG-ID reads are batched into 4 scatter rounds
    /// to eliminate the ~200 sequential DMA reads per player that the old serial loop required.
    /// Called from the registration worker for each active player.
    /// </summary>
    internal static class GearManager
    {
        #region Special Item IDs

        private static readonly FrozenSet<string> ThermalIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "5c110624d174af029e69734c",
                "6478641c19d732620e045e17",
                "609bab8b455afd752b2e6138",
                "63fc44e2429a8a166c7f61e6",
                "5d1b5e94d7ad1a2b865a96b0",
                "606f2696f2cb2e02a42aceb1",
                "5a1eaa87fcdbcb001865f75e"
            }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenSet<string> NvgIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "5c066e3a0db834001b7353f0",
                "5c0696830db834001d23f5da",
                "5c0558060db834001b735271",
                "57235b6f24597759bf5a30f1",
                "5b3b6e495acfc4330140bd88",
                "5a7c74b3e899ef0014332c29"
            }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenSet<string> SkipSlots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Compass",
                "ArmBand"
            }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private const string SecureSlot = "SecuredContainer";
        private const string DogtagSlot = "Dogtag";

        // Maximum recursion depth for nested mod slots (safety guard)
        private const int MaxRecursionDepth = 4;

        // Unity string header: chars start at offset 0x14, each char is 2 bytes (UTF-16).
        // We read a fixed 64-char window per string which covers all BSG IDs and slot names.
        private const int StringReadBytes = 128; // 64 UTF-16 chars

        #endregion

        #region Top-Level Slot Cache

        /// <summary>
        /// Cached gear result for a player, keyed by playerBase. Mid-raid equipment changes
        /// are detected by comparing the top-level slot <c>ContainedItem</c> pointers (read in
        /// Round 1 of the scatter pipeline) with the last-known set. If none changed, the
        /// expensive Rounds 2–4 plus the serial mod-slot recursion are skipped entirely.
        /// </summary>
        private sealed class GearCacheEntry
        {
            public ulong[] ItemPtrs = Array.Empty<ulong>();
            public FrozenDictionary<string, GearItem> Equipment = FrozenDictionary<string, GearItem>.Empty;
            public int GearValue;
            public bool HasNvg;
            public bool HasThermal;
        }

        private static readonly ConcurrentDictionary<ulong, GearCacheEntry> _cache = new();

        /// <summary>
        /// Removes the cached gear entry for a player base. Called by RegisteredPlayers when
        /// a player is removed, so cache memory does not leak across raids.
        /// </summary>
        internal static void ClearCache(ulong playerBase) => _cache.TryRemove(playerBase, out _);

        /// <summary>
        /// Drops every cached gear entry. Called when the raid ends.
        /// </summary>
        internal static void ClearAll() => _cache.Clear();

        #endregion

        /// <summary>
        /// Reads all equipment slots for a player and updates gear properties on the <see cref="Player"/> instance.
        /// </summary>
        /// <param name="playerBase">Base address of the player/observed player view.</param>
        /// <param name="player">The player to update.</param>
        /// <param name="isObserved">Whether this is an observed player (different offset chain).</param>
        internal static void Refresh(ulong playerBase, Player player, bool isObserved)
        {
            try
            {
                if (!TryReadEquipmentSlots(playerBase, isObserved, out var slotsArr))
                {
                    Log.WriteRateLimited(AppLogLevel.Warning,
                        $"gear_chain_{playerBase:X}", TimeSpan.FromSeconds(30),
                        $"[GearManager] Equipment chain failed for '{player.Name}' (observed={isObserved})");
                    return;
                }

                using (slotsArr)
                {
                    // Fast path: if we already resolved gear for this player and none of the
                    // top-level slot ContainedItem pointers changed, reuse the cached result.
                    // This avoids Rounds 2–4 plus the serial RecurseModSlots walk (~80–180 ms
                    // for fully-kitted humans) when equipment is unchanged, which is the
                    // common case mid-raid.
                    if (_cache.TryGetValue(playerBase, out var cached) &&
                        TryFastValidateCache(slotsArr, cached))
                    {
                        ApplyCached(player, cached);
                        return;
                    }

                    BuildGear(playerBase, player, slotsArr);
                }
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning,
                    $"gear_ex_{playerBase:X}", TimeSpan.FromSeconds(30),
                    $"[GearManager] Refresh exception for '{player.Name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Single-round scatter that reads each slot's <c>ContainedItem</c> pointer and compares
        /// to the cached set. Returns true only if the slot count and every pointer match.
        /// </summary>
        private static bool TryFastValidateCache(MemArray<ulong> slotPtrs, GearCacheEntry cached)
        {
            int n = slotPtrs.Count;
            if (n == 0 || cached.ItemPtrs.Length != n)
                return false;

            using var r = Memory.GetScatter(VmmFlags.NOCACHE);
            for (int i = 0; i < n; i++)
            {
                var slotPtr = slotPtrs[i];
                if (slotPtr.IsValidVirtualAddress())
                    r.PrepareReadPtr(slotPtr + Offsets.Slot.ContainedItem);
            }
            r.Execute();

            for (int i = 0; i < n; i++)
            {
                var slotPtr = slotPtrs[i];
                ulong current = 0;
                if (slotPtr.IsValidVirtualAddress())
                    r.ReadValue<ulong>(slotPtr + Offsets.Slot.ContainedItem, out current);
                if (current != cached.ItemPtrs[i])
                    return false;
            }
            return true;
        }

        private static void ApplyCached(Player player, GearCacheEntry cached)
        {
            player.Equipment = cached.Equipment;
            player.GearValue = cached.GearValue;
            player.HasNVG = cached.HasNvg;
            player.HasThermal = cached.HasThermal;
        }

        /// <summary>
        /// Walks the inventory controller → inventory → equipment → slots pointer chain
        /// and reads all slot pointers from the C# array.
        /// </summary>
        private static bool TryReadEquipmentSlots(
            ulong playerBase,
            bool isObserved,
            out MemArray<ulong> slotsArr)
        {
            slotsArr = null!;

            ulong invController;
            if (isObserved)
            {
                if (!Memory.TryReadPtr(playerBase + Offsets.ObservedPlayerView.ObservedPlayerController, out var obsController)
                    || !obsController.IsValidVirtualAddress())
                    return false;

                if (!Memory.TryReadPtr(obsController + Offsets.ObservedPlayerController.InventoryController, out invController)
                    || !invController.IsValidVirtualAddress())
                    return false;
            }
            else
            {
                if (!Memory.TryReadPtr(playerBase + Offsets.Player._inventoryController, out invController)
                    || !invController.IsValidVirtualAddress())
                    return false;
            }

            if (!Memory.TryReadPtr(invController + Offsets.InventoryController.Inventory, out var inventory)
                || !inventory.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtr(inventory + Offsets.Inventory.Equipment, out var equipment)
                || !equipment.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtr(equipment + Offsets.Equipment.Slots, out var slotsPtr)
                || !slotsPtr.IsValidVirtualAddress())
                return false;

            try
            {
                slotsArr = MemArray<ulong>.Get(slotsPtr, false);
            }
            catch
            {
                return false;
            }

            return slotsArr.Count > 0;
        }

        /// <summary>
        /// Builds gear data using 4 batched scatter rounds instead of sequential per-slot DMA reads.
        /// <para>
        /// Round 1 — Slot.ID ptr + Slot.ContainedItem ptr for every slot (2 × N reads in one call).
        /// Round 2 — slot name string bytes + LootItem.Template ptr for slots with valid ptrs.
        /// Round 3 — MongoID struct (template + 0xE0) for slots that passed name/item filtering.
        /// Round 4 — BSG ID string bytes (MongoID.StringID + 0x14) for slots with a valid MongoID.
        /// </para>
        /// Dogtag detection uses the already-read slot name ("Dogtag") instead of a per-item
        /// Il2CppClass.ReadName call, avoiding ~3 extra DMA hops per slot for every human player.
        /// Mod-slot recursion remains serial (tree structure prevents batching), but depth is
        /// capped at 4 (down from 8) to bound the worst-case serial read count.
        /// </summary>
        private static void BuildGear(ulong playerBase, Player player, MemArray<ulong> slotPtrs)
        {
            int n = slotPtrs.Count;
            if (n == 0)
                return;

            var gear = new Dictionary<string, GearItem>(n, StringComparer.OrdinalIgnoreCase);
            int totalValue = 0;
            bool hasNvg = false, hasThermal = false;
            bool needsProfileId = player.IsHuman && player.ProfileId is null;

            // Per-slot working state — stack-allocated where n is small, heap otherwise.
            // We avoid LINQ and List<T> allocations on the hot path.
            ulong[]? namePtrsBuf = null;
            ulong[]? itemPtrsBuf = null;
            ulong[]? templatePtrsBuf = null;
            Types.MongoID[]? mongoIdsBuf = null;
            ulong[]? ptrSnapshot = null;

            try
            {
                namePtrsBuf = ArrayPool<ulong>.Shared.Rent(n);
                itemPtrsBuf = ArrayPool<ulong>.Shared.Rent(n);
                templatePtrsBuf = ArrayPool<ulong>.Shared.Rent(n);
                mongoIdsBuf = ArrayPool<Types.MongoID>.Shared.Rent(n);

                var namePtrs = namePtrsBuf.AsSpan(0, n);
                var itemPtrs = itemPtrsBuf.AsSpan(0, n);
                var templatePtrs = templatePtrsBuf.AsSpan(0, n);
                var mongoIds = mongoIdsBuf.AsSpan(0, n);
                namePtrs.Clear();
                itemPtrs.Clear();
                templatePtrs.Clear();

                // ── Round 1: read Slot.ID ptr + Slot.ContainedItem ptr for every valid slot ──
                using (var r1 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < n; i++)
                    {
                        var slotPtr = slotPtrs[i];
                        if (!slotPtr.IsValidVirtualAddress())
                            continue;
                        r1.PrepareReadPtr(slotPtr + Offsets.Slot.ID);
                        r1.PrepareReadPtr(slotPtr + Offsets.Slot.ContainedItem);
                    }
                    r1.Execute();

                    for (int i = 0; i < n; i++)
                    {
                        var slotPtr = slotPtrs[i];
                        if (!slotPtr.IsValidVirtualAddress())
                            continue;
                        if (r1.ReadValue<ulong>(slotPtr + Offsets.Slot.ID, out var np) && np.IsValidVirtualAddress())
                            namePtrs[i] = np;
                        if (r1.ReadValue<ulong>(slotPtr + Offsets.Slot.ContainedItem, out var ip) && ip.IsValidVirtualAddress())
                            itemPtrs[i] = ip;
                    }
                }

                // Snapshot of top-level ContainedItem ptrs (taken from Round 1 results) for
                // the gear cache. Stored only on a successful BuildGear at the end.
                ptrSnapshot = new ulong[n];
                itemPtrs.CopyTo(ptrSnapshot);

                // ── Round 2: read slot name strings + LootItem.Template ptrs ──
                using (var r2 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (namePtrs[i] != 0)
                            r2.PrepareRead(namePtrs[i] + 0x14, StringReadBytes);
                        if (itemPtrs[i] != 0)
                            r2.PrepareReadPtr(itemPtrs[i] + Offsets.LootItem.Template);
                    }
                    r2.Execute();

                    for (int i = 0; i < n; i++)
                    {
                        if (itemPtrs[i] != 0)
                        {
                            if (r2.ReadValue<ulong>(itemPtrs[i] + Offsets.LootItem.Template, out var tp) && tp.IsValidVirtualAddress())
                                templatePtrs[i] = tp;
                        }
                        // Slot name string is read lazily in Round 3 filter below — we decode it there.
                    }

                    // ── Round 3: decode slot names, filter, then read MongoID structs ──
                    using (var r3 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (namePtrs[i] == 0 || itemPtrs[i] == 0)
                                continue;

                            // Decode slot name from round-2 scatter result
                            var slotName = r2.ReadString(namePtrs[i] + 0x14, StringReadBytes, System.Text.Encoding.Unicode);
                            if (slotName is null)
                                continue;

                            if (SkipSlots.Contains(slotName))
                                continue;

                            if (slotName.Equals(SecureSlot, StringComparison.OrdinalIgnoreCase))
                            {
                                // Secure container — resolve BSG ID separately (rare, cheap)
                                ReadSecureContainerWithTemplate(templatePtrs[i], gear);
                                continue;
                            }

                            if (slotName.Equals(DogtagSlot, StringComparison.OrdinalIgnoreCase))
                            {
                                if (needsProfileId)
                                {
                                    if (TryResolveProfileId(itemPtrs[i], player))
                                        needsProfileId = false;
                                }
                                continue; // Dogtag slots have no gear value
                            }

                            if (templatePtrs[i] == 0)
                                continue;

                            r3.PrepareReadValue<Types.MongoID>(templatePtrs[i] + Offsets.ItemTemplate._id);
                        }
                        r3.Execute();

                        // ── Round 4: read BSG ID strings for slots with a valid MongoID ──
                        // Collect StringID ptrs first, then do one scatter round for all strings.
                        using (var r4 = Memory.GetScatter(VmmFlags.NOCACHE))
                        {
                            for (int i = 0; i < n; i++)
                            {
                                if (templatePtrs[i] == 0)
                                    continue;
                                if (!r3.ReadValue<Types.MongoID>(templatePtrs[i] + Offsets.ItemTemplate._id, out var mid))
                                    continue;
                                mongoIds[i] = mid;
                                if (mid.StringID.IsValidVirtualAddress())
                                    r4.PrepareRead(mid.StringID + 0x14, StringReadBytes);
                            }
                            r4.Execute();

                            // Final pass: decode BSG IDs, look up in database, apply to gear
                            for (int i = 0; i < n; i++)
                            {
                                if (namePtrs[i] == 0 || itemPtrs[i] == 0 || templatePtrs[i] == 0)
                                    continue;

                                var mid = mongoIds[i];
                                if (!mid.StringID.IsValidVirtualAddress())
                                    continue;

                                var bsgId = r4.ReadString(mid.StringID + 0x14, StringReadBytes, System.Text.Encoding.Unicode);
                                if (bsgId is null || bsgId.Length == 0)
                                    continue;

                                // Decode slot name again (already decoded in r2/r3 pass, but we need it here)
                                var slotName2 = r2.ReadString(namePtrs[i] + 0x14, StringReadBytes, System.Text.Encoding.Unicode);
                                if (slotName2 is null || SkipSlots.Contains(slotName2)
                                    || slotName2.Equals(SecureSlot, StringComparison.OrdinalIgnoreCase)
                                    || slotName2.Equals(DogtagSlot, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (EftDataManager.AllItems.TryGetValue(bsgId, out var marketItem))
                                {
                                    gear[slotName2] = new GearItem
                                    {
                                        Long = marketItem.Name,
                                        Short = marketItem.ShortName,
                                        Price = marketItem.BestPrice
                                    };
                                    totalValue += marketItem.BestPrice;

                                    if (!hasNvg && NvgIds.Contains(bsgId)) hasNvg = true;
                                    if (!hasThermal && ThermalIds.Contains(bsgId)) hasThermal = true;
                                }

                                if (slotName2 is "FirstPrimaryWeapon" or "SecondPrimaryWeapon" or "Holster" or "Headwear")
                                    RecurseModSlots(itemPtrs[i], ref totalValue, ref hasNvg, ref hasThermal, depth: 0);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (namePtrsBuf is not null) ArrayPool<ulong>.Shared.Return(namePtrsBuf);
                if (itemPtrsBuf is not null) ArrayPool<ulong>.Shared.Return(itemPtrsBuf);
                if (templatePtrsBuf is not null) ArrayPool<ulong>.Shared.Return(templatePtrsBuf);
                if (mongoIdsBuf is not null) ArrayPool<Types.MongoID>.Shared.Return(mongoIdsBuf);
            }

            // Atomic update — all properties set together so the UI never sees partial state
            player.Equipment = gear.Count > 0
                ? gear.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
                : FrozenDictionary<string, GearItem>.Empty;
            player.GearValue = totalValue;
            player.HasNVG = hasNvg;
            player.HasThermal = hasThermal;

            // Snapshot the top-level slot ContainedItem pointers (captured from Round 1) so
            // a future Refresh can detect "no changes" via a single small scatter and skip
            // the rest of the pipeline + RecurseModSlots.
            _cache[playerBase] = new GearCacheEntry
            {
                ItemPtrs = ptrSnapshot ?? Array.Empty<ulong>(),
                Equipment = player.Equipment as FrozenDictionary<string, GearItem> ?? FrozenDictionary<string, GearItem>.Empty,
                GearValue = totalValue,
                HasNvg = hasNvg,
                HasThermal = hasThermal,
            };
        }

        /// <summary>
        /// Resolves the BSG ID for a secure container using an already-read template pointer.
        /// </summary>
        private static void ReadSecureContainerWithTemplate(ulong templatePtr, Dictionary<string, GearItem> gear)
        {
            try
            {
                if (templatePtr == 0 || !templatePtr.IsValidVirtualAddress())
                    return;

                if (!Memory.TryReadValue<Types.MongoID>(templatePtr + Offsets.ItemTemplate._id, out var mongoId))
                    return;

                if (!Memory.TryReadUnityString(mongoId.StringID, out var bsgId) || bsgId is null)
                    return;

                if (EftDataManager.AllItems.TryGetValue(bsgId, out var entry))
                {
                    gear[SecureSlot] = new GearItem
                    {
                        Long = entry.Name,
                        Short = entry.ShortName,
                        Price = 0
                    };
                }
            }
            catch { }
        }

        #region Alive Dogtag — ProfileId Resolution

        /// <summary>
        /// Reads the ProfileId from a player's alive dogtag (BarterOther item).
        /// <para>
        /// Chain: BarterOther → DogtagComponent (offset 0x80) → ProfileId (offset 0x28)
        /// </para>
        /// The alive dogtag only contains the player's own ProfileId — nickname and accountId
        /// fields are empty. To resolve the real name, the ProfileId must be matched against
        /// corpse dogtag data in <see cref="DogtagCache"/>.
        /// </summary>
        private static bool TryResolveProfileId(ulong barterOtherAddr, Player player)
        {
            try
            {
                if (!Memory.TryReadPtr(barterOtherAddr + Offsets.BarterOtherOffsets.Dogtag, out var dogtag)
                    || !dogtag.IsValidVirtualAddress())
                    return false;

                if (!Memory.TryReadPtr(dogtag + Offsets.DogtagComponent.ProfileId, out var profileIdPtr)
                    || !profileIdPtr.IsValidVirtualAddress())
                    return false;

                if (!Memory.TryReadUnityString(profileIdPtr, out var profileId)
                    || string.IsNullOrWhiteSpace(profileId))
                    return false;

                player.ProfileId = profileId;
                Log.WriteLine($"[GearManager] Resolved ProfileId for {player}: {profileId}");

                // Try to resolve name immediately from the corpse dogtag cache
                DogtagCache.TryApplyIdentity(player);

                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        /// <summary>
        /// Recursively walks mod slots (LootItemMod → Slots) to find nested items
        /// like thermal scopes, NVGs mounted on headwear, etc.
        /// Depth is capped at <see cref="MaxRecursionDepth"/> to bound worst-case serial read count.
        /// </summary>
        private static void RecurseModSlots(
            ulong lootItemBase,
            ref int totalValue,
            ref bool hasNvg,
            ref bool hasThermal,
            int depth)
        {
            if (depth >= MaxRecursionDepth)
                return;

            try
            {
                if (!Memory.TryReadPtr(lootItemBase + Offsets.LootItemMod.Slots, out var modSlotsPtr)
                    || !modSlotsPtr.IsValidVirtualAddress())
                    return;

                using var modSlots = MemArray<ulong>.Get(modSlotsPtr, false);
                if (modSlots.Count < 1 || modSlots.Count > 64)
                    return;

                for (int i = 0; i < modSlots.Count; i++)
                {
                    var modSlotPtr = modSlots[i];
                    if (!modSlotPtr.IsValidVirtualAddress())
                        continue;

                    try
                    {
                        if (!Memory.TryReadPtr(modSlotPtr + Offsets.Slot.ContainedItem, out var modItem)
                            || !modItem.IsValidVirtualAddress())
                            continue;

                        if (!TryReadBsgId(modItem, out var modBsgId))
                            continue;

                        if (EftDataManager.AllItems.TryGetValue(modBsgId, out var modEntry))
                        {
                            totalValue += modEntry.BestPrice;

                            if (!hasNvg && NvgIds.Contains(modBsgId)) hasNvg = true;
                            if (!hasThermal && ThermalIds.Contains(modBsgId)) hasThermal = true;
                        }

                        RecurseModSlots(modItem, ref totalValue, ref hasNvg, ref hasThermal, depth + 1);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads a BSG item ID from a loot item's template MongoID.
        /// Used for the secure container fallback and mod-slot recursion.
        /// </summary>
        private static bool TryReadBsgId(ulong itemAddr, out string bsgId)
        {
            bsgId = string.Empty;

            if (!Memory.TryReadPtr(itemAddr + Offsets.LootItem.Template, out var template)
                || !template.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadValue<Types.MongoID>(template + Offsets.ItemTemplate._id, out var mongoId))
                return false;

            if (!Memory.TryReadUnityString(mongoId.StringID, out var id) || id is null)
                return false;

            bsgId = id;
            return true;
        }
    }
}
