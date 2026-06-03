using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Rigid transform = orientation quaternion + position vector. Field order
    /// matches PhysX's <c>PxTransform</c> wire layout: quaternion first
    /// (4 floats), then position (3 floats). Total: 28 bytes, naturally
    /// aligned for SIMD reads via <see cref="System.Numerics.Vector3"/> /
    /// <see cref="System.Numerics.Quaternion"/>.
    /// <para>
    /// Immutable value type â€” all "modifications" return a fresh struct. The
    /// raycaster builds local-space rays via <see cref="InverseTransformPoint"/>
    /// / <see cref="InverseTransformDirection"/> on the hot path; both are
    /// inlined and allocation-free.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal readonly struct PxTransform
    {
        // Order matches PhysX's wire layout (q then p) so we can read this as a
        // single 28-byte blob via Memory.ReadValue<PxTransform>(va).
        public readonly Quaternion Rotation;
        public readonly Vector3    Position;

        public PxTransform(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        public static readonly PxTransform Identity = new(Vector3.Zero, Quaternion.Identity);

        // â”€â”€ Validation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// True when every component is finite. We reject NaN/Inf at the cache
        /// boundary so the raycaster's hot path can skip those checks.
        /// </summary>
        public bool IsFinite =>
            float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z) &&
            float.IsFinite(Rotation.X) && float.IsFinite(Rotation.Y) &&
            float.IsFinite(Rotation.Z) && float.IsFinite(Rotation.W);

        /// <summary>
        /// True when the quaternion is close enough to unit length that
        /// rotation operations stay numerically stable. PhysX guarantees unit
        /// quaternions in practice but we never trust DMA-sourced floats blindly.
        /// </summary>
        public bool IsRotationUnit
        {
            get
            {
                float sq = Rotation.X * Rotation.X + Rotation.Y * Rotation.Y
                         + Rotation.Z * Rotation.Z + Rotation.W * Rotation.W;
                return sq > 0.97f && sq < 1.03f; // Â±1.5 % tolerance
            }
        }

        // â”€â”€ Single-point transforms â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Local â†’ world. Rotates <paramref name="local"/> by <see cref="Rotation"/>
        /// then translates by <see cref="Position"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 TransformPoint(Vector3 local)
            => Vector3.Transform(local, Rotation) + Position;

        /// <summary>
        /// World â†’ local. Inverse of <see cref="TransformPoint"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 InverseTransformPoint(Vector3 world)
            => Vector3.Transform(world - Position, Conjugate(Rotation));

        /// <summary>
        /// Local â†’ world direction (rotation only, no translation).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 TransformDirection(Vector3 localDir)
            => Vector3.Transform(localDir, Rotation);

        /// <summary>
        /// World â†’ local direction (inverse rotation only).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 InverseTransformDirection(Vector3 worldDir)
            => Vector3.Transform(worldDir, Conjugate(Rotation));

        // â”€â”€ Composition â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Composes <c>a</c> then <c>b</c> â€” equivalent to
        /// <c>worldPt = a.TransformPoint(b.TransformPoint(localPt))</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PxTransform Multiply(in PxTransform a, in PxTransform b)
        {
            // Same algebra a game engine matrix multiply uses, expressed against
            // a quaternion + offset representation.
            var rot = Quaternion.Multiply(a.Rotation, b.Rotation);
            var pos = Vector3.Transform(b.Position, a.Rotation) + a.Position;
            return new PxTransform(pos, rot);
        }

        /// <summary>
        /// Returns the inverse transform â€” flipping it would undo a
        /// <see cref="TransformPoint"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PxTransform Inverse()
        {
            var inverseRot = Conjugate(Rotation);
            var inversePos = Vector3.Transform(-Position, inverseRot);
            return new PxTransform(inversePos, inverseRot);
        }

        // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Quaternion conjugate â€” equal to the inverse for unit quaternions.
        /// We assume unit quaternions on the hot path (see
        /// <see cref="IsRotationUnit"/>); callers validate at the cache boundary.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Quaternion Conjugate(Quaternion q) =>
            new(-q.X, -q.Y, -q.Z, q.W);

        public override string ToString()
            => $"PxTransform(pos=<{Position.X:F2},{Position.Y:F2},{Position.Z:F2}>, " +
               $"rot=<{Rotation.X:F3},{Rotation.Y:F3},{Rotation.Z:F3},{Rotation.W:F3}>)";
    }
}
