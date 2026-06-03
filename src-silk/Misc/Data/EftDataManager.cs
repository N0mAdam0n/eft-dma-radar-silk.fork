// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;
using System.IO;
using eft_dma_radar.Silk.Misc.Data.TarkovMarket;

namespace eft_dma_radar.Silk.Misc.Data
{
    /// <summary>
    /// Loads and caches the Tarkov item database.
    /// On startup the embedded DEFAULT_DATA.json resource is used; a data.json disk cache
    /// takes priority when present and fresh. A background API refresh runs automatically
    /// when the cached data is older than 6 hours.
    /// Call <see cref="ModuleInit"/> once at startup.
    /// </summary>
    internal static class EftDataManager
    {
        private const string DataFileName = "data.json";
        private static readonly TimeSpan DataUpdateInterval = TimeSpan.FromHours(6);
        private static readonly string DataFilePath =
            Path.Combine(AppContext.BaseDirectory, DataFileName);

        /// <summary>All items keyed by BSG ID (excludes static containers).</summary>
        public static FrozenDictionary<string, TarkovMarketItem> AllItems { get; private set; }
            = FrozenDictionary<string, TarkovMarketItem>.Empty;

        /// <summary>Static containers keyed by BSG ID.</summary>
        public static FrozenDictionary<string, TarkovMarketItem> AllContainers { get; private set; }
            = FrozenDictionary<string, TarkovMarketItem>.Empty;

        /// <summary>Quest/task data keyed by task ID.</summary>
        public static FrozenDictionary<string, TaskElement> TaskData { get; private set; }
            = FrozenDictionary<string, TaskElement>.Empty;

        /// <summary>Map data (extracts, transits) keyed by map nameId.</summary>
        public static FrozenDictionary<string, MapElement> MapData { get; private set; }
            = FrozenDictionary<string, MapElement>.Empty;

        /// <summary>Trader lookup — BSG trader id mapped to display name.</summary>
        public static FrozenDictionary<string, string> AllTraders { get; private set; }
            = FrozenDictionary<string, string>.Empty;

        /// <summary>
        /// Loads the item database at startup.
        /// Priority: disk cache → embedded resource.
        /// A background refresh is queued when the cache is stale.
        /// </summary>
        internal static void ModuleInit()
        {
            DataRoot? data = null;

            // 1. Try disk cache
            if (File.Exists(DataFilePath))
            {
                try
                {
                    using var fs = File.OpenRead(DataFilePath);
                    data = JsonSerializer.Deserialize<DataRoot>(fs, _jsonOpts);
                    if (data?.Items is null || data.Items.Count == 0)
                        data = null;
                    else
                        Log.WriteLine("[EftDataManager] Loaded data from disk cache.");
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[EftDataManager] Disk cache invalid, will use embedded data: {ex.Message}");
                    data = null;
                }
            }

            // 2. Fall back to embedded resource
            if (data is null)
            {
                const string resourceName = "eft_dma_radar.Silk.DEFAULT_DATA.json";
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                    if (stream is null)
                    {
                        Log.WriteLine($"[EftDataManager] Embedded resource '{resourceName}' not found.");
                    }
                    else
                    {
                        data = JsonSerializer.Deserialize<DataRoot>(stream, _jsonOpts);
                        if (data?.Items is null || data.Items.Count == 0)
                            data = null;
                        else
                            Log.WriteLine("[EftDataManager] Loaded data from embedded resource.");
                    }
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[EftDataManager] Failed to load embedded data: {ex.Message}");
                }
            }

            if (data is not null)
                ApplyData(data);

            // 3. Schedule background API refresh when cache is stale
            bool cacheStale = !File.Exists(DataFilePath)
                || (DateTime.Now - File.GetLastWriteTime(DataFilePath)) > DataUpdateInterval;

