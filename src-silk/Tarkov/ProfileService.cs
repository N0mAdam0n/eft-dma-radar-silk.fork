// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Net;
using System.Net.Http;
using eft_dma_radar.Silk.Misc.Workers;

namespace eft_dma_radar.Silk.Tarkov
{
    /// <summary>
    /// Fetches player profiles from tarkov.dev and caches them per-session.
    /// Background worker thread polls for pending lookups.
    /// </summary>
    internal static class ProfileService
    {
        #region Constants

        private const string TarkovDevUrl = "https://players.tarkov.dev/profile/";
        private const int PollIntervalMs = 500;
        private const int RequestDelayMs = 1500;
        private const int RateLimitPauseMs = 60_000;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        #endregion

        #region State

        private static readonly ConcurrentDictionary<string, ProfileData?> _profiles = new(StringComparer.OrdinalIgnoreCase);
        private static WorkerThread? _worker;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        #endregion

        #region Init

        /// <summary>
        /// Start the background worker. Called when the game process is found.
        /// </summary>
        public static void Start()
        {
            if (_worker is not null)
                return;
            _worker = new WorkerThread
            {
                Name = "ProfileService",
                ThreadPriority = ThreadPriority.BelowNormal,
                SleepDuration = TimeSpan.FromMilliseconds(PollIntervalMs),
                SleepMode = WorkerSleepMode.Default,
            };
            _worker.PerformWork += Tick;
            _worker.Start();
            Log.WriteLine("[ProfileService] Started.");
        }

        /// <summary>
        /// Stop the background worker. Called when the game process is lost.
        /// </summary>
        public static void Stop()
        {
            var w = _worker;
            _worker = null;
            w?.Dispose();
            _profiles.Clear();
            Log.WriteLine("[ProfileService] Stopped.");
        }

        #endregion

        #region Public API

        /// <summary>
        /// Register an account ID for profile lookup.
        /// If the account ID is already registered, this is a no-op.
        /// </summary>
        public static void Register(string accountId)
        {
            if (string.IsNullOrEmpty(accountId) || accountId == "0")
                return;
            // Add with null value = pending lookup
            _profiles.TryAdd(accountId, null);
        }

        /// <summary>
        /// Try to get a cached profile for the given account ID.
        /// </summary>
        public static bool TryGetProfile(string accountId, out ProfileData profile)
        {
            if (_profiles.TryGetValue(accountId, out var data) && data is not null)
            {
                profile = data;
                return true;
            }
            profile = null!;
            return false;
        }

        #endregion

        #region Worker

        /// <summary>
        /// Single tick of the lookup loop. WorkerThread invokes this at the
        /// configured PollIntervalMs cadence; the inter-request delay is
        /// applied here by waiting on the cancellation handle after a fetch
        /// so a Stop() request wakes us immediately.
        /// </summary>
        private static void Tick(CancellationToken ct)
        {
            if (!SilkProgram.Config.ProfileLookups)
                return;

            string? pendingId = null;
            foreach (var kvp in _profiles)
            {
                if (kvp.Value is null)
                {
                    pendingId = kvp.Key;
                    break;
                }
            }

            if (pendingId is null)
                return;

            try
            {
                var profile = FetchProfile(pendingId, ct);
                if (profile is not null)
                {
                    _profiles[pendingId] = profile;
                    Log.WriteLine($"[ProfileService] Fetched profile for {pendingId}: {profile.Info?.Nickname ?? "?"}");
                }
                else
                {
                    // Mark as empty so we don't retry indefinitely
                    _profiles[pendingId] = ProfileData.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[ProfileService] Tick error: {ex.Message}");
            }

            // Rate-limit ourselves so we don't hammer tarkov.dev.
            // Cancellable: Stop() wakes us instantly.
            ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(RequestDelayMs));
        }

