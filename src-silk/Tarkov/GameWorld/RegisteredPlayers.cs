// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Unity;
using VmmSharpEx;
using VmmSharpEx.Options;
using VmmSharpEx.Scatter;

using static eft_dma_radar.Silk.Tarkov.Unity.UnityOffsets;

namespace eft_dma_radar.Silk.Tarkov.GameWorld
{
    /// <summary>
    /// Manages registered players in a raid — reads, caches, and updates player data.
    /// Supports local player, observed players (PMC, PScav, AI), and voice-based AI identification.
    /// <para>
    /// Uses a two-tier refresh model:
    /// <list type="bullet">
    ///   <item>Registration refresh (slower): reads player list, discovers/removes players, updates lifecycle.</item>
    ///   <item>Realtime refresh (fast): scatter-batched position + rotation for all active players — single DMA round-trip.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed partial class RegisteredPlayers : IReadOnlyCollection<Player.Player>
    {
        #region Constants

        // Maximum parent-chain iterations (safety guard)
        private const int MaxHierarchyIterations = 4000;

        // Maximum valid player count from the registered players list
        private const int MaxPlayerCount = 256;

        // Spawn-group proximity threshold (squared distance, meters²)
        private const float SpawnGroupDistanceSqr = 25f; // 5m radius

        // Number of consecutive realtime failures before entering error state
        private const int ErrorThreshold = 3;

        // Number of consecutive successes required to clear error state (hysteresis prevents flip-flop)
        private const int RecoveryThreshold = 2;

        // Number of consecutive position failures (while TransformReady) that trigger automatic
        // transform invalidation — covers cases where the pointer chain is valid but data isn't populated yet.
        private const int ReinitThreshold = 5;

        // Lower threshold for players that have never had a valid position (just spawned) —
        // their game data is likely still initializing, so re-init faster.
        private const int ReinitThresholdNew = 2;

        // Maximum transform/rotation init retries before giving up (exponential backoff)
        private const int MaxInitRetries = 15;

        // Gear refresh interval per player (seconds)
        private const int GearRefreshIntervalSec = 10;

        // Hands refresh interval per player (seconds) — faster than gear since items swap often
        private const int HandsRefreshIntervalSec = 3;

        // Health status refresh interval per player (seconds) — moderate rate, just a single int read
        private const int HealthRefreshIntervalSec = 3;

        // Local player energy/hydration refresh interval (seconds)
        private const int EnergyHydrationRefreshIntervalSec = 3;

        // ETagStatus flag bits used for health classification
        private const int ETagDying = 8192;
        private const int ETagBadlyInjured = 4096;
        private const int ETagInjured = 2048;

        // Maximum gear + hands refreshes per registration tick — prevents thundering-herd spikes
        // when many players are discovered at once or periodic timers align.
        // Each heavy refresh (GearManager / HandsManager) can span up to ~4 scatter rounds.
        // With registration ticks at ~100ms, 1/tick = 10/sec which easily covers per-player
        // intervals (gear=10s, hands=3s) for an entire lobby — players catch up across ticks.
        private const int MaxRefreshesPerTick = 1;

        // Hard wall-clock budget for the per-player update loop. Once exceeded we stop
        // issuing new gear/hands/health refreshes this tick and catch up on the next one.
        // Keeps the registration worker comfortably under 8ms total even with spikey DMA latency,
        // leaving headroom for BatchInitTransformsAndRotations + BatchUpdateHealthStatuses.
        private const double PlayerUpdateBudgetMs = 4.0;

        // Precomputed Stopwatch tick count representing PlayerUpdateBudgetMs. Avoids the
        // per-tick division by Stopwatch.Frequency in the hot update loop.
        private static readonly long _playerUpdateBudgetTicks =
            (long)(PlayerUpdateBudgetMs * (Stopwatch.Frequency / 1000.0));

        // Random jitter range (seconds) added to gear refresh intervals to prevent timer re-alignment.
        private const double GearRefreshJitterSec = 2.0;

        // ---- Distance-aware refresh throttling --------------------------------------------------
        // Once a player has been read at least once (GearReady/HandsReady set), the next refresh
        // interval is multiplied by a distance-based factor. The very first read always happens at
        // full cadence so we never miss identifying a far-away target.
        // Sniping safety: while the local player is ADS (CameraManager.IsADS), no scaling is
        // applied — long-range targets keep their normal cadence so a sniper sees fresh data.
        // All knobs (enable, distance tiers, multipliers) are user-configurable via SilkConfig.

        #endregion

        #region Fields

        private readonly ulong _gameWorldBase;
        private readonly string _mapId;
        private readonly ConcurrentDictionary<ulong, PlayerEntry> _players = new();
        private readonly HashSet<ulong> _seenSet = new(MaxPlayerCount);

        // Reusable list for active entries — avoids per-tick allocation on the 8ms realtime path
        private readonly List<PlayerEntry> _activeEntries = new(MaxPlayerCount);

        // Reusable list for ValidateTransforms — avoids LINQ/ToArray allocation
        private readonly List<PlayerEntry> _validateEntries = new(MaxPlayerCount);

        // Backoff for repeated invalid player counts (e.g., after raid ends)
        private int _invalidCountStreak;

        // Monotonic counter used to stagger initial gear/hands refresh times for newly
        // discovered players. Each new player gets a slot offset so refreshes spread
        // across multiple registration ticks instead of thundering-herding.
        private int _staggerIndex;

        // Per-thread RNG for jittering refresh intervals (avoids contention).
        [ThreadStatic] private static Random? t_rng;
        private static Random Rng => t_rng ??= new Random();

        // Spawn-group tracking (position-proximity-based)
        private readonly List<SpawnGroupEntry> _spawnGroups = [];
        private int _nextSpawnGroupId = 1;

        // Backoff for failed CreatePlayerEntry calls — prevents hammering uninitialized objects.
        // Key: player address, Value: (failure count, earliest UTC time to retry).
        // Entries are pruned when the address is either successfully created or removed from the list.
        private readonly Dictionary<ulong, (int Failures, DateTime NextRetry)> _failedEntryBackoff = new();

        // Reusable collections for backoff pruning — avoids per-tick allocation
        private readonly HashSet<ulong> _failedBackoffPrune = new(MaxPlayerCount);
        private readonly List<ulong> _failedBackoffRemove = [];

        #endregion

        #region Properties

        /// <summary>The local player instance, or <c>null</c> if not yet discovered.</summary>
        public Player.Player? LocalPlayer { get; private set; }

        /// <summary>Raw memory address of the local player object (used for raid-ended detection).</summary>
        public ulong LocalPlayerAddr { get; private set; }

        /// <summary>Number of currently tracked players.</summary>
        public int Count => _players.Count;

        /// <summary>
        /// The most recent raw count from the game's RegisteredPlayers list.
        /// May be higher than <see cref="Count"/> when some entries are still initializing
        /// or have transiently invalid pointers. Useful for detecting missing players.
        /// </summary>
        public int ListCount => _listCount;
        private volatile int _listCount;

        /// <summary>
        /// Set when the local player disappears from the game's RegisteredPlayers list.
        /// This is an immediate, high-confidence signal that the raid is ending (death, extraction,
        /// or disconnect). The registration worker uses this to skip expensive secondary work and
        /// trigger an early <c>IsRaidActive()</c> check.
        /// </summary>
        public bool LocalPlayerLost { get; private set; }

        #endregion

        #region Inner Types

        /// <summary>
        /// Pairs a <see cref="Player.Player"/> with its cached transform data so we can avoid
        /// re-walking the pointer chain on every tick.
        /// </summary>
        internal sealed class PlayerEntry(ulong playerBase, Player.Player player, bool isObserved)
        {
            public readonly ulong Base = playerBase;
            public readonly Player.Player Player = player;
            public readonly bool IsObserved = isObserved;

            // Cached transform state (populated once, re-validated periodically)
            // Written by registration thread, read by realtime thread — volatile ensures cross-core visibility.
            // The volatile write on TransformReady/RotationReady acts as a release fence, guaranteeing
            // that all preceding data writes (TransformInternal, VerticesAddr, etc.) are visible
            // to the realtime thread that reads the volatile flag as an acquire fence.
            public ulong TransformInternal;
            public ulong VerticesAddr;
            public int TransformIndex;
            public volatile bool TransformReady;

            // Indices never change for the life of the transform — cache once
            public int[]? CachedIndices;

            // Cached rotation address
            public ulong RotationAddr;
            public volatile bool RotationReady;

            // Cached observed-only step2 ObservedMovementController address.
            // Populated alongside RotationAddr in BatchInitRotations for IsObserved entries.
            // Used to read pose/velocity/body-yaw fields in the same realtime scatter round.
            public ulong ObservedMovementCtrlAddr;

            // Error tracking for realtime loop — debounce transient failures
            public int ConsecutiveErrors;
            public int RecoveryCount;
            public bool HasError;

            // Set after the first successful realtime position read — distinguishes
            // "init-only position" from "confirmed by realtime scatter loop".
            // Used to select a lower auto-reinit threshold for newly spawned players
            // whose game data may still be initializing.
            public bool RealtimeEstablished;

            // Transform init retry tracking — exponential backoff for persistent failures
            public int TransformInitFailures;
            public DateTime NextTransformRetry;
            public int RotationInitFailures;
            public DateTime NextRotationRetry;

            // Gear refresh tracking — rate-limited to avoid excessive DMA reads
            public DateTime NextGearRefresh;

            // Hands refresh tracking — more frequent than gear
            public DateTime NextHandsRefresh;

            // Look transform (local player only) — used for aimview eye position.
            // Since both main and look chains use _playerLookRaycastTransform, the
            // TransformInternal/Vertices/Indices are identical — no separate fields needed.
            // This flag simply tracks whether the look transform has been synced.
            public volatile bool LookTransformReady;

            // Cached PWA _isAiming address (local player only) — set once during discovery,
            // batched into the realtime scatter so the ADS state is read without extra DMA calls.
            public ulong IsAimingAddr;

            // Observed health controller address — resolved once during discovery for observed players.
            // Used by the registration worker to periodically read HealthStatus.
            public ulong ObservedHealthControllerAddr;

            // Set to true each tick that this entry's health timer fires; consumed by BatchUpdateHealthStatuses.
            public bool HealthDueTick;

            // Health refresh tracking — rate-limited like gear/hands
            public DateTime NextHealthRefresh;

            // Per-player skeleton
            // Written by registration/camera worker, read by render thread — volatile on the skeleton ref
            // ensures cross-core visibility.
            public volatile Player.Skeleton? Skeleton;
        }

        #endregion

        #region Constructor

        internal RegisteredPlayers(ulong gameWorldBase, string mapId)
        {
            _gameWorldBase = gameWorldBase;
            _mapId = mapId;
        }

        #endregion

        #region IReadOnlyCollection

        /// <summary>
        /// Returns a zero-allocation struct enumerator. The C# compiler picks this overload
        /// for <c>foreach</c> via duck typing, so render/ESP/web hot paths never box the
        /// enumerator on the heap. The <see cref="IEnumerable{T}"/> overload below remains
        /// available for LINQ/external consumers (those still box).
        /// </summary>
        public PlayerEnumerator GetEnumerator() =>
            new(_players.GetEnumerator());

        IEnumerator<Player.Player> IEnumerable<Player.Player>.GetEnumerator() =>
            new PlayerEnumerator(_players.GetEnumerator());

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            new PlayerEnumerator(_players.GetEnumerator());

        /// <summary>
        /// Projecting enumerator: wraps ConcurrentDictionary's enumerator and projects
        /// KeyValuePair → Player. Avoids the extra LINQ Select iterator allocation.
        /// Public so the compiler can resolve it for <c>foreach</c> on external callers
        /// (e.g. <c>RadarWindow.Render</c>, <c>EspWindow</c>, <c>WebRadarServer</c>).
        /// </summary>
        public struct PlayerEnumerator(IEnumerator<KeyValuePair<ulong, PlayerEntry>> inner) : IEnumerator<Player.Player>
        {
            public readonly Player.Player Current => inner.Current.Value.Player;
            readonly object System.Collections.IEnumerator.Current => Current;
            public readonly bool MoveNext() => inner.MoveNext();
            public readonly void Reset() => inner.Reset();
            public readonly void Dispose() => inner.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Lightweight snapshot of per-player addresses used for IL2CPP class hierarchy dumps.
        /// All fields are raw memory addresses — 0 means unresolved/unavailable.
        /// </summary>
        internal readonly record struct PlayerDumpEntry(
            ulong PlayerBase,
            bool IsObserved,
            ulong ObservedHealthControllerAddr,
            Player.Player Player);

        /// <summary>
        /// Returns a snapshot of all currently tracked players with their raw addresses,
        /// for use in off-thread IL2CPP dump operations.
        /// </summary>
        internal List<PlayerDumpEntry> GetPlayerDumpEntries()
        {
            var result = new List<PlayerDumpEntry>(_players.Count);
            foreach (var kvp in _players)
            {
                var e = kvp.Value;
                result.Add(new PlayerDumpEntry(
                    e.Base,
                    e.IsObserved,
                    e.ObservedHealthControllerAddr,
                    e.Player));
            }
            return result;
        }


        /// <summary>
        /// Non-blocking single attempt to discover the local player (MainPlayer).
        /// Called by the registration worker on each tick until successful.
        /// Returns <c>true</c> once the local player is registered.
        /// </summary>
        internal bool TryDiscoverLocalPlayer()
        {
            if (LocalPlayer is not null)
                return true;

            var mainPlayerPtr = Memory.ReadPtr(_gameWorldBase + Offsets.ClientLocalGameWorld.MainPlayer, false);
            if (!mainPlayerPtr.IsValidVirtualAddress())
                return false;

            var className = ReadClassName(mainPlayerPtr);
            var entry = CreatePlayerEntry(mainPlayerPtr, isLocal: true);
            if (entry is null)
                return false;

            LocalPlayer = entry.Player;
            LocalPlayerAddr = mainPlayerPtr;
            entry.Player.Base = mainPlayerPtr;
            _players[mainPlayerPtr] = entry;
            Log.WriteLine($"[RegisteredPlayers] LocalPlayer found: {entry.Player.Name} (class='{className ?? "<null>"}')");
            return true;
        }

        /// <summary>
        /// Registration refresh: reads the player list, discovers new players, removes gone ones.
        /// Called from the slower registration worker thread.
        /// </summary>
        internal void RefreshRegistration()
        {
            long swTotal = Stopwatch.GetTimestamp();
            ulong rgtPlayersAddr;
            MemList<ulong> ptrs;

            try
            {
                long swListRead = Stopwatch.GetTimestamp();
                rgtPlayersAddr = Memory.ReadPtr(_gameWorldBase + Offsets.ClientLocalGameWorld.RegisteredPlayers, false);
                ptrs = MemList<ulong>.Get(rgtPlayersAddr, false);
                var listReadMs = Stopwatch.GetElapsedTime(swListRead).TotalMilliseconds;
                if (listReadMs > 10)
                    Log.WriteLine($"[RegisteredPlayers] SLOW list-read: {listReadMs:F1}ms");
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "rp_list", TimeSpan.FromSeconds(5),
                    $"[RegisteredPlayers] Failed to read player list: {ex.Message}");
                return;
            }

            using (ptrs)
            {
                var count = ptrs.Count;
                if (count < 1 || count > MaxPlayerCount)
                {
                    _invalidCountStreak++;
                    Log.WriteRateLimited(AppLogLevel.Warning, "rp_count", TimeSpan.FromSeconds(10),
                        $"[RegisteredPlayers] Invalid player count: {count} (addr=0x{rgtPlayersAddr:X}), streak={_invalidCountStreak}");

                    // Player count dropping to 0 is a strong signal the raid has ended
                    if (count == 0 && LocalPlayer is not null)
                    {
                        LocalPlayerLost = true;
                        LocalPlayer = null;
                    }

                    // Exponential backoff: sleep longer when we keep getting invalid counts (e.g., after raid ends)
                    if (_invalidCountStreak > 3)
                    {
                        int backoffMs = Math.Min(1000 * _invalidCountStreak, 10_000);
                        Thread.Sleep(backoffMs);
                    }
                    return;
                }

                _invalidCountStreak = 0;
                _listCount = count;

                // Reuse the HashSet across calls to avoid per-tick allocation
                var seen = _seenSet;
                seen.Clear();
                seen.EnsureCapacity(count);

                int invalidPtrs = 0;
                int newDiscovered = 0;
                int newFailed = 0;
                var now = DateTime.UtcNow;

                // Prune backoff entries for addresses no longer in the player list
                // (player was removed before we ever managed to create it).
                if (_failedEntryBackoff.Count > 0)
                {
                    long swPrune = Stopwatch.GetTimestamp();
                    // Build a quick set of current list addresses for O(1) lookup
                    _failedBackoffPrune.Clear();
                    for (int i = 0; i < ptrs.Count; i++)
                    {
                        var p = ptrs[i];
                        if (p.IsValidVirtualAddress())
                            _failedBackoffPrune.Add(p);
                    }

                    // Remove backoff entries that are no longer in the player list
                    _failedBackoffRemove.Clear();
                    foreach (var kvp in _failedEntryBackoff)
                    {
                        if (!_failedBackoffPrune.Contains(kvp.Key))
                            _failedBackoffRemove.Add(kvp.Key);
                    }
                    foreach (var key in _failedBackoffRemove)
                        _failedEntryBackoff.Remove(key);
                    var pruneMs = Stopwatch.GetElapsedTime(swPrune).TotalMilliseconds;
                    if (pruneMs > 5)
                        Log.WriteLine($"[RegisteredPlayers] SLOW backoff-prune: {pruneMs:F1}ms");
                }

                // Discover new players
                long swDiscover = Stopwatch.GetTimestamp();
                for (int i = 0; i < ptrs.Count; i++)
                {
                    var ptr = ptrs[i];
                    if (!ptr.IsValidVirtualAddress())
                    {
                        invalidPtrs++;
                        continue;
                    }

                    seen.Add(ptr);

                    if (_players.ContainsKey(ptr))
                        continue;

                    // Check backoff — skip addresses that failed recently
                    if (_failedEntryBackoff.TryGetValue(ptr, out var backoff) && now < backoff.NextRetry)
                        continue;

                    long swCreate = Stopwatch.GetTimestamp();
                    var entry = CreatePlayerEntry(ptr, isLocal: false);
                    var createMs = Stopwatch.GetElapsedTime(swCreate).TotalMilliseconds;
                    if (createMs > 20)
                        Log.WriteLine($"[RegisteredPlayers] SLOW CreatePlayerEntry @ 0x{ptr:X}: {createMs:F1}ms");

                    if (entry is not null)
                    {
                        _players.TryAdd(ptr, entry);
                        _failedEntryBackoff.Remove(ptr);
                        newDiscovered++;
                    }
                    else
                    {
                        // Exponential backoff: 0.5s, 1s, 2s... capped at 5s
                        int failures = _failedEntryBackoff.TryGetValue(ptr, out var prev)
                            ? prev.Failures + 1
                            : 1;
                        double backoffSec = Math.Min(0.5 * Math.Pow(2, failures - 1), 5.0);
                        _failedEntryBackoff[ptr] = (failures, now.AddSeconds(backoffSec));
                        newFailed++;
                    }
                }
                var discoverMs = Stopwatch.GetElapsedTime(swDiscover).TotalMilliseconds;
                if (discoverMs > 20)
                    Log.WriteLine($"[RegisteredPlayers] SLOW discover-loop ({count} ptrs, {newDiscovered} new, {newFailed} failed): {discoverMs:F1}ms");

                if (newDiscovered > 0 || newFailed > 0 || invalidPtrs > 0)
                {
                    Log.WriteLine($"[RegisteredPlayers] Refresh: list={count}, valid={seen.Count}, invalidPtrs={invalidPtrs}, " +
                        $"new={newDiscovered}, failed={newFailed}, total={_players.Count}");
                }
            }

            // Batch-init transforms and rotations for all entries that need it.
            long swBatch = Stopwatch.GetTimestamp();
            BatchInitTransformsAndRotations();
            var batchMs = Stopwatch.GetElapsedTime(swBatch).TotalMilliseconds;
            if (batchMs > 5)
                Log.WriteLine($"[RegisteredPlayers] SLOW BatchInitTransformsAndRotations: {batchMs:F1}ms");

            // Update existing players — mark active/inactive based on registration
            UpdateExistingPlayers(_seenSet);

            // Target: registration worker total under 8ms (below the worker tick budget).
            var totalMs = Stopwatch.GetElapsedTime(swTotal).TotalMilliseconds;
            if (totalMs > 8)
                Log.WriteRateLimited(AppLogLevel.Warning, "rp_slow_total", TimeSpan.FromSeconds(2),
                    $"[RegisteredPlayers] SLOW RefreshRegistration total: {totalMs:F1}ms (tracked={_players.Count})");
        }

        /// <summary>
        /// Scatter-batched realtime update: reads position + rotation for ALL active players
        /// in a single DMA round-trip. Called from the fast realtime worker thread.
        /// </summary>
        internal void UpdateRealtimeData()
        {
            if (_players.IsEmpty)
                return;

            using var scatter = Memory.GetScatter(VmmFlags.NOCACHE);

            // Collect active entries and prepare scatter reads (no delegates, no allocation).
            // The local player is moved to the FRONT of the processing list so its position
            // is decoded and applied before any remote players — keeping radar latency for
            // "me" as low as physically possible on every tick. We append then swap with
            // index 0 (O(1)) instead of List.Insert(0, …) which would shift the entire list.
            _activeEntries.Clear();
            int localIdx = -1;
            int transformReady = 0, rotationReady = 0;
            foreach (var kvp in _players)
            {
                var entry = kvp.Value;
                if (!entry.Player.IsActive)
                    continue;

                if (entry.Player.IsLocalPlayer)
                    localIdx = _activeEntries.Count;
                _activeEntries.Add(entry);

                PrepareScatterReads(scatter, entry);

                if (entry.TransformReady) transformReady++;
                if (entry.RotationReady) rotationReady++;
            }

            if (localIdx > 0)
            {
                (_activeEntries[0], _activeEntries[localIdx]) = (_activeEntries[localIdx], _activeEntries[0]);
            }

            if (_activeEntries.Count == 0)
                return;

            // Execute single DMA round-trip
            scatter.Execute();

            // Process results inline — local player first, so its Position/Rotation are
            // committed before we touch any remote player.
            foreach (var entry in _activeEntries)
            {
                ProcessScatterResults(scatter, entry);
            }

            // Update live DMA performance counters. Each PrepareRead call results in one
            // physical scatter entry resolved to a full 4 KB page by the DMA hardware.
            DMA.DmaStats.RecordTick(
                entityCount:     _activeEntries.Count,
                maxScatterItems: transformReady + rotationReady);

            // Periodic summary (every ~10s). Use the predicate variant so the interpolated
            // string is only built on the 1-in-1250 ticks that actually emit a line — the
            // realtime worker runs every ~8ms on the sacred pos/rot path.
            if (Log.ShouldEmitRateLimited(AppLogLevel.Info, "realtime_summary", TimeSpan.FromSeconds(10)))
            {
                Log.Write(AppLogLevel.Info,
                    $"[RealtimeWorker] Scatter: active={_activeEntries.Count} (position={transformReady}, rotation={rotationReady}), total={_players.Count}");
            }
        }

        /// <summary>
        /// Returns a refresh-interval multiplier based on the squared distance from the local
        /// player. Far-away players get a longer interval since their gear/weapon rarely matters
        /// frame-to-frame. The first read is never scaled (caller checks GearReady/HandsReady).
        /// Tier boundaries and multipliers are read live from <see cref="SilkConfig"/> so they
        /// can be tuned mid-raid via the UI.
        /// </summary>
        private double GetDistanceMultiplier(PlayerEntry entry, bool gear)
        {
            var lp = LocalPlayer;
            if (lp is null || !lp.HasValidPosition || !entry.Player.HasValidPosition)
                return 1.0;

            // Squared distance avoids the sqrt cost (called once per refresh decision).
            var d = entry.Player.Position - lp.Position;
            float distSqr = d.X * d.X + d.Y * d.Y + d.Z * d.Z;

            var cfg = SilkProgram.Config;
            float nearSqr = cfg.DistanceRefreshNearMeters * cfg.DistanceRefreshNearMeters;
            float midSqr  = cfg.DistanceRefreshMidMeters  * cfg.DistanceRefreshMidMeters;

            if (distSqr <= nearSqr)
                return 1.0;
            if (distSqr <= midSqr)
                return gear ? cfg.GearRefreshMidMul : cfg.HandsRefreshMidMul;
            return gear ? cfg.GearRefreshFarMul : cfg.HandsRefreshFarMul;
        }

        #endregion

        #region Player Lifecycle

        /// <summary>
        /// Updates existing player states based on the current registered set.
        /// Budgets expensive gear/hands refreshes to <see cref="MaxRefreshesPerTick"/> per tick
        /// to keep registration worker tick times bounded and predictable.
        /// </summary>
        private void UpdateExistingPlayers(HashSet<ulong> registered)
        {
            List<ulong>? toRemove = null;
            int refreshBudget = MaxRefreshesPerTick;
            long budgetStart = Stopwatch.GetTimestamp();
            long budgetLimitTicks = _playerUpdateBudgetTicks;

            foreach (var kvp in _players)
            {
                var entry = kvp.Value;

                if (registered.Contains(kvp.Key))
                {
                    // Player still registered — mark active
                    entry.Player.IsActive = true;
                    entry.Player.IsAlive = true;

                    // Transform + rotation init/re-init is handled by BatchInitTransformsAndRotations()
                    // which runs before UpdateExistingPlayers in the registration worker cycle.

                    // Skip expensive gear/hands work if budget is exhausted this tick.
                    // Players that missed their window will catch up in subsequent ticks.
                    if (refreshBudget <= 0)
                        continue;

                    // Hard wall-clock budget: if we've already spent most of our tick budget
                    // doing DMA work for earlier players, stop issuing new refreshes this tick.
                    // The remaining players simply defer to the next tick (100ms away).
                    if (Stopwatch.GetTimestamp() - budgetStart > budgetLimitTicks)
                        continue;

                    var now = DateTime.UtcNow;

                    // Gear refresh (rate-limited per player, budgeted per tick)
                    if (now >= entry.NextGearRefresh)
                    {
                        // Jitter the next interval to prevent long-term timer re-alignment.
                        // First read = full cadence; subsequent reads scale with distance to local
                        // player UNLESS we're ADS (sniping → don't degrade far-target freshness).
                        double jitter = Rng.NextDouble() * GearRefreshJitterSec;
                        var cfg = SilkProgram.Config;
                        double gearMul = (cfg.DistanceAwareRefresh && entry.Player.GearReady && !CameraManager.IsADS)
                            ? GetDistanceMultiplier(entry, gear: true)
                            : 1.0;
                        entry.NextGearRefresh = now.AddSeconds(GearRefreshIntervalSec * gearMul + jitter);
                        long swGear = Stopwatch.GetTimestamp();
                                GearManager.Refresh(entry.Base, entry.Player, entry.IsObserved);
                                var gearMs = Stopwatch.GetElapsedTime(swGear).TotalMilliseconds;
                                if (gearMs > 8)
                                    Log.WriteLine($"[RegisteredPlayers] SLOW GearManager.Refresh '{entry.Player.Name}': {gearMs:F1}ms");
                        entry.Player.GearReady = true;
                        refreshBudget--;

                        // Boss-guard identification heuristic (map-specific).
                        try { Player.Plugins.GuardManager.Evaluate(entry.Player, Memory.MapID); }
                        catch { /* non-fatal */ }

                        // Stable PMC display name assignment.
                        try { Player.Plugins.PlayerListManager.GetOrAssign(entry.Player); }
                        catch { /* non-fatal */ }

                        // Re-check DogtagCache for players with a ProfileId but no resolved name yet.
                        // Corpse dogtags may have been seeded since the last gear refresh.
                        if (entry.Player.ProfileId is not null && entry.Player.AccountId is null)
                        {
                            if (DogtagCache.TryApplyIdentity(entry.Player) && entry.Player.AccountId is not null)
                                CheckWatchlist(entry.Player);
                        }
                    }

                    // Hands refresh (rate-limited per player, budgeted per tick)
                    if (now >= entry.NextHandsRefresh)
                    {
                        // First hands read = full cadence; subsequent reads scale with distance
                        // unless ADS (sniping → keep watching far-target weapon swaps).
                        var cfg = SilkProgram.Config;
                        double handsMul = (cfg.DistanceAwareRefresh && entry.Player.HandsReady && !CameraManager.IsADS)
                            ? GetDistanceMultiplier(entry, gear: false)
                            : 1.0;
                        entry.NextHandsRefresh = now.AddSeconds(HandsRefreshIntervalSec * handsMul);
                        HandsManager.Refresh(entry.Base, entry.Player, entry.IsObserved);
                        entry.Player.HandsReady = true;
                        refreshBudget--;

                        // Firearm detail refresh (fire mode + mag counts) — piggybacks on hands interval.
                        try { Player.Plugins.FirearmManager.Refresh(entry.Base, entry.Player); }
                        catch { /* non-fatal */ }
                    }

                    // Health status refresh — queued for scatter-batch below; local player handled inline.
                    if (now >= entry.NextHealthRefresh)
                    {
                        entry.NextHealthRefresh = now.AddSeconds(HealthRefreshIntervalSec);

                        if (!entry.IsObserved && entry.Player is Player.LocalPlayer lp)
                        {
                            // Local player: read energy/hydration (not in scatter path)
                            lp.UpdateEnergyHydration(entry.Base);
                        }
                        else if (entry.IsObserved)
                        {
                            // Mark for scatter batch — processed after the foreach in BatchUpdateHealthStatuses().
                            entry.HealthDueTick = true;
                        }
                    }

                    }
                else
                {
                    // Player no longer in the registered list — mark inactive and queue for removal
                    entry.Player.IsActive = false;
                    entry.Player.IsAlive = false;
                    (toRemove ??= []).Add(kvp.Key);
                }
            }

            if (toRemove is not null)
            {
                foreach (var key in toRemove)
                {
                    if (_players.TryRemove(key, out var removed))
                    {
                        HandsManager.ClearCache(key);
                        GearManager.ClearCache(key);
                        Log.WriteLine($"[RegisteredPlayers] Removed '{removed.Player.Name}' ({removed.Player.Type}) @ 0x{key:X} — no longer registered");

                        if (removed.Player.IsLocalPlayer)
                        {
                            LocalPlayerLost = true;
                            LocalPlayer = null;
                            Log.WriteLine("[RegisteredPlayers] Local player lost — raid is likely ending.");
                        }
                    }
                }
            }

            // Batch all observed-player health reads into a single scatter round.
            BatchUpdateHealthStatuses();
        }

        /// <summary>
        /// Batches all observed-player ETagStatus reads into a single scatter round-trip.
        /// Players whose ObservedHealthController is not yet resolved are handled via a
        /// preceding pointer-chain scatter; newly resolved addresses are stored back into
        /// the entry so subsequent ticks use the fast path.
        /// </summary>
        private void BatchUpdateHealthStatuses()
        {
            // Collect entries whose ObservedHealthController has already been resolved.
            // Entries without a resolved address need two pointer-chain reads first.
            var resolved = new List<PlayerEntry>();
            var needResolve = new List<PlayerEntry>();

            foreach (var kvp in _players)
            {
                var entry = kvp.Value;
                if (!entry.IsObserved || !entry.Player.IsActive || !entry.HealthDueTick)
                    continue;
                entry.HealthDueTick = false;

                if (entry.ObservedHealthControllerAddr != 0)
                    resolved.Add(entry);
                else
                    needResolve.Add(entry);
            }

            // --- Phase 1: resolve ObservedHealthController for new observed players ---
            // Two scatter rounds: OPV -> OPC ptr, then OPC -> OHC ptr (with backref validate).
            if (needResolve.Count > 0)
            {
                var opcAddrs = new ulong[needResolve.Count];
                using (var s = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    foreach (var e in needResolve)
                        s.PrepareReadValue<ulong>(e.Base + Offsets.ObservedPlayerView.ObservedPlayerController);
                    s.Execute();
                    for (int i = 0; i < needResolve.Count; i++)
                        s.ReadValue<ulong>(needResolve[i].Base + Offsets.ObservedPlayerView.ObservedPlayerController, out opcAddrs[i]);
                }

                var ohcAddrs = new ulong[needResolve.Count];
                using (var s = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < needResolve.Count; i++)
                        if (opcAddrs[i].IsValidVirtualAddress())
                            s.PrepareReadValue<ulong>(opcAddrs[i] + Offsets.ObservedPlayerController.HealthController);
                    s.Execute();
                    for (int i = 0; i < needResolve.Count; i++)
                        if (opcAddrs[i].IsValidVirtualAddress())
                            s.ReadValue<ulong>(opcAddrs[i] + Offsets.ObservedPlayerController.HealthController, out ohcAddrs[i]);
                }

                // Validate backref and store resolved addresses; push to resolved list for the tag read.
                var backrefAddrs = new ulong[needResolve.Count];
                using (var s = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < needResolve.Count; i++)
                        if (ohcAddrs[i].IsValidVirtualAddress())
                            s.PrepareReadValue<ulong>(ohcAddrs[i] + Offsets.ObservedHealthController.Player);
                    s.Execute();
                    for (int i = 0; i < needResolve.Count; i++)
                        if (ohcAddrs[i].IsValidVirtualAddress())
                            s.ReadValue<ulong>(ohcAddrs[i] + Offsets.ObservedHealthController.Player, out backrefAddrs[i]);
                }

                for (int i = 0; i < needResolve.Count; i++)
                {
                    var e = needResolve[i];
                    if (ohcAddrs[i].IsValidVirtualAddress() && backrefAddrs[i] == e.Base)
                    {
                        e.ObservedHealthControllerAddr = ohcAddrs[i];
                        Log.Write(AppLogLevel.Debug,
                            $"[RegisteredPlayers] ObservedHealthController resolved for '{e.Player.Name}': OHC=0x{ohcAddrs[i]:X}");
                        resolved.Add(e);
                    }
                }
            }

