// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Features.Ballistics;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Ballistics
{
    /// <summary>
    /// Reads <c>EFT.Ballistics.BallisticsCalculator.Shots</c> each tick and accumulates
    /// per-bullet trail histories. Also snapshots the game's G1 drag table the first
    /// time a valid <c>Shot.G1</c> list is observed (feeds <see cref="G1Table"/>).
    /// </summary>
    public sealed class LiveShotTracker
    {
        private const int MaxConcurrentShots = 256;
        private const int MaxTrailPoints = 64;
        private const float MinPointDistance = 0.10f; // meters
        private const float MinPointDistanceSq = MinPointDistance * MinPointDistance;

        private readonly Dictionary<ulong, LiveShot> _shots = new(MaxConcurrentShots);
        private readonly object _sync = new();

        private TimeSpan _lifetime = TimeSpan.FromSeconds(4.5);
        public TimeSpan Lifetime
        {
            get => _lifetime;
            set => _lifetime = value < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : value;
        }

        /// <summary>Pulled from BallisticsCalculator each tick — strictly increasing per shot fired.</summary>
        public int LastFireIndex { get; private set; }
        /// <summary>Count of currently tracked shots after the latest <see cref="Update"/>.</summary>
        public int TrackedCount { get; private set; }
        /// <summary>True once <see cref="G1Table.SetFromGame"/> has accepted a real read.</summary>
        public bool G1Captured { get; private set; }

        public void Clear()
        {
            lock (_sync)
            {
                _shots.Clear();
                TrackedCount = 0;
                LastFireIndex = 0;
                G1Captured = false;
            }
        }

        /// <summary>
        /// Walk the current <c>Shots</c> list and refresh trail data for every bullet.
        /// Safe to call from a dedicated worker thread (single-writer); readers should
        /// call <see cref="GetSnapshot"/> instead of touching internal state.
        /// </summary>
        public void Update(ulong gameWorldBase)
        {
            if (!gameWorldBase.IsValidVirtualAddress()) return;

            ulong calcPtr = 0;
            if (!Memory.TryReadPtr(gameWorldBase + Offsets.ClientLocalGameWorld.SharedBallisticsCalculator, out calcPtr)
                || !calcPtr.IsValidVirtualAddress())
            {
                if (!Memory.TryReadPtr(gameWorldBase + Offsets.ClientLocalGameWorld.ClientBallisticCalculator, out calcPtr)
                    || !calcPtr.IsValidVirtualAddress())
                    return;
            }

            // Read FireIndex (debug HUD).
            if (Memory.TryReadValue<int>(calcPtr + Offsets.BallisticsCalculator.FireIndex, out var fi))
                LastFireIndex = fi;

            if (!Memory.TryReadPtr(calcPtr + Offsets.BallisticsCalculator.Shots, out var shotsListObj)
                || !shotsListObj.IsValidVirtualAddress())
                return;

            // Snapshot Shot pointers (List<Shot> stores class refs — read as ulong array).
            ulong[]? shotPtrs = null;
            int count = 0;
            try
            {
                using var list = MemList<ulong>.Get(shotsListObj, false);
                count = Math.Min(list.Count, MaxConcurrentShots);
                if (count > 0)
                {
                    shotPtrs = ArrayPool<ulong>.Shared.Rent(count);
                    list.Span[..count].CopyTo(shotPtrs.AsSpan(0, count));
                }
            }
            catch { /* empty / mid-write — skip frame */ }

            var now = DateTime.UtcNow;
            var seen = new HashSet<ulong>(count);

            if (shotPtrs is not null)
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        ulong sp = shotPtrs[i];
                        if (!sp.IsValidVirtualAddress()) continue;
                        if (!ReadShotInto(sp, now, out var trail)) continue;
                        seen.Add(sp);
                        AppendTrailPoint(trail);

                        if (!G1Captured) TryCaptureG1(sp);
                    }
                }
                finally
                {
                    Array.Clear(shotPtrs, 0, count);
                    ArrayPool<ulong>.Shared.Return(shotPtrs, false);
                }
            }

            // GC stale shots: not seen this tick AND older than Lifetime.
            lock (_sync)
            {
                if (_shots.Count > 0)
                {
                    var stale = new List<ulong>();
                    foreach (var (id, shot) in _shots)
                    {
                        if (seen.Contains(id)) continue;
                        if (now - shot.LastSeen > _lifetime) stale.Add(id);
                    }
                    foreach (var id in stale) _shots.Remove(id);
                }
                TrackedCount = _shots.Count;
            }
        }

        private bool ReadShotInto(ulong shotPtr, DateTime now, out LiveShot trail)
        {
            // Single-batch read of the hot Shot fields.
            if (!Memory.TryReadValue<Vector3>(shotPtr + Offsets.Shot.CurrentPosition, out var curPos)) { trail = null!; return false; }
            if (!Memory.TryReadValue<Vector3>(shotPtr + Offsets.Shot.Velocity,         out var vel))     vel = Vector3.Zero;
            if (!Memory.TryReadValue<float>(shotPtr + Offsets.Shot.TimeSinceShot,      out var age))     age = 0f;
            ulong owner = 0;
            Memory.TryReadPtr(shotPtr + Offsets.Shot.Player, out owner);

            lock (_sync)
            {
                if (!_shots.TryGetValue(shotPtr, out trail!))
                {
                    if (_shots.Count >= MaxConcurrentShots)
                    {
                        trail = null!;
                        return false;
                    }
                    trail = new LiveShot { Id = shotPtr };
                    if (Memory.TryReadValue<Vector3>(shotPtr + Offsets.Shot.StartPosition, out var startPos))
                        trail.StartPosition = startPos;
                    else
                        trail.StartPosition = curPos;
                    trail.Trail.Add(trail.StartPosition);
                    _shots[shotPtr] = trail;
                }
                trail.CurrentPosition = curPos;
                trail.Velocity = vel;
                trail.TimeSinceShot = age;
                trail.OwnerPlayer = owner;
                trail.LastSeen = now;
            }
            return true;
        }

        private void AppendTrailPoint(LiveShot trail)
        {
            lock (_sync)
            {
                var t = trail.Trail;
                if (t.Count >= MaxTrailPoints)
                {
                    // Drop oldest middle point to preserve start and current endpoints.
                    t.RemoveAt(t.Count / 2);
                }
                if (t.Count == 0 || (trail.CurrentPosition - t[^1]).LengthSquared() >= MinPointDistanceSq)
                    t.Add(trail.CurrentPosition);
            }
        }

        private void TryCaptureG1(ulong shotPtr)
        {
            // Respect the user's "Use Live G1 Table" toggle.
            if (!(SilkProgram.Config?.Ballistics?.UseGameG1Table ?? true))
                return;

            try
            {
                if (!Memory.TryReadPtr(shotPtr + Offsets.Shot.G1, out var g1ListObj)
                    || !g1ListObj.IsValidVirtualAddress())
                    return;
                using var list = MemList<G1DragModel>.Get(g1ListObj, false);
                if (list.Count < 40) return; // bad read
                G1Table.SetFromGame(list.Span);
                G1Captured = true;
                Log.WriteLine($"[Ballistics] Captured live G1 table from Shot 0x{shotPtr:X} ({list.Count} entries)");
            }
            catch { /* try again next shot */ }
        }

        /// <summary>
        /// Returns a stable snapshot of current trails for rendering. Each entry is an
        /// independent <see cref="LiveShot"/> instance with a copied Trail list.
        /// </summary>
        public LiveShot[] GetSnapshot()
        {
            lock (_sync)
            {
                if (_shots.Count == 0) return Array.Empty<LiveShot>();
                var arr = new LiveShot[_shots.Count];
                int i = 0;
                foreach (var s in _shots.Values)
                {
                    var copy = new LiveShot
                    {
                        Id = s.Id,
                        OwnerPlayer = s.OwnerPlayer,
                        LastSeen = s.LastSeen,
                        TimeSinceShot = s.TimeSinceShot,
                        Velocity = s.Velocity,
                        CurrentPosition = s.CurrentPosition,
                        StartPosition = s.StartPosition,
                    };
                    copy.Trail.AddRange(s.Trail);
                    arr[i++] = copy;
                }
                return arr;
            }
        }
    }
}
