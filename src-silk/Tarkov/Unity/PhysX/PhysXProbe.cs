using System.Collections.Generic;
using System.Linq;
using eft_dma_radar.Silk.DMA;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// One-shot PhysX SDK pointer discovery probe.
    ///
    /// <para>
    /// Walks <c>UnityPlayer.dll</c> looking for RIP-relative global pointer loads
    /// (<c>mov rax,[rip+rel32]</c>), then validates each unique resolved RVA by
    /// dereferencing it and checking that the resulting object is shaped like an
    /// <c>NpPhysics</c> singleton: a scene array of plausible size whose first
    /// entry contains a plausible rigid-actor count.
    /// </para>
    ///
    /// <para>
    /// Designed for <b>validation-first</b> behavior â€” the sig pattern is
    /// intentionally broad (matches thousands of global loads). The structural
    /// walk is what decides which candidate is real. This makes the probe resilient
    /// to engine patches that move code around without changing struct layouts.
    /// </para>
    ///
    /// <para>
    /// Triggered explicitly (F9). Never runs automatically. Pure read-only â€” cannot
    /// disturb the radar or the game.
    /// </para>
    /// </summary>
    internal static class PhysXProbe
    {
        // â”€â”€ Configuration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Bounds on what "plausibly NpPhysics" looks like. These are loose enough
        // to accept anything we might see in Arena while still rejecting random
        // unrelated globals.
        private const uint MaxPlausibleScenes = 8;
        private const uint MaxPlausibleActorsPerScene = 200_000;

        // Struct offsets we expect inside NpPhysics / NpScene under PhysX 4.1.
        // We READ these; wrong values just mean "this candidate fails validation"
        // â€” never a crash. The probe is the safe place to learn whether these
        // hold on the live engine build.
        private const uint NpPhysics_SceneArrayData = 0x08;
        private const uint NpPhysics_SceneArraySize = 0x10;
        private const uint NpScene_RigidActorsSize  = 0x23D0;

        // Pattern: any 7-byte `mov rax,[rip+rel32]` instruction. The fourth byte
        // onward is the rel32 displacement we decode.
        private const string PtrLoadPattern   = "48 8B 05 ? ? ? ?";
        private const int    PtrLoadInstrLen  = 7;
        private const int    PtrLoadRelOffset = 3;

        // Hard cap on scan matches so a worst-case probe finishes promptly.
        private const int MaxMatches = 65536;

        // â”€â”€ Entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Auto-discovery entry point used by SceneCache when the cached
        /// PhysXOffsets.PhysXSdkRva fails (e.g. the Arena default RVA doesn't
        /// apply to the EFT main game build). Runs the same scan as Run() but
        /// returns the strongest non-suspect candidate instead of just logging.
        /// </summary>
        public static bool TryResolveBestRva(out uint rva, out ulong npPhysics)
        {
            rva = 0; npPhysics = 0;

            var unityBase = Memory.UnityBase;
            if (unityBase == 0) return false;

            ulong[] sigAddrs;
            try { sigAddrs = Memory.FindSignatures(PtrLoadPattern, "UnityPlayer.dll", MaxMatches); }
            catch { return false; }
            if (sigAddrs.Length == 0) return false;

            var seenRvas = new HashSet<ulong>(sigAddrs.Length);
            var uniqueRvas = new List<ulong>();
            foreach (var sigAddr in sigAddrs)
            {
                var r = DecodeRipRelativeRva(sigAddr, unityBase);
                if (r != 0 && seenRvas.Add(r)) uniqueRvas.Add(r);
            }

            var hits = new List<HitRecord>();
            ulong unityEnd = unityBase + GetUnityImageSize(unityBase);
            foreach (var r in uniqueRvas)
            {
                if (TryValidate(unityBase + r, out var np, out var sc, out var ac))
                {
                    bool inModule = np >= unityBase && np < unityEnd;
                    hits.Add(new HitRecord(r, np, sc, ac, inModule));
                }
            }
            if (hits.Count == 0) return false;

            // Strongest = multiple agreeing RVAs first, then non-empty scene.
            var best = hits
                .Where(h => !h.InModule)
                .GroupBy(h => h.NpPhysics)
                .Select(g => new {
                    NpPhysics = g.Key,
                    Count = g.Count(),
                    Actors = g.First().FirstSceneActors,
                    Rva = g.OrderBy(h => h.Rva).First().Rva
                })
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.Actors)
                .FirstOrDefault();
            if (best is null) return false;

            rva = (uint)best.Rva;
            npPhysics = best.NpPhysics;
            return true;
        }

        /// <summary>
        /// Runs the probe and logs all validated SDK pointer candidates. Safe to
        /// call multiple times - the probe never mutates state.
        /// </summary>
        public static void Run()
        {
            var unityBase = Memory.UnityBase;
            if (unityBase == 0)
            {
                Log.WriteLine("[PhysXProbe] UnityBase=0 (game not attached). Skipping.");
                return;
            }

            Log.WriteLine($"[PhysXProbe] Scanning UnityPlayer.dll @ 0x{unityBase:X} for NpPhysics SDK pointer...");

            ulong[] sigAddrs;
            try
            {
                sigAddrs = Memory.FindSignatures(PtrLoadPattern, "UnityPlayer.dll", MaxMatches);
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[PhysXProbe] Sig-scan failed: {ex.Message}");
                return;
            }

            if (sigAddrs.Length == 0)
            {
                Log.WriteLine("[PhysXProbe] No ptr-load sites found â€” sig pattern may need adjustment.");
                return;
            }

            Log.WriteLine($"[PhysXProbe] Found {sigAddrs.Length} ptr-load sites; decoding unique RVAs...");

            // Dedupe by resolved RVA â€” many call sites can decode to the same global.
            var seenRvas = new HashSet<ulong>(sigAddrs.Length);
            var uniqueRvas = new List<ulong>();
            foreach (var sigAddr in sigAddrs)
            {
                var rva = DecodeRipRelativeRva(sigAddr, unityBase);
                if (rva != 0 && seenRvas.Add(rva))
                    uniqueRvas.Add(rva);
            }

            Log.WriteLine($"[PhysXProbe] {uniqueRvas.Count} unique global RVAs to validate.");

            // Collect every survivor first, then aggregate by NpPhysics pointer. Many
            // RVAs can decode to the same singleton (Unity stores the SDK pointer in
            // several globals reached from different code paths); the singleton with
            // the most agreeing RVAs is the strongest candidate. We also flag any
            // candidate whose "NpPhysics" pointer lands inside UnityPlayer.dll itself
            // â€” those are coincidental hits walking through data-section bytes, not
            // a real heap-allocated PhysX singleton.
            var hits = new List<HitRecord>();
            ulong unityEnd = unityBase + GetUnityImageSize(unityBase);

            foreach (var rva in uniqueRvas)
            {
                if (TryValidate(unityBase + rva, out var npPhysics, out var sceneCount, out var firstSceneActors))
                {
                    bool inModule = npPhysics >= unityBase && npPhysics < unityEnd;
                    hits.Add(new HitRecord(rva, npPhysics, sceneCount, firstSceneActors, inModule));
                }
            }

            if (hits.Count == 0)
            {
                Log.WriteLine("[PhysXProbe] No candidate passed validation. " +
                              "Either no match in this build, or the struct offsets we tried " +
                              "(NpPhysics+0x08/0x10, NpScene+0x23D0) don't apply to Unity 6.");
                return;
            }

            // Group by resolved NpPhysics pointer; sort groups so the strongest
            // candidate is reported last (i.e. ends the log with the answer).
            // Strength ordering:
            //   â€¢ valid (non-in-module) groups before in-module groups
            //   â€¢ more agreeing RVAs first
            //   â€¢ larger actor count first (real scene beats empty subscene)
            var groups = hits
                .GroupBy(h => h.NpPhysics)
                .Select(g => new
                {
                    NpPhysics = g.Key,
                    Rvas = g.OrderBy(h => h.Rva).ToArray(),
                    InModule = g.First().InModule,
                    Scenes = g.First().Scenes,
                    Actors = g.First().FirstSceneActors,
                })
                .OrderBy(g => g.InModule)
                .ThenByDescending(g => g.Rvas.Length)
                .ThenByDescending(g => g.Actors)
                .ToArray();

            foreach (var g in groups)
            {
                string verdict;
                if (g.InModule)
                    verdict = "SUSPECT (pointer is inside UnityPlayer.dll â€” likely a false positive)";
                else if (g.Rvas.Length >= 2 && g.Actors > 0)
                    verdict = "LIKELY (multiple RVAs agree + non-empty scene)";
                else if (g.Actors > 0)
                    verdict = "POSSIBLE (single RVA, non-empty scene)";
                else
                    verdict = "WEAK (single RVA, scene0 has zero actors)";

                var rvaList = string.Join(", ", g.Rvas.Select(h => $"0x{h.Rva:X8}"));
                Log.WriteLine(
                    $"[PhysXProbe] {verdict}  np_physics=0x{g.NpPhysics:X}  " +
                    $"scenes={g.Scenes}  scene0.actors={g.Actors}  rvas=[{rvaList}]");
            }

            // The recommended pick is the FIRST listed group (sort order above puts
            // the strongest at the end of the printout, so we re-iterate to emit a
            // clean final-answer line for tooling).
            var best = groups
                .Where(g => !g.InModule)
                .OrderByDescending(g => g.Rvas.Length)
                .ThenByDescending(g => g.Actors)
                .FirstOrDefault();
            if (best is not null)
            {
                Log.WriteLine(
                    $"[PhysXProbe] DONE â€” use rva=0x{best.Rvas[0].Rva:X8} " +
                    $"(np_physics=0x{best.NpPhysics:X}, scenes={best.Scenes}, " +
                    $"scene0.actors={best.Actors}).");
            }
            else
            {
                Log.WriteLine("[PhysXProbe] DONE â€” all candidates were SUSPECT or WEAK. " +
                              "Re-check struct offsets for Unity 6 before adopting any RVA.");
            }
        }

        /// <summary>
        /// One validated candidate from the scan. Captured before aggregation so
        /// we can group / rank without re-reading memory.
        /// </summary>
        private sealed record HitRecord(
            ulong Rva,
            ulong NpPhysics,
            uint Scenes,
            uint FirstSceneActors,
            bool InModule);

        /// <summary>
        /// Pulls UnityPlayer.dll's image size from the loaded-module table so we
        /// can detect candidates whose "NpPhysics" pointer is actually inside the
        /// DLL itself (those are coincidental data-section hits, not real PhysX).
        /// Falls back to a generous 64 MB envelope if the live size isn't available
        /// â€” never under-reports, so the in-module flag stays conservative.
        /// </summary>
        private static ulong GetUnityImageSize(ulong unityBase)
        {
            uint size = Memory.GetModuleImageSize("UnityPlayer.dll");
            return size > 0 ? size : 0x4000000UL;
        }

        // â”€â”€ Internals â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Decodes a 7-byte <c>mov rax,[rip+rel32]</c> at <paramref name="sigAddr"/>
        /// into the target global's RVA inside <paramref name="moduleBase"/>.
        /// Returns 0 if the read failed or the target falls outside the module.
        /// </summary>
        private static ulong DecodeRipRelativeRva(ulong sigAddr, ulong moduleBase)
        {
            if (!Memory.TryReadValue<int>(sigAddr + (ulong)PtrLoadRelOffset, out var rel, false))
                return 0;
            ulong targetVa = sigAddr + (ulong)PtrLoadInstrLen + (ulong)(long)rel;
            return targetVa > moduleBase ? targetVa - moduleBase : 0;
        }

        /// <summary>
        /// Walks a candidate global at <paramref name="storageVa"/> as if it were
        /// the storage for an <c>NpPhysics*</c> singleton. Returns true with the
        /// resolved pointer and scene/actor counts when the structure is plausible.
        /// </summary>
        private static bool TryValidate(
            ulong storageVa,
            out ulong npPhysics,
            out uint sceneCount,
            out uint firstSceneActorCount)
        {
            npPhysics = 0;
            sceneCount = 0;
            firstSceneActorCount = 0;

            // 1. The storage must hold a valid VA.
            if (!Memory.TryReadPtr(storageVa, out var sdk, false) || !sdk.IsValidVirtualAddress())
                return false;

            // 2. The scene-array data pointer must be valid and the size small.
            if (!Memory.TryReadPtr(sdk + NpPhysics_SceneArrayData, out var scenesArrayPtr, false))
                return false;
            if (!Memory.TryReadValue<uint>(sdk + NpPhysics_SceneArraySize, out var nScenes, false))
                return false;
            if (nScenes == 0 || nScenes > MaxPlausibleScenes)
                return false;
            if (!scenesArrayPtr.IsValidVirtualAddress())
                return false;

            // 3. The first scene pointer must be valid.
            if (!Memory.TryReadPtr(scenesArrayPtr, out var scene0, false) || !scene0.IsValidVirtualAddress())
                return false;

            // 4. The first scene's actor count must be in a plausible range.
            //    Zero is only acceptable when there are multiple scenes (auxiliary
            //    scene can legitimately be empty); for a single-scene game, zero
            //    means we hit a different singleton with similar shape.
            if (!Memory.TryReadValue<uint>(scene0 + NpScene_RigidActorsSize, out var nActors, false))
                return false;
            if (nActors > MaxPlausibleActorsPerScene)
                return false;
            if (nActors == 0 && nScenes == 1)
                return false;

            npPhysics = sdk;
            sceneCount = nScenes;
            firstSceneActorCount = nActors;
            return true;
        }
    }
}
