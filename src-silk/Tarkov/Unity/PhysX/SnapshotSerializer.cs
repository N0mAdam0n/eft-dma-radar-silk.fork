using System.IO;
using System.Text;
using eft_dma_radar.Silk.Misc;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Persistent on-disk format for <see cref="SceneSnapshot"/>. Lets the
    /// radar skip the multi-second PhysX walk on every match attach when the
    /// map / game build hasn't changed.
    /// <para>
    /// Storage path:
    /// <c>%AppData%\eft-dma-radar-silk6\physx-snapshots\&lt;mapId&gt;.bin</c>.
    /// One file per map id; the latest snapshot wins, no history kept.
    /// </para>
    /// <para>
    /// File layout (little-endian throughout):
    /// </para>
    /// <code>
    /// + 0   8  bytes  magic        = "ARNVPHX1"  (ASCII, no null)
    /// + 8   2  bytes  formatVer    = current = 1
    /// +10   2  bytes  reserved     (zero, future flags)
    /// +12   4  bytes  bodyCrc32    (CRC32C of bytes 16..EOF-4 â€” header + body)
    /// +16   8  bytes  fingerprint  (FNV-1a 64 of "unityPlayerVer|mapId|actorBucket")
    /// +24   8  bytes  buildTickMs  (Environment.TickCount64 when originally built)
    /// +32   4  bytes  sourceActorCount
    /// +36   2  bytes  mapId len    (UTF-8)
    /// +38   N  bytes  mapId        (no terminator)
    /// +N+0  2  bytes  unityVer len
    /// +N+2  M  bytes  unityVer     (UTF-8)
    ///       4  bytes  actorCount
    ///         per actor (variable, see <see cref="WriteActor"/>):
    ///             28 PxTransform + 12 aabbMin + 12 aabbMax +
    ///             1 geomType + 4 meshIdx + 4 hfIdx + 12 primSize +
    ///             8 actorBase + 4 layerMask + 4 groupMask
    ///       4  bytes  meshCount
    ///         per mesh (variable):
    ///             4 vCount + (12 * vCount) vertices +
    ///             4 iCount + (4  * iCount) indices  +
    ///             4 triangleCount +
    ///             12 localAabbMin + 12 localAabbMax + 8 meshBase
    ///       4  bytes  hfCount
    ///         per heightfield (variable):
    ///             4 rows + 4 cols + 4 sampleCount + (2 * sampleCount) i16 +
    ///             4 rowScale + 4 colScale + 4 heightScale +
    ///             12 localAabbMin + 12 localAabbMax + 8 hfBase
    /// EOF
    /// </code>
    /// <para>
    /// CRC32C is computed over header (excluding the CRC slot itself) + body
    /// and stored at +12. Mismatch â‡’ corrupted file, discard and rebuild.
    /// </para>
    /// <para>
    /// Atomic write: serialise to <c>&lt;path&gt;.tmp</c> first, then
    /// <c>File.Move(..., overwrite: true)</c>. A crash mid-write leaves the
    /// previous good snapshot intact and an orphaned <c>.tmp</c> that the
    /// next save sweeps over.
    /// </para>
    /// </summary>
    internal static class SnapshotSerializer
    {
        private const ulong Magic       = 0x315848505656524EUL; // "NRVVPHX1" little-endian read of "ARNVPHX1"
        // Format version history:
        //   V1: baseline (actors, tri-meshes, height-fields, per-actor world AABB).
        //   V2: per-actor Name (length-prefixed UTF-8) + UnityLayer (int32)
        //       added after ShapeGroupMask.
        //   V3: per-actor ConvexMeshIndex (int32) added after UnityLayer;
        //       ConvexMesh table (vertices + polygon planes + AABB) added
        //       between the Mesh and HeightField sections.
        // Older files on disk are rejected by the version check and rebuilt
        // fresh on the next attach.
        private const ushort FormatVer  = 3;
        private const int HeaderBytes   = 16; // bytes 0..15 (magic + version + reserved + crc slot)

        /// <summary>
        /// Returns the file path for this map's snapshot. Doesn't create the
        /// directory; callers that intend to write use
        /// <see cref="EnsureDirectoryExists"/>.
        /// </summary>
        public static string GetPath(string mapId)
        {
            string safeMap = SanitizeMapId(mapId);
            return Path.Combine(GetSnapshotsDirectory(), safeMap + ".bin");
        }

        /// <summary>
        /// Returns the directory that holds all per-map snapshot files
        /// (<c>%AppData%\eft-dma-radar-silk6\physx-snapshots</c>). Used by
        /// <see cref="EnumerateSnapshots"/> and the Cache View "Saved
        /// Snapshots" table â€” anywhere that needs to list files without
        /// knowing a specific map id up front.
        /// </summary>
        public static string GetSnapshotsDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "eft-dma-radar-silk6",
            "physx-snapshots");

        /// <summary>
        /// One row in the "Saved Snapshots" table â€” minimal metadata that's
        /// cheap to compute from <see cref="FileInfo"/>. <see cref="MapId"/>
        /// is the same string the snapshot was saved under (i.e. recoverable
        /// from the filename, post-sanitization).
        /// </summary>
        public sealed record SnapshotFileInfo(
            string MapId,
            string FilePath,
            long SizeBytes,
            DateTime CreatedUtc);

        /// <summary>
        /// Enumerates every <c>.bin</c> snapshot file in
        /// <see cref="GetSnapshotsDirectory"/>. Returns an empty enumeration
        /// if the directory doesn't exist yet (first run before any save).
        /// </summary>
        /// <remarks>
        /// Each row's <see cref="SnapshotFileInfo.MapId"/> is the filename
        /// stem â€” which is the sanitized form of the original map id. For
        /// vanilla Arena map names (<c>Arena_AutoService</c>, etc.) this is
        /// a lossless round-trip; for unusual map names with characters that
        /// got sanitized, the displayed value is the on-disk form.
        /// </remarks>
        public static IEnumerable<SnapshotFileInfo> EnumerateSnapshots()
        {
            string dir = GetSnapshotsDirectory();
            if (!Directory.Exists(dir)) yield break;
            foreach (var path in Directory.EnumerateFiles(dir, "*.bin"))
            {
                FileInfo fi;
                try { fi = new FileInfo(path); }
                catch { continue; }
                yield return new SnapshotFileInfo(
                    MapId: Path.GetFileNameWithoutExtension(path),
                    FilePath: path,
                    SizeBytes: fi.Length,
                    CreatedUtc: fi.LastWriteTimeUtc);
            }
        }

        /// <summary>
        /// Builds a 64-bit fingerprint that uniquely identifies the
        /// game-build / map combination this snapshot was built for. Mismatch
        /// on load â‡’ the cached file is for a different game build (Unity
        /// bump) or a different map and must not be used.
        /// <para>
        /// V1 fingerprint inputs: <c>UnityPlayer.dll FileVersion</c> +
        /// <c>mapId</c>. Catches the common stale case (BSG patches the game,
        /// Unity DLL version bumps, cache becomes invalid). Doesn't catch
        /// content-only patches that change map geometry without bumping
        /// Unity â€” empirically rare; if we observe stale-cache issues in the
        /// wild a future version can add an actor-count-bucket validator
        /// (would require a cheap pre-load probe of the live scene).
        /// </para>
        /// </summary>
        public static ulong ComputeFingerprint(string unityPlayerVersion, string mapId)
        {
            string key = $"{unityPlayerVersion}|{mapId}";
            return Fnv1a64(Encoding.UTF8.GetBytes(key));
        }

        /// <summary>
        /// Deletes the on-disk snapshot for <paramref name="mapId"/>, if one
        /// exists. Best-effort â€” returns true on success or when nothing was
        /// there to delete; false (with a log line) on permission / I/O
        /// errors. Used by the debug UI's "Invalidate Cache" button and by
        /// any future "force fresh build" code path.
        /// </summary>
        public static bool TryDelete(string mapId)
        {
            string path = GetPath(mapId);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.WriteLine($"[SnapshotSerializer] Deleted snapshot for '{mapId}' at {path}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[SnapshotSerializer] Delete failed for '{mapId}': {ex.Message}");
                return false;
            }
        }

        // â”€â”€ Save â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Serialise <paramref name="snap"/> to disk under the path returned
        /// by <see cref="GetPath"/>. Writes to a <c>.tmp</c> sidecar first
        /// then atomically renames; a crash mid-write leaves the previous
        /// snapshot intact. Returns true on success and writes a brief log
        /// line either way.
        /// </summary>
        public static bool TrySave(SceneSnapshot snap, string unityPlayerVersion)
        {
            if (snap is null || snap.IsEmpty) return false;
            string path = GetPath(snap.MapId);
            string tmp  = path + ".tmp";

            try
            {
                EnsureDirectoryExists(path);

                // FileAccess.ReadWrite (not just Write) â€” we seek back after the body
                // is written to compute and stamp the CRC32 over what we just wrote.
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
                {
                    // Reserve the header â€” the CRC at +12 isn't computable yet.
                    bw.Write(Magic);                 // +0  8 bytes
                    bw.Write(FormatVer);             // +8  2 bytes
                    bw.Write((ushort)0);             // +10 2 bytes reserved
                    bw.Write((uint)0);               // +12 4 bytes CRC placeholder

                    // Body starts at +16.
                    ulong fingerprint = ComputeFingerprint(unityPlayerVersion, snap.MapId);
                    bw.Write(fingerprint);                              // +16
                    bw.Write(snap.BuildTickMs);                         // +24
                    bw.Write(snap.SourceActorCount);                    // +32
                    WriteString(bw, snap.MapId);                        // +36 (len + bytes)
                    WriteString(bw, unityPlayerVersion);

                    bw.Write(snap.Actors.Length);
                    foreach (var a in snap.Actors) WriteActor(bw, a);

                    bw.Write(snap.Meshes.Length);
                    foreach (var m in snap.Meshes) WriteMesh(bw, m);

                    // V3 addition: ConvexMesh table between Mesh and HeightField.
                    bw.Write(snap.ConvexMeshes.Length);
                    foreach (var c in snap.ConvexMeshes) WriteConvexMesh(bw, c);

                    bw.Write(snap.HeightFields.Length);
                    foreach (var h in snap.HeightFields) WriteHeightField(bw, h);

                    // Body's all written; rewind & stamp the CRC.
                    bw.Flush();
                    long totalLen = fs.Length;
                    fs.Seek(HeaderBytes, SeekOrigin.Begin);
                    long bodyLen = totalLen - HeaderBytes;
                    uint crc = ComputeCrcFromStream(fs, bodyLen);
                    fs.Seek(12, SeekOrigin.Begin);
                    bw.Write(crc);
                    bw.Flush();
                }

                // Atomic publish â€” overwrite any previous snapshot for this map.
                File.Move(tmp, path, overwrite: true);

                long size = new FileInfo(path).Length;
                Log.WriteLine(
                    $"[SnapshotSerializer] Saved {snap.MapId}: " +
                    $"{snap.Actors.Length} actors / {snap.Meshes.Length} meshes / " +
                    $"{snap.ConvexMeshes.Length} convex / " +
                    $"{snap.HeightFields.Length} hfs, {size / 1024} KB â†’ {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[SnapshotSerializer] Save failed for '{snap.MapId}': {ex.Message}");
                TryDeleteQuiet(tmp);
                return false;
            }
        }

        // â”€â”€ Load â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Try to load + validate the snapshot for <paramref name="mapId"/>.
        /// Returns true and writes the rebuilt snapshot to
        /// <paramref name="snap"/> only when (a) the file exists, (b) magic +
        /// version + CRC all pass, (c) when <paramref name="requireFingerprint"/>
        /// is <c>true</c>, the embedded fingerprint matches
        /// <paramref name="expectedFingerprint"/>. Any failure returns false
        /// with a human-readable reason in <paramref name="error"/>; callers
        /// fall back to the live PhysX build.
        /// <para>
        /// Pass <c>requireFingerprint: false</c> when loading a snapshot for
        /// offline inspection (Cache View) where no live UnityPlayer version
        /// is available to construct the expected fingerprint. Magic, version,
        /// and CRC checks still run â€” only the game/map fingerprint is skipped.
        /// </para>
        /// </summary>
        public static bool TryLoad(
            string mapId,
            ulong expectedFingerprint,
            out SceneSnapshot? snap,
            out string error,
            bool requireFingerprint = true)
        {
            snap = null;
            error = string.Empty;

            string path = GetPath(mapId);
            if (!File.Exists(path))
            {
                error = "no cached snapshot on disk";
                return false;
            }

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

                // Header.
                if (fs.Length < HeaderBytes)
                {
                    error = $"truncated header (file is {fs.Length} bytes)";
                    return false;
                }
                ulong magic = br.ReadUInt64();
                ushort formatVer = br.ReadUInt16();
                ushort _reserved = br.ReadUInt16();
                uint expectedCrc = br.ReadUInt32();

                if (magic != Magic)
                {
                    error = $"bad magic (0x{magic:X16}, expected 0x{Magic:X16})";
                    return false;
                }
                if (formatVer != FormatVer)
                {
                    error = $"unsupported format version {formatVer} (this build expects {FormatVer})";
                    return false;
                }

                // Validate CRC before parsing anything â€” guards against partial / corrupt files.
                long bodyLen = fs.Length - HeaderBytes;
                fs.Seek(HeaderBytes, SeekOrigin.Begin);
                uint actualCrc = ComputeCrcFromStream(fs, bodyLen);
                if (actualCrc != expectedCrc)
                {
                    error = $"CRC mismatch (file=0x{expectedCrc:X8}, computed=0x{actualCrc:X8}) â€” corrupted";
                    return false;
                }

                // Rewind to body start and read.
                fs.Seek(HeaderBytes, SeekOrigin.Begin);
                ulong fingerprint    = br.ReadUInt64();
                long buildTickMs     = br.ReadInt64();
                int sourceActorCount = br.ReadInt32();
                string mapIdRead     = ReadString(br);
                string unityVerRead  = ReadString(br);

                if (requireFingerprint && fingerprint != expectedFingerprint)
                {
                    error = $"fingerprint mismatch (file=0x{fingerprint:X16}, " +
                            $"expected=0x{expectedFingerprint:X16}; mapId='{mapIdRead}', " +
                            $"unityVer='{unityVerRead}', sourceActors={sourceActorCount}) â€” game/map changed";
                    return false;
                }

                int actorCount = br.ReadInt32();
                if (actorCount < 0 || actorCount > 200_000)
                {
                    error = $"implausible actor count {actorCount}";
                    return false;
                }
                var actors = new CachedActor[actorCount];
                for (int i = 0; i < actorCount; i++) actors[i] = ReadActor(br, mapId);

                int meshCount = br.ReadInt32();
                if (meshCount < 0 || meshCount > 20_000)
                {
                    error = $"implausible mesh count {meshCount}";
                    return false;
                }
                var meshes = new CachedTriMesh[meshCount];
                for (int i = 0; i < meshCount; i++) meshes[i] = ReadMesh(br);

                int convexCount = br.ReadInt32();
                if (convexCount < 0 || convexCount > 5_000)
                {
                    error = $"implausible convex mesh count {convexCount}";
                    return false;
                }
                var convexes = new CachedConvexMesh[convexCount];
                for (int i = 0; i < convexCount; i++) convexes[i] = ReadConvexMesh(br);

                int hfCount = br.ReadInt32();
                if (hfCount < 0 || hfCount > 1_000)
                {
                    error = $"implausible heightfield count {hfCount}";
                    return false;
                }
                var hfs = new CachedHeightField[hfCount];
                for (int i = 0; i < hfCount; i++) hfs[i] = ReadHeightField(br);

                snap = new SceneSnapshot
                {
                    Actors           = actors,
                    Meshes           = meshes,
                    ConvexMeshes     = convexes,
                    HeightFields     = hfs,
                    BuildTickMs      = buildTickMs,
                    MapId            = mapIdRead,
                    NpPhysics        = 0, // not persisted â€” diagnostic only
                    SourceActorCount = sourceActorCount,
                };

                long size = new FileInfo(path).Length;
                Log.WriteLine(
                    $"[SnapshotSerializer] Loaded {mapId}: " +
                    $"{actorCount} actors / {meshCount} meshes / {hfCount} hfs, " +
                    $"{size / 1024} KB â† {path}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"read failed: {ex.Message}";
                return false;
            }
        }

        // â”€â”€ Per-record helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void WriteActor(BinaryWriter bw, CachedActor a)
        {
            // PxTransform = 28 bytes (Quaternion 16 + Vector3 12) â€” write each field
            // explicitly so we don't depend on struct memory layout serialising
            // identically across runtimes.
            bw.Write(a.WorldTransform.Rotation.X);
            bw.Write(a.WorldTransform.Rotation.Y);
            bw.Write(a.WorldTransform.Rotation.Z);
            bw.Write(a.WorldTransform.Rotation.W);
            bw.Write(a.WorldTransform.Position.X);
            bw.Write(a.WorldTransform.Position.Y);
            bw.Write(a.WorldTransform.Position.Z);

            WriteVec3(bw, a.WorldAabbMin);
            WriteVec3(bw, a.WorldAabbMax);
            bw.Write((byte)a.GeometryType);
            bw.Write(a.MeshIndex);
            bw.Write(a.HeightFieldIndex);
            WriteVec3(bw, a.PrimitiveSize);
            bw.Write(a.ActorBase);
            bw.Write(a.ShapeLayerMask);
            bw.Write(a.ShapeGroupMask);

            // V2 additions: per-actor Name + UnityLayer. BinaryWriter.Write(string)
            // writes a 7-bit-encoded length prefix followed by UTF-8 bytes â€”
            // compact for the typical short GameObject name, no per-record
            // padding wasted on empty strings.
            bw.Write(a.Name ?? string.Empty);
            bw.Write(a.UnityLayer);

            // V3 addition: per-actor ConvexMeshIndex (-1 when actor isn't
            // a convex mesh).
            bw.Write(a.ConvexMeshIndex);
        }

        private static CachedActor ReadActor(BinaryReader br, string mapId)
        {
            float qx = br.ReadSingle(), qy = br.ReadSingle(), qz = br.ReadSingle(), qw = br.ReadSingle();
            float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
            var xform = new PxTransform(new Vector3(px, py, pz), new Quaternion(qx, qy, qz, qw));
            var aMin = ReadVec3(br);
            var aMax = ReadVec3(br);
            var geom = (PxGeometryType)br.ReadByte();
            int meshIdx = br.ReadInt32();
            int hfIdx   = br.ReadInt32();
            var prim    = ReadVec3(br);
            ulong actorBase = br.ReadUInt64();
            uint layerMask  = br.ReadUInt32();
            uint groupMask  = br.ReadUInt32();
            string name     = br.ReadString();
            int unityLayer  = br.ReadInt32();
            int convexIdx   = br.ReadInt32();
            return new CachedActor
            {
                WorldTransform   = xform,
                WorldAabbMin     = aMin,
                WorldAabbMax     = aMax,
                GeometryType     = geom,
                MeshIndex        = meshIdx,
                ConvexMeshIndex  = convexIdx,
                HeightFieldIndex = hfIdx,
                PrimitiveSize    = prim,
                ActorBase        = actorBase,
                ShapeLayerMask   = layerMask,
                ShapeGroupMask   = groupMask,
                Name             = name,
                UnityLayer       = unityLayer,
                // Not persisted â€” classifier rules are runtime-tunable, so
                // we always recompute on load against the current ruleset.
                // Means rule changes apply on next session without needing
                // to invalidate the on-disk snapshot.
                IsSeeThrough     = VisibilityClassifier.Classify(mapId, layerMask, name),
            };
        }

        private static void WriteMesh(BinaryWriter bw, CachedTriMesh m)
        {
            bw.Write(m.Vertices.Length);
            for (int i = 0; i < m.Vertices.Length; i++) WriteVec3(bw, m.Vertices[i]);
            bw.Write(m.Indices.Length);
            for (int i = 0; i < m.Indices.Length; i++) bw.Write(m.Indices[i]);
            bw.Write(m.TriangleCount);
            WriteVec3(bw, m.LocalAabbMin);
            WriteVec3(bw, m.LocalAabbMax);
            bw.Write(m.MeshBase);
        }

        private static CachedTriMesh ReadMesh(BinaryReader br)
        {
            int vCount = br.ReadInt32();
            if (vCount < 0 || vCount > 1_000_000)
                throw new InvalidDataException($"implausible mesh vertex count {vCount}");
            var verts = new Vector3[vCount];
            for (int i = 0; i < vCount; i++) verts[i] = ReadVec3(br);

            int iCount = br.ReadInt32();
            if (iCount < 0 || iCount > 3_000_000)
                throw new InvalidDataException($"implausible mesh index count {iCount}");
            var inds = new int[iCount];
            for (int i = 0; i < iCount; i++) inds[i] = br.ReadInt32();

            int triCount = br.ReadInt32();
            var aMin = ReadVec3(br);
            var aMax = ReadVec3(br);
            ulong meshBase = br.ReadUInt64();
            return new CachedTriMesh
            {
                Vertices      = verts,
                Indices       = inds,
                TriangleCount = triCount,
                LocalAabbMin  = aMin,
                LocalAabbMax  = aMax,
                MeshBase      = meshBase,
            };
        }

        // â”€â”€ V3: ConvexMesh persistence â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void WriteConvexMesh(BinaryWriter bw, CachedConvexMesh m)
        {
            bw.Write(m.Vertices.Length);
            for (int i = 0; i < m.Vertices.Length; i++) WriteVec3(bw, m.Vertices[i]);
            bw.Write(m.PolygonCount);
            for (int i = 0; i < m.PolygonPlanes.Length; i++)
            {
                var pl = m.PolygonPlanes[i];
                bw.Write(pl.X); bw.Write(pl.Y); bw.Write(pl.Z); bw.Write(pl.W);
            }
            WriteVec3(bw, m.LocalAabbMin);
            WriteVec3(bw, m.LocalAabbMax);
            bw.Write(m.ConvexMeshBase);
        }

        private static CachedConvexMesh ReadConvexMesh(BinaryReader br)
        {
            int vCount = br.ReadInt32();
            // Convex hulls in PhysX are capped at 255 vertices by the cooker,
            // but we accept up to 4096 as a safety margin against future bumps.
            if (vCount < 4 || vCount > 4096)
                throw new InvalidDataException($"implausible convex vertex count {vCount}");
            var verts = new Vector3[vCount];
            for (int i = 0; i < vCount; i++) verts[i] = ReadVec3(br);

            int pCount = br.ReadInt32();
            if (pCount < 4 || pCount > 256)
                throw new InvalidDataException($"implausible convex polygon count {pCount}");
            var planes = new Vector4[pCount];
            for (int i = 0; i < pCount; i++)
            {
                float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle(), w = br.ReadSingle();
                planes[i] = new Vector4(x, y, z, w);
            }
            var aMin = ReadVec3(br);
            var aMax = ReadVec3(br);
            ulong meshBase = br.ReadUInt64();
            return new CachedConvexMesh
            {
                Vertices       = verts,
                PolygonPlanes  = planes,
                PolygonCount   = pCount,
                LocalAabbMin   = aMin,
                LocalAabbMax   = aMax,
                ConvexMeshBase = meshBase,
            };
        }

        private static void WriteHeightField(BinaryWriter bw, CachedHeightField h)
        {
            bw.Write(h.Rows);
            bw.Write(h.Columns);
            bw.Write(h.Samples.Length);
            for (int i = 0; i < h.Samples.Length; i++) bw.Write(h.Samples[i]);
            bw.Write(h.RowScale);
            bw.Write(h.ColumnScale);
            bw.Write(h.HeightScale);
            WriteVec3(bw, h.LocalAabbMin);
            WriteVec3(bw, h.LocalAabbMax);
            bw.Write(h.HeightFieldBase);
        }

        private static CachedHeightField ReadHeightField(BinaryReader br)
        {
            int rows = br.ReadInt32();
            int cols = br.ReadInt32();
            int samples = br.ReadInt32();
            if (rows < 0 || cols < 0 || samples < 0 || samples > 10_000_000 || samples != rows * cols)
                throw new InvalidDataException($"implausible heightfield dims rows={rows} cols={cols} samples={samples}");
            var s = new short[samples];
            for (int i = 0; i < samples; i++) s[i] = br.ReadInt16();
            float rowScale = br.ReadSingle();
            float colScale = br.ReadSingle();
            float hScale   = br.ReadSingle();
            var aMin = ReadVec3(br);
            var aMax = ReadVec3(br);
            ulong hfBase = br.ReadUInt64();
            return new CachedHeightField
            {
                Samples         = s,
                Rows            = rows,
                Columns         = cols,
                RowScale        = rowScale,
                ColumnScale     = colScale,
                HeightScale     = hScale,
                LocalAabbMin    = aMin,
                LocalAabbMax    = aMax,
                HeightFieldBase = hfBase,
            };
        }

        // â”€â”€ Primitive helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void WriteVec3(BinaryWriter bw, Vector3 v)
        {
            bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z);
        }

        private static Vector3 ReadVec3(BinaryReader br)
            => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

        private static void WriteString(BinaryWriter bw, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException($"string too long ({bytes.Length} > {ushort.MaxValue})", nameof(s));
            bw.Write((ushort)bytes.Length);
            bw.Write(bytes);
        }

        private static string ReadString(BinaryReader br)
        {
            ushort len = br.ReadUInt16();
            var bytes = br.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("string body truncated");
            return Encoding.UTF8.GetString(bytes);
        }

        // â”€â”€ CRC helper â€” chunked so big files don't allocate one big buffer â”€â”€
        // Standard IEEE-802.3 CRC32 (reflected polynomial 0xEDB88320). Inlined
        // to avoid pulling in System.IO.Hashing as a NuGet dependency for one
        // checksum function; the table is built once at type init.

        private static readonly uint[] _crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        private static uint ComputeCrcFromStream(Stream s, long length)
        {
            uint c = 0xFFFFFFFFu;
            byte[] buf = new byte[64 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                int want = (int)Math.Min(remaining, buf.Length);
                int read = s.Read(buf, 0, want);
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                    c = _crc32Table[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
                remaining -= read;
            }
            return c ^ 0xFFFFFFFFu;
        }

        // â”€â”€ Fingerprint hash â€” FNV-1a 64 â€” small + deterministic + zero deps â”€â”€

        private static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
        {
            const ulong Offset = 0xcbf29ce484222325UL;
            const ulong Prime  = 0x100000001b3UL;
            ulong h = Offset;
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= Prime;
            }
            return h;
        }

        // â”€â”€ Filesystem plumbing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void EnsureDirectoryExists(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static void TryDeleteQuiet(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* swallow â€” best-effort cleanup */ }
        }

        /// <summary>
        /// Strips characters that would be illegal in a Windows filename and
        /// caps length at 64 chars. Map ids in Arena are short ASCII names
        /// (<c>Arena_Bay5</c>, <c>Arena_Prison</c>) so the sanitiser rarely
        /// has to do anything, but we belt-and-brace against future BSG
        /// surprises.
        /// </summary>
        private static string SanitizeMapId(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return "_unknown";
            var sb = new StringBuilder(mapId.Length);
            foreach (char c in mapId)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            string s = sb.ToString();
            return s.Length <= 64 ? s : s[..64];
        }
    }
}
