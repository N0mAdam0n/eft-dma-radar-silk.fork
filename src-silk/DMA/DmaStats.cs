// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace eft_dma_radar.Silk.DMA
{
    /// <summary>
    /// Live DMA performance counters updated by the realtime scatter worker.
    /// All public properties are safe to read from any thread (volatile / Interlocked).
    /// </summary>
    internal static class DmaStats
    {
        // ── Accumulator fields (written by realtime worker) ──────────────────
        private static int  _tickAccum;      // scatter ticks this window
        private static long _bytesAccum;     // bytes read this window
        private static int  _entityAccum;    // latest entity count
        private static int  _maxItemsAccum;  // latest max scatter entries

        // ── Read-count accumulators (realtime scatter path only) ─────────────
        private static long _readsAccum;     // RT scatter PrepareRead calls this window
        private static long _readsPrevWindow;// reads/s from last completed window

        // ── Hardware bus trip counters (all threads) ──────────────────────────
        // Executes = number of scatter.Execute() calls (each = one batched DMA round-trip).
        // Direct   = number of unbatched MemRead* calls (each = one DMA round-trip).
        // Prepared = PrepareRead entries batched across all Execute() calls.
        private static long _executesAccum;
        private static long _directAccum;
        private static long _preparedAccum;
        private static long _executesPrevWindow;
        private static long _directPrevWindow;
        private static long _preparedPrevWindow;

        // Uncached (NOCACHE) direct reads — a subset of _directAccum. These always hit the
        // PCIe bus, so their page cost is folded into _bytesAccum (and therefore MB/s).
        // Cache-served direct reads are excluded from MB/s to avoid inflating it past the
        // hardware ceiling. Counted separately so the uncached share is observable.
        private static long _uncachedDirectAccum;
        private static long _uncachedDirectPrevWindow;

        // ── Published snapshot (updated every ~1 s) ──────────────────────────
        private static volatile int   _fps;
        private static volatile float _mbpsCurrent;
        private static volatile float _mbpsPeak;
        private static volatile float _mbpsMax;   // hardware ceiling, set once at startup
        private static volatile int   _entities;
        private static volatile int   _maxItems;

        // ── Timer ─────────────────────────────────────────────────────────────
        private static long _windowStart = Stopwatch.GetTimestamp();

        /// <summary>Realtime scatter worker ticks-per-second.</summary>
        public static int   RealtimeFps     => _fps;

        /// <summary>Current 1-second average DMA read throughput (MB/s).</summary>
        public static float ReadMBpsCurrent => _mbpsCurrent;

        /// <summary>Session peak DMA read throughput (MB/s). Resets on raid start.</summary>
        public static float ReadMBpsPeak    => _mbpsPeak;

        /// <summary>
        /// Hardware throughput ceiling measured at startup via physical LeechCore reads.
        /// Zero until the benchmark completes (~3 s after launch).
        /// </summary>
        public static float MaxThroughputMBps => _mbpsMax;

        /// <summary>Number of active entities tracked last tick.</summary>
        public static int   EntityCount     => _entities;

        /// <summary>Max scatter entries prepared in a single tick (approx. reads-per-round-trip).</summary>
        public static int   MaxItems        => _maxItems;

        /// <summary>
        /// Realtime scatter PrepareRead calls per second — player position and rotation reads only.
        /// Does NOT include loot, zone, or interactable scatter workers.
        /// </summary>
        public static long ReadsPerSecond => Interlocked.Read(ref _readsPrevWindow);

        /// <summary>
        /// scatter.Execute() calls per second across ALL threads. Each = one batched DMA round-trip.
        /// </summary>
        public static long ExecutesPerSecond => Interlocked.Read(ref _executesPrevWindow);

        /// <summary>
        /// Direct (unbatched) Memory.ReadValue / ReadPtr / ReadBuffer / ReadString calls per second.
        /// Each = one DMA round-trip. High values indicate cold-init / discovery / IL2CPP scan work.
        /// </summary>
        public static long DirectReadsPerSecond => Interlocked.Read(ref _directPrevWindow);

        /// <summary>
        /// Uncached (NOCACHE) direct read calls per second — the subset of
        /// <see cref="DirectReadsPerSecond"/> that bypassed the VMM page cache and hit the
        /// PCIe bus. Unlike cache-served direct reads, these contribute to <see cref="ReadMBpsCurrent"/>.
        /// </summary>
        public static long UncachedDirectReadsPerSecond => Interlocked.Read(ref _uncachedDirectPrevWindow);

        /// <summary>
        /// Total hardware DMA round-trips per second = ExecutesPerSecond + DirectReadsPerSecond.
        /// </summary>
        public static long TripsPerSecond => ExecutesPerSecond + DirectReadsPerSecond;

        /// <summary>
        /// Total PrepareRead entries batched per second across ALL scatter Execute() calls.
        /// Divide by <see cref="ExecutesPerSecond"/> to get average batch size per scatter trip.
        /// </summary>
        public static long PreparedPerSecond => Interlocked.Read(ref _preparedPrevWindow);

        /// <summary>
        /// Called once per scatter.Execute() from any thread.
        /// <paramref name="prepareCount"/> is the number of PrepareRead entries that were batched into this trip.
        /// <paramref name="pageCount"/> is the total number of 4 KB pages actually touched on the DMA bus
        /// (accounts for reads spanning a page boundary and array reads larger than one page).
        /// Byte accounting is done here (not in RecordTick) so ALL scatter workers contribute to the MB/s display.
        /// </summary>
        public static void AddScatterExecute(int prepareCount, int pageCount)
        {
            Interlocked.Increment(ref _executesAccum);
            Interlocked.Add(ref _preparedAccum, prepareCount);
            // Real bus cost = pages touched × 4 KB. Previously approximated as prepareCount × 4 KB,
            // which over-counted tiny reads and severely under-counted multi-page array reads.
            Interlocked.Add(ref _bytesAccum, (long)pageCount * 4096L);
        }

        /// <summary>
        /// Called once per direct Memory.ReadValue / ReadPtr / ReadBuffer / ReadString call.
        /// Always tracked as a count for <see cref="DirectReadsPerSecond"/>.
        /// <para>
        /// When <paramref name="useCache"/> is <see langword="true"/> (the default read path) the
        /// read is frequently served from VMM's in-process page cache without touching the PCIe
        /// bus, so its size is deliberately NOT added to the MB/s accumulator — doing so would
        /// inflate MB/s above the measured hardware ceiling.
        /// </para>
        /// <para>
        /// When <paramref name="useCache"/> is <see langword="false"/> (NOCACHE) the read always
        /// hits the bus. Its real cost — the 4 KB pages it touches — is folded into the MB/s
        /// figure using the same page-based accounting as the scatter path, so MB/s now reflects
        /// uncached direct reads too (cold-init, IL2CPP discovery, write-verify, PE fingerprints, …).
        /// </para>
        /// </summary>
        /// <param name="addr">Virtual address of the read (for page-boundary accounting).</param>
        /// <param name="cb">Number of bytes requested by the read.</param>
        /// <param name="useCache">Whether the read used the VMM page cache.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddDirectRead(ulong addr, long cb, bool useCache)
        {
            Interlocked.Increment(ref _directAccum);

            // Cache-served reads may never reach the bus — exclude them from MB/s.
            if (useCache || cb <= 0)
                return;

            Interlocked.Increment(ref _uncachedDirectAccum);
            // Real bus cost = pages actually spanned × 4 KB (an 8-byte read still fetches a
            // whole page; a read straddling a boundary fetches two). Matches AddScatterExecute.
            ulong firstPage = addr >> 12;
            ulong lastPage  = (addr + (ulong)cb - 1) >> 12;
            Interlocked.Add(ref _bytesAccum, (long)(lastPage - firstPage + 1) * 4096L);
        }

        /// <summary>
        /// Called by the realtime scatter worker once per tick to update FPS, entity count, and flush the window.
        /// Byte accounting is handled by <see cref="AddScatterExecute"/> across all threads — do not pass bytes here.
        /// </summary>
        /// <param name="entityCount">Number of active player entries that had scatter reads prepared.</param>
        /// <param name="maxScatterItems">Total scatter entries prepared this tick (position + rotation + IsAiming).</param>
        public static void RecordTick(int entityCount, int maxScatterItems)
        {
            Interlocked.Increment(ref _tickAccum);
            Interlocked.Add(ref _readsAccum, maxScatterItems);
            // Non-atomic for these — last-write wins is fine for display purposes.
            _entityAccum   = entityCount;
            _maxItemsAccum = maxScatterItems;

            // Flush approximately every second.
            var now = Stopwatch.GetTimestamp();
            double elapsed = (double)(now - Interlocked.Read(ref _windowStart)) / Stopwatch.Frequency;
            if (elapsed < 1.0)
                return;

            // Swap accumulators atomically.
            long windowStartSnap = Interlocked.Exchange(ref _windowStart, now);
            double actualElapsed = (double)(now - windowStartSnap) / Stopwatch.Frequency;
            if (actualElapsed <= 0)
                return;

            int  ticks    = Interlocked.Exchange(ref _tickAccum, 0);
            long bytes    = Interlocked.Exchange(ref _bytesAccum, 0L);
            long reads    = Interlocked.Exchange(ref _readsAccum, 0L);
            long executes = Interlocked.Exchange(ref _executesAccum, 0L);
            long direct   = Interlocked.Exchange(ref _directAccum, 0L);
            long uncached = Interlocked.Exchange(ref _uncachedDirectAccum, 0L);
            long prepared = Interlocked.Exchange(ref _preparedAccum, 0L);

            // bytes is now accumulated from all scatter workers via AddScatterExecute,
            // giving a complete picture of DMA bus utilization (not just position reads).
            float mbps = (float)(bytes / 1_048_576.0 / actualElapsed);

            _fps        = (int)(ticks / actualElapsed);
            _mbpsCurrent = mbps;
            if (mbps > _mbpsPeak)
                _mbpsPeak = mbps;
            _entities = _entityAccum;
            _maxItems = _maxItemsAccum;
            Interlocked.Exchange(ref _readsPrevWindow,    (long)(reads    / actualElapsed));
            Interlocked.Exchange(ref _executesPrevWindow, (long)(executes / actualElapsed));
            Interlocked.Exchange(ref _directPrevWindow,   (long)(direct   / actualElapsed));
            Interlocked.Exchange(ref _uncachedDirectPrevWindow, (long)(uncached / actualElapsed));
            Interlocked.Exchange(ref _preparedPrevWindow, (long)(prepared / actualElapsed));
        }

        /// <summary>
        /// Resets the session peak MB/s and clears read counters. Call on raid start/end.
        /// </summary>
        public static void ResetPeak()
        {
            _mbpsPeak = 0f;
            Interlocked.Exchange(ref _readsAccum, 0L);
            Interlocked.Exchange(ref _readsPrevWindow, 0L);
            Interlocked.Exchange(ref _executesAccum, 0L);
            Interlocked.Exchange(ref _directAccum, 0L);
            Interlocked.Exchange(ref _uncachedDirectAccum, 0L);
            Interlocked.Exchange(ref _preparedAccum, 0L);
            Interlocked.Exchange(ref _executesPrevWindow, 0L);
            Interlocked.Exchange(ref _directPrevWindow, 0L);
            Interlocked.Exchange(ref _uncachedDirectPrevWindow, 0L);
            Interlocked.Exchange(ref _preparedPrevWindow, 0L);
        }

        /// <summary>
        /// Sets the hardware throughput ceiling. Called once by the startup benchmark.
        /// </summary>
        public static void SetMaxThroughput(float mbps)
        {
            _mbpsMax = mbps;
        }
    }
}
