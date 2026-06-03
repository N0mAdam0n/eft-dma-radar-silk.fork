// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;

namespace eft_dma_radar.Silk.UI.Maps
{
    /// <summary>
    /// Manages map configs and the currently active <see cref="RadarMap"/>.
    /// Thread-safe lazy load — rasterizes SVG layers on a background thread to avoid
    /// blocking the render loop. The render thread checks <see cref="IsLoading"/> and
    /// shows a status message until the map is ready.
    /// </summary>
    internal static class MapManager
    {
        private static FrozenDictionary<string, MapConfig> _configs =
            FrozenDictionary<string, MapConfig>.Empty;

        /// <summary>Which renderer backs the currently loaded map.</summary>
        private enum MapSourceMode
        {
            LocalSvg,
            Satellite,
            TarkovDevSvg,
        }

        private static IRadarMap? _currentMap;
        private static string? _currentMapId;
        private static MapSourceMode _currentSourceMode;
        private static MapConfig? _currentRawConfig;
        private static SatelliteMapCatalog.Entry? _currentSatelliteEntry;
        private static MapConfig? _currentSatelliteConfig;
        private static volatile bool _isLoading;
        private static readonly Lock _lock = new();

        /// <summary>Maps directory in the output tree.</summary>
        private static string MapsDir =>
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "Maps");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Currently active map, or <see langword="null"/> if none loaded.</summary>
        internal static IRadarMap? Map => _currentMap;

        /// <summary>
        /// Raw SVG <see cref="MapConfig"/> for the currently loaded map — always the
        /// SVG-space config, regardless of whether the local renderer is currently in
        /// satellite mode. Used by the web radar so its projection isn't disturbed by
        /// the host's satellite toggle.
        /// </summary>
        internal static MapConfig? RawConfig => _currentRawConfig;

        /// <summary>
        /// Satellite-space <see cref="MapConfig"/> for the currently loaded map, or
        /// <see langword="null"/> if the map has no satellite tiles. This is the same
        /// config <see cref="SatelliteMap"/> would render against; the web radar uses
        /// it for projection when its own satellite toggle is on.
        /// </summary>
        internal static MapConfig? SatelliteConfig => _currentSatelliteConfig;

        /// <summary>
        /// Tarkov.dev satellite-tile catalog entry for the currently loaded map, or
        /// <see langword="null"/> if unsupported. Exposed so the web radar can fetch
        /// tiles directly from the same CDN.
        /// </summary>
        internal static SatelliteMapCatalog.Entry? SatelliteEntry => _currentSatelliteEntry;

        /// <summary>Whether a map is currently being loaded on a background thread.</summary>
        internal static bool IsLoading => _isLoading;

        /// <summary>
        /// Scans the Maps directory, deserializes all JSON configs, and caches them.
        /// Call once at startup from <c>Program.cs</c>.
        /// </summary>
        internal static void ModuleInit()
        {
            var dir = MapsDir;
            if (!Directory.Exists(dir))
            {
                Log.WriteLine($"[MapManager] Maps directory not found: {dir}");
                return;
            }

            var builder = new Dictionary<string, MapConfig>(StringComparer.OrdinalIgnoreCase);
            int loaded = 0, skipped = 0;

            foreach (var jsonFile in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var stream = File.OpenRead(jsonFile);
                    var config = JsonSerializer.Deserialize<MapConfig>(stream, _jsonOpts);
                    if (config is null || config.MapLayers.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    // Apply any user-saved calibration override (X / Y / Scale) so the
                    // bundled defaults can be fine-tuned without editing the JSON file.
                    // First matching map-id wins (configs are shared between IDs).
                    var overrides = SilkProgram.Config?.MapCalibrationOverrides;
                    if (overrides is not null)
                    {
                        foreach (var id in config.MapID)
                        {
                            if (!string.IsNullOrWhiteSpace(id) && overrides.TryGetValue(id, out var ov))
                            {
                                config.X = ov.X;
                                config.Y = ov.Y;
                                config.Scale = ov.Scale;
                                Log.WriteLine($"[MapManager] Applied calibration override for '{id}': X={ov.X:0.00} Y={ov.Y:0.00} Scale={ov.Scale:0.0000}");
                                break;
                            }
                        }
                    }

                    foreach (var id in config.MapID)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                            builder[id] = config;
                    }

                    loaded++;
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[MapManager] Failed to load '{Path.GetFileName(jsonFile)}': {ex.Message}");
                    skipped++;
                }
            }

