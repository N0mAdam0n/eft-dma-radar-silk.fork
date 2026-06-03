using ImGuiNET;
using System.Collections.Generic;
using System.Linq;
using eft_dma_radar.Silk.DMA;
using VmmSharpEx.Options;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// 3D wireframe debug visualizer for the PhysX scene cache.
    /// <para>
    /// Renders every <see cref="CachedActor"/>'s world AABB as a magenta
    /// wireframe box, viewed through a free-fly camera. Pairs with the
    /// text-based <see cref="VisCheckDebugWindow"/> overlay â€” the text
    /// view tells you *what's* in the cache, this view tells you *where*
    /// it is in the world. Most "ray says blocked but I think it
    /// shouldn't be" investigations need both.
    /// </para>
    /// <para>
    /// Toggled with <b>F12</b>. Lives entirely inside ImGui â€” no
    /// dedicated GL context, no shaders, no VBOs. World-to-screen is
    /// done in C# with <see cref="Matrix4x4"/>, lines are emitted via
    /// <c>ImDrawList::AddLine</c>. This keeps the surface area tiny and
    /// matches how <see cref="VisCheckDebugWindow"/> is built; trade-off
    /// is no z-buffering (wireframes always draw on top of one another),
    /// which is fine for an outline view.
    /// </para>
    /// <para>
    /// Performance budget: AABB-only render is one 12-line call per
    /// cached actor â€” at ~10k actors that's 120k lines per frame, well
    /// within ImGui's draw budget on a desktop GPU. The "Range" slider
    /// adds a distance cull so dense maps stay responsive even at
    /// short-range fly-throughs.
    /// </para>
    /// </summary>
    internal static class CacheViewWindow
    {
        // â”€â”€ Visibility / toggle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public static bool IsVisible { get; set; }
        public static void Toggle() => IsVisible = !IsVisible;

        // â”€â”€ Camera state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // Right-handed coords, +Y up. Yaw rotates around +Y (azimuth),
        // pitch rotates around the camera's local right axis (elevation).
        // Initial position is "above origin, looking forward" â€” works
        // until the user clicks "Go to local player" or moves around.

        private static Vector3 _camPos       = new(0f, 5f, 0f);
        private static float   _camYaw       = 0f;       // radians
        private static float   _camPitch     = -0.2f;    // radians (tilt down slightly)
        private static float   _camFov       = 70f;      // degrees, vertical
        private const  float   _camNear      = 0.1f;
        private const  float   _camFar       = 5000f;
        private static float   _moveSpeed    = 12f;      // metres / second
        private static float   _renderRange  = 200f;     // metres â€” distance cull radius

        // â”€â”€ Rendering options â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static bool _showActors      = true;     // AABB wireframes for every CachedActor
        private static bool _highlightLocal  = true;     // green X at local-player position
        private static bool _hideSeeThrough  = false;    // skip drawing actors classified as see-through
        private static bool _distanceFade    = true;     // alpha falls off with distance for depth perception

        // Per-geometry-type filters â€” checkboxes in the sidebar so the user
        // can isolate (say) just triangle meshes when looking for a specific
        // wall, or just box colliders to find world-bounds.
        private static bool _showSphere      = true;
        private static bool _showCapsule     = true;
        private static bool _showBox         = true;
        private static bool _showConvex      = true;
        private static bool _showTriMesh     = true;
        private static bool _showHeightField = true;

        // â”€â”€ Shape rendering (real geometry vs AABB) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // When _renderTrueShapes is on, each actor renders with type-specific
        // wireframes instead of its AABB:
        //   Sphere    â†’ three great circles
        //   Capsule   â†’ cylinder body + 2 hemisphere caps oriented by quaternion
        //   Box       â†’ 8 corners rotated by WorldTransform = oriented bounding box
        //   Convex    â†’ face polygons (vertices coplanar with each polygon plane)
        //   TriMesh   â†’ actual triangle edges (gated by _triMeshBudget)
        //   HeightField â†’ grid lines sampled at _hfStep stride
        // AABB is the universal fallback when data is missing or budget exceeded.
        private static bool _renderTrueShapes = true;
        private static int  _triMeshBudget    = 1500; // > N triangles â†’ fallback to AABB
        private static int  _hfStep           = 4;    // height-field grid stride
        private static bool _convexFaces      = true; // off â†’ fallback AABB for convex meshes

        // â”€â”€ Actor filter â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Name substring filter â€” empty = show all actors.
        private static string _nameFilter         = "";
        // Layer display filter â€” bit N set = show actors whose ShapeLayerMask
        // overlaps layer N. All bits set = show all layers (default).
        private static uint   _layerDisplayFilter = uint.MaxValue;

        /// <summary>
        /// Primary visibility-class filter. <c>All</c> shows every actor that
        /// passes the other gates, <c>BlockersOnly</c> hides everything
        /// classified as see-through (the high-signal "real cover" view â€”
        /// derived from the live snapshot's name-pattern logs that showed
        /// glass / cube props dominate the see-through set), and
        /// <c>SeeThroughOnly</c> is the inverse â€” useful when tuning the
        /// classifier rules to confirm what currently gets filtered out.
        /// </summary>
        private enum VisFilterMode { All, BlockersOnly, SeeThroughOnly }
        private static VisFilterMode _visFilter = VisFilterMode.All;

        // BSG's own marker for shootable-through-blocking world geometry â€”
        // the snapshot logs showed 86 % of layer-12 blockers on Arena_Prison
        // carry "_BALLISTIC_" in the name. A one-checkbox high-precision
        // cover filter is more useful than fighting the layer grid.
        private static bool _ballisticOnly = false;

        // Hidden name-prefix buckets. Empty = show all. Stored as a HashSet
        // of bucket strings (e.g. "Prison", "Respawn", "Rock_group") for O(1)
        // membership checks during the per-frame render loop.
        private static readonly HashSet<string> _hiddenBuckets =
            new(StringComparer.OrdinalIgnoreCase);

        // Bucket table cache â€” rebuilt only when the snapshot reference changes
        // (snapshots are atomically swapped, so reference-equality is enough).
        // Without this cache we'd re-scan 7k+ actor names every frame.
        private static SceneSnapshot? _bucketSnapshotRef;
        private static List<(string Bucket, int Count)> _bucketsAll = new();
        private static List<(string Bucket, int Count)> _bucketsBlockers = new();
        private static string _bucketFilter = ""; // substring filter for the bucket table

        // â”€â”€ Vis-check overlay â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static bool _highlightBlockers = true;  // orange outline around blocking actors
        private static bool _showLiveRays      = false; // draw eye â†’ player rays from last tick

        // â”€â”€ Live player overlay â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Turns the wireframe cache view into a usable 3D radar â€” enemy
        // positions are drawn as ground-anchored marker columns the same way
        // most 3D radars do it, so the user can correlate cover geometry
        // with enemy lines-of-sight at a glance.
        private static bool  _showPlayers      = true;
        private static bool  _showPlayerNames  = true;
        private static bool  _dimVisiblePlayers = false; // when on, players already visible to LP draw at half alpha
        private static float _playerMarkerHeight = 1.8f; // metres â€” drawn as a vertical line from feet up

        // â”€â”€ Live PhysX overlay (PlayerSuperior + Base Human bones) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // The snapshot already contains capsule actors named "PlayerSuperior(Clone)"
        // (player bodies, layer 8 on Arena_Bay5 â€” varies per map) and
        // "Base Human*" (bones â€” head, calves, thighs, etc.). They render
        // correctly in the regular wireframe pass but use the snapshot-build-time
        // pose, so they appear frozen. This overlay re-reads each tracked
        // actor's NpRigidDynamic_BufferedBody2World transform every render
        // tick (rate-limited) and re-draws the capsule + bone skeleton at
        // live positions â€” turning the cache view into a PhysX-sourced 3D
        // radar that doesn't depend on the IL2CPP / managed player list.
        private static bool _showLivePhysxPlayers = false;
        private static bool _showLivePhysxBones   = false;
        // 100 ms = 10 Hz refresh â€” enough for radar-style tracking, low
        // enough that 8 players Ã— 17 capsules each (1 body + 16 bones) at
        // ~100 reads per refresh stays well under the budget of every other
        // worker.
        private const int LivePhysxRefreshMs = 100;
        // Cached per-snapshot index lists so we don't re-scan ~10k actor
        // names per frame. Rebuilt only when the snapshot reference changes.
        private static SceneSnapshot? _livePhysxSnapshotRef;
        private static readonly List<int> _livePhysxPlayerIndices = new();
        private static readonly List<int> _livePhysxBoneIndices   = new();
        // Live pose dict â€” indexed by snapshot-relative actor index. Stays
        // valid across snapshot swaps because the index lists are rebuilt at
        // the same time and stale entries simply never get re-read.
        private static readonly Dictionary<int, PxTransform> _livePhysxPoses = new();
        // Per-actor transform-offset cache. Reading PxConcreteType once per
        // actor is far cheaper than scanning both candidate offsets every
        // refresh â€” and the first attempt at the wrong offset previously
        // produced invalid poses that the validity gate silently dropped
        // (the symptom the user hit: PlayerSuperior worked because the
        // 0x140 dynamic offset was right, but Base Human bones returned
        // garbage so nothing rendered). 0 = "unresolved", any non-zero
        // value is the resolved offset to use forever after.
        private static readonly Dictionary<int, uint> _livePhysxTransformOffsets = new();
        // Sentinel = "tried, can't resolve" so we don't keep probing dead
        // actor pointers each refresh.
        private const uint LivePhysxOffsetFailed = 0xFFFFFFFFu;
        private static long _livePhysxLastRefreshMs;
        // Cosmetic settings â€” kept distinct from the regular wireframe colour
        // scheme so the live capsules stand out against the cached geometry.
        private const uint ColorLivePhysxPlayer = 0xFF40FFFFu; // cyan
        private const uint ColorLivePhysxBone   = 0xFF40FF40u; // green

        // â”€â”€ IL2CPP skeleton overlay â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // The PhysX bone capsules path above relies on PhysX-side rigid bodies
        // that may or may not exist for every player (engine-dependent). The
        // managed-side Skeleton (Player.Skeleton.GetBonePosition) is always
        // populated for every active player by the camera worker's batch
        // scatter â€” so this overlay is the reliable per-player skeleton in
        // 3D, even when the PhysX bones path comes up dry.
        private static bool _showSkeletonBones = false;
        // Dots at each joint in addition to the line skeleton. Off by default
        // because the line skeleton is enough to read pose; the dots are
        // useful when correlating against the PhysX bone capsules.
        private static bool _showSkeletonJoints = false;
        private const uint ColorSkeletonLocal = 0xFF20FF20u; // green
        private const uint ColorSkeletonEnemy = 0xFF4040FFu; // red

        /// <summary>
        /// How the wireframe colour is chosen per actor.
        /// <list type="bullet">
        ///   <item><c>Uniform</c>: every actor in magenta â€” the classic
        ///     debug-overlay look, simple and dense.</item>
        ///   <item><c>SeeThrough</c>: blockers magenta, see-through actors a
        ///     dimmer amber so you can scan for wrongly-filtered colliders.</item>
        ///   <item><c>GeometryType</c>: one colour per <see cref="PxGeometryType"/>
        ///     so the proportion of TriMesh / Box / Capsule / etc. in a given
        ///     scene reads at a glance.</item>
        /// </list>
        /// </summary>
        private enum ColorMode { Uniform, SeeThrough, GeometryType }
        private static ColorMode _colorMode = ColorMode.SeeThrough;

        // Base colours (RGBA8 packed, ImGui-friendly: 0xAABBGGRR).
        private const uint ColorActorAabb        = 0xFFFF00FFu;  // magenta â€” uniform / blocker
        private const uint ColorSeeThroughAabb   = 0x80E0C040u;  // dim amber for see-through
        private const uint ColorTypeSphere       = 0xFF40FFFFu;  // cyan
        private const uint ColorTypeCapsule      = 0xFF40FF80u;  // green
        private const uint ColorTypeBox          = 0xFFFF8040u;  // orange
        private const uint ColorTypeConvex       = 0xFFFF40FFu;  // pink
        private const uint ColorTypeTriMesh      = 0xFFFF00FFu;  // magenta (most common â€” match Uniform)
        private const uint ColorTypeHF           = 0xFF8080FFu;  // soft blue
        private const uint ColorBackground       = 0xFF000000u;  // black
        private const uint ColorTextPrimary      = 0xFFCCCCCCu;
        private const uint ColorTextSecondary    = 0xFF808080u;
        private const uint ColorLocalPlayer      = 0xFF66FF66u;  // green crosshair
        private const uint ColorHoverHighlight   = 0xFF00FFFFu;  // cyan â€” selected actor outline
        private const uint ColorBlockerHighlight = 0xFF0080FFu;  // orange â€” actor blocking a sightline
        private const uint ColorRayVisible       = 0x6044CC44u;  // semi-transparent green ray
        private const uint ColorRayBlocked       = 0x603232C8u;  // semi-transparent red ray

        // Player marker colours â€” neutral debug palette so we don't have to
        // chase team-colour state across player kinds. Local = green, enemy
        // = red. _dimVisiblePlayers halves the alpha when the visibility
        // worker has marked the player as visible from the local eye.
        private const uint ColorPlayerLocal      = 0xFF20FF20u;  // green
        private const uint ColorPlayerEnemy      = 0xFF4040FFu;  // red
        private const uint ColorPlayerEnemyDim   = 0x804040FFu;  // half-alpha red
        private const uint ColorPlayerEnemyAI    = 0xFFB0B0B0u;  // grey (AI / placeholder)

        // Layer display filter grid button colours (matches ClassifierRulesWidget scheme).
        private const uint ColLayerActive        = 0xFF2060E0u;  // orange/blue = layer shown
        private const uint ColLayerActiveHover   = 0xFF4080FFu;
        private const uint ColLayerInactive      = 0xFF333333u;  // dark = layer hidden
        private const uint ColLayerInactiveHover = 0xFF555555u;

        // Hover-pick: tooltip if the cursor lands within ~24 px of an actor's
        // projected centre.
        private const float HoverPickMaxPxSq = 24f * 24f;

        // â”€â”€ Frame entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Called every UI frame from <see cref="UI.RadarWindow"/>'s draw pass.</summary>
        public static void Draw()
        {
            if (!IsVisible) return;

            var io = ImGui.GetIO();
            ImGui.SetNextWindowSize(new Vector2(1100f, 720f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(640f, 360f), io.DisplaySize);

            bool open = IsVisible;
            if (!ImGui.Begin("Cache View â€” PhysX wireframe", ref open,
                ImGuiWindowFlags.NoCollapse))
            {
                IsVisible = open;
                ImGui.End();
                return;
            }
            IsVisible = open;

            try
            {
                // Two-column layout: viewport (flex) + sidebar (fixed).
                const float SidebarWidth = 280f;
                float regionW = ImGui.GetContentRegionAvail().X;
                float viewportW = MathF.Max(200f, regionW - SidebarWidth - 8f);

                if (ImGui.BeginChild("##cacheview_viewport",
                        new Vector2(viewportW, 0),
                        ImGuiChildFlags.Borders))
                {
                    DrawViewport();
                }
                ImGui.EndChild();

                ImGui.SameLine();

                if (ImGui.BeginChild("##cacheview_sidebar",
                        new Vector2(0, 0),
                        ImGuiChildFlags.Borders))
                {
                    DrawSidebar();
                }
                ImGui.EndChild();
            }
            finally
            {
                ImGui.End();
            }
        }

        // â”€â”€ 3D viewport â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawViewport()
        {
            var snap = SceneCache.Snapshot;
            Vector2 vpOrigin = ImGui.GetCursorScreenPos();
            Vector2 vpSize   = ImGui.GetContentRegionAvail();
            if (vpSize.X < 16f || vpSize.Y < 16f) return;

            // Reserve the full child area as an invisible button so we can
            // receive hover + active state for camera input.
            ImGui.InvisibleButton("##cacheview_input", vpSize);
            bool hovered = ImGui.IsItemHovered();
            bool active  = ImGui.IsItemActive();

            UpdateCamera(ImGui.GetIO().DeltaTime, hovered, active);

            // Build view + projection matrices fresh each frame.
            float aspect = vpSize.X / vpSize.Y;
            Vector3 fwd  = ComputeForward(_camYaw, _camPitch);
            Matrix4x4 view = Matrix4x4.CreateLookAt(_camPos, _camPos + fwd, Vector3.UnitY);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
                _camFov * (MathF.PI / 180f), aspect, _camNear, _camFar);
            Matrix4x4 viewProj = view * proj;

            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(vpOrigin, vpOrigin + vpSize, ColorBackground);

            // â”€â”€ Wireframe pass â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            //
            // While drawing we also track the actor whose centre projects
            // closest to the mouse cursor â€” that's the tooltip target.
            int drawn = 0, culled = 0;
            float rangeSq = _renderRange * _renderRange;

            Vector2 mouseScreen = ImGui.GetIO().MousePos;
            bool mouseInViewport = hovered;
            CachedActor? hoverPick    = null;
            int          hoverPickIdx = -1;
            float        hoverPickPxSq = HoverPickMaxPxSq;

            // Snapshot the blocker set once before the render loop so both the
            // highlight pass and tooltip BLOCKER label read the same tick's data.
            HashSet<int>? blockerSet = _highlightBlockers ? BuildBlockerSet() : null;

            if (_showActors)
            {
                for (int ai = 0; ai < snap.Actors.Length; ai++)
                {
                    var a = snap.Actors[ai];
                    if (!PassesTypeFilter(a.GeometryType))      { culled++; continue; }
                    // Legacy "Hide see-through" checkbox is kept in addition
                    // to the new tri-state VisFilter so existing muscle memory
                    // still works â€” both gates have to pass.
                    if (_hideSeeThrough && a.IsSeeThrough)      { culled++; continue; }
                    if (!PassesVisFilter(a.IsSeeThrough))       { culled++; continue; }
                    if (_ballisticOnly
                        && (a.Name is null
                            || !a.Name.Contains("BALLISTIC", StringComparison.OrdinalIgnoreCase)))
                    {
                        culled++; continue;
                    }
                    if (!PassesNameFilter(a.Name))              { culled++; continue; }
                    if (!PassesLayerFilter(a.ShapeLayerMask))   { culled++; continue; }
                    if (!PassesBucketFilter(a.Name))            { culled++; continue; }

                    Vector3 center = (a.WorldAabbMin + a.WorldAabbMax) * 0.5f;
                    float dist2 = Vector3.DistanceSquared(_camPos, center);
                    if (dist2 > rangeSq) { culled++; continue; }

                    uint color = PickColor(a, dist2);
                    if (_renderTrueShapes)
                        DrawShape(dl, a, snap, viewProj, vpOrigin, vpSize, color);
                    else
                        DrawAabb(dl, a.WorldAabbMin, a.WorldAabbMax, viewProj, vpOrigin, vpSize, color);
                    drawn++;

                    if (mouseInViewport && Project(center, viewProj, vpOrigin, vpSize, out var sc))
                    {
                        float dx = sc.X - mouseScreen.X;
                        float dy = sc.Y - mouseScreen.Y;
                        float d2 = dx * dx + dy * dy;
                        if (d2 < hoverPickPxSq) { hoverPickPxSq = d2; hoverPick = a; hoverPickIdx = ai; }
                    }
                }
            }

            // â”€â”€ Hover highlight â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Redraw the picked AABB on top in cyan with thicker lines.
            if (hoverPick is not null)
            {
                DrawAabbThick(dl, hoverPick.WorldAabbMin, hoverPick.WorldAabbMax,
                              viewProj, vpOrigin, vpSize, ColorHoverHighlight, 2.0f);
            }

            // â”€â”€ Blocker highlight pass â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Orange thick outlines around actors that blocked a sightline
            // in the last worker tick. Drawn after hover so hover still wins.
            if (blockerSet is not null)
            {
                foreach (int bi in blockerSet)
                {
                    if (bi < 0 || bi >= snap.Actors.Length) continue;
                    var ba = snap.Actors[bi];
                    DrawAabbThick(dl, ba.WorldAabbMin, ba.WorldAabbMax,
                                  viewProj, vpOrigin, vpSize, ColorBlockerHighlight, 2.5f);
                }
            }

            // â”€â”€ Live ray pass â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Eye â†’ each-player lines from the last VisibilityWorker tick.
            // Green = player was visible; red = blocked.
            if (_showLiveRays)
            {
                var tickStats = VisibilityWorker.LastTickStats;
                var results   = VisibilityWorker.LastPerPlayer;
                if (results.Count > 0 && tickStats.EyePos != Vector3.Zero)
                {
                    Vector3 eyeW = tickStats.EyePos;
                    if (Project(eyeW, viewProj, vpOrigin, vpSize, out var eyeSc))
                        dl.AddCircle(eyeSc, 5f, ColorLocalPlayer, 8, 1.5f);

                    for (int ri = 0; ri < results.Count; ri++)
                    {
                        var r = results[ri];
                        if (r.LastKnownPos == Vector3.Zero) continue;
                        uint rayColor = r.Visible ? ColorRayVisible : ColorRayBlocked;
                        if (Project(eyeW, viewProj, vpOrigin, vpSize, out var eSc)
                            && Project(r.LastKnownPos, viewProj, vpOrigin, vpSize, out var tSc))
                        {
                            dl.AddLine(eSc, tSc, rayColor, 1.5f);
                            dl.AddCircle(tSc, 4f, rayColor, 8, 1.5f);
                        }
                    }
                }
            }

            // â”€â”€ Live player overlay â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // 3D radar â€” enemy + local positions as ground-anchored vertical
            // bars with a head dot. Drawn after the wireframe + blocker passes
            // so player markers always end up on top of geometry. Reads the
            // realtime worker's already-populated player list â€” zero extra DMA.
            if (_showPlayers)
            {
                var gw = Memory.Game;
                if (gw is not null)
                {
                    foreach (var p in gw.RegisteredPlayers)
                    {
                        if (!p.IsActive || !p.IsAlive || !p.HasValidPosition) continue;
                        DrawPlayerMarker(dl, p, viewProj, vpOrigin, vpSize);
                    }
                }
            }

            // â”€â”€ Live PhysX overlay (player capsules + bones) â”€â”€
            // PhysX-direct player tracking â€” re-reads the buffered
            // body-to-world transform of every "PlayerSuperior(Clone)" +
            // "Base Human*" capsule and re-renders it at the live pose.
            // Index lists + pose cache rebuilt on snapshot change; refresh
            // is rate-limited internally to LivePhysxRefreshMs.
            RebuildLivePhysxIndicesIfStale(snap);
            if (_showLivePhysxPlayers || _showLivePhysxBones)
            {
                RefreshLivePhysxPoses(snap);
                DrawLivePhysxOverlay(dl, snap, viewProj, vpOrigin, vpSize);
            }

            // â”€â”€ IL2CPP skeleton overlay â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // No DMA â€” projects already-populated bone positions.
            DrawSkeletonOverlay(dl, viewProj, vpOrigin, vpSize);

            // â”€â”€ Tooltip on hover â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            if (hoverPick is not null)
            {
                Vector3 c  = (hoverPick.WorldAabbMin + hoverPick.WorldAabbMax) * 0.5f;
                Vector3 sz = hoverPick.WorldAabbMax - hoverPick.WorldAabbMin;
                if (Project(c, viewProj, vpOrigin, vpSize, out var pickScreen))
                    dl.AddCircle(pickScreen, 6f, 0xFF00FFFFu, 12, 1.5f);

                ImGui.BeginTooltip();
                ImGui.TextUnformatted(
                    string.IsNullOrEmpty(hoverPick.Name) ? "(no name)" : hoverPick.Name);
                ImGui.Separator();
                ImGui.Text($"Layer:    {hoverPick.UnityLayer} (mask 0x{hoverPick.ShapeLayerMask:X})");
                ImGui.Text($"Geometry: {hoverPick.GeometryType}");
                ImGui.Text($"Center:   ({c.X:F1}, {c.Y:F1}, {c.Z:F1})");
                ImGui.Text($"Size:     {sz.X:F1} Ã— {sz.Y:F1} Ã— {sz.Z:F1}");
                ImGui.Text($"Distance: {Vector3.Distance(_camPos, c):F1} m");
                ImGui.Text($"Actor:    0x{hoverPick.ActorBase:X}");
                if (blockerSet?.Contains(hoverPickIdx) == true)
                    ImGui.TextColored(new Vector4(1f, 0.5f, 0.1f, 1f), "BLOCKER â€” blocking a player sightline");
                if (hoverPick.IsSeeThrough)
                {
                    string reason = VisibilityClassifier.Explain(
                        SceneCache.Snapshot.MapId,
                        hoverPick.ShapeLayerMask,
                        hoverPick.Name);
                    ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.20f, 1f),
                        $"See-through: YES â€” matched {reason}");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.60f, 0.85f, 0.60f, 1f),
                        "See-through: no (blocks visibility)");
                }
                ImGui.EndTooltip();
            }

            // â”€â”€ Local-player marker â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            if (_highlightLocal)
            {
                var lp = Memory.Game?.LocalPlayer;
                if (lp is not null && Project(lp.Position, viewProj, vpOrigin, vpSize, out var sp))
                {
                    dl.AddLine(sp + new Vector2(-8, 0), sp + new Vector2(8, 0), ColorLocalPlayer, 2f);
                    dl.AddLine(sp + new Vector2(0, -8), sp + new Vector2(0, 8), ColorLocalPlayer, 2f);
                }
            }

            // â”€â”€ HUD â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            string hudStats =
                $"{snap.Actors.Length} actors  drawn={drawn} culled={culled}  " +
                $"fov={_camFov:F0}Â°  range={_renderRange:F0}m  " +
                $"pos=({_camPos.X:F1}, {_camPos.Y:F1}, {_camPos.Z:F1})  " +
                $"yaw={_camYaw * 180f / MathF.PI:F0}Â° pitch={_camPitch * 180f / MathF.PI:F0}Â°";
            dl.AddText(vpOrigin + new Vector2(8f, 8f), ColorTextPrimary, hudStats);

            const string hint = "[RMB drag] look   [WASD] move   [Space/Ctrl] up/down   [Shift] sprint";
            Vector2 hintSize = ImGui.CalcTextSize(hint);
            dl.AddText(vpOrigin + new Vector2(8f, vpSize.Y - hintSize.Y - 8f), ColorTextSecondary, hint);
        }

        // â”€â”€ Sidebar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void DrawSidebar()
        {
            ImGui.TextUnformatted("Cache View");
            ImGui.Separator();

            DrawCameraSection();
            ImGui.Spacing();
            DrawRenderingSection();
            ImGui.Separator();

            DrawActorFilterSection();
            ImGui.Separator();

            DrawPlayerOverlaySection();
            ImGui.Separator();

            DrawVisOverlaySection();
            ImGui.Separator();

            DrawClassifierRulesSection();
            ImGui.Separator();

            DrawCacheManagerSection();
            ImGui.Separator();

            DrawSnapshotsSection();
        }

        private static void DrawCameraSection()
        {
            ImGui.TextDisabled("Camera");
            ImGui.SliderFloat("FOV",   ref _camFov,      30f, 110f,  "%.0fÂ°");
            ImGui.SliderFloat("Range", ref _renderRange, 20f, 2000f, "%.0f m");
            ImGui.SliderFloat("Speed", ref _moveSpeed,   1f,  60f,   "%.0f m/s");

            if (ImGui.Button("Reset"))
            {
                _camPos   = new Vector3(0f, 5f, 0f);
                _camYaw   = 0f;
                _camPitch = -0.2f;
            }
            ImGui.SameLine();
            var lp = Memory.Game?.LocalPlayer;
            ImGui.BeginDisabled(lp is null);
            if (ImGui.Button("Go to local") && lp is not null)
            {
                _camPos   = lp.Position + new Vector3(0f, 1.7f, 0f);
                _camYaw   = 0f;
                _camPitch = -0.1f;
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            bool hasActors = SceneCache.Snapshot.Actors.Length > 0;
            ImGui.BeginDisabled(!hasActors);
            if (ImGui.Button("Center"))
                CenterOnSnapshot();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Jump the camera to the snapshot's geometry centre and\n" +
                    "set Range to cover the whole map. Essential after loading\n" +
                    "a snapshot offline â€” the camera otherwise stays at world\n" +
                    "origin while geometry lives far away in map coordinates.");
        }

        /// <summary>
        /// Computes the union AABB of every actor in the current snapshot and
        /// places the camera up-and-back from its centre, looking inward, with
        /// the render range bumped to encompass the full extent. Called
        /// automatically after an offline Load so the user lands on a viewport
        /// that actually shows geometry instead of a black void at world origin.
        /// </summary>
        private static void CenterOnSnapshot()
        {
            var snap = SceneCache.Snapshot;
            if (snap.Actors.Length == 0) return;

            Vector3 wmin = new(float.PositiveInfinity);
            Vector3 wmax = new(float.NegativeInfinity);
            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var a = snap.Actors[i];
                wmin = Vector3.Min(wmin, a.WorldAabbMin);
                wmax = Vector3.Max(wmax, a.WorldAabbMax);
            }
            // Guard against degenerate / infinite extents from a corrupt snapshot.
            if (!float.IsFinite(wmin.X) || !float.IsFinite(wmax.X)) return;

            Vector3 center = (wmin + wmax) * 0.5f;
            Vector3 ext    = wmax - wmin;
            float maxExt   = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
            float offset   = MathF.Max(20f, maxExt * 0.4f);

            // Sit above and to the +Z side of the centre, looking back toward
            // it at ~45Â° downward (yaw = Ï€ faces -Z, pitch = -Ï€/4 tilts down).
            _camPos      = new Vector3(center.X, center.Y + offset, center.Z + offset);
            _camYaw      = MathF.PI;
            _camPitch    = -MathF.PI / 4f;
            _renderRange = Math.Clamp(maxExt * 1.2f, 200f, 2000f);
        }

        private static void DrawRenderingSection()
        {
            ImGui.TextDisabled("Rendering");
            ImGui.Checkbox("Actor AABBs",        ref _showActors);
            ImGui.Checkbox("Local-player marker", ref _highlightLocal);
            ImGui.Checkbox("Distance fade",       ref _distanceFade);
            ImGui.Checkbox("Hide see-through",    ref _hideSeeThrough);

            ImGui.Checkbox("Render true shapes", ref _renderTrueShapes);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Draw each actor with geometry-aware wireframes\n" +
                    "(sphere = 3 circles, capsule = body + caps, box = OBB,\n" +
                    " convex = face polygons, trimesh = actual triangles).\n" +
                    "Off = AABB-only (faster, much denser at distance).");

            if (_renderTrueShapes)
            {
                ImGui.Indent();
                ImGui.SliderInt("TriMesh budget", ref _triMeshBudget, 100, 10000, "%d tris");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Triangle meshes above this count fall back to AABB.\n" +
                        "Buildings can be 10k+ triangles each â€” drawing them all\n" +
                        "is honest but slow. Raise to inspect a specific mesh.");
                ImGui.SliderInt("HF step", ref _hfStep, 1, 16);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Height-field grid stride. 1 = every sample, 16 = coarse.");
                ImGui.Checkbox("Convex faces", ref _convexFaces);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("On = polygon faces per convex hull (slower).\nOff = AABB fallback.");
                ImGui.Unindent();
            }

            int modeIdx = (int)_colorMode;
            string[] modes = { "Uniform (magenta)", "By see-through", "By geometry type" };
            if (ImGui.Combo("Color by", ref modeIdx, modes, modes.Length))
                _colorMode = (ColorMode)modeIdx;

            if (ImGui.TreeNode("Geometry types"))
            {
                ImGui.Checkbox("Sphere",      ref _showSphere);
                ImGui.SameLine(140f);
                ImGui.Checkbox("Capsule",     ref _showCapsule);
                ImGui.Checkbox("Box",         ref _showBox);
                ImGui.SameLine(140f);
                ImGui.Checkbox("Convex",      ref _showConvex);
                ImGui.Checkbox("TriMesh",     ref _showTriMesh);
                ImGui.SameLine(140f);
                ImGui.Checkbox("HeightField", ref _showHeightField);
                ImGui.TreePop();
            }
        }

        private static void DrawActorFilterSection()
        {
            ImGui.TextDisabled("Actor Filter");

            // â”€â”€ Presets row â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // One-click jump to common filter combinations derived from real
            // log analysis. "Cover only" hides see-through (glass/cubes/etc.)
            // and turns on BALLISTIC, which together drop the snapshot from
            // ~7k actors to the ~2k that actually matter for sightlines.
            if (ImGui.SmallButton("Reset##fp"))    ApplyPreset_Reset();
            ImGui.SameLine();
            if (ImGui.SmallButton("Cover only##fp")) ApplyPreset_CoverOnly();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Blockers-only + BALLISTIC. Cuts the snapshot to actors that\n" +
                    "actually stop bullets â€” empirically ~30 % of the total.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Walls##fp"))    ApplyPreset_Walls();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Blockers on layer 12 only. Excludes player colliders and\n" +
                    "the gameplay-trigger layers (18 / 29 / 30) that are\n" +
                    "always see-through.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Debug see-thru##fp")) ApplyPreset_SeeThroughDebug();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Inverse view â€” only actors classified as see-through.\n" +
                    "Use when tuning classifier rules to spot false positives.");

            // â”€â”€ Vis-class tri-state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            int vfm = (int)_visFilter;
            ImGui.Text("Show:");
            ImGui.SameLine();
            if (ImGui.RadioButton("All##vfm",       ref vfm, 0)) _visFilter = (VisFilterMode)vfm;
            ImGui.SameLine();
            if (ImGui.RadioButton("Blockers##vfm",  ref vfm, 1)) _visFilter = (VisFilterMode)vfm;
            ImGui.SameLine();
            if (ImGui.RadioButton("See-thru##vfm",  ref vfm, 2)) _visFilter = (VisFilterMode)vfm;

            // â”€â”€ BALLISTIC quick filter â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            ImGui.Checkbox("Only BALLISTIC", ref _ballisticOnly);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Show only actors whose name contains \"BALLISTIC\" â€” BSG's own\n" +
                    "marker for shootable-through-blocking world geometry.\n" +
                    "On Arena_Prison this matches ~2300 of ~2700 layer-12 walls.");

            // â”€â”€ Substring filter â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            float avail = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(avail);
            ImGui.InputTextWithHint("##name_filter", "name containsâ€¦", ref _nameFilter, 128);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Case-insensitive substring â€” only actors whose name contains\n" +
                    "this text are drawn. Clear to show all.");

            // â”€â”€ Layer grid â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            ImGui.TextDisabled("Layer display filter (orange=shown, dark=hidden):");
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    int bit = row * 8 + col;
                    bool isSet = (_layerDisplayFilter & (1u << bit)) != 0;
                    if (col > 0) ImGui.SameLine(0, 2f);
                    ImGui.PushID($"ldf_{bit}");
                    ImGui.PushStyleColor(ImGuiCol.Button,        isSet ? ColLayerActive      : ColLayerInactive);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, isSet ? ColLayerActiveHover : ColLayerInactiveHover);
                    if (ImGui.Button($"{bit}", new Vector2(26f, 18f)))
                        _layerDisplayFilter ^= 1u << bit;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Layer {bit}\n" +
                            (isSet ? "Shown â€” actors on this layer are drawn"
                                   : "Hidden â€” actors on this layer are culled"));
                    ImGui.PopStyleColor(2);
                    ImGui.PopID();
                }
            }

            if (ImGui.SmallButton("All##ldf"))    _layerDisplayFilter = uint.MaxValue;
            ImGui.SameLine();
            if (ImGui.SmallButton("None##ldf"))   _layerDisplayFilter = 0u;
            ImGui.SameLine();
            if (ImGui.SmallButton("Invert##ldf")) _layerDisplayFilter = ~_layerDisplayFilter;

            // â”€â”€ Name-prefix buckets â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            DrawBucketSubsection();
        }

        /// <summary>
        /// Auto-extracted name-prefix bucket panel. Built lazily from the
        /// current snapshot the first time it's rendered after a swap (~one
        /// pass over 7k actor names; cached for the rest of the snapshot's
        /// lifetime). Each row carries a per-bucket count and a hide/show
        /// toggle so the user can drop 1000 "Prison_metal_*" props with
        /// a single click instead of fighting the substring field.
        /// </summary>
        private static void DrawBucketSubsection()
        {
            var snap = SceneCache.Snapshot;
            RebuildBucketCacheIfStale(snap);
            // Pick the source bucket list â€” when in BlockersOnly the per-bucket
            // counts should reflect that filter (otherwise the user sees "1159
            // glass" and can't tell which fraction is real cover).
            var src = _visFilter == VisFilterMode.BlockersOnly
                ? _bucketsBlockers
                : _bucketsAll;

            if (!ImGui.TreeNode($"Name buckets ({src.Count})##nb"))
                return;

            float fw = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(fw);
            ImGui.InputTextWithHint("##bucket_filter", "filter bucketsâ€¦", ref _bucketFilter, 64);

            if (ImGui.SmallButton("Show all##nb"))   _hiddenBuckets.Clear();
            ImGui.SameLine();
            if (ImGui.SmallButton("Hide all##nb"))
            {
                _hiddenBuckets.Clear();
                foreach (var b in src) _hiddenBuckets.Add(b.Bucket);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"hidden={_hiddenBuckets.Count}");

            // Scrollable table â€” 6 rows fit comfortably without forcing a
            // double-scrollbar layout. The sidebar's outer scroll handles
            // overflow when the bucket count is large.
            if (ImGui.BeginTable("##bktbl", 3,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.ScrollY, new Vector2(0, 180f)))
            {
                ImGui.TableSetupColumn("",       ImGuiTableColumnFlags.WidthFixed,   22f);
                ImGui.TableSetupColumn("Bucket", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("N",      ImGuiTableColumnFlags.WidthFixed,   42f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                for (int i = 0; i < src.Count; i++)
                {
                    var (bucket, count) = src[i];
                    if (_bucketFilter.Length > 0
                        && bucket.IndexOf(_bucketFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    bool visible = !_hiddenBuckets.Contains(bucket);
                    ImGui.PushID(i);
                    if (ImGui.Checkbox("##bk", ref visible))
                    {
                        if (visible) _hiddenBuckets.Remove(bucket);
                        else         _hiddenBuckets.Add(bucket);
                    }
                    ImGui.PopID();

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(bucket);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text(count.ToString());
                }
                ImGui.EndTable();
            }

            ImGui.TreePop();
        }

        // â”€â”€ Filter helpers / presets â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void ApplyPreset_Reset()
        {
            _visFilter          = VisFilterMode.All;
            _ballisticOnly      = false;
            _nameFilter         = string.Empty;
            _layerDisplayFilter = uint.MaxValue;
            _hideSeeThrough     = false;
            _hiddenBuckets.Clear();
            _bucketFilter       = string.Empty;
        }

        private static void ApplyPreset_CoverOnly()
        {
            ApplyPreset_Reset();
            _visFilter     = VisFilterMode.BlockersOnly;
            _ballisticOnly = true;
        }

        private static void ApplyPreset_Walls()
        {
            ApplyPreset_Reset();
            _visFilter          = VisFilterMode.BlockersOnly;
            _layerDisplayFilter = 1u << 12; // only layer 12 â€” world geometry
        }

        private static void ApplyPreset_SeeThroughDebug()
        {
            ApplyPreset_Reset();
            _visFilter = VisFilterMode.SeeThroughOnly;
        }

        private static bool PassesVisFilter(bool isSeeThrough) => _visFilter switch
        {
            VisFilterMode.BlockersOnly   => !isSeeThrough,
            VisFilterMode.SeeThroughOnly =>  isSeeThrough,
            _                            => true,
        };

        private static bool PassesBucketFilter(string? name)
        {
            if (_hiddenBuckets.Count == 0) return true;
            return !_hiddenBuckets.Contains(ExtractBucket(name));
        }

        /// <summary>
        /// Splits an actor name into a coarse bucket: the prefix up to the
        /// first underscore / space / paren / digit. Empirically this gives
        /// useful groupings on Arena_Prison:
        /// <list type="bullet">
        ///   <item><c>Prison_metal_*</c> â†’ "Prison"</item>
        ///   <item><c>Fort_Wall_*</c>    â†’ "Fort"</item>
        ///   <item><c>Cube (12)</c>      â†’ "Cube"</item>
        ///   <item><c>glass</c>          â†’ "glass"</item>
        /// </list>
        /// </summary>
        private static string ExtractBucket(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "(no name)";
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_' || c == ' ' || c == '(' || c == '-' || (c >= '0' && c <= '9'))
                    return i == 0 ? "(other)" : name.Substring(0, i);
            }
            return name;
        }

        /// <summary>
        /// Rebuilds the cached bucket lists when (and only when) the snapshot
        /// reference changes. ReferenceEquals works here because <see cref="SceneCache"/>
        /// publishes new snapshots via <c>Volatile.Write</c> with a single ref
        /// swap â€” same reference means same content, period.
        /// </summary>
        private static void RebuildBucketCacheIfStale(SceneSnapshot snap)
        {
            if (ReferenceEquals(snap, _bucketSnapshotRef)) return;
            _bucketSnapshotRef = snap;

            var all     = new Dictionary<string, int>(StringComparer.Ordinal);
            var blocker = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var a = snap.Actors[i];
                var b = ExtractBucket(a.Name);
                all.TryGetValue(b, out var n);     all[b]     = n + 1;
                if (!a.IsSeeThrough)
                {
                    blocker.TryGetValue(b, out var m); blocker[b] = m + 1;
                }
            }

            _bucketsAll = all
                .Select(kv => (kv.Key, kv.Value))
                .OrderByDescending(t => t.Value)
                .ToList();
            _bucketsBlockers = blocker
                .Select(kv => (kv.Key, kv.Value))
                .OrderByDescending(t => t.Value)
                .ToList();
        }

        private static void DrawPlayerOverlaySection()
        {
            ImGui.TextDisabled("Live Players (3D radar)");

            ImGui.Checkbox("Show players", ref _showPlayers);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Overlay live enemy + local-player positions as vertical\n" +
                    "marker columns. Reads Memory.Game.Players â€”\n" +
                    "no extra DMA work, the realtime worker already populates it.");

            if (_showPlayers)
            {
                ImGui.Indent();
                ImGui.Checkbox("Names##pl",  ref _showPlayerNames);
                ImGui.Checkbox("Dim if visible to LP##pl", ref _dimVisiblePlayers);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "When the visibility worker marks an enemy as visible\n" +
                        "from the local player's eye, draw their marker at half\n" +
                        "alpha. Quick sanity check that vischeck agrees with the\n" +
                        "geometry you're staring at.");
                ImGui.SliderFloat("Height##pl", ref _playerMarkerHeight, 0.5f, 3.0f, "%.1f m");
                ImGui.Unindent();
            }

            // â”€â”€ Live PhysX overlay sub-section â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            ImGui.Spacing();
            ImGui.TextDisabled("PhysX-sourced overlay (live capsules)");

            // Show counts inline so the user can confirm the snapshot actually
            // has player/bone actors before turning the toggles on.
            int nPlayers = _livePhysxPlayerIndices.Count;
            int nBones   = _livePhysxBoneIndices.Count;
            ImGui.TextDisabled($"  found in snapshot: {nPlayers} players, {nBones} bones");

            ImGui.Checkbox("Player capsules##live", ref _showLivePhysxPlayers);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Re-read every \"PlayerSuperior(Clone)\" capsule's live\n" +
                    "PhysX transform and re-draw it on top of the cached\n" +
                    "wireframe. PhysX-direct player tracking â€” independent\n" +
                    $"of the IL2CPP player list. Refresh: {LivePhysxRefreshMs} ms.");

            ImGui.Checkbox("Bone capsules##live", ref _showLivePhysxBones);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Same idea for \"Base Human*\" bone capsules (head, calves,\n" +
                    "thighs, etc.) â€” gives you a per-player skeleton overlay\n" +
                    "purely from PhysX. ~16 bones per player; gets expensive\n" +
                    "fast if the lobby is full.");

            ImGui.Spacing();
            ImGui.TextDisabled("IL2CPP skeleton (per-player bone positions)");
            ImGui.Checkbox("Skeleton lines##il2cpp", ref _showSkeletonBones);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Draw a connected skeleton per active player using the\n" +
                    "managed-side bone positions read by the camera worker.\n" +
                    "Independent of the PhysX bone path â€” works for every\n" +
                    "player whose skeleton has resolved, even when PhysX bone\n" +
                    "capsules aren't in the scene. Green=local, red=enemy.");
            ImGui.Checkbox("Joint dots##il2cpp", ref _showSkeletonJoints);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Draw a small dot at each joint on top of the skeleton lines.\n" +
                    "Useful when correlating against the PhysX bone capsules.");
        }

        /// <summary>
        /// Snapshot-change-triggered scan that builds the index lists of
        /// PlayerSuperior + Base Human* actors. Cheap (one pass over the
        /// names) and only runs when the snapshot reference flips.
        /// </summary>
        private static void RebuildLivePhysxIndicesIfStale(SceneSnapshot snap)
        {
            if (ReferenceEquals(snap, _livePhysxSnapshotRef)) return;
            _livePhysxSnapshotRef = snap;
            _livePhysxPlayerIndices.Clear();
            _livePhysxBoneIndices.Clear();
            _livePhysxPoses.Clear();
            // New snapshot â‡’ ActorBase pointers are new â‡’ resolved offsets
            // from the old snapshot are stale. Wipe and re-probe on next refresh.
            _livePhysxTransformOffsets.Clear();
            for (int i = 0; i < snap.Actors.Length; i++)
            {
                var nm = snap.Actors[i].Name;
                if (string.IsNullOrEmpty(nm)) continue;
                if (nm.Contains("PlayerSuperior", StringComparison.OrdinalIgnoreCase))
                    _livePhysxPlayerIndices.Add(i);
                else if (nm.StartsWith("Base Human", StringComparison.OrdinalIgnoreCase))
                    _livePhysxBoneIndices.Add(i);
            }
        }

        /// <summary>
        /// Scatter-reads the buffered body-to-world transform from every
        /// tracked actor pointer and stores the result in
        /// <see cref="_livePhysxPoses"/>. Rate-limited to
        /// <see cref="LivePhysxRefreshMs"/> so a high UI framerate doesn't
        /// turn into 60 Hz DMA traffic. Silently skips actors with invalid
        /// pointers â€” happens during snapshot transitions / player
        /// despawns / etc. Costs one scatter batch per refresh regardless
        /// of how many actors are tracked.
        /// </summary>
        private static void RefreshLivePhysxPoses(SceneSnapshot snap)
        {
            if (_livePhysxPlayerIndices.Count == 0 && _livePhysxBoneIndices.Count == 0) return;
            long now = Environment.TickCount64;
            if (now - _livePhysxLastRefreshMs < LivePhysxRefreshMs) return;
            _livePhysxLastRefreshMs = now;

            try
            {
                // Two phases: (1) resolve PxConcreteType for any actors we
                // haven't probed yet, (2) issue the actual pose scatter using
                // each actor's resolved offset. Phase 1 runs at most once per
                // actor over the snapshot's lifetime, so the steady state is
                // a single scatter batch per refresh tick.
                if (_showLivePhysxPlayers) ResolveOffsetsIfNeeded(snap, _livePhysxPlayerIndices);
                if (_showLivePhysxBones)   ResolveOffsetsIfNeeded(snap, _livePhysxBoneIndices);

                using var scatter = Memory.GetScatter(VmmFlags.NOCACHE);
                if (_showLivePhysxPlayers)
                    PreparePoseReads(scatter, snap, _livePhysxPlayerIndices);
                if (_showLivePhysxBones)
                    PreparePoseReads(scatter, snap, _livePhysxBoneIndices);
                scatter.Execute();
                if (_showLivePhysxPlayers)
                    CollectPoseReads(scatter, snap, _livePhysxPlayerIndices);
                if (_showLivePhysxBones)
                    CollectPoseReads(scatter, snap, _livePhysxBoneIndices);
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "live_physx_refresh",
                    TimeSpan.FromSeconds(5),
                    $"[CacheView] Live PhysX refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads <see cref="PhysXOffsets.PxBase_ConcreteType"/> for any actor
        /// that hasn't had its transform-offset resolved yet, then caches the
        /// right offset (<c>0x140</c> for dynamic, <c>0x90</c> for static).
        /// Issued as a single scatter batch so even N=128 actors take one
        /// DMA round-trip the first time the user turns the toggle on.
        /// </summary>
        private static void ResolveOffsetsIfNeeded(SceneSnapshot snap, List<int> indices)
        {
            // Quick check: any actors missing an offset entry?
            bool anyUnresolved = false;
            for (int i = 0; i < indices.Count; i++)
            {
                if (!_livePhysxTransformOffsets.ContainsKey(indices[i])) { anyUnresolved = true; break; }
            }
            if (!anyUnresolved) return;

            using var scatter = Memory.GetScatter(VmmFlags.NOCACHE);
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (_livePhysxTransformOffsets.ContainsKey(idx)) continue;
                ulong basePtr = snap.Actors[idx].ActorBase;
                if (basePtr == 0) { _livePhysxTransformOffsets[idx] = LivePhysxOffsetFailed; continue; }
                scatter.PrepareReadValue<ushort>(basePtr + PhysXOffsets.PxBase_ConcreteType);
            }
            scatter.Execute();
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (_livePhysxTransformOffsets.ContainsKey(idx)) continue;
                ulong basePtr = snap.Actors[idx].ActorBase;
                if (basePtr == 0) continue;
                if (!scatter.ReadValue<ushort>(basePtr + PhysXOffsets.PxBase_ConcreteType, out var typeRaw))
                {
                    _livePhysxTransformOffsets[idx] = LivePhysxOffsetFailed;
                    continue;
                }
                _livePhysxTransformOffsets[idx] = (PxConcreteType)typeRaw == PxConcreteType.RigidDynamic
                    ? PhysXOffsets.NpRigidDynamic_BufferedBody2World
                    : PhysXOffsets.NpRigidStatic_BodyToWorld;
            }
        }

        private static void PreparePoseReads(VmmSharpEx.Scatter.VmmScatter scatter,
            SceneSnapshot snap, List<int> indices)
        {
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx < 0 || idx >= snap.Actors.Length) continue;
                ulong basePtr = snap.Actors[idx].ActorBase;
                if (basePtr == 0) continue;
                if (!_livePhysxTransformOffsets.TryGetValue(idx, out var off)
                    || off == LivePhysxOffsetFailed) continue;
                scatter.PrepareReadValue<PxTransform>(basePtr + off);
            }
        }

        private static void CollectPoseReads(VmmSharpEx.Scatter.VmmScatter scatter,
            SceneSnapshot snap, List<int> indices)
        {
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx < 0 || idx >= snap.Actors.Length) continue;
                ulong basePtr = snap.Actors[idx].ActorBase;
                if (basePtr == 0) continue;
                if (!_livePhysxTransformOffsets.TryGetValue(idx, out var off)
                    || off == LivePhysxOffsetFailed) continue;
                if (scatter.ReadValue<PxTransform>(basePtr + off, out var pose)
                    && pose.IsFinite && pose.IsRotationUnit)
                {
                    _livePhysxPoses[idx] = pose;
                }
            }
        }

        /// <summary>
        /// Renders a per-player skeleton from the managed-side
        /// <see cref="GameWorld.Player.Skeleton"/> data. Each active player
        /// contributes up to 14 line segments (headâ†’neckâ†’torsoâ†’pelvis,
        /// pelvisâ†’kneeâ†’foot Ã—2, collarâ†’elbowâ†’hand Ã—2) projected through
        /// the same camera matrix as the wireframe pass. No DMA â€” reads only
        /// the already-populated bone arrays the camera worker filled.
        /// </summary>
        private static void DrawSkeletonOverlay(ImDrawListPtr dl,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize)
        {
            if (!_showSkeletonBones && !_showSkeletonJoints) return;
            var gw = Memory.Game;
            if (gw is null) return;

            float rangeSq = _renderRange * _renderRange;

            foreach (var p in gw.RegisteredPlayers)
            {
                if (!p.IsActive || !p.IsAlive) continue;
                var skel = p.Skeleton;
                if (skel is null) continue;

                // Distance cull on the body anchor â€” if the whole player is
                // out of range, skip the per-bone work. Falls back to feet
                // position when no bone has resolved yet.
                Vector3 anchorWorld = skel.GetBonePosition(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanSpine2)
                    ?? skel.GetBonePosition(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanPelvis)
                    ?? p.Position;
                if (Vector3.DistanceSquared(_camPos, anchorWorld) > rangeSq) continue;

                uint color = p.IsLocalPlayer ? ColorSkeletonLocal : ColorSkeletonEnemy;
                DrawPlayerSkeleton(dl, skel, color, viewProj, vpOrigin, vpSize);
            }
        }

        /// <summary>
        /// Renders one player's skeleton â€” projects every tracked bone, then
        /// emits the same 14-segment connectivity the
        /// <see cref="GameWorld.Player.Skeleton.UpdateScreenBuffer"/> 2D
        /// path uses. Bones that fail to project (behind camera / not yet
        /// resolved) drop the entire segment they participate in â€” better
        /// than drawing degenerate lines to (0,0).
        /// </summary>
        private static void DrawPlayerSkeleton(ImDrawListPtr dl,
            GameWorld.Player.Skeleton skel, uint color,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize)
        {
            // Project every joint we care about. Returns null for any bone
            // that isn't resolved AND null for any bone that's behind the
            // camera â€” both cases collapse to the same "skip" handling.
            Vector2? P(eft_dma_radar.Silk.Tarkov.Unity.Bones b)
            {
                var w = skel.GetBonePosition(b);
                if (!w.HasValue) return null;
                return Project(w.Value, viewProj, vpOrigin, vpSize, out var s) ? s : null;
            }

            var head    = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanHead);
            var neck    = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanNeck);
            var upper   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanSpine3);
            var mid     = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanSpine2);
            var lower   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanSpine1);
            var pelvis  = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanPelvis);
            var lCollar = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanLCollarbone);
            var rCollar = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanRCollarbone);
            var lElbow  = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanLForearm2);
            var rElbow  = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanRForearm2);
            var lHand   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanLPalm);
            var rHand   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanRPalm);
            var lKnee   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanLThigh2);
            var rKnee   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanRThigh2);
            var lFoot   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanLFoot);
            var rFoot   = P(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanRFoot);

            // Local helper that drops a segment when either endpoint is null.
            void S(Vector2? a, Vector2? b)
            {
                if (a.HasValue && b.HasValue) dl.AddLine(a.Value, b.Value, color, 1.5f);
            }

            if (_showSkeletonBones)
            {
                // Spine chain (head â†’ neck â†’ upper â†’ mid â†’ lower â†’ pelvis)
                S(head, neck); S(neck, upper); S(upper, mid); S(mid, lower); S(lower, pelvis);
                // Arms
                S(upper, lCollar); S(lCollar, lElbow); S(lElbow, lHand);
                S(upper, rCollar); S(rCollar, rElbow); S(rElbow, rHand);
                // Legs
                S(pelvis, lKnee); S(lKnee, lFoot);
                S(pelvis, rKnee); S(rKnee, rFoot);
            }

            if (_showSkeletonJoints)
            {
                void D(Vector2? p) { if (p.HasValue) dl.AddCircleFilled(p.Value, 3f, color, 6); }
                D(head); D(neck); D(upper); D(mid); D(lower); D(pelvis);
                D(lCollar); D(rCollar); D(lElbow); D(rElbow); D(lHand); D(rHand);
                D(lKnee);   D(rKnee);   D(lFoot); D(rFoot);
            }
        }

        /// <summary>
        /// Render pass for the live PhysX overlay. Called after the regular
        /// geometry pass + blocker highlights so the live capsules always
        /// end up on top. Reads the cached pose dict â€” empty until the next
        /// refresh tick fills it.
        /// </summary>
        private static void DrawLivePhysxOverlay(ImDrawListPtr dl, SceneSnapshot snap,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize)
        {
            if (!_showLivePhysxPlayers && !_showLivePhysxBones) return;

            float rangeSq = _renderRange * _renderRange;

            if (_showLivePhysxPlayers)
            {
                for (int i = 0; i < _livePhysxPlayerIndices.Count; i++)
                {
                    int idx = _livePhysxPlayerIndices[i];
                    if (!_livePhysxPoses.TryGetValue(idx, out var pose)) continue;
                    if (idx < 0 || idx >= snap.Actors.Length) continue;
                    var a = snap.Actors[idx];
                    // Distance cull matches the geometry pass â€” keeps the
                    // overlay's drawing budget honest at long render ranges.
                    if (Vector3.DistanceSquared(_camPos, pose.Position) > rangeSq) continue;
                    DrawCapsuleAt(dl, pose, a.PrimitiveSize.X, a.PrimitiveSize.Y,
                        viewProj, vpOrigin, vpSize, ColorLivePhysxPlayer, 2f);
                }
            }

            if (_showLivePhysxBones)
            {
                for (int i = 0; i < _livePhysxBoneIndices.Count; i++)
                {
                    int idx = _livePhysxBoneIndices[i];
                    if (!_livePhysxPoses.TryGetValue(idx, out var pose)) continue;
                    if (idx < 0 || idx >= snap.Actors.Length) continue;
                    var a = snap.Actors[idx];
                    if (Vector3.DistanceSquared(_camPos, pose.Position) > rangeSq) continue;
                    DrawCapsuleAt(dl, pose, a.PrimitiveSize.X, a.PrimitiveSize.Y,
                        viewProj, vpOrigin, vpSize, ColorLivePhysxBone, 1f);
                }
            }
        }

        private static void DrawVisOverlaySection()
        {
            ImGui.TextDisabled("Vis Check Overlay");

            ImGui.Checkbox("Highlight blockers", ref _highlightBlockers);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Draw orange outlines around actors that blocked a player\n" +
                    "sightline in the last VisibilityWorker tick.");

            ImGui.Checkbox("Show live rays", ref _showLiveRays);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Draw eye â†’ player rays from the last visibility tick.\n" +
                    "Green = visible, red = blocked.\n" +
                    "Requires VisibilityWorker to be running.");
        }

        private static void DrawClassifierRulesSection()
        {
            if (ImGui.TreeNode("Classifier Rules"))
            {
                ClassifierRulesWidget.Draw();
                ImGui.TreePop();
            }
        }

        private static void DrawCacheManagerSection()
        {
            ImGui.TextDisabled("Cache Manager");

            var (label, color) = SceneCache.State switch
            {
                SceneCacheState.Ready    => ("READY",    new Vector4(0.30f, 0.85f, 0.30f, 1f)),
                SceneCacheState.Building => ("BUILDING", new Vector4(0.95f, 0.85f, 0.20f, 1f)),
                SceneCacheState.Failed   => ("FAILED",   new Vector4(0.95f, 0.30f, 0.25f, 1f)),
                _                        => ("IDLE",     new Vector4(0.65f, 0.65f, 0.65f, 1f)),
            };
            ImGui.Text("State:");
            ImGui.SameLine();
            ImGui.TextColored(color, label);

            var snap = SceneCache.Snapshot;
            ImGui.Text($"Actors:       {snap.Actors.Length}");
            ImGui.Text($"Meshes:       {snap.Meshes.Length}");
            ImGui.Text($"HeightFields: {snap.HeightFields.Length}");
            ImGui.Text($"Map: {(string.IsNullOrEmpty(snap.MapId) ? "(none)" : snap.MapId)}");

            string? mapId = Memory.Game?.MapID;
            bool busy     = SceneCache.State == SceneCacheState.Building;
            bool canBuild = !string.IsNullOrEmpty(mapId) && !busy;

            ImGui.BeginDisabled(!canBuild);
            if (ImGui.Button("Build now"))
                SceneCache.TriggerBuild(mapId!);
            ImGui.SameLine();
            if (ImGui.Button("Invalidate + rebuild"))
            {
                SnapshotSerializer.TryDelete(mapId!);
                SceneCache.TriggerBuild(mapId!);
            }
            ImGui.EndDisabled();

            if (string.IsNullOrEmpty(mapId))
                ImGui.TextDisabled("(no active match)");
        }

        private static void DrawSnapshotsSection()
        {
            ImGui.TextDisabled("Saved Snapshots");

            // Status line shown for 4 s after a load attempt completes.
            if (!string.IsNullOrEmpty(_snapStatusMsg)
                && Environment.TickCount64 - _snapStatusMsgMs < 4000)
            {
                ImGui.TextColored(_snapStatusOk
                        ? new Vector4(0.40f, 0.85f, 0.40f, 1f)
                        : new Vector4(0.95f, 0.40f, 0.35f, 1f),
                    _snapStatusMsg);
            }

            int rows = 0;
            if (ImGui.BeginTable("##cacheview_snapshots", 4,
                    ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.ScrollY,
                    new Vector2(0f, 180f)))
            {
                ImGui.TableSetupColumn("Map",      ImGuiTableColumnFlags.WidthStretch, 0.46f);
                ImGui.TableSetupColumn("Size",     ImGuiTableColumnFlags.WidthStretch, 0.16f);
                ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthStretch, 0.22f);
                ImGui.TableSetupColumn("",         ImGuiTableColumnFlags.WidthStretch, 0.16f);
                ImGui.TableHeadersRow();

                foreach (var info in SnapshotSerializer.EnumerateSnapshots())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(info.MapId);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatSize(info.SizeBytes));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(info.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    ImGui.TableNextColumn();
                    ImGui.PushID(info.MapId);
                    if (ImGui.SmallButton("Load"))
                    {
                        bool ok = SceneCache.LoadFromDisk(info.MapId, out string err);
                        _snapStatusOk    = ok;
                        _snapStatusMsg   = ok
                            ? $"Loaded '{info.MapId}' ({SceneCache.Snapshot.Actors.Length} actors)"
                            : $"Load failed: {err}";
                        _snapStatusMsgMs = Environment.TickCount64;
                        // Auto-jump the camera onto the new geometry; otherwise the
                        // viewport stays black at world origin while the actors live
                        // hundreds of metres away in map coordinates.
                        if (ok) CenterOnSnapshot();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "Load this snapshot into the live cache for offline\n" +
                            "inspection. Skips the game-fingerprint check, so\n" +
                            "it works with no game attached. Magic / CRC still validated.");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("X"))
                    {
                        try { File.Delete(info.FilePath); } catch { /* best-effort */ }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Delete this snapshot file");
                    ImGui.PopID();
                    rows++;
                }
                ImGui.EndTable();
            }

            if (rows == 0) ImGui.TextDisabled("(no saved snapshots)");
        }

        // Status line shown briefly after a Load/Delete action in the snapshots table.
        private static string _snapStatusMsg   = "";
        private static long   _snapStatusMsgMs;
        private static bool   _snapStatusOk;

        // â”€â”€ Camera / projection helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Spherical-to-cartesian: turn (yaw, pitch) into a unit forward vector.
        /// Yaw 0 = +Z, yaw +Ï€/2 = +X (right-handed, +Y up).
        /// </summary>
        private static Vector3 ComputeForward(float yaw, float pitch)
        {
            float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
            float cy = MathF.Cos(yaw),   sy = MathF.Sin(yaw);
            return new Vector3(cp * sy, sp, cp * cy);
        }

        /// <summary>
        /// Reads ImGui IO once per frame and applies the current input state
        /// to the camera. Mouse-look only fires while the right button is held
        /// over the viewport; WASD only fires while the viewport is hovered.
        /// </summary>
        private static void UpdateCamera(float dt, bool hovered, bool active)
        {
            var io = ImGui.GetIO();

            if (active && ImGui.IsMouseDown(ImGuiMouseButton.Right))
            {
                const float Sens = 0.003f;
                Vector2 d = io.MouseDelta;
                _camYaw   -= d.X * Sens;
                _camPitch  = Math.Clamp(_camPitch - d.Y * Sens, -1.55f, 1.55f);
            }

            if (!hovered) return;

            float speed = _moveSpeed * dt;
            if (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift))
                speed *= 4f;

            Vector3 fwd   = ComputeForward(_camYaw, _camPitch);
            Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));

            if (ImGui.IsKeyDown(ImGuiKey.W))         _camPos += fwd   * speed;
            if (ImGui.IsKeyDown(ImGuiKey.S))         _camPos -= fwd   * speed;
            if (ImGui.IsKeyDown(ImGuiKey.A))         _camPos -= right * speed;
            if (ImGui.IsKeyDown(ImGuiKey.D))         _camPos += right * speed;
            if (ImGui.IsKeyDown(ImGuiKey.Space))     _camPos += Vector3.UnitY * speed;
            if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl))
                _camPos -= Vector3.UnitY * speed;
        }

        /// <summary>
        /// World-space point â†’ viewport pixel. Returns false (and an
        /// undefined <paramref name="screen"/>) when the point is behind the
        /// camera or otherwise outside the clip space.
        /// </summary>
        private static bool Project(Vector3 world, Matrix4x4 viewProj,
                                    Vector2 vpOrigin, Vector2 vpSize,
                                    out Vector2 screen)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProj);
            if (clip.W <= 0.001f) { screen = default; return false; }
            float invW = 1f / clip.W;
            screen = new Vector2(
                vpOrigin.X + (clip.X * invW + 1f) * 0.5f * vpSize.X,
                vpOrigin.Y + (1f - (clip.Y * invW + 1f) * 0.5f) * vpSize.Y
            );
            return true;
        }

        // â”€â”€ Filter helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static bool PassesTypeFilter(PxGeometryType t) => t switch
        {
            PxGeometryType.Sphere       => _showSphere,
            PxGeometryType.Capsule      => _showCapsule,
            PxGeometryType.Box          => _showBox,
            PxGeometryType.ConvexMesh   => _showConvex,
            PxGeometryType.TriangleMesh => _showTriMesh,
            PxGeometryType.HeightField  => _showHeightField,
            _                           => true,  // Plane / Invalid â€” always allow
        };

        private static bool PassesNameFilter(string? name)
        {
            if (string.IsNullOrEmpty(_nameFilter)) return true;
            if (string.IsNullOrEmpty(name)) return false;
            return name.Contains(_nameFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PassesLayerFilter(uint shapeLayerMask)
        {
            if (_layerDisplayFilter == uint.MaxValue) return true;
            return (_layerDisplayFilter & shapeLayerMask) != 0;
        }

        // â”€â”€ Vis-check overlay helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Reads <see cref="VisibilityWorker.LastPerPlayer"/> and collects the
        /// snapshot-relative actor indices of every actor that blocked a
        /// sightline. Called once per frame when blocker highlighting is on.
        /// </summary>
        private static HashSet<int> BuildBlockerSet()
        {
            var set     = new HashSet<int>();
            var results = VisibilityWorker.LastPerPlayer;
            for (int i = 0; i < results.Count; i++)
            {
                int idx = results[i].BlockerActorIdx;
                if (idx >= 0) set.Add(idx);
            }
            return set;
        }

        // â”€â”€ Color / draw helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Per-actor wireframe colour â€” driven by <see cref="_colorMode"/>
        /// plus optional distance-based alpha fade.
        /// </summary>
        private static uint PickColor(CachedActor a, float distSq)
        {
            uint baseColor = _colorMode switch
            {
                ColorMode.SeeThrough   => a.IsSeeThrough ? ColorSeeThroughAabb : ColorActorAabb,
                ColorMode.GeometryType => a.GeometryType switch
                {
                    PxGeometryType.Sphere       => ColorTypeSphere,
                    PxGeometryType.Capsule      => ColorTypeCapsule,
                    PxGeometryType.Box          => ColorTypeBox,
                    PxGeometryType.ConvexMesh   => ColorTypeConvex,
                    PxGeometryType.TriangleMesh => ColorTypeTriMesh,
                    PxGeometryType.HeightField  => ColorTypeHF,
                    _                           => ColorActorAabb,
                },
                _                      => ColorActorAabb,
            };

            if (!_distanceFade) return baseColor;

            // Fade from 100 % alpha at distance 0 down to ~25 % at the range
            // slider. Quadratic curve: gentle near the camera, steeper at distance.
            float rangeSq = _renderRange * _renderRange;
            if (rangeSq <= 0f) return baseColor;
            float t  = MathF.Min(distSq / rangeSq, 1f);
            byte  a8 = (byte)(255f * (1f - 0.75f * t));
            return (baseColor & 0x00FFFFFFu) | ((uint)a8 << 24);
        }

        /// <summary>
        /// Draws a single AABB as a 12-edge wireframe box. Each edge is
        /// independently projected â€” if either endpoint projects behind the
        /// camera, that edge is dropped.
        /// </summary>
        private static void DrawAabb(ImDrawListPtr dl, Vector3 min, Vector3 max,
                                     Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize,
                                     uint color)
            => DrawAabbThick(dl, min, max, viewProj, vpOrigin, vpSize, color, 1f);

        /// <summary>
        /// Same as <see cref="DrawAabb"/> but with a caller-controlled line
        /// thickness â€” used for the hover-highlight and blocker-highlight passes.
        /// </summary>
        private static void DrawAabbThick(ImDrawListPtr dl, Vector3 min, Vector3 max,
                                          Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize,
                                          uint color, float thickness)
        {
            // 8 corners, indexed by 3-bit binary: bit0=X, bit1=Y, bit2=Z
            Span<Vector3> c = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                c[i] = new Vector3(
                    (i & 1) == 0 ? min.X : max.X,
                    (i & 2) == 0 ? min.Y : max.Y,
                    (i & 4) == 0 ? min.Z : max.Z);
            }

            ReadOnlySpan<(int a, int b)> edges = [
                (0,1),(1,3),(3,2),(2,0),  // bottom face
                (4,5),(5,7),(7,6),(6,4),  // top face
                (0,4),(1,5),(2,6),(3,7),  // vertical pillars
            ];
            foreach (var (ia, ib) in edges)
            {
                if (Project(c[ia], viewProj, vpOrigin, vpSize, out var pa) &&
                    Project(c[ib], viewProj, vpOrigin, vpSize, out var pb))
                {
                    dl.AddLine(pa, pb, color, thickness);
                }
            }
        }

        // â”€â”€ True-shape rendering â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // Each branch reads the actor's WorldTransform (quaternion + position,
        // already composed at cache build time as actorÃ—shape-local pose) plus
        // the type-specific data: PrimitiveSize for sphere/capsule/box, an
        // index into the snapshot's mesh tables for trimesh/convex/heightfield.
        // AABB is the universal fallback when data is unavailable or per-shape
        // budgets are exceeded.

        private static void DrawShape(ImDrawListPtr dl, CachedActor a, SceneSnapshot snap,
                                      Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color)
        {
            switch (a.GeometryType)
            {
                case PxGeometryType.Sphere:
                    DrawSphereShape(dl, a, viewProj, vpOrigin, vpSize, color);
                    break;
                case PxGeometryType.Capsule:
                    DrawCapsuleShape(dl, a, viewProj, vpOrigin, vpSize, color);
                    break;
                case PxGeometryType.Box:
                    DrawOBBShape(dl, a, viewProj, vpOrigin, vpSize, color);
                    break;
                case PxGeometryType.ConvexMesh:
                    if (_convexFaces
                        && (uint)a.ConvexMeshIndex < (uint)snap.ConvexMeshes.Length)
                    {
                        DrawConvexMeshShape(dl, a, snap.ConvexMeshes[a.ConvexMeshIndex],
                            viewProj, vpOrigin, vpSize, color);
                    }
                    else
                    {
                        DrawAabb(dl, a.WorldAabbMin, a.WorldAabbMax, viewProj, vpOrigin, vpSize, color);
                    }
                    break;
                case PxGeometryType.TriangleMesh:
                    if ((uint)a.MeshIndex < (uint)snap.Meshes.Length)
                    {
                        var m = snap.Meshes[a.MeshIndex];
                        if (m.TriangleCount <= _triMeshBudget)
                        {
                            DrawTriMeshShape(dl, a, m, viewProj, vpOrigin, vpSize, color);
                            break;
                        }
                    }
                    DrawAabb(dl, a.WorldAabbMin, a.WorldAabbMax, viewProj, vpOrigin, vpSize, color);
                    break;
                case PxGeometryType.HeightField:
                    if ((uint)a.HeightFieldIndex < (uint)snap.HeightFields.Length)
                    {
                        DrawHeightFieldShape(dl, a, snap.HeightFields[a.HeightFieldIndex],
                            viewProj, vpOrigin, vpSize, color);
                    }
                    else
                    {
                        DrawAabb(dl, a.WorldAabbMin, a.WorldAabbMax, viewProj, vpOrigin, vpSize, color);
                    }
                    break;
                default:
                    // Plane / Invalid â€” AABB is the only meaningful representation.
                    DrawAabb(dl, a.WorldAabbMin, a.WorldAabbMax, viewProj, vpOrigin, vpSize, color);
                    break;
            }
        }

        /// <summary>
        /// PhysX sphere: <c>PrimitiveSize.X</c> = radius, centred on
        /// <c>WorldTransform.Position</c> (rotation irrelevant). Drawn as three
        /// world-axis-aligned great circles so the silhouette reads as a sphere
        /// regardless of camera angle.
        /// </summary>
        private static void DrawSphereShape(ImDrawListPtr dl, CachedActor a,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color)
        {
            Vector3 center = a.WorldTransform.Position;
            float   r      = a.PrimitiveSize.X;
            if (r <= 0f) return;
            DrawCircle(dl, center, Vector3.UnitX, Vector3.UnitY, r, viewProj, vpOrigin, vpSize, color, 24);
            DrawCircle(dl, center, Vector3.UnitY, Vector3.UnitZ, r, viewProj, vpOrigin, vpSize, color, 24);
            DrawCircle(dl, center, Vector3.UnitX, Vector3.UnitZ, r, viewProj, vpOrigin, vpSize, color, 24);
        }

        /// <summary>
        /// PhysX capsule: primary axis is local <c>+X</c>,
        /// <c>PrimitiveSize.X</c> = radius, <c>PrimitiveSize.Y</c> = half-height
        /// (cylinder body extent, not including caps). Rendered as two endcap
        /// circles, four body lines along the cylinder, and two hemisphere
        /// half-circle pairs at each end â€” fully oriented by the quaternion.
        /// </summary>
        private static void DrawCapsuleShape(ImDrawListPtr dl, CachedActor a,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color)
            => DrawCapsuleAt(dl, a.WorldTransform, a.PrimitiveSize.X, a.PrimitiveSize.Y,
                             viewProj, vpOrigin, vpSize, color, 1f);

        /// <summary>
        /// Capsule renderer parametrised on transform + radius + half-height
        /// instead of a <see cref="CachedActor"/>. The live PhysX overlay
        /// (PlayerSuperior + Base Human bones) calls this with a freshly-read
        /// transform per frame, so we can re-render the capsule at its
        /// current world pose without mutating the immutable snapshot.
        /// </summary>
        private static void DrawCapsuleAt(ImDrawListPtr dl,
            PxTransform tr, float r, float hh,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color, float thickness)
        {
            if (r <= 0f) return;

            Vector3 axisX = tr.TransformDirection(Vector3.UnitX);
            Vector3 axisY = tr.TransformDirection(Vector3.UnitY);
            Vector3 axisZ = tr.TransformDirection(Vector3.UnitZ);
            Vector3 endA  = tr.Position - axisX * hh;
            Vector3 endB  = tr.Position + axisX * hh;

            // Cross-section circles at each endcap (in the YZ plane of the capsule).
            DrawCircle(dl, endA, axisY, axisZ, r, viewProj, vpOrigin, vpSize, color, 18);
            DrawCircle(dl, endB, axisY, axisZ, r, viewProj, vpOrigin, vpSize, color, 18);

            // Four longitudinal body lines (0Â°, 90Â°, 180Â°, 270Â° around the axis).
            for (int i = 0; i < 4; i++)
            {
                float t   = i * (MathF.PI * 0.5f);
                Vector3 o = axisY * MathF.Cos(t) * r + axisZ * MathF.Sin(t) * r;
                if (Project(endA + o, viewProj, vpOrigin, vpSize, out var pa)
                    && Project(endB + o, viewProj, vpOrigin, vpSize, out var pb))
                    dl.AddLine(pa, pb, color, thickness);
            }

            // Two half-circle wireframes per endcap forming a hemisphere outline.
            DrawHalfCircle(dl, endA, -axisX, axisY, r, viewProj, vpOrigin, vpSize, color, 10);
            DrawHalfCircle(dl, endA, -axisX, axisZ, r, viewProj, vpOrigin, vpSize, color, 10);
            DrawHalfCircle(dl, endB,  axisX, axisY, r, viewProj, vpOrigin, vpSize, color, 10);
            DrawHalfCircle(dl, endB,  axisX, axisZ, r, viewProj, vpOrigin, vpSize, color, 10);
        }

        /// <summary>
        /// PhysX box: <c>PrimitiveSize</c> = half-extents along local axes.
        /// Eight corners are computed in local space and rotated by the actor's
        /// world quaternion, giving the proper oriented bounding box rather
        /// than the conservative axis-aligned one.
        /// </summary>
        private static void DrawOBBShape(ImDrawListPtr dl, CachedActor a,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color)
        {
            Vector3 he = a.PrimitiveSize;
            var tr = a.WorldTransform;
            Span<Vector3> c = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                Vector3 local = new(
                    (i & 1) == 0 ? -he.X : he.X,
                    (i & 2) == 0 ? -he.Y : he.Y,
                    (i & 4) == 0 ? -he.Z : he.Z);
                c[i] = tr.TransformPoint(local);
            }
            ReadOnlySpan<(int a, int b)> edges = [
                (0,1),(1,3),(3,2),(2,0),
                (4,5),(5,7),(7,6),(6,4),
                (0,4),(1,5),(2,6),(3,7),
            ];
            foreach (var (ia, ib) in edges)
            {
                if (Project(c[ia], viewProj, vpOrigin, vpSize, out var pa)
                    && Project(c[ib], viewProj, vpOrigin, vpSize, out var pb))
                    dl.AddLine(pa, pb, color, 1f);
            }
        }

        /// <summary>
        /// Convex hull face wireframe. For each polygon plane, scans the hull
        /// vertices to find those lying on the plane (within tolerance), sorts
        /// them angularly around the plane normal, and draws the closed face
        /// polygon. We don't have explicit face-vertex indices in the cache
        /// (the raycaster only needs planes) so this re-derives connectivity
        /// per face â€” fine for the modest vertex counts (â‰¤ 32) PhysX convex
        /// meshes carry.
        /// </summary>
        private static void DrawConvexMeshShape(ImDrawListPtr dl, CachedActor a,
            CachedConvexMesh m, Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color)
        {
            var verts  = m.Vertices;
            var planes = m.PolygonPlanes;
            if (verts.Length == 0 || planes.Length == 0) return;

            // Plane-membership tolerance scaled to the local AABB so absolute
            // hull size doesn't break the test (a 50-metre hull and a 50-cm
            // hull have very different "small" floats).
            Vector3 ext = m.LocalAabbMax - m.LocalAabbMin;
            float   diag = MathF.Sqrt(ext.X * ext.X + ext.Y * ext.Y + ext.Z * ext.Z);
            float   tol  = MathF.Max(0.001f, diag * 1e-3f);

            var tr = a.WorldTransform;

            // Small stack buffers â€” typical convex face has â‰¤ 8 vertices.
            Span<int>   faceIdx = stackalloc int[32];
            Span<float> faceAng = stackalloc float[32];

            for (int p = 0; p < planes.Length; p++)
            {
                Vector4 plane = planes[p];
                Vector3 n     = new(plane.X, plane.Y, plane.Z);
                float   d     = plane.W;

                int count = 0;
                for (int vi = 0; vi < verts.Length && count < faceIdx.Length; vi++)
                {
                    if (MathF.Abs(Vector3.Dot(n, verts[vi]) + d) < tol)
                        faceIdx[count++] = vi;
                }
                if (count < 3) continue;

                // Face centroid (in local space).
                Vector3 cen = Vector3.Zero;
                for (int k = 0; k < count; k++) cen += verts[faceIdx[k]];
                cen /= count;

                // 2D basis inside the face plane: pick any axis not parallel
                // to the normal, project to get u; v = n Ã— u for right-handed.
                Vector3 refAxis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                Vector3 u = Vector3.Normalize(Vector3.Cross(n, refAxis));
                Vector3 v = Vector3.Cross(n, u);

                for (int k = 0; k < count; k++)
                {
                    Vector3 dir = verts[faceIdx[k]] - cen;
                    faceAng[k] = MathF.Atan2(Vector3.Dot(dir, v), Vector3.Dot(dir, u));
                }

                // Insertion sort by angle â€” count is â‰¤ 32 so anything fancier
                // is overkill.
                for (int i = 1; i < count; i++)
                {
                    float fa = faceAng[i];
                    int   fi = faceIdx[i];
                    int   j  = i - 1;
                    while (j >= 0 && faceAng[j] > fa)
                    {
                        faceAng[j + 1] = faceAng[j];
                        faceIdx[j + 1] = faceIdx[j];
                        j--;
                    }
                    faceAng[j + 1] = fa;
                    faceIdx[j + 1] = fi;
                }

                for (int k = 0; k < count; k++)
                {
                    int next = (k + 1) % count;
                    Vector3 wa = tr.TransformPoint(verts[faceIdx[k]]);
                    Vector3 wb = tr.TransformPoint(verts[faceIdx[next]]);
                    if (Project(wa, viewProj, vpOrigin, vpSize, out var pa)
                        && Project(wb, viewProj, vpOrigin, vpSize, out var pb))
                        dl.AddLine(pa, pb, color, 1f);
                }
            }
        }

        /// <summary>
        /// Triangle-mesh wireframe â€” every triangle's three edges drawn in
        /// world space. Edges are not de-duplicated (shared edges between
        /// adjacent triangles get drawn twice), which is acceptable inside
        /// the per-mesh triangle budget. Above the budget the dispatcher
        /// falls back to AABB.
        /// </summary>
        private static void DrawTriMeshShape(ImDrawListPtr dl, CachedActor a,
            CachedTriMesh m, Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color)
        {
            var tr    = a.WorldTransform;
            var verts = m.Vertices;
            var idx   = m.Indices;
            int triCount = m.TriangleCount;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = idx[t * 3 + 0];
                int i1 = idx[t * 3 + 1];
                int i2 = idx[t * 3 + 2];
                if ((uint)i0 >= (uint)verts.Length
                    || (uint)i1 >= (uint)verts.Length
                    || (uint)i2 >= (uint)verts.Length) continue;

                Vector3 w0 = tr.TransformPoint(verts[i0]);
                Vector3 w1 = tr.TransformPoint(verts[i1]);
                Vector3 w2 = tr.TransformPoint(verts[i2]);

                bool ok0 = Project(w0, viewProj, vpOrigin, vpSize, out var p0);
                bool ok1 = Project(w1, viewProj, vpOrigin, vpSize, out var p1);
                bool ok2 = Project(w2, viewProj, vpOrigin, vpSize, out var p2);
                if (ok0 && ok1) dl.AddLine(p0, p1, color, 1f);
                if (ok1 && ok2) dl.AddLine(p1, p2, color, 1f);
                if (ok2 && ok0) dl.AddLine(p2, p0, color, 1f);
            }
        }

        /// <summary>
        /// Height-field wireframe â€” samples the grid at <c>_hfStep</c> stride
        /// and draws one polyline per row and one per column. World position
        /// of sample <c>(row, col)</c> is
        /// <c>(col*ColumnScale, sample*HeightScale, row*RowScale)</c> in
        /// local space, transformed by the actor's world pose.
        /// </summary>
        private static void DrawHeightFieldShape(ImDrawListPtr dl, CachedActor a,
            CachedHeightField hf, Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color)
        {
            int step = Math.Max(1, _hfStep);
            var tr = a.WorldTransform;

            // Row polylines (constant row, varying column).
            for (int r = 0; r < hf.Rows; r += step)
            {
                bool   havePrev = false;
                Vector2 prev = default;
                for (int c = 0; c < hf.Columns; c += step)
                {
                    float h = hf.Sample(r, c) * hf.HeightScale;
                    Vector3 world = tr.TransformPoint(
                        new Vector3(c * hf.ColumnScale, h, r * hf.RowScale));
                    if (Project(world, viewProj, vpOrigin, vpSize, out var sp))
                    {
                        if (havePrev) dl.AddLine(prev, sp, color, 1f);
                        prev     = sp;
                        havePrev = true;
                    }
                    else havePrev = false;
                }
            }

            // Column polylines (constant column, varying row).
            for (int c = 0; c < hf.Columns; c += step)
            {
                bool   havePrev = false;
                Vector2 prev = default;
                for (int r = 0; r < hf.Rows; r += step)
                {
                    float h = hf.Sample(r, c) * hf.HeightScale;
                    Vector3 world = tr.TransformPoint(
                        new Vector3(c * hf.ColumnScale, h, r * hf.RowScale));
                    if (Project(world, viewProj, vpOrigin, vpSize, out var sp))
                    {
                        if (havePrev) dl.AddLine(prev, sp, color, 1f);
                        prev     = sp;
                        havePrev = true;
                    }
                    else havePrev = false;
                }
            }
        }

        /// <summary>
        /// Generic circle in 3D â€” emits a closed polyline whose points are
        /// <c>center + (axisUÂ·cos(Î¸) + axisVÂ·sin(Î¸))Â·radius</c> for Î¸ stepping
        /// around 2Ï€. <paramref name="axisU"/> and <paramref name="axisV"/>
        /// must be orthonormal and orthogonal to the circle's normal; passing
        /// world-axis unit vectors gives an axis-aligned circle.
        /// </summary>
        private static void DrawCircle(ImDrawListPtr dl, Vector3 center,
            Vector3 axisU, Vector3 axisV, float radius,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color, int segments)
        {
            if (segments < 3) segments = 3;
            // Buffer the projected points so we can close the loop cleanly
            // even when the camera clips a few segments.
            Span<Vector2> pts = stackalloc Vector2[64];
            Span<bool>    ok  = stackalloc bool[64];
            if (segments > 64) segments = 64;

            for (int i = 0; i < segments; i++)
            {
                float t = (float)i / segments * (MathF.PI * 2f);
                Vector3 p = center + (axisU * MathF.Cos(t) + axisV * MathF.Sin(t)) * radius;
                ok[i] = Project(p, viewProj, vpOrigin, vpSize, out pts[i]);
            }
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                if (ok[i] && ok[j]) dl.AddLine(pts[i], pts[j], color, 1f);
            }
        }

        /// <summary>
        /// Half-circle from Î¸ = 0 to Î¸ = Ï€, parameterised as
        /// <c>center + capAxisÂ·sin(Î¸)Â·radius + perpAxisÂ·cos(Î¸)Â·radius</c>.
        /// <paramref name="capAxis"/> points outward from the parent volume
        /// (e.g. the capsule end's outward direction), so the resulting arc
        /// curves away on the appropriate side.
        /// </summary>
        private static void DrawHalfCircle(ImDrawListPtr dl, Vector3 center,
            Vector3 capAxis, Vector3 perpAxis, float radius,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize, uint color, int segments)
        {
            if (segments < 2) segments = 2;
            bool   havePrev = false;
            Vector2 prev = default;
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments * MathF.PI;
                Vector3 p = center + capAxis * MathF.Sin(t) * radius
                                    + perpAxis * MathF.Cos(t) * radius;
                if (Project(p, viewProj, vpOrigin, vpSize, out var sp))
                {
                    if (havePrev) dl.AddLine(prev, sp, color, 1f);
                    prev     = sp;
                    havePrev = true;
                }
                else havePrev = false;
            }
        }

        /// <summary>
        /// Draws a single player as a vertical column from feet up to
        /// <see cref="_playerMarkerHeight"/>, with a small dot at the head
        /// and optionally a name label above. Distance-cull-aware so it
        /// honours the same Range slider as the geometry pass â€” otherwise
        /// players 500 m away would still draw on top of everything.
        /// </summary>
        private static void DrawPlayerMarker(
            ImDrawListPtr dl,
            eft_dma_radar.Silk.Tarkov.GameWorld.Player.Player p,
            Matrix4x4 viewProj, Vector2 vpOrigin, Vector2 vpSize)
        {
            Vector3 feet = p.Position;
            float distSq = Vector3.DistanceSquared(_camPos, feet);
            if (distSq > _renderRange * _renderRange) return;

            // Local player = green. Enemies = red, dimmed when vischeck says
            // they're currently visible to the local eye (lets the user verify
            // the cache view matches what ESP is showing in-match).
            uint color;
            if (p.IsLocalPlayer)
                color = ColorPlayerLocal;
            else if (_dimVisiblePlayers && p.IsVisible)
                color = ColorPlayerEnemyDim;
            else
                color = ColorPlayerEnemy;

            Vector3 head = feet + new Vector3(0f, _playerMarkerHeight, 0f);
            bool okFeet = Project(feet, viewProj, vpOrigin, vpSize, out var feetSc);
            bool okHead = Project(head, viewProj, vpOrigin, vpSize, out var headSc);

            // Both endpoints behind the camera? Nothing usable to draw.
            if (!okFeet && !okHead) return;

            // Vertical body line â€” only emitted when both ends project. A
            // single endpoint isn't enough for a meaningful line and would
            // smear off-screen as the camera turns.
            if (okFeet && okHead)
                dl.AddLine(feetSc, headSc, color, 2.0f);

            // Head dot â€” always drawn when the head projects, regardless of
            // feet. Helps pick out players hidden behind the bottom edge of
            // a window or low cover.
            if (okHead)
                dl.AddCircleFilled(headSc, 4f, color, 8);

            // Feet ring â€” small ground anchor so the player sits visually on
            // the floor instead of floating mid-air at distance.
            if (okFeet)
                dl.AddCircle(feetSc, 5f, color, 10, 1.5f);

            // Optional name label above the head dot. Same alpha as the
            // marker so dimming carries through to the text.
            if (_showPlayerNames && okHead && !string.IsNullOrEmpty(p.Name))
            {
                var labelPos = new Vector2(headSc.X + 6f, headSc.Y - 14f);
                dl.AddText(labelPos, color, p.Name);
            }
        }

        // â”€â”€ Small helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
