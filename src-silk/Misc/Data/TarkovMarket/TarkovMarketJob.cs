// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc.Data.TarkovMarket
{
    using static TarkovDevCore;

    /// <summary>
    /// Fetches and serialises item/task/map data from the tarkov.dev GraphQL API
    /// into the same JSON shape that DEFAULT_DATA.json uses.
    /// </summary>
    internal static class TarkovMarketJob
    {
        public static async Task<string> GetUpdatedMarketDataAsync()
        {
            var data = await TarkovDevCore.QueryTarkovDevAsync();

            var result = new TarkovMarketData
            {
                Items = ParseItems(data),
                Tasks = data.Data.Tasks,
                Maps = ParseMaps(data),
                Traders = data.Data.Traders
                    .Where(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name))
                    .Select(t => new OutgoingTrader { Id = t.Id, Name = t.Name })
                    .ToList()
            };

            return JsonSerializer.Serialize(result);
        }

        private static List<OutgoingItem> ParseItems(TarkovDevQuery data)
        {
            var list = new List<OutgoingItem>(data.Data.Items.Count
                + data.Data.QuestItems.Count
                + data.Data.LootContainers.Count);

            foreach (var item in data.Data.Items)
            {
                list.Add(new OutgoingItem
                {
                    ID = item.Id,
                    Name = item.Name,
                    ShortName = item.ShortName,
                    TraderPrice = item.HighestVendorPrice,
                    BestTraderName = item.BestVendorName,
                    FleaPrice = item.OptimalFleaPrice,
                    Slots = item.Width * item.Height,
                    Categories = item.Categories.Select(c => c.Name).ToList(),
                    IconLink = item.IconLink,
                    IconLinkFallback = item.IconLinkFallback,
                    ImageLink = item.ImageLink,
                    Caliber = item.Properties?.Caliber
                });
            }

            foreach (var qi in data.Data.QuestItems)
            {
                list.Add(new OutgoingItem
                {
                    ID = qi.Id,
                    Name = $"Q_{qi.ShortName}",
                    ShortName = $"Q_{qi.ShortName}",
                    TraderPrice = -1,
                    FleaPrice = -1,
                    Slots = 1,
                    Categories = ["Quest Item"]
                });
            }

            foreach (var c in data.Data.LootContainers)
            {
                list.Add(new OutgoingItem
                {
                    ID = c.Id,
                    Name = c.NormalizedName,
                    ShortName = c.Name,
                    TraderPrice = -1,
                    FleaPrice = -1,
                    Slots = 1,
                    Categories = ["Static Container"]
                });
            }

            return list;
        }

        private static List<OutgoingMap> ParseMaps(TarkovDevQuery data) =>
            data.Data.Maps.Select(m => new OutgoingMap
            {
                Name = m.Name,
                NameId = m.NameId,
                Extracts = m.Extracts.Select(e => new OutgoingExtract
                {
                    Name = e.Name,
                    Faction = e.Faction,
                    Position = e.Position is not null
                        ? new OutgoingPosition { X = e.Position.X, Y = e.Position.Y, Z = e.Position.Z }
                        : null
                }).ToList(),
                Transits = m.Transits.Select(t => new OutgoingTransit
                {
                    Description = t.Description,
                    Position = t.Position is not null
                        ? new OutgoingPosition { X = t.Position.X, Y = t.Position.Y, Z = t.Position.Z }
                        : null
                }).ToList()
            }).ToList();

        #region Outgoing JSON types

        private sealed class TarkovMarketData
        {
            [JsonPropertyName("items")]
            public List<OutgoingItem> Items { get; set; } = [];

            [JsonPropertyName("tasks")]
            public List<TaskElement> Tasks { get; set; } = [];

            [JsonPropertyName("maps")]
            public List<OutgoingMap> Maps { get; set; } = [];

            [JsonPropertyName("traders")]
            public List<OutgoingTrader> Traders { get; set; } = [];
        }

        private sealed class OutgoingTrader
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }

        private sealed class OutgoingItem
        {
            [JsonPropertyName("bsgID")]
            public string ID { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("shortName")]
            public string ShortName { get; set; } = string.Empty;

            [JsonPropertyName("price")]
            public long TraderPrice { get; set; }

            [JsonPropertyName("traderName")]
            public string BestTraderName { get; set; } = string.Empty;

            [JsonPropertyName("fleaPrice")]
            public long FleaPrice { get; set; }

            [JsonPropertyName("slots")]
            public int Slots { get; set; }

            [JsonPropertyName("categories")]
            public List<string> Categories { get; set; } = [];

            [JsonPropertyName("iconLink")]
            public string? IconLink { get; set; }

            [JsonPropertyName("iconLinkFallback")]
            public string? IconLinkFallback { get; set; }

            [JsonPropertyName("imageLink")]
            public string? ImageLink { get; set; }

            [JsonPropertyName("caliber")]
            public string? Caliber { get; set; }
        }

        private sealed class OutgoingMap
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("nameId")]
            public string NameId { get; set; } = string.Empty;

            [JsonPropertyName("extracts")]
            public List<OutgoingExtract> Extracts { get; set; } = [];

            [JsonPropertyName("transits")]
            public List<OutgoingTransit> Transits { get; set; } = [];
        }

        private sealed class OutgoingExtract
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("faction")]
            public string Faction { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public OutgoingPosition? Position { get; set; }
        }

        private sealed class OutgoingTransit
        {
            [JsonPropertyName("description")]
            public string Description { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public OutgoingPosition? Position { get; set; }
        }

        private sealed class OutgoingPosition
        {
            [JsonPropertyName("x")] public float X { get; set; }
            [JsonPropertyName("y")] public float Y { get; set; }
            [JsonPropertyName("z")] public float Z { get; set; }
        }

        #endregion
    }
}
