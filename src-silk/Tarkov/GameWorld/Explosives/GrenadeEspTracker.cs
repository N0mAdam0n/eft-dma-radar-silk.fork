// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Numerics;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Explosives
{
    /// <summary>
    /// Accumulates per-grenade historical position trails from <see cref="ExplosivesManager"/>.
    /// Provides thread-safe snapshots for ESP rendering of drag trails + current bold dot.
    /// Trails continue to be retained for a configurable time after the grenade is removed
    /// (detonated or expired) so the visual lingers briefly as requested.
    /// </summary>
    public sealed class GrenadeEspTracker
    {
        public static GrenadeEspTracker Instance { get; } = new();

        private readonly Dictionary<ulong, GrenadeTrail> _trails = new(16);
        private readonly object _sync = new();
        private readonly List<ulong> _stale = new(8);

        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        /// <summary>
        /// Feed current explosives snapshot (called from explosives worker after Refresh).
        /// This is the single writer. Appends new positions for live grenades and ages out old trails.
        /// </summary>
        internal void Feed(ExplosivesManager? manager)
        {
            var cfg = SilkProgram.Config?.EspGrenades;
            if (cfg is null || !cfg.Enabled)
            {
                Clear();
                return;
            }

            var now = DateTime.UtcNow;
            var life = TimeSpan.FromSeconds(cfg.TrailLifetime);
            var seen = new HashSet<ulong>(8);

            var items = manager?.Snapshot;
            if (items is not null)
            {
                foreach (var item in items)
                {
                    if (item is not Grenade g || !g.IsActive)
                        continue;

                    var pos = g.Position;
                    if (!IsFinite(pos))
                        continue;

                    seen.Add(g.Addr);

                    lock (_sync)
                    {
                        if (!_trails.TryGetValue(g.Addr, out var trail))
                        {
                            trail = new GrenadeTrail
                            {
                                Id = g.Addr,
                                Name = g.Name,
                                EffectiveDistance = g.EffectiveDistance
                            };
                            trail.Trail.Add(pos);
                            _trails[g.Addr] = trail;
                        }

                        trail.CurrentPosition = pos;
                        trail.LastSeen = now;
                        trail.Name = g.Name;
                        trail.EffectiveDistance = g.EffectiveDistance;

                        // Append to trail only if moved a minimum distance (reduces point spam).
                        const float MinDistSq = 0.12f * 0.12f;
                        if (trail.Trail.Count == 0 ||
                            (pos - trail.Trail[^1]).LengthSquared() >= MinDistSq)
                        {
                            if (trail.Trail.Count >= 48)
                                trail.Trail.RemoveAt(0); // keep memory bounded
                            trail.Trail.Add(pos);
                        }
                    }
                }
            }

            // Age out unseen trails (these are the ones that provide the "brief retention" after detonation).
            lock (_sync)
            {
                if (_trails.Count > 0)
                {
                    _stale.Clear();
                    foreach (var (id, t) in _trails)
                    {
                        if (seen.Contains(id))
                            continue;
                        if (now - t.LastSeen > life)
                            _stale.Add(id);
                    }
                    for (int i = 0; i < _stale.Count; i++)
                        _trails.Remove(_stale[i]);
                }
            }
        }

        /// <summary>
        /// Returns a copy of current (and recently-expired) grenade trails for rendering.
        /// Safe to call from any thread (ESP render thread etc).
        /// </summary>
        public GrenadeTrail[] GetSnapshot()
        {
            var cfg = SilkProgram.Config?.EspGrenades;
            if (cfg is null || !cfg.Enabled)
                return Array.Empty<GrenadeTrail>();

            var life = TimeSpan.FromSeconds(cfg.TrailLifetime);
            var now = DateTime.UtcNow;

            lock (_sync)
            {
                if (_trails.Count == 0)
                    return Array.Empty<GrenadeTrail>();

                var result = new List<GrenadeTrail>(_trails.Count);
                foreach (var t in _trails.Values)
                {
                    if (now - t.LastSeen > life)
                        continue;

                    var copy = new GrenadeTrail
                    {
                        Id = t.Id,
                        Name = t.Name,
                        EffectiveDistance = t.EffectiveDistance,
                        CurrentPosition = t.CurrentPosition,
                        LastSeen = t.LastSeen
                    };
                    copy.Trail.AddRange(t.Trail);
                    result.Add(copy);
                }
                return result.ToArray();
            }
        }

        /// <summary>Clear all history (e.g. on raid end or disable).</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _trails.Clear();
            }
        }
    }
}
