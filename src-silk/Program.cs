// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;
using eft_dma_radar.Silk.Tarkov;
using System.Net.Http;
using System.Runtime.Versioning;

[assembly: AssemblyTitle("EFT DMA Radar (Silk.NET)")]
[assembly: AssemblyProduct("EFT DMA Radar (Silk.NET)")]
[assembly: AssemblyDescription("Advanced DMA radar for Escape from Tarkov — Silk.NET Edition")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: SupportedOSPlatform("Windows")]

namespace eft_dma_radar.Silk
{
    internal static partial class SilkProgram
    {
        internal const string Name = "EFT DMA Radar (Silk.NET)";

        internal static MemoryState State => Memory.State;

        /// <summary>Singleton HTTP client for all outbound requests.</summary>
        public static HttpClient HttpClient { get; } = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            // Recycle pooled connections every 5 minutes so DNS changes are picked up
            // (this client lives for the entire process lifetime).
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                    | System.Security.Authentication.SslProtocols.Tls13
            }
        })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        internal static SilkConfig Config { get; private set; } = null!;

        static SilkProgram()
        {
            GlfwWindowing.RegisterPlatform();
            GlfwInput.RegisterPlatform();
            GlfwWindowing.Use();
        }

        [STAThread]
        static void Main()
        {
            try
            {
                Config = SilkConfig.Load();
                Log.WriteLine("[SilkProgram] Config loaded OK.");

                // Wire debug logging from config or -debug command-line argument
                Log.EnableDebugLogging = Config.DebugLogging ||
                    (Environment.GetCommandLineArgs()?.Contains("-debug", StringComparer.OrdinalIgnoreCase) ?? false);
                if (Log.EnableDebugLogging)
                    Log.WriteLine("[SilkProgram] Debug logging enabled.");

                // Vischeck — apply persisted classifier rules + worker toggles +
                // diagnostic-logging flags to the live static singletons before
                // anything that might attach starts.
                eft_dma_radar.Silk.Tarkov.Unity.PhysX.VisibilityClassifier.LoadFromConfig(Config);
                eft_dma_radar.Silk.Tarkov.Unity.PhysX.VisibilityWorker.LoadFromConfig(Config);
                eft_dma_radar.Silk.Tarkov.Unity.PhysX.VisCheckDiagnostics.LoadFromConfig(Config);

                if (Config.TraceDmaExceptions) ExceptionTracer.Enabled = true;
                ExceptionTracer.Install();

                SetHighPerformanceMode();

                Memory.ModuleInit(Config);
                Memory.GameStarted += (_, _) => ProfileService.Start();
                Memory.GameStopped += (_, _) => ProfileService.Stop();
                Memory.GameStarted += (_, _) =>
                {
                    InputManager.Initialize();
                    HotkeyManager.RegisterAll();
                };
                Log.WriteLine("[SilkProgram] Memory module initialized.");

                eft_dma_radar.Silk.Tarkov.Features.FeatureManager.ModuleInit();
                Log.WriteLine("[SilkProgram] FeatureManager initialized.");

                EftDataManager.ModuleInit();

                LootFilter.LoadFilterData();

                MapManager.ModuleInit();
                Log.WriteLine("[SilkProgram] Map manager initialized, starting RadarWindow...");

                if (Config.WebRadarEnabled)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await eft_dma_radar.Silk.Web.WebRadarServer.StartAsync(
                                Config.WebRadarPort,
                                TimeSpan.FromMilliseconds(Config.WebRadarTickMs)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log.WriteLine($"[WebRadar] Failed to start: {ex.Message}");
                        }
                    });
                }

                RadarWindow.Initialize();

                RadarWindow.Run();

                Log.WriteLine("[SilkProgram] RadarWindow.Run() returned normally.");
            }
            catch (Exception ex)
            {
                HandleFatalError(ex);
            }
            finally
            {
                if (eft_dma_radar.Silk.Web.WebRadarServer.IsRunning)
                    eft_dma_radar.Silk.Web.WebRadarServer.StopAsync().GetAwaiter().GetResult();

                HotkeyManager.UnregisterAll();
                InputManager.Shutdown();
                Memory.Close();
            }
        }

        /// <summary>
        /// Sets High Performance mode: process priority, timer resolution, power plan, and MMCSS thread characteristics.
        /// </summary>
        private static void SetHighPerformanceMode()
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

            if (SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED) == 0)
                Log.WriteLine($"WARNING: Unable to set Thread Execution State. ERROR {Marshal.GetLastWin32Error()}");

            Guid highPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            if (PowerSetActiveScheme(0, ref highPerformanceGuid) != 0)
                Log.WriteLine($"WARNING: Unable to set High Performance Power Plan. ERROR {Marshal.GetLastWin32Error()}");

            if (TimeBeginPeriod(5) != 0)
                Log.WriteLine($"WARNING: Unable to set timer resolution to 5ms. ERROR {Marshal.GetLastWin32Error()}");

            if (AvSetMmThreadCharacteristicsW("Games", out _) == 0)
                Log.WriteLine($"WARNING: Unable to set Multimedia thread characteristics to 'Games'. ERROR {Marshal.GetLastWin32Error()}");

            Log.WriteLine("[SilkProgram] High performance mode set.");
        }

        private static void HandleFatalError(Exception ex)
        {
            string error = $"FATAL ERROR -> {ex}";
            Log.WriteLine(error);
            try { File.WriteAllText("crash.log", $"[{DateTime.Now:u}] {error}"); }
            catch { }
            Environment.FailFast(error);
        }

        #region P/Invoke

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001,
        }

        [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial nint AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);

        [LibraryImport("powrprof.dll", SetLastError = true)]
        private static partial uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

        [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
        private static partial uint TimeBeginPeriod(uint uMilliseconds);

        #endregion
    }
}

