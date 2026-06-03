// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Misc.Workers;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Quests
{
    /// <summary>
    /// Background lobby quest reader that polls the player's profile from TarkovApplication
    /// when NOT in a raid. Provides quest data to the Quest Panel while in the main menu/lobby.
    /// <para>
    /// Profile resolution is delegated to <see cref="LobbyProfileResolver"/>.
    /// </para>
    /// Automatically suspends while in raid (the in-raid QuestManager takes over).
    /// </summary>
    internal static class LobbyQuestReader
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private static WorkerThread? _worker;

        /// <summary>Caller-owned TarkovApplication behaviour cache slot.</summary>
        private static ulong _cachedObjectClass;

        /// <summary>
        /// The lobby QuestManager, valid when connected but not in a raid.
        /// Null when in raid (the in-raid QuestManager is used instead) or disconnected.
        /// </summary>
        public static QuestManager? QuestManager { get; private set; }

        /// <summary>
        /// Start the lobby quest reader background thread.
        /// </summary>
        internal static void Start()
        {
            if (_worker is not null)
                return;

            _worker = new WorkerThread
            {
                Name = "LobbyQuestReader",
                ThreadPriority = ThreadPriority.BelowNormal,
                SleepDuration = PollInterval,
                SleepMode = WorkerSleepMode.Default,
            };
            _worker.PerformWork += Tick;
            _worker.Start();
        }

        internal static void Stop()
        {
            var w = _worker;
            _worker = null;
            w?.Dispose();
        }

        /// <summary>Invalidate cached pointers. Called on game stop / process detach.</summary>
        internal static void InvalidateCache()
        {
            _cachedObjectClass = 0;
            QuestManager = null;
        }

        private static void Tick(CancellationToken ct)
        {
            try
            {
                // Suspend while in an active raid — the in-raid QuestManager takes over.
                // Hideout is treated like lobby: the profile is still accessible via TarkovApplication.
                if (!Memory.Ready || Memory.InRaid)
                {
                    if (Memory.InRaid)
                        QuestManager = null;
                    return;
                }

                var profilePtr = LobbyProfileResolver.Resolve(ref _cachedObjectClass);
                if (profilePtr == 0)
                    return;

                var qm = QuestManager;
                if (qm is null)
                {
                    qm = new QuestManager(profilePtr, "");
                    QuestManager = qm;
                    Log.WriteLine($"[LobbyQuestReader] QuestManager created — profile @ 0x{profilePtr:X}, " +
                        $"{qm.ActiveQuests.Count} active quests");
                }
                else
                {
                    qm.Refresh();
                }
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "lobby_quest_err", TimeSpan.FromSeconds(30),
                    $"[LobbyQuestReader] Error: {ex.Message}");
            }
        }
    }
}
