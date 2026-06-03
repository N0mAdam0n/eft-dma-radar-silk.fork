namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// A single rigid collider in the scene snapshot.
    /// <para>
    /// Two key invariants the raycaster relies on:
    /// <list type="bullet">
    ///   <item>All fields are populated at cache build time and never mutated.
    ///     Multiple visibility worker frames can read this struct concurrently
    ///     with zero locking.</item>
    ///   <item><see cref="WorldAabbMin"/>/<see cref="WorldAabbMax"/> are
    ///     pre-computed and over-conservative â€” never smaller than the actual
    ///     world-space extent. The raycaster uses this AABB as a cull gate
    ///     before doing the expensive geometry-specific test.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Mesh-bearing actors (TriangleMesh / HeightField) carry an <em>index</em>
    /// into <see cref="SceneSnapshot.Meshes"/> / <see cref="SceneSnapshot.HeightFields"/>.
    /// Primitive actors (Sphere / Capsule / Box) carry their parameters inline
    /// via <see cref="PrimitiveSize"/>:
    /// <list type="bullet">
    ///   <item>Sphere   â€” <c>PrimitiveSize.X</c> = radius</item>
    ///   <item>Capsule  â€” <c>PrimitiveSize.X</c> = radius, <c>.Y</c> = half-height</item>
    ///   <item>Box      â€” <c>PrimitiveSize</c> = half-extents (x, y, z)</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class CachedActor
    {
        public required PxTransform     WorldTransform     { get; init; }
        public required Vector3         WorldAabbMin       { get; init; }
        public required Vector3         WorldAabbMax       { get; init; }
        public required PxGeometryType  GeometryType       { get; init; }

        /// <summary>Index into <see cref="SceneSnapshot.Meshes"/>, or -1 if not a triangle mesh.</summary>
        public int MeshIndex { get; init; } = -1;

        /// <summary>Index into <see cref="SceneSnapshot.ConvexMeshes"/>, or -1 if not a convex mesh.</summary>
        public int ConvexMeshIndex { get; init; } = -1;

        /// <summary>Index into <see cref="SceneSnapshot.HeightFields"/>, or -1 if not a height field.</summary>
        public int HeightFieldIndex { get; init; } = -1;

        /// <summary>Inline primitive params for Sphere / Capsule / Box (see class summary).</summary>
        public Vector3 PrimitiveSize { get; init; } = default;

        /// <summary>
        /// Source pointer (NpRigidStatic / NpRigidDynamic). Stored for diagnostic
        /// logs only â€” the raycaster never reads it.
        /// </summary>
        public ulong ActorBase { get; init; } = 0;

        /// <summary>
        /// The shape's Unity layer as a one-hot bit (<c>1 &lt;&lt; layerIndex</c>).
        /// Read from <c>queryFilterData.word1</c> at NpShape +0x54. Confirmed by
        /// the live SHAPE-COMPARE diagnostic: word1 column shows clean power-of-2
        /// values across shapes (layer 12 / 18 / 29 dominant in Arena_AutoService),
        /// while word0 is a multi-bit "collision-group" mask. word1 is what we
        /// filter on for see-through layers (foliage, decals, gameplay-only).
        /// </summary>
        public uint ShapeLayerMask { get; init; } = 0;

        /// <summary>
        /// Multi-bit collision-group mask from <c>queryFilterData.word0</c>
        /// (NpShape +0x50). Encodes which groups this shape interacts with.
        /// Stored for diagnostic display; not currently used as a filter input.
        /// </summary>
        public uint ShapeGroupMask { get; init; } = 0;

        /// <summary>
        /// The actor's Unity <c>GameObject.name</c> if reachable, otherwise the
        /// empty string. Read at ingest via the chain
        /// <c>NpShape+0x10 â†’ NativeCollider+0x38 â†’ NativeGameObject+0x68 (NamePtr â†’ C-string)</c>.
        /// Unity 6 layout: <c>Comp_GameObject=0x38</c>, <c>GO_Name=0x68</c>
        /// (the +0x20 shift from Unity 2022). See <c>UNITY_ENGINE_CHANGES.md</c> Â§2.
        /// Used for diagnostic logs and the Cache View tooltip â€” gives us
        /// human-readable identifiers like <c>"PlayerCollider_Body_01"</c> or
        /// <c>"Wall_Concrete_LongSection"</c> instead of just layer categories.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// The actor's Unity <c>GameObject.layer</c> as a signed integer (0-31),
        /// or <c>-1</c> when the chain read failed. The exact offset within
        /// NativeGameObject is build-specific; SceneCache runs a layer-offset
        /// probe each build to identify it empirically (by matching the read
        /// value against <c>log2(ShapeLayerMask)</c> from PxFilterData).
        /// <para>
        /// Cross-check field: should match <c>log2(ShapeLayerMask)</c>. Divergence
        /// would indicate either a multi-shape actor or a layer-mask reading bug.
        /// </para>
        /// </summary>
        public int UnityLayer { get; init; } = -1;

        /// <summary>
        /// Pre-classified see-through verdict â€” the raycaster's Gate 0 reads
        /// this as a single bool to decide whether to skip the actor. Computed
        /// by <see cref="VisibilityClassifier.Classify"/> at snapshot build /
        /// load time. Combines the historical layer-mask rule
        /// (<see cref="Raycaster.SeeThroughLayerMask"/>) with the per-actor
        /// name patterns in <see cref="VisibilityClassifier.GlobalNamePatterns"/>
        /// and any per-map additions registered via
        /// <see cref="VisibilityClassifier.SetMapPatterns"/>.
        /// <para>
        /// Mutable from inside the assembly so the classifier can update the
        /// flag in place when the user edits the rule lists at runtime
        /// (<see cref="VisibilityClassifier.Reclassify"/>). External callers
        /// should treat it as read-only.
        /// </para>
        /// </summary>
        public bool IsSeeThrough { get; internal set; }
    }

    /// <summary>
    /// A cooked triangle mesh â€” vertices in mesh-local space plus a flat
    /// 32-bit index buffer (always normalized to <c>int</c> at ingest, even
    /// when the source stored 16-bit indices).
    /// <para>
    /// Held by <see cref="SceneSnapshot.Meshes"/>. Referenced by zero or more
    /// <see cref="CachedActor"/> entries via <see cref="CachedActor.MeshIndex"/>.
    /// </para>
    /// </summary>
    internal sealed class CachedTriMesh
    {
        public required Vector3[] Vertices     { get; init; }
        public required int[]     Indices      { get; init; } // length = TriangleCount * 3
        public required int       TriangleCount{ get; init; }
        public required Vector3   LocalAabbMin { get; init; }
        public required Vector3   LocalAabbMax { get; init; }

        /// <summary>Source pointer (PxTriangleMesh). Diagnostic only.</summary>
        public ulong MeshBase { get; init; } = 0;
    }

    /// <summary>
    /// A cooked convex mesh â€” a closed convex polyhedron defined by an array
    /// of <see cref="Vertices"/> and an array of <see cref="PolygonPlanes"/>
    /// (one outward-facing plane per face).
    /// <para>
    /// PhysX stores per-face vertex indices too, but the polytope slab
    /// raycast only needs the plane equations (MÃ¶ller-style slab test in
    /// half-space form) â€” so we drop the index buffer at ingest and keep
    /// memory tight. Vertices are kept for the Cache View wireframe and
    /// for any future geometry-dependent feature.
    /// </para>
    /// </summary>
    internal sealed class CachedConvexMesh
    {
        public required Vector3[] Vertices     { get; init; }
        /// <summary>
        /// One Vector4 per polygon, packed as <c>(normal.X, normal.Y, normal.Z, d)</c>
        /// where the plane equation is <c>nÂ·p + d = 0</c> (PhysX
        /// <c>PxPlane</c> convention; outward-facing normal, polyhedron
        /// interior is <c>nÂ·p + d &lt; 0</c>).
        /// </summary>
        public required Vector4[] PolygonPlanes { get; init; }
        public required int       PolygonCount  { get; init; }
        public required Vector3   LocalAabbMin  { get; init; }
        public required Vector3   LocalAabbMax  { get; init; }

        /// <summary>Source pointer (PxConvexMesh). Diagnostic only.</summary>
        public ulong ConvexMeshBase { get; init; } = 0;
    }

    /// <summary>
    /// A cooked height field â€” a row-major grid of 16-bit signed heights.
    /// World position of sample <c>(row, col)</c> = local
    /// <c>(col*ColumnScale, sample*HeightScale, row*RowScale)</c>, then the
    /// owning actor's <see cref="CachedActor.WorldTransform"/>.
    /// <para>
    /// We keep samples as the raw <c>short[]</c> â€” never widen to float â€” so
    /// memory stays bounded even for large maps.
    /// </para>
    /// </summary>
    internal sealed class CachedHeightField
    {
        public required short[] Samples      { get; init; } // length = Rows * Columns
        public required int     Rows         { get; init; }
        public required int     Columns      { get; init; }
        public required float   RowScale     { get; init; }
        public required float   ColumnScale  { get; init; }
        public required float   HeightScale  { get; init; }
        public required Vector3 LocalAabbMin { get; init; }
        public required Vector3 LocalAabbMax { get; init; }

        /// <summary>Source pointer (PxHeightField). Diagnostic only.</summary>
        public ulong HeightFieldBase { get; init; } = 0;

        /// <summary>Sample at <c>(row, col)</c>, or 0 if out of bounds.</summary>
        public short Sample(int row, int col)
        {
            if ((uint)row >= (uint)Rows || (uint)col >= (uint)Columns) return 0;
            return Samples[row * Columns + col];
        }
    }

    /// <summary>
    /// Immutable snapshot of the PhysX static-world geometry needed to answer
    /// "is point B visible from point A?" â€” produced by the scene cache, consumed
    /// by the visibility worker, read-only for everyone else.
    /// <para>
    /// Atomic swap: the cache builds a fresh <see cref="SceneSnapshot"/>, then
    /// publishes it with a single reference write. Readers grab a reference,
    /// hold it for their frame, and may keep reading even after a new snapshot
    /// replaces this one â€” until they drop the reference, the snapshot's
    /// arrays remain valid.
    /// </para>
    /// </summary>
    internal sealed class SceneSnapshot
    {
        public required CachedActor[]       Actors       { get; init; }
        public required CachedTriMesh[]     Meshes       { get; init; }
        public required CachedConvexMesh[]  ConvexMeshes { get; init; }
        public required CachedHeightField[] HeightFields { get; init; }

        /// <summary>UTC <see cref="Environment.TickCount64"/> when this snapshot was completed.</summary>
        public required long BuildTickMs { get; init; }

        /// <summary>Map id the snapshot was built for; helps the cache fingerprint catch map changes.</summary>
        public required string MapId { get; init; }

        /// <summary>Source <c>NpPhysics</c> pointer at the time of the build. Diagnostic.</summary>
        public required ulong NpPhysics { get; init; }

        /// <summary>Total actors observed in the source <c>NpScene</c> (may exceed <see cref="Actors"/>.Length when we drop some at ingest).</summary>
        public required int SourceActorCount { get; init; }

        /// <summary>Empty / placeholder snapshot returned before the first build completes.</summary>
        public static readonly SceneSnapshot Empty = new()
        {
            Actors           = Array.Empty<CachedActor>(),
            Meshes           = Array.Empty<CachedTriMesh>(),
            ConvexMeshes     = Array.Empty<CachedConvexMesh>(),
            HeightFields     = Array.Empty<CachedHeightField>(),
            BuildTickMs      = 0,
            MapId            = string.Empty,
            NpPhysics        = 0,
            SourceActorCount = 0,
        };

        public bool IsEmpty => Actors.Length == 0;

        public override string ToString()
            => $"SceneSnapshot(actors={Actors.Length}, meshes={Meshes.Length}, " +
               $"heightfields={HeightFields.Length}, map='{MapId}')";
    }
}
