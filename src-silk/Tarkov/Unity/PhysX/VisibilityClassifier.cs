using System.Collections.Generic;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Decides which cached actors are "see-through" â€” i.e. ignored by the
    /// visibility raycaster. Three rule sources combine via OR:
    /// <list type="bullet">
    ///   <item><b>Layer mask</b> (<see cref="Raycaster.SeeThroughLayerMask"/>):
    ///     historical Phase 1 V0 rule. Any actor whose one-hot
    ///     <c>ShapeLayerMask</c> bit intersects the mask is see-through.
    ///     Defaults to layer 16 (player colliders) so a player's own body
    ///     doesn't block sightlines to teammates standing behind them.</item>
    ///   <item><b>Global name substrings</b> (<see cref="GlobalNamePatterns"/>):
    ///     applied to every map. Substring match against
    ///     <see cref="CachedActor.Name"/> (ordinal case-insensitive).
    ///     Default: <c>"Glass"</c> â€” windows are transparent regardless of
    ///     which scene loaded them.</item>
    ///   <item><b>Map-scoped name substrings</b> (<see cref="GetMapPatterns"/>
    ///     / <see cref="SetMapPatterns"/>): same substring semantics as the
    ///     global list but only consulted when the snapshot's
    ///     <see cref="SceneSnapshot.MapId"/> matches the dictionary key.
    ///     Lets us add patterns that are correct on one arena scene but
    ///     would mis-filter on another (e.g. a Sandbag mesh that's
    ///     translucent on one scene but opaque on another).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why pre-compute, not check per-ray:</b> name-substring match on every
    /// ray Ã— actor pair would cost ~25 k extra string scans per visibility
    /// frame. Instead we classify once at snapshot build / load time and
    /// store the verdict as <see cref="CachedActor.IsSeeThrough"/>; the
    /// raycaster's Gate 0 then becomes a single bool read per actor.
    /// </para>
    /// <para>
    /// <b>Tuning:</b> all rule sources are writable at runtime. After mutating
    /// any of them, call <see cref="Reclassify"/> to refresh
    /// <see cref="CachedActor.IsSeeThrough"/> on the current snapshot.
    /// </para>
    /// <para>
    /// <b>Not persisted:</b> <see cref="CachedActor.IsSeeThrough"/> is a
    /// derived value â€” both fresh builds and disk-loaded snapshots compute
    /// it from the current rules. Rule changes invalidate the runtime
    /// classification but not the on-disk snapshot file, so the
    /// 30 s rebuild cost is amortised across rule iteration.
    /// </para>
    /// </remarks>
    internal static class VisibilityClassifier
    {
        // Patterns that apply to every map. Each entry is a case-insensitive
        // substring match â€” pick patterns narrow enough that you won't match
        // legitimate blockers by accident.
        //
        //   "Glass" â€” windows / panes / ballistic glass. Visually transparent
        //   even when the material stops bullets, so the visibility raycast
        //   should pass through.
        //
        //   "Cube (" â€” Unity's auto-generated primitive name with a numeric
        //   suffix (e.g. "Cube (15)", "Cube (270)"). Live match logs on
        //   Arena_AutoService showed a network of ~361 such actors on layer
        //   29 â€” invisible game-logic cubes (spawn protection / detection
        //   volumes) that block sight without any visible geometry. The
        //   trailing "(" anchors the match to the auto-named instances and
        //   leaves any legitimate "Cube" prefix alone.
        public static string[] GlobalNamePatterns { get; set; } =
        [
            "Glass",
            "Cube (",
            // Player capsule colliders. Layer varies by map (16 on Arena_Prison,
            // 8 on Arena_Bay5) so the layer-mask rule alone isn't enough â€” a
            // live Arena_Bay5 match log showed "PlayerSuperior(Clone)" capsules
            // on layer 8 reported as blockers, masking the real geometry behind
            // teammates / enemies. Matching the name catches every map regardless
            // of which layer BSG assigned the player on it.
            "PlayerSuperior",
        ];

        // Per-map additional patterns. Keyed by SceneSnapshot.MapId
        // (case-insensitive). Empty by default; the arena scenes we know
        // about (Arena_Bowl, Arena_AutoService, Arena_Prison, Arena_Iceberg,
        // Arena_saw, Arena_RailwayStation, Arena_equator_TDM_02, Arena_Yard,
        // Arena_Bay5, Arena_AirPit) all start with no entries. They get
        // populated iteratively as the user observes which colliders are
        // wrongly blocking sightlines on each scene and adds the relevant
        // substring via SetMapPatterns.
        //
        // No locking â€” the readers (Classify, Reclassify) run on a single
        // worker / build thread; the writers (UI button presses, config
        // load) are infrequent and atomic at the dictionary-replace level.
        private static readonly Dictionary<string, string[]> _mapPatterns
            = new(System.StringComparer.OrdinalIgnoreCase);

        // â”€â”€ Force-blocker rules â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // The inverse of see-through patterns. When the see-through rules
        // (layer mask + global / per-map name patterns) classify an actor as
        // see-through, but a force-blocker pattern matches its name, the
        // actor is flipped back to BLOCKER. Use cases:
        //
        //   - SeeThroughLayerMask covers a layer that mostly contains
        //     gameplay-trigger geometry but has a handful of real walls on
        //     it (e.g. layer 18 might have a few "Container_Wall_*" that
        //     should still block) â€” adding "Container_Wall" as a force-blocker
        //     keeps the layer rule simple without listing every safe actor.
        //
        //   - A global see-through pattern is broader than ideal (e.g.
        //     "Cube" catches both gameplay cubes AND a few level-design
        //     concrete-cube props) â€” adding a more specific force-blocker
        //     pattern ("ConcreteCube") carves out the real cover.
        //
        // Force-blocker rules take precedence over see-through rules.

        /// <summary>Global force-blocker name substrings â€” apply on every map.</summary>
        public static string[] GlobalBlockerPatterns { get; set; } = [];

        private static readonly Dictionary<string, string[]> _mapBlockerPatterns
            = new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Replaces the per-map force-blocker pattern list for
        /// <paramref name="mapId"/>. Passing <c>null</c> or an empty array
        /// clears the entry.
        /// </summary>
        public static void SetMapBlockerPatterns(string mapId, params string[] patterns)
        {
            if (string.IsNullOrEmpty(mapId)) return;
            if (patterns is null || patterns.Length == 0)
                _mapBlockerPatterns.Remove(mapId);
            else
                _mapBlockerPatterns[mapId] = (string[])patterns.Clone();
        }

        /// <summary>
        /// Returns the per-map force-blocker pattern list for
        /// <paramref name="mapId"/>, or an empty array when no entry exists.
        /// Snapshot copy â€” mutating it does not affect storage.
        /// </summary>
        public static string[] GetMapBlockerPatterns(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return [];
            return _mapBlockerPatterns.TryGetValue(mapId, out var v) ? (string[])v.Clone() : [];
        }

        public static IReadOnlyDictionary<string, string[]> AllMapBlockerPatterns => _mapBlockerPatterns;

        /// <summary>
        /// Replaces the per-map pattern list for <paramref name="mapId"/>.
        /// Passing <c>null</c> or an empty array clears the entry.
        /// </summary>
        public static void SetMapPatterns(string mapId, params string[] patterns)
        {
            if (string.IsNullOrEmpty(mapId)) return;
            if (patterns is null || patterns.Length == 0)
                _mapPatterns.Remove(mapId);
            else
                _mapPatterns[mapId] = (string[])patterns.Clone();
        }

        /// <summary>
        /// Returns the per-map pattern list for <paramref name="mapId"/>, or
        /// an empty array when no entry exists. The returned array is a
        /// snapshot â€” mutating it does not affect the stored entry; use
        /// <see cref="SetMapPatterns"/> for that.
        /// </summary>
        public static string[] GetMapPatterns(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return [];
            return _mapPatterns.TryGetValue(mapId, out var v) ? (string[])v.Clone() : [];
        }

        /// <summary>
        /// Snapshot of every map's pattern list. Cache View / debug UI can
        /// enumerate this to render the current per-map filter state.
        /// </summary>
        public static IReadOnlyDictionary<string, string[]> AllMapPatterns => _mapPatterns;

        // â”€â”€ Config persistence â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Applies all classifier settings stored in <paramref name="cfg"/> to the
        /// live runtime state. Called once at startup after config load.
        /// </summary>
        public static void LoadFromConfig(SilkConfig cfg)
        {
            if (cfg.VisCheckGlobalNamePatterns is { Length: > 0 })
                GlobalNamePatterns = (string[])cfg.VisCheckGlobalNamePatterns.Clone();

            // Force-blocker patterns: load even when empty so deleting all
            // patterns via the UI sticks across restart.
            GlobalBlockerPatterns = cfg.VisCheckGlobalBlockerPatterns is null
                ? []
                : (string[])cfg.VisCheckGlobalBlockerPatterns.Clone();

            Raycaster.SeeThroughLayerMask = cfg.VisCheckSeeThroughLayerMask;

            _mapPatterns.Clear();
            if (cfg.VisCheckMapNamePatterns is not null)
            {
                foreach (var kv in cfg.VisCheckMapNamePatterns)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value?.Length > 0)
                        _mapPatterns[kv.Key] = (string[])kv.Value.Clone();
                }
            }

            _mapBlockerPatterns.Clear();
            if (cfg.VisCheckMapBlockerPatterns is not null)
            {
                foreach (var kv in cfg.VisCheckMapBlockerPatterns)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value?.Length > 0)
                        _mapBlockerPatterns[kv.Key] = (string[])kv.Value.Clone();
                }
            }
        }

        /// <summary>
        /// Writes the current runtime classifier settings back into
        /// <paramref name="cfg"/> so the caller can save them to disk.
        /// </summary>
        public static void SaveToConfig(SilkConfig cfg)
        {
            cfg.VisCheckSeeThroughLayerMask    = Raycaster.SeeThroughLayerMask;
            cfg.VisCheckGlobalNamePatterns     = (string[])GlobalNamePatterns.Clone();
            cfg.VisCheckGlobalBlockerPatterns  = (string[])GlobalBlockerPatterns.Clone();

            var dict = new Dictionary<string, string[]>();
            foreach (var kv in _mapPatterns)
                dict[kv.Key] = (string[])kv.Value.Clone();
            cfg.VisCheckMapNamePatterns = dict;

            var blkDict = new Dictionary<string, string[]>();
            foreach (var kv in _mapBlockerPatterns)
                blkDict[kv.Key] = (string[])kv.Value.Clone();
            cfg.VisCheckMapBlockerPatterns = blkDict;
        }

        /// <summary>
        /// Classifies a single actor against the current rules in the context
        /// of a specific map. Used during fresh-build ingest
        /// (<see cref="SceneCache"/>) and disk-load
        /// (<see cref="SnapshotSerializer"/>).
        /// </summary>
        public static bool Classify(string mapId, uint shapeLayerMask, string? name)
        {
            // Step 1 â€” compute baseline see-through verdict from layer + name rules.
            bool isSeeThrough = false;

            uint layerMask = Raycaster.SeeThroughLayerMask;
            if (layerMask != 0 && (shapeLayerMask & layerMask) != 0)
                isSeeThrough = true;
            else if (!string.IsNullOrEmpty(name))
            {
                if (MatchesAny(name, GlobalNamePatterns))
                    isSeeThrough = true;
                else if (!string.IsNullOrEmpty(mapId)
                         && _mapPatterns.TryGetValue(mapId, out var perMap)
                         && MatchesAny(name, perMap))
                    isSeeThrough = true;
            }

            // Step 2 â€” force-blocker override. Only checked when we'd
            // otherwise mark the actor see-through; rules that don't match
            // a see-through actor don't need to fire because the actor is
            // already a blocker. Order: global first, then map-scoped.
            if (isSeeThrough && !string.IsNullOrEmpty(name))
            {
                if (MatchesAny(name, GlobalBlockerPatterns))
                    return false;
                if (!string.IsNullOrEmpty(mapId)
                    && _mapBlockerPatterns.TryGetValue(mapId, out var perMapBlk)
                    && MatchesAny(name, perMapBlk))
                    return false;
            }

            return isSeeThrough;
        }

        /// <summary>
        /// Returns the first rule that matches the given actor, formatted as a
        /// short human-readable string ("layer 0x10000", "name pattern 'Glass'",
        /// "map pattern 'BALLISTIC_Fabric'"), or "no rule matched" when the
        /// actor isn't classified as see-through. Used by the Cache View
        /// tooltip and the SceneCache build-time sample log so the user can
        /// see why a specific actor got filtered.
        /// </summary>
        public static string Explain(string mapId, uint shapeLayerMask, string? name)
        {
            // First: identify which see-through rule (if any) would have fired.
            // Then check whether a force-blocker pattern overrode it. The
            // assembled string surfaces both halves of the decision so the
            // user can tell "this is a blocker BECAUSE I told it to be"
            // from "this is a blocker because no see-through rule matched".
            string? seeThroughReason = null;

            uint layerMask = Raycaster.SeeThroughLayerMask;
            if (layerMask != 0 && (shapeLayerMask & layerMask) != 0)
                seeThroughReason = $"layer mask 0x{layerMask:X}";
            else if (!string.IsNullOrEmpty(name))
            {
                string? hit = FirstMatch(name, GlobalNamePatterns);
                if (hit is not null) seeThroughReason = $"global pattern \"{hit}\"";
                else if (!string.IsNullOrEmpty(mapId)
                         && _mapPatterns.TryGetValue(mapId, out var perMap))
                {
                    hit = FirstMatch(name, perMap);
                    if (hit is not null) seeThroughReason = $"map(\"{mapId}\") pattern \"{hit}\"";
                }
            }

            // Check the force-blocker rules â€” only meaningful when there was
            // a see-through rule to override.
            if (seeThroughReason is not null && !string.IsNullOrEmpty(name))
            {
                string? blkHit = FirstMatch(name, GlobalBlockerPatterns);
                if (blkHit is not null)
                    return $"force-blocker pattern \"{blkHit}\" overrode {seeThroughReason}";
                if (!string.IsNullOrEmpty(mapId)
                    && _mapBlockerPatterns.TryGetValue(mapId, out var perMapBlk))
                {
                    blkHit = FirstMatch(name, perMapBlk);
                    if (blkHit is not null)
                        return $"force-blocker map(\"{mapId}\") pattern \"{blkHit}\" overrode {seeThroughReason}";
                }
            }

            return seeThroughReason ?? "no rule matched";
        }

        /// <summary>
        /// Walks every actor in <paramref name="snapshot"/> and refreshes
        /// <see cref="CachedActor.IsSeeThrough"/> in place â€” for when the user
        /// edits the rule lists at runtime and wants the new rules to take
        /// effect without rebuilding the cache.
        /// </summary>
        public static void Reclassify(SceneSnapshot snapshot)
        {
            if (snapshot is null || snapshot.IsEmpty) return;

            // Track flips so the diagnostic hook can report "your new pattern
            // moved N actors to see-through, M back to blocker" â€” much more
            // useful than just "rules changed".
            int flipsToSee = 0, flipsToBlock = 0;
            foreach (var a in snapshot.Actors)
            {
                bool prev = a.IsSeeThrough;
                bool now  = Classify(snapshot.MapId, a.ShapeLayerMask, a.Name);
                if (prev != now)
                {
                    if (now) flipsToSee++;
                    else     flipsToBlock++;
                }
                a.IsSeeThrough = now;
            }

            VisCheckDiagnostics.OnClassifierChanged(
                trigger:           "Reclassify",
                newLayerMask:      Raycaster.SeeThroughLayerMask,
                newGlobalPatterns: GlobalNamePatterns,
                flipsToSeeThrough: flipsToSee,
                flipsToBlocker:    flipsToBlock);
        }

        /// <summary>
        /// Substring scan helper â€” case-insensitive ordinal Contains over a
        /// flat array. Empty / null entries in <paramref name="patterns"/>
        /// are skipped so callers don't have to pre-clean the list.
        /// </summary>
        private static bool MatchesAny(string name, string[] patterns)
        {
            if (patterns is null) return false;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (!string.IsNullOrEmpty(patterns[i])
                    && name.Contains(patterns[i], System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Like <see cref="MatchesAny"/> but returns the first matching pattern
        /// itself (useful for diagnostic / explanation output). Null when no
        /// pattern matches.
        /// </summary>
        private static string? FirstMatch(string name, string[] patterns)
        {
            if (patterns is null) return null;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (!string.IsNullOrEmpty(patterns[i])
                    && name.Contains(patterns[i], System.StringComparison.OrdinalIgnoreCase))
                    return patterns[i];
            }
            return null;
        }
    }
}
