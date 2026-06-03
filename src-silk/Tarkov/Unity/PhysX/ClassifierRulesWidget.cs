using ImGuiNET;
using System.Globalization;
using eft_dma_radar.Silk;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Shared ImGui widget that renders the <see cref="VisibilityClassifier"/> rule editor.
    /// Both <see cref="VisCheckDebugWindow"/> and <see cref="CacheViewWindow"/> call this so
    /// the controls are always identical and in sync.
    /// <para>
    /// Any change is applied to the live runtime immediately <em>and</em> persisted to
    /// <see cref="SilkConfig"/> (disk) so settings survive restart.
    /// </para>
    /// </summary>
    internal static class ClassifierRulesWidget
    {
        // â”€â”€ Layer mask editor â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string _maskHex      = $"{Raycaster.SeeThroughLayerMask:X8}";
        private static bool   _maskHexFocus; // true while the InputText has keyboard focus

        // â”€â”€ Pattern add inputs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string _newGlobalPat = "";
        private static string _newMapPat    = "";
        private static string _newGlobalBlk = "";
        private static string _newMapBlk    = "";

        // â”€â”€ Status feedback (shown for 4 s after any change) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string _statusMsg = "";
        private static long   _statusMsgMs;

        // â”€â”€ Bit-grid colours (AABBGGRR little-endian) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // See-through bit = orange/red  |  Blocker bit = dark gray

        private const uint ColSeeThru      = 0xFF2060E0u;  // R=E0 G=60 B=20 â€” orange
        private const uint ColSeeThruHover = 0xFF4080FFu;
        private const uint ColBlocker      = 0xFF333333u;
        private const uint ColBlockerHover = 0xFF555555u;

        // â”€â”€ Entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Draws the full rule editor at the current cursor position.
        /// Safe to call from any ImGui window or child.
        /// </summary>
        public static void Draw()
        {
            DrawLayerMaskSection();
            ImGui.Spacing();
            DrawGlobalPatternsSection();
            ImGui.Spacing();
            DrawMapPatternsSection();
            ImGui.Spacing();
            DrawGlobalBlockerPatternsSection();
            ImGui.Spacing();
            DrawMapBlockerPatternsSection();
            ImGui.Spacing();
            DrawReclassifyRow();
        }

        // â”€â”€ Layer mask â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawLayerMaskSection()
        {
            uint curMask = Raycaster.SeeThroughLayerMask;

            // â”€â”€ Foot-gun guard â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // A mask of 0xFFFFFFFF (or anything close to it) means "every
            // layer is see-through" â€” i.e. nothing ever blocks. The user
            // hit this exact state in a real match and spent time wondering
            // why vischeck reported every enemy as visible, so surface it
            // loudly and offer a one-click reset to the conservative default.
            int bitsSet = System.Numerics.BitOperations.PopCount(curMask);
            if (bitsSet >= 16)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.45f, 0.30f, 1f));
                ImGui.TextUnformatted(curMask == uint.MaxValue
                    ? "âš  Mask = 0xFFFFFFFF â€” EVERY layer see-through, nothing will block."
                    : $"âš  Mask has {bitsSet} layers set â€” broader than usual; check for overreach.");
                ImGui.PopStyleColor();
                if (ImGui.SmallButton("Reset to 0x60050000 (16+18+29+30)"))
                {
                    Raycaster.SeeThroughLayerMask = 0x60050000u;
                    _maskHex = $"{Raycaster.SeeThroughLayerMask:X8}";
                    PersistAndReclassify();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Reset to 0x00010000 (layer 16 only)"))
                {
                    Raycaster.SeeThroughLayerMask = 0x00010000u;
                    _maskHex = $"{Raycaster.SeeThroughLayerMask:X8}";
                    PersistAndReclassify();
                }
            }

            ImGui.TextDisabled("See-through layer mask:");

            // Keep the hex string in sync with the live property â€” but only when the
            // text field is NOT focused, so we don't fight the user's typing.
            if (!_maskHexFocus)
                _maskHex = $"{curMask:X8}";

            ImGui.SetNextItemWidth(90f);
            bool hexChanged = ImGui.InputText("##stm", ref _maskHex, 12,
                ImGuiInputTextFlags.CharsHexadecimal);
            _maskHexFocus = ImGui.IsItemActive();
            if (hexChanged && uint.TryParse(_maskHex, NumberStyles.HexNumber, null, out uint parsed))
            {
                Raycaster.SeeThroughLayerMask = parsed;
                curMask = parsed;
                Persist(); // save mask change immediately; no reclassify needed for mask-only edits
                           // (mask is read per-ray, not baked into IsSeeThrough for mask-based actors)
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hex bitmask â€” each set bit marks that Unity layer as see-through.\nSame encoding as ShapeLayerMask: bit N = layer N.");

            // Actor hit count for the current mask
            var snap = SceneCache.Snapshot;
            if (snap.Actors.Length > 0)
            {
                int hits = 0;
                for (int i = 0; i < snap.Actors.Length; i++)
                    if ((snap.Actors[i].ShapeLayerMask & curMask) != 0) hits++;
                ImGui.SameLine();
                ImGui.TextDisabled($"({hits}/{snap.Actors.Length} actors)");
            }

            // 4 Ã— 8 bit grid â€” click to toggle a layer bit.
            // Orange = see-through, dark = blocks rays.
            ImGui.TextDisabled("Layers 0â€“31 (orange = see-through, dark = blocks):");
            bool gridChanged = false;
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    int bit = row * 8 + col;
                    bool isSet = (curMask & (1u << bit)) != 0;
                    if (col > 0) ImGui.SameLine(0, 2f);
                    ImGui.PushID($"stl_{bit}");
                    ImGui.PushStyleColor(ImGuiCol.Button,        isSet ? ColSeeThru      : ColBlocker);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, isSet ? ColSeeThruHover : ColBlockerHover);
                    if (ImGui.Button($"{bit}", new Vector2(26f, 18f)))
                    {
                        curMask ^= 1u << bit;
                        Raycaster.SeeThroughLayerMask = curMask;
                        _maskHex = $"{curMask:X8}";
                        gridChanged = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            $"Layer {bit} (mask 0x{1u << bit:X})\n" +
                            (isSet ? "Currently: see-through (rays pass through)"
                                   : "Currently: blocks rays"));
                    ImGui.PopStyleColor(2);
                    ImGui.PopID();
                }
            }
            if (gridChanged) Persist();
        }

        // â”€â”€ Global name patterns â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawGlobalPatternsSection()
        {
            ImGui.TextDisabled("Global patterns â€” applied to every map:");
            var pats = VisibilityClassifier.GlobalNamePatterns;
            var snap = SceneCache.Snapshot;

            for (int i = 0; i < pats.Length; i++)
            {
                ImGui.PushID($"gp_{i}");
                // Live actor-count badge next to each existing pattern â€” answers
                // the "is this rule still doing anything?" question at a glance.
                int hits = CountMatches(snap, pats[i]);
                ImGui.TextUnformatted($"  \"{pats[i]}\"  ");
                ImGui.SameLine();
                ImGui.TextDisabled($"({hits} actors)");
                ImGui.SameLine();
                if (ImGui.SmallButton("Ã—"))
                {
                    var next = new string[pats.Length - 1];
                    int ni = 0;
                    for (int k = 0; k < pats.Length; k++)
                        if (k != i) next[ni++] = pats[k];
                    VisibilityClassifier.GlobalNamePatterns = next;
                    PersistAndReclassify();
                    ImGui.PopID();
                    return; // pats array changed; bail and re-render next frame
                }
                ImGui.PopID();
            }
            if (pats.Length == 0) ImGui.TextDisabled("  (none)");

            float avail = ImGui.GetContentRegionAvail().X;
            float addW  = ImGui.CalcTextSize("Add##gp").X + ImGui.GetStyle().FramePadding.X * 2 + 6f;
            ImGui.SetNextItemWidth(avail - addW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##ngp", "new patternâ€¦", ref _newGlobalPat, 128);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add##gp") && !string.IsNullOrWhiteSpace(_newGlobalPat))
            {
                var next = new string[pats.Length + 1];
                pats.CopyTo(next, 0);
                next[pats.Length] = _newGlobalPat.Trim();
                VisibilityClassifier.GlobalNamePatterns = next;
                _newGlobalPat = "";
                PersistAndReclassify();
            }
            // Live impact preview â€” re-computed every frame against the current
            // snapshot so the user sees exactly what their typed pattern would
            // catch. Splits the count by "currently blocker" so they can tell
            // whether the rule is broadening or just labelling already-see-through
            // actors.
            if (!string.IsNullOrEmpty(_newGlobalPat))
            {
                var (matches, blockers) = CountMatchesSplit(snap, _newGlobalPat);
                ImGui.TextDisabled($"  â†’ {matches} actor(s) match, {blockers} currently blocker");
            }
        }

        // â”€â”€ Per-map name patterns â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawMapPatternsSection()
        {
            var snap  = SceneCache.Snapshot;
            string mapId = !string.IsNullOrEmpty(snap.MapId)
                ? snap.MapId
                : Memory.Game?.MapID ?? "";

            if (string.IsNullOrEmpty(mapId))
            {
                ImGui.TextDisabled("Map patterns: (no active map â€” join a match first)");
                return;
            }

            ImGui.TextDisabled($"Map patterns â€” {mapId}:");
            var pats = VisibilityClassifier.GetMapPatterns(mapId);

            for (int i = 0; i < pats.Length; i++)
            {
                ImGui.PushID($"mp_{i}");
                int hits = CountMatches(snap, pats[i]);
                ImGui.TextUnformatted($"  \"{pats[i]}\"  ");
                ImGui.SameLine();
                ImGui.TextDisabled($"({hits} actors)");
                ImGui.SameLine();
                if (ImGui.SmallButton("Ã—"))
                {
                    var next = new string[pats.Length - 1];
                    int ni = 0;
                    for (int k = 0; k < pats.Length; k++)
                        if (k != i) next[ni++] = pats[k];
                    VisibilityClassifier.SetMapPatterns(mapId, next);
                    PersistAndReclassify();
                    ImGui.PopID();
                    return;
                }
                ImGui.PopID();
            }
            if (pats.Length == 0) ImGui.TextDisabled("  (none)");

            float avail = ImGui.GetContentRegionAvail().X;
            float addW  = ImGui.CalcTextSize("Add##mp").X + ImGui.GetStyle().FramePadding.X * 2 + 6f;
            ImGui.SetNextItemWidth(avail - addW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##nmp", "new patternâ€¦", ref _newMapPat, 128);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add##mp") && !string.IsNullOrWhiteSpace(_newMapPat))
            {
                var existing = VisibilityClassifier.GetMapPatterns(mapId);
                var next = new string[existing.Length + 1];
                existing.CopyTo(next, 0);
                next[existing.Length] = _newMapPat.Trim();
                VisibilityClassifier.SetMapPatterns(mapId, next);
                _newMapPat = "";
                PersistAndReclassify();
            }
            if (!string.IsNullOrEmpty(_newMapPat))
            {
                var (matches, blockers) = CountMatchesSplit(snap, _newMapPat);
                ImGui.TextDisabled($"  â†’ {matches} actor(s) match, {blockers} currently blocker");
            }
        }

        // â”€â”€ Global force-blocker patterns â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // The inverse of see-through patterns. Matches override the
        // see-through verdict so the actor stays a blocker even when a
        // broader rule (layer mask, name pattern) would have made it
        // see-through. Same UX as the see-through editors but rendered with
        // a distinct red badge so the two never get confused visually.

        private static readonly Vector4 BlockerHeaderColor = new(0.95f, 0.55f, 0.35f, 1f);

        private static void DrawGlobalBlockerPatternsSection()
        {
            ImGui.TextColored(BlockerHeaderColor,
                "Force-blocker patterns (global â€” override see-through):");
            var pats = VisibilityClassifier.GlobalBlockerPatterns;
            var snap = SceneCache.Snapshot;

            for (int i = 0; i < pats.Length; i++)
            {
                ImGui.PushID($"gbp_{i}");
                // For force-blocker rules the useful per-rule badge is "how
                // many actors does this STILL flip to blocker?" â€” i.e. how
                // many would have been see-through without it. That's what
                // (matches, currentlySeeThru) describes.
                var (matches, currentSee) = CountMatchesSeeThroughSplit(snap, pats[i]);
                ImGui.TextUnformatted($"  \"{pats[i]}\"  ");
                ImGui.SameLine();
                ImGui.TextDisabled($"({matches} matches; would-be-see-thru without rule: {currentSee})");
                ImGui.SameLine();
                if (ImGui.SmallButton("Ã—"))
                {
                    var next = new string[pats.Length - 1];
                    int ni = 0;
                    for (int k = 0; k < pats.Length; k++)
                        if (k != i) next[ni++] = pats[k];
                    VisibilityClassifier.GlobalBlockerPatterns = next;
                    PersistAndReclassify();
                    ImGui.PopID();
                    return;
                }
                ImGui.PopID();
            }
            if (pats.Length == 0) ImGui.TextDisabled("  (none)");

            float avail = ImGui.GetContentRegionAvail().X;
            float addW  = ImGui.CalcTextSize("Add##gbp").X + ImGui.GetStyle().FramePadding.X * 2 + 6f;
            ImGui.SetNextItemWidth(avail - addW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##ngbp", "new force-blocker patternâ€¦", ref _newGlobalBlk, 128);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add##gbp") && !string.IsNullOrWhiteSpace(_newGlobalBlk))
            {
                var next = new string[pats.Length + 1];
                pats.CopyTo(next, 0);
                next[pats.Length] = _newGlobalBlk.Trim();
                VisibilityClassifier.GlobalBlockerPatterns = next;
                _newGlobalBlk = "";
                PersistAndReclassify();
            }
            if (!string.IsNullOrEmpty(_newGlobalBlk))
            {
                var (m, see) = CountMatchesSeeThroughSplit(snap, _newGlobalBlk);
                ImGui.TextDisabled($"  â†’ {m} actor(s) match, {see} currently see-through (would flip to blocker)");
            }
        }

        private static void DrawMapBlockerPatternsSection()
        {
            var snap = SceneCache.Snapshot;
            string mapId = !string.IsNullOrEmpty(snap.MapId)
                ? snap.MapId
                : Memory.Game?.MapID ?? "";

            if (string.IsNullOrEmpty(mapId))
            {
                ImGui.TextColored(BlockerHeaderColor,
                    "Force-blocker patterns (map): (no active map â€” join a match first)");
                return;
            }

            ImGui.TextColored(BlockerHeaderColor,
                $"Force-blocker patterns â€” {mapId} (override see-through):");
            var pats = VisibilityClassifier.GetMapBlockerPatterns(mapId);

            for (int i = 0; i < pats.Length; i++)
            {
                ImGui.PushID($"mbp_{i}");
                var (matches, currentSee) = CountMatchesSeeThroughSplit(snap, pats[i]);
                ImGui.TextUnformatted($"  \"{pats[i]}\"  ");
                ImGui.SameLine();
                ImGui.TextDisabled($"({matches} matches; would-be-see-thru without rule: {currentSee})");
                ImGui.SameLine();
                if (ImGui.SmallButton("Ã—"))
                {
                    var next = new string[pats.Length - 1];
                    int ni = 0;
                    for (int k = 0; k < pats.Length; k++)
                        if (k != i) next[ni++] = pats[k];
                    VisibilityClassifier.SetMapBlockerPatterns(mapId, next);
                    PersistAndReclassify();
                    ImGui.PopID();
                    return;
                }
                ImGui.PopID();
            }
            if (pats.Length == 0) ImGui.TextDisabled("  (none)");

            float avail = ImGui.GetContentRegionAvail().X;
            float addW  = ImGui.CalcTextSize("Add##mbp").X + ImGui.GetStyle().FramePadding.X * 2 + 6f;
            ImGui.SetNextItemWidth(avail - addW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##nmbp", "new force-blocker patternâ€¦", ref _newMapBlk, 128);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add##mbp") && !string.IsNullOrWhiteSpace(_newMapBlk))
            {
                var existing = VisibilityClassifier.GetMapBlockerPatterns(mapId);
                var next = new string[existing.Length + 1];
                existing.CopyTo(next, 0);
                next[existing.Length] = _newMapBlk.Trim();
                VisibilityClassifier.SetMapBlockerPatterns(mapId, next);
                _newMapBlk = "";
                PersistAndReclassify();
            }
            if (!string.IsNullOrEmpty(_newMapBlk))
            {
                var (m, see) = CountMatchesSeeThroughSplit(snap, _newMapBlk);
                ImGui.TextDisabled($"  â†’ {m} actor(s) match, {see} currently see-through (would flip to blocker)");
            }
        }

        // â”€â”€ Live-preview helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Substring scan over the snapshot â€” O(N) per call but N is bounded
        // (~10k actors max) and these only fire when the UI is open, so the
        // cost is bounded to whatever the user can see anyway. No caching:
        // patterns change with every keystroke, and a stale count would be
        // worse than recomputing.

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

        private static (int matches, int blockers) CountMatchesSplit(SceneSnapshot snap, string pattern)
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
        /// Mirror of <see cref="CountMatchesSplit"/> tailored to force-blocker
        /// previews: the second number is "currently see-through" because
        /// those are the actors that would flip when a force-blocker rule
        /// commits. Splitting it out as its own method keeps the call sites
        /// in the blocker-pattern sections readable.
        /// </summary>
        private static (int matches, int currentlySeeThrough) CountMatchesSeeThroughSplit(
            SceneSnapshot snap, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return (0, 0);
            int m = 0, s = 0;
            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var a = snap.Actors[i];
                if (a.Name is null) continue;
                if (!a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                m++;
                if (a.IsSeeThrough) s++;
            }
            return (m, s);
        }

        // â”€â”€ Reclassify button + feedback â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawReclassifyRow()
        {
            if (ImGui.Button("Reclassify now"))
                PersistAndReclassify();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Refresh IsSeeThrough on every cached actor using the current rules.\n" +
                    "Happens automatically when you add/remove patterns.");

            if (!string.IsNullOrEmpty(_statusMsg)
                && Environment.TickCount64 - _statusMsgMs < 4000)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.40f, 0.85f, 0.40f, 1f), _statusMsg);
            }
        }

        // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void Persist()
        {
            VisibilityClassifier.SaveToConfig(SilkProgram.Config);
            SilkProgram.Config.Save();
        }

        private static void PersistAndReclassify()
        {
            VisibilityClassifier.Reclassify(SceneCache.Snapshot);
            Persist();
            int n = SceneCache.Snapshot.Actors.Length;
            _statusMsg   = $"Reclassified {n} actors â€” saved";
            _statusMsgMs = Environment.TickCount64;
        }
    }
}