            if (cacheStale)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000); // let the app finish starting
                        Log.WriteLine("[EftDataManager] Starting background market data update...");
                        string json = await TarkovMarketJob.GetUpdatedMarketDataAsync();
                        if (!string.IsNullOrEmpty(json))
                        {
                            await File.WriteAllTextAsync(DataFilePath, json);
                            var updated = JsonSerializer.Deserialize<DataRoot>(json, _jsonOpts);
                            if (updated?.Items is { Count: > 0 })
                            {
                                ApplyData(updated);
                                Log.WriteLine("[EftDataManager] Background market data update applied.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine($"[EftDataManager] Background update failed: {ex.Message}");
                    }
                });
            }
        }

        private static void ApplyData(DataRoot data)
        {
            var itemBuilder = new Dictionary<string, TarkovMarketItem>(data.Items.Count, StringComparer.Ordinal);
            var containerBuilder = new Dictionary<string, TarkovMarketItem>(64, StringComparer.Ordinal);
            foreach (var item in data.Items)
            {
                if (string.IsNullOrEmpty(item.BsgId))
                    continue;
                if (item.IsStaticContainer)
                    containerBuilder.TryAdd(item.BsgId, item);
                else
                    itemBuilder.TryAdd(item.BsgId, item);
            }
            AllItems = itemBuilder.ToFrozenDictionary(StringComparer.Ordinal);
            AllContainers = containerBuilder.ToFrozenDictionary(StringComparer.Ordinal);
            Log.WriteLine($"[EftDataManager] {AllItems.Count} items, {AllContainers.Count} containers.");

            if (data.Tasks is { Count: > 0 })
            {
                var taskBuilder = new Dictionary<string, TaskElement>(data.Tasks.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var task in data.Tasks)
                    if (!string.IsNullOrEmpty(task.Id))
                        taskBuilder.TryAdd(task.Id, task);
                TaskData = taskBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                Log.WriteLine($"[EftDataManager] {TaskData.Count} tasks.");
            }

            if (data.Maps is { Count: > 0 })
            {
                var mapBuilder = new Dictionary<string, MapElement>(data.Maps.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var map in data.Maps)
                    if (!string.IsNullOrEmpty(map.NameId))
                        mapBuilder.TryAdd(map.NameId, map);
                MapData = mapBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                Log.WriteLine($"[EftDataManager] {MapData.Count} map configs.");
            }

            if (data.Traders is { Count: > 0 })
            {
                var traderBuilder = new Dictionary<string, string>(data.Traders.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var t in data.Traders)
                    if (!string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name))
                        traderBuilder.TryAdd(t.Id, t.Name);
                AllTraders = traderBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                Log.WriteLine($"[EftDataManager] {AllTraders.Count} traders.");
            }
        }

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private sealed class DataRoot
        {
            [JsonPropertyName("items")]
            public List<TarkovMarketItem> Items { get; set; } = [];

            [JsonPropertyName("tasks")]
            public List<TaskElement> Tasks { get; set; } = [];

            [JsonPropertyName("maps")]
            public List<MapElement> Maps { get; set; } = [];

            [JsonPropertyName("traders")]
            public List<TraderEntry> Traders { get; set; } = [];
        }

        private sealed class TraderEntry
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }

        #region Map Data Models

        internal sealed class MapElement
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("nameId")]
            public string NameId { get; set; } = string.Empty;

            [JsonPropertyName("extracts")]
            public List<ExtractElement> Extracts { get; set; } = [];

            [JsonPropertyName("transits")]
            public List<TransitElement> Transits { get; set; } = [];
        }

        internal sealed class ExtractElement
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("faction")]
            public string Faction { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public MapPositionElement? Position { get; set; }
        }

        internal sealed class TransitElement
        {
            [JsonPropertyName("description")]
            public string Description { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public MapPositionElement? Position { get; set; }
        }

        internal sealed class MapPositionElement
        {
            [JsonPropertyName("x")]
            public float X { get; set; }

            [JsonPropertyName("y")]
            public float Y { get; set; }

            [JsonPropertyName("z")]
            public float Z { get; set; }

            public Vector3 ToVector3() => new(X, Y, Z);
        }

        #endregion
    }
}
