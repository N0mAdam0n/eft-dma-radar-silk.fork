// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Buffers;
using System.Collections.Frozen;
using eft_dma_radar.Silk.DMA.ScatterAPI;
using eft_dma_radar.Silk.Tarkov.GameWorld.Player;
using eft_dma_radar.Silk.Tarkov.Unity;
using VmmSharpEx;
using VmmSharpEx.Options;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Loot
{
    /// <summary>
    /// Reads loose loot from the GameWorld LootList.
    /// Also scans corpses for dogtag identity data and equipment.
    /// </summary>
    internal sealed class LootManager(ulong localGameWorld)
    {
        private readonly ulong _lgw = localGameWorld;
        private volatile IReadOnlyList<LootItem> _loot = [];
        private volatile IReadOnlyList<LootCorpse> _corpses = [];
        private volatile IReadOnlyList<LootContainer> _containers = [];
        private volatile IReadOnlyList<LootAirdrop> _airdrops = [];
        private long _lastRefreshTimestamp;
        private int _consecutiveScatterFailures;
        private static readonly long FailureBackoffTicks = (long)(Stopwatch.Frequency * 1); // 1 second base backoff

        /// <summary>
        /// Per-item BSG ID Unity-string read cap (bytes). BSG IDs are 24 hex chars =
        /// 48 UTF-16 bytes; 64 gives a 33% safety margin and halves bandwidth vs the
        /// previous 128-byte cap on every uncached item per refresh.
        /// </summary>
        private const int BsgIdReadBytes = 64;

        /// <summary>Per-item short-name read cap for quest items (UTF-16 bytes).</summary>
        private const int QuestShortNameReadBytes = 96;

        /// <summary>
        /// Live refresh interval in stopwatch ticks. Read from
        /// <see cref="SilkConfig.LootRefreshIntervalSeconds"/> each tick so the user can
        /// raise it (e.g. 10s) for bandwidth-constrained setups without restarting the raid.
        /// </summary>
        private static long RefreshIntervalTicks =>
            (long)(Stopwatch.Frequency * Math.Clamp(SilkProgram.Config?.LootRefreshIntervalSeconds ?? 10f, 2f, 60f));

        // Track corpses we've already read dogtags from (by interactiveClass address)
        private readonly HashSet<ulong> _processedCorpses = [];
        private readonly HashSet<ulong> _killfeedPushed = [];
        private readonly Dictionary<ulong, int> _killfeedAttempts = [];

        // interactiveClass → timestamp (Stopwatch ticks) when gear may be re-read
        private readonly Dictionary<ulong, long> _corpseGearNextReadAt = new();
        private const double CorpseGearRefreshSeconds = 30.0;

        // interactiveClass
        private readonly Dictionary<ulong, string> _corpseNicknames = new();

        // Phase 1 scatter cache: lootBase → resolved pending data.
        // Avoids re-running the expensive 6-round pointer-chain scatter for stable entries.
        private readonly Dictionary<ulong, PendingLoot> _lootPhase1Cache = new();
        private readonly Dictionary<ulong, PendingCorpse> _corpsePhase1Cache = new();

        // Phase 2 result cache: lootBase → fully-resolved LootItem (incl. world position).
        // Ground loot doesn't move once spawned — the dropped-item Transform chain is
        // immutable for the item's lifetime in the LootList. Re-reading the BSG ID string
        // and the parent-vertex array for every single item every refresh was the single
        // biggest contributor to >200 MB/s DMA bandwidth, so we cache the resolved
        // LootItem and only emit it on subsequent refreshes (zero Phase 2 reads).
        // Pruned alongside _lootPhase1Cache when a lootBase leaves the LootList.
        private readonly Dictionary<ulong, LootItem> _lootItemCache = new();

        // Static container cache: lootBase → fully resolved LootContainer + interactiveClass.
        // Containers don't move, so after the first successful resolve we only refresh the
        // Searched flag (single InteractingPlayer ulong read per container per cycle).
        private readonly record struct ContainerCacheEntry(LootContainer Item, ulong InteractiveClass, ulong MainStoragePtr, Vector3 Position);
        private readonly Dictionary<ulong, ContainerCacheEntry> _containerCache = new();

        // Slot names to skip when reading corpse equipment
        private static readonly FrozenSet<string> _skipSlots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Compass", "ArmBand", "SecuredContainer" }
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>Current loot snapshot (thread-safe read).</summary>
        public IReadOnlyList<LootItem> Loot => _loot;

        /// <summary>Current corpse snapshot (thread-safe read).</summary>
        public IReadOnlyList<LootCorpse> Corpses => _corpses;

        /// <summary>Current static container snapshot (thread-safe read).</summary>
        public IReadOnlyList<LootContainer> Containers => _containers;

        /// <summary>Current airdrop snapshot (thread-safe read).</summary>
        public IReadOnlyList<LootAirdrop> Airdrops => _airdrops;

        /// <summary>
        /// Refreshes loot from memory. Rate-limited to once per <see cref="RefreshInterval"/>.
        /// Call from the registration worker thread.
        /// </summary>
        public void Refresh()
        {
            var now = Stopwatch.GetTimestamp();
            if (now - _lastRefreshTimestamp < RefreshIntervalTicks)
                return;
            _lastRefreshTimestamp = now;

            // Read the LootList pointer array once — shared by all phases
            if (!TryReadLootListPtrs(out var ptrs))
            {
                _loot = [];
                _corpses = [];
                _containers = [];
                _airdrops = [];
                return;
            }

            // ── Unified scatter pass: identifies loot + corpses + containers in one batched read ──
            // This eliminates hundreds of serial Il2CppClass.ReadName() calls that were
            // previously needed to filter corpses from the LootList.
            List<LootItem> lootResult = [];
            List<LootCorpse> corpseResult = [];
            List<LootContainer> containerResult = [];
            List<LootAirdrop> airdropResult = [];

            bool scatterOk = false;
            try
            {
                ReadLootAndCorpses(ptrs, out lootResult, out corpseResult, out containerResult, out airdropResult);
                scatterOk = true;
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "loot_scatter_fail", TimeSpan.FromSeconds(5),
                    $"[LootManager] Unified loot/corpse/container scatter failed: {ex.Message}");
                // Back off instead of hammering immediately. Exponential up to the normal refresh interval,
                // so a transient bad LootList entry doesn't generate scatter spam every tick.
                int fails = Math.Min(++_consecutiveScatterFailures, 6);
                long backoff = Math.Min(FailureBackoffTicks << (fails - 1), RefreshIntervalTicks);
                _lastRefreshTimestamp = now - (RefreshIntervalTicks - backoff);
            }
            finally
            {
                ptrs.Dispose();
            }

            // Only overwrite cached snapshots on a successful read — stale data is better than blank
            if (scatterOk)
            {
                _consecutiveScatterFailures = 0;
                _loot = lootResult;
                _containers = containerResult;
                _airdrops = airdropResult;
            }
            else
            {
                // Keep previous snapshots; skip corpse merge and gear reads for this tick
                return;
            }

            // Carry over previously-read name + gear data to new corpse objects.
            // The corpse list is rebuilt every loot refresh, but equipment re-reads are
            // throttled (CorpseGearRefreshSeconds) — without this carry-over, gear/value
            // would disappear between throttled reads, leaving the tooltip blank.
            var oldCorpses = _corpses;
            if (oldCorpses.Count > 0 && corpseResult.Count > 0)
            {
                Dictionary<ulong, LootCorpse>? oldByIc = null;
                foreach (var oc in oldCorpses)
                    (oldByIc ??= new(oldCorpses.Count))[oc.InteractiveClass] = oc;

                if (oldByIc is not null)
                {
                    foreach (var nc in corpseResult)
                    {
                        if (!oldByIc.TryGetValue(nc.InteractiveClass, out var oc))
                            continue;
                        if (oc.Name != "Corpse")
                            nc.Name = oc.Name;
                        if (oc.GearReady)
                        {
                            nc.Equipment = oc.Equipment;
                            nc.TotalValue = oc.TotalValue;
                            nc.GearReady = true;
                        }
                    }
                }
            }

            _corpses = corpseResult;

            // Prune per-corpse state for corpses that are no longer in the LootList.
            // Without this, entries grow unbounded across a raid and — worse — IL2CPP can
            // recycle heap addresses, so a NEW corpse at an old address would inherit the
            // "processed" flag and skip dogtag/gear reads.
            PruneCorpseState(corpseResult);

            // Dogtag + equipment reads — now iterate only known corpses (typically 0-5),
            // not the entire LootList (hundreds of items).
            try
            {
                ReadCorpseDogtags();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[LootManager] Corpse dogtag scan failed: {ex.Message}");
            }

            // Resolve corpse display names from dogtag nicknames + read equipment.
            // Equipment re-reads are throttled per-corpse (every CorpseGearRefreshSeconds)
            // so looted/removed items eventually reflect without drowning the scatter pass.
            var nowTs = Stopwatch.GetTimestamp();
            foreach (var corpse in _corpses)
            {
                if (_corpseNicknames.TryGetValue(corpse.InteractiveClass, out var nickname))
                    corpse.Name = nickname;

                if (!_corpseGearNextReadAt.TryGetValue(corpse.InteractiveClass, out var nextAt) || nowTs >= nextAt)
                {
                    ReadCorpseEquipment(corpse);
                    _corpseGearNextReadAt[corpse.InteractiveClass] =
                        nowTs + (long)(Stopwatch.Frequency * CorpseGearRefreshSeconds);
                }
            }
        }

        #region LootList Helper

        /// <summary>
        /// Removes cached Phase 1 scatter results for loot/corpse entries no longer present
        /// in the LootList. Prevents unbounded cache growth and ensures dropped or picked-up
        /// items don't persist in the cache with stale pointer chains.
        /// </summary>
        private void PruneLootCache(HashSet<ulong> currentPtrs)
        {
            if (_lootPhase1Cache.Count > 0)
            {
                List<ulong>? stale = null;
                foreach (var k in _lootPhase1Cache.Keys)
                    if (!currentPtrs.Contains(k))
                        (stale ??= new()).Add(k);
                if (stale is not null)
                    foreach (var k in stale)
                    {
                        _lootPhase1Cache.Remove(k);
                        _lootItemCache.Remove(k);
                    }
            }

            // Defensive: drop any _lootItemCache entry whose lootBase is no longer present
            // even if it lacked a Phase 1 entry (shouldn't happen, but cheap to verify).
            if (_lootItemCache.Count > 0)
            {
                List<ulong>? stale = null;
                foreach (var k in _lootItemCache.Keys)
                    if (!currentPtrs.Contains(k))
                        (stale ??= new()).Add(k);
                if (stale is not null)
                    foreach (var k in stale)
                        _lootItemCache.Remove(k);
            }

            if (_corpsePhase1Cache.Count > 0)
            {
                List<ulong>? stale = null;
                foreach (var k in _corpsePhase1Cache.Keys)
                    if (!currentPtrs.Contains(k))
                        (stale ??= new()).Add(k);
                if (stale is not null)
                    foreach (var k in stale)
                        _corpsePhase1Cache.Remove(k);
            }

            if (_containerCache.Count > 0)
            {
                List<ulong>? stale = null;
                foreach (var k in _containerCache.Keys)
                    if (!currentPtrs.Contains(k))
                        (stale ??= new()).Add(k);
                if (stale is not null)
                    foreach (var k in stale)
                        _containerCache.Remove(k);
            }
        }

        /// <summary>
        /// Removes cached per-corpse state for corpses no longer present in the LootList.
        /// Prevents unbounded growth across a raid and — critically — prevents IL2CPP
        /// heap-address recycling from carrying stale "processed" flags onto a newly
        /// spawned corpse, which would otherwise skip dogtag reads and never resolve a name.
        /// </summary>
        private void PruneCorpseState(IReadOnlyList<LootCorpse> currentCorpses)
        {
            if (_processedCorpses.Count == 0
                && _killfeedPushed.Count == 0
                && _killfeedAttempts.Count == 0
                && _corpseGearNextReadAt.Count == 0
                && _corpseNicknames.Count == 0)
                return;

            var alive = new HashSet<ulong>(currentCorpses.Count);
            foreach (var c in currentCorpses)
                alive.Add(c.InteractiveClass);

            _processedCorpses.RemoveWhere(k => !alive.Contains(k));
            _killfeedPushed.RemoveWhere(k => !alive.Contains(k));

            if (_killfeedAttempts.Count > 0)
            {
                List<ulong>? stale = null;
                foreach (var kv in _killfeedAttempts)
                    if (!alive.Contains(kv.Key))
                        (stale ??= new()).Add(kv.Key);
                if (stale is not null)
                    foreach (var k in stale)
                        _killfeedAttempts.Remove(k);
            }

            if (_corpseGearNextReadAt.Count > 0)
            {
                List<ulong>? stale = null;
                foreach (var kv in _corpseGearNextReadAt)
                    if (!alive.Contains(kv.Key))
                        (stale ??= new()).Add(kv.Key);
                if (stale is not null)
                    foreach (var k in stale)
                        _corpseGearNextReadAt.Remove(k);
            }

            if (_corpseNicknames.Count > 0)
            {
                List<ulong>? stale = null;
                foreach (var kv in _corpseNicknames)
                    if (!alive.Contains(kv.Key))
                        (stale ??= new()).Add(kv.Key);
                if (stale is not null)
                    foreach (var k in stale)
                        _corpseNicknames.Remove(k);
            }
        }

        /// <summary>
        /// Reads the LootList pointer array once, shared by loot, corpse, and dogtag phases.
        /// </summary>
        private bool TryReadLootListPtrs(out MemList<ulong> list)
        {
            list = null!;

            if (!Memory.TryReadPtr(_lgw + Offsets.ClientLocalGameWorld.LootList, out var lootListAddr))
                return false;

            try
            {
                list = MemList<ulong>.Get(lootListAddr, false);
            }
            catch
            {
                return false;
            }

            return list.Count > 0;
        }

        #endregion

        /// <summary>
        /// Unified scatter pass — reads ALL LootList entries in one batched operation,
        /// routing items to loot or corpse lists based on class name (resolved in round 3).
        /// <para>
        /// <b>Two-phase design:</b>
        /// <list type="number">
        ///   <item><b>Phase 1</b> — 6-round ScatterReadMap resolves pointer chains and collects
        ///     <c>transformInternal</c> + <c>mongoId</c> for each item into pending lists.</item>
        ///   <item><b>Phase 2</b> — batched VmmScatter reads resolve all transform positions
        ///     and BSG ID strings in ~3 DMA round-trips instead of serial reads per item.</item>
        /// </list>
        /// </para>
        /// </summary>
        private void ReadLootAndCorpses(MemList<ulong> ptrs, out List<LootItem> lootResult, out List<LootCorpse> corpseResult, out List<LootContainer> containerResult, out List<LootAirdrop> airdropResult)
        {
            lootResult = [];
            corpseResult = [];
            containerResult = [];
            airdropResult = [];

            // Pending items collected during Phase 1 scatter callbacks
            var pendingLoot = new List<PendingLoot>(ptrs.Count);
            var pendingCorpses = new List<PendingCorpse>();
            var pendingContainers = new List<PendingContainer>();
            var pendingAirdrops = new List<PendingAirdrop>();

            // Build set of all current lootBase pointers for cache pruning
            var currentPtrSet = new HashSet<ulong>(ptrs.Count);
            for (int ix = 0; ix < ptrs.Count; ix++)
            {
                var lb = ptrs[ix];
                if (Utils.IsValidVirtualAddress(lb))
                    currentPtrSet.Add(lb);
            }

            // Prune stale Phase 1 cache entries (items no longer in the LootList)
            PruneLootCache(currentPtrSet);

            // Pre-populate pending lists from cache — these skip Phase 1 scatter entirely
            foreach (var kv in _lootPhase1Cache)
                pendingLoot.Add(kv.Value);
            foreach (var kv in _corpsePhase1Cache)
                pendingCorpses.Add(kv.Value);

            // ── Phase 1: 6-round scatter to resolve pointer chains (new entries only) ──
            using (var map = ScatterReadMap.Get())
            {
                var round1 = map.AddRound();
                var round2 = map.AddRound();
                var round3 = map.AddRound();
                var round4 = map.AddRound();
                var round5 = map.AddRound();
                var round6 = map.AddRound();

                for (int ix = 0; ix < ptrs.Count; ix++)
                {
                    var i = ix;
                    var lootBase = ptrs[i];
                    if (!Utils.IsValidVirtualAddress(lootBase))
                        continue;

                    // Skip entries already resolved in the cache
                    if (_lootPhase1Cache.ContainsKey(lootBase) ||
                        _corpsePhase1Cache.ContainsKey(lootBase) ||
                        _containerCache.ContainsKey(lootBase))
                        continue;

                    // ROUND 1: MonoBehaviour (LootItemPositionClass + 0x10) + class name chain start
                    round1[i].AddEntry<MemPointer>(0, lootBase + 0x10);
                    round1[i].AddEntry<MemPointer>(1, lootBase);

                    round1[i].Callbacks += x1 =>
                    {
                        if (!x1.TryGetResult<MemPointer>(0, out var monoBehaviour) ||
                            !x1.TryGetResult<MemPointer>(1, out var c1))
                            return;

                        // ROUND 2: InteractiveClass, GameObject, class name ptr
                        round2[i].AddEntry<MemPointer>(2, monoBehaviour + UnityOffsets.Comp_ObjectClass);
                        round2[i].AddEntry<MemPointer>(3, monoBehaviour + UnityOffsets.Comp_GameObject);
                        round2[i].AddEntry<MemPointer>(4, c1 + 0x10);

                        round2[i].Callbacks += x2 =>
                        {
                            if (!x2.TryGetResult<MemPointer>(2, out var interactiveClass) ||
                                !x2.TryGetResult<MemPointer>(3, out var gameObject) ||
                                !x2.TryGetResult<MemPointer>(4, out var classNamePtr))
                                return;

                            // ROUND 3: Components array, class name string, GameObject name pointer
                            round3[i].AddEntry<MemPointer>(5, gameObject + UnityOffsets.GO_Components);
                            round3[i].AddEntry<UTF8String>(6, classNamePtr, 64);
                            round3[i].AddEntry<MemPointer>(15, gameObject + UnityOffsets.GO_Name);

                            round3[i].Callbacks += x3 =>
                            {
                                if (!x3.TryGetResult<MemPointer>(5, out var components) ||
                                    !x3.TryGetResult<UTF8String>(6, out var classNameRaw))
                                    return;

                                string? className = classNameRaw;
                                if (string.IsNullOrEmpty(className))
                                    return;

                                if (className.Equals("ObservedLootItem", StringComparison.OrdinalIgnoreCase))
                                    CollectLootItem(round4, round5, round6, i, interactiveClass, components, pendingLoot, lootBase);
                                else if (className.Contains("Corpse", StringComparison.OrdinalIgnoreCase))
                                    CollectCorpseItem(round4, round5, round6, i, interactiveClass, components, pendingCorpses, lootBase);
                                else if (className.Equals("LootableContainer", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Read objectName to distinguish airdrops ("loot_collider") from normal containers
                                    x3.TryGetResult<MemPointer>(15, out var goNamePtr);
                                    CollectContainerItem(round4, round5, round6, i, interactiveClass, components, goNamePtr, pendingContainers, pendingAirdrops, lootBase);
                                }
                            };
                        };
                    };
                }

                try
                {
                    map.Execute();
                }
                catch (Exception ex)
                {
                    // Phase 1 resolves the pointer chains; if it fails the pending lists are
                    // typically incomplete. Log and fall through so any partially-collected
                    // items can still be resolved in Phase 2 — better than blanking the radar.
                    Log.WriteRateLimited(AppLogLevel.Warning, "loot_phase1_fail", TimeSpan.FromSeconds(5),
                        $"[LootManager] Phase1 scatter failed: {ex.Message}");
                }
            }

            // Update Phase 1 caches with newly resolved entries
            foreach (var p in pendingLoot)
                if (p.LootBase != 0 && !_lootPhase1Cache.ContainsKey(p.LootBase))
                    _lootPhase1Cache[p.LootBase] = p;
            foreach (var p in pendingCorpses)
                if (p.LootBase != 0 && !_corpsePhase1Cache.ContainsKey(p.LootBase))
                    _corpsePhase1Cache[p.LootBase] = p;

            Log.WriteRateLimited(AppLogLevel.Debug, "loot_phase1", TimeSpan.FromSeconds(10),
                $"[LootManager] Phase1: list={ptrs.Count} loot={pendingLoot.Count} corpses={pendingCorpses.Count} containers={pendingContainers.Count} airdrops={pendingAirdrops.Count}");

            // ── Phase 2: Batched transform + BSG ID resolution ──────────────────
            // Each category is resolved independently; a transient scatter failure in one
            // category must NOT blank the others. Exceptions from any single Resolve* call
            // are swallowed so partial snapshots can still be returned to the caller.
            try
            {
                // Split pending into (a) entries already resolved in a prior refresh
                // and (b) brand-new entries that still need the full Phase 2 scatter.
                // Cached entries skip all 3 batches entirely — by far the biggest
                // bandwidth saving for a steady-state raid (95%+ of loot is static).
                List<PendingLoot>? newPending = null;
                int cachedHits = 0;
                lootResult = new List<LootItem>(pendingLoot.Count);
                foreach (var p in pendingLoot)
                {
                    if (p.LootBase != 0 && _lootItemCache.TryGetValue(p.LootBase, out var cached))
                    {
                        lootResult.Add(cached);
                        cachedHits++;
                    }
                    else
                    {
                        (newPending ??= new List<PendingLoot>()).Add(p);
                    }
                }
                if (newPending is not null && newPending.Count > 0)
                {
                    var resolved = ResolveLootBatched(newPending);
                    foreach (var item in resolved)
                    {
                        lootResult.Add(item);
                        // Skip caching unknowns so a later devdata refresh can resolve them
                        // to real market entries on the next loot refresh.
                        if (item.LootBase != 0 && !item.IsUnknownItem)
                            _lootItemCache[item.LootBase] = item;
                    }
                }
                Log.WriteRateLimited(AppLogLevel.Debug, "loot_cache_stats", TimeSpan.FromSeconds(30),
                    $"[LootManager] LootItem cache: {cachedHits} hits, {newPending?.Count ?? 0} new, {_lootItemCache.Count} cached total");
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "loot_resolve_phase_fail", TimeSpan.FromSeconds(5),
                    $"[LootManager] Loot resolve phase failed: {ex.Message}");
            }
            try { corpseResult = ResolveCorpsesBatched(pendingCorpses); }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "corpse_resolve_phase_fail", TimeSpan.FromSeconds(5),
                    $"[LootManager] Corpse resolve phase failed: {ex.Message}");
            }
            try
            {
                // Resolve only new (uncached) containers via the full pipeline
                var resolved = ResolveContainersBatched(pendingContainers);
                foreach (var entry in resolved)
                {
                    if (entry.LootBase != 0 && !_containerCache.ContainsKey(entry.LootBase))
                        _containerCache[entry.LootBase] = new ContainerCacheEntry(entry.Item, entry.InteractiveClass, entry.MainStoragePtr, entry.Position);
                }

                // Refresh Searched flag for ALL cached containers (incl. just-added) in one
                // batched scatter, then emit them. Containers don't move, so we never re-run
                // the expensive transform/MongoID/vertex chain for already-cached entries.
                if (_containerCache.Count > 0)
                {
                    using var sSearched = Memory.GetScatter(VmmFlags.NOCACHE);
                    foreach (var kv in _containerCache)
                    {
                        var ic = kv.Value.InteractiveClass;
                        if (ic.IsValidVirtualAddress())
                            sSearched.PrepareReadValue<ulong>(ic + Offsets.LootableContainer.InteractingPlayer);
                    }
                    sSearched.Execute();

                    foreach (var kv in _containerCache)
                    {
                        var ic = kv.Value.InteractiveClass;
                        if (ic.IsValidVirtualAddress() &&
                            sSearched.ReadValue<ulong>(ic + Offsets.LootableContainer.InteractingPlayer, out var interactingPlayer))
                        {
                            bool wasSearched = kv.Value.Item.Searched;
                            bool isNowSearched = interactingPlayer != 0;
                            kv.Value.Item.UpdateSearched(isNowSearched);

                            // Log when container is searched (has InteractingPlayer)
                            if (isNowSearched)
                            {
                                Log.WriteRateLimited(AppLogLevel.Info, $"container_searched_{kv.Value.Item.Id}", TimeSpan.FromSeconds(30),
                                    $"[LootManager] Container '{kv.Value.Item.Name}' (ID: {kv.Value.Item.Id}) is SEARCHED | InteractingPlayer: 0x{interactingPlayer:X16} | IC: 0x{ic:X16}");
                            }
                        }
                        containerResult.Add(kv.Value.Item);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "container_resolve_phase_fail", TimeSpan.FromSeconds(5),
                    $"[LootManager] Container resolve phase failed: {ex.Message}");
            }
            try { airdropResult = ResolveAirdropsBatched(pendingAirdrops); }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "airdrop_resolve_phase_fail", TimeSpan.FromSeconds(5),
                    $"[LootManager] Airdrop resolve phase failed: {ex.Message}");
            }
            Log.WriteRateLimited(AppLogLevel.Debug, "loot_phase2", TimeSpan.FromSeconds(10),
                $"[LootManager] Phase2: loot={lootResult.Count}/{pendingLoot.Count} corpses={corpseResult.Count}/{pendingCorpses.Count} containers={containerResult.Count}/{pendingContainers.Count} airdrops={airdropResult.Count}/{pendingAirdrops.Count}");
        }

        #region Phase 1 — Scatter Callbacks (collect pending items, no serial reads)

        /// <summary>
        /// Intermediate loot data collected during Phase 1 scatter — no serial DMA reads here.
        /// </summary>
        private readonly record struct PendingLoot(
            ulong TransformInternal,
            ulong BsgIdStringAddr,
            bool IsQuestItem,
            ulong ShortNameUnityString,
            ulong LootBase = 0);

        /// <summary>
        /// Intermediate corpse data collected during Phase 1 scatter — no serial DMA reads here.
        /// </summary>
        private readonly record struct PendingCorpse(ulong InteractiveClass, ulong TransformInternal, ulong LootBase = 0);

        /// <summary>
        /// Intermediate container data collected during Phase 1 scatter — no serial DMA reads here.
        /// Stores transform + template address (MongoID resolved in Phase 2) + opened state + MainStorage pointer for grid items.
        /// </summary>
        private readonly record struct PendingContainer(ulong TransformInternal, ulong TemplateAddr, bool Searched, ulong MainStoragePtr, ulong LootBase = 0, ulong InteractiveClass = 0);

        /// <summary>
        /// Intermediate airdrop data collected during Phase 1 scatter — transform only.
        /// </summary>
        private readonly record struct PendingAirdrop(ulong TransformInternal);

        /// <summary>
        /// Scatter callback for loose loot — resolves transform + BSG ID pointers (rounds 4-6),
        /// then adds to pending list. No serial reads.
        /// </summary>
        private static void CollectLootItem(
            ScatterReadRound round4, ScatterReadRound round5, ScatterReadRound round6,
            int i, ulong interactiveClass, ulong components, List<PendingLoot> pending, ulong lootBase)
        {
            round4[i].AddEntry<MemPointer>(7, components + 0x08);
            round4[i].AddEntry<MemPointer>(8, interactiveClass + Offsets.InteractiveLootItem.Item);

            round4[i].Callbacks += x4 =>
            {
                if (!x4.TryGetResult<MemPointer>(7, out var t1) ||
                    !x4.TryGetResult<MemPointer>(8, out var item))
                    return;

                round5[i].AddEntry<MemPointer>(9, t1 + UnityOffsets.Comp_ObjectClass);
                round5[i].AddEntry<MemPointer>(10, item + Offsets.LootItem.Template);

                round5[i].Callbacks += x5 =>
                {
                    if (!x5.TryGetResult<MemPointer>(9, out var t2) ||
                        !x5.TryGetResult<MemPointer>(10, out var template))
                        return;

                    round6[i].AddEntry<MemPointer>(11, t2 + 0x10);
                    round6[i].AddEntry<Types.MongoID>(12, template + Offsets.ItemTemplate._id);
                    round6[i].AddEntry<bool>(17, template + Offsets.ItemTemplate.QuestItem);
                    round6[i].AddEntry<MemPointer>(18, template + Offsets.ItemTemplate.ShortName);

                    round6[i].Callbacks += x6 =>
                    {
                        if (!x6.TryGetResult<MemPointer>(11, out var transformInternal) ||
                            !x6.TryGetResult<Types.MongoID>(12, out var mongoId))
                            return;

                        if (!mongoId.StringID.IsValidVirtualAddress())
                            return;

                        if (!((ulong)transformInternal).IsValidVirtualAddress())
                            return;

                        x6.TryGetResult<bool>(17, out var isQuestItem);
                        x6.TryGetResult<MemPointer>(18, out var shortNamePtr);

                        pending.Add(new PendingLoot(transformInternal, mongoId.StringID, isQuestItem, shortNamePtr, lootBase));
                    };
                };
            };
        }

        /// <summary>
        /// Scatter callback for corpse items — resolves transform pointer (rounds 4-6),
        /// then adds to pending list. No serial reads.
        /// </summary>
        private static void CollectCorpseItem(
            ScatterReadRound round4, ScatterReadRound round5, ScatterReadRound round6,
            int i, ulong interactiveClass, ulong components, List<PendingCorpse> pending, ulong lootBase)
        {
            round4[i].AddEntry<MemPointer>(7, components + 0x08);

            round4[i].Callbacks += x4 =>
            {
                if (!x4.TryGetResult<MemPointer>(7, out var t1))
                    return;

                round5[i].AddEntry<MemPointer>(9, t1 + UnityOffsets.Comp_ObjectClass);

                round5[i].Callbacks += x5 =>
                {
                    if (!x5.TryGetResult<MemPointer>(9, out var t2))
                        return;

                    round6[i].AddEntry<MemPointer>(11, t2 + 0x10);

                    round6[i].Callbacks += x6 =>
                    {
                        if (!x6.TryGetResult<MemPointer>(11, out var transformInternal))
                            return;

                        if (!((ulong)transformInternal).IsValidVirtualAddress())
                            return;

                        pending.Add(new PendingCorpse(interactiveClass, transformInternal, lootBase));
                    };
                };
            };
        }

        /// <summary>
        /// Scatter callback for static containers — resolves transform + BSG ID pointers (rounds 4-6),
        /// plus checks grids/slots for items to determine searched state. Routes airdrops to a separate pending list.
        /// </summary>
        private static void CollectContainerItem(
            ScatterReadRound round4, ScatterReadRound round5, ScatterReadRound round6,
            int i, ulong interactiveClass, ulong components, ulong goNamePtr,
            List<PendingContainer> pending, List<PendingAirdrop> pendingAirdrops, ulong lootBase)
        {
            // Round 4: transform chain start + ItemOwner + objectName string
            round4[i].AddEntry<MemPointer>(7, components + 0x08);
            round4[i].AddEntry<MemPointer>(8, interactiveClass + Offsets.LootableContainer.ItemOwner);
            if (goNamePtr.IsValidVirtualAddress())
                round4[i].AddEntry<UTF8String>(16, goNamePtr, 64);

            round4[i].Callbacks += x4 =>
            {
                if (!x4.TryGetResult<MemPointer>(7, out var t1))
                    return;

                // Check if this is an airdrop by objectName.
                // Unity's Instantiate appends "(Clone)" (and possibly numeric suffixes) to
                // names of cloned GameObjects, so a second/Nth airdrop in the same raid
                // will appear as "loot_collider(Clone)", "loot_collider (1)", etc.
                // Use a prefix match so all airdrop instances are detected, not just the first.
                if (x4.TryGetResult<UTF8String>(16, out var objectNameRaw))
                {
                    string? objectName = objectNameRaw;
                    if (!string.IsNullOrEmpty(objectName) &&
                        objectName.StartsWith("loot_collider", StringComparison.OrdinalIgnoreCase))
                    {
                        // Airdrop — only need transform chain, skip BSG ID resolution
                        round5[i].AddEntry<MemPointer>(9, t1 + UnityOffsets.Comp_ObjectClass);

                        round5[i].Callbacks += x5 =>
                        {
                            if (!x5.TryGetResult<MemPointer>(9, out var t2))
                                return;

                            round6[i].AddEntry<MemPointer>(11, t2 + 0x10);

                            round6[i].Callbacks += x6 =>
                            {
                                if (!x6.TryGetResult<MemPointer>(11, out var transformInternal))
                                    return;

                                if (!((ulong)transformInternal).IsValidVirtualAddress())
                                    return;

                                pendingAirdrops.Add(new PendingAirdrop(transformInternal));
                            };
                        };
                        return;
                    }
                }

                // Normal container path
                if (!x4.TryGetResult<MemPointer>(8, out var itemOwner))
                    return;

                // Round 5: transform chain + RootItem from ItemOwner + MainStorage pointer for grid checking
                round5[i].AddEntry<MemPointer>(9, t1 + UnityOffsets.Comp_ObjectClass);
                round5[i].AddEntry<MemPointer>(10, itemOwner + Offsets.LootableContainerItemOwner.RootItem);
                // MainStorage is Grid[] array at itemOwner + 0xC8 (from ItemController)
                round5[i].AddEntry<MemPointer>(15, itemOwner + 0xC8);

                round5[i].Callbacks += x5 =>
                {
                    if (!x5.TryGetResult<MemPointer>(9, out var t2) ||
                        !x5.TryGetResult<MemPointer>(10, out var rootItem))
                        return;

                    x5.TryGetResult<MemPointer>(15, out var mainStoragePtr);

                    // Round 6: transformInternal + Template from RootItem + check first grid for items
                    round6[i].AddEntry<MemPointer>(11, t2 + 0x10);
                    round6[i].AddEntry<MemPointer>(14, rootItem + Offsets.LootItem.Template);

                    // If MainStorage exists, read the first grid to check for items
                    if (((ulong)mainStoragePtr).IsValidVirtualAddress())
                    {
                        // MainStorage is a grid array: element 0 is at mainStoragePtr + 0x20 (after header)
                        // Read the first grid pointer from the array
                        round6[i].AddEntry<MemPointer>(20, mainStoragePtr + 0x20);
                    }

                    round6[i].Callbacks += x6 =>
                    {
                        if (!x6.TryGetResult<MemPointer>(11, out var transformInternal) ||
                            !x6.TryGetResult<MemPointer>(14, out var template))
                            return;

                        if (!((ulong)template).IsValidVirtualAddress())
                            return;

                        if (!((ulong)transformInternal).IsValidVirtualAddress())
                            return;

                        // Determine if container is searched by checking if any grid has items
                        bool searched = false;
                        if (((ulong)mainStoragePtr).IsValidVirtualAddress() && x6.TryGetResult<MemPointer>(20, out var firstGridPtr) && ((ulong)firstGridPtr).IsValidVirtualAddress())
                        {
                            // Successfully read first grid pointer; if grids exist, container has been initialized
                            // and thus has been searched (has grid structure set up)
                            searched = true;
                        }

                        pending.Add(new PendingContainer(transformInternal, (ulong)template, searched, (ulong)mainStoragePtr, lootBase, interactiveClass));
                    };
                };
            };
        }

        #endregion

        #region Phase 2 — Batched Transform + BSG ID Resolution

        /// <summary>
        /// Resolves all pending loot items in batched DMA operations:
        /// <list type="number">
        ///   <item>Batch 1: Read hierarchy + index + BSG ID strings for all items</item>
        ///   <item>Batch 2: Read verticesPtr + indicesPtr from hierarchies</item>
        ///   <item>Batch 3: Read vertices + indices arrays for all valid items</item>
        ///   <item>Compute positions locally (pure math, no DMA)</item>
        /// </list>
        /// </summary>
        private static List<LootItem> ResolveLootBatched(List<PendingLoot> pending)
        {
            if (pending.Count == 0)
                return [];

            var result = new List<LootItem>(pending.Count);

            // Arrays to hold intermediate state across batches
            var hierarchies = new ulong[pending.Count];
            var indices = new int[pending.Count];
            var bsgIds = new string?[pending.Count];
            var shortNames = new string?[pending.Count];
            var verticesPtrs = new ulong[pending.Count];
            var indicesPtrs = new ulong[pending.Count];

            // ── Batch 1: hierarchy + index + BSG ID strings ─────────────────────
            using (var s1 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    if (!((ulong)ti).IsValidVirtualAddress())
                        continue;
                    s1.PrepareReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset);
                    s1.PrepareReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset);
                    // Unity string: length at +0x10 (int), chars at +0x14 (UTF-16).
                    // BSG IDs are 24 hex chars (48 UTF-16 bytes); cap generously at 64 to
                    // halve the per-item string bandwidth without truncating any real ID.
                    s1.PrepareRead(pending[i].BsgIdStringAddr + 0x14, BsgIdReadBytes);

                    // Quest items rarely exist in the market database — read ShortName
                    // so we can synthesize a display name for them.
                    if (pending[i].IsQuestItem && pending[i].ShortNameUnityString.IsValidVirtualAddress())
                        s1.PrepareRead(pending[i].ShortNameUnityString + 0x14, QuestShortNameReadBytes);
                }
                s1.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    s1.ReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset, out hierarchies[i]);
                    s1.ReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset, out indices[i]);
                    bsgIds[i] = s1.ReadString(pending[i].BsgIdStringAddr + 0x14, BsgIdReadBytes, Encoding.Unicode);
                    if (pending[i].IsQuestItem && pending[i].ShortNameUnityString.IsValidVirtualAddress())
                        shortNames[i] = s1.ReadString(pending[i].ShortNameUnityString + 0x14, QuestShortNameReadBytes, Encoding.Unicode);
                }
            }

            // ── Batch 2: verticesPtr + indicesPtr from hierarchies ──────────────
            using (var s2 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000)
                        continue;
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset);
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset);
                }
                s2.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000)
                        continue;
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset, out verticesPtrs[i]);
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset, out indicesPtrs[i]);
                }
            }

            // ── Batch 3: vertices + indices arrays ──────────────────────────────
            using (var s3 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress())
                        continue;
                    int vertCount = indices[i] + 1;
                    s3.PrepareReadArray<TrsX>(verticesPtrs[i], vertCount);
                    s3.PrepareReadArray<int>(indicesPtrs[i], vertCount);
                }
                s3.Execute();

                // Compute positions and create LootItems
                for (int i = 0; i < pending.Count; i++)
                {
                    try
                    {
                        // Validate BSG ID
                        var rawId = bsgIds[i];
                        if (string.IsNullOrEmpty(rawId))
                            continue;

                        // Trim null terminator if present
                        int nt = rawId.IndexOf('\0');
                        var bsgId = nt >= 0 ? rawId[..nt] : rawId;
                        if (bsgId.Length == 0)
                            continue;

                        if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress())
                            continue;

                        int vertCount = indices[i] + 1;
                        var rentedV = ArrayPool<TrsX>.Shared.Rent(vertCount);
                        var rentedI = ArrayPool<int>.Shared.Rent(vertCount);
                        try
                        {
                            var vertices = rentedV.AsSpan(0, vertCount);
                            var parentIndices = rentedI.AsSpan(0, vertCount);
                            if (!s3.ReadSpan<TrsX>(verticesPtrs[i], vertices) ||
                                !s3.ReadSpan<int>(indicesPtrs[i], parentIndices))
                                continue;

                            var pos = ComputeTransformPosition(vertices, parentIndices, indices[i]);
                            if (pos == Vector3.Zero)
                                continue;

                            bool isQuestItem = pending[i].IsQuestItem;
                            bool isUnknownItem = false;
                            TarkovMarketItem? marketItem;
                            if (!EftDataManager.AllItems.TryGetValue(bsgId, out marketItem))
                            {
                                if (!isQuestItem)
                                {
                                    // Surface unknown BSG IDs (e.g. items added by a patch but not
                                    // yet in tarkov.dev) so users can report them. Rate-limited per
                                    // BSG ID to avoid spam when the same unknown item is on screen.
                                    Log.WriteRateLimited(AppLogLevel.Info, $"loot_unknown_{bsgId}", TimeSpan.FromSeconds(60),
                                        $"[LootManager] Unknown BSG ID '{bsgId}' not in devdata | Pos: ({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}) | LootBase: 0x{pending[i].LootBase:X16}");

                                    // Only render unknowns when the user has opted in — they have no
                                    // category, price, or display name, so they'd otherwise be radar noise.
                                    if (!SilkProgram.Config.LootShowUnknownItems)
                                        continue;

                                    isUnknownItem = true;
                                    string shortPrefix = bsgId.Length > 8 ? bsgId[..8] : bsgId;
                                    marketItem = new TarkovMarketItem
                                    {
                                        BsgId = bsgId,
                                        Name = $"Unknown {bsgId}",
                                        ShortName = $"?{shortPrefix}",
                                    };
                                }
                                else
                                {
                                    // Quest items are typically absent from the market DB. Synthesize a
                                    // minimal TarkovMarketItem from the short name we already scattered.
                                    var rawShort = shortNames[i];
                                    string shortName = "Quest Item";
                                    if (!string.IsNullOrEmpty(rawShort))
                                    {
                                        int snt = rawShort.IndexOf('\0');
                                        var s = snt >= 0 ? rawShort[..snt] : rawShort;
                                        if (s.Length > 0) shortName = s;
                                    }
                                    marketItem = new TarkovMarketItem
                                    {
                                        BsgId = bsgId,
                                        Name = shortName,
                                        ShortName = $"Q_{shortName}",
                                    };
                                }
                            }

                            var item = new LootItem(marketItem, pos)
                            {
                                IsQuestItem = isQuestItem,
                                IsUnknownItem = isUnknownItem,
                                LootBase = pending[i].LootBase,
                            };
                            item.RefreshImportance();
                            result.Add(item);
                        }
                        finally
                        {
                            ArrayPool<TrsX>.Shared.Return(rentedV);
                            ArrayPool<int>.Shared.Return(rentedI);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteRateLimited(AppLogLevel.Debug, "loot_resolve_fail", TimeSpan.FromSeconds(10),
                            $"[LootManager] Loot resolve failed: {ex.Message}");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves all pending corpse items in batched DMA operations (same 3-batch pattern as loot).
        /// </summary>
        private static List<LootCorpse> ResolveCorpsesBatched(List<PendingCorpse> pending)
        {
            if (pending.Count == 0)
                return [];

            var result = new List<LootCorpse>(pending.Count);
            var hierarchies = new ulong[pending.Count];
            var indices = new int[pending.Count];
            var verticesPtrs = new ulong[pending.Count];
            var indicesPtrs = new ulong[pending.Count];

            // ── Batch 1: hierarchy + index ──────────────────────────────────────
            using (var s1 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    if (!((ulong)ti).IsValidVirtualAddress())
                        continue;
                    s1.PrepareReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset);
                    s1.PrepareReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset);
                }
                s1.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    s1.ReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset, out hierarchies[i]);
                    s1.ReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset, out indices[i]);
                }
            }

            // ── Batch 2: verticesPtr + indicesPtr ───────────────────────────────
            using (var s2 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000)
                        continue;
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset);
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset);
                }
                s2.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000)
                        continue;
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset, out verticesPtrs[i]);
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset, out indicesPtrs[i]);
                }
            }

            // ── Batch 3: vertices + indices arrays ──────────────────────────────
            using (var s3 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress())
                        continue;
                    int vertCount = indices[i] + 1;
                    s3.PrepareReadArray<TrsX>(verticesPtrs[i], vertCount);
                    s3.PrepareReadArray<int>(indicesPtrs[i], vertCount);
                }
                s3.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    try
                    {
                        if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress())
                            continue;

                        int vertCount = indices[i] + 1;
                        var rentedV = ArrayPool<TrsX>.Shared.Rent(vertCount);
                        var rentedI = ArrayPool<int>.Shared.Rent(vertCount);
                        try
                        {
                            var vertices = rentedV.AsSpan(0, vertCount);
                            var parentIndices = rentedI.AsSpan(0, vertCount);
                            if (!s3.ReadSpan<TrsX>(verticesPtrs[i], vertices) ||
                                !s3.ReadSpan<int>(indicesPtrs[i], parentIndices))
                                continue;

                            var pos = ComputeTransformPosition(vertices, parentIndices, indices[i]);
                            if (pos == Vector3.Zero)
                                continue;

                            result.Add(new LootCorpse(pending[i].InteractiveClass, pos));
                        }
                        finally
                        {
                            ArrayPool<TrsX>.Shared.Return(rentedV);
                            ArrayPool<int>.Shared.Return(rentedI);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteRateLimited(AppLogLevel.Debug, "corpse_resolve_fail", TimeSpan.FromSeconds(10),
                            $"[LootManager] Corpse resolve failed: {ex.Message}");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Pure math — computes world position from pre-read vertices + indices.
        /// No DMA reads. Shared by loot and corpse resolution.
        /// </summary>
        private static Vector3 ComputeTransformPosition(ReadOnlySpan<TrsX> vertices, ReadOnlySpan<int> parentIndices, int index)
        {
            var pos = TrsX.ComputeWorldPosition(vertices, parentIndices, index);

            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
                return Vector3.Zero;

            if (pos.LengthSquared() < 16f)
                return Vector3.Zero;

            return pos;
        }

        /// <summary>
        /// Resolves all pending airdrop items in batched DMA operations.
        /// Uses the standard 3-batch transform pattern (same as corpses — position only, no BSG ID).
        /// </summary>
        private static List<LootAirdrop> ResolveAirdropsBatched(List<PendingAirdrop> pending)
        {
            if (pending.Count == 0)
                return [];

            var result = new List<LootAirdrop>(pending.Count);
            var hierarchies = new ulong[pending.Count];
            var indices = new int[pending.Count];
            var verticesPtrs = new ulong[pending.Count];
            var indicesPtrs = new ulong[pending.Count];

            try
            {
                using var s1 = Memory.GetScatter(VmmFlags.NOCACHE);
                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    if (!((ulong)ti).IsValidVirtualAddress()) continue;
                    s1.PrepareReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset);
                    s1.PrepareReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset);
                }
                s1.Execute();
                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    s1.ReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset, out hierarchies[i]);
                    s1.ReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset, out indices[i]);
                }
            }
            catch { return result; }

            try
            {
                using var s2 = Memory.GetScatter(VmmFlags.NOCACHE);
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000) continue;
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset);
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset);
                }
                s2.Execute();
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000) continue;
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset, out verticesPtrs[i]);
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset, out indicesPtrs[i]);
                }
            }
            catch { return result; }

            try
            {
                using var s3 = Memory.GetScatter(VmmFlags.NOCACHE);
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress()) continue;
                    int vertCount = indices[i] + 1;
                    s3.PrepareReadArray<TrsX>(verticesPtrs[i], vertCount);
                    s3.PrepareReadArray<int>(indicesPtrs[i], vertCount);
                }
                s3.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    try
                    {
                        if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress()) continue;
                        int vertCount = indices[i] + 1;
                        var rentedV = ArrayPool<TrsX>.Shared.Rent(vertCount);
                        var rentedI = ArrayPool<int>.Shared.Rent(vertCount);
                        try
                        {
                            var vertices = rentedV.AsSpan(0, vertCount);
                            var parentIndices = rentedI.AsSpan(0, vertCount);
                            if (!s3.ReadSpan<TrsX>(verticesPtrs[i], vertices) ||
                                !s3.ReadSpan<int>(indicesPtrs[i], parentIndices))
                                continue;

                            var pos = ComputeTransformPosition(vertices, parentIndices, indices[i]);
                            if (pos == Vector3.Zero) continue;
                            result.Add(new LootAirdrop(pos));
                        }
                        finally
                        {
                            ArrayPool<TrsX>.Shared.Return(rentedV);
                            ArrayPool<int>.Shared.Return(rentedI);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteRateLimited(AppLogLevel.Debug, "airdrop_resolve_fail", TimeSpan.FromSeconds(10),
                            $"[LootManager] Airdrop resolve failed: {ex.Message}");
                    }
                }
            }
            catch { /* s3.Execute() failed — return whatever we have */ }

            return result;
        }

        /// <summary>
        /// Resolves all pending container items in batched DMA operations.
        /// Uses 4 batches: MongoID resolution, then the standard 3-batch transform pattern.
        /// Returns container data plus MainStoragePtr for grid item extraction.
        /// </summary>
        private static List<(LootContainer Item, ulong LootBase, ulong InteractiveClass, ulong MainStoragePtr, Vector3 Position)> ResolveContainersBatched(List<PendingContainer> pending)
        {
            if (pending.Count == 0)
                return [];

            var result = new List<(LootContainer, ulong, ulong, ulong, Vector3)>(pending.Count);

            // Arrays to hold intermediate state across batches
            var hierarchies = new ulong[pending.Count];
            var indices = new int[pending.Count];
            var bsgIdStringAddrs = new ulong[pending.Count];
            var bsgIds = new string?[pending.Count];
            var verticesPtrs = new ulong[pending.Count];
            var indicesPtrs = new ulong[pending.Count];

            // ── Batch 0: Read MongoID from template to get StringID pointer ────
            using (var s0 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    var tmpl = pending[i].TemplateAddr;
                    if (tmpl.IsValidVirtualAddress())
                        s0.PrepareReadValue<Types.MongoID>(tmpl + Offsets.ItemTemplate._id);
                }
                s0.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    var tmpl = pending[i].TemplateAddr;
                    if (tmpl.IsValidVirtualAddress() &&
                        s0.ReadValue<Types.MongoID>(tmpl + Offsets.ItemTemplate._id, out var mongoId) &&
                        mongoId.StringID.IsValidVirtualAddress())
                    {
                        bsgIdStringAddrs[i] = mongoId.StringID;
                    }
                }
            }

            // ── Batch 1: hierarchy + index + BSG ID strings ─────────────────────
            using (var s1 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    if (!((ulong)ti).IsValidVirtualAddress())
                        continue;
                    s1.PrepareReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset);
                    s1.PrepareReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset);
                    if (bsgIdStringAddrs[i].IsValidVirtualAddress())
                        s1.PrepareRead(bsgIdStringAddrs[i] + 0x14, 128);
                }
                s1.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    var ti = pending[i].TransformInternal;
                    s1.ReadValue<ulong>(ti + UnityOffsets.TransformAccess.HierarchyOffset, out hierarchies[i]);
                    s1.ReadValue<int>(ti + UnityOffsets.TransformAccess.IndexOffset, out indices[i]);
                    if (bsgIdStringAddrs[i].IsValidVirtualAddress())
                        bsgIds[i] = s1.ReadString(bsgIdStringAddrs[i] + 0x14, 128, Encoding.Unicode);
                }
            }

            // ── Batch 2: verticesPtr + indicesPtr from hierarchies ──────────────
            using (var s2 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000)
                        continue;
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset);
                    s2.PrepareReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset);
                }
                s2.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    if (!hierarchies[i].IsValidVirtualAddress() || indices[i] < 0 || indices[i] > 150_000)
                        continue;
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.VerticesOffset, out verticesPtrs[i]);
                    s2.ReadValue<ulong>(hierarchies[i] + UnityOffsets.TransformHierarchy.IndicesOffset, out indicesPtrs[i]);
                }
            }

            // ── Batch 3: vertices + indices arrays ──────────────────────────────
            using (var s3 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress())
                        continue;
                    int vertCount = indices[i] + 1;
                    s3.PrepareReadArray<TrsX>(verticesPtrs[i], vertCount);
                    s3.PrepareReadArray<int>(indicesPtrs[i], vertCount);
                }
                s3.Execute();

                for (int i = 0; i < pending.Count; i++)
                {
                    try
                    {
                        var rawId = bsgIds[i];
                        if (string.IsNullOrEmpty(rawId))
                            continue;

                        int nt = rawId.IndexOf('\0');
                        var bsgId = nt >= 0 ? rawId[..nt] : rawId;
                        if (bsgId.Length == 0)
                            continue;

                        if (!EftDataManager.AllContainers.TryGetValue(bsgId, out var containerItem))
                            continue;

                        if (!verticesPtrs[i].IsValidVirtualAddress() || !indicesPtrs[i].IsValidVirtualAddress())
                            continue;

                        int vertCount = indices[i] + 1;
                        var rentedV = ArrayPool<TrsX>.Shared.Rent(vertCount);
                        var rentedI = ArrayPool<int>.Shared.Rent(vertCount);
                        try
                        {
                            var vertices = rentedV.AsSpan(0, vertCount);
                            var parentIndices = rentedI.AsSpan(0, vertCount);
                            if (!s3.ReadSpan<TrsX>(verticesPtrs[i], vertices) ||
                                !s3.ReadSpan<int>(indicesPtrs[i], parentIndices))
                                continue;

                            var pos = ComputeTransformPosition(vertices, parentIndices, indices[i]);
                            if (pos == Vector3.Zero)
                                continue;

                            result.Add((new LootContainer(bsgId, containerItem.ShortName, pos, pending[i].Searched), pending[i].LootBase, pending[i].InteractiveClass, pending[i].MainStoragePtr, pos));
                        }
                        finally
                        {
                            ArrayPool<TrsX>.Shared.Return(rentedV);
                            ArrayPool<int>.Shared.Return(rentedI);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteRateLimited(AppLogLevel.Debug, "container_resolve_fail", TimeSpan.FromSeconds(10),
                            $"[LootManager] Container resolve failed: {ex.Message}");
                    }
                }
            }

            return result;
        }

        #endregion

        #region Corpse Dogtag Reading

        /// <summary>
        /// Iterates only the known corpses (from the unified scatter pass) and walks their
        /// equipment slots to find dogtag items and seed <see cref="DogtagCache"/>.
        /// This is O(corpses) not O(lootList) — typically 0-5 iterations, not hundreds.
        /// </summary>
        private void ReadCorpseDogtags()
        {
            var corpses = _corpses;
            if (corpses.Count == 0)
                return;

            foreach (var corpse in corpses)
            {
                var interactiveClass = corpse.InteractiveClass;

                // Skip only if both victim seeding AND killfeed push are done.
                // We intentionally do NOT mark as processed up-front: killer
                // dogtag fields (KillerName/KillerProfileId/WeaponName) often
                // populate a tick or two after victim fields, so we need to
                // retry this corpse until we either push a killfeed event or
                // exhaust a small retry budget.
                bool victimDone = _processedCorpses.Contains(interactiveClass);
                bool killfeedDone = _killfeedPushed.Contains(interactiveClass);
                if (victimDone && killfeedDone)
                    continue;

                // Cap killfeed retries (e.g. 10 passes ≈ a few seconds) so we
                // don't re-read corpses whose killer field never populates
                // (suicides, environment kills, etc).
                if (victimDone && !killfeedDone)
                {
                    _killfeedAttempts.TryGetValue(interactiveClass, out var attempts);
                    if (attempts >= 10)
                    {
                        _killfeedPushed.Add(interactiveClass);
                        continue;
                    }
                    _killfeedAttempts[interactiveClass] = attempts + 1;
                }

                try
                {
                    // Corpse: InteractiveLootItem.Item → base item → LootItemMod.Slots → array of slots
                    if (!Memory.TryReadPtr(interactiveClass + Offsets.InteractiveLootItem.Item, out var itemBase)
                        || !itemBase.IsValidVirtualAddress())
                        continue;

                    if (!Memory.TryReadPtr(itemBase + Offsets.LootItemMod.Slots, out var slotsArr)
                        || !slotsArr.IsValidVirtualAddress())
                        continue;

                    using var slotPtrs = MemArray<ulong>.Get(slotsArr, false);
                    if (slotPtrs.Count < 1 || slotPtrs.Count > 64)
                        continue;

                    for (int si = 0; si < slotPtrs.Count; si++)
                    {
                        var slotPtr = slotPtrs[si];
                        if (!slotPtr.IsValidVirtualAddress())
                            continue;

                        if (!Memory.TryReadPtr(slotPtr + Offsets.Slot.ContainedItem, out var slotItem)
                            || !slotItem.IsValidVirtualAddress())
                            continue;

                        // Check if this is a BarterOther (dogtag)
                        var slotClassName = Il2CppClass.ReadName(slotItem);
                        if (slotClassName is null || !slotClassName.Equals("BarterOther", StringComparison.Ordinal))
                            continue;

                        // Read DogtagComponent
                        if (!Memory.TryReadPtr(slotItem + Offsets.BarterOtherOffsets.Dogtag, out var dogtag)
                            || !dogtag.IsValidVirtualAddress())
                            continue;

                        // Read victim identity fields
                        var profileId = ReadDogtagString(dogtag + Offsets.DogtagComponent.ProfileId);
                        if (string.IsNullOrWhiteSpace(profileId))
                            continue;

                        var nickname = ReadDogtagString(dogtag + Offsets.DogtagComponent.Nickname);
                        var accountId = ReadDogtagString(dogtag + Offsets.DogtagComponent.AccountId);
                        Memory.TryReadValue<int>(dogtag + Offsets.DogtagComponent.Level, out var level);

                        DogtagCache.Seed(profileId, nickname, accountId, level);

                        // Victim seeded — mark so next pass only re-reads killer.
                        _processedCorpses.Add(interactiveClass);

                        // Store nickname for corpse name resolution
                        if (!string.IsNullOrWhiteSpace(nickname))
                            _corpseNicknames[interactiveClass] = nickname;

                        // Also seed killer identity if available
                        var killerProfileId = ReadDogtagString(dogtag + Offsets.DogtagComponent.KillerProfileId);
                        var killerAccountId = ReadDogtagString(dogtag + Offsets.DogtagComponent.KillerAccountId);
                        var killerName = ReadDogtagString(dogtag + Offsets.DogtagComponent.KillerName);
                        var killerWeapon = ReadDogtagString(dogtag + Offsets.DogtagComponent.WeaponName);

                        // Dogtag WeaponName is often a BSG template id — resolve to a readable name
                        killerWeapon = ResolveWeaponName(killerWeapon);

                        if (!string.IsNullOrWhiteSpace(killerProfileId))
                            DogtagCache.Seed(killerProfileId, killerName, killerAccountId, 0);

                        // Push killfeed event — resolve killer's PlayerType from live player list
                        if (!string.IsNullOrWhiteSpace(killerName) && !string.IsNullOrWhiteSpace(nickname))
                        {
                            var killerSide = PlayerType.Default;
                            var livePlayers = Memory.Players;
                            if (livePlayers is not null)
                            {
                                foreach (var lp in livePlayers)
                                {
                                    // Match by profileId first (most reliable), fall back to name
                                    bool byId = !string.IsNullOrWhiteSpace(killerProfileId)
                                        && !string.IsNullOrWhiteSpace(lp.ProfileId)
                                        && string.Equals(lp.ProfileId, killerProfileId, StringComparison.OrdinalIgnoreCase);
                                    bool byName = !byId
                                        && string.Equals(lp.Name, killerName, StringComparison.OrdinalIgnoreCase);
                                    if (byId || byName)
                                    {
                                        killerSide = lp.Type;
                                        break;
                                    }
                                }
                            }
                            KillfeedManager.Push(killerName, nickname, killerWeapon ?? "", level, killerSide);
                            _killfeedPushed.Add(interactiveClass);
                        }

                        break; // Only one dogtag per corpse
                    }
                }
                catch
                {
                    // Non-fatal — skip this corpse
                }
            }
        }

        /// <summary>
        /// Reads a Unity string from a dogtag component field (ptr → Unity string).
        /// </summary>
        private static string? ReadDogtagString(ulong fieldAddr)
        {
            if (!Memory.TryReadPtr(fieldAddr, out var strPtr) || !strPtr.IsValidVirtualAddress())
                return null;
            return Memory.TryReadUnityString(strPtr, out var result) ? result : null;
        }

        /// <summary>
        /// The dogtag WeaponName field stores a BSG template id (e.g. "5447a9cd4bdc2dbd208b4567").
        /// Resolve it to a readable short/long name via the item database; fall back to the raw value.
        /// </summary>
        private static string ResolveWeaponName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            // The dogtag weapon field may be:
            //   • a BSG template id (24 hex chars), or
            //   • an unresolved localization token like "5926bb2186f7744b1c6c6e60 ShortName",
            //   • or an already-localized name.
            // Scan for a 24-char hex run and resolve it via the item database.
            if (TryExtractBsgId(raw, out var bsgId)
                && Misc.Data.EftDataManager.AllItems.TryGetValue(bsgId, out var item))
            {
                if (!string.IsNullOrWhiteSpace(item.ShortName))
                    return item.ShortName;
                if (!string.IsNullOrWhiteSpace(item.Name))
                    return item.Name;
            }
            return raw;

            static bool TryExtractBsgId(string s, out string id)
            {
                for (int i = 0; i + 24 <= s.Length; i++)
                {
                    int j = 0;
                    while (j < 24)
                    {
                        char c = s[i + j];
                        bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                        if (!ok) break;
                        j++;
                    }
                    if (j == 24)
                    {
                        // Must be a standalone token (not part of a longer hex run)
                        bool leftOk = i == 0 || !IsHex(s[i - 1]);
                        bool rightOk = i + 24 == s.Length || !IsHex(s[i + 24]);
                        if (leftOk && rightOk)
                        {
                            id = s.Substring(i, 24);
                            return true;
                        }
                    }
                }
                id = string.Empty;
                return false;
            }

            static bool IsHex(char c) =>
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        #endregion

        #region Corpse Equipment Reading

        /// <summary>
        /// Reads the equipment slots of a corpse and populates its <see cref="LootCorpse.Equipment"/>
        /// and <see cref="LootCorpse.TotalValue"/>. Re-read cadence is gated by
        /// <see cref="_corpseGearNextReadAt"/> in the corpse refresh loop.
        /// </summary>
        private void ReadCorpseEquipment(LootCorpse corpse)
        {
            try
            {
                // InteractiveClass → Item → Slots array
                if (!Memory.TryReadPtr(corpse.InteractiveClass + Offsets.InteractiveLootItem.Item, out var itemBase)
                    || !itemBase.IsValidVirtualAddress())
                    return;

                if (!Memory.TryReadPtr(itemBase + Offsets.LootItemMod.Slots, out var slotsArr)
                    || !slotsArr.IsValidVirtualAddress())
                    return;

                using var slotPtrs = MemArray<ulong>.Get(slotsArr, false);
                if (slotPtrs.Count < 1 || slotPtrs.Count > 64)
                    return;

                var gear = new Dictionary<string, CorpseGearItem>(slotPtrs.Count, StringComparer.OrdinalIgnoreCase);
                int totalValue = 0;

                for (int si = 0; si < slotPtrs.Count; si++)
                {
                    var slotPtr = slotPtrs[si];
                    if (!slotPtr.IsValidVirtualAddress())
                        continue;

                    try
                    {
                        // Read slot name
                        if (!Memory.TryReadPtr(slotPtr + Offsets.Slot.ID, out var namePtr)
                            || !namePtr.IsValidVirtualAddress())
                            continue;

                        if (!Memory.TryReadUnityString(namePtr, out var slotName) || slotName is null)
                            continue;

                        if (_skipSlots.Contains(slotName))
                            continue;

                        // Read contained item
                        if (!Memory.TryReadPtr(slotPtr + Offsets.Slot.ContainedItem, out var slotItem)
                            || !slotItem.IsValidVirtualAddress())
                            continue;

                        // Resolve BSG ID via template → MongoID
                        if (!TryReadBsgId(slotItem, out var bsgId))
                            continue;

                        if (!EftDataManager.AllItems.TryGetValue(bsgId, out var marketItem))
                            continue;

                        gear[slotName] = new CorpseGearItem
                        {
                            ShortName = marketItem.ShortName,
                            Name = marketItem.Name,
                            Price = marketItem.BestPrice
                        };
                        totalValue += marketItem.BestPrice;
                    }
                    catch
                    {
                        // Skip individual slot failures
                    }
                }

                corpse.Equipment = gear.Count > 0
                    ? gear.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
                    : FrozenDictionary<string, CorpseGearItem>.Empty;
                corpse.TotalValue = totalValue;
                corpse.GearReady = true;
            }
            catch
            {
                // Non-fatal — will retry next refresh
            }
        }

        /// <summary>
        /// Reads a BSG ID string from an item via its template → MongoID chain.
        /// </summary>
        private static bool TryReadBsgId(ulong itemAddr, out string bsgId)
        {
            bsgId = string.Empty;

            if (!Memory.TryReadPtr(itemAddr + Offsets.LootItem.Template, out var template)
                || !template.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadValue<Types.MongoID>(template + Offsets.ItemTemplate._id, out var mongoId))
                return false;

            if (!mongoId.StringID.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadUnityString(mongoId.StringID, out var id) || string.IsNullOrEmpty(id))
                return false;

            bsgId = id;
            return true;
        }

        #endregion
    }
}
