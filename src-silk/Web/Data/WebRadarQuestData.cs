// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Misc.Data;

namespace eft_dma_radar.Silk.Web.Data
{
    /// <summary>
    /// Quest data snapshot served to the buddy web client via <c>/api/questdata</c>.
    /// Sourced from the bundled tarkov.dev data (<see cref="EftDataManager.TaskData"/>),
    /// independent of the host's active raid profile. The buddy decides what to track.
    /// </summary>
    public sealed class WebRadarQuestData
    {
        /// <summary>Schema version of this payload — bump when the shape changes.</summary>
        public int Version { get; set; } = 1;

        public WebRadarQuestEntry[] Quests { get; set; } = [];

        internal static WebRadarQuestData Build()
        {
            var src = EftDataManager.TaskData;
            if (src is null || src.Count == 0)
                return new WebRadarQuestData();

            var quests = new List<WebRadarQuestEntry>(src.Count);
            foreach (var kvp in src)
            {
                var t = kvp.Value;
                if (t is null || string.IsNullOrEmpty(t.Id))
                    continue;

                var entry = new WebRadarQuestEntry
                {
                    Id = t.Id,
                    Name = t.Name ?? t.Id,
                    Trader = t.Trader?.Name,
                    MapId = t.Map?.NormalizedName ?? t.Map?.Id,
                    KappaRequired = t.KappaRequired,
                };

                if (t.Objectives is { Count: > 0 })
                {
                    var objs = new List<WebRadarQuestObjective>(t.Objectives.Count);
                    foreach (var o in t.Objectives)
                    {
                        if (o is null)
                            continue;

                        var obj = new WebRadarQuestObjective
                        {
                            Id = o.Id,
                            Type = o.Type ?? string.Empty,
                            Optional = o.Optional,
                            Description = o.Description ?? string.Empty,
                            Count = o.Count,
                            FoundInRaid = o.FoundInRaid,
                        };

                        if (o.Item is not null && !string.IsNullOrEmpty(o.Item.Id))
                            obj.ItemId = o.Item.Id;
                        if (o.QuestItem is not null && !string.IsNullOrEmpty(o.QuestItem.Id))
                            obj.QuestItemId = o.QuestItem.Id;
                        if (o.MarkerItem is not null && !string.IsNullOrEmpty(o.MarkerItem.Id))
                            obj.MarkerItemId = o.MarkerItem.Id;

                        if (o.Maps is { Count: > 0 })
                        {
                            var mapIds = new List<string>(o.Maps.Count);
                            foreach (var m in o.Maps)
                            {
                                if (m is null) continue;
                                var mid = m.NormalizedName ?? m.Id;
                                if (!string.IsNullOrEmpty(mid))
                                    mapIds.Add(mid);
                            }
                            if (mapIds.Count > 0)
                                obj.MapIds = [.. mapIds];
                        }

                        if (o.Zones is { Count: > 0 })
                        {
                            var zones = new List<WebRadarQuestZone>(o.Zones.Count);
                            foreach (var z in o.Zones)
                            {
                                if (z is null) continue;
                                var zone = new WebRadarQuestZone
                                {
                                    Id = z.Id,
                                    MapId = z.Map?.NormalizedName ?? z.Map?.Id,
                                };
                                if (z.Position is not null)
                                {
                                    zone.X = z.Position.X;
                                    zone.Y = z.Position.Y;
                                    zone.Z = z.Position.Z;
                                    zone.HasPosition = true;
                                }
                                if (z.Outline is { Count: > 2 })
                                {
                                    var outline = new float[z.Outline.Count * 3];
                                    int idx = 0;
                                    foreach (var p in z.Outline)
                                    {
                                        if (p is null) continue;
                                        outline[idx++] = p.X;
                                        outline[idx++] = p.Y;
                                        outline[idx++] = p.Z;
                                    }
                                    if (idx > 0)
                                    {
                                        if (idx < outline.Length)
                                            Array.Resize(ref outline, idx);
                                        zone.Outline = outline;
                                    }
                                }
                                zones.Add(zone);
                            }
                            if (zones.Count > 0)
                                obj.Zones = [.. zones];
                        }

                        objs.Add(obj);
                    }
                    if (objs.Count > 0)
                        entry.Objectives = [.. objs];
                }

                quests.Add(entry);
            }

            return new WebRadarQuestData { Quests = [.. quests] };
        }
    }

    public sealed class WebRadarQuestEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Trader { get; set; }
        public string? MapId { get; set; }
        public bool KappaRequired { get; set; }
        public WebRadarQuestObjective[]? Objectives { get; set; }
    }

    public sealed class WebRadarQuestObjective
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool Optional { get; set; }
        public string Description { get; set; } = string.Empty;
        public int Count { get; set; }
        public bool FoundInRaid { get; set; }

        /// <summary>Required loose item BSG id (e.g. find/handover items).</summary>
        public string? ItemId { get; set; }
        /// <summary>Quest item BSG id (e.g. watches, letters, flagged with ItemTemplate.QuestItem).</summary>
        public string? QuestItemId { get; set; }
        /// <summary>Marker item BSG id (e.g. place item objectives).</summary>
        public string? MarkerItemId { get; set; }

        public string[]? MapIds { get; set; }
        public WebRadarQuestZone[]? Zones { get; set; }
    }

    public sealed class WebRadarQuestZone
    {
        public string Id { get; set; } = string.Empty;
        public string? MapId { get; set; }
        public bool HasPosition { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        /// <summary>Flat array of XYZ triples — length is multiple of 3.</summary>
        public float[]? Outline { get; set; }
    }
}
