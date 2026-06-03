// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;
using VmmSharpEx;
using static SDK.Offsets;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Loot
{
    /// <summary>
    /// Dumps container memory structures (ItemController, grids, items) to a text file
    /// for debugging and reverse engineering. Includes live memory pointers and field values.
    /// 
    /// <para><b>Usage:</b>
    /// <code>
    /// ContainerDumper.DumpAllContainers(game);  // Dump all containers to file
    /// ContainerDumper.DumpContainer(container, game);  // Dump a single container
    /// </code>
    /// </para>
    /// </summary>
    internal static class ContainerDumper
    {
        // ── Paths ────────────────────────────────────────────────────────────────
        private static readonly string DumpsDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dumps");

        // ── Entry points ─────────────────────────────────────────────────────────

        /// <summary>
        /// Dumps all containers in the game to a timestamped file.
        /// </summary>
        public static void DumpAllContainers(LocalGameWorld game)
        {
            try
            {
                if (game is null) return;

                var now = DateTime.UtcNow;
                string ts = now.ToString("yyyyMMdd_HHmmss");
                string filename = Path.Combine(DumpsDir, $"containers_{ts}.txt");

                Directory.CreateDirectory(DumpsDir);

                using var sw = new StreamWriter(filename, append: false);
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.WriteLine($"CONTAINER MEMORY DUMP — {now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine($"GameWorld Base: 0x{game.Base:X}");
                sw.WriteLine($"Total Containers: {game.Containers.Count}");
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.WriteLine();
                sw.Flush();

                int dumpCount = 0;
                foreach (var container in game.Containers)
                {
                    try
                    {
                        sw.WriteLine($"\n───────────────────────────────────────────────────────────────");
                        sw.WriteLine($"Container [{dumpCount}]: {container.Name} (ID: {container.Id})");
                        sw.WriteLine($"Searched: {container.Searched} | Position: {container.Position}");
                        sw.WriteLine($"───────────────────────────────────────────────────────────────");
                        sw.Flush();

                        DumpContainer(container, game, sw);
                        dumpCount++;
                    }
                    catch (Exception ex)
                    {
                        sw.WriteLine($"// ERROR dumping container: {ex.Message}");
                        sw.Flush();
                    }
                }

                sw.WriteLine();
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.WriteLine($"Dump complete. Containers dumped: {dumpCount}");
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.Flush();

                Log.WriteLine($"[ContainerDumper] Dumped {dumpCount} containers to: {filename}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[ContainerDumper] DumpAllContainers failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps a single container's ItemController and grid structures.
        /// </summary>
        public static void DumpContainer(LootContainer container, LocalGameWorld game)
        {
            try
            {
                var now = DateTime.UtcNow;
                string ts = now.ToString("yyyyMMdd_HHmmss");
                string filename = Path.Combine(DumpsDir, $"container_{container.Name}_{ts}.txt");

                Directory.CreateDirectory(DumpsDir);

                using var sw = new StreamWriter(filename, append: false);
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.WriteLine($"CONTAINER MEMORY DUMP — {now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine($"Container: {container.Name} (ID: {container.Id})");
                sw.WriteLine($"Searched: {container.Searched} | Position: {container.Position}");
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.WriteLine();
                sw.Flush();

                DumpContainer(container, game, sw);

                sw.WriteLine();
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.WriteLine("Dump complete.");
                sw.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                sw.Flush();

                Log.WriteLine($"[ContainerDumper] Dumped container to: {filename}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[ContainerDumper] DumpContainer failed: {ex.Message}");
            }
        }

        // ── Internal dump logic ──────────────────────────────────────────────────

        private static void DumpContainer(LootContainer container, LocalGameWorld game, StreamWriter sw)
        {
            try
            {
                sw.WriteLine($"// LootContainer: {container.Name}");
                sw.WriteLine($"// BSG ID: {container.Id}");
                sw.WriteLine($"// Position: X={container.Position.X:F2}, Y={container.Position.Y:F2}, Z={container.Position.Z:F2}");
                sw.WriteLine($"// Searched: {container.Searched}");
                sw.WriteLine();

                sw.WriteLine($"MEMORY STRUCTURE REFERENCE:");
                sw.WriteLine($"─────────────────────────────────────────────────────────");
                sw.WriteLine($"LootableContainer layout:");
                sw.WriteLine($"  ItemOwner (ItemController) @ offset 0x168");
                sw.WriteLine($"  Template @ offset 0x170");
                sw.WriteLine($"  ClosedPosition @ offset 0x178");
                sw.WriteLine($"  OpenPosition @ offset 0x184");
                sw.WriteLine();
                sw.WriteLine($"ItemController layout:");
                sw.WriteLine($"  MainStorage (Grid[]) @ offset 0xC8 - contains all container grids");
                sw.WriteLine($"  SearchController @ offset 0x20");
                sw.WriteLine($"  RootItem @ offset 0xD0");
                sw.WriteLine($"  Items (IEnumerable) @ fetched via MainStorage grids");
                sw.WriteLine();
                sw.WriteLine($"Grid layout:");
                sw.WriteLine($"  LayoutBuffer (List<bool>) @ offset 0x18 - slot occupation");
                sw.WriteLine($"  Horizontal (List<int>) @ offset 0x20");
                sw.WriteLine($"  Vertical (List<int>) @ offset 0x28");
                sw.WriteLine($"  GridWidth @ offset 0x34");
                sw.WriteLine($"  GridHeight @ offset 0x38");
                sw.WriteLine($"  ItemCollection @ offset 0x48 - contains actual items");
                sw.WriteLine($"  ID @ offset 0x70");
                sw.WriteLine();
                sw.WriteLine($"─────────────────────────────────────────────────────────");
                sw.WriteLine();

                // If we had access to the InteractiveClass pointer from LootManager, we could:
                // - Read ItemOwner (ItemController) at +0x168
                // - Dump all grids and their items
                // - Print actual slot layout and item positions
                // - Show InteractingPlayer state at +0x150
                sw.WriteLine($"DUMP INSTRUCTIONS:");
                sw.WriteLine($"To fully dump this container's memory:");
                sw.WriteLine($"1. Get LootableContainer pointer from LootList");
                sw.WriteLine($"2. Read ItemController ptr: container_base + 0x168");
                sw.WriteLine($"3. Call DumpItemController(itemControllerPtr, sw)");
                sw.WriteLine($"4. For each grid in MainStorage[], call DumpGrid()");
                sw.WriteLine();
                sw.WriteLine($"Current limitation: LootContainer object doesn't expose InteractiveClass pointer");
                sw.WriteLine($"Enhancement needed: Pass InteractiveClass from LootManager.ReadLootAndCorpses()");
            }
            catch (Exception ex)
            {
                sw.WriteLine($"// Failed to dump container: {ex.Message}");
            }
        }

        // ── Optional: Deep IL2CPP dumps ──────────────────────────────────────────

        /// <summary>
        /// Dumps ItemController class hierarchy and field values.
        /// Call with the resolved ItemController memory address.
        /// </summary>
        public static void DumpItemController(ulong itemControllerAddr, StreamWriter sw)
        {
            if (!itemControllerAddr.IsValidVirtualAddress())
            {
                sw.WriteLine("// ItemController: null or invalid address");
                return;
            }

            try
            {
                sw.WriteLine();
                sw.WriteLine("═══════════════════════════════════════════════════════════════");
                sw.WriteLine("ITEMCONTROLLER DEEP DUMP");
                sw.WriteLine("═══════════════════════════════════════════════════════════════");
                sw.WriteLine($"// ItemController @ 0x{itemControllerAddr:X}");
                sw.Flush();

                Il2CppDumper.DumpClassFieldsToWriter(itemControllerAddr, sw, $"ItemController @ 0x{itemControllerAddr:X}");
                sw.WriteLine();
                sw.Flush();

                // Read key fields
                sw.WriteLine($"// Key ItemController fields:");
                if (Memory.TryReadValue<uint>(itemControllerAddr + 0x98, out var nextOpId))
                    sw.WriteLine($"//   _nextOperationId @ +0x98: {nextOpId}");
                if (Memory.TryReadPtr(itemControllerAddr + 0xC0, out var idPtr) && idPtr.IsValidVirtualAddress())
                    sw.WriteLine($"//   <ID> @ +0xC0: 0x{idPtr:X}");

                // Try to read MainStorage grid array (most important for container contents)
                if (Memory.TryReadPtr(itemControllerAddr + 0xC8, out var mainStoragePtr) && mainStoragePtr.IsValidVirtualAddress())
                {
                    sw.WriteLine();
                    sw.WriteLine($"// MainStorage (Grid array) @ 0x{mainStoragePtr:X}");
                    sw.Flush();

                    try
                    {
                        using var grids = MemArray<ulong>.Get(mainStoragePtr, false);
                        sw.WriteLine($"//   Total grids: {grids.Count}");
                        sw.WriteLine();
                        sw.Flush();

                        // Dump each grid
                        for (int i = 0; i < grids.Count && i < 20; i++)
                        {
                            var gridPtr = grids[i];
                            if (gridPtr.IsValidVirtualAddress())
                            {
                                DumpGrid(gridPtr, i, sw);
                            }
                            else
                            {
                                sw.WriteLine($"// Grid[{i}]: null");
                            }
                        }

                        if (grids.Count > 20)
                            sw.WriteLine($"// ... ({grids.Count - 20} more grids not shown)");
                    }
                    catch (Exception ex)
                    {
                        sw.WriteLine($"//   ERROR walking MainStorage: {ex.Message}");
                    }
                }
                else
                {
                    sw.WriteLine("// MainStorage: null or invalid");
                }

                sw.Flush();
            }
            catch (Exception ex)
            {
                sw.WriteLine($"// ERROR dumping ItemController: {ex.Message}");
                sw.Flush();
            }
        }

        /// <summary>
        /// Dumps Grid class hierarchy and field values.
        /// Call with the resolved Grid memory address.
        /// </summary>
        public static void DumpGrid(ulong gridAddr, int gridIndex, StreamWriter sw)
        {
            if (!gridAddr.IsValidVirtualAddress())
            {
                sw.WriteLine($"// Grid[{gridIndex}]: null or invalid address");
                return;
            }

            try
            {
                sw.WriteLine($"───────────────────────────────────────────────────────────");
                sw.WriteLine($"Grid[{gridIndex}] @ 0x{gridAddr:X}");
                sw.WriteLine($"───────────────────────────────────────────────────────────");
                sw.Flush();

                Il2CppDumper.DumpClassFieldsToWriter(gridAddr, sw, $"Grid[{gridIndex}] @ 0x{gridAddr:X}");
                sw.WriteLine();
                sw.Flush();

                // Read and display grid dimensions
                int gridWidth = 0, gridHeight = 0;
                if (Memory.TryReadValue<int>(gridAddr + 0x34, out var width))
                {
                    gridWidth = width;
                    sw.WriteLine($"// GridWidth @ +0x34: {gridWidth}");
                }
                if (Memory.TryReadValue<int>(gridAddr + 0x38, out var height))
                {
                    gridHeight = height;
                    sw.WriteLine($"// GridHeight @ +0x38: {gridHeight}");
                }
                sw.WriteLine($"// Grid slots: {gridWidth} x {gridHeight} = {gridWidth * gridHeight} total");
                sw.WriteLine();
                sw.Flush();

                // Read LayoutBuffer (List<bool> of slot occupation)
                if (Memory.TryReadPtr(gridAddr + 0x18, out var layoutBufferPtr) && layoutBufferPtr.IsValidVirtualAddress())
                {
                    sw.WriteLine($"// LayoutBuffer (slot occupation) @ 0x{layoutBufferPtr:X}");
                    try
                    {
                        using var layoutList = MemList<bool>.Get(layoutBufferPtr, false);
                        int occupiedCount = layoutList.Count(x => x);
                        sw.WriteLine($"//   Total slots: {layoutList.Count}, Occupied: {occupiedCount}");

                        // Show grid visual representation (simple ASCII)
                        if (gridWidth > 0 && gridHeight > 0 && layoutList.Count == gridWidth * gridHeight)
                        {
                            sw.WriteLine($"//   Grid layout (X = occupied, . = empty):");
                            for (int y = 0; y < gridHeight && y < 20; y++)
                            {
                                sw.Write($"//     ");
                                for (int x = 0; x < gridWidth; x++)
                                {
                                    int idx = y * gridWidth + x;
                                    sw.Write(layoutList[idx] ? "X" : ".");
                                }
                                sw.WriteLine();
                            }
                            if (gridHeight > 20)
                                sw.WriteLine($"//     ... ({gridHeight - 20} more rows)");
                        }
                    }
                    catch (Exception ex)
                    {
                        sw.WriteLine($"//   ERROR reading LayoutBuffer: {ex.Message}");
                    }
                }
                else
                {
                    sw.WriteLine("// LayoutBuffer: null or invalid");
                }

                sw.WriteLine();
                sw.Flush();

                // Read ItemCollection (contains actual Item objects)
                if (Memory.TryReadPtr(gridAddr + 0x48, out var itemCollPtr) && itemCollPtr.IsValidVirtualAddress())
                {
                    sw.WriteLine($"// ItemCollection @ 0x{itemCollPtr:X}");
                    sw.Flush();

                    Il2CppDumper.DumpClassFieldsToWriter(itemCollPtr, sw, $"ItemCollection[{gridIndex}] @ 0x{itemCollPtr:X}");

                    try
                    {
                        // Try to iterate items in the collection
                        // ItemCollection contains GridItems which reference Items
                        sw.WriteLine($"//   Attempting to read ItemCollection contents...");
                        sw.Flush();

                        // Note: Full item enumeration is complex; this is a placeholder
                        sw.WriteLine($"//   (Full item enumeration requires understanding GridItemCollection structure)");
                    }
                    catch (Exception ex)
                    {
                        sw.WriteLine($"//   ERROR reading ItemCollection contents: {ex.Message}");
                    }
                }
                else
                {
                    sw.WriteLine("// ItemCollection: null or invalid");
                }

                sw.WriteLine();
                sw.Flush();
            }
            catch (Exception ex)
            {
                sw.WriteLine($"// ERROR dumping Grid[{gridIndex}]: {ex.Message}");
                sw.Flush();
            }
        }
    }
}
