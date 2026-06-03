using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using eft_dma_radar.Silk.DMA;
using eft_dma_radar.Silk.Misc;
using VmmSharpEx.Options;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>Cache life-cycle state visible to the debug window and renderers.</summary>
    internal enum SceneCacheState
    {
        /// <summary>Never built; <see cref="SceneCache.Snapshot"/> is the empty placeholder.</summary>
        Idle,
        /// <summary>A build task is currently walking PhysX and producing a snapshot.</summary>
        Building,
        /// <summary>A snapshot is ready and current.</summary>
        Ready,
        /// <summary>Last build failed; <see cref="SceneCache.LastError"/> has the reason.</summary>
        Failed,
    }

    /// <summary>
    /// Owns the live PhysX <see cref="SceneSnapshot"/>. Provides:
    /// <list type="bullet">
    ///   <item>A read-only <see cref="Snapshot"/> accessor for the visibility
    ///     worker and renderers â€” guaranteed to never be null.</item>
    ///   <item>A non-blocking <see cref="TriggerBuild"/> that runs the walk on
    ///     a background task; while it runs, the previous snapshot stays
    ///     readable so the radar keeps working.</item>
    ///   <item>Atomic publication: a finished build replaces <see cref="Snapshot"/>
    ///     with a single reference write. Readers that already grabbed the
    ///     previous reference keep using its arrays â€” safe and lock-free.</item>
    ///   <item>State + metrics for the debug overlay (build duration, source
    ///     pointer, actor / mesh / heightfield counts, last error).</item>
    /// </list>
    /// </summary>
    internal static class SceneCache
    {
        // â”€â”€ Public observable state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>The current snapshot. Never null. Replaced atomically by builds.</summary>
        public static SceneSnapshot Snapshot => _snapshot;

        public static SceneCacheState State => _state;

        public static string LastError => _lastError;

        /// <summary>How long the most recent successful build took.</summary>
        public static TimeSpan LastBuildDuration => _lastBuildDuration;

        /// <summary>Wall-clock time the last build started (UTC), or <c>default</c>.</summary>
        public static DateTime LastBuildStartedUtc => _lastBuildStartedUtc;

        /// <summary>NpPhysics pointer resolved during the most recent build attempt.</summary>
        public static ulong LastNpPhysics => _lastNpPhysics;

        /// <summary>RVA used to resolve the NpPhysics pointer (constant unless re-discovery runs).</summary>
        public static uint LastSdkRva => _lastSdkRva;

        /// <summary>
        /// Total successful builds + total failures, for the debug overlay's "1
        /// build / 0 errors" indicator.
        /// </summary>
        public static int BuildSuccessCount => _buildSuccessCount;
        public static int BuildFailureCount => _buildFailureCount;

        /// <summary>
        /// Drop EFT's <c>*_COLLIDER</c> movement-collision shapes at ingest,
        /// keeping the paired <c>*_BALLISTIC_*</c> bullet/sight blockers. EFT
        /// ships both for almost every object, so this roughly halves the
        /// snapshot with no loss for visibility (BALLISTIC is the real
        /// sight blocker) or maps (BALLISTIC carries the deck geometry).
        /// Takes effect on the next rebuild; existing on-disk snapshots must be
        /// invalidated to shrink.
        /// </summary>
        public static bool DropMovementColliders { get; set; } = true;

        // â”€â”€ Per-skip counters from the most recent build (for IDA-assisted debugging) â”€â”€
        public static int LastSkippedNonRigid    { get; private set; }
        public static int LastSkippedZeroShapes  { get; private set; }
        public static int LastSkippedReadError   { get; private set; }
        public static int LastSkippedBadGeometry { get; private set; }
        public static int LastSkippedCollider    { get; private set; }
        /// <summary>One representative actor pointer that was processed (or attempted) in the last build. Diagnostic.</summary>
        public static ulong LastSampleActorPtr   { get; private set; }
        /// <summary>The raw concrete-type byte read at <c>+0x8</c> of that sample actor. Useful for IDA cross-check.</summary>
        public static ushort LastSampleActorTypeRaw { get; private set; }

        // â”€â”€ Backing fields â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static SceneSnapshot _snapshot = SceneSnapshot.Empty;
        private static SceneCacheState _state = SceneCacheState.Idle;
        private static string _lastError = string.Empty;
        private static TimeSpan _lastBuildDuration;
        private static DateTime _lastBuildStartedUtc;
        private static ulong _lastNpPhysics;
        private static uint _lastSdkRva = PhysXOffsets.PhysXSdkRva;
        private static int _buildSuccessCount;
        private static int _buildFailureCount;

        // Exactly one build runs at a time. A second TriggerBuild() while a
        // build is in flight is silently dropped (logged).
        private static int _buildInFlight;

        // Per-build sample collector for the NativeGameObject layer-offset
        // probe. Each entry is (gameObjectPtr, expectedLayer-mask-from-shape).
        // Reset at the start of every Walk; emptied + analysed in the build
        // summary block so the right offset can be picked empirically rather
        // than guessed. Sampled, not exhaustive â€” 256 actors is plenty of
        // statistical power for a 32-value-domain probe.
        private const int MaxLayerProbeSamples = 256;
        private static readonly List<(ulong GameObject, uint ShapeLayerMask)> _layerProbeSamples
            = new(MaxLayerProbeSamples);

        // â”€â”€ Public API â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Replaces <see cref="Snapshot"/> with the empty placeholder. Used on
        /// match end / game disconnect so we never serve a stale snapshot from
        /// a previous match. Cheap â€” one reference write.
        /// </summary>
        public static void Reset()
        {
            Volatile.Write(ref _snapshot, SceneSnapshot.Empty);
            _state = SceneCacheState.Idle;
            _lastError = string.Empty;
        }

        /// <summary>
        /// Loads a snapshot for <paramref name="mapId"/> from disk and
        /// publishes it as the current <see cref="Snapshot"/> â€” bypassing
        /// the fingerprint check so the Cache View can inspect cached maps
        /// with no live game attached. Magic / version / CRC still validate;
        /// any failure leaves the previous snapshot in place and returns
        /// <c>false</c> with a human-readable reason in
        /// <paramref name="error"/>.
        /// <para>
        /// Re-entrancy: shares the <c>_buildInFlight</c> guard with
        /// <see cref="TriggerBuild"/> so a live build and an offline load
        /// can't race. The load is synchronous â€” the on-disk parse is fast
        /// (â‰¤ 500 ms even for 10 k actors) and we want the snapshot ready
        /// before the next UI frame redraws.
        /// </para>
        /// </summary>
        public static bool LoadFromDisk(string mapId, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(mapId))
            {
                error = "mapId is empty";
                return false;
            }
            if (Interlocked.Exchange(ref _buildInFlight, 1) == 1)
            {
                error = "another build/load is already in flight";
                return false;
            }

            try
            {
                _state = SceneCacheState.Building;
                _lastBuildStartedUtc = DateTime.UtcNow;
                _lastError = string.Empty;

                var sw = Stopwatch.StartNew();
                if (!SnapshotSerializer.TryLoad(mapId, 0, out var cached, out var loadErr,
                        requireFingerprint: false)
                    || cached is null)
                {
                    sw.Stop();
                    error = loadErr;
                    _lastError = loadErr;
                    _state = SceneCacheState.Failed;
                    Interlocked.Increment(ref _buildFailureCount);
                    Log.WriteLine($"[SceneCache] Offline load failed for '{mapId}': {loadErr}");
                    return false;
                }

                sw.Stop();
                _lastBuildDuration = sw.Elapsed;
                Volatile.Write(ref _snapshot, cached);
                _state = SceneCacheState.Ready;
                Interlocked.Increment(ref _buildSuccessCount);
                Log.WriteLine(
                    $"[SceneCache] Offline-loaded '{mapId}' in {sw.ElapsedMilliseconds}ms: " +
                    $"actors={cached.Actors.Length} triMeshes={cached.Meshes.Length} " +
                    $"heightfields={cached.HeightFields.Length}");
                VisCheckDiagnostics.OnSnapshotBuilt(cached);
                return true;
            }
            finally
            {
                Interlocked.Exchange(ref _buildInFlight, 0);
            }
        }

        /// <summary>
        /// Kicks a non-blocking build. Returns false if a build is already running
        /// (so the caller can decide whether to log + ignore or queue).
        /// </summary>
        public static bool TriggerBuild(string mapId, bool quiet = false)
        {
            if (Interlocked.Exchange(ref _buildInFlight, 1) == 1)
            {
                if (!quiet)
                    Log.WriteLine("[SceneCache] Build already in flight â€” request ignored.");
                return false;
            }

            _state = SceneCacheState.Building;
            _lastBuildStartedUtc = DateTime.UtcNow;
            _lastError = string.Empty;

            // Fire-and-forget. Errors land in _lastError and bump the failure
            // counter; the previous snapshot is preserved so the radar keeps
            // working through a failed rebuild.
            _ = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // Persistence fast path â€” try loading a previously-saved
                    // snapshot for this map first. Validates magic, CRC, and
                    // fingerprint (UnityPlayer FileVersion + mapId); mismatch
                    // â‡’ silently fall through to a live build.
                    ulong expectedFp = SnapshotSerializer.ComputeFingerprint(
                        Memory.UnityPlayerVersion, mapId);
                    if (SnapshotSerializer.TryLoad(mapId, expectedFp, out var cached, out var loadErr)
                        && cached is not null)
                    {
                        sw.Stop();
                        _lastBuildDuration = sw.Elapsed;
                        Volatile.Write(ref _snapshot, cached);
                        _state = SceneCacheState.Ready;
                        Interlocked.Increment(ref _buildSuccessCount);
                        Log.WriteLine(
                            $"[SceneCache] Loaded from disk in {sw.ElapsedMilliseconds}ms: " +
                            $"actors={cached.Actors.Length} triMeshes={cached.Meshes.Length} " +
                            $"heightfields={cached.HeightFields.Length} (originally built " +
                            $"{(Environment.TickCount64 - cached.BuildTickMs) / 1000}s ago)");
                        VisCheckDiagnostics.OnSnapshotBuilt(cached);
                        return;
                    }
                    if (!string.IsNullOrEmpty(loadErr))
                        Log.WriteLine($"[SceneCache] Disk cache miss: {loadErr} â€” building fresh");

                    var fresh = BuildOnce(mapId);
                    sw.Stop();
                    _lastBuildDuration = sw.Elapsed;

                    if (fresh is null)
                    {
                        _state = SceneCacheState.Failed;
                        Interlocked.Increment(ref _buildFailureCount);
                        Log.WriteLine($"[SceneCache] Build FAILED: {_lastError}");
                    }
                    else
                    {
                        Volatile.Write(ref _snapshot, fresh);
                        _state = SceneCacheState.Ready;
                        Interlocked.Increment(ref _buildSuccessCount);
                        Log.WriteLine(
                            $"[SceneCache] Build complete: actors={fresh.Actors.Length} " +
                            $"triMeshes={fresh.Meshes.Length} heightfields={fresh.HeightFields.Length} " +
                            $"sourceActorCount={fresh.SourceActorCount} buildMs={sw.ElapsedMilliseconds}");

                        // Each downstream step gets its own try/catch with an
                        // explicit log marker. Without this, a crash in
                        // SnapshotSerializer or VisCheckDiagnostics would
                        // bubble up to the outer Task.Run handler and only
                        // surface as "Build EXCEPTION" — which is misleading
                        // since the build itself already succeeded and the
                        // snapshot is live. Granular logging also lets the
                        // user see exactly which side-effect hung when "Build
                        // complete" lands but the next expected log line never
                        // does (the failure mode the user actually hit).
                        try { SnapshotSerializer.TrySave(fresh, Memory.UnityPlayerVersion); }
                        catch (Exception ex)
                        {
                            Log.WriteLine($"[SceneCache] SnapshotSerializer.TrySave threw " +
                                $"{ex.GetType().Name}: {ex.Message} (snapshot is live in memory, just not persisted)");
                        }

                        try { VisCheckDiagnostics.OnSnapshotBuilt(fresh); }
                        catch (Exception ex)
                        {
                            Log.WriteLine($"[SceneCache] VisCheckDiagnostics.OnSnapshotBuilt threw " +
                                $"{ex.GetType().Name}: {ex.Message} (diagnostic dump skipped)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _lastBuildDuration = sw.Elapsed;
                    _lastError = $"{ex.GetType().Name}: {ex.Message}";
                    _state = SceneCacheState.Failed;
                    Interlocked.Increment(ref _buildFailureCount);
                    Log.WriteLine($"[SceneCache] Build EXCEPTION after {sw.ElapsedMilliseconds}ms: {_lastError}");
                }
                finally
                {
                    Volatile.Write(ref _buildInFlight, 0);
                }
            });
            return true;
        }

        // â”€â”€ Build implementation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Walks the live PhysX scene graph and builds a fresh snapshot. Runs on
        /// a background task. Returns null on failure (and writes
        /// <see cref="_lastError"/>); never throws.
        /// </summary>
        private static SceneSnapshot? BuildOnce(string mapId)
        {
            // 1. Resolve the NpPhysics singleton.
            var unityBase = Memory.UnityBase;
            if (unityBase == 0)
            {
                _lastError = "UnityBase=0 (game not attached)";
                return null;
            }

            // Try the cached RVA first (fast path). If the read fails OR the
            // pointer is bogus, fall back to PhysXProbe's sig-scan resolver —
            // necessary when the default RVA (baked for one game build) doesn't
            // apply to the live binary. Once resolved we update _lastSdkRva so
            // subsequent builds skip the scan.
            uint tryRva = _lastSdkRva;
            ulong npPhysics = 0;
            bool ok = Memory.TryReadPtr(unityBase + tryRva, out npPhysics, false)
                      && npPhysics.IsValidVirtualAddress();
            if (!ok)
            {
                Log.WriteLine($"[SceneCache] Cached RVA 0x{tryRva:X8} failed — running PhysXProbe sig-scan...");
                if (PhysXProbe.TryResolveBestRva(out var newRva, out var newNp))
                {
                    Log.WriteLine($"[SceneCache] PhysXProbe resolved RVA 0x{newRva:X8} (np_physics=0x{newNp:X}).");
                    tryRva = newRva;
                    npPhysics = newNp;
                    ok = true;
                }
            }
            if (!ok || !npPhysics.IsValidVirtualAddress())
            {
                _lastError = $"NpPhysics pointer invalid (cached rva=0x{_lastSdkRva:X8}, probe also failed)";
                return null;
            }
            _lastNpPhysics = npPhysics;
            _lastSdkRva = tryRva;

            // 2. Walk the scene array; pick the scene with the largest actor count.
            //    Phase 0 confirmed Arena has 3 scenes; gameplay is whichever has
            //    the most actors (don't trust scene index 0 across patches).
            if (!Memory.TryReadPtr(npPhysics + PhysXOffsets.NpPhysics_SceneArrayData, out var sceneArrayPtr, false)
                || !Memory.TryReadValue<uint>(npPhysics + PhysXOffsets.NpPhysics_SceneArraySize, out var nScenes, false)
                || nScenes == 0 || nScenes > 8)
            {
                _lastError = "scene array unreadable / count out of range";
                return null;
            }

            ulong gameplayScene = 0;
            uint  gameplayActorCount = 0;
            int   gameplayShapedEst = -1;

            for (uint s = 0; s < nScenes; s++)
            {
                if (!Memory.TryReadPtr(sceneArrayPtr + s * 8, out var scenePtr, false)
                    || !scenePtr.IsValidVirtualAddress())
                    continue;
                if (!Memory.TryReadValue<uint>(scenePtr + PhysXOffsets.NpScene_RigidActorsSize, out var aCount, false)
                    || aCount == 0)
                    continue;

                // Sample the first 64 actors and count how many carry at least one shape.
                // Scale to the full actor count so scenes are comparable regardless of size.
                int shapedEst = SampleShapedActors(scenePtr, aCount);
                Log.WriteLine($"[SceneCache] Scene[{s}] 0x{scenePtr:X}: actors={aCount} shapedEst={shapedEst}");

                bool better = shapedEst > gameplayShapedEst
                           || (shapedEst == gameplayShapedEst && aCount > gameplayActorCount);
                if (better)
                {
                    gameplayShapedEst  = shapedEst;
                    gameplayActorCount = aCount;
                    gameplayScene      = scenePtr;
                }
            }

            if (gameplayScene == 0 || gameplayActorCount == 0)
            {
                _lastError = "no scene with non-zero actor count";
                return null;
            }

            // 3. Read the actor pointer array.
            if (!Memory.TryReadPtr(gameplayScene + PhysXOffsets.NpScene_RigidActorsData, out var actorsArrayPtr, false)
                || !actorsArrayPtr.IsValidVirtualAddress())
            {
                _lastError = "actor array pointer invalid";
                return null;
            }

            // Sanity cap only — Interchange empirically has 50k+ real actors;
            // the array can legitimately be 100k+ slots with many nulls
            // mixed in (slot reuse / freed-but-not-shrunk). 500k is a defence
            // against a wildly-misread size field, not a real upper bound.
            // IsValidVirtualAddress is a CLR range check so iterating null
            // slots is essentially free — the DMA cost is concentrated on
            // the valid actors regardless of total array length.
            const int MaxPlausibleActors = 500_000;
            int walkCount = (int)gameplayActorCount;
            if (walkCount > MaxPlausibleActors)
            {
                Log.WriteLine(
                    $"[SceneCache] Reported actor count {walkCount} exceeds sanity " +
                    $"cap {MaxPlausibleActors} — clamping. The size field at NpScene+0x{PhysXOffsets.NpScene_RigidActorsSize:X} " +
                    $"may be over-reporting; investigate the live layout if too few " +
                    $"actors appear post-walk.");
                walkCount = MaxPlausibleActors;
            }

            ulong[]? actorPtrs;
            try { actorPtrs = Memory.ReadArray<ulong>(actorsArrayPtr, walkCount, false); }
            catch (Exception ex)
            {
                _lastError = $"actor array read failed: {ex.Message}";
                return null;
            }
            if (actorPtrs is null)
            {
                _lastError = "actor array read returned null";
                return null;
            }

            // 4. Walk each actor. Skip anything that fails validation; never
            //    let one bad actor abort the whole build.
            var actors = new List<CachedActor>(actorPtrs.Length);
            var meshes = new List<CachedTriMesh>(64);
            var convexes = new List<CachedConvexMesh>(32);
            var heightFields = new List<CachedHeightField>(8);
            var meshDedup    = new Dictionary<ulong, int>(64);
            var convexDedup  = new Dictionary<ulong, int>(32);
            var hfDedup      = new Dictionary<ulong, int>(8);

            // Reset the layer-offset probe collector for this build. Cleared
            // here so any stale entries from a previous walk don't bias the
            // new histogram. Filled inside TryBuildActor.
            _layerProbeSamples.Clear();

            // Reset diagnostic-dump counters so each build emits a fresh round
            // of dumps (otherwise the cap fires once and never again for the
            // process lifetime).
            _convexDumpsLogged = 0;
            _convexGeomUnionDumps = 0;

            int processed = 0;
            int skippedNonRigid = 0;
            int skippedZeroShapes = 0;
            int skippedReadError = 0;
            int skippedCollider = 0;
            int multiShapeActors = 0;
            bool dropColliders = DropMovementColliders;
            // Per-step failure attribution from TryBuildActor; indexed by (int)BuildActorResult.
            // Ok always stays at 0 (those are counted as `processed`).
            var stepCounts = new int[Enum.GetValues<BuildActorResult>().Length];
            ulong sampleActor = 0;
            ushort sampleType = 0;
            // Remember the first actor whose shape pre-check passes â€” that's the
            // shaped actor whose hex dump tells us if downstream offsets are right.
            ulong firstShapedActor = 0;
            // Sample shape pointers from spread-out shaped actors so the
            // post-build diagnostic can compare them side-by-side. The first
            // 8 spaced ~every 200 shaped-actors apart gives variety without
            // adding meaningful build cost.
            const int ShapeSampleStride = 200;
            const int MaxShapeSamples   = 8;
            var sampledShapePtrs = new List<ulong>(MaxShapeSamples);
            int shapedActorsSeen = 0;

            // ── Phase 1: scatter-batched prefilter ──────────────────────────
            //
            // For Interchange-scale scenes (50k+ valid actors in a 145k array),
            // the old "two serial DMA reads per actor" prefilter took 5-10 minutes
            // of pure round-trip time. Scatter-batching ~256 actors at a time
            // collapses that to <2 seconds: one scatter pulls both the PxBase
            // type byte and the NpShapeManager.shape-count for every actor in
            // the chunk, then we filter survivors locally. Only actors that
            // pass both gates (rigid + shape-count > 0) go to the full ingest.

            const int PrefilterChunkSize       = 256;
            const int PrefilterProgressChunks  = 64;   // log every 64 chunks ≈ 16k actors
            // Each survivor's index in actorPtrs is preserved so the diagnostic
            // logs and sample collection downstream can still cite it.
            var survivors = new List<(int Index, ulong ActorPtr, PxConcreteType Type, ushort ShapeCount)>(8192);
            long prefilterStartMs = Environment.TickCount64;
            int prefilterChunks   = 0;

            for (int chunkStart = 0; chunkStart < actorPtrs.Length; chunkStart += PrefilterChunkSize)
            {
                int chunkEnd = Math.Min(chunkStart + PrefilterChunkSize, actorPtrs.Length);

                using (var scatter = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    int prepared = 0;
                    for (int i = chunkStart; i < chunkEnd; i++)
                    {
                        var actorPtr = actorPtrs[i];
                        if (!actorPtr.IsValidVirtualAddress()) continue;
                        scatter.PrepareReadValue<ushort>(
                            actorPtr + PhysXOffsets.PxBase_ConcreteType);
                        scatter.PrepareReadValue<ushort>(
                            actorPtr + PhysXOffsets.PxRigidActor_ShapeManager
                                     + PhysXOffsets.NpShapeManager_ShapesCount);
                        prepared++;
                    }
                    // Empty scatter -> Execute throws; an all-null actor-array
                    // chunk (sparse slot reuse) has nothing to read.
                    if (prepared > 0) scatter.Execute();

                    for (int i = chunkStart; i < chunkEnd; i++)
                    {
                        var actorPtr = actorPtrs[i];
                        if (!actorPtr.IsValidVirtualAddress())
                        {
                            // Array is sparse — slot reuse without shrink.
                            skippedReadError++;
                            continue;
                        }
                        if (sampleActor == 0) sampleActor = actorPtr;

                        if (!scatter.ReadValue<ushort>(
                                actorPtr + PhysXOffsets.PxBase_ConcreteType, out var typeRaw))
                        {
                            skippedReadError++;
                            continue;
                        }
                        if (sampleType == 0) sampleType = typeRaw;
                        var concrete = (PxConcreteType)typeRaw;
                        if (!concrete.IsRigidActor())
                        {
                            skippedNonRigid++;
                            continue;
                        }

                        if (!scatter.ReadValue<ushort>(
                                actorPtr + PhysXOffsets.PxRigidActor_ShapeManager
                                         + PhysXOffsets.NpShapeManager_ShapesCount,
                                out var shapeCount)
                            || shapeCount == 0)
                        {
                            skippedZeroShapes++;
                            continue;
                        }

                        if (shapeCount > 1) multiShapeActors++;
                        if (firstShapedActor == 0) firstShapedActor = actorPtr;
                        survivors.Add((i, actorPtr, concrete, shapeCount));
                    }
                }

                if (++prefilterChunks % PrefilterProgressChunks == 0)
                {
                    long elapsed = Environment.TickCount64 - prefilterStartMs;
                    Log.WriteLine(
                        $"[SceneCache] Prefilter: chunk {prefilterChunks}, " +
                        $"index {chunkEnd}/{actorPtrs.Length}, survivors={survivors.Count}, " +
                        $"elapsed={elapsed}ms");
                }
            }

            Log.WriteLine(
                $"[SceneCache] Prefilter done: {survivors.Count} survivors of " +
                $"{actorPtrs.Length} slots (read-error={skippedReadError}, " +
                $"non-rigid={skippedNonRigid}, zero-shapes={skippedZeroShapes}) " +
                $"in {Environment.TickCount64 - prefilterStartMs}ms");

            // ── Phase 2: scatter-batched ingest on survivors ────────────────
            //
            // The bulk of per-actor cost is ~10 serial Memory.TryReadValue
            // calls walking the (actor → shape → core → filter / flags /
            // geom type / native collider → GameObject → layer + name ptr)
            // chain. For 50k+ survivors that's ~500k round-trips → minutes.
            // We restructure into chunked scatter passes that fetch the same
            // data in 4 batched passes per chunk (~256 actors), collapsing
            // the round-trip count by ~1000×. Per-actor synthesis (validation
            // + worldXform compose + geometry-specific mesh ingest) runs
            // locally from the prefetched buffers; the mesh-ingest path itself
            // is dedup'd by source pointer so cold-mesh DMA dominates that
            // tier rather than per-actor cost.

            const int IngestChunkSize         = 256;
            const int IngestProgressChunks    = 16;  // log every 16 chunks ≈ 4k actors
            const int NameBytes               = 96;  // max bytes per actor name read (variable, null-terminated)

            // Per-chunk reusable buffers — sized to IngestChunkSize and
            // re-allocated on the rare last under-full chunk.
            ulong[] shapePtrsBuf = new ulong[IngestChunkSize];
            ulong[] colliderPtrsBuf = new ulong[IngestChunkSize];
            ulong[] gameObjectPtrsBuf = new ulong[IngestChunkSize];

            long ingestStartMs = Environment.TickCount64;
            int ingestChunks = 0;

            for (int chunkStart = 0; chunkStart < survivors.Count; chunkStart += IngestChunkSize)
            {
                int chunkEnd = Math.Min(chunkStart + IngestChunkSize, survivors.Count);
                int chunkLen = chunkEnd - chunkStart;

                // Re-allocate if the last chunk is smaller than the buffer
                // (Clear is cheaper than re-allocating, but we don't trust the
                // previous chunk's tail entries to be uninitialised values we
                // can distinguish from real-but-failed reads).
                if (shapePtrsBuf.Length < chunkLen)
                {
                    shapePtrsBuf      = new ulong[chunkLen];
                    colliderPtrsBuf   = new ulong[chunkLen];
                    gameObjectPtrsBuf = new ulong[chunkLen];
                }
                Array.Clear(shapePtrsBuf,      0, chunkLen);
                Array.Clear(colliderPtrsBuf,   0, chunkLen);
                Array.Clear(gameObjectPtrsBuf, 0, chunkLen);

                // ── Pass 1: actor pose + shape-manager ShapesSingle ─────────
                using (var scatter = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int li = 0; li < chunkLen; li++)
                    {
                        var sv = survivors[chunkStart + li];
                        uint poseOff = sv.Type == PxConcreteType.RigidDynamic
                            ? PhysXOffsets.NpRigidDynamic_BufferedBody2World
                            : PhysXOffsets.NpRigidStatic_BodyToWorld;
                        scatter.PrepareReadValue<PxTransform>(sv.ActorPtr + poseOff);
                        scatter.PrepareReadValue<ulong>(
                            sv.ActorPtr + PhysXOffsets.PxRigidActor_ShapeManager
                                        + PhysXOffsets.NpShapeManager_ShapesSingle);
                    }
                    scatter.Execute();
                    for (int li = 0; li < chunkLen; li++)
                    {
                        var sv = survivors[chunkStart + li];
                        if (!scatter.ReadValue<ulong>(
                                sv.ActorPtr + PhysXOffsets.PxRigidActor_ShapeManager
                                            + PhysXOffsets.NpShapeManager_ShapesSingle,
                                out var singleVal))
                            singleVal = 0;
                        // For count==1 the ShapesSingle slot IS the shape ptr.
                        // For count>1 it's a pointer to an array; need an extra
                        // deref in Pass 1b. Most actors are count==1 so the
                        // fast path covers the bulk of the cost.
                        shapePtrsBuf[li] = sv.ShapeCount == 1 ? singleVal : 0;
                    }
                }

                // ── Pass 1b: dereference shape ptr for count>1 actors ───────
                // These are infrequent (Arena had ~12 of 10k; EFT main appears
                // similar). Batched anyway so a map with many doesn't slow us.
                using (var scatter = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    bool any = false;
                    for (int li = 0; li < chunkLen; li++)
                    {
                        var sv = survivors[chunkStart + li];
                        if (sv.ShapeCount <= 1) continue;
                        if (!Memory.TryReadPtr(
                                sv.ActorPtr + PhysXOffsets.PxRigidActor_ShapeManager
                                            + PhysXOffsets.NpShapeManager_ShapesSingle,
                                out var arrPtr, false)
                            || !arrPtr.IsValidVirtualAddress())
                            continue;
                        scatter.PrepareReadValue<ulong>(arrPtr);
                        any = true;
                    }
                    if (any)
                    {
                        scatter.Execute();
                        for (int li = 0; li < chunkLen; li++)
                        {
                            var sv = survivors[chunkStart + li];
                            if (sv.ShapeCount <= 1) continue;
                            if (!Memory.TryReadPtr(
                                    sv.ActorPtr + PhysXOffsets.PxRigidActor_ShapeManager
                                                + PhysXOffsets.NpShapeManager_ShapesSingle,
                                    out var arrPtr, false)
                                || !arrPtr.IsValidVirtualAddress())
                                continue;
                            if (scatter.ReadValue<ulong>(arrPtr, out var firstShape))
                                shapePtrsBuf[li] = firstShape;
                        }
                    }
                }

                // ── Pass 2: shape fields ────────────────────────────────────
                // Per shape: ShapeFlags + LocalPose + filter word0/word1 +
                // geometry type tag + NpShape→NativeCollider ptr.
                using (var scatter = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    int prepared = 0;
                    for (int li = 0; li < chunkLen; li++)
                    {
                        ulong shapePtr = shapePtrsBuf[li];
                        if (!shapePtr.IsValidVirtualAddress()) continue;
                        ulong coreVa = shapePtr + PhysXOffsets.NpShape_PxShapeCoreOffset;
                        scatter.PrepareReadValue<byte>(shapePtr + PhysXOffsets.NpShape_ShapeFlags);
                        scatter.PrepareReadValue<PxTransform>(coreVa + PhysXOffsets.PxShapeCore_LocalPose);
                        scatter.PrepareReadValue<uint>(
                            coreVa + PhysXOffsets.PxShapeCore_QueryFilterData + PhysXOffsets.FilterData_Word0);
                        scatter.PrepareReadValue<uint>(
                            coreVa + PhysXOffsets.PxShapeCore_QueryFilterData + PhysXOffsets.FilterData_Word1);
                        scatter.PrepareReadValue<int>(
                            coreVa + PhysXOffsets.PxShapeCore_Geometry + PhysXOffsets.PxGeometry_TypeTag);
                        scatter.PrepareReadValue<ulong>(shapePtr + PhysXOffsets.NpShape_NativeCollider);
                        prepared++;
                    }
                    // Empty scatter -> VMMDLL_Scatter_Execute returns false and
                    // throws; skip when nothing valid was prepared (whole chunk
                    // had no valid shapes).
                    if (prepared > 0) scatter.Execute();
                    for (int li = 0; li < chunkLen; li++)
                    {
                        ulong shapePtr = shapePtrsBuf[li];
                        if (!shapePtr.IsValidVirtualAddress()) continue;
                        scatter.ReadValue<ulong>(shapePtr + PhysXOffsets.NpShape_NativeCollider, out var collider);
                        colliderPtrsBuf[li] = collider;
                    }
                }

                // ── Pass 3: GameObject ptr from NativeCollider ──────────────
                using (var scatter = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    int prepared = 0;
                    for (int li = 0; li < chunkLen; li++)
                    {
                        ulong collider = colliderPtrsBuf[li];
                        if (!collider.IsValidVirtualAddress()) continue;
                        scatter.PrepareReadValue<ulong>(collider + PhysXOffsets.NativeCollider_GameObject);
                        prepared++;
                    }
                    // Empty scatter -> Execute throws; skip when no actor in this
                    // chunk had a valid collider (e.g. null PhysX userData).
                    if (prepared > 0) scatter.Execute();
                    for (int li = 0; li < chunkLen; li++)
                    {
                        ulong collider = colliderPtrsBuf[li];
                        if (!collider.IsValidVirtualAddress()) continue;
                        scatter.ReadValue<ulong>(collider + PhysXOffsets.NativeCollider_GameObject, out var go);
                        gameObjectPtrsBuf[li] = go;
                    }
                }

                // ── Pass 4: layer + name pointer from GameObject ────────────
                // The name STRING itself is variable-length and resolved
                // serially per actor in the synthesis loop; batching it would
                // require a fixed-size read which is fine for most names but
                // would also pull bytes past the null terminator. Keeping
                // names serial is the right trade-off.
                using (var scatter = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    int prepared = 0;
                    for (int li = 0; li < chunkLen; li++)
                    {
                        ulong go = gameObjectPtrsBuf[li];
                        if (!go.IsValidVirtualAddress()) continue;
                        scatter.PrepareReadValue<int>(go + PhysXOffsets.NativeGameObject_Layer);
                        scatter.PrepareReadValue<ulong>(go + PhysXOffsets.NativeGameObject_NamePtr);
                        prepared++;
                    }
                    // Empty scatter -> Execute throws; skip when no actor in this
                    // chunk resolved a GameObject. The synthesis loop below still
                    // runs (its scatter.ReadValue calls are gated on a valid
                    // GameObject ptr, so they're simply not reached here).
                    if (prepared > 0) scatter.Execute();
                    // Results read inline in the synthesis loop below to keep
                    // per-actor state local.

                    // ── Synthesis: per-actor build using prefetched data ────
                    for (int li = 0; li < chunkLen; li++)
                    {
                        var sv = survivors[chunkStart + li];
                        var actorPtr = sv.ActorPtr;
                        var concrete = sv.Type;

                        // Diagnostic sampling — keep first shape ptr per stride
                        // for the post-walk SHAPE-COMPARE dump.
                        if (sampledShapePtrs.Count < MaxShapeSamples
                            && (shapedActorsSeen % ShapeSampleStride) == 0
                            && shapePtrsBuf[li].IsValidVirtualAddress())
                        {
                            sampledShapePtrs.Add(shapePtrsBuf[li]);
                        }
                        shapedActorsSeen++;

                        ulong shapePtr = shapePtrsBuf[li];
                        if (!shapePtr.IsValidVirtualAddress())
                        {
                            stepCounts[(int)BuildActorResult.NoShape]++;
                            continue;
                        }
                        ulong coreVa = shapePtr + PhysXOffsets.NpShape_PxShapeCoreOffset;

                        // Re-fetch from scatter buffers (cheap — scatter result
                        // dict lookups, no DMA). Some reads can fail; we mimic
                        // the original TryBuildActor's behaviour for each.

                        // Pass 1: actor pose
                        uint poseOff = concrete == PxConcreteType.RigidDynamic
                            ? PhysXOffsets.NpRigidDynamic_BufferedBody2World
                            : PhysXOffsets.NpRigidStatic_BodyToWorld;
                        // Can't reach Pass 1's scatter handle anymore — re-read
                        // serially. (We could carry the result through; for
                        // legibility and uniformity with the missing reads, a
                        // single serial fetch here keeps the synthesis pure.)
                        if (!Memory.TryReadValue<PxTransform>(actorPtr + poseOff, out var actorPose, false)
                            || !actorPose.IsFinite)
                        {
                            stepCounts[(int)BuildActorResult.ActorPoseFail]++;
                            continue;
                        }

                        // Pass 2 reads (from this method's scatter scope is gone
                        // — we exited the using block above to inline synthesis
                        // inside Pass 4's using block, so the scatter handle
                        // we still have IS the Pass 4 one). Pass 2's data was
                        // captured-only for collider; the rest re-reads here.
                        if (!Memory.TryReadValue<byte>(shapePtr + PhysXOffsets.NpShape_ShapeFlags, out var flagsRaw, false))
                            flagsRaw = 0; // fall through like original
                        else
                        {
                            var flags = (PxShapeFlag)flagsRaw;
                            bool isQueryable = (flags & PxShapeFlag.SceneQueryShape) != 0;
                            bool isTrigger   = (flags & PxShapeFlag.TriggerShape)    != 0;
                            if (!isQueryable || isTrigger)
                            {
                                stepCounts[(int)BuildActorResult.TriggerOrNonQueryShape]++;
                                continue;
                            }
                        }

                        if (!Memory.TryReadValue<int>(
                                coreVa + PhysXOffsets.PxShapeCore_Geometry + PhysXOffsets.PxGeometry_TypeTag,
                                out var geomTypeRaw, false))
                        {
                            stepCounts[(int)BuildActorResult.GeomTypeReadFail]++;
                            continue;
                        }
                        var geomType = (PxGeometryType)geomTypeRaw;
                        if (!geomType.IsRaycastable() && geomType != PxGeometryType.Box && geomType != PxGeometryType.Capsule)
                        {
                            stepCounts[(int)BuildActorResult.GeomTypeUnsupported]++;
                            continue;
                        }

                        if (!Memory.TryReadValue<PxTransform>(coreVa + PhysXOffsets.PxShapeCore_LocalPose, out var shapeLocal, false)
                            || !shapeLocal.IsFinite)
                        {
                            stepCounts[(int)BuildActorResult.ShapeLocalFail]++;
                            continue;
                        }
                        var worldXform = PxTransform.Multiply(actorPose, shapeLocal);
                        if (!worldXform.IsFinite || !worldXform.IsRotationUnit)
                        {
                            stepCounts[(int)BuildActorResult.WorldXformInvalid]++;
                            continue;
                        }

                        Memory.TryReadValue<uint>(
                            coreVa + PhysXOffsets.PxShapeCore_QueryFilterData + PhysXOffsets.FilterData_Word0,
                            out var shapeGroupMask, false);
                        Memory.TryReadValue<uint>(
                            coreVa + PhysXOffsets.PxShapeCore_QueryFilterData + PhysXOffsets.FilterData_Word1,
                            out var shapeLayerMask, false);

                        // Name/layer chain — use prefetched GameObject ptr +
                        // batched layer/name-ptr reads from Pass 4. Name
                        // string remains serial (variable length).
                        string actorName = string.Empty;
                        int unityLayer = -1;
                        ulong nativeGameObject = gameObjectPtrsBuf[li];
                        if (nativeGameObject.IsValidVirtualAddress())
                        {
                            scatter.ReadValue<int>(nativeGameObject + PhysXOffsets.NativeGameObject_Layer, out unityLayer);
                            if (scatter.ReadValue<ulong>(nativeGameObject + PhysXOffsets.NativeGameObject_NamePtr, out var namePtr)
                                && namePtr.IsValidVirtualAddress()
                                && Memory.TryReadString(namePtr, out var s, NameBytes, false)
                                && !string.IsNullOrEmpty(s))
                            {
                                actorName = s!;
                            }
                            if (_layerProbeSamples.Count < MaxLayerProbeSamples
                                && shapeLayerMask != 0
                                && (shapeLayerMask & (shapeLayerMask - 1)) == 0)
                            {
                                _layerProbeSamples.Add((nativeGameObject, shapeLayerMask));
                            }
                        }

                        // Drop EFT's movement-collision duplicates before the
                        // (expensive) mesh cook — the paired *_BALLISTIC_* shape
                        // is kept and carries the same geometry for sightlines
                        // and maps. Halves snapshot size on EFT maps.
                        if (dropColliders
                            && actorName.Length != 0
                            && actorName.Contains("_COLLIDER", StringComparison.Ordinal))
                        {
                            skippedCollider++;
                            continue;
                        }

                        // Geometry-specific ingest — Sphere/Capsule/Box use
                        // primitives we already read; mesh types defer to
                        // the original TryBuildActor's mesh-ingest path via
                        // a thin helper. The mesh ingest is dedup'd by
                        // source pointer so the first sighting of each mesh
                        // pays the cost; everything after is a dict lookup.
                        var built = BuildCachedActorFromGeom(
                            mapId, actorPtr, geomType, coreVa, worldXform,
                            shapeLayerMask, shapeGroupMask, actorName, unityLayer,
                            meshes, convexes, heightFields,
                            meshDedup, convexDedup, hfDedup,
                            out var br);
                        stepCounts[(int)br]++;
                        if (br == BuildActorResult.Ok && built is not null)
                        {
                            actors.Add(built);
                            processed++;
                        }
                    }
                }

                if (++ingestChunks % IngestProgressChunks == 0)
                {
                    long elapsedMs = Environment.TickCount64 - ingestStartMs;
                    Log.WriteLine(
                        $"[SceneCache] Ingest: chunk {ingestChunks}, " +
                        $"survivor {chunkEnd}/{survivors.Count}, " +
                        $"processed={processed}, elapsed={elapsedMs}ms");
                }
            }

            int skippedBadGeometry = 0;
            for (int i = 0; i < stepCounts.Length; i++)
                if (i != (int)BuildActorResult.Ok) skippedBadGeometry += stepCounts[i];

            LastSkippedNonRigid    = skippedNonRigid;
            LastSkippedZeroShapes  = skippedZeroShapes;
            LastSkippedReadError   = skippedReadError;
            LastSkippedBadGeometry = skippedBadGeometry;
            LastSkippedCollider    = skippedCollider;
            LastSampleActorPtr     = sampleActor;
            LastSampleActorTypeRaw = sampleType;

            Log.WriteLine(
                $"[SceneCache] Walked scene 0x{gameplayScene:X}: actors processed={processed} " +
                $"skipped(non-rigid={skippedNonRigid}, zero-shapes={skippedZeroShapes}, " +
                $"read-error={skippedReadError}, bad-geometry={skippedBadGeometry}, " +
                $"collider-dup={skippedCollider}) " +
                $"â€” meshes={meshes.Count} convex={convexes.Count} hfs={heightFields.Count} " +
                $"multi-shape={multiShapeActors}");

            // Layer histogram â€” counts processed actors by Unity layer (word1 one-hot).
            // Multi-bit / zero values get bucketed as "other". Top 10 by count printed
            // so the user can see the layer distribution at a glance and decide
            // which layers to filter as see-through props.
            if (processed > 0)
            {
                var layerHist = new Dictionary<int, int>(16);
                int multiBitOrZero = 0;
                foreach (var a in actors)
                {
                    uint m = a.ShapeLayerMask;
                    if (m == 0 || (m & (m - 1)) != 0) { multiBitOrZero++; continue; }
                    int idx = System.Numerics.BitOperations.Log2(m);
                    layerHist[idx] = layerHist.GetValueOrDefault(idx, 0) + 1;
                }
                var topLayers = layerHist
                    .OrderByDescending(kv => kv.Value)
                    .Take(10)
                    .Select(kv => $"idx{kv.Key}={kv.Value}");
                var hist = string.Join(" ", topLayers);
                if (multiBitOrZero > 0) hist += $" other={multiBitOrZero}";
                Log.WriteLine($"[SceneCache] Layer histogram: {hist}");

                // Per-actor name histogram from Marcel's chain
                // (NpShapeâ†’NativeColliderâ†’NativeGameObject). Gives us human-readable
                // identifiers per actor â€” strictly more useful than per-layer category
                // names from TagManager (which we couldn't reach in arena anyway).
                int withName = 0;
                var nameHist = new Dictionary<string, int>(64, StringComparer.Ordinal);
                foreach (var a in actors)
                {
                    if (string.IsNullOrEmpty(a.Name)) continue;
                    withName++;
                    nameHist[a.Name] = nameHist.GetValueOrDefault(a.Name, 0) + 1;
                }
                if (withName > 0)
                {
                    int distinct = nameHist.Count;
                    var topNames = nameHist
                        .OrderByDescending(kv => kv.Value)
                        .Take(8)
                        .Select(kv => $"\"{Truncate(kv.Key, 32)}\"Ã—{kv.Value}");
                    Log.WriteLine(
                        $"[SceneCache] Actor names: {withName}/{actors.Count} with name, " +
                        $"{distinct} distinct, top: {string.Join(" ", topNames)}");
                }
                else
                {
                    Log.WriteLine("[SceneCache] Actor names: none read (chain may have wrong offsets for this build)");
                }

                // Cross-check: every actor's UnityLayer (from the chain) should
                // match log2(ShapeLayerMask) (from PxFilterData.word1). Divergence
                // is interesting â€” either Marcel's offsets are wrong, our
                // filter-data reading is wrong, or there's a class of actor where
                // the shape's filter doesn't match the GameObject's layer.
                int crossChecked = 0, layersAgree = 0, layersDisagree = 0;
                foreach (var a in actors)
                {
                    if (a.UnityLayer < 0) continue;
                    uint m = a.ShapeLayerMask;
                    if (m == 0 || (m & (m - 1)) != 0) continue; // non-one-hot â€” skip
                    crossChecked++;
                    int idxFromMask = System.Numerics.BitOperations.Log2(m);
                    if (idxFromMask == a.UnityLayer) layersAgree++;
                    else layersDisagree++;
                }
                if (crossChecked > 0)
                {
                    Log.WriteLine(
                        $"[SceneCache] Layer cross-check: {layersAgree}/{crossChecked} actors agree " +
                        $"(UnityLayer vs log2(ShapeLayerMask))" +
                        (layersDisagree > 0 ? $", {layersDisagree} disagree" : ""));
                }

                // See-through breakdown â€” how many actors does the classifier
                // (Phase 1 V1: layer mask + name patterns) treat as
                // transparent, and by which rule. Helps the user dial in the
                // see-through patterns without having to manually correlate
                // name + layer histograms.
                int seeThroughTotal = 0;
                int seeThroughByLayer = 0;
                int seeThroughByGlobalName = 0;
                int seeThroughByMapName = 0;
                uint seeThroughLayerMask = Raycaster.SeeThroughLayerMask;
                var globalPatterns = VisibilityClassifier.GlobalNamePatterns;
                var mapPatterns    = VisibilityClassifier.GetMapPatterns(mapId);
                foreach (var a in actors)
                {
                    if (!a.IsSeeThrough) continue;
                    seeThroughTotal++;
                    if (seeThroughLayerMask != 0
                        && (a.ShapeLayerMask & seeThroughLayerMask) != 0)
                        seeThroughByLayer++;
                    if (!string.IsNullOrEmpty(a.Name))
                    {
                        if (HasSubstringMatch(a.Name, globalPatterns)) seeThroughByGlobalName++;
                        if (HasSubstringMatch(a.Name, mapPatterns))    seeThroughByMapName++;
                    }
                }
                // Per-rule counts overlap (an actor that matches both layer
                // and a name pattern is counted in both columns), so they
                // sum to â‰¥ seeThroughTotal â€” by design, so the user can see
                // each rule's pull independently.
                Log.WriteLine(
                    $"[SceneCache] See-through filter: {seeThroughTotal}/{actors.Count} actors " +
                    $"(layer-mask 0x{seeThroughLayerMask:X}â†’{seeThroughByLayer}, " +
                    $"global-names [{string.Join(",", globalPatterns)}]â†’{seeThroughByGlobalName}, " +
                    $"map-names ({mapPatterns.Length})â†’{seeThroughByMapName})");

                // Loud guard: if > 90 % of actors got filtered out as see-through,
                // vischeck is effectively disabled â€” the user almost certainly
                // didn't mean to configure that. Surface it as a WARNING so it's
                // visible in the console even when buried in normal build output.
                if (actors.Count > 0)
                {
                    double seeThruPct = 100.0 * seeThroughTotal / actors.Count;
                    if (seeThruPct >= 90.0)
                    {
                        Log.Write(AppLogLevel.Warning,
                            $"[SceneCache] {seeThruPct:F0}% of actors are see-through â€” vischeck will rarely block. " +
                            $"Likely cause: SeeThroughLayerMask=0x{seeThroughLayerMask:X} is too broad. " +
                            $"Open VisCheck Debug (F11) â†’ Classifier Rules â†’ Reset.");
                    }
                }

                // Sample of see-through actors so wrongly-filtered colliders
                // (e.g. an opaque "GlassCabinet" that matched the global "Glass"
                // substring) show up in the log. Even-stride pick across the
                // see-through subset gives a representative spread without RNG.
                if (seeThroughTotal > 0)
                {
                    const int SampleSize = 10;
                    int taken = 0;
                    int step = Math.Max(1, seeThroughTotal / SampleSize);
                    int hitIndex = 0;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"[SceneCache] See-through sample ({Math.Min(SampleSize, seeThroughTotal)} of {seeThroughTotal} â€” inspect for wrongly-filtered colliders):");
                    foreach (var a in actors)
                    {
                        if (!a.IsSeeThrough) continue;
                        if (hitIndex++ % step != 0) continue;
                        if (taken++ >= SampleSize) break;
                        string nm = string.IsNullOrEmpty(a.Name) ? "(no name)"
                            : a.Name.Length > 48 ? a.Name.Substring(0, 47) + "â€¦" : a.Name;
                        string why = VisibilityClassifier.Explain(mapId, a.ShapeLayerMask, a.Name);
                        sb.AppendLine($"  \"{nm}\" type={a.GeometryType} layer={a.UnityLayer} â†’ {why}");
                    }
                    Log.WriteLine(sb.ToString().TrimEnd());
                }

                // Layer-offset probe â€” safety net for future Unity layout
                // shifts. Sweeps candidate offsets in NativeGameObject's
                // header region and scores each by how often the read int32
                // matches log2(ShapeLayerMask). If the winner matches the
                // committed PhysXOffsets.NativeGameObject_Layer with > 80 %
                // confidence the probe stays quiet; otherwise it logs a
                // warning + the top scores so the user knows to update.
                if (_layerProbeSamples.Count > 0)
                {
                    uint[] candidateOffsets =
                    {
                        0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C,
                        0x60, 0x64, 0x6C, 0x70, 0x74, 0x78, 0x7C, 0x80,
                    };
                    int samples = _layerProbeSamples.Count;
                    int bestOffset = -1, bestMatches = 0;
                    var scores = new List<(uint Off, int Match)>();

                    foreach (uint off in candidateOffsets)
                    {
                        int match = 0;
                        for (int i = 0; i < samples; i++)
                        {
                            var (go, mask) = _layerProbeSamples[i];
                            if (!Memory.TryReadValue<int>(go + off, out var candidateLayer, false))
                                continue;
                            if (candidateLayer < 0 || candidateLayer > 31) continue;
                            int expected = System.Numerics.BitOperations.Log2(mask);
                            if (candidateLayer == expected) match++;
                        }
                        scores.Add((off, match));
                        if (match > bestMatches) { bestMatches = match; bestOffset = (int)off; }
                    }

                    // Quiet when the probe confirms the committed offset with
                    // high confidence â€” every build doesn't need to repeat
                    // "the offset is still correct". Loud only on disagreement.
                    bool agrees = bestOffset == PhysXOffsets.NativeGameObject_Layer
                                  && bestMatches >= (samples * 4 / 5); // 80 %
                    if (!agrees)
                    {
                        var top = scores.OrderByDescending(s => s.Match).Take(3)
                            .Select(s => $"+0x{s.Off:X2}={s.Match}/{samples}");
                        Log.WriteLine(
                            $"[SceneCache] Layer-offset probe DISAGREES with committed " +
                            $"0x{PhysXOffsets.NativeGameObject_Layer:X2} " +
                            $"({samples} samples) â€” top: {string.Join(" ", top)}" +
                            (bestOffset >= 0 && bestMatches > samples / 2
                                ? $" â†’ update PhysXOffsets.NativeGameObject_Layer to 0x{bestOffset:X2}"
                                : " â†’ no clear winner either; layer offset reading is broken"));
                    }
                }
            }

            // Multi-shape comparison: read N NpShape memory blocks side-by-side
            // and print u32 per offset. Looking for the offset where values look
            // like Unity one-hot layer masks (1, 2, 4, 8, ... 0x80000000) â€” that's
            // simulationFilterData.word0 = the shape's Unity layer.
            if (sampledShapePtrs.Count >= 2)
                DumpShapeComparison(sampledShapePtrs);

            // Per-step breakdown of the bad-geometry bucket â€” pinpoints which offset
            // in PhysXOffsets is wrong (the step where the chain breaks).
            if (skippedBadGeometry > 0)
            {
                Log.WriteLine(
                    $"[SceneCache] bad-geometry breakdown: " +
                    $"no-shape={stepCounts[(int)BuildActorResult.NoShape]} " +
                    $"trigger/non-query={stepCounts[(int)BuildActorResult.TriggerOrNonQueryShape]} " +
                    $"geom-type-read={stepCounts[(int)BuildActorResult.GeomTypeReadFail]} " +
                    $"geom-type-unsup={stepCounts[(int)BuildActorResult.GeomTypeUnsupported]} " +
                    $"actor-pose={stepCounts[(int)BuildActorResult.ActorPoseFail]} " +
                    $"shape-local={stepCounts[(int)BuildActorResult.ShapeLocalFail]} " +
                    $"world-xform={stepCounts[(int)BuildActorResult.WorldXformInvalid]} " +
                    $"geom-data={stepCounts[(int)BuildActorResult.GeomDataFail]} " +
                    $"oversized-trigger={stepCounts[(int)BuildActorResult.OversizedTrigger]}");
            }

            // If nothing made it through, probe actors at three spread-out indices to
            // distinguish "all actors genuinely have zero shapes" (wrong scene) from
            // "something else is broken" (offset or read issue), then hex-dump a
            // SHAPED actor â€” the one whose layout actually needs validating against
            // IDA. Falls back to actor[0] only if no shaped actor was ever found.
            if (processed == 0 && actorPtrs.Length > 0)
            {
                Log.WriteLine(
                    $"[SceneCache] DIAG zero actors processed â€” " +
                    $"non-rigid={skippedNonRigid} zero-shapes={skippedZeroShapes} " +
                    $"read-error={skippedReadError} bad-geometry={skippedBadGeometry}");

                int n = actorPtrs.Length;
                int[] probeIdx = n >= 5001
                    ? new[] { 0, 1000, 5000 }
                    : new[] { 0, n / 2, Math.Max(0, n - 1) };

                foreach (var idx in probeIdx.Distinct())
                {
                    if (idx >= actorPtrs.Length) continue;
                    var probe = actorPtrs[idx];
                    if (!probe.IsValidVirtualAddress()) continue;
                    Memory.TryReadValue<ushort>(probe + PhysXOffsets.PxBase_ConcreteType, out var t, false);
                    Memory.TryReadValue<ushort>(
                        probe + PhysXOffsets.PxRigidActor_ShapeManager + PhysXOffsets.NpShapeManager_ShapesCount,
                        out var sc, false);
                    Log.WriteLine($"[SceneCache] DIAG actor[{idx}] 0x{probe:X}: type=0x{t:X4} shapeCount={sc}");
                }

                // Full 512-byte hex dump of the first shaped actor. This is the one
                // whose shape pointer, shape-core pointer, and geometry offsets we
                // actually need to validate â€” actor[0] often has zero shapes and
                // tells us nothing useful about the downstream offset chain.
                if (firstShapedActor != 0)
                {
                    Memory.TryReadValue<ushort>(firstShapedActor + PhysXOffsets.PxBase_ConcreteType, out var shapedType, false);
                    Log.WriteLine($"[SceneCache] DIAG hex dump of first SHAPED actor 0x{firstShapedActor:X} (type=0x{shapedType:X4}):");
                    DumpActorDiagnostics(firstShapedActor, shapedType);

                    // Also follow the shape pointer and dump the NpShape memory so we
                    // can locate the embedded PxShapeCore (PhysX 4.1 stores mCore inline,
                    // not behind a pointer). Auto-scan flags any 4-byte-aligned offset
                    // whose content looks like {unit quaternion, finite Vec3, geomType âˆˆ [0,6]}.
                    if (Memory.TryReadPtr(
                            firstShapedActor + PhysXOffsets.PxRigidActor_ShapeManager + PhysXOffsets.NpShapeManager_ShapesSingle,
                            out var shapePtr, false)
                        && shapePtr.IsValidVirtualAddress())
                    {
                        Log.WriteLine($"[SceneCache] DIAG NpShape pointer from actor+0x{PhysXOffsets.PxRigidActor_ShapeManager:X}: 0x{shapePtr:X}");
                        DumpShapeDiagnostics(shapePtr);
                    }
                }
                else
                {
                    // No actor passed the shape pre-check â€” dump actor[0] as a last resort.
                    Log.WriteLine("[SceneCache] DIAG no shaped actor found â€” falling back to actor[0]");
                    DumpActorDiagnostics(actorPtrs[0], sampleType);
                }
            }

            return new SceneSnapshot
            {
                Actors           = actors.ToArray(),
                Meshes           = meshes.ToArray(),
                ConvexMeshes     = convexes.ToArray(),
                HeightFields     = heightFields.ToArray(),
                BuildTickMs      = Environment.TickCount64,
                MapId            = mapId,
                NpPhysics        = npPhysics,
                SourceActorCount = (int)gameplayActorCount,
            };
        }

        /// <summary>
        /// Per-step outcome of <see cref="TryBuildActor"/>. The caller uses these
        /// to attribute the "bad geometry" bucket to a specific failing step,
        /// which is what tells us which offset in <see cref="PhysXOffsets"/> is
        /// wrong when the build comes back empty.
        /// </summary>
        private enum BuildActorResult
        {
            Ok,
            NoShape,
            GeomTypeReadFail,
            GeomTypeUnsupported,
            ActorPoseFail,
            ShapeLocalFail,
            WorldXformInvalid,
            GeomDataFail,
            /// <summary>
            /// Geometry is too large to plausibly be a real wall/structure â€”
            /// almost certainly a trigger/world-bounds volume that would cause
            /// false-positive visibility blocks. Filtered at cache build time.
            /// </summary>
            OversizedTrigger,
            /// <summary>
            /// Shape's PxShapeFlags indicate a trigger volume or a shape not
            /// participating in scene queries â€” PhysX's own raycast would skip
            /// these. Catches railings, foliage, and gameplay-only triggers
            /// that aren't filtered by size alone.
            /// </summary>
            TriggerOrNonQueryShape,
        }

        /// <summary>
        /// Maximum half-extent (metres) for a Box geometry to be treated as real
        /// geometry. Boxes larger than this are almost always map-boundary
        /// triggers, kill zones, or out-of-bounds volumes â€” they engulf the
        /// player's position and would block every ray. Arena maps' real walls
        /// top out around 25 m tall Ã— 25 m long.
        /// </summary>
        private const float MaxRealBoxHalfExtent = 50f;

        /// <summary>
        /// Maximum radius (metres) for a Sphere geometry to be treated as real.
        /// Larger spheres are audio / fog / lighting triggers â€” Bay5 had three
        /// 30 m radius spheres scattered around the map blocking everything.
        /// Real spherical physics objects in shooters are barrels, balls,
        /// canisters â€” all under 2 m radius.
        /// </summary>
        private const float MaxRealSphereRadius = 10f;

        /// <summary>
        /// Per-axis maximum (radius or half-height) for a Capsule to be real
        /// geometry. Game-world capsules are barrels, pillars, low-cover â€” all
        /// well under 5 m on any dimension. Larger capsules tend to be NPC
        /// detection cones or audio occlusion volumes.
        /// </summary>
        private const float MaxRealCapsuleDimension = 10f;

        /// <summary>
        /// Maximum linear dimension (metres) of a TriangleMesh's local AABB
        /// before we treat it as map-spanning terrain rather than a
        /// blocking-vs-vis wall. Verified by live diagnostic: Arena_Prison's
        /// terrain mesh is 345 Ã— 24 Ã— 440 m and engulfs the playable area â€”
        /// every ray crosses its bounding volume and gets blocked by an actual
        /// ground triangle. The biggest legitimate building meshes top out
        /// around 35 m on a side, so 100 m is a wide safety margin.
        /// </summary>
        private const float MaxRealMeshLinearExtent = 100f;

        /// <summary>
        /// Reads one actor's first shape, classifies its geometry, and assembles
        /// a <see cref="CachedActor"/> with the baked world transform and AABB.
        /// Returns a step-attributed reason for any failure (the caller buckets
        /// these into named counters for the zero-processed diagnostic).
        /// </summary>
        private static BuildActorResult TryBuildActor(
            string mapId,
            ulong actorPtr, PxConcreteType concrete,
            List<CachedTriMesh> meshes, List<CachedConvexMesh> convexes,
            List<CachedHeightField> heightFields,
            Dictionary<ulong, int> meshDedup, Dictionary<ulong, int> convexDedup,
            Dictionary<ulong, int> hfDedup,
            out CachedActor built)
        {
            built = default!;

            // Find a shape manager that yields a non-zero shape count.
            if (!TryReadShape(actorPtr, out var shapePtr, out var shapeCoreVa))
                return BuildActorResult.NoShape;

            // Filter on PxShapeFlags: keep shapes that PhysX itself would hit
            // in a raycast â€” those with eSCENE_QUERY_SHAPE set and eTRIGGER_SHAPE
            // clear. This catches trigger volumes and "ghost" shapes regardless
            // of size, which is the principled fix for railings / signposts /
            // gameplay triggers (the kind of false positives the size filter
            // can't address).
            if (Memory.TryReadValue<byte>(shapePtr + PhysXOffsets.NpShape_ShapeFlags, out var flagsRaw, false))
            {
                var flags = (PxShapeFlag)flagsRaw;
                bool isQueryable = (flags & PxShapeFlag.SceneQueryShape) != 0;
                bool isTrigger   = (flags & PxShapeFlag.TriggerShape)    != 0;
                if (!isQueryable || isTrigger)
                    return BuildActorResult.TriggerOrNonQueryShape;
            }
            // If the flags read fails, fall through â€” we'd rather have a
            // possibly-spurious actor than drop a real one.

            // Read geometry header to know what kind we're dealing with.
            if (!Memory.TryReadValue<int>(shapeCoreVa + PhysXOffsets.PxShapeCore_Geometry + PhysXOffsets.PxGeometry_TypeTag,
                                          out var geomTypeRaw, false))
                return BuildActorResult.GeomTypeReadFail;
            var geomType = (PxGeometryType)geomTypeRaw;
            if (!geomType.IsRaycastable() && geomType != PxGeometryType.Box && geomType != PxGeometryType.Capsule)
                return BuildActorResult.GeomTypeUnsupported;

            // World transform = actor's body-to-world Ã— shape's local pose.
            if (!TryReadActorPose(actorPtr, concrete, out var actorPose) || !actorPose.IsFinite)
                return BuildActorResult.ActorPoseFail;
            if (!Memory.TryReadValue<PxTransform>(shapeCoreVa + PhysXOffsets.PxShapeCore_LocalPose, out var shapeLocal, false)
                || !shapeLocal.IsFinite)
                return BuildActorResult.ShapeLocalFail;
            var worldXform = PxTransform.Multiply(actorPose, shapeLocal);
            if (!worldXform.IsFinite || !worldXform.IsRotationUnit)
                return BuildActorResult.WorldXformInvalid;

            // Query filter data: word0 = collision-group bitmask (multi-bit),
            // word1 = shape's Unity layer (one-hot 1<<N). Verified by live
            // SHAPE-COMPARE: word1 column shows clean power-of-2 values across
            // shapes while word0 is multi-bit. Best-effort reads â€” if any fail
            // we still build the actor with layer=0, the raycaster doesn't
            // strictly require these.
            Memory.TryReadValue<uint>(
                shapeCoreVa + PhysXOffsets.PxShapeCore_QueryFilterData + PhysXOffsets.FilterData_Word0,
                out var shapeGroupMask, false);
            Memory.TryReadValue<uint>(
                shapeCoreVa + PhysXOffsets.PxShapeCore_QueryFilterData + PhysXOffsets.FilterData_Word1,
                out var shapeLayerMask, false);

            // Native chain â€” pull the owning GameObject's name + layer.
            // Best-effort: any step that fails just leaves Name="" / UnityLayer=-1.
            // The visibility filter doesn't need either of these; they're
            // diagnostic / UI features.
            //
            //   NpShape          + 0x10 â†’ NativeCollider*
            //   NativeCollider   + 0x58 â†’ NativeGameObject*
            //   NativeGameObject + 0x68 â†’ NamePtr (ulong â†’ C-string)
            //   NativeGameObject + ?    â†’ Layer   (int32; probed)
            //
            // Name is a NamePtr+C-string pair, NOT a std::string. Verified
            // against Unity.cs which has the same layout for managed
            // GameObjects (UnityOffsets.GO_Name = 0x68).
            //
            // Layer field offset is probed across +0x60..+0x78 at the same
            // time, so the layer-probe histogram (below in the build summary)
            // can pick the winning offset for future builds without touching
            // any visible behavior right now.
            string actorName  = string.Empty;
            int    unityLayer = -1;
            ulong  nativeGameObject = 0;
            if (Memory.TryReadPtr(shapePtr + PhysXOffsets.NpShape_NativeCollider,
                                  out var nativeCollider, false)
                && nativeCollider.IsValidVirtualAddress()
                && Memory.TryReadPtr(nativeCollider + PhysXOffsets.NativeCollider_GameObject,
                                     out nativeGameObject, false)
                && nativeGameObject.IsValidVirtualAddress())
            {
                // Layer at the current best guess. Real offset may differ;
                // the probe further down validates by histogram.
                Memory.TryReadValue<int>(nativeGameObject + PhysXOffsets.NativeGameObject_Layer,
                                         out unityLayer, false);

                // Name = *(NamePtr) interpreted as a null-terminated C string.
                if (Memory.TryReadPtr(nativeGameObject + PhysXOffsets.NativeGameObject_NamePtr,
                                      out var namePtr, false)
                    && namePtr.IsValidVirtualAddress()
                    && Memory.TryReadString(namePtr, out var s, 96, false)
                    && !string.IsNullOrEmpty(s))
                {
                    actorName = s!;
                }

                // Record the GameObject pointer + layer-mask source so the
                // outer probe can compute a layer-offset histogram once at
                // the end of the walk. Cap at MaxLayerProbeSamples â€” 256 is
                // plenty of statistical power and keeps DMA cost bounded.
                if (_layerProbeSamples.Count < MaxLayerProbeSamples
                    && shapeLayerMask != 0
                    && (shapeLayerMask & (shapeLayerMask - 1)) == 0)
                {
                    _layerProbeSamples.Add((nativeGameObject, shapeLayerMask));
                }
            }

            // Geometry-specific ingest. Each branch fills the actor fields and
            // either references an existing mesh / hf cache entry or builds a new one.
            ulong geomVa = shapeCoreVa + PhysXOffsets.PxShapeCore_Geometry;
            switch (geomType)
            {
                case PxGeometryType.Sphere:
                {
                    if (!Memory.TryReadValue<float>(geomVa + PhysXOffsets.Sphere_Radius, out var radius, false)
                        || !float.IsFinite(radius) || radius <= 0f)
                        return BuildActorResult.GeomDataFail;
                    // Drop huge spheres â€” they're audio/lighting/fog triggers.
                    if (radius > MaxRealSphereRadius)
                        return BuildActorResult.OversizedTrigger;
                    var aabbMin = worldXform.Position - new Vector3(radius);
                    var aabbMax = worldXform.Position + new Vector3(radius);
                    built = new CachedActor
                    {
                        WorldTransform   = worldXform,
                        WorldAabbMin     = aabbMin,
                        WorldAabbMax     = aabbMax,
                        GeometryType     = geomType,
                        PrimitiveSize    = new Vector3(radius, 0f, 0f),
                        ActorBase        = actorPtr,
                        ShapeLayerMask   = shapeLayerMask,
                        ShapeGroupMask   = shapeGroupMask,
                        Name             = actorName,
                        UnityLayer       = unityLayer,
                        IsSeeThrough     = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                    return BuildActorResult.Ok;
                }

                case PxGeometryType.Capsule:
                {
                    if (!Memory.TryReadValue<float>(geomVa + PhysXOffsets.Capsule_Radius, out var radius, false)
                        || !Memory.TryReadValue<float>(geomVa + PhysXOffsets.Capsule_HalfHeight, out var halfH, false)
                        || !float.IsFinite(radius) || radius <= 0f
                        || !float.IsFinite(halfH)  || halfH < 0f)
                        return BuildActorResult.GeomDataFail;
                    // Drop oversized capsules â€” typically NPC detection / audio volumes.
                    if (radius > MaxRealCapsuleDimension || halfH > MaxRealCapsuleDimension)
                        return BuildActorResult.OversizedTrigger;
                    // Conservative world AABB: a sphere of radius (halfH + radius).
                    float r = halfH + radius;
                    built = new CachedActor
                    {
                        WorldTransform   = worldXform,
                        WorldAabbMin     = worldXform.Position - new Vector3(r),
                        WorldAabbMax     = worldXform.Position + new Vector3(r),
                        GeometryType     = geomType,
                        PrimitiveSize    = new Vector3(radius, halfH, 0f),
                        ActorBase        = actorPtr,
                        ShapeLayerMask   = shapeLayerMask,
                        ShapeGroupMask   = shapeGroupMask,
                        Name             = actorName,
                        UnityLayer       = unityLayer,
                        IsSeeThrough     = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                    return BuildActorResult.Ok;
                }

                case PxGeometryType.Box:
                {
                    if (!Memory.TryReadValue<Vector3>(geomVa + PhysXOffsets.Box_HalfExtents, out var he, false)
                        || !float.IsFinite(he.X) || !float.IsFinite(he.Y) || !float.IsFinite(he.Z)
                        || he.X <= 0f || he.Y <= 0f || he.Z <= 0f)
                        return BuildActorResult.GeomDataFail;
                    // Drop world-bounds / map-boundary triggers â€” these massive boxes
                    // engulf the player and cause false-positive visibility blocks.
                    if (he.X > MaxRealBoxHalfExtent || he.Y > MaxRealBoxHalfExtent || he.Z > MaxRealBoxHalfExtent)
                        return BuildActorResult.OversizedTrigger;
                    // Conservative world AABB: rotated box bounded by its largest enclosing AABB.
                    var (aMin, aMax) = TransformOrientedAabb(worldXform, -he, he);
                    built = new CachedActor
                    {
                        WorldTransform   = worldXform,
                        WorldAabbMin     = aMin,
                        WorldAabbMax     = aMax,
                        GeometryType     = geomType,
                        PrimitiveSize    = he,
                        ActorBase        = actorPtr,
                        ShapeLayerMask   = shapeLayerMask,
                        ShapeGroupMask   = shapeGroupMask,
                        Name             = actorName,
                        UnityLayer       = unityLayer,
                        IsSeeThrough     = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                    return BuildActorResult.Ok;
                }

                case PxGeometryType.TriangleMesh:
                {
                    if (!Memory.TryReadPtr(geomVa + PhysXOffsets.TriangleMeshGeom_MeshPtr, out var meshPtr, false)
                        || !meshPtr.IsValidVirtualAddress())
                        return BuildActorResult.GeomDataFail;
                    int meshIndex;
                    if (!meshDedup.TryGetValue(meshPtr, out meshIndex))
                    {
                        if (!TryIngestTriMesh(meshPtr, out var mesh))
                            return BuildActorResult.GeomDataFail;
                        meshIndex = meshes.Count;
                        meshes.Add(mesh);
                        meshDedup[meshPtr] = meshIndex;
                    }
                    var meshRef = meshes[meshIndex];

                    // Filter map-terrain meshes (any linear dimension > MaxRealMeshLinearExtent).
                    // These are the TriangleMesh analog of the world-bounds Box triggers we
                    // already filter â€” they engulf the playable area and turn every ray into
                    // a false-positive block. Verified on Arena_Prison where the terrain
                    // mesh is 345 Ã— 24 Ã— 440 m.
                    var meshSize = meshRef.LocalAabbMax - meshRef.LocalAabbMin;
                    if (meshSize.X > MaxRealMeshLinearExtent
                        || meshSize.Y > MaxRealMeshLinearExtent
                        || meshSize.Z > MaxRealMeshLinearExtent)
                        return BuildActorResult.OversizedTrigger;

                    var (mMin, mMax) = TransformOrientedAabb(worldXform, meshRef.LocalAabbMin, meshRef.LocalAabbMax);
                    built = new CachedActor
                    {
                        WorldTransform   = worldXform,
                        WorldAabbMin     = mMin,
                        WorldAabbMax     = mMax,
                        GeometryType     = geomType,
                        MeshIndex        = meshIndex,
                        ActorBase        = actorPtr,
                        ShapeLayerMask   = shapeLayerMask,
                        ShapeGroupMask   = shapeGroupMask,
                        Name             = actorName,
                        UnityLayer       = unityLayer,
                        IsSeeThrough     = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                    return BuildActorResult.Ok;
                }

                case PxGeometryType.ConvexMesh:
                {
                    // Dump the geometry union for the first few ConvexMesh actors
                    // so we can locate the real PxConvexMesh pointer offset.
                    // Step 1 of D used 0x28 (by analogy with PxTriangleMeshGeometry)
                    // and 100 % of arena's actors fail validation â€” the actual
                    // pointer is at a different offset in PxConvexMeshGeometry.
                    DumpConvexGeomUnion(geomVa);

                    if (!Memory.TryReadPtr(geomVa + PhysXOffsets.ConvexMeshGeom_MeshPtr, out var cmPtr, false)
                        || !cmPtr.IsValidVirtualAddress()
                        || (cmPtr & 0x7) != 0)            // PhysX heap pointers are 8-byte aligned
                        return BuildActorResult.GeomDataFail;
                    int cmIndex;
                    if (!convexDedup.TryGetValue(cmPtr, out cmIndex))
                    {
                        if (!TryIngestConvexMesh(cmPtr, out var cm))
                            return BuildActorResult.GeomDataFail;
                        cmIndex = convexes.Count;
                        convexes.Add(cm);
                        convexDedup[cmPtr] = cmIndex;
                    }
                    var cmRef = convexes[cmIndex];

                    // Same oversize filter as TriangleMesh â€” large convex hulls
                    // that span the whole map are world-bounds, not real cover.
                    var cmSize = cmRef.LocalAabbMax - cmRef.LocalAabbMin;
                    if (cmSize.X > MaxRealMeshLinearExtent
                        || cmSize.Y > MaxRealMeshLinearExtent
                        || cmSize.Z > MaxRealMeshLinearExtent)
                        return BuildActorResult.OversizedTrigger;

                    var (cMin, cMax) = TransformOrientedAabb(worldXform, cmRef.LocalAabbMin, cmRef.LocalAabbMax);
                    built = new CachedActor
                    {
                        WorldTransform   = worldXform,
                        WorldAabbMin     = cMin,
                        WorldAabbMax     = cMax,
                        GeometryType     = geomType,
                        ConvexMeshIndex  = cmIndex,
                        ActorBase        = actorPtr,
                        ShapeLayerMask   = shapeLayerMask,
                        ShapeGroupMask   = shapeGroupMask,
                        Name             = actorName,
                        UnityLayer       = unityLayer,
                        IsSeeThrough     = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                    return BuildActorResult.Ok;
                }

                case PxGeometryType.HeightField:
                {
                    if (!Memory.TryReadPtr(geomVa + PhysXOffsets.HeightFieldGeom_HeightFieldPtr, out var hfPtr, false)
                        || !hfPtr.IsValidVirtualAddress())
                        return BuildActorResult.GeomDataFail;
                    if (!Memory.TryReadValue<float>(geomVa + PhysXOffsets.HeightFieldGeom_HeightScale, out var hs, false)
                        || !Memory.TryReadValue<float>(geomVa + PhysXOffsets.HeightFieldGeom_RowScale,   out var rs, false)
                        || !Memory.TryReadValue<float>(geomVa + PhysXOffsets.HeightFieldGeom_ColumnScale,out var cs, false)
                        || !float.IsFinite(hs) || !float.IsFinite(rs) || !float.IsFinite(cs))
                        return BuildActorResult.GeomDataFail;
                    int hfIndex;
                    if (!hfDedup.TryGetValue(hfPtr, out hfIndex))
                    {
                        if (!TryIngestHeightField(hfPtr, hs, rs, cs, out var hf))
                            return BuildActorResult.GeomDataFail;
                        hfIndex = heightFields.Count;
                        heightFields.Add(hf);
                        hfDedup[hfPtr] = hfIndex;
                    }
                    var hfRef = heightFields[hfIndex];
                    var (hMin, hMax) = TransformOrientedAabb(worldXform, hfRef.LocalAabbMin, hfRef.LocalAabbMax);
                    built = new CachedActor
                    {
                        WorldTransform   = worldXform,
                        WorldAabbMin     = hMin,
                        WorldAabbMax     = hMax,
                        GeometryType     = geomType,
                        HeightFieldIndex = hfIndex,
                        ActorBase        = actorPtr,
                        ShapeLayerMask   = shapeLayerMask,
                        ShapeGroupMask   = shapeGroupMask,
                        Name             = actorName,
                        UnityLayer       = unityLayer,
                        IsSeeThrough     = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                    return BuildActorResult.Ok;
                }

                default:
                    return BuildActorResult.GeomTypeUnsupported;
            }
        }

        /// <summary>
        /// Extracted geometry-switch + mesh-ingest used by the batched ingest
        /// path. Takes everything <see cref="TryBuildActor"/> would have read
        /// itself and just runs the per-geom-type assembly + mesh dedup.
        /// The remaining DMA reads (geometry-specific fields, mesh data)
        /// happen here — they're either single-value reads per actor
        /// (primitives) or one-shot per-mesh reads dedup'd through the
        /// caller's index dictionaries.
        /// </summary>
        private static CachedActor? BuildCachedActorFromGeom(
            string mapId,
            ulong actorPtr,
            PxGeometryType geomType,
            ulong shapeCoreVa,
            PxTransform worldXform,
            uint shapeLayerMask,
            uint shapeGroupMask,
            string actorName,
            int unityLayer,
            List<CachedTriMesh> meshes,
            List<CachedConvexMesh> convexes,
            List<CachedHeightField> heightFields,
            Dictionary<ulong, int> meshDedup,
            Dictionary<ulong, int> convexDedup,
            Dictionary<ulong, int> hfDedup,
            out BuildActorResult result)
        {
            ulong geomVa = shapeCoreVa + PhysXOffsets.PxShapeCore_Geometry;
            switch (geomType)
            {
                case PxGeometryType.Sphere:
                {
                    if (!Memory.TryReadValue<float>(geomVa + PhysXOffsets.Sphere_Radius, out var radius, false)
                        || !float.IsFinite(radius) || radius <= 0f)
                    { result = BuildActorResult.GeomDataFail; return null; }
                    if (radius > MaxRealSphereRadius)
                    { result = BuildActorResult.OversizedTrigger; return null; }
                    var aabbMin = worldXform.Position - new Vector3(radius);
                    var aabbMax = worldXform.Position + new Vector3(radius);
                    result = BuildActorResult.Ok;
                    return new CachedActor
                    {
                        WorldTransform = worldXform, WorldAabbMin = aabbMin, WorldAabbMax = aabbMax,
                        GeometryType = geomType, PrimitiveSize = new Vector3(radius, 0f, 0f),
                        ActorBase = actorPtr, ShapeLayerMask = shapeLayerMask, ShapeGroupMask = shapeGroupMask,
                        Name = actorName, UnityLayer = unityLayer,
                        IsSeeThrough = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                }
                case PxGeometryType.Capsule:
                {
                    if (!Memory.TryReadValue<float>(geomVa + PhysXOffsets.Capsule_Radius, out var radius, false)
                        || !Memory.TryReadValue<float>(geomVa + PhysXOffsets.Capsule_HalfHeight, out var halfH, false)
                        || !float.IsFinite(radius) || radius <= 0f
                        || !float.IsFinite(halfH)  || halfH < 0f)
                    { result = BuildActorResult.GeomDataFail; return null; }
                    if (radius > MaxRealCapsuleDimension || halfH > MaxRealCapsuleDimension)
                    { result = BuildActorResult.OversizedTrigger; return null; }
                    float r = halfH + radius;
                    result = BuildActorResult.Ok;
                    return new CachedActor
                    {
                        WorldTransform = worldXform,
                        WorldAabbMin = worldXform.Position - new Vector3(r),
                        WorldAabbMax = worldXform.Position + new Vector3(r),
                        GeometryType = geomType, PrimitiveSize = new Vector3(radius, halfH, 0f),
                        ActorBase = actorPtr, ShapeLayerMask = shapeLayerMask, ShapeGroupMask = shapeGroupMask,
                        Name = actorName, UnityLayer = unityLayer,
                        IsSeeThrough = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                }
                case PxGeometryType.Box:
                {
                    if (!Memory.TryReadValue<Vector3>(geomVa + PhysXOffsets.Box_HalfExtents, out var he, false)
                        || !float.IsFinite(he.X) || !float.IsFinite(he.Y) || !float.IsFinite(he.Z)
                        || he.X <= 0f || he.Y <= 0f || he.Z <= 0f)
                    { result = BuildActorResult.GeomDataFail; return null; }
                    if (he.X > MaxRealBoxHalfExtent || he.Y > MaxRealBoxHalfExtent || he.Z > MaxRealBoxHalfExtent)
                    { result = BuildActorResult.OversizedTrigger; return null; }
                    var (aMin, aMax) = TransformOrientedAabb(worldXform, -he, he);
                    result = BuildActorResult.Ok;
                    return new CachedActor
                    {
                        WorldTransform = worldXform, WorldAabbMin = aMin, WorldAabbMax = aMax,
                        GeometryType = geomType, PrimitiveSize = he,
                        ActorBase = actorPtr, ShapeLayerMask = shapeLayerMask, ShapeGroupMask = shapeGroupMask,
                        Name = actorName, UnityLayer = unityLayer,
                        IsSeeThrough = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                }
                case PxGeometryType.TriangleMesh:
                {
                    if (!Memory.TryReadPtr(geomVa + PhysXOffsets.TriangleMeshGeom_MeshPtr, out var meshPtr, false)
                        || !meshPtr.IsValidVirtualAddress())
                    { result = BuildActorResult.GeomDataFail; return null; }
                    if (!meshDedup.TryGetValue(meshPtr, out int meshIndex))
                    {
                        if (!TryIngestTriMesh(meshPtr, out var mesh))
                        { result = BuildActorResult.GeomDataFail; return null; }
                        meshIndex = meshes.Count; meshes.Add(mesh); meshDedup[meshPtr] = meshIndex;
                    }
                    var meshRef = meshes[meshIndex];
                    var meshSize = meshRef.LocalAabbMax - meshRef.LocalAabbMin;
                    if (meshSize.X > MaxRealMeshLinearExtent
                        || meshSize.Y > MaxRealMeshLinearExtent
                        || meshSize.Z > MaxRealMeshLinearExtent)
                    { result = BuildActorResult.OversizedTrigger; return null; }
                    var (mMin, mMax) = TransformOrientedAabb(worldXform, meshRef.LocalAabbMin, meshRef.LocalAabbMax);
                    result = BuildActorResult.Ok;
                    return new CachedActor
                    {
                        WorldTransform = worldXform, WorldAabbMin = mMin, WorldAabbMax = mMax,
                        GeometryType = geomType, MeshIndex = meshIndex,
                        ActorBase = actorPtr, ShapeLayerMask = shapeLayerMask, ShapeGroupMask = shapeGroupMask,
                        Name = actorName, UnityLayer = unityLayer,
                        IsSeeThrough = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                }
                case PxGeometryType.ConvexMesh:
                {
                    DumpConvexGeomUnion(geomVa);
                    if (!Memory.TryReadPtr(geomVa + PhysXOffsets.ConvexMeshGeom_MeshPtr, out var cmPtr, false)
                        || !cmPtr.IsValidVirtualAddress()
                        || (cmPtr & 0x7) != 0)
                    { result = BuildActorResult.GeomDataFail; return null; }
                    if (!convexDedup.TryGetValue(cmPtr, out int cmIndex))
                    {
                        if (!TryIngestConvexMesh(cmPtr, out var cm))
                        { result = BuildActorResult.GeomDataFail; return null; }
                        cmIndex = convexes.Count; convexes.Add(cm); convexDedup[cmPtr] = cmIndex;
                    }
                    var cmRef = convexes[cmIndex];
                    var cmSize = cmRef.LocalAabbMax - cmRef.LocalAabbMin;
                    if (cmSize.X > MaxRealMeshLinearExtent
                        || cmSize.Y > MaxRealMeshLinearExtent
                        || cmSize.Z > MaxRealMeshLinearExtent)
                    { result = BuildActorResult.OversizedTrigger; return null; }
                    var (cMin, cMax) = TransformOrientedAabb(worldXform, cmRef.LocalAabbMin, cmRef.LocalAabbMax);
                    result = BuildActorResult.Ok;
                    return new CachedActor
                    {
                        WorldTransform = worldXform, WorldAabbMin = cMin, WorldAabbMax = cMax,
                        GeometryType = geomType, ConvexMeshIndex = cmIndex,
                        ActorBase = actorPtr, ShapeLayerMask = shapeLayerMask, ShapeGroupMask = shapeGroupMask,
                        Name = actorName, UnityLayer = unityLayer,
                        IsSeeThrough = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                }
                case PxGeometryType.HeightField:
                {
                    if (!Memory.TryReadPtr(geomVa + PhysXOffsets.HeightFieldGeom_HeightFieldPtr, out var hfPtr, false)
                        || !hfPtr.IsValidVirtualAddress())
                    { result = BuildActorResult.GeomDataFail; return null; }
                    if (!Memory.TryReadValue<float>(geomVa + PhysXOffsets.HeightFieldGeom_HeightScale, out var hs, false)
                        || !Memory.TryReadValue<float>(geomVa + PhysXOffsets.HeightFieldGeom_RowScale,   out var rs, false)
                        || !Memory.TryReadValue<float>(geomVa + PhysXOffsets.HeightFieldGeom_ColumnScale,out var cs, false)
                        || !float.IsFinite(hs) || !float.IsFinite(rs) || !float.IsFinite(cs))
                    { result = BuildActorResult.GeomDataFail; return null; }
                    if (!hfDedup.TryGetValue(hfPtr, out int hfIndex))
                    {
                        if (!TryIngestHeightField(hfPtr, hs, rs, cs, out var hf))
                        { result = BuildActorResult.GeomDataFail; return null; }
                        hfIndex = heightFields.Count; heightFields.Add(hf); hfDedup[hfPtr] = hfIndex;
                    }
                    var hfRef = heightFields[hfIndex];
                    var (hMin, hMax) = TransformOrientedAabb(worldXform, hfRef.LocalAabbMin, hfRef.LocalAabbMax);
                    result = BuildActorResult.Ok;
                    return new CachedActor
                    {
                        WorldTransform = worldXform, WorldAabbMin = hMin, WorldAabbMax = hMax,
                        GeometryType = geomType, HeightFieldIndex = hfIndex,
                        ActorBase = actorPtr, ShapeLayerMask = shapeLayerMask, ShapeGroupMask = shapeGroupMask,
                        Name = actorName, UnityLayer = unityLayer,
                        IsSeeThrough = VisibilityClassifier.Classify(mapId, shapeLayerMask, actorName),
                    };
                }
                default:
                    result = BuildActorResult.GeomTypeUnsupported;
                    return null;
            }
        }

        /// <summary>
        /// Walks the actor's shape manager candidates and
        /// returns the first shape pointer + shape-core VA. Most actors carry
        /// exactly one shape; we consume the first.
        /// </summary>
        private static bool TryReadShape(ulong actorPtr, out ulong shapePtr, out ulong shapeCoreVa)
        {
            shapePtr = 0; shapeCoreVa = 0;
            ReadOnlySpan<uint> candidates =
            [
                PhysXOffsets.PxRigidActor_ShapeManager
            ];
            foreach (var off in candidates)
            {
                if (!Memory.TryReadPtr(actorPtr + off + PhysXOffsets.NpShapeManager_ShapesSingle, out var ptrTableSingle, false))
                    continue;
                if (!Memory.TryReadValue<ushort>(actorPtr + off + PhysXOffsets.NpShapeManager_ShapesCount, out var count, false))
                    continue;
                if (count == 0) continue;

                ulong shapeAddr = count == 1
                    ? ptrTableSingle
                    : (Memory.TryReadPtr(ptrTableSingle, out var firstFromArr, false) ? firstFromArr : 0);
                if (!shapeAddr.IsValidVirtualAddress()) continue;

                // PxShapeCore is embedded inline inside NpShape (PhysX 4.1 layout) â€”
                // we ADD the offset, we do NOT dereference a pointer. The previous
                // pointer-deref interpretation read a member that wasn't a shape core.
                ulong coreVa = shapeAddr + PhysXOffsets.NpShape_PxShapeCoreOffset;
                if (!coreVa.IsValidVirtualAddress())
                    continue;

                shapePtr = shapeAddr;
                shapeCoreVa = coreVa;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the actor's world pose. Dynamic actors use their buffered
        /// body-to-world transform; statics keep theirs at a fixed slot.
        /// </summary>
        private static bool TryReadActorPose(ulong actorPtr, PxConcreteType kind, out PxTransform pose)
        {
            uint off = kind == PxConcreteType.RigidDynamic
                ? PhysXOffsets.NpRigidDynamic_BufferedBody2World
                : PhysXOffsets.NpRigidStatic_BodyToWorld;
            return Memory.TryReadValue(actorPtr + off, out pose, false);
        }

        private static bool TryIngestTriMesh(ulong meshPtr, out CachedTriMesh mesh)
        {
            mesh = default!;
            if (!Memory.TryReadValue<uint>(meshPtr + PhysXOffsets.TriangleMesh_NbVertices,  out var nVerts, false)
                || !Memory.TryReadValue<uint>(meshPtr + PhysXOffsets.TriangleMesh_NbTriangles, out var nTris, false)
                || nVerts == 0 || nTris == 0 || nVerts > 1_000_000 || nTris > 500_000)
                return false;
            if (!Memory.TryReadPtr(meshPtr + PhysXOffsets.TriangleMesh_Vertices, out var vertsPtr, false)
                || !Memory.TryReadPtr(meshPtr + PhysXOffsets.TriangleMesh_Triangles, out var trisPtr, false)
                || !vertsPtr.IsValidVirtualAddress() || !trisPtr.IsValidVirtualAddress())
                return false;
            if (!Memory.TryReadValue<byte>(meshPtr + PhysXOffsets.TriangleMesh_Flags, out var flagsByte, false))
                return false;
            bool has16BitIndices = ((PxTriangleMeshFlag)flagsByte & PxTriangleMeshFlag.Has16BitIndices) != 0;

            Vector3[]? verts;
            try { verts = Memory.ReadArray<Vector3>(vertsPtr, (int)nVerts, false); }
            catch { return false; }
            if (verts is null) return false;

            int[] indices;
            try
            {
                if (has16BitIndices)
                {
                    var raw = Memory.ReadArray<ushort>(trisPtr, (int)nTris * 3, false);
                    if (raw is null) return false;
                    indices = new int[raw.Length];
                    for (int i = 0; i < raw.Length; i++) indices[i] = raw[i];
                }
                else
                {
                    var raw = Memory.ReadArray<int>(trisPtr, (int)nTris * 3, false);
                    if (raw is null) return false;
                    indices = raw;
                }
            }
            catch { return false; }

            // Local AABB from the cooked mesh header is the cheap path. Some
            // builds zero it out â€” fall back to scanning vertices in that case.
            Vector3 localMin, localMax;
            if (!Memory.TryReadValue<Vector3>(meshPtr + PhysXOffsets.TriangleMesh_LocalBoundsMin, out localMin, false)
                || !Memory.TryReadValue<Vector3>(meshPtr + PhysXOffsets.TriangleMesh_LocalBoundsMax, out localMax, false)
                || !IsFinite(localMin) || !IsFinite(localMax)
                || localMin.X > localMax.X || localMin.Y > localMax.Y || localMin.Z > localMax.Z)
            {
                ComputeAabbFromVertices(verts, out localMin, out localMax);
            }

            mesh = new CachedTriMesh
            {
                Vertices      = verts,
                Indices       = indices,
                TriangleCount = (int)nTris,
                LocalAabbMin  = localMin,
                LocalAabbMax  = localMax,
                MeshBase      = meshPtr,
            };
            return true;
        }

        // Quiet shared state so the hex-dump fallback in TryIngestConvexMesh
        // fires for the first failing actor only, instead of flooding the log
        // when the offset guesses are wrong on every actor.
        private static int _convexDumpsLogged;
        private const int MaxConvexDumps = 2;

        /// <summary>
        /// Reads a <c>PxConvexMesh</c> and caches its vertex array + polygon
        /// plane equations. The PhysX 4.1.2 binary layout for
        /// <c>Gu::ConvexHullData</c> is approximated from header conventions
        /// (see <see cref="PhysXOffsets"/> ConvexMesh_*). If the counts read
        /// as implausible (vertex count outside [4,255], polygon count
        /// outside [4,64]), we treat the offsets as wrong and log a hex
        /// dump of the structure so the layout can be corrected.
        /// </summary>
        private static bool TryIngestConvexMesh(ulong cmPtr, out CachedConvexMesh cm)
        {
            cm = default!;

            // AABB read first â€” same offset as PxTriangleMesh by structural
            // analogy, lowest risk of being wrong.
            if (!Memory.TryReadValue<Vector3>(cmPtr + PhysXOffsets.ConvexMesh_AabbMin, out var aMin, false)
                || !Memory.TryReadValue<Vector3>(cmPtr + PhysXOffsets.ConvexMesh_AabbMax, out var aMax, false)
                || !float.IsFinite(aMin.X) || !float.IsFinite(aMax.X))
            {
                DumpConvexMeshHex(cmPtr, "AABB read failed");
                return false;
            }
            if (aMin.X > aMax.X || aMin.Y > aMax.Y || aMin.Z > aMax.Z)
            {
                DumpConvexMeshHex(cmPtr, $"AABB invalid: min=({aMin.X:F1},{aMin.Y:F1},{aMin.Z:F1}) max=({aMax.X:F1},{aMax.Y:F1},{aMax.Z:F1})");
                return false;
            }

            // Counts â€” single-byte fields in ConvexHullData. PhysX limits hull
            // vertices to 255 and polygons to 64 in the cooker, so the valid
            // ranges below are tight.
            if (!Memory.TryReadValue<byte>(cmPtr + PhysXOffsets.ConvexMesh_NbVertices, out byte nV, false)
                || !Memory.TryReadValue<byte>(cmPtr + PhysXOffsets.ConvexMesh_NbPolygons, out byte nP, false))
            {
                DumpConvexMeshHex(cmPtr, "count read failed");
                return false;
            }
            if (nV < 4 || nV > 255 || nP < 4 || nP > 64)
            {
                DumpConvexMeshHex(cmPtr, $"counts implausible: nV={nV} nP={nP} (expected nVâˆˆ[4,255], nPâˆˆ[4,64])");
                return false;
            }

            // Vertex + polygon array pointers.
            if (!Memory.TryReadPtr(cmPtr + PhysXOffsets.ConvexMesh_Vertices, out var vertsPtr, false)
                || !vertsPtr.IsValidVirtualAddress()
                || !Memory.TryReadPtr(cmPtr + PhysXOffsets.ConvexMesh_Polygons, out var polysPtr, false)
                || !polysPtr.IsValidVirtualAddress())
            {
                DumpConvexMeshHex(cmPtr, "vertex/polygon pointer read failed");
                return false;
            }

            // Bulk-read the vertex array: nV Ã— Vector3 (12 bytes each).
            Vector3[] verts;
            try { verts = Memory.ReadArray<Vector3>(vertsPtr, nV, false); }
            catch
            {
                DumpConvexMeshHex(cmPtr, $"vertex array read failed ({nV} Ã— Vec3 @ 0x{vertsPtr:X})");
                return false;
            }
            if (verts is null || verts.Length != nV)
            {
                DumpConvexMeshHex(cmPtr, "vertex array short read");
                return false;
            }

            // Per-polygon: read the leading PxPlane (16 bytes), skip the
            // index-buffer tail (4 bytes). Stride is HullPolygonData_Stride.
            var planes = new Vector4[nP];
            for (int i = 0; i < nP; i++)
            {
                ulong addr = polysPtr + (ulong)i * PhysXOffsets.HullPolygonData_Stride
                                      + PhysXOffsets.HullPolygonData_Plane;
                if (!Memory.TryReadValue<Vector4>(addr, out var pl, false)
                    || !float.IsFinite(pl.X) || !float.IsFinite(pl.Y) || !float.IsFinite(pl.Z) || !float.IsFinite(pl.W))
                {
                    DumpConvexMeshHex(cmPtr, $"polygon[{i}] plane read failed @ 0x{addr:X}");
                    return false;
                }
                // Normals from a valid hull are unit length; reject mesh if
                // any plane normal has a sketchy magnitude.
                float nlen2 = pl.X * pl.X + pl.Y * pl.Y + pl.Z * pl.Z;
                if (nlen2 < 0.5f || nlen2 > 1.5f)
                {
                    DumpConvexMeshHex(cmPtr, $"polygon[{i}] normal not unit-length: |n|Â²={nlen2:F3}");
                    return false;
                }
                planes[i] = pl;
            }

            cm = new CachedConvexMesh
            {
                Vertices       = verts,
                PolygonPlanes  = planes,
                PolygonCount   = nP,
                LocalAabbMin   = aMin,
                LocalAabbMax   = aMax,
                ConvexMeshBase = cmPtr,
            };
            return true;
        }

        // Diagnostic dump of the geometry union itself â€” separate from the
        // PxConvexMesh struct dump. Used to locate the real PxConvexMesh*
        // offset within PxConvexMeshGeometry. Capped so the log can't flood.
        private static int _convexGeomUnionDumps;
        private const int MaxConvexGeomUnionDumps = 3;

        /// <summary>
        /// Dumps the first 64 bytes of <paramref name="geomVa"/>, which is
        /// where PxConvexMeshGeometry sits inside the shape core. The PxGeometry
        /// header (type tag) is at offset 0, then the type-specific fields
        /// follow â€” somewhere among these 64 bytes is the PxConvexMesh pointer
        /// we need to read. Step-2 correction looks at this dump to choose
        /// the right offset.
        /// </summary>
        private static void DumpConvexGeomUnion(ulong geomVa)
        {
            int idx = Interlocked.Increment(ref _convexGeomUnionDumps);
            if (idx > MaxConvexGeomUnionDumps) return;
            byte[]? hex;
            try { hex = Memory.ReadArray<byte>(geomVa, 64, false); }
            catch { hex = null; }
            if (hex is null)
            {
                Log.WriteLine($"[SceneCache] ConvexGeomUnion dump #{idx} at 0x{geomVa:X}: union unreadable");
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[SceneCache] ConvexGeomUnion dump #{idx} at 0x{geomVa:X} " +
                          $"(first 64 bytes of PxConvexMeshGeometry â€” look for a valid heap ptr):");
            for (int row = 0; row < hex.Length; row += 16)
            {
                sb.Append($"  +0x{row:X2}  ");
                for (int col = 0; col < 16 && row + col < hex.Length; col++)
                    sb.Append($"{hex[row + col]:X2} ");
                sb.AppendLine();
            }
            // Also pre-interpret each qword as "if this were a pointer, what
            // would it be" â€” saves an eye-strain step in the analysis.
            sb.AppendLine("  Possible qword interpretations:");
            for (int q = 0; q + 8 <= hex.Length; q += 8)
            {
                ulong v = BitConverter.ToUInt64(hex, q);
                string note;
                if (v == 0) note = "zero";
                else if ((v & 0x7) != 0) note = "NOT 8-byte aligned (cannot be a PhysX heap pointer)";
                else if (v < 0x10000) note = "small integer / flags";
                else note = "could be a heap pointer";
                sb.AppendLine($"    +0x{q:X2}  0x{v:X16}   ({note})");
            }
            Log.WriteLine(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Hex dump of the first 128 bytes of a PxConvexMesh struct when our
        /// best-guess offsets fail validation. Capped at <see cref="MaxConvexDumps"/>
        /// dumps per build so the log doesn't flood if every actor fails.
        /// </summary>
        private static void DumpConvexMeshHex(ulong cmPtr, string reason)
        {
            int idx = Interlocked.Increment(ref _convexDumpsLogged);
            if (idx > MaxConvexDumps) return;
            byte[]? hex;
            try { hex = Memory.ReadArray<byte>(cmPtr, 128, false); }
            catch { hex = null; }
            if (hex is null)
            {
                Log.WriteLine($"[SceneCache] ConvexMesh dump #{idx} at 0x{cmPtr:X}: {reason}; structure unreadable");
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[SceneCache] ConvexMesh dump #{idx} at 0x{cmPtr:X}: {reason}");
            for (int row = 0; row < hex.Length; row += 16)
            {
                sb.Append($"  +0x{row:X2}  ");
                for (int col = 0; col < 16 && row + col < hex.Length; col++)
                    sb.Append($"{hex[row + col]:X2} ");
                sb.AppendLine();
            }
            Log.WriteLine(sb.ToString().TrimEnd());
        }

        private static bool TryIngestHeightField(
            ulong hfPtr, float hScale, float rScale, float cScale, out CachedHeightField hf)
        {
            hf = default!;
            if (!Memory.TryReadValue<uint>(hfPtr + PhysXOffsets.HeightField_Rows,    out var rows, false)
                || !Memory.TryReadValue<uint>(hfPtr + PhysXOffsets.HeightField_Columns, out var cols, false)
                || rows == 0 || cols == 0 || rows > 4096 || cols > 4096)
                return false;
            uint nSamples = rows * cols;
            if (!Memory.TryReadPtr(hfPtr + PhysXOffsets.HeightField_Samples, out var samplesPtr, false)
                || !samplesPtr.IsValidVirtualAddress())
                return false;

            // PxHfSample is a 4-byte struct (i16 height + 2 bytes material flags).
            // We only need the height; read the whole array as i16 with a stride
            // skip of 2 bytes. Easiest: read the full byte blob, extract heights.
            byte[]? raw;
            try { raw = Memory.ReadArray<byte>(samplesPtr, (int)nSamples * 4, false); }
            catch { return false; }
            if (raw is null) return false;

            var samples = new short[nSamples];
            for (int i = 0; i < (int)nSamples; i++)
            {
                int off = i * 4;
                samples[i] = (short)(raw[off] | (raw[off + 1] << 8));
            }

            // Local AABB: (col*colScale, sample*heightScale, row*rowScale) bounds.
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int i = 0; i < samples.Length; i++)
            {
                float h = samples[i] * hScale;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
            if (samples.Length == 0) { minH = 0f; maxH = 0f; }

            float widthX = cols * cScale;
            float widthZ = rows * rScale;
            var localMin = new Vector3(0f, minH, 0f);
            var localMax = new Vector3(widthX, maxH, widthZ);

            hf = new CachedHeightField
            {
                Samples         = samples,
                Rows            = (int)rows,
                Columns         = (int)cols,
                RowScale        = rScale,
                ColumnScale     = cScale,
                HeightScale     = hScale,
                LocalAabbMin    = localMin,
                LocalAabbMax    = localMax,
                HeightFieldBase = hfPtr,
            };
            return true;
        }

        // â”€â”€ Geometry helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Transforms a local-space AABB by a rigid transform and returns the
        /// (over-conservative) world-space AABB of the result. The classic
        /// "rotate the 8 corners and take min/max" â€” cheap, no allocation.
        /// </summary>
        private static (Vector3 min, Vector3 max) TransformOrientedAabb(
            in PxTransform xform, Vector3 localMin, Vector3 localMax)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            corners[0] = new Vector3(localMin.X, localMin.Y, localMin.Z);
            corners[1] = new Vector3(localMax.X, localMin.Y, localMin.Z);
            corners[2] = new Vector3(localMin.X, localMax.Y, localMin.Z);
            corners[3] = new Vector3(localMax.X, localMax.Y, localMin.Z);
            corners[4] = new Vector3(localMin.X, localMin.Y, localMax.Z);
            corners[5] = new Vector3(localMax.X, localMin.Y, localMax.Z);
            corners[6] = new Vector3(localMin.X, localMax.Y, localMax.Z);
            corners[7] = new Vector3(localMax.X, localMax.Y, localMax.Z);

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = xform.TransformPoint(corners[i]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (min, max);
        }

        /// <summary>
        /// Truncates a string to <paramref name="max"/> characters, appending
        /// an ellipsis if cut. Used by the name-histogram log to keep long
        /// generated names ("MeshCollider (instance) (clone) ...") from
        /// wrecking the log line layout.
        /// </summary>
        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
            return s.Substring(0, max - 1) + "â€¦";
        }

        /// <summary>
        /// Case-insensitive ordinal Contains scan against a flat pattern array.
        /// Used by the see-through breakdown log so each rule source's
        /// contribution can be counted independently.
        /// </summary>
        private static bool HasSubstringMatch(string name, string[] patterns)
        {
            if (patterns is null) return false;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (!string.IsNullOrEmpty(patterns[i])
                    && name.Contains(patterns[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void ComputeAabbFromVertices(Vector3[] verts, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) continue;
                if (v.X < min.X) min.X = v.X; if (v.X > max.X) max.X = v.X;
                if (v.Y < min.Y) min.Y = v.Y; if (v.Y > max.Y) max.Y = v.Y;
                if (v.Z < min.Z) min.Z = v.Z; if (v.Z > max.Z) max.Z = v.Z;
            }
            if (min.X > max.X) { min = Vector3.Zero; max = Vector3.Zero; }
        }

        private static bool IsFinite(Vector3 v)
            => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        // â”€â”€ Scene survey â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Samples the first 64 actors in <paramref name="scenePtr"/> and counts
        /// how many carry at least one PhysX shape. Returns an estimate scaled to
        /// <paramref name="actorCount"/> so scenes of different sizes are comparable.
        /// Returns 0 on any read failure.
        /// </summary>
        private static int SampleShapedActors(ulong scenePtr, uint actorCount)
        {
            if (!Memory.TryReadPtr(scenePtr + PhysXOffsets.NpScene_RigidActorsData, out var arrPtr, false)
                || !arrPtr.IsValidVirtualAddress())
                return 0;

            const int MaxSample = 64;
            int sampleN = (int)Math.Min(actorCount, MaxSample);
            ulong[]? ptrs;
            try { ptrs = Memory.ReadArray<ulong>(arrPtr, sampleN, false); }
            catch { return 0; }
            if (ptrs is null) return 0;

            int shaped = 0;
            foreach (var ptr in ptrs)
            {
                if (!ptr.IsValidVirtualAddress()) continue;
                if (!Memory.TryReadValue<ushort>(
                        ptr + PhysXOffsets.PxRigidActor_ShapeManager + PhysXOffsets.NpShapeManager_ShapesCount,
                        out var cnt, false))
                    continue;
                if (cnt > 0) shaped++;
            }

            // Scale the sampled count to the full actor count so scenes are ranked
            // by their estimated total shaped actors, not just the sample fraction.
            return actorCount <= MaxSample
                ? shaped
                : (int)((long)shaped * actorCount / sampleN);
        }

        // â”€â”€ Diagnostic dump â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Logs a 512-byte window from <paramref name="actorPtr"/> in 16-byte
        /// rows, then a "shape-array candidate" scan that flags any 8-byte
        /// aligned offset whose neighbouring bytes look like
        /// <c>{ T* mData; u32 mSize; u32 mCapacity; }</c>. Fires only when the
        /// build couldn't process a single actor â€” gives us enough data to
        /// either fix the offset constants in-place or correlate against IDA
        /// without writing more code.
        /// </summary>
        private static void DumpActorDiagnostics(ulong actorPtr, ushort firstTypeRead)
        {
            const int DumpSize = 0x200; // 512 bytes
            byte[]? bytes = null;
            try { bytes = Memory.ReadArray<byte>(actorPtr, DumpSize, false); } catch { }
            if (bytes is null || bytes.Length != DumpSize)
            {
                Log.WriteLine($"[SceneCache] DIAG dump read failed for actor 0x{actorPtr:X}");
                return;
            }

            Log.WriteLine(
                $"[SceneCache] DIAG sample actor 0x{actorPtr:X}  " +
                $"concreteType@+0x8 = 0x{firstTypeRead:X4}  " +
                $"({DumpSize} bytes follow, 16 per row):");

            // 16-byte rows: "  +0xXXX: aa bb cc dd ee ff ...  (ascii where printable)"
            // for the row to be useful at a glance.
            var hex = new System.Text.StringBuilder(80);
            for (int row = 0; row < DumpSize; row += 16)
            {
                hex.Clear();
                hex.Append($"  +0x{row:X3}: ");
                for (int i = 0; i < 16; i++)
                {
                    hex.Append(bytes[row + i].ToString("X2"));
                    hex.Append(' ');
                    if (i == 7) hex.Append(' '); // small gap halfway for readability
                }
                Log.WriteLine($"[SceneCache] DIAG{hex}");
            }

            // Candidate scan: every 8-byte-aligned offset that looks like a
            // PhysX shape-array storage triple.
            Log.WriteLine("[SceneCache] DIAG candidate {ptr, count, capacity} pairs:");
            int candidates = 0;
            for (int off = 0; off + 16 <= DumpSize; off += 8)
            {
                ulong ptr = System.BitConverter.ToUInt64(bytes, off);
                uint  size = System.BitConverter.ToUInt32(bytes, off + 8);
                uint  cap  = System.BitConverter.ToUInt32(bytes, off + 12);
                if (!IsLikelyHeapPointer(ptr)) continue;
                if (size == 0 || size > 16) continue;          // shape count, plausibly small
                if (cap < size || cap > 64) continue;          // capacity â‰¥ size, not absurd
                Log.WriteLine(
                    $"[SceneCache] DIAG   +0x{off:X3}: ptr=0x{ptr:X}  size={size}  capacity={cap}");
                candidates++;
            }
            if (candidates == 0)
            {
                Log.WriteLine("[SceneCache] DIAG   (no Ps::Array<T*>-shaped triples found in 512 bytes)");
            }

            // Also list any standalone heap pointer in the dump â€” useful if
            // the shape pointer is stored without an inline size/capacity pair.
            Log.WriteLine("[SceneCache] DIAG standalone heap pointers (for cross-reference):");
            int ptrCount = 0;
            for (int off = 0; off + 8 <= DumpSize; off += 8)
            {
                ulong p = System.BitConverter.ToUInt64(bytes, off);
                if (!IsLikelyHeapPointer(p)) continue;
                Log.WriteLine($"[SceneCache] DIAG   +0x{off:X3}: 0x{p:X}");
                ptrCount++;
                if (ptrCount >= 32) { Log.WriteLine("[SceneCache] DIAG   (truncated at 32)"); break; }
            }
        }

        /// <summary>
        /// Dumps 256 bytes of the NpShape pointed to by <paramref name="shapePtr"/>
        /// and auto-scans for the embedded PxShapeCore. PhysX 4.1 stores
        /// <c>mCore</c> inline inside NpShape, not behind a pointer, so the
        /// task is to find the offset at which a PxShapeCore signature appears:
        /// a unit-length quaternion (4 floats), a finite Vec3 (3 floats), and
        /// then a <see cref="PxGeometryType"/> value in [0, 6] as i32 at +28.
        /// Any matching offset is the correct value for
        /// <c>NpShape_ShapeCoreOffset</c> (replacing the current pointer-deref
        /// approach).
        /// </summary>
        private static void DumpShapeDiagnostics(ulong shapePtr)
        {
            const int DumpSize = 0x100; // 256 bytes â€” large enough to span NpShape header + embedded PxShapeCore
            byte[]? bytes = null;
            try { bytes = Memory.ReadArray<byte>(shapePtr, DumpSize, false); } catch { }
            if (bytes is null || bytes.Length != DumpSize)
            {
                Log.WriteLine($"[SceneCache] DIAG NpShape dump read failed for 0x{shapePtr:X}");
                return;
            }

            Log.WriteLine($"[SceneCache] DIAG NpShape 0x{shapePtr:X} ({DumpSize} bytes follow):");
            var hex = new System.Text.StringBuilder(80);
            for (int row = 0; row < DumpSize; row += 16)
            {
                hex.Clear();
                hex.Append($"  +0x{row:X3}: ");
                for (int i = 0; i < 16; i++)
                {
                    hex.Append(bytes[row + i].ToString("X2"));
                    hex.Append(' ');
                    if (i == 7) hex.Append(' ');
                }
                Log.WriteLine($"[SceneCache] DIAG{hex}");
            }

            // Auto-scan for embedded PxShapeCore: a unit-quaternion immediately
            // followed by Vec3 immediately followed by an i32 geometry type in [0, 6].
            // The first match's offset is the correct NpShape â†’ PxShapeCore offset.
            Log.WriteLine("[SceneCache] DIAG PxShapeCore candidates (unit quat + Vec3 + geomType âˆˆ [0,6] @ +28):");
            int candidates = 0;
            for (int off = 0; off + 32 <= DumpSize; off += 4)
            {
                float qx = System.BitConverter.ToSingle(bytes, off);
                float qy = System.BitConverter.ToSingle(bytes, off + 4);
                float qz = System.BitConverter.ToSingle(bytes, off + 8);
                float qw = System.BitConverter.ToSingle(bytes, off + 12);
                if (!float.IsFinite(qx) || !float.IsFinite(qy) || !float.IsFinite(qz) || !float.IsFinite(qw)) continue;
                float sq = qx * qx + qy * qy + qz * qz + qw * qw;
                if (sq < 0.97f || sq > 1.03f) continue;

                float px = System.BitConverter.ToSingle(bytes, off + 16);
                float py = System.BitConverter.ToSingle(bytes, off + 20);
                float pz = System.BitConverter.ToSingle(bytes, off + 24);
                if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz)) continue;
                // Reject the obvious noise pattern: all-zero positions next to a "unit"
                // quaternion that's actually the identity (0,0,0,1) â€” common in unrelated padding.
                bool isIdentityAtZero = qx == 0f && qy == 0f && qz == 0f && qw == 1f
                                        && px == 0f && py == 0f && pz == 0f;

                int geomType = System.BitConverter.ToInt32(bytes, off + 28);
                if (geomType < 0 || geomType > 6) continue;

                Log.WriteLine(
                    $"[SceneCache] DIAG   +0x{off:X3}: quat=({qx:F3},{qy:F3},{qz:F3},{qw:F3}) " +
                    $"pos=({px:F2},{py:F2},{pz:F2}) geomType@+28={geomType}" +
                    (isIdentityAtZero ? "  [possibly identity/zero noise]" : ""));
                candidates++;
            }
            if (candidates == 0)
            {
                Log.WriteLine("[SceneCache] DIAG   (no PxShapeCore-shaped block found in 256 bytes)");
            }
        }

        /// <summary>
        /// Reads <paramref name="shapePtrs"/> (up to 8) and prints a side-by-side
        /// u32-per-offset comparison of their NpShape memory. The goal: find the
        /// offset where the value varies across shapes as a Unity one-hot layer
        /// mask (1, 2, 4, ..., 0x80000000). That offset is
        /// <c>simulationFilterData.word0</c> â€” the shape's Unity layer index.
        /// Once identified, we read it during cache build and skip see-through
        /// layers (railings, foliage, signposts), eliminating most remaining
        /// false-positive visibility blocks.
        /// </summary>
        private static void DumpShapeComparison(IReadOnlyList<ulong> shapePtrs)
        {
            const int DumpSize = 0x100; // 256 bytes per shape
            int n = shapePtrs.Count;
            var samples = new byte[n][];
            int validSamples = 0;
            for (int i = 0; i < n; i++)
            {
                byte[]? bytes = null;
                try { bytes = Memory.ReadArray<byte>(shapePtrs[i], DumpSize, false); } catch { }
                if (bytes is not null && bytes.Length == DumpSize)
                {
                    samples[i] = bytes;
                    validSamples++;
                }
            }
            if (validSamples < 2)
            {
                Log.WriteLine("[SceneCache] SHAPE-COMPARE skipped â€” fewer than 2 valid samples");
                return;
            }

            Log.WriteLine($"[SceneCache] SHAPE-COMPARE {validSamples} NpShape samples (u32 per 4-byte offset; look for one-hot layer-mask column):");
            // Header
            var hdr = new System.Text.StringBuilder("  offset  ");
            for (int i = 0; i < n; i++)
                if (samples[i] is not null)
                    hdr.Append($"shape[{i}]:{shapePtrs[i]:X12}  ");
            Log.WriteLine("[SceneCache]" + hdr);

            // Body: walk every 4-byte offset, print u32 from each sample.
            // We start at +0x40 so the PxFilterData region (NpShape+0x60..+0x6F per
            // IDA decompile of setSimulationFilterData) is included â€” that's where
            // the Unity layer one-hot should appear in one of the four u32 words.
            for (int off = 0x40; off + 4 <= DumpSize; off += 4)
            {
                var row = new System.Text.StringBuilder($"  +0x{off:X3}  ");
                bool anyInteresting = false;
                for (int i = 0; i < n; i++)
                {
                    if (samples[i] is null) { row.Append("            ".PadRight(26)); continue; }
                    uint v = System.BitConverter.ToUInt32(samples[i], off);
                    row.Append($"0x{v:X8}".PadRight(26));
                    // "Interesting" = small power of 2 (likely Unity layer mask)
                    if (v != 0 && v < 0x80000001 && (v & (v - 1)) == 0) anyInteresting = true;
                }
                // Always print, but tag rows with possible layer-mask values for easy spotting.
                Log.WriteLine($"[SceneCache]{row}{(anyInteresting ? "  â† power-of-2 candidate" : "")}");
            }
        }

        /// <summary>
        /// Cheap-and-cheerful "could this be a Windows-userland heap pointer?"
        /// test â€” high enough to escape sentinel zones, not so high that it's
        /// a kernel address. Excludes pointers that point inside UnityPlayer.dll
        /// itself (those are not what we're hunting for here).
        /// </summary>
        private static bool IsLikelyHeapPointer(ulong p)
        {
            if (!Misc.Utils.IsValidVirtualAddress(p)) return false;
            // Reject pointers that fall inside UnityPlayer.dll's mapped range
            // â€” those are vtables / globals, not heap-allocated members.
            ulong unityBase = Memory.UnityBase;
            if (unityBase != 0)
            {
                uint imageSize = Memory.GetModuleImageSize("UnityPlayer.dll");
                ulong end = imageSize > 0 ? unityBase + imageSize : unityBase + 0x4000000UL;
                if (p >= unityBase && p < end) return false;
            }
            return true;
        }
    }
}