            // --- Phase 2: read ETagStatus for all resolved entries in one scatter pass ---
            if (resolved.Count == 0)
                return;

            long swHealth = Stopwatch.GetTimestamp();
            using (var s = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                foreach (var e in resolved)
                    s.PrepareReadValue<int>(e.ObservedHealthControllerAddr + Offsets.ObservedHealthController.HealthStatus);
                s.Execute();

                foreach (var e in resolved)
                {
                    if (!s.ReadValue<int>(e.ObservedHealthControllerAddr + Offsets.ObservedHealthController.HealthStatus, out var tag))
                        continue;

                    if ((tag & ETagDying) != 0)
                        e.Player.HealthStatus = Player.EHealthStatus.Dying;
                    else if ((tag & ETagBadlyInjured) != 0)
                        e.Player.HealthStatus = Player.EHealthStatus.BadlyInjured;
                    else if ((tag & ETagInjured) != 0)
                        e.Player.HealthStatus = Player.EHealthStatus.Injured;
                    else
                        e.Player.HealthStatus = Player.EHealthStatus.Healthy;
                }
            }
            var healthMs = Stopwatch.GetElapsedTime(swHealth).TotalMilliseconds;
            if (healthMs > 4)
                Log.WriteLine($"[RegisteredPlayers] SLOW BatchUpdateHealthStatuses ({resolved.Count} players): {healthMs:F1}ms");
        }

        /// <summary>
        /// Reads the ETagStatus bitmask from the ObservedHealthController and maps it
        /// to the simplified <see cref="Player.EHealthStatus"/> enum.
        /// Kept as fallback for one-off calls outside the batch path.
        /// </summary>
        private static void UpdateObservedHealthStatus(PlayerEntry entry)
        {
            var ohc = entry.ObservedHealthControllerAddr;
            if (ohc == 0)
                return; // Not yet resolved — will stay Healthy

            try
            {
                if (!Memory.TryReadValue<int>(ohc + Offsets.ObservedHealthController.HealthStatus, out var tag, false))
                    return;

                // ETagStatus is a [Flags] enum — check from most severe to least
                if ((tag & ETagDying) != 0)
                    entry.Player.HealthStatus = Player.EHealthStatus.Dying;
                else if ((tag & ETagBadlyInjured) != 0)
                    entry.Player.HealthStatus = Player.EHealthStatus.BadlyInjured;
                else if ((tag & ETagInjured) != 0)
                    entry.Player.HealthStatus = Player.EHealthStatus.Injured;
                else
                    entry.Player.HealthStatus = Player.EHealthStatus.Healthy;
            }
            catch
            {
                // Suppressed — transient DMA failure
            }
        }

        #endregion

        #region Skeleton

        // Skeleton init is expensive (~96 sequential DMA reads per player) — limit to once per 500ms.
        private DateTime _nextSkeletonInit;
        private static readonly TimeSpan SkeletonInitInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Attempts to initialize skeletons for players that don't have one yet.
        /// Rate-limited to avoid hammering DMA with pointer chain walks every tick.
        /// Called from the camera worker.
        /// </summary>
        internal void TryInitSkeletons()
        {
            var now = DateTime.UtcNow;
            if (now < _nextSkeletonInit)
                return;
            _nextSkeletonInit = now + SkeletonInitInterval;

            foreach (var kvp in _players)
            {
                var entry = kvp.Value;
                if (!entry.Player.IsActive || !entry.Player.IsAlive)
                    continue;

                // Skip the local player — we don't draw skeleton for ourselves
                if (entry.Player.IsLocalPlayer)
                    continue;

                // Already has a skeleton
                if (entry.Skeleton is not null)
                    continue;

                // Need a valid transform first (position must be working)
                if (!entry.TransformReady)
                    continue;

                entry.Skeleton = Player.Skeleton.TryCreate(entry.Base, entry.IsObserved);

                // Sync to Player for O(1) render-thread access
                if (entry.Skeleton is not null)
                    entry.Player.Skeleton = entry.Skeleton;
            }
        }

        /// <summary>
        /// Updates all active player skeleton bone positions via a single batched DMA scatter.
        /// Called from the camera worker thread.
        /// </summary>
        internal void UpdateSkeletons()
        {
            // Collect active player entries (with valid skeleton)
            int count = 0;
            PlayerEntry[] players = _playerUpdateBuf;
            foreach (var kvp in _players)
            {
                var entry = kvp.Value;
                if (!entry.Player.IsActive || !entry.Player.IsAlive)
                    continue;

                var skeleton = entry.Skeleton;
                if (skeleton is null || !skeleton.TransformsReady)
                    continue;

                if (count < players.Length)
                    players[count++] = entry;
            }

            if (count == 0)
                return;

            // Single scatter for ALL bone vertex arrays across ALL players
            Player.Skeleton.UpdateBonePositionsBatched(players.AsSpan(0, count));

            // Clear refs to avoid holding them across ticks
            Array.Clear(players, 0, count);
        }

        // Reusable buffer for skeleton update — avoids per-tick allocation
        private readonly PlayerEntry[] _playerUpdateBuf = new PlayerEntry[MaxPlayerCount];

        /// <summary>
        /// Drops the cached skeleton for a player so the camera worker re-creates it
        /// from scratch on the next <see cref="TryInitSkeletons"/> pass. Must be called
        /// whenever the player's main Transform changes (re-init, mass invalidation,
        /// auto-invalidation after repeated position failures) because the skeleton
        /// caches bone TransformInternal pointers rooted in the old hierarchy.
        /// </summary>
        private static void InvalidateSkeleton(PlayerEntry entry)
        {
            if (entry.Skeleton is null)
                return;
            entry.Skeleton = null;
            entry.Player.Skeleton = null;
        }

        #endregion

        #region Debug Dump

        /// <summary>
        /// Dumps IL2CPP hierarchy for every currently-active player.
        /// Gated by <see cref="Log.EnableDebugLogging"/> — no-op in normal operation.
        /// Called by <see cref="LocalGameWorld.DumpAll"/> and the F8 toggle path.
        /// </summary>
        internal void DumpAll()
        {
            if (!Log.EnableDebugLogging)
                return;

            foreach (var kvp in _players)
            {
                var entry = kvp.Value;
                try
                {
                    DumpPlayerHierarchy(entry.Base, entry.Player.Name, entry.IsObserved);
                }
                catch (Exception ex)
                {
                    Log.Write(AppLogLevel.Debug, $"DumpPlayerHierarchy failed for '{entry.Player.Name}': {ex.Message}", "RegisteredPlayers");
                }
            }
        }

        #endregion
    }
}
