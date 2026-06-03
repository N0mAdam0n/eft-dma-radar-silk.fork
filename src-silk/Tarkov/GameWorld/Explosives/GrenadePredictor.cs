// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Unity;
using static eft_dma_radar.Silk.Tarkov.Unity.UnityOffsets;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Explosives
{
    /// <summary>
    /// Reads the local player's hands controller each tick to detect when a grenade is equipped.
    /// When active, simulates a ballistic arc from the player's eye position along their look direction
    /// and exposes the predicted landing point for radar rendering.
    /// Also records a <see cref="LastThrow"/> snapshot the moment the grenade leaves the hand so that
    /// <see cref="Grenade"/> can compare the prediction against the actual trajectory on detonation.
    /// </summary>
    internal sealed class GrenadePredictor
    {
        // Default throw force (m/s) — used when no packet data is available.
        // EFT high-throw is approximately 19 m/s; low-throw is ~9 m/s.
        private const float DefaultHighThrowSpeed = 19f;
        private const float DefaultLowThrowSpeed  = 9f;

        private static readonly Dictionary<string, float> EffectiveDistances =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "F-1", 7f }, { "M67", 8f }, { "RGD-5", 7f }, { "RGN", 5f },
                { "RGO", 7f }, { "V40", 5f }, { "VOG-17", 6f }, { "VOG-25", 7f }
            };

        private readonly Player.LocalPlayer _localPlayer;

        // Cached last-known arc — written on worker thread, read on render thread.
        private volatile PredictedArc? _current;

        // Set to true while a grenade controller is active; used to detect the throw moment.
        private bool _wasActive;

        // Locked-in aim direction captured during the stable aiming phase (before throw arm-swing).
        // We stop updating this once lookDir.Y rises above the arm-swing threshold so that the
        // LastThrow snapshot always reflects where the player was actually aiming.
        private Vector3 _stableLookDir;
        private bool _stableLookDirSet;

        /// <summary>
        /// The prediction snapshot captured the moment the grenade left the local player's hand.
        /// Stays set until the next grenade is picked up and thrown.
        /// Consumed by <see cref="Grenade"/> for accuracy comparison logging.
        /// </summary>
        public static volatile PredictedArc? LastThrow;

        public GrenadePredictor(Player.LocalPlayer localPlayer)
        {
            _localPlayer = localPlayer;
        }

        /// <summary>The most recently computed arc, or null if no grenade is in hand.</summary>
        public PredictedArc? Current => _current;

        /// <summary>
        /// Called each explosives worker tick.  Reads the hands controller from memory,
        /// checks if a grenade is equipped, and recomputes the arc.
        /// </summary>
        public void Refresh()
        {
            try
            {
                var playerBase = _localPlayer.Base;
                if (!playerBase.IsValidVirtualAddress())
                {
                    ClearActive("player base invalid");
                    return;
                }

                // Read hands-controller pointer
                var handsControllerPtr = Memory.ReadPtr(playerBase + Offsets.Player._handsController, false);
                if (!handsControllerPtr.IsValidVirtualAddress())
                {
                    ClearActive("hands controller ptr invalid");
                    return;
                }

                // Check class name to confirm a grenade is in hand
                var className = Il2CppClass.ReadName(handsControllerPtr, 64, false);
                if (className is null || !className.Contains("GrenadeHandsController", StringComparison.OrdinalIgnoreCase))
                {
                    ClearActive(null);
                    return;
                }

                // Read look direction from MovementContext
                var movCtxPtr = Memory.ReadPtr(playerBase + Offsets.Player.MovementContext, false);
                if (!movCtxPtr.IsValidVirtualAddress())
                {
                    ClearActive("MovementContext ptr invalid");
                    return;
                }

                var lookDir = Memory.ReadValue<Vector3>(movCtxPtr + Offsets.MovementContext._lookDirection, false);
                if (!float.IsFinite(lookDir.X) || lookDir == Vector3.Zero)
                {
                    ClearActive("look direction invalid");
                    return;
                }
                lookDir = Vector3.Normalize(lookDir);

                // Lock the stable aim direction while the player is aiming (not throwing).
                // EFT's throw animation yanks the arm dramatically in a single tick (~30-50° jump).
                // Normal aiming between worker ticks moves far less.  We update the stable dir freely
                // unless the change in a single tick exceeds the arm-swing threshold.
                const float ArmSwingDelta = 0.35f; // ~20° per tick — arm-swing far exceeds this
                if (!_stableLookDirSet)
                {
                    _stableLookDir    = lookDir;
                    _stableLookDirSet = true;
                }
                else if (Vector3.Distance(lookDir, _stableLookDir) < ArmSwingDelta)
                {
                    _stableLookDir = lookDir;
                }

                // Use the stable aim direction for the arc; fall back to current if never set yet.
                var aimDir = _stableLookDirSet ? _stableLookDir : lookDir;

                // Use eye/look position as throw origin
                var origin = _localPlayer.HasLookPosition ? _localPlayer.LookPosition : _localPlayer.Position;

                // Try to read throw force / low-throw flag from GrenadePacket
                TryReadThrowForce(handsControllerPtr, out float throwSpeed, out bool isLow);

                // Resolve grenade name / effective distance
                var (name, effectiveDist) = ResolveGrenadeInfo(handsControllerPtr);

                // Simulate arc — use stable aim dir, not throw-animation dir
                var (arc, landing) = SimulateArc(origin, aimDir * throwSpeed);

                var prediction = new PredictedArc(arc, landing, name, effectiveDist,
                                                  origin, aimDir, throwSpeed, isLow);
                _current = prediction;

                // Log first detection
                if (!_wasActive)
                {
                    _wasActive = true;
                    Log.Write(AppLogLevel.Info,
                        $"[GrenadePredictor] Grenade in hand: {name}  " +
                        $"origin=({origin.X:F1},{origin.Y:F1},{origin.Z:F1})  " +
                        $"lookDir=({lookDir.X:F3},{lookDir.Y:F3},{lookDir.Z:F3})  " +
                        $"throwSpeed={throwSpeed:F1} m/s  lowThrow={isLow}  " +
                        $"predictedLanding=({landing.X:F1},{landing.Y:F1},{landing.Z:F1})");
                }
                else
                {
                    // Per-tick debug update (rate-limited to once per second)
                    Log.WriteRateLimited(AppLogLevel.Debug, "grenade_pred_tick", TimeSpan.FromSeconds(1f),
                        $"[GrenadePredictor] tick  name={name}  speed={throwSpeed:F1}  lowThrow={isLow}  " +
                        $"origin=({origin.X:F1},{origin.Y:F1},{origin.Z:F1})  " +
                        $"lookDir=({lookDir.X:F3},{lookDir.Y:F3},{lookDir.Z:F3})  " +
                        $"predictedLanding=({landing.X:F1},{landing.Y:F1},{landing.Z:F1})");
                }
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "grenade_pred_err", TimeSpan.FromSeconds(5),
                    $"[GrenadePredictor] Exception in Refresh: {ex.Message}");
                _current = null;
                _wasActive = false;
            }
        }

        /// <summary>
        /// Clears the active prediction.  If a grenade was previously in hand this is the throw moment —
        /// saves the last arc to <see cref="LastThrow"/> for post-flight comparison.
        /// </summary>
        private void ClearActive(string? reason)
        {
            if (_wasActive)
            {
                _wasActive = false;
                var snapshot = _current;
                if (snapshot is not null)
                {
                    LastThrow = snapshot;
                    Log.Write(AppLogLevel.Info,
                        $"[GrenadePredictor] Grenade left hand{(reason is not null ? $" ({reason})" : string.Empty)} — " +
                        $"throw snapshot saved  name={snapshot.Name}  " +
                        $"speed={snapshot.ThrowSpeed:F1} m/s  lowThrow={snapshot.IsLowThrow}  " +
                        $"predictedLanding=({snapshot.Landing.X:F1},{snapshot.Landing.Y:F1},{snapshot.Landing.Z:F1})  " +
                        $"arcPts={snapshot.Arc.Count}");
                }
            }
            _current          = null;
            _stableLookDirSet = false;
        }

        private static void TryReadThrowForce(ulong handsControllerPtr, out float speed, out bool isLow)
        {
            isLow = false;
            speed = DefaultHighThrowSpeed;
            try
            {
                var packetBase   = handsControllerPtr + Offsets.GrenadeHandsController.GrenadePacket;
                var throwDataBase = packetBase + Offsets.GrenadePacket.GrenadeThrowData;

                var hasData = Memory.ReadValue<bool>(throwDataBase + Offsets.GrenadeThrowData.HasThrowData, false);
                if (hasData)
                {
                    isLow = Memory.ReadValue<bool>(throwDataBase + Offsets.GrenadeThrowData.LowThrow, false);
                    var forceVec = Memory.ReadValue<Vector3>(throwDataBase + Offsets.GrenadeThrowData.ThrowForce, false);
                    float mag = forceVec.Length();
                    if (float.IsFinite(mag) && mag > 1f)
                    {
                        speed = mag;
                        return;
                    }
                }
            }
            catch { }

            // Fallback based on packet flags
            try
            {
                var packetBase = handsControllerPtr + Offsets.GrenadeHandsController.GrenadePacket;
                isLow = Memory.ReadValue<bool>(packetBase + Offsets.GrenadePacket.LowThrow, false);
            }
            catch { }
            speed = isLow ? DefaultLowThrowSpeed : DefaultHighThrowSpeed;
        }

        private static (string Name, float EffectiveDist) ResolveGrenadeInfo(ulong handsControllerPtr)
        {
            return ("Grenade", 0f);
        }

        private static (List<Vector3> Arc, Vector3 Landing) SimulateArc(Vector3 origin, Vector3 initialVelocity)
        {
            const float dt        = 0.05f;
            const float gravity   = -9.81f;
            const int   maxSteps  = 200;
            // Floor clamp: stop the arc when it descends below the player's feet level.
            // Use a slightly generous offset (3m) to account for terrain that drops below
            // the throw origin (e.g. throwing off an elevated position).
            const float floorDrop = 1.6f;
            float       floorY    = origin.Y - floorDrop;

            var arc = new List<Vector3>(64) { origin };
            var pos = origin;
            var vel = initialVelocity;

            for (int i = 0; i < maxSteps; i++)
            {
                // EFT grenades have negligible horizontal drag — apply gravity only.
                vel.Y += gravity * dt;
                pos   += vel * dt;

                if (pos.Y <= floorY && vel.Y < 0f)
                {
                    pos.Y = floorY;
                    arc.Add(pos);
                    break;
                }

                arc.Add(pos);

                if (vel.LengthSquared() < 0.25f)
                    break;
            }

            return (arc, arc[^1]);
        }
    }

    /// <summary>
    /// Snapshot of a predicted grenade trajectory.
    /// </summary>
    internal sealed class PredictedArc
    {
        public List<Vector3> Arc          { get; }
        public Vector3       Landing      { get; }
        public string        Name         { get; }
        public float         EffDist      { get; }

        // Extra fields for logging / comparison
        public Vector3       Origin       { get; }
        public Vector3       LookDir      { get; }
        public float         ThrowSpeed   { get; }
        public bool          IsLowThrow   { get; }

        public PredictedArc(List<Vector3> arc, Vector3 landing, string name, float effDist,
                            Vector3 origin, Vector3 lookDir, float throwSpeed, bool isLowThrow)
        {
            Arc         = arc;
            Landing     = landing;
            Name        = name;
            EffDist     = effDist;
            Origin      = origin;
            LookDir     = lookDir;
            ThrowSpeed  = throwSpeed;
            IsLowThrow  = isLowThrow;
        }
    }
}