            _configs = builder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            Log.WriteLine($"[MapManager] Loaded {loaded} map configs ({_configs.Count} IDs), skipped {skipped}.");
        }

        /// <summary>
        /// Returns <c>true</c> if the given map ID has a bundled config in the Maps directory.
        /// Used to detect brand-new maps that we'll be rendering with the default fallback
        /// so we can surface a warning to the user. Not used to reject raids — unknown
        /// maps are still accepted and rendered against the default background.
        /// </summary>
        internal static bool IsKnownMap(string mapId)
        {
            if (string.IsNullOrEmpty(mapId))
                return false;

            return _configs.ContainsKey(mapId);
        }

        /// <summary>
        /// Kicks off a background load for the map matching <paramref name="mapId"/>.
        /// Returns immediately — the render thread should check <see cref="IsLoading"/>
        /// and <see cref="Map"/> each frame. No-ops if the requested map is already
        /// active or a load is in progress.
        /// </summary>
        internal static void LoadMap(string mapId)
        {
            if (string.IsNullOrEmpty(mapId))
                return;

            // Source-mode selection priority:
            //   1. Satellite tiles (if user opted in + catalog has tile entry)
            //   2. tarkov.dev SVG (if user opted in + catalog has SVG entry)
            //   3. Bundled local SVG (default fallback — always available)
            var wantMode = MapSourceMode.LocalSvg;
            if (SilkProgram.Config.UseSatelliteMap && SatelliteMapCatalog.IsSupported(mapId))
                wantMode = MapSourceMode.Satellite;
            else if (SilkProgram.Config.UseTarkovDevMap && TarkovDevSvgCatalog.IsSupported(mapId))
                wantMode = MapSourceMode.TarkovDevSvg;

            lock (_lock)
            {
                // Already loaded with same mode?
                if (string.Equals(_currentMapId, mapId, StringComparison.OrdinalIgnoreCase)
                    && _currentSourceMode == wantMode)
                    return;

                // Already loading?
                if (_isLoading)
                    return;

                // Resolve config
                if (!_configs.TryGetValue(mapId, out var config))
                {
                    // Fallback to default
                    if (!_configs.TryGetValue("default", out config))
                    {
                        Log.WriteLine($"[MapManager] No config found for '{mapId}' and no default.");
                        return;
                    }
                    Log.WriteLine($"[MapManager] No config for '{mapId}', using default.");
                }

                // Dispose old map
                var old = _currentMap;
                _currentMap = null;
                _currentMapId = null;
                old?.Dispose();

                // Cache raw SVG config + satellite metadata immediately so the web radar
                // can serve a stable projection even while the active map is mid-load.
                _currentRawConfig = config;
                if (SatelliteMapCatalog.TryGet(mapId, out var satEntry))
                {
                    _currentSatelliteEntry = satEntry;
                    _currentSatelliteConfig = SatelliteMapCatalog.BuildConfig(mapId, config, satEntry);
                }
                else
                {
                    _currentSatelliteEntry = null;
                    _currentSatelliteConfig = null;
                }

                _isLoading = true;
                var capturedConfig = config;
                var capturedId = mapId;
                var capturedMode = wantMode;

                Task.Run(() =>
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        IRadarMap? map = null;

                        switch (capturedMode)
                        {
                            case MapSourceMode.Satellite when SatelliteMapCatalog.TryGet(capturedId, out var entry):
                                Log.WriteLine($"[MapManager] Loading satellite map '{capturedId}' ({entry.CacheKey})...");
                                var satConfig = SatelliteMapCatalog.BuildConfig(capturedId, capturedConfig, entry);
                                map = new SatelliteMap(capturedId, satConfig, entry);
                                break;

                            case MapSourceMode.TarkovDevSvg when TarkovDevSvgCatalog.TryGet(capturedId, out var tdEntry):
                                Log.WriteLine($"[MapManager] Loading tarkov.dev SVG map '{capturedId}' ({tdEntry.NameId})...");
                                var svgPath = TarkovDevMapsClient.EnsureSvgCached(tdEntry);
                                if (!string.IsNullOrEmpty(svgPath))
                                {
                                    try
                                    {
                                        // MapConfig is computed inside the constructor from the
                                        // rasterized image dimensions + catalog bounds + user rotation.
                                        map = new TarkovDevSvgMap(capturedId, capturedConfig, tdEntry, svgPath, SilkProgram.Config.TarkovDevMapRotation);
                                    }
                                    catch (Exception tdEx)
                                    {
                                        Log.WriteLine($"[MapManager] tarkov.dev SVG renderer failed for '{capturedId}', falling back to local SVG: {tdEx.Message}");
                                    }
                                }
                                else
                                {
                                    Log.WriteLine($"[MapManager] tarkov.dev SVG fetch failed for '{capturedId}', falling back to local SVG.");
                                }
                                // Fall through to local SVG if tarkov.dev failed.
                                map ??= new RadarMap(MapsDir, capturedId, capturedConfig);
                                break;

                            default:
                                Log.WriteLine($"[MapManager] Loading map '{capturedId}' ({capturedConfig.Name})...");
                                map = new RadarMap(MapsDir, capturedId, capturedConfig);
                                break;
                        }
                        sw.Stop();
                        Log.WriteLine($"[MapManager] Map '{capturedConfig.Name}' ready ({sw.ElapsedMilliseconds}ms, mode={capturedMode}).");

                        lock (_lock)
                        {
                            _currentMap = map;
                            _currentMapId = capturedId;
                            _currentSourceMode = capturedMode;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine($"[MapManager] Failed to load map '{capturedId}': {ex}");
                    }
                    finally
                    {
                        _isLoading = false;
                    }
                });
            }
        }

        /// <summary>
        /// Forces the active map to reload (e.g. after toggling the satellite-map option).
        /// </summary>
        internal static void ReloadCurrent()
        {
            string? id;
            lock (_lock)
            {
                id = _currentMapId;
                var old = _currentMap;
                _currentMap = null;
                _currentMapId = null;
                old?.Dispose();
            }
            if (!string.IsNullOrEmpty(id))
                LoadMap(id);
        }
    }
}
