// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.Features.Ballistics
{
    /// <summary>
    /// G1 drag-coefficient lookup. Returns either:
    ///   <list type="bullet">
    ///     <item>The game's own table (snapshotted from <c>EFT.Ballistics.Shot.G1</c> on the
    ///       first successfully-read in-flight bullet), or</item>
    ///     <item>The hardcoded 87-entry fallback table sourced from prior reverse engineering,
    ///       used until a live snapshot becomes available.</item>
    ///   </list>
    /// Source choice is exposed via <see cref="UsingLiveTable"/> for the debug HUD.
    /// </summary>
    public static class G1Table
    {
        private const float SpeedOfSound = 343f;
        private const float MachStep = 0.05f;

        // ── Hardcoded fallback (ported from prior build) ──────────────────────────
        // Declared first so it's initialized before _table below depends on it.
        private static readonly G1DragModel[] _fallback =
        [
            new(0f,     0.2629f), new(0.05f,  0.2558f), new(0.1f,   0.2487f), new(0.15f,  0.2413f),
            new(0.2f,   0.2344f), new(0.25f,  0.2278f), new(0.3f,   0.2214f), new(0.35f,  0.2155f),
            new(0.4f,   0.2104f), new(0.45f,  0.2061f), new(0.5f,   0.2032f), new(0.55f,  0.202f),
            new(0.6f,   0.2034f), new(0.7f,   0.2165f), new(0.725f, 0.223f),  new(0.75f,  0.2313f),
            new(0.775f, 0.2417f), new(0.8f,   0.2546f), new(0.825f, 0.2706f), new(0.85f,  0.2901f),
            new(0.875f, 0.3136f), new(0.9f,   0.3415f), new(0.925f, 0.3734f), new(0.95f,  0.4084f),
            new(0.975f, 0.4448f), new(1f,     0.4805f), new(1.025f, 0.5136f), new(1.05f,  0.5427f),
            new(1.075f, 0.5677f), new(1.1f,   0.5883f), new(1.125f, 0.6053f), new(1.15f,  0.6191f),
            new(1.2f,   0.6393f), new(1.25f,  0.6518f), new(1.3f,   0.6589f), new(1.35f,  0.6621f),
            new(1.4f,   0.6625f), new(1.45f,  0.6607f), new(1.5f,   0.6573f), new(1.55f,  0.6528f),
            new(1.6f,   0.6474f), new(1.65f,  0.6413f), new(1.7f,   0.6347f), new(1.75f,  0.628f),
            new(1.8f,   0.621f),  new(1.85f,  0.6141f), new(1.9f,   0.6072f), new(1.95f,  0.6003f),
            new(2f,     0.5934f), new(2.05f,  0.5867f), new(2.1f,   0.5804f), new(2.15f,  0.5743f),
            new(2.2f,   0.5685f), new(2.25f,  0.563f),  new(2.3f,   0.5577f), new(2.35f,  0.5527f),
            new(2.4f,   0.5481f), new(2.45f,  0.5438f), new(2.5f,   0.5397f), new(2.6f,   0.5325f),
            new(2.7f,   0.5264f), new(2.8f,   0.5211f), new(2.9f,   0.5168f), new(3f,     0.5133f),
            new(3.1f,   0.5105f), new(3.2f,   0.5084f), new(3.3f,   0.5067f), new(3.4f,   0.5054f),
            new(3.5f,   0.504f),  new(3.6f,   0.503f),  new(3.7f,   0.5022f), new(3.8f,   0.5016f),
            new(3.9f,   0.501f),  new(4f,     0.5006f), new(4.2f,   0.4998f), new(4.4f,   0.4995f),
            new(4.6f,   0.4992f), new(4.8f,   0.499f),  new(5f,     0.4988f),
        ];

        /// <summary>
        /// Snapshot of the game's G1 table. Replaced by
        /// <see cref="SetFromGame(ReadOnlySpan{G1DragModel})"/> on first successful read.
        /// Defaults to the hardcoded fallback so the simulator is usable before any shot is fired.
        /// </summary>
        private static G1DragModel[] _table = CloneFallback();

        public static bool UsingLiveTable { get; private set; }

        /// <summary>Number of entries in the active table — for debug HUD.</summary>
        public static int EntryCount => _table.Length;

        /// <summary>Force-revert to the hardcoded fallback. Call at raid end.</summary>
        public static void Reset()
        {
            _table = CloneFallback();
            UsingLiveTable = false;
        }

        /// <summary>
        /// Snapshot a table read from the game's <c>Shot.G1</c> list. Silently ignored if the
        /// span is empty or too short to be a valid G1 model (saves us from garbage reads).
        /// </summary>
        public static void SetFromGame(ReadOnlySpan<G1DragModel> liveTable)
        {
            if (liveTable.Length < 40) return; // real table is 87 entries; reject obviously bad reads
            var copy = new G1DragModel[liveTable.Length];
            liveTable.CopyTo(copy);
            _table = copy;
            UsingLiveTable = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CalculateDragCoefficient(float velocity)
        {
            var g1 = _table.AsSpan();
            int num = (int)Math.Round(Math.Floor(velocity / SpeedOfSound / MachStep));

            if (num <= 0)
                return 0f;
            if (num > g1.Length - 1)
                return g1[^1].Ballist;

            float num2 = g1[num - 1].Mach * SpeedOfSound;
            float num3 = g1[num].Mach * SpeedOfSound;
            float ballist = g1[num - 1].Ballist;
            return (g1[num].Ballist - ballist) / (num3 - num2) * (velocity - num2) + ballist;
        }

        private static G1DragModel[] CloneFallback()
        {
            var copy = new G1DragModel[_fallback.Length];
            _fallback.CopyTo(copy, 0);
            return copy;
        }
    }
}
