using ImGuiNET;
using eft_dma_radar.Silk;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Comprehensive visibility-check debug overlay.
    /// <para>
    /// Left column: live monitoring â€” cache state, snapshot stats, worker stats,
    /// per-player check results with filtering and optional bone-mask / blocker columns.
    /// </para>
    /// <para>
    /// Right column: tuning controls â€” per-bone ray toggles, max distance,
    /// the full <see cref="ClassifierRulesWidget"/> (layer mask + name patterns),
    /// and action buttons.
    /// </para>
    /// <para>Toggled with <b>F11</b>.</para>
    /// </summary>
    internal static class VisCheckDebugWindow
    {
        public static bool IsVisible { get; set; }
        public static void Toggle() => IsVisible = !IsVisible;

        // â”€â”€ Per-player table options â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static int  _playerFilter = 0;   // 0=All  1=Visible  2=Blocked
        private static bool _colBones     = true;
        private static bool _colBlocker   = true;

        // â”€â”€ Top blockers state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Persists across frames so the user can keep a popup open while
        // looking at the table. Cleared when the popup closes.
        private static int    _blockerAddPopupActorIdx = -1;
        private static string _blockerAddPopupName     = "";
        private static string _blockerCustomPattern    = "";
        private static string _blockerAddStatus        = "";
        private static long   _blockerAddStatusMs;

        // â”€â”€ Top see-through state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Same shape as the blocker popup state but for the mirror workflow:
        // user is browsing see-through actors and wants to promote one to a
        // force-blocker rule. Separate state vars so the two popups can't
        // collide if the user opens one then the other.
        private static string _seeThruAddPopupName  = "";
        private static string _seeThruCustomPattern = "";
        private static string _seeThruAddStatus     = "";
        private static long   _seeThruAddStatusMs;

        // Cached aggregation of see-through actors from the current snapshot.
        // Unlike Top Blockers (live hit window), see-through actors aren't
        // tested per-tick so there's no natural live signal â€” we just count
        // occurrences in the snapshot. ReferenceEquals(snap) gate avoids
        // re-aggregating 7k+ names every UI frame.
        private static SceneSnapshot? _seeThruSnapshotRef;
        private static List<(string Name, int Count, int FirstIdx)> _seeThruByName = new();
        private static string _seeThruFilter = "";

        // â”€â”€ Frame entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public static void Draw()
        {
            if (!IsVisible) return;

            var io = ImGui.GetIO();
            ImGui.SetNextWindowSizeConstraints(new Vector2(640f, 440f), io.DisplaySize);
            ImGui.SetNextWindowSize(new Vector2(900f, 640f), ImGuiCond.FirstUseEver);

            bool open = IsVisible;
            if (!ImGui.Begin("VisCheck Debug", ref open,
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                IsVisible = open;
                ImGui.End();
                return;
            }
            IsVisible = open;

            try
            {
                // Two-column layout: monitor (flex) | settings (330 px fixed).
                const float SettingsWidth = 330f;
                float regionW   = ImGui.GetContentRegionAvail().X;
                float monitorW  = MathF.Max(200f, regionW - SettingsWidth - 8f);

                if (ImGui.BeginChild("##vcd_mon", new Vector2(monitorW, 0), ImGuiChildFlags.Borders))
                    DrawMonitorColumn();
                ImGui.EndChild();

                ImGui.SameLine();

                if (ImGui.BeginChild("##vcd_set", new Vector2(0, 0), ImGuiChildFlags.Borders))
                    DrawSettingsColumn();
                ImGui.EndChild();
            }
            finally
            {
                ImGui.End();
            }
        }

        // â”€â”€ Monitor column â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawMonitorColumn()
        {
            DrawCacheBlock();
            ImGui.Separator();
            DrawSnapshotBlock();
            ImGui.Separator();
            DrawWorkerStatsBlock();
            ImGui.Separator();
            DrawTopBlockersBlock();
            ImGui.Separator();
            DrawTopSeeThroughBlock();
            ImGui.Separator();
            DrawPerPlayerBlock();
        }

        private static void DrawCacheBlock()
        {
            var (lbl, col) = SceneCache.State switch
            {
                SceneCacheState.Ready    => ("READY",    new Vector4(0.30f, 0.85f, 0.30f, 1f)),
                SceneCacheState.Building => ("BUILDING", new Vector4(0.95f, 0.85f, 0.20f, 1f)),
                SceneCacheState.Failed   => ("FAILED",   new Vector4(0.95f, 0.30f, 0.25f, 1f)),
                _                        => ("IDLE",     new Vector4(0.65f, 0.65f, 0.65f, 1f)),
            };
            ImGui.Text("Cache:"); ImGui.SameLine();
            ImGui.TextColored(col, lbl);
            ImGui.SameLine(0, 12);
            ImGui.TextDisabled(
                $"NpPhysics=0x{SceneCache.LastNpPhysics:X}  SDK RVA=0x{SceneCache.LastSdkRva:X8}");

            if (SceneCache.LastBuildStartedUtc != default)
            {
                double age = (DateTime.UtcNow - SceneCache.LastBuildStartedUtc).TotalSeconds;
                ImGui.Text(
                    $"Build: {SceneCache.LastBuildDuration.TotalMilliseconds:F0}ms  " +
                    $"{age:F0}s ago  " +
                    $"ok={SceneCache.BuildSuccessCount}  fail={SceneCache.BuildFailureCount}");
            }
            else
            {
                ImGui.TextDisabled("Build: (never)");
            }

            int totalSkip = SceneCache.LastSkippedNonRigid
                          + SceneCache.LastSkippedReadError
                          + SceneCache.LastSkippedBadGeometry
                          + SceneCache.LastSkippedCollider;
            if (totalSkip > 0)
            {
                var sc = SceneCache.Snapshot.Actors.Length == 0
                    ? new Vector4(0.95f, 0.40f, 0.40f, 1f)
                    : new Vector4(0.75f, 0.70f, 0.40f, 1f);
                ImGui.TextColored(sc,
                    $"Skipped: non-rigid={SceneCache.LastSkippedNonRigid}  " +
                    $"read-err={SceneCache.LastSkippedReadError}  " +
                    $"bad-geom={SceneCache.LastSkippedBadGeometry}  " +
                    $"collider-dup={SceneCache.LastSkippedCollider}");
            }

            if (!string.IsNullOrEmpty(SceneCache.LastError))
                ImGui.TextColored(new Vector4(0.95f, 0.40f, 0.40f, 1f),
                    $"Error: {SceneCache.LastError}");
        }

        private static void DrawSnapshotBlock()
        {
            var snap = SceneCache.Snapshot;
            ImGui.Text(
                $"Snapshot '{snap.MapId}':  actors={snap.Actors.Length}  " +
                $"meshes={snap.Meshes.Length}  convex={snap.ConvexMeshes.Length}  " +
                $"hf={snap.HeightFields.Length}  built {AgeText(snap.BuildTickMs)}");

            if (snap.Actors.Length > 0)
            {
                int nS=0,nPl=0,nCap=0,nBox=0,nC=0,nTri=0,nHf=0,nOth=0,nST=0;
                for (int i = 0; i < snap.Actors.Length; i++)
                {
                    switch (snap.Actors[i].GeometryType)
                    {
                        case PxGeometryType.Sphere:       nS++;   break;
                        case PxGeometryType.Plane:        nPl++;  break;
                        case PxGeometryType.Capsule:      nCap++; break;
                        case PxGeometryType.Box:          nBox++; break;
                        case PxGeometryType.ConvexMesh:   nC++;   break;
                        case PxGeometryType.TriangleMesh: nTri++; break;
                        case PxGeometryType.HeightField:  nHf++;  break;
                        default:                          nOth++; break;
                    }
                    if (snap.Actors[i].IsSeeThrough) nST++;
                }
                ImGui.TextDisabled(
                    $"  sph={nS} plane={nPl} cap={nCap} box={nBox} " +
                    $"convex={nC} tri={nTri} hf={nHf} other={nOth}  " +
                    $"see-thru={nST}/{snap.Actors.Length}");
            }
        }

        private static void DrawWorkerStatsBlock()
        {
            var (wlbl, wcol) = VisibilityWorker.Enabled
                ? ("RUNNING", new Vector4(0.30f, 0.85f, 0.30f, 1f))
                : ("STOPPED", new Vector4(0.65f, 0.65f, 0.65f, 1f));
            ImGui.Text("Worker:"); ImGui.SameLine();
            ImGui.TextColored(wcol, wlbl);

            var stats = VisibilityWorker.LastTickStats;
            long tickAge = stats.TickMs == 0 ? -1 : Environment.TickCount64 - stats.TickMs;
            string ageStr = tickAge < 0 ? "" : $"  (tick {tickAge}ms ago)";
            string pct    = stats.Checks > 0
                ? $" ({100.0 * stats.Blocked / stats.Checks:F0}%)"
                : "";
            ImGui.Text(
                $"checks={stats.Checks}  blocked={stats.Blocked}{pct}  " +
                $"avg={stats.AvgUs:F1}Î¼s  max={stats.MaxUs:F1}Î¼s{ageStr}");
            ImGui.Text(
                $"eye: ({stats.EyePos.X:F1}, {stats.EyePos.Y:F1}, {stats.EyePos.Z:F1})  " +
                $"max-dist={VisibilityWorker.MaxRayDistance:F0}m");
        }

        /// <summary>
        /// Aggregated "what's actually blocking my sightlines right now" view â€”
        /// the single most useful surface for tuning classifier rules. Each
        /// row is one actor that's caused â‰¥1 block in the rolling 30 s window;
        /// the +SeeThru button opens a smart-pattern picker so the user can
        /// promote the actor to a see-through rule without typing a substring
        /// blind from the per-player table.
        /// </summary>
        private static void DrawTopBlockersBlock()
        {
            var top = BlockerHistory.GetTop(50);
            int totalHits = BlockerHistory.TotalHits;

            // Header line. Always show it (even when empty) so the user knows
            // the panel exists and the tracker is running.
            ImGui.Text(
                $"Top Blockers (last {BlockerHistory.WindowMs / 1000}s):  " +
                $"actors={top.Count}  total hits={totalHits}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Rolling-window aggregation of which actors have blocked a\n" +
                    "player sightline. Click the +SeeThru button on a row to add\n" +
                    "it as a classifier rule (with live impact preview).");

            if (top.Count == 0)
            {
                ImGui.TextDisabled("  (nothing has blocked yet â€” play a few seconds with worker enabled)");
                MaybeDrawBlockerPopup();
                return;
            }

            var snap = SceneCache.Snapshot;
            long now = Environment.TickCount64;

            // Status line â€” confirmation of the most recent rule add.
            if (!string.IsNullOrEmpty(_blockerAddStatus) && now - _blockerAddStatusMs < 4000)
            {
                ImGui.TextColored(new Vector4(0.40f, 0.85f, 0.40f, 1f), _blockerAddStatus);
            }

            // Fixed table height so the Per-Player table below stays usable.
            // 6 rows visible at standard ImGui line height â€” scroll for more.
            const float TableHeight = 140f;
            if (ImGui.BeginTable("##topblk", 5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                    new Vector2(0, TableHeight)))
            {
                ImGui.TableSetupColumn("Name",   ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Layer",  ImGuiTableColumnFlags.WidthFixed,   42f);
                ImGui.TableSetupColumn("Hits",   ImGuiTableColumnFlags.WidthFixed,   48f);
                ImGui.TableSetupColumn("Players", ImGuiTableColumnFlags.WidthFixed,  56f);
                ImGui.TableSetupColumn("",       ImGuiTableColumnFlags.WidthFixed,   62f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                for (int i = 0; i < top.Count; i++)
                {
                    var agg = top[i];
                    if (agg.ActorIdx < 0 || agg.ActorIdx >= snap.Actors.Length) continue;
                    var a = snap.Actors[agg.ActorIdx];

                    ImGui.TableNextRow();
                    int col = 0;

                    ImGui.TableSetColumnIndex(col++);
                    string display = string.IsNullOrEmpty(a.Name) ? "(unnamed)" : a.Name;
                    ImGui.TextUnformatted(display);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            $"Name: {display}\n" +
                            $"Layer: {a.UnityLayer} (mask 0x{a.ShapeLayerMask:X})\n" +
                            $"Geometry: {a.GeometryType}\n" +
                            $"ActorBase: 0x{a.ActorBase:X}\n" +
                            $"Currently classified: {(a.IsSeeThrough ? "see-through" : "blocker")}");

                    ImGui.TableSetColumnIndex(col++);
                    ImGui.Text(a.UnityLayer.ToString());

                    ImGui.TableSetColumnIndex(col++);
                    ImGui.Text(agg.Count.ToString());

                    ImGui.TableSetColumnIndex(col++);
                    ImGui.Text(agg.UniquePlayers.ToString());

                    ImGui.TableSetColumnIndex(col);
                    ImGui.PushID(i);
                    if (ImGui.SmallButton("+SeeThru"))
                    {
                        _blockerAddPopupActorIdx = agg.ActorIdx;
                        _blockerAddPopupName     = a.Name ?? "";
                        _blockerCustomPattern    = a.Name ?? "";
                        ImGui.OpenPopup("##add_seethru_popup");
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "Promote this actor to a see-through classifier rule.\n" +
                            "Opens a picker with full-name / stripped-suffix / prefix\n" +
                            "options + live match counts so you can pick the right\n" +
                            "level of broadness before committing.");
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }

            MaybeDrawBlockerPopup();
        }

        /// <summary>
        /// Modal pattern-picker popup. Shows three candidate substrings derived
        /// from the actor's name â€” full / stripped / prefix â€” each with a live
        /// preview of how many actors the pattern would match and how many of
        /// those are currently blockers. The user picks one (or types a custom
        /// substring) and commits.
        /// </summary>
        private static void MaybeDrawBlockerPopup()
        {
            if (!ImGui.BeginPopup("##add_seethru_popup"))
                return;
            try
            {
                ImGui.TextDisabled("Add see-through rule for:");
                ImGui.TextUnformatted(_blockerAddPopupName);
                ImGui.Separator();

                var (stripped, prefix) = SuggestPatterns(_blockerAddPopupName);
                var snap = SceneCache.Snapshot;

                ImGui.TextDisabled("Pick a pattern (broader â†’ more matches):");

                DrawPatternCandidate("Full name",   _blockerAddPopupName, snap);
                if (!string.IsNullOrEmpty(stripped) && stripped != _blockerAddPopupName)
                    DrawPatternCandidate("Strip _##/numeric tail", stripped, snap);
                if (!string.IsNullOrEmpty(prefix) && prefix != stripped && prefix != _blockerAddPopupName)
                    DrawPatternCandidate("Prefix bucket", prefix, snap);

                ImGui.Separator();
                ImGui.TextDisabled("Or custom substring:");
                ImGui.SetNextItemWidth(300f);
                ImGui.InputText("##custom_pat", ref _blockerCustomPattern, 128);
                int custMatch = string.IsNullOrEmpty(_blockerCustomPattern)
                    ? 0
                    : CountMatches(snap, _blockerCustomPattern);
                ImGui.Text($"  â†’ {custMatch} actor(s) match");
                ImGui.SameLine();
                if (ImGui.Button("Add custom") && !string.IsNullOrWhiteSpace(_blockerCustomPattern))
                {
                    CommitPattern(_blockerCustomPattern.Trim());
                    ImGui.CloseCurrentPopup();
                }

                ImGui.Separator();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            }
            finally
            {
                ImGui.EndPopup();
            }
        }

        /// <summary>
        /// Renders one row in the pattern-picker popup: label + the literal
        /// pattern string + live "N actors match, M currently blockers" + an
        /// Apply button. Encapsulates the impact preview so all three candidate
        /// rows behave identically and the user reads the same shape every time.
        /// </summary>
        private static void DrawPatternCandidate(string label, string pattern, SceneSnapshot snap)
        {
            ImGui.PushID(label);
            (int matches, int blockers) = CountMatchesWithBlockerSplit(snap, pattern);

            ImGui.TextUnformatted($"  {label}: \"{pattern}\"");
            ImGui.TextDisabled($"     â†’ {matches} match, {blockers} currently blocker");
            ImGui.SameLine();
            if (ImGui.SmallButton("Apply"))
            {
                CommitPattern(pattern);
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopID();
        }

        /// <summary>
        /// Heuristic pattern derivation from an actor name. Returns two
        /// candidate substrings:
        /// <list type="bullet">
        ///   <item><c>stripped</c> â€” the name with the last "_NN" or trailing
        ///     digit run removed. Catches instance siblings
        ///     ("Wall_concrete_01", "Wall_concrete_02", ...).</item>
        ///   <item><c>prefix</c> â€” everything up to the first separator. The
        ///     broadest sensible filter; matches the bucketing logic in
        ///     <see cref="CacheViewWindow"/>.</item>
        /// </list>
        /// Either may be the empty string when the input is degenerate
        /// (single token, all digits, etc.) â€” the caller suppresses degenerate
        /// rows so the popup never shows a useless "  â†’ 0 match" entry.
        /// </summary>
        private static (string stripped, string prefix) SuggestPatterns(string name)
        {
            if (string.IsNullOrEmpty(name)) return ("", "");

            // Strip trailing "_NN" or " NN" or "(NN)" suffix.
            string stripped = name;
            int last = stripped.Length;
            while (last > 0 && (char.IsDigit(stripped[last - 1]) || stripped[last - 1] == ' '
                                || stripped[last - 1] == '_'    || stripped[last - 1] == '('
                                || stripped[last - 1] == ')'))
            {
                last--;
            }
            if (last > 0 && last < stripped.Length) stripped = stripped.Substring(0, last);

            // Prefix bucket â€” first underscore / space / paren / digit run.
            string prefix = "";
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_' || c == ' ' || c == '(' || c == '-' || (c >= '0' && c <= '9'))
                {
                    if (i > 0) prefix = name.Substring(0, i);
                    break;
                }
            }
            if (string.IsNullOrEmpty(prefix)) prefix = stripped;

            return (stripped, prefix);
        }

        private static int CountMatches(SceneSnapshot snap, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            int n = 0;
            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var nm = snap.Actors[i].Name;
                if (!string.IsNullOrEmpty(nm)
                    && nm.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    n++;
            }
            return n;
        }

        private static (int matches, int blockers) CountMatchesWithBlockerSplit(
            SceneSnapshot snap, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return (0, 0);
            int m = 0, b = 0;
            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var a = snap.Actors[i];
                if (a.Name is null) continue;
                if (!a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                m++;
                if (!a.IsSeeThrough) b++;
            }
            return (m, b);
        }

        /// <summary>
        /// Adds <paramref name="pattern"/> to the live classifier as a global
        /// see-through rule, triggers reclassification, persists to config,
        /// and surfaces a status line so the user sees the change took effect.
        /// </summary>
        private static void CommitPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;

            // Avoid silently appending duplicates â€” a no-op duplicate would
            // confuse the impact preview (the user wonders why nothing changed).
            var current = VisibilityClassifier.GlobalNamePatterns;
            foreach (var p in current)
            {
                if (string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    _blockerAddStatus   = $"Pattern \"{pattern}\" already exists â€” no change.";
                    _blockerAddStatusMs = Environment.TickCount64;
                    return;
                }
            }

            var next = new string[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = pattern;
            VisibilityClassifier.GlobalNamePatterns = next;

            VisibilityClassifier.Reclassify(SceneCache.Snapshot);
            VisibilityClassifier.SaveToConfig(SilkProgram.Config);
            SilkProgram.Config.Save();

            _blockerAddStatus   = $"Added \"{pattern}\" â€” see-through rules now {next.Length}.";
            _blockerAddStatusMs = Environment.TickCount64;
        }

        /// <summary>
        /// Mirror of <see cref="CommitPattern"/> for the force-blocker side â€”
        /// appends to <see cref="VisibilityClassifier.GlobalBlockerPatterns"/>
        /// instead of <c>GlobalNamePatterns</c>. Separate status field so the
        /// two parallel popups can both show their own feedback without
        /// stomping each other's success line.
        /// </summary>
        private static void CommitBlockerPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            var current = VisibilityClassifier.GlobalBlockerPatterns;
            foreach (var p in current)
            {
                if (string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    _seeThruAddStatus   = $"Pattern \"{pattern}\" already exists â€” no change.";
                    _seeThruAddStatusMs = Environment.TickCount64;
                    return;
                }
            }

            var next = new string[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = pattern;
            VisibilityClassifier.GlobalBlockerPatterns = next;

            VisibilityClassifier.Reclassify(SceneCache.Snapshot);
            VisibilityClassifier.SaveToConfig(SilkProgram.Config);
            SilkProgram.Config.Save();

            _seeThruAddStatus   = $"Added \"{pattern}\" â€” force-blocker rules now {next.Length}.";
            _seeThruAddStatusMs = Environment.TickCount64;
        }

        /// <summary>
        /// Browseable view of currently-see-through actors so the user can
        /// promote a misclassified actor to a force-blocker rule. Mirror of
        /// <see cref="DrawTopBlockersBlock"/>, but the aggregation is over the
        /// snapshot (see-through actors don't get hit per-tick, so there's no
        /// live frequency signal to sort by â€” instead we sort by occurrence
        /// count in the snapshot, which still surfaces "this name appears
        /// 384 times â€” almost certainly a real prop group" first).
        /// </summary>
        private static void DrawTopSeeThroughBlock()
        {
            var snap = SceneCache.Snapshot;
            RebuildSeeThroughCacheIfStale(snap);

            ImGui.Text(
                $"Top See-Through Actors (snapshot):  " +
                $"names={_seeThruByName.Count}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Aggregated counts of currently-see-through actors from\n" +
                    "the snapshot. Click +Blocker on a row to promote it to a\n" +
                    "force-blocker rule (overrides the see-through verdict).\n" +
                    "Useful when a layer-mask rule is too broad and a few\n" +
                    "specific actors on that layer should still block.");

            if (_seeThruByName.Count == 0)
            {
                ImGui.TextDisabled("  (no see-through actors in current snapshot)");
                MaybeDrawSeeThruPopup();
                return;
            }

            long now = Environment.TickCount64;
            if (!string.IsNullOrEmpty(_seeThruAddStatus) && now - _seeThruAddStatusMs < 4000)
                ImGui.TextColored(new Vector4(0.95f, 0.55f, 0.35f, 1f), _seeThruAddStatus);

            float fw = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(fw);
            ImGui.InputTextWithHint("##stf", "filter namesâ€¦", ref _seeThruFilter, 64);

            const float TableHeight = 140f;
            if (ImGui.BeginTable("##topst", 4,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                    new Vector2(0, TableHeight)))
            {
                ImGui.TableSetupColumn("Name",  ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Layer", ImGuiTableColumnFlags.WidthFixed,   42f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed,   56f);
                ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed,   62f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                for (int i = 0; i < _seeThruByName.Count; i++)
                {
                    var (name, count, firstIdx) = _seeThruByName[i];

                    if (_seeThruFilter.Length > 0
                        && name.IndexOf(_seeThruFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if ((uint)firstIdx >= (uint)snap.Actors.Length) continue;
                    var firstActor = snap.Actors[firstIdx];

                    ImGui.TableNextRow();
                    int col = 0;

                    ImGui.TableSetColumnIndex(col++);
                    ImGui.TextUnformatted(string.IsNullOrEmpty(name) ? "(unnamed)" : name);
                    if (ImGui.IsItemHovered())
                    {
                        string reason = VisibilityClassifier.Explain(
                            snap.MapId, firstActor.ShapeLayerMask, firstActor.Name);
                        ImGui.SetTooltip(
                            $"Name: {(string.IsNullOrEmpty(name) ? "(unnamed)" : name)}\n" +
                            $"Count in snapshot: {count}\n" +
                            $"See-through reason: {reason}\n" +
                            "Click +Blocker to force the see-through rule to skip this name.");
                    }

                    ImGui.TableSetColumnIndex(col++);
                    ImGui.Text(firstActor.UnityLayer.ToString());

                    ImGui.TableSetColumnIndex(col++);
                    ImGui.Text(count.ToString());

                    ImGui.TableSetColumnIndex(col);
                    ImGui.PushID(i);
                    if (ImGui.SmallButton("+Blocker"))
                    {
                        _seeThruAddPopupName  = name ?? "";
                        _seeThruCustomPattern = name ?? "";
                        ImGui.OpenPopup("##add_blocker_popup");
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "Promote this actor to a force-blocker rule.\n" +
                            "Opens a picker with full-name / stripped-suffix /\n" +
                            "prefix options + live preview of how many\n" +
                            "see-through actors would flip back to blocker.");
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }

            MaybeDrawSeeThruPopup();
        }

        private static void MaybeDrawSeeThruPopup()
        {
            if (!ImGui.BeginPopup("##add_blocker_popup"))
                return;
            try
            {
                ImGui.TextDisabled("Add force-blocker rule for:");
                ImGui.TextUnformatted(_seeThruAddPopupName);
                ImGui.Separator();

                var (stripped, prefix) = SuggestPatterns(_seeThruAddPopupName);
                var snap = SceneCache.Snapshot;

                ImGui.TextDisabled("Pick a pattern (broader â†’ flips more actors):");

                DrawBlockerCandidate("Full name",   _seeThruAddPopupName, snap);
                if (!string.IsNullOrEmpty(stripped) && stripped != _seeThruAddPopupName)
                    DrawBlockerCandidate("Strip _##/numeric tail", stripped, snap);
                if (!string.IsNullOrEmpty(prefix) && prefix != stripped && prefix != _seeThruAddPopupName)
                    DrawBlockerCandidate("Prefix bucket", prefix, snap);

                ImGui.Separator();
                ImGui.TextDisabled("Or custom substring:");
                ImGui.SetNextItemWidth(300f);
                ImGui.InputText("##custom_blk_pat", ref _seeThruCustomPattern, 128);
                int custMatch = string.IsNullOrEmpty(_seeThruCustomPattern)
                    ? 0
                    : CountMatches(snap, _seeThruCustomPattern);
                int custSee = string.IsNullOrEmpty(_seeThruCustomPattern)
                    ? 0
                    : CountMatchesSeeThrough(snap, _seeThruCustomPattern);
                ImGui.Text($"  â†’ {custMatch} actor(s) match, {custSee} currently see-through");
                ImGui.SameLine();
                if (ImGui.Button("Add custom") && !string.IsNullOrWhiteSpace(_seeThruCustomPattern))
                {
                    CommitBlockerPattern(_seeThruCustomPattern.Trim());
                    ImGui.CloseCurrentPopup();
                }

                ImGui.Separator();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            }
            finally
            {
                ImGui.EndPopup();
            }
        }

        /// <summary>
        /// Mirror of <see cref="DrawPatternCandidate"/> for blocker rules. Renders
        /// "N actors match, M currently see-through (would flip back to blocker)"
        /// so the user picks the right specificity. Apply hits
        /// <see cref="CommitBlockerPattern"/> instead of the see-through commit.
        /// </summary>
        private static void DrawBlockerCandidate(string label, string pattern, SceneSnapshot snap)
        {
            ImGui.PushID(label);
            int matches = CountMatches(snap, pattern);
            int wouldFlip = CountMatchesSeeThrough(snap, pattern);

            ImGui.TextUnformatted($"  {label}: \"{pattern}\"");
            ImGui.TextDisabled($"     â†’ {matches} match, {wouldFlip} currently see-through (would flip back to blocker)");
            ImGui.SameLine();
            if (ImGui.SmallButton("Apply"))
            {
                CommitBlockerPattern(pattern);
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopID();
        }

        private static int CountMatchesSeeThrough(SceneSnapshot snap, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            int n = 0;
            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var a = snap.Actors[i];
                if (a.Name is null) continue;
                if (!a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.IsSeeThrough) n++;
            }
            return n;
        }

        /// <summary>
        /// Rebuilds <see cref="_seeThruByName"/> when the snapshot ref changes.
        /// Groups by exact actor name so the user sees natural clusters
        /// ("collider_mesh" Ã— 384, "Glass" Ã— 129, etc.) â€” same shape as the
        /// existing Cache View bucket panel but simpler (no bucket extraction
        /// since the natural unit here is the full name).
        /// </summary>
        private static void RebuildSeeThroughCacheIfStale(SceneSnapshot snap)
        {
            if (ReferenceEquals(snap, _seeThruSnapshotRef)) return;
            _seeThruSnapshotRef = snap;

            var counts = new Dictionary<string, (int Count, int FirstIdx)>(StringComparer.Ordinal);
            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var a = snap.Actors[i];
                if (!a.IsSeeThrough) continue;
                var nm = a.Name ?? "";
                if (counts.TryGetValue(nm, out var c))
                    counts[nm] = (c.Count + 1, c.FirstIdx);
                else
                    counts[nm] = (1, i);
            }

            _seeThruByName = counts
                .Select(kv => (kv.Key, kv.Value.Count, kv.Value.FirstIdx))
                .OrderByDescending(t => t.Count)
                .Take(200)
                .ToList();
        }

        private static void DrawPerPlayerBlock()
        {
            // â”€â”€ Filter + column toggles â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            ImGui.Text("Players:");
            ImGui.SameLine();
            ImGui.RadioButton("All",     ref _playerFilter, 0); ImGui.SameLine();
            ImGui.RadioButton("Visible", ref _playerFilter, 1); ImGui.SameLine();
            ImGui.RadioButton("Blocked", ref _playerFilter, 2);
            ImGui.SameLine(0, 14f);
            ImGui.TextDisabled("cols:");
            ImGui.SameLine();
            ImGui.Checkbox("Bones##col",   ref _colBones);
            ImGui.SameLine();
            ImGui.Checkbox("Blocker##col", ref _colBlocker);

            var rows = VisibilityWorker.LastPerPlayer;
            if (rows.Count == 0)
            {
                ImGui.TextDisabled("  (no checks yet â€” start the worker and build the cache)");
                return;
            }

            // Dynamic column count based on active toggles.
            int nCols = 4; // Name | Dist | Status | Î¼s â€” always visible
            if (_colBones)   nCols++;
            if (_colBlocker) nCols++;

            var  snap   = SceneCache.Snapshot;
            float tableH = ImGui.GetContentRegionAvail().Y - 4f;

            if (ImGui.BeginTable("##vcdpl", nCols,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders |
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                new Vector2(0, tableH)))
            {
                ImGui.TableSetupColumn("Name",    ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Dist",    ImGuiTableColumnFlags.WidthFixed,   52f);
                ImGui.TableSetupColumn("Status",  ImGuiTableColumnFlags.WidthFixed,   64f);
                if (_colBones)
                    ImGui.TableSetupColumn("Bones",   ImGuiTableColumnFlags.WidthFixed, 50f);
                if (_colBlocker)
                    ImGui.TableSetupColumn("Blocker", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Î¼s",      ImGuiTableColumnFlags.WidthFixed,   64f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                var visCol = new Vector4(0.30f, 0.85f, 0.30f, 1f);
                var blkCol = new Vector4(0.85f, 0.45f, 0.25f, 1f);

                foreach (var r in rows)
                {
                    if (_playerFilter == 1 && !r.Visible) continue;
                    if (_playerFilter == 2 &&  r.Visible) continue;

                    ImGui.TableNextRow();
                    int col = 0;

                    // Name
                    ImGui.TableSetColumnIndex(col++);
                    ImGui.TextUnformatted(string.IsNullOrEmpty(r.Name) ? "(unnamed)" : r.Name);

                    // Distance
                    ImGui.TableSetColumnIndex(col++);
                    ImGui.Text($"{r.Distance:F0}m");

                    // Status
                    ImGui.TableSetColumnIndex(col++);
                    ImGui.TextColored(r.Visible ? visCol : blkCol,
                        r.Visible ? "visible" : "blocked");

                    // Bone mask: uppercase = bone visible, lowercase = blocked / disabled
                    if (_colBones)
                    {
                        ImGui.TableSetColumnIndex(col++);
                        bool hv = (r.BoneMask & (1u << 0)) != 0;
                        bool cv = (r.BoneMask & (1u << 1)) != 0;
                        bool pv = (r.BoneMask & (1u << 2)) != 0;
                        ImGui.TextUnformatted(
                            $"{(hv ? 'H' : 'h')}{(cv ? 'C' : 'c')}{(pv ? 'P' : 'p')}");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(
                                "H=head  C=chest  P=pelvis\n" +
                                "Uppercase = bone is visible from your eye.\n" +
                                "Lowercase = bone is blocked or bone check is disabled.");
                    }

                    // Blocker actor name
                    if (_colBlocker)
                    {
                        ImGui.TableSetColumnIndex(col++);
                        if (!r.Visible
                            && r.BlockerActorIdx >= 0
                            && r.BlockerActorIdx < snap.Actors.Length)
                        {
                            var bl = snap.Actors[r.BlockerActorIdx];
                            string nm = string.IsNullOrEmpty(bl.Name)
                                ? $"({bl.GeometryType})"
                                : bl.Name;
                            if (nm.Length > 38) nm = nm.Substring(0, 37) + "â€¦";
                            ImGui.TextUnformatted(nm);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(
                                    $"Layer {bl.UnityLayer} (mask 0x{bl.ShapeLayerMask:X})\n" +
                                    $"Type: {bl.GeometryType}\n" +
                                    $"Actor: 0x{bl.ActorBase:X}");
                        }
                        else
                        {
                            ImGui.TextDisabled("â€”");
                        }
                    }

                    // Ray cast time
                    ImGui.TableSetColumnIndex(col);
                    ImGui.Text($"{r.TimeUs:F1}");
                }
                ImGui.EndTable();
            }
        }

        // â”€â”€ Settings column â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawSettingsColumn()
        {
            DrawWorkerSettingsSection();
            ImGui.Separator();
            DrawDiagnosticLoggingSection();
            ImGui.Separator();

            // Reserve space for the diagnostic section + actions block below
            // so the classifier scroll doesn't push them off-screen.
            if (ImGui.BeginChild("##vcd_classifier_scroll",
                    new Vector2(0, ImGui.GetContentRegionAvail().Y - 80f), ImGuiChildFlags.None))
            {
                ClassifierRulesWidget.Draw();
            }
            ImGui.EndChild();

            ImGui.Separator();
            DrawActionsSection();
        }

        private static void DrawDiagnosticLoggingSection()
        {
            ImGui.TextDisabled("Diagnostic Logging");

            bool dumpSnap = VisCheckDiagnostics.DumpSnapshotOnBuild;
            if (ImGui.Checkbox("Dump snapshot on build##diag", ref dumpSnap))
            {
                VisCheckDiagnostics.DumpSnapshotOnBuild = dumpSnap;
                SaveDiagCfg();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "After every SceneCache build/load, write the full snapshot to\n" +
                    "a JSONL file (one line per actor with name, layer, AABB,\n" +
                    "geometry, classifier verdict). One file per build.\n" +
                    "Typical size: ~3 MB for 10k actors.");

            bool logTicks = VisCheckDiagnostics.LogVisibilityTicks;
            if (ImGui.Checkbox("Log visibility ticks##diag", ref logTicks))
            {
                VisCheckDiagnostics.LogVisibilityTicks = logTicks;
                SaveDiagCfg();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Append per-player visibility results to a session-scoped log.\n" +
                    "Throttled to 10 Hz. Rolls over at 50 MB.\n" +
                    "Each line carries eye + player positions, distance, bone mask,\n" +
                    "blocker name + geometry, and ray duration.");

            bool logCls = VisCheckDiagnostics.LogClassifierChanges;
            if (ImGui.Checkbox("Log classifier edits##diag", ref logCls))
            {
                VisCheckDiagnostics.LogClassifierChanges = logCls;
                SaveDiagCfg();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "After every classifier rule change + reclassification, log\n" +
                    "the new rules + how many actors flipped see-through â†” blocker.\n" +
                    "Use to verify a new name pattern actually moved what you expected.");

            // VmmException tracer â€” answers "which read is throwing the
            // first-chance exception I see in the debugger?". Logs each
            // unique call site once with a full stack trace (capped at 200
            // sites to self-limit). Requires restart to take effect â€” the
            // FirstChanceException hook gets installed once at startup.
            bool traceDma = SilkProgram.Config.TraceDmaExceptions;
            if (ImGui.Checkbox("Trace DMA exceptions##diag", ref traceDma))
            {
                SilkProgram.Config.TraceDmaExceptions = traceDma;
                SilkProgram.Config.Save();
                // Live-flip if the hook is already installed; otherwise the
                // user has to restart for the AppDomain hook to register.
                ExceptionTracer.Enabled = traceDma;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Hook AppDomain.FirstChanceException and log each unique\n" +
                    "VmmException / BadPtrException call site once, with a full\n" +
                    "stack trace. Answers \"which read is throwing?\" without\n" +
                    "spamming the log (capped at 200 distinct sites).\n" +
                    "Takes effect on next launch if turned on before startup;\n" +
                    "live-toggle works once the hook is installed.");

            // Action row â€” dump on demand + folder open. Disabled when there's
            // no live snapshot to dump.
            bool hasSnap = SceneCache.Snapshot.Actors.Length > 0;
            ImGui.BeginDisabled(!hasSnap);
            if (ImGui.Button("Dump now##diag"))
                VisCheckDiagnostics.DumpSnapshotNow();
            if (ImGui.IsItemHovered() && hasSnap)
                ImGui.SetTooltip("Write the current snapshot to a new JSONL file regardless of toggle.");
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Open folder##diag"))
                VisCheckDiagnostics.OpenLogFolder();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(VisCheckDiagnostics.OutputDirectory);

            // Last-dump path readout â€” confirms the file landed and gives the
            // user something to copy/paste into their analysis tool.
            var lastDump = VisCheckDiagnostics.LastSnapshotDumpPath;
            if (!string.IsNullOrEmpty(lastDump))
                ImGui.TextDisabled($"Last: {Path.GetFileName(lastDump)}");
            var curTick = VisCheckDiagnostics.CurrentTickLogPath;
            if (!string.IsNullOrEmpty(curTick))
                ImGui.TextDisabled($"Tick: {Path.GetFileName(curTick)}");
        }

        private static void SaveDiagCfg()
        {
            VisCheckDiagnostics.SaveToConfig(SilkProgram.Config);
            SilkProgram.Config.Save();
        }

        private static void DrawWorkerSettingsSection()
        {
            ImGui.TextDisabled("Worker Settings");

            bool wEnabled = VisibilityWorker.Enabled;
            if (ImGui.Checkbox("Enabled##wk", ref wEnabled))
            {
                if (wEnabled) VisibilityWorker.Start();
                else          VisibilityWorker.Stop();
            }

            float maxDist = VisibilityWorker.MaxRayDistance;
            if (ImGui.SliderFloat("Max dist##wk", ref maxDist, 10f, 500f, "%.0f m"))
            {
                VisibilityWorker.MaxRayDistance = maxDist;
                VisibilityWorker.SaveToConfig(SilkProgram.Config);
                SilkProgram.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Players beyond this distance default to visible (no ray cast).");

            ImGui.Text("Check bones:");
            ImGui.SameLine();

            bool bH = VisibilityWorker.CheckHead;
            if (ImGui.Checkbox("H##b", ref bH)) { VisibilityWorker.CheckHead = bH; SaveWorkerCfg(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cast a ray toward the head bone.");
            ImGui.SameLine();

            bool bC = VisibilityWorker.CheckChest;
            if (ImGui.Checkbox("C##b", ref bC)) { VisibilityWorker.CheckChest = bC; SaveWorkerCfg(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cast a ray toward the chest (spine3) bone.");
            ImGui.SameLine();

            bool bP = VisibilityWorker.CheckPelvis;
            if (ImGui.Checkbox("P##b", ref bP)) { VisibilityWorker.CheckPelvis = bP; SaveWorkerCfg(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cast a ray toward the pelvis bone.");
        }

        private static void DrawActionsSection()
        {
            ImGui.TextDisabled("Actions");
            string mapId = Memory.Game?.MapID ?? string.Empty;
            bool hasmatch = !string.IsNullOrEmpty(mapId);
            bool busy     = SceneCache.State == SceneCacheState.Building;

            bool dropCol = SceneCache.DropMovementColliders;
            if (ImGui.Checkbox("Drop _COLLIDER duplicates", ref dropCol))
                SceneCache.DropMovementColliders = dropCol;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Skip EFT's *_COLLIDER movement shapes at build, keeping the\n" +
                    "paired *_BALLISTIC_* bullet/sight blockers. Roughly halves the\n" +
                    "snapshot with no loss for visibility or maps.\n" +
                    "Takes effect on the next Rebuild (use Invalidate + Rebuild to\n" +
                    "replace an existing on-disk snapshot).");

            ImGui.BeginDisabled(!hasmatch || busy);
            if (ImGui.Button("Rebuild"))
                SceneCache.TriggerBuild(mapId);
            ImGui.SameLine();
            if (ImGui.Button("Invalidate + Rebuild"))
            {
                SnapshotSerializer.TryDelete(mapId);
                SceneCache.TriggerBuild(mapId);
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button(VisibilityWorker.Enabled ? "Stop Worker" : "Start Worker"))
            {
                if (VisibilityWorker.Enabled) VisibilityWorker.Stop();
                else                          VisibilityWorker.Start();
            }

            if (ImGui.Button("Reset Cache")) SceneCache.Reset();

            if (!hasmatch) ImGui.TextDisabled("(no active match)");
        }

        // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void SaveWorkerCfg()
        {
            VisibilityWorker.SaveToConfig(SilkProgram.Config);
            SilkProgram.Config.Save();
        }

        private static string AgeText(long tickMs)
        {
            if (tickMs == 0) return "(never)";
            var ageSec = (Environment.TickCount64 - tickMs) / 1000.0;
            return ageSec < 60 ? $"{ageSec:F0}s ago" : $"{ageSec / 60.0:F1}min ago";
        }
    }
}
