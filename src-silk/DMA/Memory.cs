// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using eft_dma_radar.Silk.DMA.ScatterAPI;
using eft_dma_radar.Silk.Tarkov.GameWorld.Interactables;
using eft_dma_radar.Silk.Tarkov.Hideout;
using eft_dma_radar.Silk.Tarkov.Unity;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;
using GameObjectManager = eft_dma_radar.Silk.Tarkov.Unity.GOM;
using VmmSharpEx;
using VmmSharpEx.Extensions;
using VmmSharpEx.Options;
using VmmSharpEx.Refresh;
using VmmSharpEx.Scatter;

namespace eft_dma_radar.Silk.DMA
{
    /// <summary>
    /// Standalone DMA Memory module for the Silk.NET radar.
    /// Minimal dependencies, self-contained.
    /// </summary>
    internal static class Memory
    {
        #region Constants / Fields

        private const string ProcessName = "EscapeFromTarkov.exe";
        private const string MemMapFile = "mmap.txt";
        public const uint MAX_READ_SIZE = 0x1000u * 1500u;

        private static Vmm? _vmm;
        private static uint _pid;
        private static readonly Lock _restartLock = new();
        private static CancellationTokenSource _cts = new();
        private static volatile bool _shutdown;
        private static Thread? _workerThread;

        private static MemoryState _state = MemoryState.NotStarted;

        /// <summary>
        /// Optional UI notification callback. Set by RadarWindow after init.
        /// Thread-safe: invoked from the memory worker thread.
        /// </summary>
        public static Action<string, NotificationLevel>? ShowNotification;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static VmmFlags ToFlags(bool useCache) =>
            useCache ? VmmFlags.NONE : VmmFlags.NOCACHE;

        /// <summary>Returns the active VMM handle or throws if disposed.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vmm VmmOrThrow() =>
            _vmm ?? throw new ObjectDisposedException(nameof(Memory));

        #endregion

        #region Public State

        public static MemoryState State => _state;
        /// <summary>The active VMM handle, or <see langword="null"/> if not yet initialized.</summary>
        internal static Vmm? VmmHandle => _vmm;
        public static ulong UnityBase { get; private set; }
        public static ulong GOM { get; private set; }
        public static ulong GameAssemblyBase { get; private set; }

        /// <summary>
        /// UnityPlayer.dll FileVersion from its PE version resource, snapshotted
        /// at module load. Used by the PhysX snapshot fingerprint so a Unity
        /// engine bump auto-invalidates the on-disk scene cache. Empty if the
        /// version resource was unreadable.
        /// </summary>
        public static string UnityPlayerVersion { get; private set; } = string.Empty;
        public static bool Ready => _state is MemoryState.ProcessFound or MemoryState.InRaid or MemoryState.InHideout;
        public static bool InRaid => _state is MemoryState.InRaid;
        public static bool InHideout => _state is MemoryState.InHideout;

        /// <summary>Hideout manager instance — persists across hideout entries.</summary>
        public static HideoutManager Hideout { get; } = new();

        /// <summary>Persistent player history — tracks PMCs seen across sessions.</summary>
        public static PlayerHistory PlayerHistory { get; } = new();

        /// <summary>Manual player watchlist — persists user-tagged players across sessions.</summary>
        public static PlayerWatchlist PlayerWatchlist { get; } = new();

        public static LocalGameWorld? Game { get; private set; }
        public static string? MapID => Game?.MapID;
        public static RegisteredPlayers? Players => Game?.RegisteredPlayers;
        public static Player? LocalPlayer => Game?.LocalPlayer;
        public static IReadOnlyList<LootItem>? Loot => Game?.Loot;
        public static IReadOnlyList<LootCorpse>? Corpses => Game?.Corpses;
        public static IReadOnlyList<LootContainer>? Containers => Game?.Containers;
        public static IReadOnlyList<LootAirdrop>? Airdrops => Game?.Airdrops;
        public static IReadOnlyList<Exfil>? Exfils => Game?.Exfils;
        public static IReadOnlyList<TransitPoint>? Transits => Game?.Transits;
        public static IReadOnlyList<Door>? Doors => Game?.Doors;
        public static IReadOnlyList<QuestLocation>? QuestLocations => Game?.QuestLocations;
        public static eft_dma_radar.Silk.Tarkov.GameWorld.Quests.QuestManager? QuestManager => Game?.QuestManager ?? LobbyQuestReader.QuestManager;
        public static eft_dma_radar.Silk.Tarkov.GameWorld.Profile.WishlistManager? WishlistManager => Game?.WishlistManager;
        public static eft_dma_radar.Silk.Tarkov.GameWorld.Explosives.ExplosivesManager? Explosives => Game?.Explosives;
        public static eft_dma_radar.Silk.Tarkov.GameWorld.Explosives.PredictedArc? InHandGrenadePrediction => Game?.Explosives?.InHandPrediction;
        public static eft_dma_radar.Silk.Tarkov.GameWorld.Btr.BtrTracker? Btr => Game?.Btr;
        public static IReadOnlyList<eft_dma_radar.Silk.Tarkov.GameWorld.Interactables.Switch>? Switches => Game?.Switches;
        #endregion

        #region Events

        /// <summary>Raised when the game process is found and ready.</summary>
        public static event EventHandler<EventArgs>? GameStarted;
        /// <summary>Raised when the game process is no longer running.</summary>
        public static event EventHandler<EventArgs>? GameStopped;
        /// <summary>Raised when a raid begins.</summary>
        public static event EventHandler<EventArgs>? RaidStarted;
        /// <summary>Raised when a raid ends.</summary>
        public static event EventHandler<EventArgs>? RaidStopped;
        /// <summary>Raised when the player enters the hideout.</summary>
        public static event EventHandler<EventArgs>? HideoutEntered;
        /// <summary>Raised when the player leaves the hideout.</summary>
        public static event EventHandler<EventArgs>? HideoutExited;

