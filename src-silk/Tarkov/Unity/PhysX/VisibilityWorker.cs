using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using eft_dma_radar.Silk.DMA;
using eft_dma_radar.Silk.Tarkov.GameWorld;
using eft_dma_radar.Silk.Misc;
using eft_dma_radar.Silk.Misc.Workers;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Per-tick visibility loop. Runs on its own background thread, pulls the
    /// latest <see cref="SceneCache.Snapshot"/> reference, casts up to three
    /// rays per enemy (head / spine3 / pelvis), and writes the aggregated
    /// result into <see cref="Player.IsVisible"/> + <see cref="Player.LastVisCheckTickMs"/>.
    /// A player is reported visible if <em>any</em> of the three bones is
    /// reachable from the local eye â€” this catches the common "head behind
    /// cover, torso exposed" case that a single-ray head check misses.
    /// <para>
    /// Lock-free reader of the cache. Holds the snapshot reference for the
    /// duration of a single tick â€” any new snapshot published mid-tick is
    /// picked up on the next iteration.
    /// </para>
    /// <para>
    /// Metrics (checks, blocked %, avg/max time) are visible on the debug
    /// overlay via <see cref="LastTickStats"/> and <see cref="LastPerPlayer"/>.
    /// </para>
    /// </summary>
    internal static class VisibilityWorker
    {
        // â”€â”€ Configuration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Sleep between visibility ticks. 16 ms = ~60 Hz â€” fast enough for any
        /// UI refresh, slow enough not to compete with the realtime worker.
        /// </summary>
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);

        /// <summary>Max ray length. Players past this distance default to "visible" to avoid wasting raycasts.</summary>
        public static float MaxRayDistance { get; set; } = 200f;

        // â”€â”€ Body-part fallback heights â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // Player.Position is feet/root. When a player's skeleton hasn't been
        // resolved yet (mid-init after spawn / scene swap) we substitute these
        // standing-pose heights so rays don't trace from ground level.
        //
        // 1.65 m eye height matches typical first-person camera height in
        // tactical-shooter rigs; 1.0 m for chest and 0.5 m for hip put the
        // body-part fallbacks roughly where the spine3 and pelvis bones sit
        // on an average standing player model.
        private const float EyeFallbackY    = 1.65f;
        private const float ChestFallbackY  = 1.00f;
        private const float PelvisFallbackY = 0.50f;

        // Clearance subtracted from each ray's max distance so the target's
        // own collider doesn't self-block. 5 cm â€” conservative enough to
        // still catch glass / thin walls right in front of the target while
        // skipping the body capsule.
        private const float TargetClearance = 0.05f;

        // Per-bone bit positions in the visibility mask. The Phase 1 output
        // is still a boolean (IsVisible = any bone visible); the mask is
        // logged in the DIAG line so misfires can be pinpointed to a
        // specific bone, and is the foundation for future per-bone rendering.
        private const int BoneBitHead   = 0;
        private const int BoneBitChest  = 1;
        private const int BoneBitPelvis = 2;

        // â”€â”€ Worker state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static WorkerThread? _worker;
        private static volatile bool _enabled;
        // Throttle: tick-millisecond timestamp of the last diagnostic log line.
        private static long _lastDiagLogMs;

        public static bool Enabled => _enabled;

        // â”€â”€ Per-bone toggles (exposed so the debug window can disable individual bones) â”€â”€

        public static bool CheckHead   { get; set; } = true;
        public static bool CheckChest  { get; set; } = true;
        public static bool CheckPelvis { get; set; } = true;

        // â”€â”€ Config persistence helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public static void LoadFromConfig(SilkConfig cfg)
        {
            MaxRayDistance = cfg.VisCheckMaxRayDistance;
            CheckHead      = cfg.VisCheckBoneHead;
            CheckChest     = cfg.VisCheckBoneChest;
            CheckPelvis    = cfg.VisCheckBonePelvis;
        }

        public static void SaveToConfig(SilkConfig cfg)
        {
            cfg.VisCheckMaxRayDistance = MaxRayDistance;
            cfg.VisCheckBoneHead       = CheckHead;
            cfg.VisCheckBoneChest      = CheckChest;
            cfg.VisCheckBonePelvis     = CheckPelvis;
        }

        // â”€â”€ Public stats (read by the debug overlay) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Aggregate stats from the most recent visibility tick.</summary>
        public static TickStats LastTickStats { get; private set; }

        /// <summary>Per-player check results from the most recent visibility tick.</summary>
        private static readonly List<PlayerCheckResult> _lastPerPlayer = new();
        private static readonly object _lastPerPlayerLock = new();

        public static IReadOnlyList<PlayerCheckResult> LastPerPlayer
        {
            get
            {
                lock (_lastPerPlayerLock) return _lastPerPlayer.ToArray();
            }
        }

        // â”€â”€ Lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public static void Start()
        {
            if (_worker is not null) return;
            _worker = new WorkerThread
            {
                Name           = "Visibility Worker",
                ThreadPriority = ThreadPriority.BelowNormal,
                SleepDuration  = TickInterval,
                SleepMode      = WorkerSleepMode.DynamicSleep,
            };
            _worker.PerformWork += Tick;
            _worker.Start();
            _enabled = true;
            Log.WriteLine("[VisibilityWorker] Started.");
        }

        public static void Stop()
        {
            _worker?.Dispose();
            _worker = null;
            _enabled = false;
            Log.WriteLine("[VisibilityWorker] Stopped.");
        }

        // â”€â”€ Tick body â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void Tick(CancellationToken ct)
        {
            // Grab the snapshot reference *once* per tick; it stays valid for
            // the whole iteration even if SceneCache swaps in a newer one.
            var snap = SceneCache.Snapshot;
            if (snap.IsEmpty)
            {
                LastTickStats = new TickStats { Checks = 0, Blocked = 0, AvgUs = 0f, MaxUs = 0f, EyePos = Vector3.Zero };
                return;
            }

            var gw = Memory.Game;
            var local = gw?.LocalPlayer;
            // Skip when local player isn't alive â€” rays from a dead body
            // (which still has a position) would either be useless (the body
            // is in cover, so everything looks blocked) or pin "IsVisible"
            // to stale values from the last alive tick. Cleaner to clear the
            // tick stats and let the debug overlay show "checks=0" so the
            // user can see the worker is intentionally idle, not stuck.
            if (gw is null || local is null || !local.HasValidPosition || !local.IsAlive)
            {
                LastTickStats = new TickStats { Checks = 0, Blocked = 0, AvgUs = 0f, MaxUs = 0f, EyePos = Vector3.Zero };
                return;
            }

            // Eye position: prefer the head bone (stable from skeleton scatter);
            // fall back to player.Position (look-raycast endpoint) when unavailable.
            var eye = ResolveEyePosition(local);
            if (eye == Vector3.Zero)
            {
                LastTickStats = new TickStats { Checks = 0, Blocked = 0, AvgUs = 0f, MaxUs = 0f, EyePos = Vector3.Zero };
                return;
            }

            int checks = 0, blocked = 0;
            double totalUs = 0;
            float maxUs = 0f;
            var nowMs = Environment.TickCount64;
            // Throttle diagnostic logging â€” one line per ~1 second so we can see
            // eye / target positions and which actor blocked without flooding
            // the log. Gated behind Log.EnableDebugLogging so it stays silent
            // in normal play â€” the Top Blockers panel + tick-JSONL log are
            // the structured replacements for this ad-hoc per-second line.
            bool wantDiagLog = Log.EnableDebugLogging && nowMs - _lastDiagLogMs >= 1000;
            bool didDiagLog = false;

            // Reuse one buffer for the per-player results â€” cleared at start of tick.
            var perPlayer = new List<PlayerCheckResult>(16);

            foreach (var p in gw.RegisteredPlayers)
            {
                ct.ThrowIfCancellationRequested();
                if (p.IsLocalPlayer || !p.IsActive || !p.IsAlive || !p.HasValidPosition)
                    continue;

                // Resolve the three body-part targets up front so the dist /
                // out-of-range check can use the closest of them (head is
                // usually closest to the eye on a standing enemy).
                var targetHead   = ResolveBonePosition(p, eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanHead,   EyeFallbackY);
                var targetChest  = ResolveBonePosition(p, eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanSpine3, ChestFallbackY);
                var targetPelvis = ResolveBonePosition(p, eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanPelvis, PelvisFallbackY);
                if (targetHead == Vector3.Zero
                    && targetChest == Vector3.Zero
                    && targetPelvis == Vector3.Zero)
                    continue;

                float distHead   = Vector3.Distance(eye, targetHead);
                float distChest  = Vector3.Distance(eye, targetChest);
                float distPelvis = Vector3.Distance(eye, targetPelvis);
                // Reference distance â€” the closest body part. Used for the
                // out-of-range short-circuit and the perPlayer metric.
                float distRef = MathF.Min(distHead, MathF.Min(distChest, distPelvis));
                if (distRef <= 0.01f) continue;
                if (distRef > MaxRayDistance)
                {
                    // Too far to be worth raycasting â€” default to visible so
                    // long-range enemies don't get spuriously hidden.
                    p.IsVisible = true;
                    p.LastVisCheckTickMs = nowMs;
                    continue;
                }

                // Per-bone visibility â€” bit set when the bone IS visible.
                // Run all three even after the first success so the DIAG log
                // can show which bones are exposed vs blocked. Cheap enough at
                // current actor counts (3 Ã— ~100 Î¼s â‰ˆ 300 Î¼s per enemy).
                uint boneVis = 0;
                int firstBlockerIdx = -1;
                int firstBlockerBone = -1;

                var swPlayer = Stopwatch.GetTimestamp();
                // Declare blocker indices before the conditional casts so they're always in scope.
                int blkH = -1, blkC = -1, blkP = -1;
                bool h  = CheckHead   && !CastBoneRay(snap, eye, targetHead,   distHead,   out blkH);
                bool c  = CheckChest  && !CastBoneRay(snap, eye, targetChest,  distChest,  out blkC);
                bool pl = CheckPelvis && !CastBoneRay(snap, eye, targetPelvis, distPelvis, out blkP);
                double playerUs = (Stopwatch.GetTimestamp() - swPlayer) * 1_000_000.0 / Stopwatch.Frequency;

                if (h)  boneVis |= 1u << BoneBitHead;
                else if (CheckHead   && firstBlockerIdx < 0) { firstBlockerIdx = blkH; firstBlockerBone = BoneBitHead; }
                if (c)  boneVis |= 1u << BoneBitChest;
                else if (CheckChest  && firstBlockerIdx < 0) { firstBlockerIdx = blkC; firstBlockerBone = BoneBitChest; }
                if (pl) boneVis |= 1u << BoneBitPelvis;
                else if (CheckPelvis && firstBlockerIdx < 0) { firstBlockerIdx = blkP; firstBlockerBone = BoneBitPelvis; }

                if (playerUs > maxUs) maxUs = (float)playerUs;
                totalUs += playerUs;

                // When all bones are disabled there's nothing to block â€” default to visible.
                bool anyBoneEnabled = CheckHead || CheckChest || CheckPelvis;
                bool anyVisible = !anyBoneEnabled || boneVis != 0;
                p.IsVisible = anyVisible;
                p.LastVisCheckTickMs = nowMs;

                checks++;
                if (!anyVisible) blocked++;

                // Throttled diagnostic â€” first checked player of the tick gets a log line.
                if (wantDiagLog && !didDiagLog)
                {
                    didDiagLog = true;
                    _lastDiagLogMs = nowMs;
                    Log.WriteLine(BuildDiagLine(
                        p, eye, targetHead, targetChest, targetPelvis,
                        distHead, distChest, distPelvis,
                        boneVis, firstBlockerIdx, firstBlockerBone, snap));
                }

                perPlayer.Add(new PlayerCheckResult
                {
                    PlayerBase      = p.Base,
                    Name            = p.Name ?? "",
                    Distance        = distRef,
                    Visible         = anyVisible,
                    TimeUs          = (float)playerUs,
                    BlockerActorIdx = firstBlockerIdx,
                    BoneMask        = boneVis,
                    LastKnownPos    = p.Position,
                });
            }

            LastTickStats = new TickStats
            {
                Checks  = checks,
                Blocked = blocked,
                AvgUs   = checks > 0 ? (float)(totalUs / checks) : 0f,
                MaxUs   = maxUs,
                EyePos  = eye,
                TickMs  = Environment.TickCount64,
            };
            lock (_lastPerPlayerLock)
            {
                _lastPerPlayer.Clear();
                _lastPerPlayer.AddRange(perPlayer);
            }

            // Diagnostic hook â€” no-op unless the user enabled tick logging.
            // Passes the same `perPlayer` list to avoid a second alloc/lock
            // round-trip via the public LastPerPlayer accessor.
            if (perPlayer.Count > 0)
                VisCheckDiagnostics.OnVisibilityTick(eye, perPlayer, snap);

            // Always-on rolling-window blocker tracker â€” feeds the debug
            // window's "Top Blockers" table. Cheap (O(perPlayer)) and the
            // window self-prunes, so leaving it always-on costs nothing.
            BlockerHistory.RecordTick(perPlayer, Environment.TickCount64);
        }

        // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Local-player eye position. Prefer the resolved head bone; fall back
        /// to <c>Position + EyeFallbackY</c> so the ray starts at standing-eye
        /// height instead of feet level â€” feet-to-feet rays go through the
        /// ground and would always report blocked.
        /// </summary>
        private static Vector3 ResolveEyePosition(Player local)
        {
            var headBone = local.Skeleton?.GetBonePosition(eft_dma_radar.Silk.Tarkov.Unity.Bones.HumanHead);
            if (headBone.HasValue && IsFinite(headBone.Value) && headBone.Value != Vector3.Zero)
                return headBone.Value;
            if (local.HasValidPosition && IsFinite(local.Position))
                return local.Position + new Vector3(0f, EyeFallbackY, 0f);
            return Vector3.Zero;
        }

        /// <summary>
        /// Per-bone target resolver. Prefer the resolved bone position;
        /// fall back to <c>Position + (0, fallbackY, 0)</c> so the ray ends
        /// at the appropriate body-part height even when the skeleton hasn't
        /// been read yet (right after spawn, scene swap, etc.).
        /// </summary>
        private static Vector3 ResolveBonePosition(Player p, eft_dma_radar.Silk.Tarkov.Unity.Bones bone, float fallbackY)
        {
            var bonePos = p.Skeleton?.GetBonePosition(bone);
            if (bonePos.HasValue && IsFinite(bonePos.Value) && bonePos.Value != Vector3.Zero)
                return bonePos.Value;
            if (p.HasValidPosition && IsFinite(p.Position))
                return p.Position + new Vector3(0f, fallbackY, 0f);
            return Vector3.Zero;
        }

        /// <summary>
        /// Single-bone ray cast with target-clearance handling. Returns true
        /// when the ray was BLOCKED by an actor (out param is the blocker's
        /// index in the snapshot, -1 on miss). Returns false when the bone
        /// is visible. Skips the cast and returns "miss" when the target is
        /// the zero vector (skeleton lookup failed AND no position fallback).
        /// </summary>
        private static bool CastBoneRay(
            SceneSnapshot snap, Vector3 eye, Vector3 target, float dist, out int blockerIdx)
        {
            blockerIdx = -1;
            if (target == Vector3.Zero || dist <= 0.01f) return false;
            var dir = (target - eye) / dist;
            float maxT = dist - TargetClearance;
            if (maxT <= 0f) return false;
            return Raycaster.AnyHitWithActor(snap, eye, dir, maxT, out blockerIdx);
        }

        /// <summary>
        /// Composes the per-tick DIAG log line. Shows the eye position, all
        /// three bone targets with their distances, the bone-visibility mask
        /// (one bit per bone), and the first blocker encountered with its
        /// name + see-through classification.
        /// </summary>
        private static string BuildDiagLine(
            Player p, Vector3 eye,
            Vector3 tH, Vector3 tC, Vector3 tP,
            float dH, float dC, float dP,
            uint boneVis, int firstBlockerIdx, int firstBlockerBone,
            SceneSnapshot snap)
        {
            string boneLabel(int bit) => bit switch
            {
                BoneBitHead   => "head",
                BoneBitChest  => "chest",
                BoneBitPelvis => "pelvis",
                _             => "?",
            };
            string visStr =
                ((boneVis >> BoneBitHead)   & 1) == 1 ? "H" : "h" ;
            visStr +=
                ((boneVis >> BoneBitChest)  & 1) == 1 ? "C" : "c";
            visStr +=
                ((boneVis >> BoneBitPelvis) & 1) == 1 ? "P" : "p";

            string blockerDesc = "n/a";
            if (firstBlockerIdx >= 0 && firstBlockerIdx < snap.Actors.Length)
            {
                var blocker = snap.Actors[firstBlockerIdx];
                uint mask = blocker.ShapeLayerMask;
                string layerDesc = mask == 0
                    ? "layer=0"
                    : (mask & (mask - 1)) == 0
                        ? $"layer=idx{BitOperations.Log2(mask)}"
                        : $"layer=0x{mask:X8}(multi)";
                string nameDesc = string.IsNullOrEmpty(blocker.Name)
                    ? "(no name)"
                    : blocker.Name.Length > 36 ? blocker.Name.Substring(0, 35) + "â€¦" : blocker.Name;
                blockerDesc =
                    $"bone={boneLabel(firstBlockerBone)} " +
                    $"actor#{firstBlockerIdx} \"{nameDesc}\" " +
                    $"type={blocker.GeometryType} {layerDesc} " +
                    $"seeThrough={blocker.IsSeeThrough}";
            }

            string playerName = string.IsNullOrEmpty(p.Name) ? "(unnamed)" : p.Name;
            return
                $"[VisibilityWorker] DIAG enemy=\"{playerName}\" " +
                $"eye=({eye.X:F1},{eye.Y:F1},{eye.Z:F1}) " +
                $"H@d={dH:F1} C@d={dC:F1} P@d={dP:F1}  " +
                $"vis={visStr} blocker={blockerDesc}";
        }

        private static bool IsFinite(Vector3 v)
            => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        // â”€â”€ Stats DTOs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Aggregate stats from one visibility tick. Read by the debug overlay.</summary>
        public readonly struct TickStats
        {
            public int     Checks { get; init; }
            public int     Blocked{ get; init; }
            public float   AvgUs  { get; init; }
            public float   MaxUs  { get; init; }
            public Vector3 EyePos { get; init; }
            /// <summary><see cref="Environment.TickCount64"/> when this tick completed.</summary>
            public long    TickMs { get; init; }
        }

        /// <summary>Per-player visibility result from one tick.</summary>
        public readonly struct PlayerCheckResult
        {
            public ulong   PlayerBase { get; init; }
            public string  Name       { get; init; }
            public float   Distance   { get; init; }
            public bool    Visible    { get; init; }
            public float   TimeUs     { get; init; }
            /// <summary>
            /// Snapshot-relative index of the first blocker actor (-1 when
            /// <see cref="Visible"/> = true or all bones are disabled).
            /// Used by the Cache View to highlight currently-blocking actors.
            /// </summary>
            public int     BlockerActorIdx { get; init; }
            /// <summary>
            /// One bit per bone (bit 0 = head, 1 = chest, 2 = pelvis).
            /// Bit set â†’ bone is visible from the local eye. Uppercase H/C/P
            /// in the debug window indicates the bit is set; lowercase = blocked.
            /// </summary>
            public uint    BoneMask    { get; init; }
            /// <summary>Last known world position â€” used for live ray rendering in Cache View.</summary>
            public Vector3 LastKnownPos { get; init; }
        }
    }
}