        private static ProfileData? FetchProfile(string accountId, CancellationToken stopCt)
        {
            try
            {
                var url = $"{TarkovDevUrl}{accountId}.json";

                // Use the shared pooled HttpClient (SocketsHttpHandler) instead of an owned
                // HttpClientHandler. Sync HttpClient.Send avoids an async-over-sync thread-pool
                // hop on this dedicated worker thread.
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("eft-dma-radar/1.0");

                // Link the per-request timeout with the worker's stop token so a Stop()
                // also aborts an in-flight HTTP request.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopCt);
                cts.CancelAfter(RequestTimeout);
                using var response = SilkProgram.HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Log.WriteLine("[ProfileService] Rate limited (429), pausing...");
                    // Cancellable pause — Stop() wakes us instantly.
                    stopCt.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(RateLimitPauseMs));
                    return null;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();

                using var stream = response.Content.ReadAsStream(cts.Token);
                var container = JsonSerializer.Deserialize<ProfileData>(stream, _jsonOptions);
                return container;
            }
            catch (HttpRequestException ex)
            {
                Log.WriteLine($"[ProfileService] HTTP error for {accountId}: {ex.Message}");
                return null;
            }
            catch (OperationCanceledException)
            {
                Log.WriteLine($"[ProfileService] Timeout for {accountId}");
                return null;
            }
            catch (JsonException ex)
            {
                Log.WriteLine($"[ProfileService] JSON parse error for {accountId}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region JSON Models

        /// <summary>
        /// Root profile data from tarkov.dev.
        /// </summary>
        public sealed class ProfileData
        {
            public static readonly ProfileData Empty = new();

            public ProfileInfo? Info { get; set; }
            public ProfileStats? PmcStats { get; set; }
            public ProfileStats? ScavStats { get; set; }
            public Dictionary<string, long>? Achievements { get; set; }

            /// <summary>Whether this profile has any meaningful data.</summary>
            [JsonIgnore]
            public bool HasData => Info is not null && Info.Nickname is not null;

            #region Computed Stats

            [JsonIgnore]
            public int Kills => GetOverallCounter(PmcStats, "Kills");

            [JsonIgnore]
            public int Deaths => GetOverallCounter(PmcStats, "Deaths");

            [JsonIgnore]
            public float KD => Deaths > 0 ? (float)Kills / Deaths : Kills;

            [JsonIgnore]
            public int Sessions => GetOverallCounter(PmcStats, "Sessions", "Pmc");

            [JsonIgnore]
            public int Survived => GetOverallCounter(PmcStats, "ExitStatus", "Survived", "Pmc");

            [JsonIgnore]
            public float SurvivedRate => Sessions > 0 ? (float)Survived / Sessions * 100f : 0f;

            [JsonIgnore]
            public int Hours => (int)((PmcStats?.Eft?.TotalInGameTime ?? 0) / 3600);

            [JsonIgnore]
            public int AchievementCount => Achievements?.Count ?? 0;

            [JsonIgnore]
            public string AccountType => Info?.MemberCategory switch
            {
                2 => "EOD",
                // Unheard: memberCategory 2 + prestige >= 1, OR dedicated UH flag
                _ when Info?.MemberCategory == 2 && (Info?.PrestigeLevel ?? 0) >= 1 => "UH",
                _ => "STD"
            };

            private static int GetOverallCounter(ProfileStats? stats, params string[] keyParts)
            {
                var items = stats?.Eft?.OverAllCounters?.Items;
                if (items is null)
                    return 0;

                foreach (var item in items)
                {
                    if (item.Key is null || item.Key.Count != keyParts.Length)
                        continue;

                    bool match = true;
                    for (int i = 0; i < keyParts.Length; i++)
                    {
                        if (!string.Equals(item.Key[i], keyParts[i], StringComparison.OrdinalIgnoreCase))
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match)
                        return item.Value;
                }
                return 0;
            }

            #endregion
        }

        public sealed class ProfileInfo
        {
            public string? Nickname { get; set; }
            public int Experience { get; set; }
            public int MemberCategory { get; set; }
            public int PrestigeLevel { get; set; }
        }

        public sealed class ProfileStats
        {
            public ProfileStatsEft? Eft { get; set; }
        }

        public sealed class ProfileStatsEft
        {
            public long TotalInGameTime { get; set; }
            public OverAllCountersContainer? OverAllCounters { get; set; }
        }

        public sealed class OverAllCountersContainer
        {
            public List<OverAllCounterItem>? Items { get; set; }
        }

        public sealed class OverAllCounterItem
        {
            public List<string>? Key { get; set; }
            public int Value { get; set; }
        }

        #endregion
    }
}