        private static void OnGameStarted()
        {
            SetState(MemoryState.ProcessFound);
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            LobbyQuestReader.Start();
            eft_dma_radar.Silk.Tarkov.QuestPlanner.QuestPlannerWorker.Start();
            GameStarted?.Invoke(null, EventArgs.Empty);
        }

        private static void OnGameStopped()
        {
            SetState(MemoryState.WaitingForProcess);
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            UnityBase = default;
            GOM = default;
            GameAssemblyBase = default;
            _pid = default;
            GameObjectManager.ResetCachedAddresses();
            MatchingProgressResolver.Reset();
            Hideout.Reset();
            KillfeedManager.Reset();
            LobbyQuestReader.InvalidateCache();
            eft_dma_radar.Silk.Tarkov.QuestPlanner.QuestPlannerWorker.InvalidateCache();
            GameStopped?.Invoke(null, EventArgs.Empty);
        }

        private static void OnRaidStarted()
        {
            SetState(MemoryState.InRaid);
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            RaidStarted?.Invoke(null, EventArgs.Empty);
        }

        private static void OnRaidStopped()
        {
            if (_state is MemoryState.InRaid or MemoryState.InHideout)
                SetState(MemoryState.ProcessFound);
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            Game = null;
            LootFilter.ClearCaches();
            RaidStopped?.Invoke(null, EventArgs.Empty);
        }

        private static void OnHideoutEntered()
        {
            SetState(MemoryState.InHideout);
            HideoutEntered?.Invoke(null, EventArgs.Empty);
        }

        private static void OnHideoutExited()
        {
            if (_state is MemoryState.InHideout)
                SetState(MemoryState.ProcessFound);
            Game = null;
            Hideout.InvalidatePointers();
            HideoutExited?.Invoke(null, EventArgs.Empty);
        }

        private static void SetState(MemoryState s)
        {
            _state = s;
            Log.WriteLine($"[Memory] State → {s}");
        }

        #endregion

        #region Init

        /// <summary>
        /// Initialize the DMA layer. Called once from <see cref="SilkProgram.Main"/>.
        /// </summary>
        public static void ModuleInit(SilkConfig config)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                Log.WriteLine("[Memory] Running on non-Windows (Linux dev mode). DMA features are disabled. UI and non-DMA subsystems can still be tested.");
                // Leave Game, Players, etc. as null so the UI can render "waiting" states and allow panel testing.
                return;
            }

            Log.WriteLine("[Memory] Initializing DMA...");

            // Pre-flight: ensure the critical native LeechCore/MemProcFS DLLs are present next to the executable.
            // If these are missing the Vmm ctor will fail in confusing ways (or hang). This commonly happens
            // when accidentally launching the .exe straight from a source tree instead of a build output folder.
            string baseDir = AppContext.BaseDirectory;
            string vmmPath = Path.Combine(baseDir, "vmm.dll");
            string lcPath = Path.Combine(baseDir, "leechcore.dll");

            string vmmVer = "unknown";
            string lcVer = "unknown";

            try
            {
                if (!File.Exists(vmmPath))
                    throw new FileNotFoundException("vmm.dll not found next to executable.", vmmPath);
                if (!File.Exists(lcPath))
                    throw new FileNotFoundException("leechcore.dll not found next to executable.", lcPath);

                vmmVer = FileVersionInfo.GetVersionInfo(vmmPath).FileVersion ?? "unknown";
                lcVer = FileVersionInfo.GetVersionInfo(lcPath).FileVersion ?? "unknown";
                Log.WriteLine($"[Memory] Native libs OK. vmm.dll={vmmVer}  leechcore.dll={lcVer}");
            }
            catch (Exception preEx)
            {
                throw new InvalidOperationException(
                    "【关键文件缺失】\n" +
                    $"无法找到 vmm.dll 或 leechcore.dll：{preEx.Message}\n\n" +
                    "解决办法：\n" +
                    "- 不要直接双击 src 目录下的 exe。\n" +
                    "- 使用 dotnet build / Visual Studio 完整编译后，从输出目录（bin\\Debug\\... 或 publish 输出）运行 exe。\n" +
                    "- 或者从干净的 zip 包重新解压 + 构建。\n" +
                    "构建时 VmmSharpEx 项目会自动把 lib\\VmmSharpEx\\native\\ 下的 dll 复制到输出目录。", preEx);
            }

            Log.WriteLine($"[Memory] DeviceStr=\"{config.DeviceStr}\", MemMapEnabled={config.MemMapEnabled}, vmm.dll={vmmVer}, leechcore.dll={lcVer}");

            // Support "-nomemmap" (or "--nomemmap") on command line to skip the map generation step for this run only.
            // This is useful for troubleshooting: lets you test whether basic DMA device connection (the main Vmm creation)
            // succeeds even if the full physical memory map scan is slow or problematic on your specific card/setup.
            // Still performs real DMA hardware connection + process attach. Does NOT enable any offline mode.
            string[] cmd = Environment.GetCommandLineArgs() ?? [];
            bool skipMemMapGen = cmd.Any(a => a.Equals("-nomemmap", StringComparison.OrdinalIgnoreCase) ||
                                              a.Equals("--nomemmap", StringComparison.OrdinalIgnoreCase));
            if (skipMemMapGen)
                Log.WriteLine("[Memory] -nomemmap flag detected: will skip map generation even if mmap.txt is missing (still uses real DMA).");

            var baseArgs = new List<string>(["-norefresh", "-device", config.DeviceStr, "-waitinitialize"]);

