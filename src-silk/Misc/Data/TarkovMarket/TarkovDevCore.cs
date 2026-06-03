// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Net.Http.Json;

namespace eft_dma_radar.Silk.Misc.Data.TarkovMarket
{
    internal static class TarkovDevCore
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<TarkovDevQuery> QueryTarkovDevAsync()
        {
            var query = new Dictionary<string, string>()
            {
                { "query",
                """
                {
                  maps {
                    name
                    nameId
                    extracts {
                      name
                      faction
                      position { x, y, z }
                    }
                    transits {
                      description
                      position { x, y, z }
                    }
                  }
                  items {
                    id
                    name
                    shortName
                    width
                    height
                    sellFor {
                      vendor {
                        name
                      }
                      priceRUB
                    }
                    basePrice
                    avg24hPrice
                    historicalPrices {
                      price
                    }
                    categories {
                      name
                    }
                    iconLink
                    iconLinkFallback
                    imageLink
                    properties {
                      ... on ItemPropertiesWeapon {
                        caliber
                      }
                    }
                  }
                  questItems {
                    id
                    shortName
                  }
                  lootContainers {
                    id
                    normalizedName
                    name
                  }
                  tasks {
                    id
                    name
                    trader {
                      name
                    }
                    kappaRequired
                    map {
                      id
                      normalizedName
                      name
                    }
                    objectives {
                      id
                      type
                      optional
                      description
                      maps {
                        id
                        name
                        normalizedName
                      }
                      ... on TaskObjectiveItem {
                        item {
                          id
                          name
                          shortName
                        }
                        zones {
                          id
                          map {
                            id
                            normalizedName
                            name
                          }
                          position { y x z }
                        }
                        requiredKeys {
                          id
                          name
                          shortName
                        }
                        count
                        foundInRaid
                      }
                      ... on TaskObjectiveMark {
                        id
                        description
                        markerItem {
                          id
                          name
                          shortName
                        }
                        maps {
                          id
                          normalizedName
                          name
                        }
                        zones {
                          id
                          map {
                            id
                            normalizedName
                            name
                          }
                          position { y x z }
                        }
                        requiredKeys {
                          id
                          name
                          shortName
                        }
                      }
                      ... on TaskObjectiveQuestItem {
                        id
                        description
                        requiredKeys {
                          id
                          name
                          shortName
                        }
                        maps {
                          id
                          normalizedName
                          name
                        }
                        zones {
                          id
                          map {
                            id
                            normalizedName
                            name
                          }
                          position { y x z }
                        }
                        questItem {
                          id
                          name
                          shortName
                          normalizedName
                          description
                        }
                        count
                      }
                      ... on TaskObjectiveBasic {
                        id
                        description
                        requiredKeys {
                          id
                          name
                          shortName
                        }
                        maps {
                          id
                          normalizedName
                          name
                        }
                        zones {
                          id
                          map {
                            id
                            normalizedName
                            name
                          }
                          position { y x z }
                        }
                      }
                      ... on TaskObjectiveShoot {
                        maps {
                          id
                          normalizedName
                          name
                        }
                        zones {
                          id
                          map {
                            id
                            normalizedName
                            name
                          }
                          outline { x y z }
                          position { y x z }
                        }
                      }
                    }
                    taskRequirements {
                      task {
                        id
                      }
                      status
                    }
                  }
                  traders {
                    id
                    name
                  }
                }
                """
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            using var response = await SilkProgram.HttpClient.PostAsJsonAsync(
                requestUri: "https://api.tarkov.dev/graphql",
                value: query,
                cancellationToken: cts.Token);
            response.EnsureSuccessStatusCode();
            return await JsonSerializer.DeserializeAsync<TarkovDevQuery>(
                await response.Content.ReadAsStreamAsync(), _jsonOptions);
        }

        // ── Response types ───────────────────────────────────────────────────

        internal sealed class TarkovDevQuery
        {
            [JsonPropertyName("data")]
            public TarkovDevData Data { get; set; } = new();
        }

        internal sealed class TarkovDevData
        {
            [JsonPropertyName("maps")]
            public List<ApiMapElement> Maps { get; set; } = [];

            [JsonPropertyName("items")]
            public List<ApiItemElement> Items { get; set; } = [];

            [JsonPropertyName("questItems")]
            public List<ApiQuestItemElement> QuestItems { get; set; } = [];

            [JsonPropertyName("lootContainers")]
            public List<ApiContainerElement> LootContainers { get; set; } = [];

            [JsonPropertyName("tasks")]
            public List<TaskElement> Tasks { get; set; } = [];

            [JsonPropertyName("traders")]
            public List<ApiTraderElement> Traders { get; set; } = [];
        }

        internal sealed class ApiMapElement
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("nameId")]
            public string NameId { get; set; } = string.Empty;

            [JsonPropertyName("extracts")]
            public List<ApiExtractElement> Extracts { get; set; } = [];

            [JsonPropertyName("transits")]
            public List<ApiTransitElement> Transits { get; set; } = [];
        }

