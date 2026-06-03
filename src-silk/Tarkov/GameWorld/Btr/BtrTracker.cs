// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Unity;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Btr
{
    /// <summary>A single BTR route stop with its world position and game-assigned id string.</summary>
    internal sealed record BtrRouteStop(Vector3 Position, string Id, string? MapId)
    {
        /// <summary>Human-readable stop name, or <c>null</c> for unnamed depot waypoints.</summary>
        public string? Name => BtrStopNames.Get(Id, MapId);
    }

    /// <summary>Static lookup: maps BTR route-stop ids to human-readable names, keyed by map id.</summary>
    internal static class BtrStopNames
    {
        // Woods: p1-p8 are the named passenger stops. p9+ are unnamed depot/turnaround waypoints.
        private static readonly Dictionary<string, string> _woods = new(StringComparer.OrdinalIgnoreCase)
        {
            { "p1", "Scav Bunker" },
            { "p2", "Sunken Village" },
            { "p3", "Junction" },
            { "p4", "Sawmill" },
            { "p5", "USEC Checkpoint" },
            { "p6", "Emercom Base" },
            { "p7", "Old Sawmill" },
            { "p8", "Train Depot" },
        };

        // Streets of Tarkov: same p1-p8 IDs but different landmark names.
        private static readonly Dictionary<string, string> _streets = new(StringComparer.OrdinalIgnoreCase)
        {
            { "p1", "Rodina Cinema" },
            { "p2", "Tram" },
            { "p3", "City Center" },
            { "p4", "Collapsed Crane" },
            { "p5", "Old Scav Checkpoint" },
            { "p6", "Pinewood Hotel" },
        };

        /// <summary>
        /// Returns the stop name for <paramref name="id"/> on <paramref name="mapId"/>,
        /// or <c>null</c> if the id is an unnamed depot waypoint.
        /// </summary>
        public static string? Get(string id, string? mapId)
        {
            var dict = mapId != null && mapId.Equals("tarkovstreets", StringComparison.OrdinalIgnoreCase)
                ? _streets
                : _woods;
            return dict.TryGetValue(id, out var name) ? name : null;
        }
    }

    /// <summary>
    /// Tracks the BTR vehicle position and renders it on the radar.
    /// The BTR only spawns on Streets and Woods maps.
    /// Position is read from BTRView._previousPosition via BtrController.
    ///
    /// <para>
    /// <b>Update model:</b> Resolution of the BtrView pointer is slow/rare and runs on the
    /// explosives worker (~100ms tick) via <see cref="Refresh"/>. Position / state / gunner
    /// reads are fast and run on the realtime worker (~8ms tick) via
    /// <see cref="UpdatePosition"/> so the BTR moves at the same sample rate as players —
    /// no visible jitter on radar or ESP.
    /// </para>
    /// <para>
    /// <b>Failure handling:</b> Transient DMA failures keep the last valid position so
    /// the marker does not flicker. Only after <see cref="MaxConsecutiveFailures"/>
    /// consecutive failures does the tracker invalidate its pointer and attempt to
    /// re-resolve on the next explosives tick.
    /// </para>
    /// <para>
    /// <b>Passenger snapping:</b> <see cref="TrySnapPassengerXZ"/> snaps the horizontal
    /// position of any player standing/sitting on the BTR to the BTR's own XZ. This
    /// removes jitter caused by passenger transforms being sampled slightly out-of-phase
    /// with the vehicle transform. The turret gunner is additionally identified directly
    /// via <see cref="GunnerPtr"/> (<c>BTRTurretView._bot</c>), which is an exact
    /// <c>ObservedPlayerView</c> pointer match.
    /// </para>
    /// </summary>
    internal sealed class BtrTracker
    {
        private const int MaxConsecutiveFailures = 10;

        /// <summary>Horizontal radius (meters) within which a player is considered to be on the BTR.</summary>
        private const float PassengerXZRadius = 3.0f;
        private const float PassengerXZRadiusSq = PassengerXZRadius * PassengerXZRadius;

        /// <summary>Vertical window (meters) around the BTR in which a player can be considered a passenger.</summary>
        private const float PassengerYBelow = 1.0f;
        private const float PassengerYAbove = 3.5f;

        private readonly ulong _localGameWorld;
        private readonly string _mapId;
        private ulong _btrController;
        private ulong _btrView;
        private ulong _btrTurretView;
        private Vector3 _position;
        private Vector3 _depotPosition;
        private float _currentSpeed;
        private byte _state;
        private byte _routeState;
        private int _timeToEndPauseMs;
        private bool _isPaid;
        private ulong _gunnerPtr;
        private float _turretYawDeg;
        private bool _initialized;
        private bool _hasValidPosition;
        private int _failureCount;
        private IReadOnlyList<BtrRouteStop> _routeStops = [];

        /// <summary>BTR world position (last known valid value).</summary>
        public Vector3 Position => _position;

        /// <summary>BTR depot/turnaround position from <c>MapPathConfig.DepotPosition</c>.</summary>
        public Vector3 DepotPosition => _depotPosition;

        /// <summary>Ordered list of BTR route stops resolved from <c>MapPathConfig.PathDestinations</c>.</summary>
        public IReadOnlyList<BtrRouteStop> RouteStops => _routeStops;

        /// <summary>Current BTR speed in m/s (from <c>BTRView.CurrentSpeed</c>).</summary>
        public float CurrentSpeed => _currentSpeed;

        /// <summary>True while the BTR is driving (any non-zero speed over a small threshold).</summary>
        public bool IsMoving => _currentSpeed > 0.1f;

        /// <summary>Raw <c>EBtrState</c> byte from <c>BTRView._btrState</c>.</summary>
        public byte State => _state;

        /// <summary>Raw <c>EBtrRouteState</c> byte from <c>BTRView.RouteState</c> (approach / at-stop / leaving).</summary>
        public byte RouteState => _routeState;

        /// <summary>
        /// Remaining pause time (milliseconds) at the current passenger stop,
        /// counted down live by <c>BTRView._timeToEndPause</c>. 0 when not paused.
        /// </summary>
        public int TimeToEndPauseMs => _timeToEndPauseMs;

        /// <summary>True when a player has paid for the BTR taxi service this raid (<c>BtrController.IsBtrPaid</c>).</summary>
        public bool IsPaid => _isPaid;

        /// <summary>
        /// Pointer to the turret gunner's <c>ObservedPlayerView</c>, or 0 if no gunner.
        /// Source: <c>BTRView.turret (0x60) → BTRTurretView._bot (0x60)</c>.
        /// </summary>
        public ulong GunnerPtr => _gunnerPtr;

        /// <summary>
        /// Current turret yaw in world degrees (0..360). Source: <c>BTRTurretView._targetTurretRotate</c>.
        /// Returns 0 if no turret has been resolved yet.
        /// </summary>
        public float TurretYawDeg => _turretYawDeg;

        /// <summary>True if the BTR has been found and has a valid last-known position.</summary>
        public bool IsActive => _initialized && _hasValidPosition;

        public BtrTracker(ulong localGameWorld, string mapId)
        {
            _localGameWorld = localGameWorld;
            _mapId = mapId;
        }

        /// <summary>
        /// Slow resolution tick — runs on the explosives worker (~100ms).
        /// Resolves the BtrView + turret pointers if not yet known.
        /// </summary>
        public void Refresh()
        {
            if (_initialized)
                return;

            try
            {
                if (!TryResolveBtrView())
                    return;

                // Cache turret once; the reference rarely changes for the life of the raid.
                if (Memory.TryReadPtr(_btrView + Offsets.BTRView.turret, out var turret, false) && turret != 0)
                    _btrTurretView = turret;

                // Resolve route stop positions from MapPathConfig.PathDestinations.
                // Each entry is a MonoBehaviour — position is read via the standard TransformChain.
                _routeStops = TryReadRouteStops();

                _initialized = true;
                _failureCount = 0;
                Log.WriteLine($"[BTR] BTR vehicle found — BtrView @ 0x{_btrView:X}, Turret @ 0x{_btrTurretView:X}");
            }
            catch
            {
                // BTR may not exist yet on this map — silently retry next tick
            }
        }

        /// <summary>
        /// Fast update — runs on the realtime worker (~8ms). Reads position, speed, state,
        /// and the turret gunner pointer. Keeps the last valid values on transient failure.
        /// </summary>
        public void UpdatePosition()
        {
            if (!_initialized)
                return;

            if (!Memory.TryReadValue<Vector3>(_btrView + Offsets.BTRView._previousPosition, out var pos, false))
            {
                OnReadFailure();
                return;
            }

            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
            {
                OnReadFailure();
                return;
            }

            _position = pos;
            _hasValidPosition = true;
            _failureCount = 0;

            // Cheap auxiliary reads — failures are non-fatal.
            if (Memory.TryReadValue<float>(_btrView + Offsets.BTRView.CurrentSpeed, out var spd, false)
                && float.IsFinite(spd))
                _currentSpeed = spd;

            if (Memory.TryReadValue<byte>(_btrView + Offsets.BTRView._btrState, out var st, false))
                _state = st;

            if (Memory.TryReadValue<byte>(_btrView + Offsets.BTRView.RouteState, out var rs, false))
                _routeState = rs;

            if (Memory.TryReadValue<int>(_btrView + Offsets.BTRView._timeToEndPause, out var ttp, false)
                && ttp >= 0 && ttp < 600) // sanity clamp (<10min, value is seconds)
                _timeToEndPauseMs = ttp * 1000;

            if (_btrController != 0
                && Memory.TryReadValue<byte>(_btrController + Offsets.BtrController.IsBtrPaid, out var paid, false))
            {
                _isPaid = paid != 0;
            }

            if (_btrTurretView != 0
                && Memory.TryReadPtr(_btrTurretView + Offsets.BTRTurretView.Bot, out var gunner, false))
            {
                _gunnerPtr = gunner.IsValidVirtualAddress() ? gunner : 0;
            }

            if (_btrTurretView != 0
                && Memory.TryReadValue<float>(_btrTurretView + Offsets.BTRTurretView.TargetTurretRotate, out var yaw, false)
                && float.IsFinite(yaw))
            {
                _turretYawDeg = yaw;
            }
        }

        /// <summary>
        /// Returns true if <paramref name="observedPlayerViewPtr"/> is the current BTR turret gunner.
        /// This is an authoritative identity match (not a proximity heuristic).
        /// </summary>
        public bool IsGunner(ulong observedPlayerViewPtr) =>
            _gunnerPtr != 0 && observedPlayerViewPtr == _gunnerPtr;

        /// <summary>
        /// If <paramref name="worldPos"/> lies within the BTR's passenger envelope, snaps
        /// its X/Z to the BTR's current X/Z (keeping the original Y). This removes jitter
        /// for the BTR turret operator / "scav on top" whose transform is sampled slightly
        /// out-of-phase with the vehicle itself.
        /// Returns true if a snap occurred.
        /// </summary>
        public bool TrySnapPassengerXZ(ref Vector3 worldPos)
        {
            if (!IsActive)
                return false;

            float dy = worldPos.Y - _position.Y;
            if (dy < -PassengerYBelow || dy > PassengerYAbove)
                return false;

            float dx = worldPos.X - _position.X;
            float dz = worldPos.Z - _position.Z;
            if (dx * dx + dz * dz > PassengerXZRadiusSq)
                return false;

            worldPos.X = _position.X;
            worldPos.Z = _position.Z;
            return true;
        }

        private void OnReadFailure()
        {
            if (++_failureCount < MaxConsecutiveFailures)
                return;

            // Pointer likely stale — force re-resolve on next explosives tick.
            _initialized = false;
            _hasValidPosition = false;
            _btrController = 0;
            _btrView = 0;
            _btrTurretView = 0;
            _gunnerPtr = 0;
            _currentSpeed = 0f;
            _state = 0;
            _routeState = 0;
            _timeToEndPauseMs = 0;
            _isPaid = false;
            _turretYawDeg = 0f;
            _failureCount = 0;
            _position = Vector3.Zero;
            Unity.IL2CPP.BtrControllerResolver.InvalidateCache();
        }

        /// <summary>
        /// Dumps IL2CPP field layouts for BtrController, BTRView, and BTRTurretView to <paramref name="sw"/>.
        /// Provides a full server-side/client-side field picture so offsets can be validated against the dump.
        /// </summary>
        internal void DumpAll(StreamWriter sw)
        {
            if (_btrController.IsValidVirtualAddress())
            {
                Il2CppDumper.DumpClassFieldsToWriter(_btrController, sw, $"BtrController @ 0x{_btrController:X}");

                // Dump destinationPrices — Dictionary<string, int> mapping stop id → taxi price
                try
                {
                    if (Memory.TryReadPtr(_btrController + Offsets.BtrController.DestinationPrices, out var pricesDict, false)
                        && pricesDict.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(pricesDict, sw, $"  destinationPrices @ 0x{pricesDict:X}");
                        using var dict = MemDictionary<ulong, int>.Get(pricesDict, false);
                        sw.WriteLine($"  destinationPrices count={dict.Count}");
                        int idx = 0;
                        foreach (var entry in dict)
                        {
                            if (!entry.Key.IsValidVirtualAddress()) continue;
                            string key = "<unknown>";
                            if (Memory.TryReadUnityString(entry.Key, out var ks) && !string.IsNullOrEmpty(ks))
                                key = ks;
                            sw.WriteLine($"    [{idx}] {key} = {entry.Value}");
                            idx++;
                        }
                    }
                    else sw.WriteLine("  destinationPrices: null");
                }
                catch (Exception ex) { sw.WriteLine($"  destinationPrices walk failed: {ex.Message}"); }
            }

            if (_btrView.IsValidVirtualAddress())
                Il2CppDumper.DumpClassFieldsToWriter(_btrView, sw, $"BTRView @ 0x{_btrView:X}");

            if (_btrTurretView.IsValidVirtualAddress())
                Il2CppDumper.DumpClassFieldsToWriter(_btrTurretView, sw, $"BTRTurretView @ 0x{_btrTurretView:X}");

            // Dump MapPathConfig so route stop and depot positions can be verified.
            if (_btrController.IsValidVirtualAddress()
                && Memory.TryReadPtr(_btrController + Offsets.BtrController.MapPathsConfiguration, out var mapPathCfg, false)
                && mapPathCfg.IsValidVirtualAddress())
            {
                Il2CppDumper.DumpClassFieldsToWriter(mapPathCfg, sw, $"MapPathConfig @ 0x{mapPathCfg:X}");
            }

            // Dump BTRGlobalSettings and its LocationsWithBTR string array.
            if (_btrController.IsValidVirtualAddress()
                && Memory.TryReadPtr(_btrController + Offsets.BtrController.BtrGlobalSettings, out var btrGlobals, false)
                && btrGlobals.IsValidVirtualAddress())
            {
                Il2CppDumper.DumpClassFieldsToWriter(btrGlobals, sw, $"BTRGlobalSettings @ 0x{btrGlobals:X}");
                try
                {
                    if (Memory.TryReadPtr(btrGlobals + Offsets.BTRGlobalSettings.LocationsWithBTR, out var arrPtr, false)
                        && arrPtr.IsValidVirtualAddress())
                    {
                        using var locArr = MemArray<ulong>.Get(arrPtr, false);
                        sw.WriteLine($"  LocationsWithBTR ({locArr.Count}):");
                        for (int i = 0; i < locArr.Count; i++)
                        {
                            if (Memory.TryReadUnityString(locArr[i], out var loc) && !string.IsNullOrEmpty(loc))
                                sw.WriteLine($"    [{i}] {loc}");
                            else
                                sw.WriteLine($"    [{i}] <unreadable>");
                        }
                    }
                    else sw.WriteLine("  LocationsWithBTR: null");
                }
                catch (Exception ex) { sw.WriteLine($"  LocationsWithBTR walk failed: {ex.Message}"); }

                // Walk MapsConfigs — Dictionary<string, BTRMapPath>
                try
                {
                    if (Memory.TryReadPtr(btrGlobals + Offsets.BTRGlobalSettings.MapsConfigs, out var mapsDict, false)
                        && mapsDict.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(mapsDict, sw, $"  MapsConfigs @ 0x{mapsDict:X}");
                        using var dict = MemDictionary<ulong, ulong>.Get(mapsDict, false);
                        sw.WriteLine($"  MapsConfigs entry count={dict.Count}");
                        int mapIdx = 0;
                        foreach (var entry in dict)
                        {
                            // Skip free/deleted slots — invalid key or value pointer
                            if (!entry.Key.IsValidVirtualAddress() || !entry.Value.IsValidVirtualAddress())
                                continue;

                            string keyStr = "<unknown>";
                            if (Memory.TryReadUnityString(entry.Key, out var ks) && !string.IsNullOrEmpty(ks))
                                keyStr = ks;

                            // Read value as string first to see what it actually is at runtime
                            string valStr = "<unknown>";
                            if (Memory.TryReadUnityString(entry.Value, out var vs) && !string.IsNullOrEmpty(vs))
                                valStr = vs;

                            sw.WriteLine($"  [{mapIdx}] key={keyStr} value={valStr}");

                            // Dump value object field layout to see what BTRMapPath really contains
                            Il2CppDumper.DumpClassFieldsToWriter(entry.Value, sw,
                                $"    BTRMapPath[{mapIdx}] @ 0x{entry.Value:X}");

                            // Try pathsConfigurations at runtime offset 0x18
                            if (Memory.TryReadPtr(entry.Value + Offsets.BTRMapPath.pathsConfigurations, out var pathsArr, false)
                                && pathsArr.IsValidVirtualAddress())
                            {
                                try
                                {
                                    using var pathArr = MemArray<ulong>.Get(pathsArr, false);
                                    sw.WriteLine($"    pathsConfigurations ({pathArr.Count}):");
                                    for (int pi = 0; pi < pathArr.Count; pi++)
                                    {
                                        var pathCfg = pathArr[pi];
                                        if (!pathCfg.IsValidVirtualAddress()) { sw.WriteLine($"      [{pi}] <invalid>"); continue; }
                                        Il2CppDumper.DumpClassFieldsToWriter(pathCfg, sw, $"      PathConfig[{pi}] @ 0x{pathCfg:X}");
                                    }
                                }
                                catch (Exception ex2) { sw.WriteLine($"    pathsConfigurations walk failed: {ex2.Message}"); }
                            }
                            else sw.WriteLine("    pathsConfigurations: null");

                            mapIdx++;
                        }
                    }
                    else sw.WriteLine("  MapsConfigs: null");
                }
                catch (Exception ex) { sw.WriteLine($"  MapsConfigs walk failed: {ex.Message}"); }
            }

            // Log resolved route stops.
            var stops = _routeStops;
            if (stops is { Count: > 0 })
            {
                sw.WriteLine($"  Route stops ({stops.Count}):");
                for (int i = 0; i < stops.Count; i++)
                    sw.WriteLine($"    [{i}] id={stops[i].Id} ({stops[i].Position.X:F1}, {stops[i].Position.Y:F1}, {stops[i].Position.Z:F1})");
            }
            else
            {
                sw.WriteLine("  Route stops: none resolved yet.");
            }
        }

        /// <summary>
        /// Draws the BTR on the radar as a large orange/raider-colored marker.
        /// </summary>
        public void Draw(SKCanvas canvas, MapParams mapParams, MapConfig mapCfg, Player.Player localPlayer, bool showRoute = false)
        {
            if (!IsActive)
                return;

            // ── Route stops ──────────────────────────────────────────────────────
            if (showRoute)
            {
                var stops = _routeStops;
                for (int i = 0; i < stops.Count; i++)
                {
                    var sp = mapParams.ToScreenPos(MapParams.ToMapPos(stops[i].Position, mapCfg));
                    canvas.DrawCircle(sp, 3f, SKPaints.ShapeBorder);
                    canvas.DrawCircle(sp, 3f, SKPaints.PaintBtrRouteStop);
                    // Only draw a name label for named passenger stops (p1-p8); depot waypoints are skipped.
                    var stopName = stops[i].Name;
                    if (stopName is not null)
                    {
                        var stopLabelPt = new SKPoint(sp.X + 5f, sp.Y - 4f);
                        canvas.DrawText(stopName, stopLabelPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
                        canvas.DrawText(stopName, stopLabelPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextBtr);
                    }
                }

                // ── Depot position marker ────────────────────────────────────────
                if (_depotPosition != Vector3.Zero)
                {
                    var depotScreenPos = mapParams.ToScreenPos(MapParams.ToMapPos(_depotPosition, mapCfg));
                    const float depotSize = 6f;
                    canvas.DrawCircle(depotScreenPos, depotSize, SKPaints.ShapeBorder);
                    canvas.DrawCircle(depotScreenPos, depotSize, SKPaints.PaintBtrRouteStop);
                    var depotLabel = "Depot";
                    var depotLabelWidth = SKPaints.FontRegular11.MeasureText(depotLabel, SKPaints.TextBtr);
                    var depotLabelPt = new SKPoint(depotScreenPos.X - depotLabelWidth / 2f, depotScreenPos.Y - 12f);
                    canvas.DrawText(depotLabel, depotLabelPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
                    canvas.DrawText(depotLabel, depotLabelPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextBtr);
                }
            }

            // ── Vehicle marker ───────────────────────────────────────────────────
            var dist = Vector3.Distance(localPlayer.Position, _position);
            var point = mapParams.ToScreenPos(MapParams.ToMapPos(_position, mapCfg));

            const float size = 8f;
            canvas.DrawCircle(point, size, SKPaints.ShapeBorder);
            canvas.DrawCircle(point, size, SKPaints.PaintBtr);

            // Turret aimline — short line pointing where the BTR gun is aimed.
            // Unity Y-yaw is clockwise from +Z; convert to 2D screen (X right, Y down):
            //   dx =  sin(yaw), dz = cos(yaw)  → on map, Y axis is inverted.
            if (_btrTurretView != 0)
            {
                const float lineLen = 28f;
                float rad = _turretYawDeg * (MathF.PI / 180f);
                float dx = MathF.Sin(rad);
                float dz = MathF.Cos(rad);
                // Map-space direction: +X right, -Z up in EFT → screen Y is inverted.
                var tip = new SKPoint(point.X + dx * lineLen, point.Y - dz * lineLen);
                canvas.DrawLine(point, tip, SKPaints.PaintBtr);
            }

            // "BTR" label. When stopped, show the remaining pause countdown if we have
            // one (from BTRView._timeToEndPause); otherwise just "idle". Append "$" when
            // a player has paid for the taxi service this raid (useful on Streets/Woods).
            string label;
            if (IsMoving)
            {
                label = "BTR";
            }
            else if (_timeToEndPauseMs > 0)
            {
                int secs = (_timeToEndPauseMs + 999) / 1000;
                label = $"BTR ({secs}s)";
            }
            else
            {
                label = "BTR (idle)";
            }
            if (_isPaid)
                label += " $";
            var labelWidth = SKPaints.FontRegular11.MeasureText(label, SKPaints.TextBtr);
            var labelPt = new SKPoint(point.X - labelWidth / 2f, point.Y - 12f);
            canvas.DrawText(label, labelPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
            canvas.DrawText(label, labelPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextBtr);

            // Distance label
            var distText = $"{(int)dist}m";
            var distWidth = SKPaints.FontRegular11.MeasureText(distText, SKPaints.TextBtr);
            var distPt = new SKPoint(point.X - distWidth / 2f, point.Y + 18f);
            canvas.DrawText(distText, distPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
            canvas.DrawText(distText, distPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextBtr);
        }

        private IReadOnlyList<BtrRouteStop> TryReadRouteStops()
        {
            try
            {
                if (!Memory.TryReadPtr(_btrController + Offsets.BtrController.MapPathsConfiguration, out var mapPathCfg, false)
                    || !mapPathCfg.IsValidVirtualAddress())
                    return [];

                // Read depot position from MapPathConfig.DepotPosition (offset 0x80)
                if (Memory.TryReadValue<Vector3>(mapPathCfg + Offsets.MapPathConfig.DepotPosition, out var depotPos, false)
                    && depotPos != Vector3.Zero)
                {
                    _depotPosition = depotPos;
                    Log.Write(AppLogLevel.Debug, $"[BTR] DepotPosition @ 0x{mapPathCfg + Offsets.MapPathConfig.DepotPosition:X} = ({depotPos.X:F1},{depotPos.Y:F1},{depotPos.Z:F1})");
                }

                if (!Memory.TryReadPtr(mapPathCfg + Offsets.MapPathConfig.PathDestinations, out var destList, false)
                    || !destList.IsValidVirtualAddress())
                    return [];

                using var list = MemList<ulong>.Get(destList, useCache: false);
                Log.Write(AppLogLevel.Debug, $"[BTR] PathDestinations list count={list.Count} @ 0x{destList:X}");
                if (list.Count <= 0 || list.Count > 64)
                    return [];

                var stops = new List<BtrRouteStop>(list.Count);
                // PathDestination is a scene-placed MonoBehaviour. The standard 6-hop TransformChain
                // fails because Comp_ObjectClass (+0x20) returns null for these objects.
                // Use the same short 4-hop chain as SniperFiringZonesManager:
                //   IL2CPP obj +0x10 → native MonoBehaviour
                //   +0x58 → native GameObject
                //   +0x58 → ComponentArray.ArrayBase
                //   +0x08 → Entry[0].Component = native Transform C++ ptr (= TransformInternal)
                uint[] shortChain = [0x10, UnityOffsets.Comp_GameObject, UnityOffsets.GO_Components, 0x08];

                for (int i = 0; i < list.Count; i++)
                {
                    var entry = list[i];
                    if (!entry.IsValidVirtualAddress())
                    {
                        Log.Write(AppLogLevel.Info, $"[BTR]   [{i}] entry invalid: 0x{entry:X}");
                        continue;
                    }

                    if (!Memory.TryReadPtrChain(entry, shortChain, out var transformInternal, false)
                        || !transformInternal.IsValidVirtualAddress())
                    {
                        Log.Write(AppLogLevel.Info, $"[BTR]   [{i}] transform chain failed for entry=0x{entry:X}");
                        continue;
                    }

                    var pos = UnityOffsets.ReadWorldPosition(transformInternal);

                    // Read PathPartBase.id (System.String* at offset 0x20 on the IL2CPP object).
                    string id = $"stop_{i}";
                    if (Memory.TryReadPtr(entry + 0x20, out var idPtr, false)
                        && Memory.TryReadUnityString(idPtr, out var idStr)
                        && !string.IsNullOrEmpty(idStr))
                        id = idStr;

                    Log.Write(AppLogLevel.Info, $"[BTR]   [{i}] id={id} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) transformInternal=0x{transformInternal:X}");
                    if (pos != Vector3.Zero)
                        stops.Add(new BtrRouteStop(pos, id, _mapId));
                }

                Log.WriteLine($"[BTR] Resolved {stops.Count} route stop(s) from MapPathConfig.");
                return stops;
            }
            catch (Exception ex)
            {
                Log.Write(AppLogLevel.Info, $"[BTR] TryReadRouteStops failed: {ex.Message}");
                return [];
            }
        }

        private bool TryResolveBtrView()
        {
            // Preferred path: resolve BtrController directly from its IL2CPP singleton
            // (<Instance>k__BackingField via TypeInfoTable + StaticFields). This avoids
            // an extra LocalGameWorld dereference and is stable across the raid.
            var btrController = Unity.IL2CPP.BtrControllerResolver.GetInstance();

            // Fallback: legacy chain via ClientLocalGameWorld.BtrController — kept so
            // the BTR still works on builds where the TypeIndex hasn't been dumped yet.
            if (!btrController.IsValidVirtualAddress())
            {
                if (!Memory.TryReadPtr(_localGameWorld + Offsets.ClientLocalGameWorld.BtrController, out btrController, false)
                    || btrController == 0)
                    return false;
            }

            if (!Memory.TryReadPtr(btrController + Offsets.BtrController.BtrView, out var btrView, false)
                || btrView == 0)
                return false;

            _btrController = btrController;
            _btrView = btrView;
            return true;
        }
    }
}
