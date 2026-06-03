namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// PhysX runtime object-class tag. PhysX writes this as a 16-bit value at
    /// <c>+0x8</c> of every <c>PxBase</c> object. We only enumerate the cases we
    /// actually distinguish; anything else stays raw.
    /// <para>
    /// Used to discriminate <c>NpRigidStatic</c> vs <c>NpRigidDynamic</c> while
    /// walking the per-scene actor list, plus the two flavors of triangle-mesh
    /// the engine ships (the BVH version is the wire tag inside an
    /// <c>NpTriangleMesh</c> object).
    /// </para>
    /// </summary>
    internal enum PxConcreteType : ushort
    {
        // We only list the values we test against. All other tags (cloth fabric,
        // particle systems, articulations, etc.) pass through as their raw u16.
        HeightField        = 0,
        ConvexMesh         = 1,
        TriangleMeshBvh33  = 2,
        TriangleMeshBvh34  = 3,
        RigidDynamic       = 5,
        RigidStatic        = 6,
        Shape              = 7,
        Material           = 8,
    }

    /// <summary>
    /// Geometry kind stored at offset 0 of every <c>PxGeometry</c>. PhysX uses
    /// this to disambiguate the geometry union (sphere/plane/capsule/box/convex/
    /// triangle-mesh/height-field).
    /// <para>
    /// Phase 1 only consumes <see cref="TriangleMesh"/> and <see cref="HeightField"/>
    /// (the two that cover ~99 % of map geometry on Arena maps) plus <see cref="Sphere"/>
    /// for the foliage / "see-through" heuristic. The other variants are kept in
    /// the enum so the geometry-dispatch switch can default safely.
    /// </para>
    /// </summary>
    internal enum PxGeometryType : int
    {
        Sphere        = 0,
        Plane         = 1,
        Capsule       = 2,
        Box           = 3,
        ConvexMesh    = 4,
        TriangleMesh  = 5,
        HeightField   = 6,

        /// <summary>Sentinel â€” anything outside [0..6] is treated as Invalid.</summary>
        Invalid       = -1,
    }

    /// <summary>
    /// Per-shape behaviour flag byte stored at <c>NpShape.shapeFlags</c>. PhysX
    /// uses these to decide whether a shape participates in simulation, in
    /// scene queries, or both â€” plus whether it's a trigger (raycasts pass
    /// through, only overlap callbacks fire).
    /// </summary>
    [Flags]
    internal enum PxShapeFlag : byte
    {
        None             = 0,
        /// <summary>Shape participates in the dynamics simulation (real wall, collider for movement).</summary>
        SimulationShape  = 1 << 0, // 0x01
        /// <summary>Shape is visible to scene-query raycasts/overlaps. Required for our raycaster to hit it.</summary>
        SceneQueryShape  = 1 << 1, // 0x02
        /// <summary>Shape is a trigger volume â€” overlap events only, no physical blocking. We must SKIP these for visibility.</summary>
        TriggerShape     = 1 << 2, // 0x04
        /// <summary>Debug visualisation flag â€” irrelevant for us.</summary>
        Visualization    = 1 << 3, // 0x08
    }

    /// <summary>
    /// Per-triangle-mesh flag byte stored at <c>NpTriangleMesh.flags</c>. We only
    /// need to know whether the triangle index buffer is 16-bit or 32-bit; the
    /// remaining bits are not used by the radar.
    /// </summary>
    [Flags]
    internal enum PxTriangleMeshFlag : byte
    {
        None             = 0,
        /// <summary>Triangle indices are <c>ushort</c> (3 per triangle). When clear, indices are <c>uint</c>.</summary>
        Has16BitIndices  = 1 << 1,
        /// <summary>Mesh ships precomputed adjacency information â€” not consumed by us.</summary>
        HasAdjacency     = 1 << 2,
    }

    /// <summary>
    /// Helpers for classifying / sanity-checking PhysX enum values read from
    /// DMA. The radar's hot paths read raw values then ask these predicates â€”
    /// keeps boundary validation in one place.
    /// </summary>
    internal static class PhysXEnumExtensions
    {
        /// <summary>True for the geometry kinds Phase 1 actively raycasts against.</summary>
        public static bool IsRaycastable(this PxGeometryType t) => t switch
        {
            PxGeometryType.Sphere       => true,
            PxGeometryType.TriangleMesh => true,
            PxGeometryType.HeightField  => true,
            PxGeometryType.ConvexMesh   => true,
            _                           => false,
        };

        /// <summary>True for either flavor of PhysX's triangle-mesh wire tag.</summary>
        public static bool IsTriangleMesh(this PxConcreteType t) =>
            t is PxConcreteType.TriangleMeshBvh33 or PxConcreteType.TriangleMeshBvh34;

        /// <summary>True for the two rigid-actor kinds we walk in the scene.</summary>
        public static bool IsRigidActor(this PxConcreteType t) =>
            t is PxConcreteType.RigidStatic or PxConcreteType.RigidDynamic;
    }
}