        internal sealed class ApiExtractElement
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("faction")]
            public string Faction { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public ApiPositionElement? Position { get; set; }
        }

        internal sealed class ApiTransitElement
        {
            [JsonPropertyName("description")]
            public string Description { get; set; } = string.Empty;

            [JsonPropertyName("position")]
            public ApiPositionElement? Position { get; set; }
        }

        internal sealed class ApiPositionElement
        {
            [JsonPropertyName("x")] public float X { get; set; }
            [JsonPropertyName("y")] public float Y { get; set; }
            [JsonPropertyName("z")] public float Z { get; set; }
        }

        internal sealed class ApiItemElement
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("shortName")]
            public string ShortName { get; set; } = string.Empty;

            [JsonPropertyName("width")]
            public int Width { get; set; }

            [JsonPropertyName("height")]
            public int Height { get; set; }

            [JsonPropertyName("basePrice")]
            public long BasePrice { get; set; }

            [JsonPropertyName("avg24hPrice")]
            public long? Avg24HPrice { get; set; }

            [JsonPropertyName("categories")]
            public List<ApiNameElement> Categories { get; set; } = [];

            [JsonPropertyName("sellFor")]
            public List<ApiSellForElement> SellFor { get; set; } = [];

            [JsonPropertyName("historicalPrices")]
            public List<ApiHistoricalPrice> HistoricalPrices { get; set; } = [];

            [JsonPropertyName("iconLink")]
            public string IconLink { get; set; } = string.Empty;

            [JsonPropertyName("iconLinkFallback")]
            public string IconLinkFallback { get; set; } = string.Empty;

            [JsonPropertyName("imageLink")]
            public string ImageLink { get; set; } = string.Empty;

            [JsonPropertyName("properties")]
            public ApiItemProperties? Properties { get; set; }

            [JsonIgnore]
            public long HighestVendorPrice => SellFor
                .Where(x => x.Vendor?.Name != null && x.Vendor.Name != "Flea Market" && x.PriceRub.HasValue)
                .Select(x => x.PriceRub!.Value)
                .DefaultIfEmpty()
                .Max();

            [JsonIgnore]
            public string BestVendorName => SellFor
                .Where(x => x.Vendor?.Name != null && x.Vendor.Name != "Flea Market" && x.PriceRub.HasValue)
                .OrderByDescending(x => x.PriceRub)
                .Select(x => x.Vendor!.Name)
                .FirstOrDefault() ?? string.Empty;

            [JsonIgnore]
            public long OptimalFleaPrice
            {
                get
                {
                    if (BasePrice == 0) return 0;
                    if (Avg24HPrice is long avg && FleaTax.Calculate(avg, BasePrice) < avg)
                        return avg;
                    return (long)(HistoricalPrices
                        .Where(x => x.Price is long p && FleaTax.Calculate(p, BasePrice) < p)
                        .Select(x => x.Price!.Value)
                        .DefaultIfEmpty()
                        .Average());
                }
            }
        }

        internal sealed class ApiSellForElement
        {
            [JsonPropertyName("priceRUB")]
            public long? PriceRub { get; set; }

            [JsonPropertyName("vendor")]
            public ApiNameElement? Vendor { get; set; }
        }

        internal sealed class ApiHistoricalPrice
        {
            [JsonPropertyName("price")]
            public long? Price { get; set; }
        }

        internal sealed class ApiNameElement
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }

        internal sealed class ApiItemProperties
        {
            [JsonPropertyName("caliber")]
            public string? Caliber { get; set; }
        }

        internal sealed class ApiQuestItemElement
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("shortName")]
            public string ShortName { get; set; } = string.Empty;
        }

        internal sealed class ApiContainerElement
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("normalizedName")]
            public string NormalizedName { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }

        internal sealed class ApiTraderElement
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }
    }
}
