using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>How wall geometry is rendered by <see cref="MapImageGenerator"/>.</summary>
    internal enum MapWallStyle
    {
        /// <summary>Solid height-shaded fills (default).</summary>
        Filled,
        /// <summary>Stroke only the outer silhouette of each height bucket.</summary>
        Outline,
        /// <summary>Stroke feature edges — architectural line-art that keeps interior rooms.</summary>
        Edges,
    }

    /// <summary>
    /// Tunables for <see cref="MapImageGenerator"/>. A plain mutable bag so the
    /// Map Generator panel can bind each field to a live ImGui control and the
    /// headless hotkey path can pass <c>null</c> for sane defaults.
    /// </summary>
    internal sealed class MapGenOptions
    {
        /// <summary>Output resolution: rendered pixels per world metre.</summary>
        public float PixelsPerMeter { get; set; } = 6f;

        /// <summary>Transparent border around the geometry, in pixels.</summary>
        public float MarginPx { get; set; } = 32f;

        /// <summary>
        /// Whole-map 180° flip baked into the projection. Only <c>0</c> and
        /// <c>180</c> are supported. 180 point-reflects the image (negates both
        /// world X and Z) so high-Z geometry lands at the BOTTOM — e.g. a ship's
        /// bow drawn down instead of up. The config then carries a NEGATIVE
        /// <c>scale</c> (the local-map convention — see MapConfig.Rotation docs),
        /// so <see cref="UI.Maps.MapParams.ToMapPos"/> negates both world axes for
        /// entity positions AND Player.Draw flips the heading chevron/aimline to
        /// match (it keys off Scale &lt; 0). Using the config's rotation:180 field
        /// instead would fix positions but leave headings backwards — that field
        /// only drives the tarkov.dev maps that physically rotate their canvas.
        /// </summary>
        public int RotationDegrees { get; set; } = 0;

        /// <summary>
        /// Actors whose world AABB exceeds this in X or Z are dropped — catches
        /// map-spanning "world bounds" / skybox colliders that would otherwise
        /// blow up the map extent.
        /// </summary>
        public float MaxActorExtentMeters { get; set; } = 120f;

        /// <summary>Approximate height of one floor band, in metres.</summary>
        public float FloorBandMeters { get; set; } = 3.0f;

        /// <summary>Hard cap on the number of floor layers emitted.</summary>
        public int MaxFloors { get; set; } = 10;

        /// <summary>Clamp on either rendered image dimension; px/m is reduced to fit.</summary>
        public int MaxImageDimensionPx { get; set; } = 12000;

        /// <summary>
        /// A surface counts as a wall (drawn solid) when |worldNormal.Y| is below
        /// this; at or above it the surface is horizontal (floor / ceiling).
        /// </summary>
        public float WallMaxNormalY { get; set; } = 0.5f;

        /// <summary>
        /// Box / convex actors thinner than this in world-Y are treated as flat
        /// slabs (floors / platforms) rather than walls.
        /// </summary>
        public float SlabMaxThicknessMeters { get; set; } = 0.6f;

        /// <summary>Alpha (0-255) for the deck "sheet" fill.</summary>
        public byte FloorAlpha { get; set; } = 235;

        /// <summary>
        /// Actors whose horizontal footprint (larger of world AABB X/Z) is below
        /// this are dropped as clutter (bolts, debris, tiny props). Walls survive
        /// because they are long in at least one axis.
        /// </summary>
        public float MinFeatureMeters { get; set; } = 0.4f;

        /// <summary>
        /// Percentile (each end) of the X/Z actor distribution discarded before
        /// computing map bounds, so far outlier colliders — open-water planes,
        /// terrain skirts, stray volumes that surround the real subject — don't
        /// shrink it to a speck. <c>0</c> = raw min/max (legacy). ~0.5–1 tightens
        /// to the main structure (e.g. a ship inside a sea of sparse colliders).
        /// Trimmed actors are dropped from the render too, not just the bounds.
        /// </summary>
        public float BoundsTrimPercent { get; set; } = 0.5f;

        // ── Appearance (RGB 0-1; converted to SKColor at render) ──────────────

        /// <summary>The deck/floor "sheet" colour — solid mid-gray plate.</summary>
        public Vector3 DeckColor { get; set; } = new(88f / 255f, 88f / 255f, 96f / 255f);

        /// <summary>Wall colour for the lowest height bucket.</summary>
        public Vector3 WallLowColor { get; set; } = new(205f / 255f, 205f / 255f, 214f / 255f);

        /// <summary>Wall colour for the highest height bucket (height-shaded toward this).</summary>
        public Vector3 WallHighColor { get; set; } = new(242f / 255f, 242f / 255f, 248f / 255f);

        /// <summary>
        /// When set, the <see cref="MapWallStyle.Filled"/> style strokes a crisp
        /// region outline (the simplified wall/deck silhouette) in this colour —
        /// the flat-gray-fill + dark-outline "community floor plan" look. Null
        /// keeps the legacy behaviour (dilate stroke in the fill colour).
        /// </summary>
        public Vector3? WallOutlineColor { get; set; }

        /// <summary>
        /// When set, actors whose GameObject name marks them as doors / doorways
        /// are filled in this colour (with the wall outline) so entry points read
        /// as landmarks. Null = draw them as ordinary walls.
        /// </summary>
        public Vector3? DoorColor { get; set; }

        /// <summary>
        /// When set, stair / staircase / ladder actors are filled in this colour
        /// (community maps use a gold accent) so deck connectors stand out. These
        /// are drawn un-sliced — the whole flight shows on every band it spans, so
        /// a staircase reads as a continuous connector rather than chopped steps.
        /// Null = draw them as ordinary geometry.
        /// </summary>
        public Vector3? StairColor { get; set; }

        // ── Deck-aware banding ────────────────────────────────────────────────

        /// <summary>
        /// Explicit band-cut heights (world Y), ascending. <c>N</c> cuts produce
        /// <c>N+1</c> contiguous, non-overlapping floor layers whose ranges share
        /// boundary values (gap-free for <see cref="UI.Maps.MapLayer.IsHeightInRange"/>).
        /// <c>null</c> or empty falls back to uniform <see cref="FloorBandMeters"/> bands.
        /// </summary>
        public List<float>? FloorSplitsY { get; set; }

        /// <summary>
        /// Optional rectangular XZ sub-regions broken out into their own
        /// fine-banded layers — for a tall multi-deck chamber (e.g. an engine
        /// room) whose internal catwalks are finer than the ship's main decks.
        /// Actors inside a region's XZ box AND within its <see cref="SubRegion.MinY"/>..
        /// <see cref="SubRegion.MaxY"/> span are REMOVED from the main floor bands
        /// and instead emitted as the region's own <see cref="SubRegion.FloorSplitsY"/>
        /// cross-sections, so the chamber reads per-catwalk without double-drawing
        /// over the main decks. Null/empty = no sub-regions (normal banding only).
        /// </summary>
        public List<SubRegion>? SubRegions { get; set; }

        // ── Render style ──────────────────────────────────────────────────────

        /// <summary>How wall geometry is drawn — filled, silhouette, or feature-edge line-art.</summary>
        public MapWallStyle WallStyle { get; set; } = MapWallStyle.Edges;

        /// <summary>Stroke width (final px) for the Outline / Edges styles.</summary>
        public float OutlineWidthPx { get; set; } = 1.5f;

        /// <summary>Edges style: also stroke faint floor / terrain feature lines (deck detail, grids).</summary>
        public bool ShowDetailEdges { get; set; } = true;

        /// <summary>Edges style: draw a mesh edge when its adjacent faces differ by more than this angle (°).</summary>
        public float EdgeCreaseDegrees { get; set; } = 25f;

        /// <summary>Draw a soft dark blur under walls for depth (ambient-occlusion feel).</summary>
        public bool WallShadow { get; set; }

        /// <summary>Render at this multiple then downscale for crisper edges (1 = off, max 4).</summary>
        public int Supersample { get; set; } = 1;

        /// <summary>Root directory (relative to CWD) the per-map folder is written under.</summary>
        public string OutputRoot { get; set; } = "generated_maps";

        /// <summary>Copy with independent <see cref="FloorSplitsY"/> / <see cref="SubRegions"/> lists.</summary>
        public MapGenOptions Clone()
        {
            var c = (MapGenOptions)MemberwiseClone();
            c.FloorSplitsY = FloorSplitsY is null ? null : new List<float>(FloorSplitsY);
            c.SubRegions = SubRegions is null ? null : SubRegions.Select(s => s.Clone()).ToList();
            return c;
        }

        /// <summary>
        /// Parses pasted deck data into ascending band cuts. Each non-blank line
        /// contributes one deck height = the average of every number on that line
        /// (so "16.75 – 17.0  Deck 4" → 16.875 and label text is ignored); lines
        /// with no number are skipped. The returned cuts are the midpoints between
        /// adjacent deck heights, so one layer lands on each deck.
        /// </summary>
        public static List<float> ParseDeckHeightsToCuts(string text)
        {
            var decks = new List<float>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (var rawLine in text.Split('\n'))
                {
                    double sum = 0; int count = 0;
                    int i = 0; var line = rawLine;
                    while (i < line.Length)
                    {
                        char ch = line[i];
                        // Stop at the first label word so trailing text like
                        // "Deck 2" / "Deck 11" can't pollute the height value.
                        // Heights (and "a – b" ranges) always lead the line.
                        if (char.IsLetter(ch)) break;
                        bool numStart = char.IsDigit(ch) ||
                            ((ch == '-' || ch == '+' || ch == '.') && i + 1 < line.Length && char.IsDigit(line[i + 1]));
                        if (!numStart) { i++; continue; }
                        int start = i;
                        if (ch == '-' || ch == '+') i++;
                        bool dot = false;
                        while (i < line.Length && (char.IsDigit(line[i]) || (line[i] == '.' && !dot)))
                        {
                            if (line[i] == '.') dot = true;
                            i++;
                        }
                        if (float.TryParse(line.AsSpan(start, i - start),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v))
                        {
                            sum += v; count++;
                        }
                    }
                    if (count > 0) decks.Add((float)(sum / count));
                }
            }

            decks.Sort();
            // Collapse near-duplicate deck heights (within 0.3 m).
            var dedup = new List<float>();
            foreach (var d in decks)
                if (dedup.Count == 0 || d - dedup[^1] > 0.3f) dedup.Add(d);

            var cuts = new List<float>(Math.Max(0, dedup.Count - 1));
            for (int k = 1; k < dedup.Count; k++)
                cuts.Add((dedup[k - 1] + dedup[k]) * 0.5f);
            return cuts;
        }
    }

    /// <summary>
    /// A rectangular XZ chamber broken out into its own fine-banded cross-section
    /// layers (see <see cref="MapGenOptions.SubRegions"/>). World-space box +
    /// vertical span + the internal catwalk cut heights.
    /// </summary>
    public sealed class SubRegion
    {
        /// <summary>XZ footprint of the chamber (world metres).</summary>
        public float MinX { get; set; }
        public float MaxX { get; set; }
        public float MinZ { get; set; }
        public float MaxZ { get; set; }

        /// <summary>Vertical extent of the chamber (world Y). Actors outside this
        /// span keep their normal main-deck rendering even if their XZ is inside
        /// the box (so decks far above/below the chamber aren't gutted).</summary>
        public float MinY { get; set; }
        public float MaxY { get; set; }

        /// <summary>Internal catwalk cut heights (world Y), ascending. N cuts → N+1
        /// fine cross-section layers across <see cref="MinY"/>..<see cref="MaxY"/>.</summary>
        public List<float> FloorSplitsY { get; set; } = new();

        /// <summary>Filename + layer-id suffix: <c>&lt;map&gt;_&lt;Tag&gt;&lt;n&gt;.png</c>.</summary>
        public string Tag { get; set; } = "sub";

        /// <summary>True when the actor's XZ centre is inside the box.</summary>
        public bool ContainsXz(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

        public SubRegion Clone()
        {
            var c = (SubRegion)MemberwiseClone();
            c.FloorSplitsY = new List<float>(FloorSplitsY);
            return c;
        }
    }

    /// <summary>
    /// One emitted map layer: a base layer (all geometry) or a height-banded
    /// floor. Carries the actor subset to draw plus the height window and output
    /// filename so both the rasteriser and the JSON writer agree.
    /// </summary>
    internal sealed class PlannedLayer
    {
        /// <summary>Floor height (world Y) below which this layer hides — null = no floor.</summary>
        public float? Lo { get; init; }

        /// <summary>Ceiling height (world Y) above which this layer hides — null = no ceiling.</summary>
        public float? Hi { get; init; }

        /// <summary>Output PNG filename (no directory).</summary>
        public string File { get; init; } = "";

        /// <summary>Whether terrain height-fields are drawn into this layer.</summary>
        public bool IncludeHeightFields { get; init; }

        /// <summary>Actors that fall into this layer.</summary>
        public List<CachedActor> Actors { get; init; } = new();

        /// <summary>Human label for the layer selector.</summary>
        public string DisplayName { get; init; } = "";
    }

    /// <summary>
    /// A fully-resolved map render plan: filtered actors, world bounds, the
    /// projection parameters that mirror <see cref="UI.Maps.MapParams.ToMapPos"/>,
    /// and the ordered layer list. Computed once by <see cref="MapImageGenerator.TryPlan"/>;
    /// consumed by both the live preview and the file writer so what the panel
    /// shows is exactly what gets written.
    /// </summary>
    internal sealed class MapGenPlan
    {
        public SceneSnapshot Snapshot { get; init; } = SceneSnapshot.Empty;
        public string MapId { get; init; } = "";
        public string MapName { get; init; } = "";
        public MapGenOptions Options { get; init; } = new();

        /// <summary>Final resolution after the max-dimension clamp (px / metre).</summary>
        public float Ppm { get; init; }
        public float CfgX { get; init; }
        public float CfgY { get; init; }

        /// <summary>Config rotation (0 or 180) baked into the projection.</summary>
        public int Rotation { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public float WxMin { get; init; }
        public float WxMax { get; init; }
        public float WzMin { get; init; }
        public float WzMax { get; init; }
        public float WyMin { get; init; }
        public float WyMax { get; init; }
        public float YSpan { get; init; }

        /// <summary>Structural actors kept after filtering (== base-layer actors).</summary>
        public List<CachedActor> Actors { get; init; } = new();

        /// <summary>Total actors in the source snapshot (before filtering).</summary>
        public int TotalActorsConsidered { get; init; }

        /// <summary>Layer 0 is the base; 1.. are floor bands.</summary>
        public List<PlannedLayer> Layers { get; init; } = new();
    }

    /// <summary>
    /// Renders a top-down map (PNG layers + JSON config) straight from a PhysX
    /// <see cref="SceneSnapshot"/>. Output drops into the radar's existing
    /// <c>Maps/</c> pipeline: each floor is a height-shaded raster and the JSON's
    /// <c>x/y/scale/svgScale</c> exactly match <see cref="UI.Maps.MapParams.ToMapPos"/>
    /// so live players plot correctly.
    /// <para>
    /// Surfaces are classified by orientation so the result reads like a floor
    /// plan rather than a solid silhouette: vertical surfaces (walls) draw solid
    /// and height-shaded, up-facing horizontals (floors / decks) draw faint, and
    /// down-facing horizontals (ceilings / roof undersides) are dropped — they
    /// are pure top-down occluders that hide everything below.
    /// </para>
    /// <para>Read-only and offline: consumes a finished snapshot, touches no game memory.</para>
    /// </summary>
    internal static class MapImageGenerator
    {
        private const int HeightBuckets = 24;

        // Downscale sampling for supersampled output (mirrors the satellite-tile path).
        private static readonly SKSamplingOptions _downsample = new(SKFilterMode.Linear, SKMipmapMode.None);

        // Eight ±1 sign triples for box corners (PrimitiveSize = half-extents).
        private static readonly Vector3[] _boxSign =
        [
            new(-1,-1,-1), new( 1,-1,-1), new( 1,-1, 1), new(-1,-1, 1),
            new(-1, 1,-1), new( 1, 1,-1), new( 1, 1, 1), new(-1, 1, 1),
        ];

        /// <summary>
        /// Per-layer accumulation buffers: solid height-shaded wall paths plus a
        /// single faint path for all horizontal floor/deck surfaces.
        /// </summary>
        private sealed class LayerPaths
        {
            public readonly SKPath[] Walls = new SKPath[HeightBuckets];
            public readonly SKPath Floor = new() { FillType = SKPathFillType.Winding };
            // Stroked line-art (Edges style): wall feature edges (bright) and
            // floor / terrain feature edges (faint).
            public readonly SKPath WallEdges = new();
            public readonly SKPath DetailEdges = new();
            // Name-classified feature accents (doors, stairs/ladders) — filled in
            // their own colours on top so they read as navigation landmarks.
            public readonly SKPath Doors = new() { FillType = SKPathFillType.Winding };
            public readonly SKPath Stairs = new() { FillType = SKPathFillType.Winding };

            public LayerPaths()
            {
                for (int k = 0; k < HeightBuckets; k++)
                    Walls[k] = new SKPath { FillType = SKPathFillType.Winding };
            }

            public void Dispose()
            {
                for (int k = 0; k < HeightBuckets; k++) Walls[k].Dispose();
                Floor.Dispose();
                WallEdges.Dispose();
                DetailEdges.Dispose();
                Doors.Dispose();
                Stairs.Dispose();
            }
        }

        // ── Planning ─────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a <see cref="MapGenPlan"/> from a snapshot without rendering
        /// or writing anything. Returns false with <paramref name="error"/> for
        /// ordinary "no data" cases; never throws.
        /// </summary>
        public static bool TryPlan(SceneSnapshot snap, string mapId, MapGenOptions? opts,
            out MapGenPlan? plan, out string error)
        {
            opts ??= new MapGenOptions();
            plan = null;
            error = string.Empty;

            if (snap is null || snap.IsEmpty)
            {
                error = "scene snapshot is empty — build it first (VisCheck Rebuild Snapshot)";
                return false;
            }
            if (string.IsNullOrWhiteSpace(mapId))
                mapId = "unknown";

            // 1. Keep structural actors only.
            var actors = new List<CachedActor>(snap.Actors.Length);
            foreach (var a in snap.Actors)
            {
                if (a.IsSeeThrough) continue;
                // Invisible gameplay volumes (named *_BLOCKER) are collision-only —
                // never drawn in-game, so they'd be phantom walls on the map.
                if (!string.IsNullOrEmpty(a.Name) && a.Name.Contains("BLOCKER", StringComparison.OrdinalIgnoreCase))
                    continue;
                var size = a.WorldAabbMax - a.WorldAabbMin;
                if (size.X > opts.MaxActorExtentMeters || size.Z > opts.MaxActorExtentMeters)
                    continue;
                // Clutter cull (bolts / debris / tiny props) — but never drop a
                // door or stair, which are small yet are the navigation landmarks.
                if (MathF.Max(size.X, size.Z) < opts.MinFeatureMeters && ClassifyFeature(a.Name) == Feature.Normal)
                    continue;
                actors.Add(a);
            }
            if (actors.Count == 0) { error = "no structural actors after filtering"; return false; }

            // 1b. Trim far outliers so the subject fills the frame. A live snapshot
            //     often spans the whole level (ship + a sea of sparse colliders);
            //     percentile-clipping the actor centres rejects those strays — both
            //     from the bounds and the render — instead of letting them collapse
            //     the real structure to a speck (the cause of low-res output).
            if (opts.BoundsTrimPercent > 0f && actors.Count > 50)
            {
                float p = Math.Clamp(opts.BoundsTrimPercent, 0f, 10f) / 100f;
                var xs = new List<float>(actors.Count);
                var zs = new List<float>(actors.Count);
                foreach (var a in actors)
                {
                    xs.Add((a.WorldAabbMin.X + a.WorldAabbMax.X) * 0.5f);
                    zs.Add((a.WorldAabbMin.Z + a.WorldAabbMax.Z) * 0.5f);
                }
                xs.Sort(); zs.Sort();
                float xLo = Pctl(xs, p), xHi = Pctl(xs, 1f - p);
                float zLo = Pctl(zs, p), zHi = Pctl(zs, 1f - p);
                // Pad so walls at the trimmed edge aren't clipped.
                float padX = (xHi - xLo) * 0.02f + 2f, padZ = (zHi - zLo) * 0.02f + 2f;
                xLo -= padX; xHi += padX; zLo -= padZ; zHi += padZ;
                actors.RemoveAll(a =>
                {
                    float cx = (a.WorldAabbMin.X + a.WorldAabbMax.X) * 0.5f;
                    float cz = (a.WorldAabbMin.Z + a.WorldAabbMax.Z) * 0.5f;
                    return cx < xLo || cx > xHi || cz < zLo || cz > zHi;
                });
                if (actors.Count == 0) { error = "all actors trimmed (BoundsTrimPercent too high)"; return false; }
            }

            // 2. World bounds.
            float wxMin = float.MaxValue, wxMax = float.MinValue;
            float wzMin = float.MaxValue, wzMax = float.MinValue;
            float wyMin = float.MaxValue, wyMax = float.MinValue;
            foreach (var a in actors)
            {
                wxMin = MathF.Min(wxMin, a.WorldAabbMin.X); wxMax = MathF.Max(wxMax, a.WorldAabbMax.X);
                wzMin = MathF.Min(wzMin, a.WorldAabbMin.Z); wzMax = MathF.Max(wzMax, a.WorldAabbMax.Z);
                wyMin = MathF.Min(wyMin, a.WorldAabbMin.Y); wyMax = MathF.Max(wyMax, a.WorldAabbMax.Y);
            }

            // 3. Resolution + config params (must mirror MapParams.ToMapPos with
            //    SvgScale 1). unflipped : mapX = cfgX + wx*ppm ; mapY = cfgY - wz*ppm
            //    flipped 180° (drawn with a point-reflected projection; emitted as
            //    a negative scale): mapX = cfgX - wx*ppm ; mapY = cfgY + wz*ppm
            float ppm = opts.PixelsPerMeter;
            float margin = opts.MarginPx;
            int W = (int)MathF.Ceiling((wxMax - wxMin) * ppm + 2 * margin);
            int H = (int)MathF.Ceiling((wzMax - wzMin) * ppm + 2 * margin);
            int maxDim = Math.Max(64, opts.MaxImageDimensionPx);
            if (W > maxDim || H > maxDim)
            {
                ppm *= maxDim / (float)Math.Max(W, H);
                W = (int)MathF.Ceiling((wxMax - wxMin) * ppm + 2 * margin);
                H = (int)MathF.Ceiling((wzMax - wzMin) * ppm + 2 * margin);
            }
            if (W < 1 || H < 1) { error = "degenerate map bounds"; return false; }

            // rotation 180 puts the projection origin at the opposite world corner
            // so the canvas still fills [margin, W-margin] x [margin, H-margin].
            bool rot180 = opts.RotationDegrees == 180;
            float cfgX = rot180 ? margin + wxMax * ppm : margin - wxMin * ppm;
            float cfgY = rot180 ? margin - wzMin * ppm : margin + wzMax * ppm;
            float ySpan = MathF.Max(0.001f, wyMax - wyMin);

            string mapName = SanitizeFile(mapId);

            // 4. Layers — base (everything, incl. terrain) then floor bands.
            var layers = new List<PlannedLayer>
            {
                new()
                {
                    Lo = null, Hi = null,
                    File = mapName + ".png",
                    IncludeHeightFields = true,
                    Actors = actors,
                    DisplayName = $"Base — all geometry ({actors.Count})",
                },
            };

            // Band ranges: explicit cuts (deck-aware) or uniform slices. N cuts →
            // N+1 contiguous bands that share boundary values so the radar's
            // layer picker never sees a gap.
            var bands = new List<(float? Lo, float? Hi)>();
            if (opts.FloorSplitsY is { Count: > 0 } splits)
            {
                var cuts = new List<float>(splits);
                cuts.Sort();
                cuts.RemoveAll(c => c <= wyMin + 0.01f || c >= wyMax - 0.01f);
                int n = cuts.Count;
                if (n > 0)
                    for (int i = 0; i <= n; i++)
                        bands.Add((i == 0 ? null : cuts[i - 1], i == n ? null : cuts[i]));
            }
            else
            {
                int nBands = 0;
                if (ySpan >= opts.FloorBandMeters * 1.5f)
                    nBands = Math.Clamp((int)MathF.Ceiling(ySpan / opts.FloorBandMeters), 1, opts.MaxFloors);
                for (int i = 0; i < nBands; i++)
                {
                    float lo = wyMin + ySpan * i / nBands;
                    float hi = wyMin + ySpan * (i + 1) / nBands;
                    bands.Add((i == 0 ? null : lo, i == nBands - 1 ? null : hi));
                }
            }

            // Sub-region (e.g. engine room) actors are pulled OUT of the main
            // bands and rendered only in their own fine layers below, so a tall
            // chamber doesn't double-draw its catwalks over the main decks.
            var subRegions = opts.SubRegions is { Count: > 0 } sr ? sr : null;
            bool InAnySubRegion(CachedActor a)
            {
                if (subRegions is null) return false;
                float cx = a.WorldTransform.Position.X, cz = a.WorldTransform.Position.Z;
                foreach (var r in subRegions)
                    if (r.ContainsXz(cx, cz) && a.WorldAabbMax.Y >= r.MinY && a.WorldAabbMin.Y <= r.MaxY)
                        return true;
                return false;
            }

            for (int i = 0; i < bands.Count; i++)
            {
                var (lo, hi) = bands[i];
                float loEff = lo ?? float.MinValue;
                float hiEff = hi ?? float.MaxValue;
                var sub = new List<CachedActor>();
                foreach (var a in actors)
                    if (a.WorldAabbMax.Y >= loEff && a.WorldAabbMin.Y <= hiEff && !InAnySubRegion(a))
                        sub.Add(a);
                if (sub.Count == 0) continue;

                string range = $"{(lo.HasValue ? lo.Value.ToString("F0") : "↓")}…{(hi.HasValue ? hi.Value.ToString("F0") : "↑")}m";
                layers.Add(new PlannedLayer
                {
                    Lo = lo,
                    Hi = hi,
                    File = $"{mapName}_f{i}.png",
                    IncludeHeightFields = false,
                    Actors = sub,
                    DisplayName = $"Floor {i} — {range} ({sub.Count})",
                });
            }

            // Fine-banded sub-region layers. Inserted right AFTER the base and
            // BEFORE the main bands (see InsertRange below) so that, at any height,
            // the player's actual main deck is the highest in-range layer (the
            // radar undims only the top layer). The engine chamber then reads as a
            // dimmer recessed shaft through the hole the main bands left — rather
            // than the engine layer becoming "top" everywhere and dimming the whole
            // ship (its Y-span overlaps almost every deck). Each region's catwalk
            // cuts give contiguous bands bounded by the chamber's MinY/MaxY.
            if (subRegions is not null)
            {
                var subLayers = new List<PlannedLayer>();
                foreach (var r in subRegions)
                {
                    var boxActors = new List<CachedActor>();
                    foreach (var a in actors)
                        if (r.ContainsXz(a.WorldTransform.Position.X, a.WorldTransform.Position.Z) &&
                            a.WorldAabbMax.Y >= r.MinY && a.WorldAabbMin.Y <= r.MaxY)
                            boxActors.Add(a);
                    if (boxActors.Count == 0) continue;

                    var rcuts = new List<float>(r.FloorSplitsY);
                    rcuts.Sort();
                    rcuts.RemoveAll(c => c <= r.MinY + 0.01f || c >= r.MaxY - 0.01f);
                    int rn = rcuts.Count;
                    for (int j = 0; j <= rn; j++)
                    {
                        float blo = j == 0 ? r.MinY : rcuts[j - 1];
                        float bhi = j == rn ? r.MaxY : rcuts[j];
                        var sub = new List<CachedActor>();
                        foreach (var a in boxActors)
                            if (a.WorldAabbMax.Y >= blo && a.WorldAabbMin.Y <= bhi)
                                sub.Add(a);
                        if (sub.Count == 0) continue;

                        subLayers.Add(new PlannedLayer
                        {
                            Lo = blo,
                            Hi = bhi,
                            File = $"{mapName}_{r.Tag}{j}.png",
                            IncludeHeightFields = false,
                            Actors = sub,
                            DisplayName = $"{r.Tag} {j} — {blo:F1}…{bhi:F1}m ({sub.Count})",
                        });
                    }
                }
                if (subLayers.Count > 0)
                    layers.InsertRange(1, subLayers);   // after base, before main bands
            }

            plan = new MapGenPlan
            {
                Snapshot = snap,
                MapId = mapId,
                MapName = mapName,
                Options = opts,
                Ppm = ppm,
                CfgX = cfgX,
                CfgY = cfgY,
                Rotation = rot180 ? 180 : 0,
                Width = W,
                Height = H,
                WxMin = wxMin, WxMax = wxMax,
                WzMin = wzMin, WzMax = wzMax,
                WyMin = wyMin, WyMax = wyMax,
                YSpan = ySpan,
                Actors = actors,
                TotalActorsConsidered = snap.Actors.Length,
                Layers = layers,
            };
            return true;
        }

        // ── Full generate (plan → rasterise every layer → write PNG + JSON) ──

        /// <summary>
        /// Generates the map to disk. Returns false with <paramref name="error"/>
        /// on failure; never throws for ordinary "no data" cases.
        /// </summary>
        public static bool Generate(SceneSnapshot snap, string mapId, MapGenOptions? opts,
            out string outputDir, out string error)
        {
            outputDir = string.Empty;
            if (!TryPlan(snap, mapId, opts, out var plan, out error) || plan is null)
                return false;

            var o = plan.Options;
            outputDir = Path.Combine(o.OutputRoot, plan.MapName);
            Directory.CreateDirectory(outputDir);

            for (int i = 0; i < plan.Layers.Count; i++)
            {
                using var bmp = RenderLayerToBitmap(plan, i, SKColors.Transparent);
                if (bmp is null)
                {
                    Log.WriteLine($"[MapGen] render failed for {plan.Layers[i].File}");
                    continue;
                }
                using var image = SKImage.FromBitmap(bmp);
                using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(outputDir, plan.Layers[i].File), data.ToArray());
            }

            // Config JSON — keys match UI.Maps.MapConfig's JsonPropertyNames.
            var cfgObj = new
            {
                mapID = new[] { plan.MapId },
                x = plan.CfgX,
                y = plan.CfgY,
                // A 180° flip is encoded as a NEGATIVE scale (the local-map
                // convention — see MapConfig.Rotation docs), NOT the rotation
                // field. Negative scale makes ToMapPos negate both world axes for
                // positions AND makes Player.Draw flip the heading chevron/aimline
                // (it keys off Scale < 0). The rotation field only drives the
                // tarkov.dev maps that physically rotate their canvas, so using it
                // here would fix positions but leave every heading backwards.
                scale = plan.Rotation == 180 ? -plan.Ppm : plan.Ppm,
                svgScale = 1f,
                disableDimming = false,
                mapLayers = plan.Layers.Select(l => new
                {
                    minHeight = l.Lo,
                    maxHeight = l.Hi,
                    filename = l.File,
                }).ToArray(),
            };
            File.WriteAllText(
                Path.Combine(outputDir, plan.MapName + ".json"),
                JsonSerializer.Serialize(cfgObj, new JsonSerializerOptions { WriteIndented = true }));

            Log.WriteLine(
                $"[MapGen] '{plan.MapId}': {plan.Layers.Count} layer(s), {plan.Width}x{plan.Height}px @ {plan.Ppm:F1}px/m, " +
                $"{plan.Actors.Count} actors -> {Path.GetFullPath(outputDir)}");
            return true;
        }

        // ── Per-layer rasterization (shared by preview + file writer) ─────────

        /// <summary>
        /// Rasterises a single planned layer into a fresh <see cref="SKBitmap"/>
        /// (RGBA8888). Pass <see cref="SKColors.Transparent"/> for layered file
        /// output, or an opaque colour for an on-screen preview that mimics the
        /// radar's dark background. Caller owns/disposes the bitmap.
        /// </summary>
        public static SKBitmap? RenderLayerToBitmap(MapGenPlan plan, int layerIndex, SKColor background)
        {
            if (plan is null || (uint)layerIndex >= (uint)plan.Layers.Count)
                return null;
            int W = plan.Width, H = plan.Height;
            if (W < 1 || H < 1) return null;

            int ss = Math.Clamp(plan.Options.Supersample, 1, 4);
            // Keep the supersampled intermediate within a sane allocation budget.
            while (ss > 1 && (long)W * ss * H * ss > 96_000_000L) ss--;

            if (ss == 1)
            {
                var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(background);
                DrawLayerContent(canvas, plan, layerIndex);
                canvas.Flush();
                return bmp;
            }

            // Render at ss× then downscale for crisp anti-aliased edges.
            using (var big = new SKBitmap(new SKImageInfo(W * ss, H * ss, SKColorType.Rgba8888, SKAlphaType.Premul)))
            {
                using (var bc = new SKCanvas(big))
                {
                    bc.Clear(background);
                    bc.Scale(ss, ss);
                    DrawLayerContent(bc, plan, layerIndex);
                    bc.Flush();
                }

                var small = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var sc = new SKCanvas(small))
                using (var bigImg = SKImage.FromBitmap(big))
                {
                    sc.Clear(background);
                    sc.DrawImage(bigImg, new SKRect(0, 0, W, H), _downsample, null);
                    sc.Flush();
                }
                return small;
            }
        }

        /// <summary>
        /// Draws one planned layer's geometry into <paramref name="canvas"/>
        /// (already cleared and, for supersampling, pre-scaled). Honours the
        /// deck / wall colours, outline vs filled wall style, and the optional
        /// drop-shadow from <see cref="MapGenOptions"/>.
        /// </summary>
        private static void DrawLayerContent(SKCanvas canvas, MapGenPlan plan, int layerIndex)
        {
            var layer = plan.Layers[layerIndex];
            var opts = plan.Options;
            var snap = plan.Snapshot;

            float ppm = plan.Ppm, cfgX = plan.CfgX, cfgY = plan.CfgY;
            float wyMin = plan.WyMin, ySpan = plan.YSpan;
            // Must match MapParams.ToMapPos for plan.Rotation so geometry lands
            // where live players are plotted (180 negates both world axes).
            bool rot180 = plan.Rotation == 180;
            Vector2 Project(Vector3 w) => rot180
                ? new(cfgX - w.X * ppm, cfgY + w.Z * ppm)
                : new(cfgX + w.X * ppm, cfgY - w.Z * ppm);
            int Bucket(float y)
            {
                int b = (int)((y - wyMin) / ySpan * (HeightBuckets - 1));
                return b < 0 ? 0 : (b >= HeightBuckets ? HeightBuckets - 1 : b);
            }

            // Floor layers slice the geometry to their height window so each deck
            // reads as a clean horizontal cross-section (a wall/deck from another
            // level no longer bleeds in). The base layer leaves these at ±∞.
            float loY = layer.Lo ?? float.NegativeInfinity;
            float hiY = layer.Hi ?? float.PositiveInfinity;

            var lp = new LayerPaths();
            var scratch = new List<Vector2>(64);
            try
            {
                bool edges = opts.WallStyle == MapWallStyle.Edges;
                foreach (var a in layer.Actors)
                {
                    // Name-classified accents: doors / stairs get filled into their
                    // own path (in their colour). Stairs span decks, so draw them
                    // un-sliced — the whole flight shows on every band it touches.
                    var feat = ClassifyFeature(a.Name);
                    SKPath? force =
                        feat == Feature.Door && opts.DoorColor.HasValue ? lp.Doors :
                        feat == Feature.Stair && opts.StairColor.HasValue ? lp.Stairs : null;
                    // Doors are usually Box / Convex actors and AddBox/AddConvex
                    // don't Y-slice — they'd draw the full 2-D hull on every band
                    // the AABB touches (= same door on 3 decks). Pin each door to
                    // the deck it's mounted on (its AABB-min Y = its floor).
                    if (force == lp.Doors)
                    {
                        float mount = a.WorldAabbMin.Y;
                        if (mount < loY || mount >= hiY) continue;
                    }
                    bool forceEdges = edges && force is null;
                    float aLo = force == lp.Stairs ? float.NegativeInfinity : loY;
                    float aHi = force == lp.Stairs ? float.PositiveInfinity : hiY;

                    switch (a.GeometryType)
                    {
                        case PxGeometryType.TriangleMesh:
                            if ((uint)a.MeshIndex < (uint)snap.Meshes.Length)
                            {
                                if (forceEdges) AddMeshEdges(lp, snap.Meshes[a.MeshIndex], a.WorldTransform, Project, opts, aLo, aHi);
                                else AddMesh(lp, snap.Meshes[a.MeshIndex], a.WorldTransform, Project, Bucket, opts, aLo, aHi, force);
                            }
                            break;
                        case PxGeometryType.ConvexMesh:
                            if ((uint)a.ConvexMeshIndex < (uint)snap.ConvexMeshes.Length)
                                AddConvex(lp, a, snap.ConvexMeshes[a.ConvexMeshIndex], Project, Bucket, opts, scratch, forceEdges, force, aLo, aHi);
                            break;
                        case PxGeometryType.Box:
                            AddBox(lp, a, Project, Bucket, opts, scratch, forceEdges, force, aLo, aHi);
                            break;
                        case PxGeometryType.Sphere:
                            // Vertical-ish primitive — treat as a wall feature.
                            AddDisc(edges ? lp.WallEdges : lp.Walls[Bucket(a.WorldTransform.Position.Y)],
                                Project(a.WorldTransform.Position), a.PrimitiveSize.X * ppm);
                            break;
                        case PxGeometryType.Capsule:
                            AddDisc(edges ? lp.WallEdges : lp.Walls[Bucket(a.WorldTransform.Position.Y)],
                                Project(a.WorldTransform.Position),
                                MathF.Max(a.PrimitiveSize.X, a.PrimitiveSize.Y) * ppm);
                            break;
                        case PxGeometryType.HeightField:
                            if (layer.IncludeHeightFields && (uint)a.HeightFieldIndex < (uint)snap.HeightFields.Length)
                            {
                                if (!edges)
                                    AddHeightField(lp.Floor, snap.HeightFields[a.HeightFieldIndex], a.WorldTransform, Project);
                                else if (opts.ShowDetailEdges)
                                    AddHeightField(lp.DetailEdges, snap.HeightFields[a.HeightFieldIndex], a.WorldTransform, Project);
                            }
                            break;
                    }
                }

                var deck = ToSk(opts.DeckColor);
                var low = ToSk(opts.WallLowColor);
                var high = ToSk(opts.WallHighColor);

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                };

                // 1. Deck/floor sheet first — the solid map silhouette.
                if (!lp.Floor.IsEmpty)
                {
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = deck.WithAlpha(opts.FloorAlpha);
                    canvas.DrawPath(lp.Floor, paint);
                }

                // 2. Optional soft ambient-occlusion under the combined walls
                //    (filled styles only — Edges has no wall fill to cast it).
                if (opts.WallShadow && opts.WallStyle != MapWallStyle.Edges)
                {
                    using var allWalls = new SKPath { FillType = SKPathFillType.Winding };
                    for (int k = 0; k < HeightBuckets; k++)
                        if (!lp.Walls[k].IsEmpty) allWalls.AddPath(lp.Walls[k]);
                    if (!allWalls.IsEmpty)
                    {
                        float sigma = Math.Clamp(ppm * 0.22f, 1.5f, 6f);
                        float off = Math.Clamp(ppm * 0.12f, 1f, 4f);
                        using var shadow = new SKPaint
                        {
                            IsAntialias = true,
                            Style = SKPaintStyle.Fill,
                            Color = new SKColor(0, 0, 0, 90),
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma),
                        };
                        canvas.Save();
                        canvas.Translate(off, off);
                        canvas.DrawPath(allWalls, shadow);
                        canvas.Restore();
                    }
                }

                // 3. Walls.
                switch (opts.WallStyle)
                {
                    case MapWallStyle.Edges:
                        // Faint floor / terrain detail under the bright wall lines.
                        if (opts.ShowDetailEdges && !lp.DetailEdges.IsEmpty)
                        {
                            paint.Style = SKPaintStyle.Stroke;
                            paint.StrokeWidth = MathF.Max(0.25f, opts.OutlineWidthPx * 0.75f);
                            paint.Color = low.WithAlpha(110);
                            canvas.DrawPath(lp.DetailEdges, paint);
                        }
                        if (!lp.WallEdges.IsEmpty)
                        {
                            paint.Style = SKPaintStyle.Stroke;
                            paint.StrokeWidth = MathF.Max(0.25f, opts.OutlineWidthPx);
                            paint.Color = high;
                            canvas.DrawPath(lp.WallEdges, paint);
                        }
                        break;

                    case MapWallStyle.Outline:
                        // Simplify() collapses the triangle soup into the region
                        // boundary, so we stroke just the silhouette.
                        {
                            float stroke = MathF.Max(0.25f, opts.OutlineWidthPx);
                            for (int k = 0; k < HeightBuckets; k++)
                            {
                                if (lp.Walls[k].IsEmpty) continue;
                                using var simp = lp.Walls[k].Simplify();
                                var src = simp ?? lp.Walls[k];
                                paint.Color = Lerp(low, high, k / (float)(HeightBuckets - 1));
                                paint.Style = SKPaintStyle.Stroke;
                                paint.StrokeWidth = stroke;
                                canvas.DrawPath(src, paint);
                            }
                        }
                        break;

                    default: // Filled — solid fills, optionally with dark region outlines.
                        {
                            bool hasOutline = opts.WallOutlineColor.HasValue;
                            float dilate = Math.Clamp(ppm * 0.08f, 0.75f, 1.5f);

                            if (hasOutline)
                            {
                                // Community-map look. Simplify() is far too slow on
                                // dense soups, so we build walls by stroking the raw
                                // paths: a wide dark base, then a slightly narrower
                                // fill on top. The dilation thickens hairline collision
                                // into solid, continuous walls (merging broken pieces)
                                // and leaves a thin dark rim — for free, no Simplify.
                                float wallW = MathF.Max(1.6f, ppm * 0.11f);          // solid wall body
                                float rim = MathF.Max(0.8f, opts.OutlineWidthPx);     // dark edge each side

                                paint.Color = ToSk(opts.WallOutlineColor!.Value);
                                paint.Style = SKPaintStyle.Stroke;
                                paint.StrokeWidth = wallW + rim * 2f;
                                for (int k = 0; k < HeightBuckets; k++)
                                    if (!lp.Walls[k].IsEmpty) canvas.DrawPath(lp.Walls[k], paint);

                                for (int k = 0; k < HeightBuckets; k++)
                                {
                                    if (lp.Walls[k].IsEmpty) continue;
                                    paint.Color = Lerp(low, high, k / (float)(HeightBuckets - 1));
                                    paint.Style = SKPaintStyle.Fill;
                                    canvas.DrawPath(lp.Walls[k], paint);
                                    paint.Style = SKPaintStyle.Stroke;     // thicken + merge
                                    paint.StrokeWidth = wallW;
                                    canvas.DrawPath(lp.Walls[k], paint);
                                }
                            }
                            else
                            {
                                for (int k = 0; k < HeightBuckets; k++)
                                {
                                    if (lp.Walls[k].IsEmpty) continue;
                                    paint.Color = Lerp(low, high, k / (float)(HeightBuckets - 1));
                                    paint.Style = SKPaintStyle.Fill;
                                    canvas.DrawPath(lp.Walls[k], paint);
                                    paint.Style = SKPaintStyle.Stroke;
                                    paint.StrokeWidth = dilate;
                                    canvas.DrawPath(lp.Walls[k], paint);
                                }
                            }
                        }
                        break;
                }

                // 4. Feature accents on top — stairs (gold) then doors. Each is
                //    dilated to a legible minimum (door/stair collision is small)
                //    with a dark rim, so they read as landmarks even at deck scale.
                SKColor accentRimCol = opts.WallOutlineColor is { } orc ? ToSk(orc) : new SKColor(10, 10, 10);
                void DrawAccent(SKPath p, Vector3 color, float bodyPx)
                {
                    if (p.IsEmpty) return;
                    paint.Color = accentRimCol;            // dark base, wider → rim
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = bodyPx + MathF.Max(1f, opts.OutlineWidthPx);
                    canvas.DrawPath(p, paint);
                    var c = ToSk(color);                   // colour: fill + dilate-merge
                    paint.Color = c; paint.Style = SKPaintStyle.Fill; canvas.DrawPath(p, paint);
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = bodyPx; canvas.DrawPath(p, paint);
                }
                // Stairs first (under doors) so a door at the head of a flight wins.
                if (opts.StairColor is { } sc) DrawAccent(lp.Stairs, sc, MathF.Max(1.5f, ppm * 0.10f));
                if (opts.DoorColor is { } dc) DrawAccent(lp.Doors, dc, MathF.Max(3f, ppm * 0.18f));
            }
            finally
            {
                lp.Dispose();
            }
        }

        // ── Geometry → bucketed fill paths ───────────────────────────────────

        private static void AddMesh(LayerPaths lp, CachedTriMesh m, PxTransform tr,
            Func<Vector3, Vector2> project, Func<float, int> bucket, MapGenOptions opts, float loY, float hiY,
            SKPath? forceTarget = null)
        {
            var verts = m.Vertices;
            var idx = m.Indices;
            int triN = m.TriangleCount;
            for (int t = 0; t < triN; t++)
            {
                int i0 = idx[t * 3], i1 = idx[t * 3 + 1], i2 = idx[t * 3 + 2];
                if ((uint)i0 >= (uint)verts.Length || (uint)i1 >= (uint)verts.Length || (uint)i2 >= (uint)verts.Length)
                    continue;

                Vector3 w0 = tr.TransformPoint(verts[i0]);
                Vector3 w1 = tr.TransformPoint(verts[i1]);
                Vector3 w2 = tr.TransformPoint(verts[i2]);

                // Height slice: drop triangles entirely outside this layer's band.
                if (MathF.Max(w0.Y, MathF.Max(w1.Y, w2.Y)) < loY ||
                    MathF.Min(w0.Y, MathF.Min(w1.Y, w2.Y)) > hiY)
                    continue;

                Vector2 p0 = project(w0), p1 = project(w1), p2 = project(w2);

                // Drop sub-pixel triangles (huge meshes are mostly these top-down).
                float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                if (MathF.Abs(area) < 0.5f) continue;

                SKPath path;
                if (forceTarget != null)
                {
                    path = forceTarget;             // door / stair accent — fill every face
                }
                else
                {
                    // World-space orientation: |ny| ~ 1 horizontal, ~ 0 vertical.
                    Vector3 n = Vector3.Cross(w1 - w0, w2 - w0);
                    float nLen = n.Length();
                    if (nLen < 1e-6f) continue;
                    float ny = n.Y / nLen;

                    if (MathF.Abs(ny) >= opts.WallMaxNormalY)
                    {
                        if (ny < 0f) continue;          // ceiling / roof underside — drop
                        path = lp.Floor;                // up-facing floor / deck — faint
                    }
                    else
                    {
                        path = lp.Walls[bucket((w0.Y + w1.Y + w2.Y) / 3f)]; // wall — solid
                    }
                }

                // Force a consistent winding so overlapping coplanar triangles
                // union solidly (non-zero rule) instead of cancelling into holes.
                if (area < 0) (p1, p2) = (p2, p1);
                path.MoveTo(p0.X, p0.Y);
                path.LineTo(p1.X, p1.Y);
                path.LineTo(p2.X, p2.Y);
                path.Close();
            }
        }

        private static void AddConvex(LayerPaths lp, CachedActor a, CachedConvexMesh c,
            Func<Vector3, Vector2> project, Func<float, int> bucket, MapGenOptions opts, List<Vector2> scratch, bool edges,
            SKPath? forceTarget = null,
            float aLo = float.NegativeInfinity, float aHi = float.PositiveInfinity)
        {
            var verts = c.Vertices;
            if (verts.Length < 3) return;
            var tr = a.WorldTransform;
            scratch.Clear();

            // Thin slab = floor/ceiling → draw by membership (clipping a flat slab
            // at a band edge erases it). Only tall walls get Y-clipped. See AddBox.
            bool isSlab = (a.WorldAabbMax.Y - a.WorldAabbMin.Y) < opts.SlabMaxThicknessMeters;
            bool clipY = forceTarget == null && !isSlab
                && !float.IsNegativeInfinity(aLo) && !float.IsPositiveInfinity(aHi);

            float ySum = 0; int cnt = 0;
            int n = Math.Min(verts.Length, 256);
            if (!clipY)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector3 w = tr.TransformPoint(verts[i]);
                    scratch.Add(project(w));
                    ySum += w.Y; cnt++;
                }
            }
            else
            {
                // Convex Y-clip via vertex filter: include in-band vertices. Edge
                // intersections aren't computed (convex hulls don't expose an edge
                // list cheaply), so a convex whose vertices straddle the band may
                // lose some footprint precision — but it won't bleed onto decks
                // where no vertices live. Cheap and good enough; snapshot has 0
                // convex actors for Icebreaker anyway.
                Vector3 wmin = new(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 wmax = new(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < n; i++)
                {
                    Vector3 w = tr.TransformPoint(verts[i]);
                    if (w.Y < wmin.Y) wmin.Y = w.Y;
                    if (w.Y > wmax.Y) wmax.Y = w.Y;
                    if (w.Y >= aLo && w.Y <= aHi)
                    {
                        scratch.Add(project(w));
                        ySum += w.Y; cnt++;
                    }
                }
                if (wmax.Y < aLo || wmin.Y > aHi) return;       // fully outside
                if (scratch.Count < 3) return;
            }

            float thickness = clipY
                ? MathF.Min(a.WorldAabbMax.Y - a.WorldAabbMin.Y, aHi - aLo)
                : a.WorldAabbMax.Y - a.WorldAabbMin.Y;
            var target = forceTarget ?? (thickness < opts.SlabMaxThicknessMeters
                ? lp.Floor
                : (edges ? lp.WallEdges : lp.Walls[bucket(cnt > 0 ? ySum / cnt : (aLo + aHi) * 0.5f)]));
            FillConvexHull(target, scratch);
        }

        private static void AddBox(LayerPaths lp, CachedActor a,
            Func<Vector3, Vector2> project, Func<float, int> bucket, MapGenOptions opts, List<Vector2> scratch, bool edges,
            SKPath? forceTarget = null,
            float aLo = float.NegativeInfinity, float aHi = float.PositiveInfinity)
        {
            var he = a.PrimitiveSize;
            var tr = a.WorldTransform;
            scratch.Clear();

            Span<Vector3> v = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var s = _boxSign[i];
                v[i] = tr.TransformPoint(new Vector3(he.X * s.X, he.Y * s.Y, he.Z * s.Z));
            }

            // Only TALL boxes (walls) get Y-clipped — that's what bled across decks.
            // A thin slab is a floor/ceiling: it's flat, so Y-clipping it at a band
            // edge erases the whole footprint (= the "missing floor" bug). Draw
            // slabs by membership (full footprint); the band overlap already places
            // them on the right deck(s).
            float slabThick = a.WorldAabbMax.Y - a.WorldAabbMin.Y;
            bool isSlab = slabThick < opts.SlabMaxThicknessMeters;

            // Doors / stairs / base layer (±∞ bounds): draw the full hull; pinning
            // is handled at the call site and stairs draw on every band on purpose.
            bool clipY = forceTarget == null && !isSlab
                && !float.IsNegativeInfinity(aLo) && !float.IsPositiveInfinity(aHi);

            float ySum = 0; int cnt = 0;
            if (!clipY)
            {
                for (int i = 0; i < 8; i++)
                {
                    scratch.Add(project(v[i]));
                    ySum += v[i].Y; cnt++;
                }
            }
            else
            {
                // Y-clip the box to [aLo, aHi]. Without this, a tall box wall (e.g.
                // the ship hull or a multi-deck pillar) draws its full XZ hull on
                // every band its AABB touches = same wall bleeding across decks.
                int below = 0, above = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (v[i].Y < aLo) below++;
                    if (v[i].Y > aHi) above++;
                }
                if (below == 8 || above == 8) return;        // fully outside band

                for (int i = 0; i < 8; i++)
                {
                    if (v[i].Y >= aLo && v[i].Y <= aHi)
                    {
                        scratch.Add(project(v[i]));
                        ySum += v[i].Y; cnt++;
                    }
                }
                // Edges connect vertices whose signs differ in exactly one axis.
                for (int ia = 0; ia < 8; ia++)
                {
                    var sa = _boxSign[ia];
                    for (int ib = ia + 1; ib < 8; ib++)
                    {
                        var sb = _boxSign[ib];
                        int diff = (sa.X != sb.X ? 1 : 0) + (sa.Y != sb.Y ? 1 : 0) + (sa.Z != sb.Z ? 1 : 0);
                        if (diff != 1) continue;

                        Vector3 va = v[ia], vb = v[ib];
                        float dy = vb.Y - va.Y;
                        if (MathF.Abs(dy) < 1e-6f) continue;        // edge near-horizontal

                        if ((va.Y < aLo) != (vb.Y < aLo))
                        {
                            float t = (aLo - va.Y) / dy;
                            if (t > 0f && t < 1f)
                            {
                                var p = va + t * (vb - va);
                                scratch.Add(project(p));
                                ySum += p.Y; cnt++;
                            }
                        }
                        if ((va.Y > aHi) != (vb.Y > aHi))
                        {
                            float t = (aHi - va.Y) / dy;
                            if (t > 0f && t < 1f)
                            {
                                var p = va + t * (vb - va);
                                scratch.Add(project(p));
                                ySum += p.Y; cnt++;
                            }
                        }
                    }
                }
                if (scratch.Count < 3) return;
            }

            float thickness = clipY
                ? MathF.Min(a.WorldAabbMax.Y - a.WorldAabbMin.Y, aHi - aLo)
                : a.WorldAabbMax.Y - a.WorldAabbMin.Y;
            var target = forceTarget ?? (thickness < opts.SlabMaxThicknessMeters
                ? lp.Floor
                : (edges ? lp.WallEdges : lp.Walls[bucket(cnt > 0 ? ySum / cnt : (aLo + aHi) * 0.5f)]));
            FillConvexHull(target, scratch);
        }

        /// <summary>Name-based classification for special-rendered map features.</summary>
        private enum Feature { Normal, Door, Stair }

        /// <summary>
        /// Classifies an actor by GameObject name into a door, a stair/ladder, or
        /// ordinary geometry. Stair / door railings and "outdoor" / furniture
        /// false-positives are excluded so only real navigation features match.
        /// </summary>
        private static Feature ClassifyFeature(string? name)
        {
            if (string.IsNullOrEmpty(name)) return Feature.Normal;
            const StringComparison IC = StringComparison.OrdinalIgnoreCase;

            if (name.Contains("stair", IC) || name.Contains("ladder", IC) || name.Contains("staircase", IC))
                return name.Contains("railing", IC) ? Feature.Normal : Feature.Stair;

            if (name.Contains("door", IC) && !name.Contains("outdoor", IC))
            {
                // Furniture doors (kitchen, cabinets, lockers, oven, etc.) are NOT
                // navigation entries — don't mark them cyan.
                if (name.Contains("cupboard", IC) || name.Contains("kitchen", IC) ||
                    name.Contains("locker", IC) || name.Contains("fridge", IC) || name.Contains("cabinet", IC) ||
                    name.Contains("closet", IC) || name.Contains("microwave", IC) ||
                    name.Contains("nightstand", IC) || name.Contains("oven", IC) || name.Contains("drawer", IC))
                    return Feature.Normal;
                // Decorative flat markers — `Hallway_doorway_*` actors are h≈0
                // planes at the TOP of the door opening (~1.7 m above the floor),
                // not panels. Marking them cyan placed them on the deck ABOVE the
                // door they belong to. The door panel itself carries the entry
                // marker; treat the caps as ordinary geometry (they'll still tint
                // the floor sheet, just without the misleading accent).
                if (name.Contains("doorway", IC) || name.Contains("cap_metal", IC))
                    return Feature.Normal;
                return Feature.Door;
            }
            return Feature.Normal;
        }

        // Per-edge accumulator for feature-edge extraction.
        private struct EdgeRec
        {
            public Vector3 N0;     // normal of the first triangle sharing this edge
            public int A, B;       // vertex indices (A < B)
            public bool WallAny;   // any adjacent triangle is vertical (wall)
            public bool Feature;   // boundary edge, or crease between differing faces
        }

        /// <summary>
        /// Line-art renderer for triangle meshes: strokes only <em>feature</em>
        /// edges — boundary edges plus creases where adjacent faces differ by
        /// more than <see cref="MapGenOptions.EdgeCreaseDegrees"/>. Coplanar
        /// triangulation diagonals are dropped, so coarse collision geometry
        /// reads as clean room / equipment outlines. Wall edges land in
        /// <see cref="LayerPaths.WallEdges"/>; purely horizontal edges land in
        /// <see cref="LayerPaths.DetailEdges"/>.
        /// </summary>
        private static void AddMeshEdges(LayerPaths lp, CachedTriMesh m, PxTransform tr,
            Func<Vector3, Vector2> project, MapGenOptions opts, float loY, float hiY)
        {
            int triN = m.TriangleCount;
            if (triN <= 0 || triN > 300_000) return;   // skip pathological meshes
            var verts = m.Vertices;
            var idx = m.Indices;

            var wv = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) wv[i] = tr.TransformPoint(verts[i]);

            float creaseDot = MathF.Cos(Math.Clamp(opts.EdgeCreaseDegrees, 1f, 89f) * (MathF.PI / 180f));
            var map = new Dictionary<long, EdgeRec>(triN * 2);

            for (int t = 0; t < triN; t++)
            {
                int i0 = idx[t * 3], i1 = idx[t * 3 + 1], i2 = idx[t * 3 + 2];
                if ((uint)i0 >= (uint)wv.Length || (uint)i1 >= (uint)wv.Length || (uint)i2 >= (uint)wv.Length)
                    continue;

                // Height slice: drop triangles entirely outside this layer's band.
                if (MathF.Max(wv[i0].Y, MathF.Max(wv[i1].Y, wv[i2].Y)) < loY ||
                    MathF.Min(wv[i0].Y, MathF.Min(wv[i1].Y, wv[i2].Y)) > hiY)
                    continue;

                Vector3 nrm = Vector3.Cross(wv[i1] - wv[i0], wv[i2] - wv[i0]);
                float len = nrm.Length();
                if (len < 1e-6f) continue;
                nrm /= len;
                bool isWall = MathF.Abs(nrm.Y) < opts.WallMaxNormalY;

                // Up-facing horizontals still fill the deck plate so the hull
                // reads as a solid sheet behind the line-art (ceilings dropped).
                if (!isWall && nrm.Y > 0f)
                {
                    Vector2 q0 = project(wv[i0]), q1 = project(wv[i1]), q2 = project(wv[i2]);
                    float area = (q1.X - q0.X) * (q2.Y - q0.Y) - (q2.X - q0.X) * (q1.Y - q0.Y);
                    if (MathF.Abs(area) >= 0.5f)
                    {
                        if (area < 0) (q1, q2) = (q2, q1);
                        lp.Floor.MoveTo(q0.X, q0.Y);
                        lp.Floor.LineTo(q1.X, q1.Y);
                        lp.Floor.LineTo(q2.X, q2.Y);
                        lp.Floor.Close();
                    }
                }

                Register(map, i0, i1, nrm, isWall, creaseDot);
                Register(map, i1, i2, nrm, isWall, creaseDot);
                Register(map, i2, i0, nrm, isWall, creaseDot);
            }

            foreach (var r in map.Values)
            {
                if (!r.Feature) continue;
                Vector2 pa = project(wv[r.A]), pb = project(wv[r.B]);
                float dx = pb.X - pa.X, dy = pb.Y - pa.Y;
                if (dx * dx + dy * dy < 0.6f) continue;   // sub-pixel edge
                var path = r.WallAny ? lp.WallEdges : lp.DetailEdges;
                path.MoveTo(pa.X, pa.Y);
                path.LineTo(pb.X, pb.Y);
            }
        }

        private static void Register(Dictionary<long, EdgeRec> map, int a, int b,
            Vector3 n, bool isWall, float creaseDot)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (map.TryGetValue(key, out var r))
            {
                r.WallAny |= isWall;
                // Two faces share this edge: keep it only if they differ (a crease).
                r.Feature = Vector3.Dot(n, r.N0) < creaseDot;
                map[key] = r;
            }
            else
            {
                map[key] = new EdgeRec
                {
                    N0 = n,
                    A = a < b ? a : b,
                    B = a < b ? b : a,
                    WallAny = isWall,
                    Feature = true,   // assume boundary until a second face appears
                };
            }
        }

        private static void AddDisc(SKPath path, Vector2 centerPx, float radiusPx)
        {
            if (radiusPx < 0.5f) radiusPx = 0.5f;
            path.AddCircle(centerPx.X, centerPx.Y, radiusPx);
        }

        private static void AddHeightField(SKPath floor, CachedHeightField hf, PxTransform tr,
            Func<Vector3, Vector2> project)
        {
            int rows = hf.Rows, cols = hf.Columns;
            if (rows < 2 || cols < 2) return;

            // Bound the work: at most ~150k cells regardless of grid size.
            const long MaxCells = 150_000;
            long cells = (long)(rows - 1) * (cols - 1);
            int stride = cells <= MaxCells ? 1 : (int)MathF.Ceiling(MathF.Sqrt(cells / (float)MaxCells));

            Vector3 World(int r, int c) => tr.TransformPoint(new Vector3(
                c * hf.ColumnScale, hf.Sample(r, c) * hf.HeightScale, r * hf.RowScale));

            for (int r = 0; r + stride < rows; r += stride)
            {
                for (int c = 0; c + stride < cols; c += stride)
                {
                    Vector2 a = project(World(r, c)), b = project(World(r, c + stride));
                    Vector2 cc = project(World(r + stride, c + stride)), d = project(World(r + stride, c));
                    floor.MoveTo(a.X, a.Y);
                    floor.LineTo(b.X, b.Y);
                    floor.LineTo(cc.X, cc.Y);
                    floor.LineTo(d.X, d.Y);
                    floor.Close();
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Andrew's monotone-chain convex hull; appends the filled hull to <paramref name="path"/>.</summary>
        private static void FillConvexHull(SKPath path, List<Vector2> pts)
        {
            int n = pts.Count;
            if (n < 3) return;
            pts.Sort(static (p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

            Span<Vector2> hull = stackalloc Vector2[2 * n];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
                hull[k++] = pts[i];
            }
            int lower = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lower && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
                hull[k++] = pts[i];
            }
            int hn = k - 1; // last point == first
            if (hn < 3) return;

            path.MoveTo(hull[0].X, hull[0].Y);
            for (int i = 1; i < hn; i++) path.LineTo(hull[i].X, hull[i].Y);
            path.Close();
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        private static SKColor ToSk(Vector3 c) => new(
            (byte)Math.Clamp(c.X * 255f, 0f, 255f),
            (byte)Math.Clamp(c.Y * 255f, 0f, 255f),
            (byte)Math.Clamp(c.Z * 255f, 0f, 255f));

        private static SKColor Lerp(SKColor a, SKColor b, float t)
            => new(
                (byte)(a.Red + (b.Red - a.Red) * t),
                (byte)(a.Green + (b.Green - a.Green) * t),
                (byte)(a.Blue + (b.Blue - a.Blue) * t),
                255);

        /// <summary><paramref name="sorted"/> ascending; <paramref name="p"/> in [0,1].</summary>
        private static float Pctl(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return 0f;
            int i = Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }

        private static string SanitizeFile(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s;
        }
    }
}