            try
            {
                bool memMapReady = false;
                if (config.MemMapEnabled && !File.Exists(MemMapFile) && !skipMemMapGen)
                {
                    Log.WriteLine("[Memory] No MemMap, generating...（首次运行或删除了 mmap.txt 时需要用真实DMA硬件对游戏端内存做一次完整扫描来生成 mmap.txt。此步骤在不同DMA卡/链路速度下可能需要 30 秒 ~ 3 分钟，请耐心等待，并确保游戏端PC已开机、DMA卡已正确连接）");

                    try
                    {
                        // The Vmm ctor + GetMemoryMap can block for a long time on slow/first-time DMA links or large RAM targets.
                        // We wrap in Task + long timeout so the user gets a clear actionable error instead of the process appearing hung forever.
                        var genCts = new CancellationTokenSource();
                        var genTask = Task.Run(() =>
                        {
                            using var tmpVmm = new Vmm([.. baseArgs]);
                            tmpVmm.GetMemoryMap(applyMap: true, outputFile: MemMapFile);
                        }, genCts.Token);

                        // Generous timeout for real hardware: map generation legitimately walks physical memory and can be slow.
                        bool completed = genTask.Wait(TimeSpan.FromSeconds(180));
                        if (!completed)
                        {
                            genCts.Cancel();
                            Log.WriteLine("[Memory] 内存映射生成超时（180秒）。");
                            throw new TimeoutException("MemMap generation timed out after 180s. DMA device responded too slowly or link is unstable.");
                        }

                        if (File.Exists(MemMapFile))
                        {
                            memMapReady = true;
                            Log.WriteLine("[Memory] MemMap generated successfully to mmap.txt (subsequent starts will be faster)");
                        }
                        else
                        {
                            Log.WriteLine("[Memory] MemMap generation reported success but file not found on disk.");
                        }
                    }
                    catch (Exception genEx)
                    {
                        Log.WriteLine($"[Memory] 内存映射生成失败: {genEx.Message}");
                        Log.WriteLine("[Memory] 将继续尝试不使用 -memmap 进行主初始化（首次附加到游戏进程可能会慢一些）。下次启动前可手动删除 mmap.txt 让它重试生成，或者使用 -nomemmap 命令行参数跳过。");
                        memMapReady = false;
                    }
                }
                else if (config.MemMapEnabled && File.Exists(MemMapFile))
                {
                    memMapReady = true;
                    if (skipMemMapGen)
                        Log.WriteLine("[Memory] mmap.txt exists but -nomemmap was given; NOT adding -memmap for this run.");
                }
                else if (skipMemMapGen)
                {
                    Log.WriteLine("[Memory] Skipping memmap generation due to -nomemmap (no mmap.txt will be created/used this run).");
                }

                var args = new List<string>(baseArgs);
                if (memMapReady)
                    args.AddRange(["-memmap", MemMapFile]);

                Log.WriteLine($"[Memory] Creating main VMM (device connection). args: {string.Join(" ", args)}");

                // The main device connection (with -waitinitialize) is also wrapped. Some cards take a while to enumerate / handshake.
                var mainCts = new CancellationTokenSource();
                Vmm? mainVmm = null;
                var mainTask = Task.Run(() =>
                {
                    mainVmm = new Vmm([.. args]);
                }, mainCts.Token);

                // Main attach timeout also generous for real hardware that is just slow to init the link.
                bool mainOk = mainTask.Wait(TimeSpan.FromSeconds(90));
                if (!mainOk)
                {
                    mainCts.Cancel();
                    Log.WriteLine("[Memory] 主VMM/设备连接超时（90秒）。");
                    throw new TimeoutException("Main VMM creation timed out waiting for DMA device (90s).");
                }
                _vmm = mainVmm!;

                // Benchmark BEFORE registering auto-refresh so the PCIe bus is idle
                // during measurement. RegisterAutoRefresh drives continuous TLP traffic
                // that would starve concurrent LeechCore.Read calls.
                RunThroughputBenchmark(_vmm);

                _vmm.RegisterAutoRefresh(RefreshOption.MemoryPartial, TimeSpan.FromMilliseconds(300));
                _vmm.RegisterAutoRefresh(RefreshOption.TlbPartial, TimeSpan.FromSeconds(2));

                SetState(MemoryState.WaitingForProcess);

                _workerThread = new Thread(MemoryWorker) { IsBackground = true, Name = "MemoryWorker" };
                _workerThread.Start();

                Log.WriteLine("[Memory] DMA initialized OK. (real hardware path)");
            }
            catch (Exception ex)
            {
                string zhMsg =
                    "DMA 初始化失败！\n" +
                    $"原因: {ex.Message}\n" +
                    $"vmm.dll 版本: {vmmVer}   leechcore.dll 版本: {lcVer}\n" +
                    $"设备参数 DeviceStr: \"{config.DeviceStr}\"\n\n" +
                    "【重要】此版本为纯只读DMA雷达，【必须】使用真实DMA硬件卡 + 游戏端电脑才能工作。\n" +
                    "排查与解决步骤（请严格按顺序操作）：\n" +
                    "1. 【必须】用鼠标右键点击 eft-dma-radar.exe → “以管理员身份运行”。\n" +
                    "2. 确认DMA硬件卡（常见FPGA卡等）驱动已正确安装，Windows设备管理器中该设备显示正常、无黄色叹号、无禁用。\n" +
                    "3. 检查DMA数据线（USB/雷电/专用DMA线）两端都插紧、没有松动。游戏端电脑必须已开机通电。\n" +
                    "4. 游戏端电脑上《逃离塔科夫》进程（EscapeFromTarkov.exe）最好已经启动（这样DMA能更快看到Unity内存）。\n" +
                    "5. 第一次在该电脑/卡/线缆组合上使用，或中途换过硬件：删除当前目录下的 mmap.txt 和整个 Symbols 文件夹，然后重试。\n" +
                    "6. 默认 DeviceStr = \"fpga\" 。很多FPGA卡用这个就行；如果你的卡需要特殊参数（例如带 //bar=xx 或 algo= 的），请编辑同目录下的 config.json，修改 \"deviceStr\" 的值，保存后重新运行程序。\n" +
                    "7. 【强烈建议】在启动本雷达【之前】，先用独立的DMA测试工具（LeechCore自带测试、MemProcFS命令行、或你买卡时配套的测试程序）确认“能正常连接”并能读取到游戏端内存，再来启动雷达。\n" +
                    "8. 重启两台电脑 + 拔插一次DMA线缆，有时PCIe/驱动总线会进入坏状态。\n" +
                    "9. 仍然卡住？在命令行用 -nomemmap 参数启动（例如 eft-dma-radar.exe -nomemmap），这会跳过内存映射生成步骤，直接测试基础DMA设备连接是否能成功。\n\n" +
                    "生成内存映射（mmap.txt）只在第一次或文件不存在时做一次，成功后后面启动会明显变快。\n" +
                    "你说“有DMA硬件卡并且可以正常连接”，但雷达卡在“No MemMap, generating...”或设备连接步，99% 是上面 1-8 其中一条没满足，或者你的卡在全量物理内存扫描时特别慢导致超时。\n" +
                    "如果用 -nomemmap 后依然卡住或报错，请把完整错误信息（包括你卡的具体型号）告诉我。";

                string enMsg =
                    "DMA Initialization Failed!\n" +
                    $"Reason: {ex.Message}\n" +
                    $"vmm: {vmmVer}  leechcore: {lcVer}\n" +
                    $"DeviceStr: \"{config.DeviceStr}\"\n\n" +
                    "READ-ONLY DMA radar — real hardware card + game PC required.\n" +
                    "Troubleshooting (in order):\n" +
                    "1. Right-click exe → Run as administrator.\n" +
                    "2. DMA card driver OK in Device Manager (no errors).\n" +
                    "3. Cable firmly connected on both ends; game PC powered on.\n" +
                    "4. EscapeFromTarkov.exe running on game side.\n" +
                    "5. New setup/hardware change? Delete mmap.txt + Symbols folder.\n" +
                    "6. Change deviceStr in config.json if your card needs non-default value.\n" +
                    "7. Test link first with standalone LeechCore/MemProcFS tools.\n" +
                    "8. Reboot both PCs and re-seat the cable.\n" +
                    "9. Try running with command-line flag -nomemmap to bypass the map generation step for diagnosis.\n\n" +
                    "If you confirm the card 'connects normally' in other tools but this still fails at the map or connect step, the issue is almost always one of the above or the first full memory map scan taking >3 minutes on your link.";

                throw new InvalidOperationException(zhMsg + "\n\n" + enMsg, ex);
            }
        }

        #endregion

        #region Worker

        /// <summary>
        /// Synchronous physical-memory throughput benchmark. Mirrors the DMA test tool
        /// approach exactly: iterate all 4 KB physical pages, find ones with ≥16 MB
        /// contiguous after them, shuffle, then time raw LeechCore.Read calls.
        /// Must be called before <see cref="Vmm.RegisterAutoRefresh"/>.
        /// Stores the result in <see cref="DmaStats.MaxThroughputMBps"/>.
        /// </summary>
        private static void RunThroughputBenchmark(Vmm vmm)
        {
            try
            {
                const uint ChunkSize  = 0x1000000u; // 16 MB — matches ThroughputTest
                const int  DurationMs = 3000;

                // ── Build page list exactly like DmaConnection.GetMemoryMap() ────────
                var physMap = vmm.Map_GetPhysMem();
                if (physMap is null || physMap.Length == 0)
                {
                    Log.WriteLine("[Memory] Throughput benchmark: no physical memory map.");
                    return;
                }

                var candidates = new List<ulong>(512);
                foreach (var section in physMap)
                {
                    for (ulong p = section.pa, remaining = section.cb;
                         remaining > 0x1000;
                         p += 0x1000, remaining -= 0x1000)
                    {
                        if (remaining >= ChunkSize)
                            candidates.Add(p);
                    }
                }

                if (candidates.Count == 0)
                {
                    Log.WriteLine("[Memory] Throughput benchmark: no 16 MB contiguous pages found.");
                    return;
                }

                // Shuffle so we don't always hit the same cached region.
                Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates));
                ulong pa = candidates[0];

                Log.WriteLine($"[Memory] Throughput benchmark: reading 16 MB chunks from PA 0x{pa:X} for {DurationMs / 1000} s...");

                long totalReads = 0, failedReads = 0;
                var sw = Stopwatch.StartNew();

                unsafe
                {
                    void* buf = NativeMemory.AlignedAlloc(ChunkSize, 0x1000);
                    try
                    {
                        while (sw.ElapsedMilliseconds < DurationMs)
                        {
                            if (!vmm.LeechCore.Read(pa, (IntPtr)buf, ChunkSize))
                                failedReads++;
                            totalReads++;
                        }
                    }
                    finally
                    {
                        NativeMemory.AlignedFree(buf);
                    }
                }

                sw.Stop();
                long ok   = totalReads - failedReads;
                float mbps = (float)(ok * ChunkSize / 1_048_576.0 / sw.Elapsed.TotalSeconds);
                DmaStats.SetMaxThroughput(mbps);
                Log.WriteLine($"[Memory] Throughput benchmark: {mbps:F1} MB/s ({ok}/{totalReads} reads OK in {sw.Elapsed.TotalSeconds:F1} s)");
            }
            catch (Exception ex)
            {
                Log.Write(AppLogLevel.Warning, $"[Memory] Throughput benchmark failed: {ex.Message}");
            }
        }

        private static void MemoryWorker()
        {
            Log.WriteLine("[Memory] Worker thread started.");

            while (!_shutdown)
            {
                try
                {
                    RunStartupLoop();
                    if (_shutdown) break;
                    OnGameStarted();
                    RunGameLoop();
                    OnGameStopped();
                }
                catch (OperationCanceledException) when (_shutdown)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_shutdown) break;
                    Log.Write(AppLogLevel.Error, $"FATAL on memory thread: {ex}");
                    Notify("FATAL error on memory thread — restarting", NotificationLevel.Error);
                    OnGameStopped();
                    Thread.Sleep(1000);
                }
            }

            Log.WriteLine("[Memory] Worker thread exiting.");
        }

        #endregion

        #region Startup Loop

        private static void RunStartupLoop()
        {
            Log.WriteLine("[Memory] Waiting for game process...");
            SetState(MemoryState.WaitingForProcess);
            var cooldown = Stopwatch.StartNew();

            while (!_shutdown)
            {
                try
                {
                    if (cooldown.ElapsedMilliseconds >= 3000)
                    {
                        FullRefresh();
                        cooldown.Restart();
                    }

                    LoadProcess();

                    // Retry module loading — game may still be initializing
                    bool modulesReady = false;
                    for (int i = 0; i < 10; i++)
                    {
                        try
                        {
                            if (i > 0)
                            {
                                FullRefresh();
                                cooldown.Restart();
                            }
                            LoadModules();
                            modulesReady = true;
                            break;
                        }
                        catch
                        {
                            if (i == 0)
                                Log.WriteLine("[Memory] Process found, waiting for modules...");
                            Thread.Sleep(1000);
                        }
                    }

                    if (!modulesReady)
                        throw new Exception("Modules failed to load after retries.");

                    // Wait for EFT's IL2CPP runtime to finish initializing the TypeInfoTable.
                    // The game populates it a few seconds after its modules load; probing it
                    // directly is the most reliable guard.  We check using the hardcoded
                    // fallback RVA — if it resolves to a valid table pointer the runtime is
                    // ready.  Cap at 60 s; Il2CppDumper has its own 30-retry loop as backup.
                    WaitForTypeInfoTable();

                    eft_dma_radar.Silk.Tarkov.Unity.IL2CPP.Il2CppDumper.Dump();
                    // NOTE: CameraManager.Initialize() (AllCameras sig-scan + camera_offsets.json)
                    // is intentionally deferred to Phase 4 (Aimview). Not needed for Phase 1.

                    SetState(MemoryState.Initializing);
                    Log.WriteLine("[Memory] Game startup OK.");
                    Notify("Game startup OK", NotificationLevel.Info);
                    return;
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[Memory] Startup failed: {ex.Message}");
                    OnGameStopped();
                    Thread.Sleep(1000);
                }
            }
        }

        #endregion

        #region Game Loop

        private static void RunGameLoop()
        {
            while (!_shutdown)
            {
                try
                {
                    var ct = _cts.Token;

                    using var game = Game = LocalGameWorld.Create(ct);

                    if (game.IsHideout)
                    {
                        if (SilkProgram.Config.HideoutEnabled)
                        {
                            Log.WriteLine("[Memory] Entered hideout.");
                            RunHideoutLoop(game, ct);
                        }
                        else
                        {
                            Log.WriteLine("[Memory] Hideout detected but disabled by config — skipping.");
                        }
                        continue;
                    }

                    Log.WriteLine($"[Memory] Raid started. Map = '{game.MapID}'");
                    OnRaidStarted();
                    game.Start();

                    while (game.InRaid)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (_restartRequested)
                        {
                            _restartRequested = false;
                            RequestRestart();
                            break;
                        }

                        // Check if game process is still alive (expensive, rate-limited)
                        Thread.Sleep(133);
                    }

                    if (!game.InRaid)
                        Log.WriteLine("[Memory] Raid ended (player extracted, died, or left).");
                }
                catch (OperationCanceledException)
                {
                    if (_shutdown) break;
                    Log.WriteLine("[Memory] Radar restart requested.");
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break; // VMM handle disposed — shutdown in progress
                }
                catch (GameNotRunningException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_shutdown) break;
                    Log.WriteLine($"[Memory] CRITICAL in game loop: {ex}");
                    Notify("CRITICAL error in game loop", NotificationLevel.Error);
                    break;
                }
                finally
                {
                    OnRaidStopped();
                    if (!_shutdown)
                        Thread.Sleep(100);
                }
            }

            if (!_shutdown)
            {
                Log.WriteLine("[Memory] Game is no longer running.");
                Notify("Game is no longer running", NotificationLevel.Warning);
            }
        }

        /// <summary>
        /// Lightweight loop for hideout mode. No worker threads, no loot/player tracking.
        /// Automatically refreshes the HideoutManager, then polls until the GameWorld
        /// changes (player queued for raid or returned to main menu).
        /// </summary>
        private static void RunHideoutLoop(LocalGameWorld game, CancellationToken ct)
        {
            OnHideoutEntered();

            try
            {
                // Auto-refresh hideout data on entry (if enabled)
                if (SilkProgram.Config.HideoutAutoRefresh)
                {
                    try
                    {
                        var status = Hideout.RefreshAll();
                        Log.WriteLine($"[Memory] Hideout auto-refresh: {status}");
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine($"[Memory] Hideout auto-refresh failed: {ex.Message}");
                    }
                }
                else
                {
                    Log.WriteLine("[Memory] Hideout auto-refresh disabled by config.");
                }

                // Poll until hideout GameWorld is no longer valid
                int validityTick = 0;
                while (!_shutdown)
                {
                    ct.ThrowIfCancellationRequested();

                    if (_restartRequested)
                    {
                        _restartRequested = false;
                        RequestRestart();
                        break;
                    }

                    // Every 4th tick (~2s) verify the hideout GameWorld is still alive.
                    // When the player queues for a raid or returns to the main menu,
                    // the hideout scene is torn down and MainPlayer becomes invalid.
                    if ((++validityTick & 0x3) == 0 && !game.IsHideoutAlive())
                    {
                        Log.WriteLine("[Memory] Hideout GameWorld no longer valid.");
                        break;
                    }

                    Thread.Sleep(500);
                }

                Log.WriteLine("[Memory] Left hideout.");
            }
            finally
            {
                OnHideoutExited();
            }
        }

        private static volatile bool _restartRequested;

        public static bool RestartRadar
        {
            set { if (InRaid || InHideout) _restartRequested = value; }
        }

        #endregion

        #region Restart

        /// <summary>
        /// Issue a CancellationToken swap to break the current game loop iteration.
        /// </summary>
        public static void RequestRestart()
        {
            // Allow re-detection of the same GameWorld on user-initiated restart
            LocalGameWorld.ClearStaleGuard();
            lock (_restartLock)
            {
                var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
                old.Cancel();
                old.Dispose();
            }
        }

        #endregion

        #region Process / Module Loading

        private static void LoadProcess()
        {
            if (!VmmOrThrow().PidGetFromName(ProcessName, out uint pid))
                throw new Exception($"Process '{ProcessName}' not found.");
            _pid = pid;
        }

        private static void LoadModules()
        {
            var vmm = VmmOrThrow();
            var unityBase = vmm.ProcessGetModuleBase(_pid, "UnityPlayer.dll");
            ArgumentOutOfRangeException.ThrowIfZero(unityBase, nameof(unityBase));
            UnityBase = unityBase;

            // Snapshot the Unity engine version from the PE version resource.
            // Used by the PhysX snapshot fingerprint so a Unity bump
            // invalidates the disk cache automatically. Failures non-fatal.
            try
            {
                var modules = vmm.Map_GetModule(_pid, fExtendedInfo: true);
                if (modules is not null)
                {
                    foreach (var m in modules)
                    {
                        if (!string.Equals(m.sText, "UnityPlayer.dll", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (m.VersionInfo.fValid && !string.IsNullOrEmpty(m.VersionInfo.sFileVersion))
                        {
                            UnityPlayerVersion = m.VersionInfo.sFileVersion;
                            Log.WriteLine($"[Memory] UnityPlayer.dll FileVersion={m.VersionInfo.sFileVersion}");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Memory] UnityPlayer version snapshot failed (non-fatal): {ex.Message}");
            }

            var gaBase = vmm.ProcessGetModuleBase(_pid, "GameAssembly.dll");
            if (gaBase != 0)
            {
                GameAssemblyBase = gaBase;
                Log.WriteLine($"[Memory] GameAssembly.dll base: 0x{gaBase:X}");
            }
            else
            {
                Log.WriteLine("[Memory] WARNING: GameAssembly.dll not found.");
            }

            GOM = GameObjectManager.GetAddr(unityBase);
            ArgumentOutOfRangeException.ThrowIfZero(GOM, nameof(GOM));
            Log.WriteLine($"[Memory] GOM: 0x{GOM:X}");
        }

        /// <summary>
        /// Spins until EFT's IL2CPP TypeInfoTable pointer is readable, or until the
        /// 60-second timeout expires.  This ensures <see cref="Il2CppDumper"/> does
        /// not fire before the game's own IL2CPP runtime has finished initializing.
        /// </summary>
        private static void WaitForTypeInfoTable()
        {
            var gaBase = GameAssemblyBase;
            if (gaBase == 0) return;

            var rva = Offsets.Special.TypeInfoTableRva;
            if (rva == 0)
            {
                Log.WriteLine("[Memory] TypeInfoTableRva is 0 — skipping pre-dump wait, Il2CppDumper will sig-scan.");
                return;
            }

            const int timeoutMs = 60_000;
            const int intervalMs = 500;
            var sw = Stopwatch.StartNew();
            bool logged = false;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var tablePtr = ReadValue<ulong>(gaBase + rva, false);
                    if (tablePtr.IsValidVirtualAddress())
                    {
                        if (logged)
                            Log.WriteLine($"[Memory] TypeInfoTable ready (waited {sw.ElapsedMilliseconds}ms).");
                        return;
                    }
                }
                catch { }

                if (!logged)
                {
                    Log.WriteLine("[Memory] Waiting for IL2CPP TypeInfoTable to initialize...");
                    logged = true;
                }

                Thread.Sleep(intervalMs);
            }

            Log.WriteLine("[Memory] TypeInfoTable wait timed out — proceeding; Il2CppDumper retry loop will handle it.");
        }

        #endregion

        #region Scatter Read

        /// <summary>Executes a batch scatter read against the game process.</summary>
        public static void ReadScatter(IScatterEntry[] entries, int count, bool useCache = true)
        {
            if (count == 0) return;
            using var scatter = new VmmScatter(VmmOrThrow(), _pid, ToFlags(useCache));
            scatter.Executed += (count, pages) => DmaStats.AddScatterExecute(count, pages);

            for (int i = 0; i < count; i++)
            {
                var e = entries[i];
                if (!e.Address.IsValidVirtualAddress() || e.CB == 0 || (uint)e.CB > MAX_READ_SIZE)
                {
                    e.IsFailed = true;
                    continue;
                }
                if (!scatter.PrepareRead(e.Address, (uint)e.CB))
                    e.IsFailed = true;
            }

            scatter.Execute();

            for (int i = 0; i < count; i++)
            {
                var e = entries[i];
                if (!e.IsFailed)
                    e.ReadResult(scatter);
            }
        }

        public static void ReadScatter(IScatterEntry[] entries, bool useCache = true)
            => ReadScatter(entries, entries.Length, useCache);

        #endregion

        #region Read Methods

        /// <summary>
        /// Compile-time element size of <typeparamref name="T"/>. JIT-folds to a constant.
        /// Mirrors the <c>sizeof(T)</c> the underlying VmmSharpEx read uses, and accepts the
        /// same <c>allows ref struct</c> generics as <see cref="ReadValue{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int SizeOf<T>() where T : unmanaged, allows ref struct => sizeof(T);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T ReadValue<T>(ulong addr, bool useCache = true)
            where T : unmanaged, allows ref struct
        {
            if (!addr.IsValidVirtualAddress())
                throw new BadPtrException(0, addr);
            DmaStats.AddDirectRead(addr, SizeOf<T>(), useCache);
            // Use the non-throwing variant so we can attach the faulting address to the
            // exception — the bare "Memory Read Failed!" from VmmSharpEx tells you nothing
            // about which read died. The interpolated string is only built on failure.
            if (!VmmOrThrow().MemReadValue<T>(_pid, addr, out var result, ToFlags(useCache)))
                throw new VmmException($"ReadValue<{typeof(T).Name}> failed @ 0x{addr:X}");
            return result;
        }

        public static ulong ReadPtr(ulong addr, bool useCache = true)
        {
            var ptr = ReadValue<ulong>(addr, useCache);
            if (!ptr.IsValidVirtualAddress())
                throw new BadPtrException(addr, ptr);
            return ptr;
        }

        public static ulong ReadPtrChain(ulong addr, ReadOnlySpan<uint> offsets, bool useCache = true)
        {
            var p = addr;
            foreach (var o in offsets)
                p = ReadPtr(p + o, useCache);
            return p;
        }

        public static void ReadBuffer<T>(ulong addr, Span<T> buffer, bool useCache = true)
            where T : unmanaged
        {
            if (!addr.IsValidVirtualAddress())
                throw new BadPtrException(0, addr);
            if (buffer.IsEmpty) return;
            DmaStats.AddDirectRead(addr, (long)buffer.Length * SizeOf<T>(), useCache);
            if (!VmmOrThrow().MemReadSpan(_pid, addr, buffer, ToFlags(useCache)))
                throw new VmmException($"ReadBuffer<{typeof(T).Name}> failed @ 0x{addr:X} ({buffer.Length} elems)");
        }

        public static T[] ReadArray<T>(ulong addr, int count, bool useCache = true)
            where T : unmanaged
        {
            if (count <= 0) return [];
            T[] arr = new T[count];
            ReadBuffer(addr, arr.AsSpan(), useCache);
            return arr;
        }

        public static string ReadString(ulong addr, int cb = 128, bool useCache = true)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cb, 0, nameof(cb));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(cb, 0x1000, nameof(cb));
            if (!addr.IsValidVirtualAddress())
                throw new BadPtrException(0, addr);
            DmaStats.AddDirectRead(addr, cb, useCache);
            return VmmOrThrow().MemReadString(_pid, addr, cb, Encoding.UTF8, ToFlags(useCache))
                ?? throw new VmmException($"ReadString failed @ 0x{addr:X} (cb={cb})");
        }

        public static string ReadUnityString(ulong addr, int length = 128, bool useCache = true)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, 0, nameof(length));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 0x1000, nameof(length));
            if (!addr.IsValidVirtualAddress())
                throw new BadPtrException(0, addr);
            DmaStats.AddDirectRead(addr + 0x14, length, useCache);
            return VmmOrThrow().MemReadString(_pid, addr + 0x14, length, Encoding.Unicode, ToFlags(useCache))
                ?? throw new VmmException($"ReadUnityString failed @ 0x{addr + 0x14:X} (len={length})");
        }

        #endregion

        #region Try Read Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadValue<T>(ulong addr, out T result, bool useCache = true)
            where T : unmanaged, allows ref struct
        {
            if (!addr.IsValidVirtualAddress()) { result = default; return false; }
            DmaStats.AddDirectRead(addr, SizeOf<T>(), useCache);
            return VmmOrThrow().MemReadValue(_pid, addr, out result, ToFlags(useCache));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadPtr(ulong addr, out ulong result, bool useCache = true)
        {
            if (!TryReadValue(addr, out result, useCache)) return false;
            return result.IsValidVirtualAddress();
        }

        public static bool TryReadPtrChain(ulong addr, ReadOnlySpan<uint> offsets, out ulong result, bool useCache = true)
        {
            result = addr;
            foreach (var o in offsets)
                if (!TryReadPtr(result + o, out result, useCache)) return false;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadBuffer<T>(ulong addr, Span<T> buffer, bool useCache = true)
            where T : unmanaged
        {
            if (!addr.IsValidVirtualAddress()) return false;
            if (buffer.IsEmpty) return true;
            DmaStats.AddDirectRead(addr, (long)buffer.Length * SizeOf<T>(), useCache);
            return VmmOrThrow().MemReadSpan(_pid, addr, buffer, ToFlags(useCache));
        }

        public static bool TryReadString(ulong addr, out string? result, int cb = 128, bool useCache = true)
        {
            result = null;
            if (cb <= 0 || cb > 0x1000) return false;
            if (!addr.IsValidVirtualAddress()) return false;
            DmaStats.AddDirectRead(addr, cb, useCache);
            result = VmmOrThrow().MemReadString(_pid, addr, cb, Encoding.UTF8, ToFlags(useCache));
            return result is not null;
        }

        public static bool TryReadUnityString(ulong addr, out string? result, int length = 128, bool useCache = true)
        {
            result = null;
            if (length <= 0 || length > 0x1000) return false;
            if (!addr.IsValidVirtualAddress()) return false;
            DmaStats.AddDirectRead(addr + 0x14, length, useCache);
            result = VmmOrThrow().MemReadString(_pid, addr + 0x14, length, Encoding.Unicode, ToFlags(useCache));
            return result is not null;
        }

        public static (uint Timestamp, uint SizeOfImage) ReadPeFingerprint(ulong moduleBase)
        {
            if (!TryReadValue<uint>(moduleBase + 0x3C, out var eLfanew, false) || eLfanew == 0 || eLfanew > 0x1000)
                return (0, 0);
            if (!TryReadValue<uint>(moduleBase + eLfanew + 8, out var ts, false)) return (0, 0);
            if (!TryReadValue<uint>(moduleBase + eLfanew + 0x50, out var sz, false)) return (0, 0);
            return (ts, sz);
        }

        #endregion

        #region Signature Scanning

        public static ulong FindSignature(string signature, string moduleName)
        {
            var vmm = VmmOrThrow();
            int tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (tokens <= 32)
                return vmm.FindSignature(_pid, signature, moduleName);
            var results = vmm.FindSignatures(_pid, signature, moduleName, maxMatches: 1);
            return results.Length > 0 ? results[0] : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong[] FindSignatures(string signature, string moduleName, int maxMatches = int.MaxValue)
            => VmmOrThrow().FindSignatures(_pid, signature, moduleName, maxMatches);

        /// <summary>
        /// Returns the image size (in bytes) of the named module currently
        /// loaded. Used by <see cref="PhysX.PhysXProbe"/> to flag candidate
        /// pointers that fall inside UnityPlayer.dll's own image (those would
        /// be vtables / globals, not heap-allocated SDK pointers).
        /// </summary>
        public static uint GetModuleImageSize(string moduleName)
        {
            try
            {
                var modules = VmmOrThrow().Map_GetModule(_pid, fExtendedInfo: false);
                if (modules is null) return 0;
                foreach (var m in modules)
                    if (string.Equals(m.sText, moduleName, StringComparison.OrdinalIgnoreCase))
                        return m.cbImageSize;
            }
            catch { }
            return 0;
        }

        #endregion

        #region Write Methods (Low-level primitives — NOT USED by any active feature)
        //
        // These methods exist as general DMA building blocks.
        // After removal of all memory-write cheat features (NightVision, ThermalVision, etc.),
        // NO CODE IN THIS PROJECT CALLS THEM. They are kept only to avoid breaking potential
        // future internal tools or diagnostics. Do not use them for gameplay modification.
        //

        public static unsafe void WriteValue<T>(ulong addr, T value)
            where T : unmanaged, allows ref struct
        {
            if (!addr.IsValidVirtualAddress())
                throw new BadPtrException(0, addr);
            Span<byte> buf = stackalloc byte[sizeof(T)];
            if (buf.IsEmpty) return;
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buf), value);
            VmmOrThrow().MemWriteSpan(_pid, addr, buf);
        }

        /// <summary>
        /// Write value to memory with verification — retries up to 3 times.
        /// </summary>
        public static unsafe void WriteValueEnsure<T>(ulong addr, T value)
            where T : unmanaged
        {
            int cb = sizeof(T);
            var b1 = new ReadOnlySpan<byte>(&value, cb);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    WriteValue(addr, value);
                    Thread.SpinWait(5);
                    T temp = ReadValue<T>(addr, false);
                    var b2 = new ReadOnlySpan<byte>(&temp, cb);
                    if (b1.SequenceEqual(b2)) return;
                }
                catch { }
            }
            throw new VmmException("Memory write verification failed.");
        }

        /// <summary>Write a buffer of unmanaged values to memory.</summary>
        public static void WriteBuffer<T>(ulong addr, Span<T> buffer)
            where T : unmanaged
        {
            if (!addr.IsValidVirtualAddress())
                throw new BadPtrException(0, addr);
            if (buffer.IsEmpty) return;
            VmmOrThrow().MemWriteSpan(_pid, addr, buffer);
        }

        /// <summary>Write a buffer with verification — retries up to 3 times.</summary>
        public static void WriteBufferEnsure<T>(ulong addr, Span<T> buffer)
            where T : unmanaged
        {
            int cb = SizeChecker<T>.Size * buffer.Length;
            Span<byte> temp = cb > 0x1000 ? new byte[cb] : stackalloc byte[cb];
            ReadOnlySpan<byte> b1 = MemoryMarshal.Cast<T, byte>(buffer);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    WriteBuffer(addr, buffer);
                    Thread.SpinWait(5);
                    temp.Clear();
                    ReadBuffer(addr, temp, false);
                    if (temp.SequenceEqual(b1)) return;
                }
                catch { }
            }
            throw new VmmException("Memory write buffer verification failed.");
        }

        #endregion

        #region Misc

        public static void FullRefresh() => _vmm?.ForceFullRefresh();

        /// <summary>
        /// Throws <see cref="GameNotRunningException"/> if the game process is gone.
        /// </summary>
        public static void ThrowIfNotInGame()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    FullRefresh();
                    if (VmmOrThrow().PidGetFromName(ProcessName, out uint pid) && pid == _pid)
                        return;
                }
                catch { Thread.Sleep(150); }
            }
            throw new GameNotRunningException();
        }

        /// <summary>Creates a new <see cref="VmmScatter"/> against the game process.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VmmScatter GetScatter(VmmFlags flags)
        {
            var s = new VmmScatter(VmmOrThrow(), _pid, flags);
            s.Executed += s_scatterExecutedHandler;
            return s;
        }

        /// <summary>Creates a new <see cref="VmmScatter"/> against the game process with cache control.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VmmScatter CreateScatter(bool useCache = true)
        {
            var s = new VmmScatter(VmmOrThrow(), _pid, ToFlags(useCache));
            s.Executed += s_scatterExecutedHandler;
            return s;
        }

        // Cached delegate — avoids per-scatter closure allocation on the hot path.
        // Scatter handles are created and disposed many times per worker tick
        // (realtime, validation, gear, hands, firearm, etc.), so even a small
        // managed allocation here adds up to measurable GC pressure over a raid.
        private static readonly Action<int, int> s_scatterExecutedHandler =
            static (count, pages) => DmaStats.AddScatterExecute(count, pages);

        /// <summary>Signals the worker to stop, waits for it to exit, and disposes the VMM handle.</summary>
        public static void Close()
        {
            if (_shutdown) return; // idempotent
            _shutdown = true;
            LobbyQuestReader.Stop();
            eft_dma_radar.Silk.Tarkov.QuestPlanner.QuestPlannerWorker.Stop();
            try { _cts.Cancel(); } catch { }

            // Wait for the worker thread to finish — bounded to prevent hung shutdown
            _workerThread?.Join(TimeSpan.FromSeconds(5));

            // Drain any in-progress IL2CPP match dump before the VMM handle is disposed.
            // Without this the background task is cut off mid-write and the file is truncated.
            MatchDumper.Drain(TimeSpan.FromSeconds(120));

            _vmm?.Dispose();
            _vmm = null;
            Log.WriteLine("[Memory] Closed.");
        }

        private static void Notify(string msg, NotificationLevel level)
        {
            try { ShowNotification?.Invoke(msg, level); }
            catch { }
        }

        #endregion

        #region Nested Types

        /// <summary>Thrown when the game process is no longer running.</summary>
        public sealed class GameNotRunningException : DmaException
        {
            public GameNotRunningException()
                : base("Game process is no longer running.") { }
        }

        #endregion
    }

    /// <summary>Memory module lifecycle state.</summary>
    public enum MemoryState
    {
        NotStarted,
        WaitingForProcess,
        Initializing,
        ProcessFound,
        InRaid,
        InHideout,
    }

    /// <summary>Notification severity used by <see cref="Memory.ShowNotification"/>.</summary>
    public enum NotificationLevel { Info, Warning, Error }
}
