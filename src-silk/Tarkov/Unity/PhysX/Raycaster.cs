using System.Runtime.CompilerServices;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Pure-CPU geometry library. Given a <see cref="SceneSnapshot"/> and a ray,
    /// answers "is the ray blocked before reaching the target?"
    /// <para>
    /// No I/O, no allocations on the hot path, no shared mutable state. Every
    /// public function is safe to call concurrently from multiple threads on
    /// the same snapshot.
    /// </para>
    /// <para>
    /// Phase 1 strategy: linear scan over actors, gated by a fast slab-test
    /// against each actor's pre-computed world AABB. Per-mesh and per-scene
    /// BVHs are a Phase 2 optimization â€” added without changing this file's
    /// public API (the geometry functions just gain accelerated variants).
    /// </para>
    /// </summary>
    internal static class Raycaster
    {
        // â”€â”€ See-through layer filter â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Unity layers (one-hot) whose actors are treated as see-through â€”
        /// the raycaster skips them entirely. <see cref="CachedActor.ShapeLayerMask"/>
        /// is itself one-hot from <c>PxFilterData.word1</c>, so a per-actor
        /// AND against this mask answers "is this shape on a see-through
        /// layer?" in one instruction.
        /// <para>
        /// Default value: <c>1 &lt;&lt; 16</c>. Empirically validated on
        /// Arena_Bay5 and Arena_Prison via the per-blocker DIAG line â€”
        /// layer 16 shapes are all 0.5 Ã— 0.5 Ã— 0.5 capsules at moving
        /// positions, i.e. player character colliders. Leaving them in the
        /// raycaster causes enemy A's collider to "block" the sightline to
        /// enemy B even when nothing else is between them.
        /// </para>
        /// <para>
        /// Tunable at runtime â€” the value is read once per <see cref="AnyHit"/>
        /// call. To add more see-through layers (e.g. foliage / glass / decals
        /// once we identify them by index from live DIAGs), OR additional bits
        /// into this property.
        /// </para>
        /// </summary>
        public static uint SeeThroughLayerMask { get; set; } = 1u << 16;

        // â”€â”€ Public entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Returns true if the ray from <paramref name="origin"/> in direction
        /// <paramref name="direction"/> hits ANY actor in <paramref name="snapshot"/>
        /// before <paramref name="maxDistance"/>. Returns false when the path is
        /// clear (i.e. the target point at <c>origin + direction * maxDistance</c>
        /// is visible).
        /// <para>
        /// <paramref name="direction"/> must be unit length; the caller normalizes
        /// once and reuses the normalized vector across multiple checks.
        /// </para>
        /// </summary>
        public static bool AnyHit(
            SceneSnapshot snapshot,
            Vector3 origin,
            Vector3 direction,
            float maxDistance)
            => AnyHitWithActor(snapshot, origin, direction, maxDistance, out _);

        /// <summary>
        /// Same as <see cref="AnyHit"/> but writes the index of the first actor
        /// whose geometry blocked the ray to <paramref name="actorIndex"/>.
        /// Used by the visibility-debug overlay to surface "which actor blocked
        /// this player". Returns -1 in <paramref name="actorIndex"/> on miss.
        /// </summary>
        public static bool AnyHitWithActor(
            SceneSnapshot snapshot,
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            out int actorIndex)
        {
            actorIndex = -1;
            if (snapshot is null || snapshot.IsEmpty || maxDistance <= 0f)
                return false;

            // Precompute inverse direction for the slab test. Components that are
            // exactly zero get +Inf so the slab math still produces +Inf/-Inf at
            // the right times and the min/max calls handle them correctly.
            var invDir = new Vector3(
                direction.X != 0f ? 1f / direction.X : float.PositiveInfinity,
                direction.Y != 0f ? 1f / direction.Y : float.PositiveInfinity,
                direction.Z != 0f ? 1f / direction.Z : float.PositiveInfinity);

            var actors = snapshot.Actors;
            for (int i = 0; i < actors.Length; i++)
            {
                var actor = actors[i];

                // Gate 0: see-through classifier. A single pre-computed bool â€”
                // <see cref="VisibilityClassifier"/> combines the layer-mask
                // rule (see <see cref="SeeThroughLayerMask"/>) with per-actor
                // name patterns at build / load time. One bool read is the
                // cheapest possible reject; no per-ray string work.
                if (actor.IsSeeThrough)
                    continue;

                // Gate 1: world AABB test. A tight AABB is the cheapest possible
                // rejector â€” one fmadd per axis. Anything that fails this can't
                // possibly hit the actor's geometry.
                if (!RayAabb(origin, invDir, actor.WorldAabbMin, actor.WorldAabbMax, maxDistance))
                    continue;

                // Gate 2: geometry-specific test. Each branch returns true on hit
                // within maxDistance, false otherwise.
                if (HitsActor(snapshot, actor, origin, direction, maxDistance))
                {
                    actorIndex = i;
                    return true;
                }
            }
            return false;
        }

        // â”€â”€ Per-actor dispatch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static bool HitsActor(
            SceneSnapshot snapshot,
            CachedActor actor,
            Vector3 origin,
            Vector3 direction,
            float maxDistance)
        {
            switch (actor.GeometryType)
            {
                case PxGeometryType.TriangleMesh:
                    return actor.MeshIndex >= 0
                        && HitsTriMesh(snapshot.Meshes[actor.MeshIndex], actor.WorldTransform,
                                       origin, direction, maxDistance);

                case PxGeometryType.ConvexMesh:
                    return actor.ConvexMeshIndex >= 0
                        && HitsConvexMesh(snapshot.ConvexMeshes[actor.ConvexMeshIndex], actor.WorldTransform,
                                          origin, direction, maxDistance);

                case PxGeometryType.HeightField:
                    return actor.HeightFieldIndex >= 0
                        && HitsHeightField(snapshot.HeightFields[actor.HeightFieldIndex],
                                           actor.WorldTransform, origin, direction, maxDistance);

                case PxGeometryType.Sphere:
                    return RaySphere(origin, direction,
                                     actor.WorldTransform.Position, actor.PrimitiveSize.X,
                                     maxDistance);

                case PxGeometryType.Capsule:
                    return HitsCapsule(actor, origin, direction, maxDistance);

                case PxGeometryType.Box:
                    return HitsBox(actor, origin, direction, maxDistance);

                // Plane / ConvexMesh / Invalid â€” Phase 1 doesn't ray-test these.
                // The world AABB gate already cleared them, so reporting no-hit
                // is correct for Phase 1's "visible through this collider" check.
                default:
                    return false;
            }
        }

        // â”€â”€ AABB slab test â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Standard ray-AABB slab test. <paramref name="invDir"/> must be the
        /// componentwise reciprocal of the ray direction (or +Inf for zero
        /// components). Returns true if the ray enters the AABB at any t in
        /// <c>[0, maxT]</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RayAabb(
            Vector3 origin, Vector3 invDir,
            Vector3 boxMin, Vector3 boxMax,
            float maxT)
        {
            float tx1 = (boxMin.X - origin.X) * invDir.X;
            float tx2 = (boxMax.X - origin.X) * invDir.X;
            float tmin = MathF.Min(tx1, tx2);
            float tmax = MathF.Max(tx1, tx2);

            float ty1 = (boxMin.Y - origin.Y) * invDir.Y;
            float ty2 = (boxMax.Y - origin.Y) * invDir.Y;
            tmin = MathF.Max(tmin, MathF.Min(ty1, ty2));
            tmax = MathF.Min(tmax, MathF.Max(ty1, ty2));

            float tz1 = (boxMin.Z - origin.Z) * invDir.Z;
            float tz2 = (boxMax.Z - origin.Z) * invDir.Z;
            tmin = MathF.Max(tmin, MathF.Min(tz1, tz2));
            tmax = MathF.Min(tmax, MathF.Max(tz1, tz2));

            // Hit when the slabs overlap AND the overlap reaches into [0, maxT].
            return tmax >= MathF.Max(tmin, 0f) && tmin <= maxT;
        }

        // â”€â”€ Sphere â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Ray-sphere intersection â€” visibility semantics. Returns true if
        /// the ray ENTERS the sphere from outside within <c>[0, maxT]</c>.
        /// If the ray ORIGIN is already inside the sphere, returns false â€”
        /// the sphere is then an enclosing volume, not a wall between us and
        /// anything outside it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RaySphere(
            Vector3 origin, Vector3 direction,
            Vector3 center, float radius,
            float maxT)
        {
            if (radius <= 0f) return false;
            var oc = origin - center;
            float c = Vector3.Dot(oc, oc) - radius * radius;
            // Origin inside the sphere â€” visibility passes through; the
            // sphere encloses us rather than blocking a line of sight.
            if (c <= 0f) return false;
            float b = Vector3.Dot(oc, direction);
            // Ray pointing away from sphere â‡’ no hit.
            if (b > 0f) return false;
            float disc = b * b - c;
            if (disc < 0f) return false;
            float t = -b - MathF.Sqrt(disc);
            return t >= 0f && t <= maxT;
        }

        // â”€â”€ Triangle (MÃ¶llerâ€“Trumbore) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Ray-triangle intersection. Returns true and writes the hit distance
        /// to <paramref name="t"/> when the ray crosses the triangle at
        /// <c>t âˆˆ [0, maxT]</c>. Triangles are double-sided (no back-face culling)
        /// because PhysX triangle meshes can be hit from either side depending
        /// on the surface they represent.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RayTriangle(
            Vector3 origin, Vector3 direction,
            Vector3 v0, Vector3 v1, Vector3 v2,
            float maxT, out float t)
        {
            const float EPS = 1e-7f;
            t = 0f;

            var e1 = v1 - v0;
            var e2 = v2 - v0;
            var p  = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            // Near-parallel ray â‡’ skip (we never hit the plane in any useful sense).
            if (det > -EPS && det < EPS) return false;
            float invDet = 1f / det;

            var s = origin - v0;
            float u = Vector3.Dot(s, p) * invDet;
            if (u < 0f || u > 1f) return false;

            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(direction, q) * invDet;
            if (v < 0f || u + v > 1f) return false;

            float tt = Vector3.Dot(e2, q) * invDet;
            if (tt < 0f || tt > maxT) return false;

            t = tt;
            return true;
        }

        // â”€â”€ Triangle mesh â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Triangle-mesh hit test. Transforms the ray to mesh-local space (single
        /// inverse-rotate + subtract), then walks the index buffer linearly.
        /// </summary>
        /// <remarks>
        /// Phase 1 is linear. Big meshes (1000+ triangles) get expensive; the
        /// per-frame budget is preserved by the actor-level world-AABB cull
        /// being aggressive enough to skip most meshes entirely.
        /// </remarks>
        private static bool HitsTriMesh(
            CachedTriMesh mesh, PxTransform worldXform,
            Vector3 origin, Vector3 direction, float maxT)
        {
            // Bring the ray into the mesh's local frame. Rotation only â€” the
            // worldXform.InverseTransformDirection skips translation as required.
            var localOrigin = worldXform.InverseTransformPoint(origin);
            var localDir    = worldXform.InverseTransformDirection(direction);

            // Local AABB pre-test using the just-transformed ray. Avoids the
            // per-triangle work when the mesh isn't on the ray's path even
            // though the (looser) world AABB said it might be.
            var localInvDir = new Vector3(
                localDir.X != 0f ? 1f / localDir.X : float.PositiveInfinity,
                localDir.Y != 0f ? 1f / localDir.Y : float.PositiveInfinity,
                localDir.Z != 0f ? 1f / localDir.Z : float.PositiveInfinity);
            if (!RayAabb(localOrigin, localInvDir, mesh.LocalAabbMin, mesh.LocalAabbMax, maxT))
                return false;

            var verts = mesh.Vertices;
            var inds  = mesh.Indices;
            int triCount = mesh.TriangleCount;
            for (int t = 0; t < triCount; t++)
            {
                int base3 = t * 3;
                var v0 = verts[inds[base3 + 0]];
                var v1 = verts[inds[base3 + 1]];
                var v2 = verts[inds[base3 + 2]];
                if (RayTriangle(localOrigin, localDir, v0, v1, v2, maxT, out _))
                    return true;
            }
            return false;
        }

        // â”€â”€ Convex mesh (polytope slab method) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Ray vs convex polyhedron. Transforms the ray into the mesh's local
        /// frame once, then clips it against each polygon plane in turn â€”
        /// classic slab method generalised from AABB to arbitrary convex
        /// polytopes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each plane is stored as a <see cref="Vector4"/> packed as
        /// <c>(n.x, n.y, n.z, d)</c> with the plane equation <c>nÂ·p + d = 0</c>
        /// (PhysX <c>PxPlane</c> convention; outward-facing normal, polyhedron
        /// interior is <c>nÂ·p + d &lt; 0</c>). The ingest path validates
        /// that <c>|n| â‰ˆ 1</c> for every polygon before we get here.
        /// </para>
        /// <para>
        /// Origin-inside-polyhedron is treated as "no block" â€” same visibility
        /// semantics as the other geometry tests. We detect it by tEnter
        /// staying â‰¤ 0 after all planes have been processed: the ray was
        /// already inside every half-space at t=0, so it never entered from
        /// outside.
        /// </para>
        /// </remarks>
        private static bool HitsConvexMesh(
            CachedConvexMesh mesh, PxTransform worldXform,
            Vector3 origin, Vector3 direction, float maxT)
        {
            var lo = worldXform.InverseTransformPoint(origin);
            var ld = worldXform.InverseTransformDirection(direction);

            // Local AABB pre-test â€” same cost-saving as the TriMesh path.
            var localInvDir = new Vector3(
                ld.X != 0f ? 1f / ld.X : float.PositiveInfinity,
                ld.Y != 0f ? 1f / ld.Y : float.PositiveInfinity,
                ld.Z != 0f ? 1f / ld.Z : float.PositiveInfinity);
            if (!RayAabb(lo, localInvDir, mesh.LocalAabbMin, mesh.LocalAabbMax, maxT))
                return false;

            const float EPS = 1e-7f;
            float tEnter = float.NegativeInfinity;
            float tExit  = float.PositiveInfinity;

            var planes = mesh.PolygonPlanes;
            int n = mesh.PolygonCount;
            for (int i = 0; i < n; i++)
            {
                var pl = planes[i];
                var nrm = new Vector3(pl.X, pl.Y, pl.Z);
                float dPlane = pl.W;
                float denom = Vector3.Dot(nrm, ld);
                // num is rearranged from "nÂ·(o + tÂ·d) + dPlane = 0":
                //   tÂ·(nÂ·d) = -(dPlane + nÂ·o)
                float num = -(dPlane + Vector3.Dot(nrm, lo));

                if (MathF.Abs(denom) < EPS)
                {
                    // Ray parallel to this plane. If origin is on the outside
                    // half-space (num < 0), the ray never enters the
                    // polyhedron; otherwise this plane imposes no constraint.
                    if (num < 0f) return false;
                    continue;
                }

                float t = num / denom;
                if (denom < 0f)
                {
                    // Entry plane (ray moving into the half-space) â€” raises tEnter.
                    if (t > tEnter) tEnter = t;
                }
                else
                {
                    // Exit plane (ray moving out of the half-space) â€” lowers tExit.
                    if (t < tExit) tExit = t;
                }
                if (tEnter > tExit) return false;
            }

            // Origin inside the polyhedron â€” visibility passes through.
            if (tEnter <= 0f) return false;
            // Ray enters polyhedron within the segment of interest.
            return tEnter <= maxT && tEnter <= tExit;
        }

        // â”€â”€ Height field â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Height-field hit test. Phase 1: a simple bounded walk over the row
        /// span the ray crosses in local space; for each row, build the two
        /// triangles for every column the ray can possibly hit and run the
        /// triangle test. Coarse but correct; replaced by a 2D-DDA in Phase 2.
        /// </summary>
        private static bool HitsHeightField(
            CachedHeightField hf, PxTransform worldXform,
            Vector3 origin, Vector3 direction, float maxT)
        {
            // Transform ray into heightfield-local space.
            var lo = worldXform.InverseTransformPoint(origin);
            var ld = worldXform.InverseTransformDirection(direction);

            // Local AABB test first.
            var liv = new Vector3(
                ld.X != 0f ? 1f / ld.X : float.PositiveInfinity,
                ld.Y != 0f ? 1f / ld.Y : float.PositiveInfinity,
                ld.Z != 0f ? 1f / ld.Z : float.PositiveInfinity);
            if (!RayAabb(lo, liv, hf.LocalAabbMin, hf.LocalAabbMax, maxT))
                return false;

            // Heightfield local-space mapping:
            //   localX = col * ColumnScale
            //   localZ = row * RowScale
            //   localY = sample * HeightScale
            float colScale = hf.ColumnScale;
            float rowScale = hf.RowScale;
            float hScale   = hf.HeightScale;
            if (colScale == 0f || rowScale == 0f) return false;
            float invCol = 1f / colScale;
            float invRow = 1f / rowScale;

            int colCount = hf.Columns;
            int rowCount = hf.Rows;

            // Iterate every cell whose footprint intersects the ray's local AABB.
            // Phase 1 keeps this simple; Phase 2 replaces with a 2D-DDA walk so
            // a long grazing ray doesn't touch every cell.
            int rMin = Math.Clamp((int)MathF.Floor(hf.LocalAabbMin.Z * invRow), 0, rowCount - 1);
            int rMax = Math.Clamp((int)MathF.Ceiling(hf.LocalAabbMax.Z * invRow), 0, rowCount - 1);
            int cMin = Math.Clamp((int)MathF.Floor(hf.LocalAabbMin.X * invCol), 0, colCount - 1);
            int cMax = Math.Clamp((int)MathF.Ceiling(hf.LocalAabbMax.X * invCol), 0, colCount - 1);

            for (int r = rMin; r < rMax; r++)
            {
                for (int c = cMin; c < cMax; c++)
                {
                    // Quad corners in local space, two triangles per cell:
                    //   v00 â”€â”€â”€ v01
                    //    â”‚ \  T1â”‚
                    //    â”‚T0 \  â”‚
                    //   v10 â”€â”€â”€ v11
                    float x0 = c * colScale, x1 = (c + 1) * colScale;
                    float z0 = r * rowScale, z1 = (r + 1) * rowScale;
                    float y00 = hf.Sample(r,     c    ) * hScale;
                    float y01 = hf.Sample(r,     c + 1) * hScale;
                    float y10 = hf.Sample(r + 1, c    ) * hScale;
                    float y11 = hf.Sample(r + 1, c + 1) * hScale;

                    var v00 = new Vector3(x0, y00, z0);
                    var v01 = new Vector3(x1, y01, z0);
                    var v10 = new Vector3(x0, y10, z1);
                    var v11 = new Vector3(x1, y11, z1);

                    if (RayTriangle(lo, ld, v00, v10, v11, maxT, out _) ||
                        RayTriangle(lo, ld, v00, v11, v01, maxT, out _))
                        return true;
                }
            }
            return false;
        }

        // â”€â”€ Capsule â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Capsule hit test. PhysX capsules are stored as a half-height along
        /// the local X axis with radius. We bring the ray into local space and
        /// solve the cylinder + 2 endcap-sphere intersections.
        /// </summary>
        private static bool HitsCapsule(
            CachedActor actor, Vector3 origin, Vector3 direction, float maxT)
        {
            float radius     = actor.PrimitiveSize.X;
            float halfHeight = actor.PrimitiveSize.Y;
            if (radius <= 0f || halfHeight < 0f) return false;

            var lo = actor.WorldTransform.InverseTransformPoint(origin);
            var ld = actor.WorldTransform.InverseTransformDirection(direction);

            // Endcap spheres at (Â±halfHeight, 0, 0) â€” share the same radius.
            if (RaySphere(lo, ld, new Vector3( halfHeight, 0f, 0f), radius, maxT)) return true;
            if (RaySphere(lo, ld, new Vector3(-halfHeight, 0f, 0f), radius, maxT)) return true;

            // Infinite cylinder around local X. Project the ray onto the YZ
            // plane and solve a 2D ray-circle problem; then clamp X to the
            // capsule's body span.
            float dy = ld.Y, dz = ld.Z;
            float oy = lo.Y, oz = lo.Z;
            float a = dy * dy + dz * dz;
            if (a < 1e-8f) return false; // ray parallel to cylinder axis â€” endcaps handled this
            float b = oy * dy + oz * dz;
            float c = oy * oy + oz * oz - radius * radius;
            // Origin inside the cylinder body (within radius on YZ plane)
            // AND between the body's X-span. Capsule encloses the eye â€”
            // doesn't block. The endcap-inside case is handled by the
            // RaySphere short-circuit above.
            if (c <= 0f && lo.X >= -halfHeight && lo.X <= halfHeight)
                return false;
            float disc = b * b - a * c;
            if (disc < 0f) return false;
            float sqrtDisc = MathF.Sqrt(disc);
            float t0 = (-b - sqrtDisc) / a;
            float t1 = (-b + sqrtDisc) / a;
            // Try the near-side intersection first.
            for (int i = 0; i < 2; i++)
            {
                float t = i == 0 ? t0 : t1;
                if (t < 0f || t > maxT) continue;
                float xHit = lo.X + ld.X * t;
                if (xHit >= -halfHeight && xHit <= halfHeight)
                    return true;
            }
            return false;
        }

        // â”€â”€ Box (OBB) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Oriented-box hit test â€” visibility semantics. Transforms the ray
        /// to box-local space, then reuses the slab test against the
        /// symmetric local AABB. If the origin is already inside the box,
        /// returns false â€” the box is enclosing the eye, not blocking the
        /// line of sight to anything outside it. This fix matters whenever
        /// the local player stands inside a debug / spawn-protection /
        /// detection volume the game models as a Box collider.
        /// </summary>
        private static bool HitsBox(
            CachedActor actor, Vector3 origin, Vector3 direction, float maxT)
        {
            var he = actor.PrimitiveSize;
            if (he.X <= 0f || he.Y <= 0f || he.Z <= 0f) return false;

            var lo = actor.WorldTransform.InverseTransformPoint(origin);
            // Origin inside the OBB â‡’ box encloses the eye, doesn't block.
            if (MathF.Abs(lo.X) < he.X
                && MathF.Abs(lo.Y) < he.Y
                && MathF.Abs(lo.Z) < he.Z)
                return false;

            var ld = actor.WorldTransform.InverseTransformDirection(direction);
            var liv = new Vector3(
                ld.X != 0f ? 1f / ld.X : float.PositiveInfinity,
                ld.Y != 0f ? 1f / ld.Y : float.PositiveInfinity,
                ld.Z != 0f ? 1f / ld.Z : float.PositiveInfinity);
            return RayAabb(lo, liv, -he, he, maxT);
        }
    }
}
