namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Field offsets inside PhysX 4.1 / Unity 6 objects.
    /// <para>
    /// All values here are <b>struct-relative</b>, not module-relative. They are
    /// stable as long as the PhysX SDK version stays at 4.1.x â€” Unity engine
    /// patches that recompile <c>UnityPlayer.dll</c> shift code addresses
    /// (handled by the <see cref="PhysXProbe"/> sig-scan) but the in-struct
    /// layout is owned by NVIDIA's SDK and does not move.
    /// </para>
    /// </summary>
    internal static class PhysXOffsets
    {
        // â”€â”€ SDK entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // RVA inside UnityPlayer.dll where the NpPhysics singleton pointer is
        // stored. Found by PhysXProbe's sig-scan; re-verified every attach.
        // Different across game builds even at the same Unity version because
        // the .data section re-links; SceneCache falls back to PhysXProbe's
        // sig-scan when the cached value doesn't resolve, so a wrong default
        // here just costs one sig-scan on first attach.
        //   Arena (0.5.0.3.45073, Unity 6000.3.6f1) : 0x01EE1028 / 0x01F41DE8 alias
        //   EFT main game (Unity 6, Nov 2026 build) : 0x0210C5E8 (resolved live)
        public const uint PhysXSdkRva = 0x0210C5E8; // EFT main (Unity 6)

        // â”€â”€ NpPhysics layout â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // The SDK singleton owns a scene array. We only need the first two
        // fields to enumerate scenes.
        public const uint NpPhysics_SceneArrayData = 0x08; // âœ… ptr to NpScene*[N]
        public const uint NpPhysics_SceneArraySize = 0x10; // âœ… uint32 â€” confirmed 2..3 on Arena

        // â”€â”€ NpScene layout â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Each NpScene owns a rigid-actor array. The array contains both
        // RigidStatic and RigidDynamic; the actor kind is discriminated by the
        // PxBase concrete type at +0x8 of the actor.
        public const uint NpScene_RigidActorsData = 0x23C8; // âœ… ptr to PxActor*[N]
        public const uint NpScene_RigidActorsSize = 0x23D0; // âœ… uint32 â€” confirmed = 7842..10732 on Arena

        // â”€â”€ PxBase header (every PhysX object starts with this) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // First 16 bytes of any PxRigidActor / NpShape / mesh / heightfield.
        public const uint PxBase_ConcreteType = 0x08; // âœ… PxConcreteType (u16)

        // â”€â”€ PxRigidActor (NpRigidStatic / NpRigidDynamic share this prefix) â”€â”€
        // Some EFT/Unity builds split shape-manager location across two offsets;
        // SceneCache walks all three and uses whichever yields a sane shape count.
        public const uint PxRigidActor_ShapeManager        = 0x28; // âœ… NpShapeManager â€” arena

        // â”€â”€ NpShapeManager (a ptr_table_t: small inline + heap overflow) â”€â”€â”€â”€
        // count == 0 â‡’ no shapes
        // count == 1 â‡’ "ShapesSingle" holds the shape pointer directly
        // count >  1 â‡’ "ShapesSingle" is a pointer to a ptr-array of length count
        public const uint NpShapeManager_ShapesSingle = 0x00; // âœ… NpShape* or NpShape*[]
        public const uint NpShapeManager_ShapesCount  = 0x08; // âœ… uint16

        // â”€â”€ NpShape (Sc::ShapeCore embedded at +0x50) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Layout fully verified by IDA disassembly of:
        //   â€¢ NpShape::setSimulationFilterData   â€” writes at this+0x60
        //   â€¢ NpShape::setQueryFilterData        â€” writes at this+0x50
        //   â€¢ NpShape::setFlag                   â€” direct path reads at this+0x90
        //   â€¢ Sc::ShapeCore::ShapeCore           â€” full canonical field layout
        // The NpShape header occupies +0x00..+0x4F (vtable / actor ptr /
        // buffered-write bookkeeping). The embedded Sc::ShapeCore starts at
        // +0x50; field reads compose NpShape_PxShapeCoreOffset + PxShapeCore_*.
        // ShapeFlags is the one exception â€” read as an absolute offset because
        // the trigger / scene-query flag check runs in the hot per-actor loop.
        public const uint NpShape_PxShapeCoreOffset = 0x50; // âœ… inline Sc::ShapeCore
        public const uint NpShape_ShapeFlags        = 0x90; // âœ… = PxShapeCoreOffset + 0x40

        // â”€â”€ Sc::ShapeCore (a.k.a. PxShapeCore) â€” offsets relative to its start â”€
        // From the Sc::ShapeCore::ShapeCore constructor IDA decompile:
        //   +0x00..+0x0F : queryFilterData       (4 Ã— u32 â€” initialised by setQueryFilterData)
        //   +0x10..+0x1F : simulationFilterData  (4 Ã— u32 â€” initialised by setSimulationFilterData)
        //   +0x20..+0x3B : transform             (PxTransform = quat(4f) + Vec3(3f) = 28 bytes;
        //                                         constructor writes 1.0f at +0x2C giving qw=1.0)
        //   +0x3C..+0x3F : contactOffset         (float â€” initialised to scale Ã— 0.02)
        //   +0x40        : shapeFlags            (PxShapeFlag byte â€” eSIM/eSQ/eTRIGGER/eVis)
        //   +0x41        : mOwnsMaterialIndices  (byte)
        //   +0x42..+0x43 : materialIndex         (u16)
        //   +0x48..+0x87 : PxGeometryUnion       (64 bytes â€” type tag (i32) first, then data)
        // We only read the three fields the visibility filter needs at runtime;
        // the rest of the canonical layout is documented in the comment above
        // for future work (Phase 2 / 3 readers).
        public const uint PxShapeCore_QueryFilterData = 0x00; // âœ…
        public const uint PxShapeCore_LocalPose       = 0x20; // âœ… PxTransform (28 bytes â€” q,p)
        public const uint PxShapeCore_Geometry        = 0x48; // âœ… PxGeometryUnion (64 bytes)

        // â”€â”€ NpRigidDynamic-specific: cached buffered body-to-world transform â”€
        // The "live" pose of a dynamic actor is its bufferedBody2World, written
        // by PhysX once per simulation step. Phase 2 work will read this for
        // moving colliders (doors, vehicles); Phase 1 only handles statics.
        public const uint NpRigidDynamic_BufferedBody2World = 0x140; // ðŸŸ¡ PxTransform

        // â”€â”€ NpRigidStatic-specific â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // âœ… Verified by live hex dump: RigidStatic actor is 0xB0 bytes total,
        // three back-to-back actors all carry their world transform at +0x90
        // within the struct (quaternion magnitude check passes, position matches
        // a sane Arena world coordinate).
        public const uint NpRigidStatic_BodyToWorld = 0x90; // âœ… PxTransform â€” single static pose

        // â”€â”€ PxTriangleMesh (the cooked mesh referenced by triangle-mesh geom) â”€
        // The mesh holds vertices (always Vec3 float) and triangle indices
        // (either u16 or u32 depending on PxTriangleMeshFlag.Has16BitIndices).
        public const uint TriangleMesh_NbVertices     = 0x20; // âœ… u32
        public const uint TriangleMesh_NbTriangles    = 0x24; // âœ… u32
        public const uint TriangleMesh_Vertices       = 0x28; // âœ… Vec3* â€” packed contiguous
        public const uint TriangleMesh_Triangles      = 0x30; // âœ… u16*/u32* â€” 3 per triangle
        public const uint TriangleMesh_LocalBoundsMin = 0x38; // âœ… Vec3 â€” for fast AABB cull
        public const uint TriangleMesh_LocalBoundsMax = 0x44; // âœ… Vec3
        public const uint TriangleMesh_Flags          = 0x5C; // âœ… u8 (PxTriangleMeshFlag)

        // â”€â”€ PxHeightField (cooked heightmap) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Samples are int16 heights on a 2D grid. World space:
        //   localX = column * columnScale
        //   localZ = row    * rowScale
        //   localY = sample * heightScale
        // Arena maps are all triangle-mesh; these offsets are exercised only
        // when the heightfield path is touched (not yet live-tested in Arena).
        public const uint HeightField_Rows    = 0x38; // ðŸŸ¡ u32
        public const uint HeightField_Columns = 0x3C; // ðŸŸ¡ u32
        public const uint HeightField_Samples = 0x50; // ðŸŸ¡ PxHfSample* (i16 + 2 bytes flags each)

        // â”€â”€ PxGeometry header (first int of any geometry struct) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Each geometry kind below starts with this 4-byte type tag.
        public const uint PxGeometry_TypeTag = 0x00; // âœ… PxGeometryType (i32)

        // â”€â”€ PxSphereGeometry / PxCapsuleGeometry / PxBoxGeometry â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        public const uint Sphere_Radius      = 0x04; // âœ… float
        public const uint Capsule_Radius     = 0x04; // âœ… float
        public const uint Capsule_HalfHeight = 0x08; // âœ… float
        public const uint Box_HalfExtents    = 0x04; // âœ… Vec3

        // â”€â”€ PxTriangleMeshGeometry / PxHeightFieldGeometry / PxConvexMeshGeometry â”€â”€
        // We only need the pointer to the cooked mesh + the per-axis scales.
        // PxMeshScale (Vec3 scale + Quat rot, 28 bytes) at TriMeshGeom +0x04
        // is not consumed by the radar â€” Arena maps don't ship per-instance
        // mesh scales â€” so reading it would just complicate the ingest.
        public const uint TriangleMeshGeom_MeshPtr       = 0x28; // âœ… PxTriangleMesh*
        // PxConvexMeshGeometry's field order differs from PxTriangleMeshGeometry:
        // in ConvexMesh the pointer comes BEFORE the flags (so it sits right
        // after the 28-byte PxMeshScale + 4-byte type prefix = +0x20). In
        // TriangleMesh the flags come first and push the pointer to +0x28.
        // Confirmed from live ConvexGeomUnion dumps on arena 0.5.0.3.45073 â€”
        // all three sampled actors had a clean 8-byte-aligned heap pointer
        // at +0x20 and the (wrong) +0x28 read gave a low-bit-set Frankenstein
        // value (low 32 bits of flags joined with high 32 bits of the next field).
        public const uint ConvexMeshGeom_MeshPtr         = 0x20; // âœ… PxConvexMesh* (verified live)
        public const uint HeightFieldGeom_HeightFieldPtr = 0x08; // ðŸŸ¡ PxHeightField*
        public const uint HeightFieldGeom_HeightScale    = 0x10; // ðŸŸ¡ float

        // â”€â”€ PxConvexMesh (the cooked convex hull referenced by ConvexMesh geom) â”€
        // âš  These offsets are BEST-GUESS by structural analogy with our
        // verified PxTriangleMesh layout (data starts at +0x20 inside the SDK
        // object). They have NOT been verified against arena's binary yet â€”
        // SceneCache logs a hex dump of the first ConvexMesh actor whose
        // counts fail validation so we can correct these from concrete data
        // on the next pass.
        //
        // The underlying layout in PhysX 4.1.2 is Gu::ConvexHullData:
        //   +0x00  PxBounds3 mAABB         (24 bytes: Vec3 min + Vec3 max)
        //   +0x18  PxVec3    mCenterOfMass (12 bytes)
        //   +0x24  pad to 8-byte align
        //   +0x28  HullPolygonData* mPolygons
        //   +0x30  PxU8* mBigConvexRawData (only for hulls with > 64 verts)
        //   +0x38  PxU8* mVertexData8     (index buffer)
        //   +0x40  PxU8* mFacesByEdges8
        //   +0x48  PxU8* mFacesByVertices8
        //   +0x50  PxVec3* mHullVertices  (vertex array)
        //   +0x58  PxU16 mNbEdges
        //   +0x5A  PxU8  mNbHullVertices
        //   +0x5B  PxU8  mNbPolygons
        // Offsets below assume the same +0x20 prefix as PxTriangleMesh.
        public const uint ConvexMesh_AabbMin       = 0x20; // ðŸŸ¡ Vec3
        public const uint ConvexMesh_AabbMax       = 0x2C; // ðŸŸ¡ Vec3
        public const uint ConvexMesh_Polygons      = 0x48; // ðŸŸ¡ HullPolygonData*
        public const uint ConvexMesh_Vertices      = 0x70; // ðŸŸ¡ PxVec3*
        public const uint ConvexMesh_NbVertices    = 0x7A; // ðŸŸ¡ u8
        public const uint ConvexMesh_NbPolygons    = 0x7B; // ðŸŸ¡ u8

        // HullPolygonData layout (per-polygon descriptor referenced by mPolygons):
        //   +0x00  PxPlane mPlane (Vec3 normal + float d) = 16 bytes
        //   +0x10  PxU16   mVRef8
        //   +0x12  PxU8    mNbVerts
        //   +0x13  PxU8    mMinIndex
        // Total: 20 bytes per polygon descriptor.
        public const uint HullPolygonData_Plane    = 0x00; // PxPlane = Vec4
        public const uint HullPolygonData_Stride   = 0x14; // ðŸŸ¡ 20 bytes per descriptor â€” verify alignment on real hull
        public const uint HeightFieldGeom_RowScale       = 0x14; // ðŸŸ¡ float
        public const uint HeightFieldGeom_ColumnScale    = 0x18; // ðŸŸ¡ float

        // â”€â”€ PxFilterData â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Four u32s. Unity packs the layer index into word1 as a one-hot bit
        // (`1 << layerIndex`); word0 is the collision-group mask. word2 / word3
        // are unused by the radar (the SHAPE-COMPARE diagnostic showed word3 is
        // uniformly 0x0000FFFF and word2 is zero).
        public const uint FilterData_Word0 = 0x00; // âœ… group bitmask
        public const uint FilterData_Word1 = 0x04; // âœ… Unity layer one-hot

        // Unity's std::string variant (libc++-style short string optimization):
        //   bytes 0..15  : either heap data pointer (long mode) OR start of in-place SSO buffer
        //   bytes 16..23 : size_t length (long mode only)
        //   byte  31     : SSO discriminator (the high byte of the struct)
        //     â€¢ value >= 0x40  â‡’  long mode: read data ptr at +0, length at +16
        //     â€¢ value <  0x40  â‡’  SSO mode:  length = 31 - flag, data starts at +0
        public const uint StdString_DataOrSsoBuf = 0x00;
        public const uint StdString_Length       = 0x10;
        public const uint StdString_SsoFlag      = 0x1F;

        // The layer offset isn't documented in Unity.cs. We probe several
        // candidates at build time (SceneCache layer-offset probe) and pick
        // the one whose value matches log2(ShapeLayerMask) most often.
        public const uint NpShape_NativeCollider     = 0x10;
        // GameObject-chain offsets are IL2CPP-layout, NOT PhysX-SDK, so they
        // differ between Unity versions. Values below are for EFT's Unity 2022
        // build and mirror the authoritative layout in Unity.cs (UnityOffsets):
        //   NativeCollider_GameObject = UnityOffsets.Comp_GameObject (0x58)
        //   NativeGameObject_NamePtr  = UnityOffsets.GO_Name        (0x88)
        // (Unity 6 / arena used 0x38 / 0x68 - that build's GameObject lacks the
        // ObjectClass field at 0x80, so its name sits 0x20 earlier.)
        public const uint NativeCollider_GameObject  = 0x58;
        public const uint NativeGameObject_NamePtr   = 0x88;
        // Layer offset confirmed by SceneCache probe (256/256 samples at +0x58
        // on arena 0.5.0.3.45073 / Unity 6000.3.6f1). The probe still runs on
        // every build so a future patch's shift is caught immediately â€” just
        // change this constant to whatever the new winner is.
        // NOTE (Unity 2022): the 0x58 below is the Unity 6 / arena value and is
        // NOT correct for this build (it now collides with GO_Components). It is
        // read only for the diagnostic layer cross-check - the classifier keys
        // off the shape's PhysX filter mask (word1), not this - so a wrong value
        // is harmless. The layer-offset probe prints the correct Unity 2022
        // offset on every build; commit that value to silence the cross-check.
        public const uint NativeGameObject_Layer     = 0x58;
    }
}
