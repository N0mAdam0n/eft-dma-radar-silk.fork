using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using eft_dma_radar.Silk.Tarkov.GameWorld;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Structured, opt-in diagnostic dump for the vischeck pipeline. Produces
    /// JSONL files that can be grep'd, jq'd, or pandas'd offline to answer
    /// questions the live UI can't surface â€” "what name patterns dominate
    /// layer 18 on this map?", "which actors blocked the most sightlines in
    /// the last 5 minutes?", "did the SeeThrough verdict for actor X flip
    /// when I added pattern Y?".
    /// <para>
    /// All hooks are no-ops when the relevant toggle is off, so leaving the
    /// instrumentation in the hot paths is free. Toggles persist via
    /// <see cref="SilkConfig"/>; files land in
    /// <c>%AppData%\eft-dma-radar-silk6\vischeck-diag\</c>.
    /// </para>
    /// <para>
    /// Output formats:
    /// <list type="bullet">
    ///   <item><c>snapshot-&lt;mapId&gt;-&lt;timestamp&gt;.jsonl</c> â€” one line per
    ///     <see cref="CachedActor"/>, every field the cache holds.
    ///     One file per build; overwritten only if the same build runs twice
    ///     within the same UTC second (rare).</item>
    ///   <item><c>tick-&lt;sessionStart&gt;.jsonl</c> â€” one line per
    ///     (tick, player) pair. Rolling file per process session, capped at
    ///     <see cref="TickLogMaxBytes"/> with a numbered rollover.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Performance: tick logging is throttled to <see cref="TickLogIntervalMs"/>
    /// (default 100 ms = 10 Hz worst case, 8 players = 80 lines/s â‰ˆ 16 KB/s)
    /// to keep the file usable for human inspection. Snapshot dump is one-shot
    /// per build and uses a <see cref="StringBuilder"/> + single <c>File.WriteAllText</c>
    /// so the worker thread isn't blocked on per-line IO.
    /// </para>
    /// </summary>
    internal static class VisCheckDiagnostics
    {
        // â”€â”€ Output directory â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static readonly string OutputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "eft-dma-radar-silk6", "vischeck-diag");

        /// <summary>Public for the UI's "Open folder" button + folder-exists check.</summary>
        public static string OutputDirectory => OutputDir;

        // â”€â”€ Toggles (mirrored to SilkConfig) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Dump the full snapshot to a fresh JSONL file every time SceneCache builds or loads.</summary>
        public static bool DumpSnapshotOnBuild { get; set; } = false;

        /// <summary>Append one JSONL line per (tick, player) pair to a session-scoped file.</summary>
        public static bool LogVisibilityTicks { get; set; } = false;

        /// <summary>Append one JSONL line per classifier rule edit, with the resulting reclassification stats.</summary>
        public static bool LogClassifierChanges { get; set; } = false;

        public static void LoadFromConfig(SilkConfig cfg)
        {
            DumpSnapshotOnBuild  = cfg.VisCheckDiagDumpSnapshot;
            LogVisibilityTicks   = cfg.VisCheckDiagLogTicks;
            LogClassifierChanges = cfg.VisCheckDiagLogClassifier;
        }

        public static void SaveToConfig(SilkConfig cfg)
        {
            cfg.VisCheckDiagDumpSnapshot   = DumpSnapshotOnBuild;
            cfg.VisCheckDiagLogTicks       = LogVisibilityTicks;
            cfg.VisCheckDiagLogClassifier  = LogClassifierChanges;
        }

        // â”€â”€ Tick log state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // 10 Hz cap on tick records: the live UI already runs at 60 Hz, the log
        // is for trend analysis after the fact â€” finer granularity just bloats
        // the file without adding insight.
        private const int  TickLogIntervalMs = 100;
        private const long TickLogMaxBytes   = 50L * 1024 * 1024; // 50 MB â†’ roll over

        private static readonly Lock _tickLogLock = new();
        private static StreamWriter? _tickLogWriter;
        private static string?       _tickLogPath;
        private static long          _tickLogBytes;
        private static long          _lastTickLogMs;

        // â”€â”€ Snapshot dump state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Path of the most recent successful snapshot dump â€” surfaced in the UI
        // so the user can confirm "yes, the file landed where I expected".
        private static string? _lastSnapshotDumpPath;
        public  static string? LastSnapshotDumpPath => _lastSnapshotDumpPath;
        public  static string? CurrentTickLogPath   => _tickLogPath;

        // â”€â”€ Hook: SceneCache build finished â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Called by <see cref="SceneCache"/> immediately after a fresh
        /// snapshot is published. No-op unless <see cref="DumpSnapshotOnBuild"/>
        /// is on. Runs on the cache build task; never throws â€” diagnostic
        /// failures are logged but don't bubble up.
        /// </summary>
        public static void OnSnapshotBuilt(SceneSnapshot snap)
        {
            if (!DumpSnapshotOnBuild || snap is null || snap.IsEmpty) return;
            try
            {
                var path = DumpSnapshotInternal(snap, reason: "auto");
                Log.WriteLine($"[VisCheckDiag] Snapshot auto-dumped: {path}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[VisCheckDiag] Snapshot auto-dump failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Manual "Dump now" trigger from the UI. Always writes, regardless of
        /// the <see cref="DumpSnapshotOnBuild"/> toggle. Returns the file path
        /// or <c>null</c> on failure.
        /// </summary>
        public static string? DumpSnapshotNow()
        {
            var snap = SceneCache.Snapshot;
            if (snap.IsEmpty)
            {
                Log.WriteLine("[VisCheckDiag] Manual dump skipped: snapshot is empty.");
                return null;
            }
            try
            {
                var path = DumpSnapshotInternal(snap, reason: "manual");
                Log.WriteLine($"[VisCheckDiag] Snapshot dumped: {path}");
                return path;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[VisCheckDiag] Manual dump failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // â”€â”€ Hook: VisibilityWorker tick finished â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Called by <see cref="VisibilityWorker"/> at the end of every tick
        /// with the per-player results from that tick. No-op unless
        /// <see cref="LogVisibilityTicks"/> is on. Throttled to
        /// <see cref="TickLogIntervalMs"/> so 60 Hz worker ticks don't flood
        /// the file.
        /// </summary>
        public static void OnVisibilityTick(
            Vector3 eye,
            IReadOnlyList<VisibilityWorker.PlayerCheckResult> results,
            SceneSnapshot snap)
        {
            if (!LogVisibilityTicks || results is null || results.Count == 0) return;

            var nowMs = Environment.TickCount64;
            if (nowMs - _lastTickLogMs < TickLogIntervalMs) return;
            _lastTickLogMs = nowMs;

            try
            {
                lock (_tickLogLock)
                {
                    EnsureTickLogOpen();
                    if (_tickLogWriter is null) return;

                    long tickWall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    for (int i = 0; i < results.Count; i++)
                    {
                        var r = results[i];
                        var line = FormatTickLine(tickWall, eye, in r, snap);
                        _tickLogWriter.WriteLine(line);
                        // +2 for the line terminator; cheaper than re-stat'ing the file.
                        _tickLogBytes += line.Length + 2;
                    }
                    _tickLogWriter.Flush();

                    if (_tickLogBytes >= TickLogMaxBytes)
                        RollTickLog();
                }
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "vcdiag_tick_err",
                    TimeSpan.FromSeconds(30),
                    $"[VisCheckDiag] Tick log error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // â”€â”€ Hook: classifier rules edited â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Called by <see cref="VisibilityClassifier"/> after a rule edit +
        /// reclassification pass. <paramref name="flipsToSeeThrough"/> /
        /// <paramref name="flipsToBlocker"/> are the deltas vs. the previous
        /// classifier state, so the user can confirm a new pattern actually
        /// moved actors.
        /// </summary>
        public static void OnClassifierChanged(
            string trigger,
            uint   newLayerMask,
            string[] newGlobalPatterns,
            int    flipsToSeeThrough,
            int    flipsToBlocker)
        {
            if (!LogClassifierChanges) return;
            try
            {
                Directory.CreateDirectory(OutputDir);
                var path = Path.Combine(OutputDir, "classifier-history.jsonl");
                var sb = new StringBuilder(256);
                sb.Append('{');
                AppendField(sb, "wallMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());           AppendSep(sb);
                AppendField(sb, "trigger", trigger);                                                 AppendSep(sb);
                AppendField(sb, "mapId", SceneCache.Snapshot.MapId);                                 AppendSep(sb);
                AppendField(sb, "layerMask", $"0x{newLayerMask:X8}");                                AppendSep(sb);
                AppendField(sb, "flipsToSeeThru", flipsToSeeThrough);                                AppendSep(sb);
                AppendField(sb, "flipsToBlocker", flipsToBlocker);                                   AppendSep(sb);
                AppendArrayField(sb, "globalPatterns", newGlobalPatterns);
                sb.Append('}');
                File.AppendAllText(path, sb.ToString() + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[VisCheckDiag] Classifier-log error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // â”€â”€ Misc UI helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Opens the diagnostic folder in Explorer (creating it first if it
        /// doesn't exist yet). Used by the UI's "Open folder" button.
        /// </summary>
        public static void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(OutputDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = OutputDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[VisCheckDiag] OpenLogFolder failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Flushes + closes the tick log. Called from app exit / match end so
        /// the file is consistent on disk before the next session opens it.
        /// </summary>
        public static void Flush()
        {
            lock (_tickLogLock)
            {
                try { _tickLogWriter?.Flush(); } catch { /* best-effort */ }
            }
        }

        public static void CloseTickLog()
        {
            lock (_tickLogLock)
            {
                try { _tickLogWriter?.Dispose(); } catch { /* best-effort */ }
                _tickLogWriter = null;
                _tickLogPath   = null;
                _tickLogBytes  = 0;
            }
        }

        // â”€â”€ Snapshot dump implementation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string DumpSnapshotInternal(SceneSnapshot snap, string reason)
        {
            Directory.CreateDirectory(OutputDir);
            var mapId = string.IsNullOrEmpty(snap.MapId) ? "unknown" : SanitizeFilename(snap.MapId);
            var ts    = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var path  = Path.Combine(OutputDir, $"snapshot-{mapId}-{ts}.jsonl");

            // Buffer the whole file in memory then single-shot write â€” avoids
            // 10k tiny IO calls and keeps the dump atomic (no half-written
            // file if the process exits mid-dump).
            var sb = new StringBuilder(snap.Actors.Length * 256);

            // Header line â€” overall snapshot metadata, identified by "_type":"header"
            // so consumers can ignore it when streaming actor records.
            sb.Append('{');
            AppendField(sb, "_type", "header");                                                       AppendSep(sb);
            AppendField(sb, "reason", reason);                                                        AppendSep(sb);
            AppendField(sb, "wallMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());                AppendSep(sb);
            AppendField(sb, "mapId", snap.MapId);                                                     AppendSep(sb);
            AppendField(sb, "npPhysics", $"0x{snap.NpPhysics:X}");                                    AppendSep(sb);
            AppendField(sb, "buildTickMs", snap.BuildTickMs);                                         AppendSep(sb);
            AppendField(sb, "actorCount", snap.Actors.Length);                                        AppendSep(sb);
            AppendField(sb, "sourceActorCount", snap.SourceActorCount);                               AppendSep(sb);
            AppendField(sb, "meshCount", snap.Meshes.Length);                                         AppendSep(sb);
            AppendField(sb, "convexCount", snap.ConvexMeshes.Length);                                 AppendSep(sb);
            AppendField(sb, "hfCount", snap.HeightFields.Length);
            sb.Append('}').Append('\n');

            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var a = snap.Actors[i];
                AppendActorJson(sb, i, a, snap);
                sb.Append('\n');
            }

            File.WriteAllText(path, sb.ToString());
            _lastSnapshotDumpPath = path;
            return path;
        }

        private static void AppendActorJson(StringBuilder sb, int idx, CachedActor a, SceneSnapshot snap)
        {
            // Derived AABB extents â€” pre-compute here so consumers don't have
            // to re-derive in their analysis pipeline. Saves a column-math
            // pass when filtering "show me actors with extent > 100 m".
            var ext = a.WorldAabbMax - a.WorldAabbMin;
            var ctr = (a.WorldAabbMin + a.WorldAabbMax) * 0.5f;

            sb.Append('{');
            AppendField(sb, "i", idx);                                                                AppendSep(sb);
            AppendField(sb, "base", $"0x{a.ActorBase:X}");                                            AppendSep(sb);
            AppendField(sb, "name", a.Name ?? string.Empty);                                          AppendSep(sb);
            AppendField(sb, "layer", a.UnityLayer);                                                   AppendSep(sb);
            AppendField(sb, "laymask", $"0x{a.ShapeLayerMask:X8}");                                   AppendSep(sb);
            AppendField(sb, "grpmask", $"0x{a.ShapeGroupMask:X8}");                                   AppendSep(sb);
            AppendField(sb, "geom", a.GeometryType.ToString());                                       AppendSep(sb);
            AppendVec3(sb, "aabbMin", a.WorldAabbMin);                                                AppendSep(sb);
            AppendVec3(sb, "aabbMax", a.WorldAabbMax);                                                AppendSep(sb);
            AppendVec3(sb, "ctr", ctr);                                                               AppendSep(sb);
            AppendVec3(sb, "ext", ext);                                                               AppendSep(sb);
            AppendVec3(sb, "pos", a.WorldTransform.Position);                                         AppendSep(sb);
            AppendQuat(sb, "rot", a.WorldTransform.Rotation);                                         AppendSep(sb);
            AppendVec3(sb, "size", a.PrimitiveSize);                                                  AppendSep(sb);
            AppendField(sb, "meshIdx", a.MeshIndex);                                                  AppendSep(sb);
            AppendField(sb, "cvxIdx", a.ConvexMeshIndex);                                             AppendSep(sb);
            AppendField(sb, "hfIdx", a.HeightFieldIndex);                                             AppendSep(sb);
            AppendField(sb, "seeThru", a.IsSeeThrough);                                               AppendSep(sb);
            // Classifier reason: only worth computing for see-through actors â€”
            // for blockers there's no rule that fired and "Explain" returns
            // an empty string anyway.
            string reason = a.IsSeeThrough
                ? VisibilityClassifier.Explain(snap.MapId, a.ShapeLayerMask, a.Name)
                : string.Empty;
            AppendField(sb, "reason", reason);
            sb.Append('}');
        }

        // â”€â”€ Tick log implementation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void EnsureTickLogOpen()
        {
            if (_tickLogWriter is not null) return;
            Directory.CreateDirectory(OutputDir);
            var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            _tickLogPath  = Path.Combine(OutputDir, $"tick-{ts}.jsonl");
            // Append mode + share-read so the user can tail -f the file while it grows.
            var fs = new FileStream(_tickLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _tickLogWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = false };
            _tickLogBytes  = new FileInfo(_tickLogPath).Length;
        }

        private static void RollTickLog()
        {
            try { _tickLogWriter?.Dispose(); } catch { /* best-effort */ }
            _tickLogWriter = null;
            _tickLogPath   = null;
            _tickLogBytes  = 0;
        }

        private static string FormatTickLine(
            long wallMs, Vector3 eye, in VisibilityWorker.PlayerCheckResult r, SceneSnapshot snap)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendField(sb, "wallMs", wallMs);                                                        AppendSep(sb);
            AppendField(sb, "player", r.Name ?? string.Empty);                                        AppendSep(sb);
            AppendField(sb, "base", $"0x{r.PlayerBase:X}");                                           AppendSep(sb);
            AppendVec3(sb, "eye", eye);                                                               AppendSep(sb);
            AppendVec3(sb, "pos", r.LastKnownPos);                                                    AppendSep(sb);
            AppendField(sb, "dist", FormatFloat(r.Distance));                                         AppendSep(sb);
            AppendField(sb, "vis", r.Visible);                                                        AppendSep(sb);
            AppendField(sb, "bones", $"0b{Convert.ToString(r.BoneMask & 0x7, 2).PadLeft(3, '0')}");   AppendSep(sb);
            AppendField(sb, "us", FormatFloat(r.TimeUs));                                             AppendSep(sb);
            AppendField(sb, "blkIdx", r.BlockerActorIdx);

            // Blocker details â€” pulled from the snapshot at log time so the
            // consumer doesn't have to cross-reference snapshot dumps to know
            // what hit a player. Cheap (one indexed lookup per record).
            if (r.BlockerActorIdx >= 0 && r.BlockerActorIdx < snap.Actors.Length)
            {
                var blk = snap.Actors[r.BlockerActorIdx];
                AppendSep(sb);
                AppendField(sb, "blkName", blk.Name ?? string.Empty);                                 AppendSep(sb);
                AppendField(sb, "blkLayer", blk.UnityLayer);                                          AppendSep(sb);
                AppendField(sb, "blkGeom", blk.GeometryType.ToString());                              AppendSep(sb);
                AppendField(sb, "blkSeeThru", blk.IsSeeThrough);
            }

            sb.Append('}');
            return sb.ToString();
        }

        // â”€â”€ Tiny hand-rolled JSON emitter â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // System.Text.Json works fine but it allocates a JsonWriter per call
        // and adds 200+ bytes of overhead to every actor record. For a 10k-actor
        // dump that's 2 MB of pure overhead. This emitter is ~5 lines per type
        // and lets the GC stay quiet during the dump.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AppendSep(StringBuilder sb) => sb.Append(',');

        private static void AppendField(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append('"').Append(':').Append('"');
            AppendEscaped(sb, value);
            sb.Append('"');
        }

        private static void AppendField(StringBuilder sb, string name, int value)
            => sb.Append('"').Append(name).Append('"').Append(':').Append(value);

        private static void AppendField(StringBuilder sb, string name, long value)
            => sb.Append('"').Append(name).Append('"').Append(':').Append(value);

        private static void AppendField(StringBuilder sb, string name, bool value)
            => sb.Append('"').Append(name).Append('"').Append(':').Append(value ? "true" : "false");

        private static void AppendVec3(StringBuilder sb, string name, Vector3 v)
        {
            sb.Append('"').Append(name).Append('"').Append(':').Append('[')
              .Append(FormatFloat(v.X)).Append(',')
              .Append(FormatFloat(v.Y)).Append(',')
              .Append(FormatFloat(v.Z)).Append(']');
        }

        private static void AppendQuat(StringBuilder sb, string name, System.Numerics.Quaternion q)
        {
            sb.Append('"').Append(name).Append('"').Append(':').Append('[')
              .Append(FormatFloat(q.X)).Append(',')
              .Append(FormatFloat(q.Y)).Append(',')
              .Append(FormatFloat(q.Z)).Append(',')
              .Append(FormatFloat(q.W)).Append(']');
        }

        private static void AppendArrayField(StringBuilder sb, string name, string[]? values)
        {
            sb.Append('"').Append(name).Append('"').Append(':').Append('[');
            if (values is not null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"');
                    AppendEscaped(sb, values[i] ?? string.Empty);
                    sb.Append('"');
                }
            }
            sb.Append(']');
        }

        private static void AppendEscaped(StringBuilder sb, string s)
        {
            // Minimal JSON string escaping â€” name fields come from Unity
            // GameObject names which are usually printable ASCII, but a
            // backslash, quote, or control char does occasionally appear
            // (and would otherwise break JSONL parsers).
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append('\\').Append('"');  break;
                    case '\\': sb.Append('\\').Append('\\'); break;
                    case '\n': sb.Append('\\').Append('n');  break;
                    case '\r': sb.Append('\\').Append('r');  break;
                    case '\t': sb.Append('\\').Append('t');  break;
                    default:
                        if (c < 0x20) sb.Append('?');        // ditch other control chars
                        else          sb.Append(c);
                        break;
                }
            }
        }

        // Fixed-format with 4 decimals â€” short enough for 10k-actor dumps,
        // precise enough for world-space metres / quaternions.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string FormatFloat(float v)
            => float.IsFinite(v) ? v.ToString("0.####", CultureInfo.InvariantCulture) : "0";

        private static string SanitizeFilename(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
