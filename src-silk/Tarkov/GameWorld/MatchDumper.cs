// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using eft_dma_radar.Silk.Tarkov.GameWorld.Explosives;
using eft_dma_radar.Silk.Tarkov.GameWorld.Exits;
using eft_dma_radar.Silk.Tarkov.GameWorld.Loot;
using eft_dma_radar.Silk.Tarkov.GameWorld.Player;
using eft_dma_radar.Silk.Tarkov.Unity.Collections;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;
using static SDK.Offsets;

namespace eft_dma_radar.Silk.Tarkov.GameWorld
{
    /// <summary>
    /// Serializes the full live radar snapshot to a timestamped JSON file under
    /// &lt;exe&gt;\dumps\&lt;mapId&gt;_&lt;timestamp&gt;.json.
    ///
    /// <para><b>Toggle:</b> set <see cref="Enabled"/> to <c>true</c> / <c>false</c>
    /// in code, or bind it to <see cref="Config.SilkConfig.EnableMatchDump"/> at the
    /// call site.  Nothing runs when disabled — zero overhead.</para>
    ///
    /// <para><b>Sections in the dump:</b>
    /// <list type="bullet">
    ///   <item><b>players</b>  — all tracked players with full properties per
    ///     <see cref="Player.Player"/>, gear, and hands.</item>
    ///   <item><b>lootItems</b>  — loose loot (<see cref="LootItem"/> per entry).</item>
    ///   <item><b>lootCorpses</b>  — corpses with equipment inventory.</item>
    ///   <item><b>lootContainers</b>  — static containers (toolboxes, bags…).</item>
    ///   <item><b>lootAirdrops</b>  — airdrop containers.</item>
    ///   <item><b>exfils</b>  — extraction points with current status.</item>
    ///   <item><b>explosives</b>  — active grenades / tripwires / mortars.</item>
    ///   <item><b>killfeed</b>  — kill events from the current session.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class MatchDumper
    {
        // ── Toggle ───────────────────────────────────────────────────────────────
        // Flip this to enable/disable the whole system with a single line change.
        // At runtime the config flag SilkConfig.EnableMatchDump is also checked.
        private const bool EnabledByDefault = false;

        // Flip this to also write an IL2CPP class hierarchy dump (offsets, parents, values)
        // alongside the JSON snapshot. Runs on its own background thread — does not delay
        // the JSON write. May take 10-30s during an active raid due to DMA read volume.
        private const bool EnableIl2CppDump = true;

        // Maps like Interchange have 250+ doors, all sharing the same WorldInteractiveObject
        // class layout. Dumping the full IL2CPP hierarchy for every single one takes minutes
        // and produces near-identical content. Cap full-hierarchy door dumps to this many;
        // the rest still get a metadata line (id, state, key, position).
        private const int DoorFullDumpLimit = 5;

        // ── Paths ────────────────────────────────────────────────────────────────
        private static readonly string DumpsDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dumps");

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // ── Guard ─────────────────────────────────────────────────────────────────
        // Track the last dump timestamp per map so repeated "dump now" requests
        // can't flood the disk on rapid clicks.
        private static DateTime _lastDumpTime = DateTime.MinValue;
        private static readonly TimeSpan MinDumpInterval = TimeSpan.FromSeconds(5);

        // ── Active IL2CPP dump task ───────────────────────────────────────────────
        // Kept so Memory.Close() can drain it before VMM is disposed, preventing
        // a mid-write truncation when the user closes the app or exits the raid.
        private static volatile Task? _activeIl2CppTask;

        // ── Entry points ─────────────────────────────────────────────────────────

        /// <summary>
        /// Dumps the full match state to disk if the feature is enabled.
        /// Safe to call from any thread — JSON serialization and IO run on a
        /// background <see cref="ThreadPool"/> thread to avoid blocking callers.
        /// </summary>
        /// <param name="game">The active <see cref="LocalGameWorld"/> instance.</param>
        public static void DumpAsync(LocalGameWorld game)
        {
            try
            {
                if (game is null) return;
                if (!SilkProgram.Config.EnableMatchDump && !EnabledByDefault)
                    return;

                var now = DateTime.UtcNow;
                if (now - _lastDumpTime < MinDumpInterval)
                {
                    Log.WriteLine("[MatchDumper] Skipped — too soon since last dump.");
                    return;
                }
                _lastDumpTime = now;

                // Move EVERYTHING to a background worker — including the snapshot build.
                // BuildSnapshot performs many DMA reads that can throw; doing it on the
                // calling (UI) thread previously turned a single bad read into a crash.
                var gameRef = game;
                string safeMap = string.Concat(game.MapID
                    .Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
                string ts = now.ToString("yyyyMMdd_HHmmss");

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var snapshot = BuildSnapshot(gameRef, now);
                        WriteSnapshot(snapshot);
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine($"[MatchDumper] Background dump failed: {ex}");
                    }
                });

                // IL2CPP dump is extremely heavy (many scatter reads per player/sub-object).
                // Run it as a completely separate task so it never delays the JSON write.
                // The task is stored in _activeIl2CppTask so DrainAsync() can await it
                // before VMM is disposed during shutdown, preventing file truncation.
                if (EnableIl2CppDump)
                {
                    _activeIl2CppTask = Task.Run(() =>
                    {
                        try { WriteIl2CppDump(gameRef, safeMap, ts); }
                        catch (Exception ex) { Log.WriteLine($"[MatchDumper] IL2CPP dump failed: {ex.Message}"); }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[MatchDumper] DumpAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Waits for any in-progress IL2CPP dump to finish, up to
        /// <paramref name="timeout"/>. Call this from <c>Memory.Close()</c>
        /// before VMM is disposed so the file is never truncated mid-write.
        /// </summary>
        public static void Drain(TimeSpan timeout)
        {
            var task = _activeIl2CppTask;
            if (task is null || task.IsCompleted) return;
            Log.WriteLine("[MatchDumper] Waiting for IL2CPP dump to finish before shutdown...");
            if (!task.Wait(timeout))
                Log.WriteLine("[MatchDumper] IL2CPP dump did not finish in time — file may be incomplete.");
        }

        // ── Snapshot builder ─────────────────────────────────────────────────────

        private static MatchSnapshot BuildSnapshot(LocalGameWorld game, DateTime timestamp)
        {
            // Players
            var players = new List<DumpPlayer>();
            var registeredPlayers = game.RegisteredPlayers;
            foreach (var p in registeredPlayers)
            {
                try
                {
                    // Only use already-cached Player properties — zero live DMA reads here.
                    // Any live read in a background thread contends with the main DMA loop
                    // and will block until the process exits.
                    players.Add(new DumpPlayer
                    {
                        Name = p.Name,
                        Type = p.Type.ToString(),
                        Side = p.Type switch
                        {
                            PlayerType.BEAR     => "BEAR",
                            PlayerType.USEC     => "USEC",
                            PlayerType.PScav    => "PScav",
                            PlayerType.AIScav   => "AIScav",
                            PlayerType.AIBoss   => "AIBoss",
                            PlayerType.AIRaider => "AIRaider",
                            _                   => p.Type.ToString()
                        },
                        IsLocalPlayer = p.IsLocalPlayer,
                        IsAlive = p.IsAlive,
                        IsActive = p.IsActive,
                        IsHuman = p.IsHuman,
                        Position = ToDumpVec(p.Position),
                        RotationYaw = p.RotationYaw,
                        RotationPitch = p.RotationPitch,
                        GroupId = p.GroupID,
                        SpawnGroupId = p.SpawnGroupID,
                        Level = p.Level,
                        ProfileId = p.ProfileId,
                        AccountId = p.AccountId,
                        HealthStatus = p.HealthStatus.ToString(),
                        GearValue = p.GearValue,
                        HasNVG = p.HasNVG,
                        HasThermal = p.HasThermal,
                        InHandsItem = p.InHandsItem,
                        InHandsAmmo = p.InHandsAmmo,
                        IsWeaponInHands = p.IsWeaponInHands,
                        Equipment = p.Equipment.Count > 0
                            ? p.Equipment.ToDictionary(
                                kv => kv.Key,
                                kv => new DumpGearItem { Long = kv.Value.Long, Short = kv.Value.Short, Price = kv.Value.Price })
                            : null,
                        Base = $"0x{p.Base:X}",
                    });
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[MatchDumper] Player snapshot failed ({p?.Name}): {ex.Message}");
                }
            }

            // Loose loot
            var loot = new List<DumpLootItem>();
            try
            {
                foreach (var i in game.Loot)
                {
                    try
                    {
                        loot.Add(new DumpLootItem
                        {
                            Id = i.Id,
                            Name = i.Name,
                            ShortName = i.ShortName,
                            Position = ToDumpVec(i.Position),
                            DisplayPrice = i.DisplayPrice,
                            IsQuestItem = i.IsQuestItem,
                            IsImportant = i.IsImportant,
                        });
                    }
                    catch { /* skip bad item */ }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Loot snapshot failed: {ex.Message}"); }

            // Corpses
            var corpses = new List<DumpLootCorpse>();
            try
            {
                foreach (var c in game.Corpses)
                {
                    try
                    {
                        // No live DMA reads — use only cached properties on LootCorpse.
                        corpses.Add(new DumpLootCorpse
                        {
                            Name = c.Name,
                            Position = ToDumpVec(c.Position),
                            TotalValue = c.TotalValue,
                            GearReady = c.GearReady,
                            Equipment = c.Equipment.Count > 0
                                ? c.Equipment.ToDictionary(
                                    kv => kv.Key,
                                    kv => new DumpCorpseGear { ShortName = kv.Value.ShortName, Name = kv.Value.Name, Price = kv.Value.Price })
                                : null,
                            InteractiveClass = $"0x{c.InteractiveClass:X}",
                        });
                    }
                    catch { /* skip bad corpse */ }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Corpse snapshot failed: {ex.Message}"); }

            // Containers
            var containers = new List<DumpLootContainer>();
            try
            {
                foreach (var c in game.Containers)
                {
                    try
                    {
                        containers.Add(new DumpLootContainer
                        {
                            Id = c.Id,
                            Name = c.Name,
                            Position = ToDumpVec(c.Position),
                            Searched = c.Searched,
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Container snapshot failed: {ex.Message}"); }

            // Airdrops
            var airdrops = new List<DumpLootAirdrop>();
            try
            {
                foreach (var a in game.Airdrops)
                {
                    try { airdrops.Add(new DumpLootAirdrop { Position = ToDumpVec(a.Position) }); }
                    catch { }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Airdrop snapshot failed: {ex.Message}"); }

            // Exfils
            var exfils = new List<DumpExfil>();
            try
            {
                if (game.Exfils is { } ex)
                {
                    foreach (var e in ex)
                    {
                        try
                        {
                            exfils.Add(new DumpExfil
                            {
                                Name = e.Name,
                                Position = ToDumpVec(e.Position),
                                Status = e.Status.ToString(),
                                IsSecret = e.IsSecret,
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Exfil snapshot failed: {ex.Message}"); }

            // Transits
            var transits = new List<DumpTransit>();
            try
            {
                if (game.Transits is { } tr)
                {
                    foreach (var t in tr)
                    {
                        try
                        {
                            transits.Add(new DumpTransit
                            {
                                Name = t.Name,
                                Position = ToDumpVec(t.Position),
                                IsActive = t.IsActive,
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Transit snapshot failed: {ex.Message}"); }

            // Doors
            var doors = new List<DumpDoor>();
            try
            {
                foreach (var d in game.Doors)
                {
                    try
                    {
                        doors.Add(new DumpDoor
                        {
                            Id = d.Id,
                            KeyId = d.KeyId,
                            KeyName = d.KeyName,
                            DoorState = d.DoorState.ToString(),
                            Position = ToDumpVec(d.Position),
                            Base = $"0x{d.Base:X}",
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Door snapshot failed: {ex.Message}"); }

            // Switches
            var switches = new List<DumpSwitch>();
            try
            {
                if (game.Switches is { } sws)
                {
                    foreach (var s in sws)
                    {
                        try
                        {
                            switches.Add(new DumpSwitch
                            {
                                Name = s.Name,
                                Type = s.Type.ToString(),
                                Position = ToDumpVec(s.Position),
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Switch snapshot failed: {ex.Message}"); }

            // Quest locations
            var questLocations = new List<DumpQuestLocation>();
            try
            {
                if (game.QuestLocations is { } qls)
                {
                    foreach (var q in qls)
                    {
                        try
                        {
                            questLocations.Add(new DumpQuestLocation
                            {
                                QuestId = q.QuestId,
                                QuestName = q.QuestName,
                                ZoneId = q.ZoneId,
                                ObjectiveId = q.ObjectiveId,
                                ObjectiveType = q.ObjectiveType.ToString(),
                                Optional = q.Optional,
                                Position = ToDumpVec(q.Position),
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Quest snapshot failed: {ex.Message}"); }

            // BTR
            DumpBtr? btr = null;
            try
            {
                if (game.Btr is { IsActive: true } b)
                {
                    btr = new DumpBtr
                    {
                        Position = ToDumpVec(b.Position),
                        CurrentSpeed = b.CurrentSpeed,
                        IsMoving = b.IsMoving,
                        State = b.State,
                        RouteState = b.RouteState,
                        TimeToEndPauseMs = b.TimeToEndPauseMs,
                        IsPaid = b.IsPaid,
                        TurretYawDeg = b.TurretYawDeg,
                        GunnerPtr = $"0x{b.GunnerPtr:X}",
                    };
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] BTR snapshot failed: {ex.Message}"); }

            // Explosives
            var explosives = new List<DumpExplosive>();
            try
            {
                if (game.Explosives is not null)
                {
                    foreach (var ex in game.Explosives.Snapshot)
                    {
                        try
                        {
                            explosives.Add(new DumpExplosive
                            {
                                Name = ex is eft_dma_radar.Silk.Tarkov.GameWorld.Explosives.Grenade g2 ? g2.Name : ex.GetType().Name,
                                Position = ToDumpVec(ex.Position),
                                IsActive = ex.IsActive,
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Explosive snapshot failed: {ex.Message}"); }

            // Killfeed
            var killfeed = new List<DumpKillfeedEntry>();
            try
            {
                foreach (var k in KillfeedManager.Entries)
                {
                    try
                    {
                        killfeed.Add(new DumpKillfeedEntry
                        {
                            Killer = k.Killer,
                            Victim = k.Victim,
                            Weapon = k.Weapon,
                            VictimLevel = k.VictimLevel,
                            KillerSide = k.KillerSide.ToString(),
                            Timestamp = k.Timestamp,
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] Killfeed snapshot failed: {ex.Message}"); }

            // Local player extended dump
            DumpLocalPlayer? localPlayer = null;
            try
            {
                if (game.LocalPlayer is Player.LocalPlayer lp)
                {
                    var lpBase = lp.Base;
                    string? movBase = null, hcBase = null, icBase = null, pbBase = null;
                    if (lpBase.IsValidVirtualAddress())
                    {
                        if (Memory.TryReadPtr(lpBase + Offsets.Player.MovementContext, out var mcPtr, false) && mcPtr.IsValidVirtualAddress())
                            movBase = $"0x{mcPtr:X}";
                        if (Memory.TryReadPtr(lpBase + Offsets.Player._healthController, out var hcPtr, false) && hcPtr.IsValidVirtualAddress())
                            hcBase = $"0x{hcPtr:X}";
                        if (Memory.TryReadPtr(lpBase + Offsets.Player._inventoryController, out var icPtr, false) && icPtr.IsValidVirtualAddress())
                            icBase = $"0x{icPtr:X}";
                        if (Memory.TryReadPtr(lpBase + Offsets.Player._playerBody, out var pbPtr, false) && pbPtr.IsValidVirtualAddress())
                            pbBase = $"0x{pbPtr:X}";
                    }
                    localPlayer = new DumpLocalPlayer
                    {
                        Name          = lp.Name,
                        Base          = $"0x{lpBase:X}",
                        MoveBase      = movBase,
                        HealthBase    = hcBase,
                        InventoryBase = icBase,
                        PlayerBodyBase = pbBase,
                        ProfilePtr    = $"0x{lp.ProfilePtr:X}",
                        ProfileId     = lp.ProfileId,
                        AccountId     = lp.AccountId,
                        IsPmc         = lp.IsPmc,
                        IsScav        = lp.IsScav,
                        EntryPoint    = lp.EntryPoint,
                        Position      = ToDumpVec(lp.Position),
                        LookPosition  = lp.HasLookPosition ? ToDumpVec(lp.LookPosition) : null,
                        RotationYaw   = lp.RotationYaw,
                        RotationPitch = lp.RotationPitch,
                        IsAlive       = lp.IsAlive,
                        IsADS         = lp.IsADS,
                        Energy        = lp.HealthReady ? lp.Energy : null,
                        Hydration     = lp.HealthReady ? lp.Hydration : null,
                        HealthStatus  = lp.HealthStatus.ToString(),
                        Level         = lp.Level,
                        GearValue     = lp.GearValue,
                        HasNVG        = lp.HasNVG,
                        HasThermal    = lp.HasThermal,
                        InHandsItem   = lp.InHandsItem,
                        InHandsAmmo   = lp.InHandsAmmo,
                        PWA           = $"0x{lp.PWA:X}",
                        Equipment     = lp.Equipment.Count > 0
                            ? lp.Equipment.ToDictionary(
                                kv => kv.Key,
                                kv => new DumpGearItem { Long = kv.Value.Long, Short = kv.Value.Short, Price = kv.Value.Price })
                            : null,
                    };
                }
            }
            catch (Exception ex) { Log.WriteLine($"[MatchDumper] LocalPlayer snapshot failed: {ex.Message}"); }

            return new MatchSnapshot
            {
                DumpedAt = timestamp,
                MapId = game.MapID,
                GameWorldBase = $"0x{game.Base:X}",
                LocalPlayer = localPlayer,
                Players = players,
                LootItems = loot,
                LootCorpses = corpses,
                LootContainers = containers,
                LootAirdrops = airdrops,
                Exfils = exfils,
                Transits = transits,
                Doors = doors,
                Switches = switches,
                QuestLocations = questLocations,
                Btr = btr,
                Explosives = explosives,
                Killfeed = killfeed,
            };
        }

        // ── Writers ──────────────────────────────────────────────────────────────

        private static void WriteSnapshot(MatchSnapshot snapshot)
        {
            try
            {
                Directory.CreateDirectory(DumpsDir);

                string safeMap = string.Concat(snapshot.MapId
                    .Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
                string ts = snapshot.DumpedAt.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(DumpsDir, $"{safeMap}_{ts}.json");

                string json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                File.WriteAllText(path, json, Encoding.UTF8);

                Log.WriteLine($"[MatchDumper] Dump written: {path}  " +
                    $"({snapshot.Players.Count} players, " +
                    $"{snapshot.LootItems.Count} loot, " +
                    $"{snapshot.LootCorpses.Count} corpses, " +
                    $"{snapshot.LootContainers.Count} containers, " +
                    $"{snapshot.LootAirdrops.Count} airdrops, " +
                    $"{snapshot.Exfils.Count} exfils, " +
                    $"{snapshot.Transits.Count} transits, " +
                    $"{snapshot.Doors.Count} doors, " +
                    $"{snapshot.Switches.Count} switches, " +
                    $"{snapshot.QuestLocations.Count} quests, " +
                    $"{snapshot.Explosives.Count} explosives, " +
                    $"{snapshot.Killfeed.Count} kills" +
                    (snapshot.Btr is not null ? ", btr" : "") + ")");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[MatchDumper] Write failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a full IL2CPP class hierarchy dump for every addressable raid object
        /// (GameWorld, players, corpses, exfils, grenades) to
        /// <c>dumps\&lt;map&gt;_&lt;ts&gt;_il2cpp.txt</c>.
        /// Each section follows the <see cref="Il2CppDumper.DumpClassFields"/> format:
        /// offset, type, field name, and live value, walking the full parent chain.
        /// </summary>
        private static void WriteIl2CppDump(LocalGameWorld game, string safeMap, string ts)
        {
            string path = Path.Combine(DumpsDir, $"{safeMap}_{ts}_il2cpp.txt");
            Log.WriteLine($"[MatchDumper] Writing IL2CPP dump to: {path}");

            try
            {
                Directory.CreateDirectory(DumpsDir);
                // Smaller buffer + AutoFlush=false; we manually Flush() after each section
                // so the file grows visibly during the long dump and survives early shutdown.
                using var sw = new StreamWriter(path, false, Encoding.UTF8, 4096);
                var dumpStart = DateTime.UtcNow;

                sw.WriteLine($"// IL2CPP Match Dump — {dumpStart:u}");
                sw.WriteLine($"// Map: {game.MapID}");
                sw.WriteLine($"// GameWorld @ 0x{game.Base:X}");
                sw.WriteLine();
                sw.Flush();

                // ── GameWorld ────────────────────────────────────────────────────
                if (!game.InRaid) { sw.WriteLine("// Raid ended before dump could complete."); return; }
                sw.WriteLine("═══════════════════════════════════════");
                sw.WriteLine("SECTION: GameWorld");
                sw.WriteLine("═══════════════════════════════════════");
                Il2CppDumper.DumpClassFieldsToWriter(game.Base, sw,
                    $"ClientLocalGameWorld @ 0x{game.Base:X} (map={game.MapID})");
                sw.Flush();
                Log.WriteLine($"[MatchDumper] GameWorld done ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");

                // ── Players ──────────────────────────────────────────────────────
                if (!game.InRaid) { sw.WriteLine("// Raid ended — stopping IL2CPP dump."); return; }
                sw.WriteLine("═══════════════════════════════════════");
                sw.WriteLine("SECTION: Players");
                sw.WriteLine("═══════════════════════════════════════");
                var dumpEntries = game.RegisteredPlayers.GetPlayerDumpEntries();
                // Only dump one representative player per PlayerType so the file stays manageable.
                var seenTypes = new HashSet<PlayerType>();
                foreach (var de in dumpEntries)
                {
                    if (!seenTypes.Add(de.Player.Type))
                        continue;
                    if (!game.InRaid) { sw.WriteLine("// Raid ended mid-player-dump — stopping."); break; }
                    try
                    {
                        if (!de.PlayerBase.IsValidVirtualAddress()) continue;
                        var p = de.Player;
                        var pLabel = $"{p.Name} [{p.Type}] @ 0x{de.PlayerBase:X}" +
                                     (p.IsLocalPlayer ? " (LOCAL)" : "");
                    // Augment observed players with side, server spawn-time, and numeric server ID
                    string extraInfo = "";
                    if (de.IsObserved)
                    {
                        const uint OPV_WorldTime = 0x68;
                        const uint OPV_ServerId  = 0x7C;
                        string sideStr = de.Player.Type switch
                        {
                                PlayerType.BEAR    => "BEAR",
                                PlayerType.USEC    => "USEC",
                                PlayerType.PScav   => "PScav",
                                PlayerType.AIScav  => "AIScav",
                                PlayerType.AIBoss  => "AIBoss",
                                PlayerType.AIRaider=> "AIRaider",
                                _                  => de.Player.Type.ToString()
                            };
                                string wtStr = Memory.TryReadValue<float>(de.PlayerBase + OPV_WorldTime, out var wt)
                                                ? wt.ToString("F1") : "?";
                                            string sidStr = Memory.TryReadValue<int>(de.PlayerBase + OPV_ServerId, out var sid)
                                        ? sid.ToString() : "?";
                                    string voiceStr = "?";
                                    if (Memory.TryReadPtr(de.PlayerBase + Offsets.ObservedPlayerView.Voice, out var voicePtr)
                                        && voicePtr.IsValidVirtualAddress()
                                        && Memory.TryReadUnityString(voicePtr, out var voiceVal)
                                        && voiceVal is not null)
                                        voiceStr = voiceVal;
                                    extraInfo = $"  side={sideStr}  worldTime={wtStr}s  serverId={sidStr}  voice={voiceStr}";
                    }
                    sw.WriteLine($"// {pLabel}  pos=({p.Position.X:F1},{p.Position.Y:F1},{p.Position.Z:F1})" +
                        $"  gear={p.GearValue}  health={p.HealthStatus}" +
                        $"  hands={p.InHandsItem ?? "?"}" +
                        $"  nvg={p.HasNVG}  thermal={p.HasThermal}{extraInfo}");

                    // Player base object
                    Il2CppDumper.DumpClassFieldsToWriter(de.PlayerBase, sw, pLabel);

                    // Sub-objects: InventoryController, Inventory, Equipment, HandsController, HealthController
                    DumpPlayerSubObjects(de, sw);
                    }
                    catch (Exception pex)
                    {
                        try { sw.WriteLine($"// Player dump failed: {pex.Message}"); } catch { }
                    }
                    sw.Flush();
                }
                Log.WriteLine($"[MatchDumper] Players done ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");

                // ── Corpses ──────────────────────────────────────────────────────
                if (!game.InRaid) { sw.WriteLine("// Raid ended — stopping IL2CPP dump."); return; }
                sw.WriteLine("═══════════════════════════════════════");
                sw.WriteLine("SECTION: Corpses");
                sw.WriteLine("═══════════════════════════════════════");
                foreach (var c in game.Corpses)
                {
                    try
                    {
                        if (!c.InteractiveClass.IsValidVirtualAddress()) continue;
                    var cLabel = $"Corpse '{c.Name}' @ 0x{c.InteractiveClass:X}";

                    // Read Side (0x148) and PlayerProfileID (0x188) directly from the corpse object.
                    // Side: 1=USEC, 2=BEAR, 4=Scav.
                    string corpseSideStr = "?";
                    if (Memory.TryReadValue<int>(c.InteractiveClass + 0x148, out var corpseSideRaw))
                        corpseSideStr = corpseSideRaw switch { 1 => "USEC", 2 => "BEAR", 4 => "Scav", _ => corpseSideRaw.ToString() };
                    string corpseProfileId = "?";
                    if (Memory.TryReadPtr(c.InteractiveClass + 0x188, out var corpseProfilePtr)
                        && corpseProfilePtr.IsValidVirtualAddress()
                        && Memory.TryReadUnityString(corpseProfilePtr, out var cpId)
                        && cpId is not null)
                        corpseProfileId = cpId;

                    sw.WriteLine($"// {cLabel}  pos=({c.Position.X:F1},{c.Position.Y:F1},{c.Position.Z:F1})" +
                        $"  side={corpseSideStr}  gear={c.TotalValue}  profileId={corpseProfileId}");
                    Il2CppDumper.DumpClassFieldsToWriter(c.InteractiveClass, sw, cLabel);
                    }
                    catch (Exception cex)
                    {
                        try { sw.WriteLine($"// Corpse dump failed: {cex.Message}"); } catch { }
                    }
                }
                sw.Flush();

                // ── Exfils ───────────────────────────────────────────────────────
                if (!game.InRaid) { sw.WriteLine("// Raid ended — stopping IL2CPP dump."); return; }
                if (game.Exfils is { } exfils)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Exfils");
                    sw.WriteLine("═══════════════════════════════════════");
                    foreach (var e in exfils)
                    {
                        try
                        {
                            if (!e.BaseAddr.IsValidVirtualAddress()) continue;
                            var eLabel = $"Exfil '{e.Name}' [{e.Status}] @ 0x{e.BaseAddr:X}";
                            sw.WriteLine($"// {eLabel}");
                            Il2CppDumper.DumpClassFieldsToWriter(e.BaseAddr, sw, eLabel);
                        }
                        catch { }
                    }
                }

                // ── Transits ─────────────────────────────────────────────────────
                if (game.Transits is { } transits && transits.Count > 0)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Transits");
                    sw.WriteLine("═══════════════════════════════════════");
                    foreach (var t in transits)
                    {
                        try
                        {
                            sw.WriteLine($"// Transit '{t.Name}' active={t.IsActive}" +
                                $"  pos=({t.Position.X:F1},{t.Position.Y:F1},{t.Position.Z:F1})");
                        }
                        catch { }
                    }
                    sw.WriteLine();
                }

                // ── Doors ────────────────────────────────────────────────────────
                if (!game.InRaid) { sw.WriteLine("// Raid ended — stopping IL2CPP dump."); return; }
                if (game.Doors is { } doors && doors.Count > 0)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine($"SECTION: Doors (full hierarchy on first {DoorFullDumpLimit}, metadata only after)");
                    sw.WriteLine("═══════════════════════════════════════");
                    int dIdx = 0;
                    foreach (var d in doors)
                    {
                        try
                        {
                            if (!d.Base.IsValidVirtualAddress()) continue;
                            var dLabel = $"Door '{d.Id}' [{d.DoorState}] @ 0x{d.Base:X}";
                            sw.WriteLine($"// {dLabel}  key={d.KeyId ?? "?"} ({d.KeyName ?? "?"})" +
                                $"  pos=({d.Position.X:F1},{d.Position.Y:F1},{d.Position.Z:F1})");
                            if (dIdx < DoorFullDumpLimit)
                                Il2CppDumper.DumpClassFieldsToWriter(d.Base, sw, dLabel);
                            dIdx++;
                        }
                        catch { }
                    }
                    sw.Flush();
                    Log.WriteLine($"[MatchDumper] Doors done ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s, full={Math.Min(dIdx, DoorFullDumpLimit)}/{doors.Count})");
                }

                Log.WriteLine($"[MatchDumper] Section Switches starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Switches ─────────────────────────────────────────────────────
                if (game.Switches is { } switches && switches.Count > 0)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Switches");
                    sw.WriteLine("═══════════════════════════════════════");
                    foreach (var s in switches)
                    {
                        try
                        {
                            sw.WriteLine($"// Switch '{s.Name}' [{s.Type}]" +
                                $"  pos=({s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1})");
                        }
                        catch { }
                    }
                    sw.WriteLine();
                }

                sw.Flush();
                Log.WriteLine($"[MatchDumper] Section BTR starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── BTR ──────────────────────────────────────────────────────────
                if (game.Btr is { IsActive: true } btrTracker)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: BTR");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        sw.WriteLine($"// BTR pos=({btrTracker.Position.X:F1},{btrTracker.Position.Y:F1},{btrTracker.Position.Z:F1})" +
                            $"  speed={btrTracker.CurrentSpeed:F2}  state={btrTracker.State}  routeState={btrTracker.RouteState}" +
                            $"  paid={btrTracker.IsPaid}  turretYaw={btrTracker.TurretYawDeg:F1}" +
                            $"  gunner=0x{btrTracker.GunnerPtr:X}");
                        btrTracker.DumpAll(sw);
                    }
                    catch { }
                    sw.WriteLine();
                }

                Log.WriteLine($"[MatchDumper] Section QuestLocations starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Quest locations ──────────────────────────────────────────────
                if (game.QuestLocations is { } qls && qls.Count > 0)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: QuestLocations (metadata only)");
                    sw.WriteLine("═══════════════════════════════════════");
                    foreach (var q in qls)
                    {
                        try
                        {
                            sw.WriteLine($"// [{q.QuestId}] '{q.QuestName}' zone={q.ZoneId}" +
                                $"  obj={q.ObjectiveType}  optional={q.Optional}" +
                                $"  pos=({q.Position.X:F1},{q.Position.Y:F1},{q.Position.Z:F1})");
                        }
                        catch { }
                    }
                    sw.WriteLine();
                }

                Log.WriteLine($"[MatchDumper] Section Grenades starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Grenades/Explosives ──────────────────────────────────────────
                if (game.Explosives?.Snapshot is { } grenades && grenades.Count > 0)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Explosives");
                    sw.WriteLine("═══════════════════════════════════════");
                    foreach (var ex in grenades)
                    {
                        try
                        {
                            if (ex is not eft_dma_radar.Silk.Tarkov.GameWorld.Explosives.Grenade g) continue;
                            if (!((ulong)g).IsValidVirtualAddress()) continue;
                            var gLabel = $"Grenade '{g.Name}' @ 0x{(ulong)g:X}";
                            sw.WriteLine($"// {gLabel}  active={g.IsActive}");
                            Il2CppDumper.DumpClassFieldsToWriter((ulong)g, sw, gLabel);
                        }
                        catch { }
                    }
                }

                Log.WriteLine($"[MatchDumper] Section LootItems starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Loot items (no object addresses — metadata only) ─────────────
                sw.WriteLine("═══════════════════════════════════════");
                sw.WriteLine("SECTION: LootItems (metadata only — no object address available)");
                sw.WriteLine("═══════════════════════════════════════");
                foreach (var item in game.Loot)
                {
                    sw.WriteLine($"// [{item.Id}] {item.Name}  price={item.DisplayPrice}" +
                        $"  pos=({item.Position.X:F1},{item.Position.Y:F1},{item.Position.Z:F1})");
                }
                sw.WriteLine();

                Log.WriteLine($"[MatchDumper] Section Containers starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Containers (no object address — metadata only) ────────────────
                sw.WriteLine("═══════════════════════════════════════");
                sw.WriteLine("SECTION: Containers (metadata only — no object address available)");
                sw.WriteLine("═══════════════════════════════════════");
                foreach (var c in game.Containers)
                {
                    sw.WriteLine($"// [{c.Id}] {c.Name}  searched={c.Searched}" +
                        $"  pos=({c.Position.X:F1},{c.Position.Y:F1},{c.Position.Z:F1})");
                }
                sw.WriteLine();

                Log.WriteLine($"[MatchDumper] Section Airdrops starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Airdrops ─────────────────────────────────────────────────────
                if (game.InRaid)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Airdrops");
                    sw.WriteLine("═══════════════════════════════════════");

                    // Cached airdrop positions from LootManager
                    var airdrops = game.Airdrops;
                    sw.WriteLine($"// Cached airdrop count: {airdrops.Count}");
                    for (int ai = 0; ai < airdrops.Count; ai++)
                    {
                        var a = airdrops[ai];
                        sw.WriteLine($"// [airdrop-{ai}] pos=({a.Position.X:F1},{a.Position.Y:F1},{a.Position.Z:F1})");
                    }

                    // Live AirdropManager via GameWorld → SynchronizableObjectLogicProcessor (+0x0248) → AirdropManager (+0x0038)
                    try
                    {
                        const uint OffSyncObjLogicProcessor = 0x0248;
                        const uint OffAirdropManager        = 0x0038;
                        if (Memory.TryReadPtr(game.Base + OffSyncObjLogicProcessor, out var syncObjProc)
                            && syncObjProc.IsValidVirtualAddress()
                            && Memory.TryReadPtr(syncObjProc + OffAirdropManager, out var airdropMgr)
                            && airdropMgr.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(airdropMgr, sw,
                                $"AirdropManager @ 0x{airdropMgr:X}");

                            // _isInited flag
                            if (Memory.TryReadValue<bool>(airdropMgr + Offsets.AirdropManager._isInited, out var inited))
                                sw.WriteLine($"//   _isInited = {inited}");

                            // CachedAirdropParameters (inline struct at +0x28)
                            try
                            {
                                ulong cachedParams = airdropMgr + Offsets.AirdropManager.CachedAirdropParameters;
                                sw.WriteLine($"//   CachedAirdropParameters @ 0x{cachedParams:X}:");
                                if (Memory.TryReadValue<int>(cachedParams + Offsets.AirdropParameters.PlaneAirdropStartMin, out var startMin))
                                    sw.WriteLine($"//     PlaneAirdropStartMin={startMin}");
                                if (Memory.TryReadValue<int>(cachedParams + Offsets.AirdropParameters.PlaneAirdropStartMax, out var startMax))
                                    sw.WriteLine($"//     PlaneAirdropStartMax={startMax}");
                                if (Memory.TryReadValue<int>(cachedParams + Offsets.AirdropParameters.PlaneAirdropEnd, out var end))
                                    sw.WriteLine($"//     PlaneAirdropEnd={end}");
                                if (Memory.TryReadValue<float>(cachedParams + Offsets.AirdropParameters.PlaneAirdropChance, out var chance))
                                    sw.WriteLine($"//     PlaneAirdropChance={chance:F3}");
                                if (Memory.TryReadValue<int>(cachedParams + Offsets.AirdropParameters.PlaneAirdropMax, out var max))
                                    sw.WriteLine($"//     PlaneAirdropMax={max}");
                                if (Memory.TryReadValue<int>(cachedParams + Offsets.AirdropParameters.MinPlayersCountToSpawnAirdrop, out var minPlayers))
                                    sw.WriteLine($"//     MinPlayersCountToSpawnAirdrop={minPlayers}");
                            }
                            catch { sw.WriteLine("//   CachedAirdropParameters read failed"); }

                            // _airdropPoints — List<AirdropPoint> read via MemList
                            try
                            {
                                if (Memory.TryReadPtr(airdropMgr + Offsets.AirdropManager._airdropPoints, out var pointsListObj)
                                    && pointsListObj.IsValidVirtualAddress())
                                {
                                    using var pts = MemList<ulong>.Get(pointsListObj, false);
                                    if (pts.Count > 0)
                                    {
                                        sw.WriteLine($"//   _airdropPoints count={pts.Count}");
                                        for (int pi = 0; pi < pts.Count; pi++)
                                        {
                                            var pt = pts[pi];
                                            if (!pt.IsValidVirtualAddress()) continue;
                                            Il2CppDumper.DumpClassFieldsToWriter(pt, sw,
                                                $"    AirdropPoint[{pi}] @ 0x{pt:X}");
                                        }
                                    }
                                    else
                                    {
                                        sw.WriteLine("//   _airdropPoints: empty or not yet populated");
                                    }
                                }
                                else
                                {
                                    sw.WriteLine("//   _airdropPoints: list pointer null");
                                }
                            }
                            catch { sw.WriteLine("//   _airdropPoints walk failed"); }
                        }
                        else
                        {
                            sw.WriteLine("// AirdropManager: pointer null or not yet initialized (no airdrop spawned yet this raid)");
                        }
                    }
                    catch { sw.WriteLine("// AirdropManager dump failed — skip"); }

                    sw.WriteLine();
                }

                Log.WriteLine($"[MatchDumper] Section Explosives starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Explosives (grenades, tripwires, mortar projectiles) ──────────
                if (game.InRaid)
                {
                    var explosives = game.Explosives?.Snapshot;
                    if (explosives is not null && explosives.Count > 0)
                    {
                        sw.WriteLine("═══════════════════════════════════════");
                        sw.WriteLine("SECTION: Explosives");
                        sw.WriteLine("═══════════════════════════════════════");
                        foreach (var ex in explosives)
                        {
                            if (!game.InRaid) { sw.WriteLine("// Raid ended — stopping IL2CPP dump."); break; }
                            try
                            {
                                if (!ex.Addr.IsValidVirtualAddress()) continue;
                                var p = ex.Position;
                                var typeName = ex.GetType().Name;
                                sw.WriteLine($"// {typeName}  active={ex.IsActive}" +
                                    $"  pos=({p.X:F1},{p.Y:F1},{p.Z:F1})");
                                Il2CppDumper.DumpClassFieldsToWriter(ex.Addr, sw,
                                    $"{typeName} @ 0x{ex.Addr:X}");
                            }
                            catch { }
                        }
                        sw.WriteLine();
                    }
                }

                Log.WriteLine($"[MatchDumper] Section ClientShellingController starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── ClientShellingController (artillery / mortar shelling) ─────────
                if (game.InRaid)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: ClientShellingController");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.ClientShellingController, out var shellingCtrl)
                            && shellingCtrl.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(shellingCtrl, sw,
                                $"ClientShellingController @ 0x{shellingCtrl:X}");

                            // ActiveClientProjectiles — Dictionary<int, ulong> (same layout as ExplosivesManager reads)
                            try
                            {
                                if (Memory.TryReadPtr(shellingCtrl + Offsets.ClientShellingController.ActiveClientProjectiles, out var projCollObj)
                                    && projCollObj.IsValidVirtualAddress())
                                {
                                    using var projs = MemDictionary<int, ulong>.Get(projCollObj, false);
                                    if (projs.Count > 0)
                                    {
                                        sw.WriteLine($"//   ActiveClientProjectiles count={projs.Count}");
                                        int pi = 0;
                                        foreach (var entry in projs)
                                        {
                                            if (!entry.Value.IsValidVirtualAddress()) continue;
                                            Il2CppDumper.DumpClassFieldsToWriter(entry.Value, sw,
                                                $"  ArtilleryProjectileClient[key={entry.Key}] @ 0x{entry.Value:X}");
                                            pi++;
                                        }
                                    }
                                    else
                                    {
                                        sw.WriteLine("//   ActiveClientProjectiles: empty or not yet populated");
                                    }
                                }
                            }
                            catch { sw.WriteLine("//   ActiveClientProjectiles walk failed"); }
                        }
                        else
                        {
                            sw.WriteLine("// ClientShellingController: pointer null (no artillery on this map)");
                        }
                    }
                    catch (Exception ex) { sw.WriteLine($"// ClientShellingController dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section BallisticsCalculator starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── BallisticsCalculator (per-raid singleton — owner of every in-flight Shot) ─────
                // Prefer _sharedBallisticsCalculator (always populated in raid). Falls back to
                // <ClientBallisticCalculator>k__BackingField which is non-null only in some modes.
                if (game.InRaid)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: BallisticsCalculator");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        ulong calcPtr = 0;
                        string source = "_sharedBallisticsCalculator";
                        if (!Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.SharedBallisticsCalculator, out calcPtr)
                            || !calcPtr.IsValidVirtualAddress())
                        {
                            if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.ClientBallisticCalculator, out var fallback)
                                && fallback.IsValidVirtualAddress())
                            {
                                calcPtr = fallback;
                                source = "<ClientBallisticCalculator>k__BackingField";
                            }
                        }

                        if (calcPtr.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(calcPtr, sw,
                                $"BallisticsCalculator (via {source}) @ 0x{calcPtr:X}");

                            // Dump the Shots list head and the first few Shot entries so we can
                            // confirm offsets for Shot.CurrentPosition, Velocity, G1, etc.
                            try
                            {
                                if (Memory.TryReadPtr(calcPtr + Offsets.BallisticsCalculator.Shots, out var shotsList)
                                    && shotsList.IsValidVirtualAddress())
                                {
                                    Il2CppDumper.DumpClassFieldsToWriter(shotsList, sw,
                                        $"  Shots (List<Shot>) @ 0x{shotsList:X}");

                                    // List<T> layout: _items @ 0x10, _size @ 0x18.
                                    if (Memory.TryReadPtr(shotsList + 0x10, out var itemsArr)
                                        && itemsArr.IsValidVirtualAddress()
                                        && Memory.TryReadValue<int>(shotsList + 0x18, out var size)
                                        && size > 0)
                                    {
                                        int dumpCount = Math.Min(size, 3);
                                        sw.WriteLine($"//   Shots count={size}, dumping first {dumpCount}");
                                        // IL2CPP array data starts at +0x20.
                                        for (int i = 0; i < dumpCount; i++)
                                        {
                                            if (!Memory.TryReadPtr(itemsArr + (uint)(0x20 + i * 8), out var shotPtr)
                                                || !shotPtr.IsValidVirtualAddress())
                                                continue;
                                            Il2CppDumper.DumpClassFieldsToWriter(shotPtr, sw,
                                                $"    Shot[{i}] @ 0x{shotPtr:X}");
                                        }
                                    }
                                    else
                                    {
                                        sw.WriteLine("//   Shots: empty (no in-flight bullets right now)");
                                    }
                                }
                                else
                                {
                                    sw.WriteLine("//   Shots pointer null — offset may need updating");
                                }
                            }
                            catch (Exception sex) { sw.WriteLine($"//   Shots walk failed: {sex.Message}"); }
                        }
                        else
                        {
                            sw.WriteLine("// BallisticsCalculator: both _shared and <ClientBallisticCalculator> pointers null");
                        }

                        // Also dump the local player's FirearmController so we can correlate
                        // BallisticsCalculator state with the held weapon's BallisticsCalculator ref.
                        try
                        {
                            var lp = game.LocalPlayer;
                            if (lp is not null
                                && Memory.TryReadPtr(lp.Base + Offsets.Player._handsController, out var handsPtr)
                                && handsPtr.IsValidVirtualAddress())
                            {
                                Il2CppDumper.DumpClassFieldsToWriter(handsPtr, sw,
                                    $"  LocalPlayer._handsController @ 0x{handsPtr:X}");
                            }
                        }
                        catch { /* best-effort */ }
                    }
                    catch (Exception ex) { sw.WriteLine($"// BallisticsCalculator dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section WeatherController starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── WeatherController (singleton via TypeIndex) ───────────────────
                if (game.InRaid)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: WeatherController");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        var klassPtr = Il2CppDumper.ResolveKlassByTypeIndex(Offsets.Special.WeatherController_TypeIndex);
                        if (klassPtr.IsValidVirtualAddress())
                        {
                            var staticFields = Memory.ReadPtr(klassPtr + Offsets.Il2CppClass.StaticFields, false);
                            if (staticFields.IsValidVirtualAddress())
                            {
                                var instance = Memory.ReadPtr(staticFields + Offsets.WeatherController.Instance, false);
                                if (instance.IsValidVirtualAddress())
                                    Il2CppDumper.DumpClassFieldsToWriter(instance, sw,
                                        $"WeatherController @ 0x{instance:X}");
                                else
                                    sw.WriteLine("// WeatherController: instance pointer null");
                            }
                            else
                                sw.WriteLine("// WeatherController: static fields pointer invalid");
                        }
                        else
                            sw.WriteLine("// WeatherController: TypeIndex not resolved");
                    }
                    catch (Exception ex) { sw.WriteLine($"// WeatherController dump failed: {ex.Message}"); }
                    sw.WriteLine();
                }

                Log.WriteLine($"[MatchDumper] Section EFTHardSettings starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── EFTHardSettings (singleton via TypeIndex resolver) ────────────
                if (game.InRaid)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: EFTHardSettings");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        var instance = EftHardSettingsResolver.GetInstance();
                        if (instance.IsValidVirtualAddress())
                            Il2CppDumper.DumpClassFieldsToWriter(instance, sw,
                                $"EFTHardSettings @ 0x{instance:X}");
                        else
                            sw.WriteLine("// EFTHardSettings: instance not resolved");
                    }
                    catch (Exception ex) { sw.WriteLine($"// EFTHardSettings dump failed: {ex.Message}"); }
                    sw.WriteLine();
                }

                Log.WriteLine($"[MatchDumper] Section LevelSettings starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── LevelSettings (GOM scan resolver) ────────────────────────────
                // Use cached value only — full GOM scan can iterate up to 200k linked-list
                // nodes synchronously and stall the dump for minutes. ResolveAsync() runs in
                // the background; on subsequent dumps the cached pointer will be present.
                if (game.InRaid)
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: LevelSettings");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        ulong instance = 0;
                        if (!LevelSettingsResolver.TryGetCached(out instance))
                        {
                            // Kick off background resolve for next dump; do not block here.
                            LevelSettingsResolver.ResolveAsync();
                        }
                        if (instance.IsValidVirtualAddress())
                            Il2CppDumper.DumpClassFieldsToWriter(instance, sw,
                                $"LevelSettings @ 0x{instance:X}");
                        else
                            sw.WriteLine("// LevelSettings: not cached yet — async resolve queued (will appear in next dump)");
                    }
                    catch (Exception ex) { sw.WriteLine($"// LevelSettings dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section WorldController starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── WorldController (GameWorld._world @ +0x218 → Interactables + _lootSyncPackets) ──
                // EFT.ClientWorld._lootSyncPackets @ +0x0118 : List<LootSyncPacket>
                // EFT.LootSyncPacket struct layout (EFT.LootSyncPacket [6341]):
                //   +0x00  _rotationQuantizer     : class (static shared)
                //   +0x08  _velocityQuantizer     : class (static shared)
                //   +0x10  Id                     : int
                //   +0x14  Position               : Vector3 (12 bytes)
                //   +0x20  Rotation               : Quaternion (16 bytes)
                //   +0x30  Velocity               : Vector3 (12 bytes)
                //   +0x3C  AngularVelocity        : Vector3 (12 bytes)
                //   +0x48  Done                   : bool
                //   stride = 0x50 (80 bytes)
                // NOTE: no InRaid guard — game.Base pointers remain valid after raid ends
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: WorldController");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        const uint OffWorld = 0x218;
                        if (Memory.TryReadPtr(game.Base + OffWorld, out var world)
                            && world.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(world, sw,
                                $"WorldController @ 0x{world:X}");

                            if (Memory.TryReadPtr(world + Offsets.WorldController.Interactables, out var interactables)
                                && interactables.IsValidVirtualAddress())
                            {
                                Il2CppDumper.DumpClassFieldsToWriter(interactables, sw,
                                    $"  Interactables list @ 0x{interactables:X}");
                            }

                            // ── _lootSyncPackets (ClientWorld @ +0x0118) ─────────────
                            // List<EFT.LootSyncPacket> — pending packets waiting to be
                            // processed by ProcessLootSyncPackets().  Each struct is 0x50
                            // bytes; Id/Position/Done are the diagnostic fields we care about.
                            try
                            {
                                const uint OffLootSyncPackets = 0x0118; // EFT.ClientWorld._lootSyncPackets
                                const uint ListCountOff       = 0x18;   // System.Collections.Generic.List<T>._size
                                const uint ListArrOff         = 0x10;   // List<T>._items (T[])
                                const uint ArrElemsOff        = 0x20;   // Array elements start (Unity: klass(8)+bounds(8)+count(4)+pad(4))
                                const uint StridePacket       = 0x50;   // sizeof(EFT.LootSyncPacket) — padded to 0x50
                                const uint OffId              = 0x10;   // LootSyncPacket.Id
                                const uint OffPosX            = 0x14;   // LootSyncPacket.Position.x
                                const uint OffPosY            = 0x18;   // LootSyncPacket.Position.y
                                const uint OffPosZ            = 0x1C;   // LootSyncPacket.Position.z
                                const uint OffDone            = 0x48;   // LootSyncPacket.Done
                                const int  MaxPackets         = 64;

                                if (Memory.TryReadPtr(world + OffLootSyncPackets, out var lspList)
                                    && lspList.IsValidVirtualAddress()
                                    && Memory.TryReadValue<int>(lspList + ListCountOff, out var pktCount)
                                    && pktCount > 0
                                    && Memory.TryReadPtr(lspList + ListArrOff, out var lspArr)
                                    && lspArr.IsValidVirtualAddress())
                                {
                                    int dumpCount = Math.Min(pktCount, MaxPackets);
                                    sw.WriteLine($"//   _lootSyncPackets list @ 0x{lspList:X}  count={pktCount}" +
                                        (pktCount > MaxPackets ? $"  (showing first {MaxPackets})" : ""));
                                    ulong elemsBase = lspArr + ArrElemsOff;
                                    for (int pi = 0; pi < dumpCount; pi++)
                                    {
                                        ulong elem = elemsBase + (ulong)(pi * StridePacket);
                                        Memory.TryReadValue<int>(elem + OffId, out var pktId);
                                        Memory.TryReadValue<float>(elem + OffPosX, out var px);
                                        Memory.TryReadValue<float>(elem + OffPosY, out var py);
                                        Memory.TryReadValue<float>(elem + OffPosZ, out var pz);
                                        Memory.TryReadValue<bool>(elem + OffDone, out var done);
                                        sw.WriteLine($"//     [pkt-{pi:D3}] Id={pktId,-6}  pos=({px:F2},{py:F2},{pz:F2})  done={done}  @ 0x{elem:X}");
                                    }
                                }
                                else
                                {
                                    sw.WriteLine($"//   _lootSyncPackets: list @ 0x{world + OffLootSyncPackets:X} — empty or null");
                                }
                            }
                            catch (Exception lspEx)
                            {
                                sw.WriteLine($"//   _lootSyncPackets walk failed: {lspEx.Message}");
                            }
                        }
                        else
                        {
                            sw.WriteLine("// WorldController: pointer null");
                        }
                    }
                    catch (Exception ex) { sw.WriteLine($"// WorldController dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section MineManager starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── MineManager (GameWorld @ +0x250) ─────────────────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: MineManager");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        const uint OffMineManager = 0x250;
                        if (Memory.TryReadPtr(game.Base + OffMineManager, out var mineMgr)
                            && mineMgr.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(mineMgr, sw,
                                $"MineManager @ 0x{mineMgr:X}");
                        }
                        else
                        {
                            sw.WriteLine("// MineManager: pointer null (no mines on this map)");
                        }
                    }
                    catch (Exception ex) { sw.WriteLine($"// MineManager dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section SpeakerManager starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── SpeakerManager (GameWorld @ +0x200) ──────────────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: SpeakerManager");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        const uint OffSpeakerManager = 0x200;
                        if (Memory.TryReadPtr(game.Base + OffSpeakerManager, out var speakerMgr)
                            && speakerMgr.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(speakerMgr, sw,
                                $"SpeakerManager @ 0x{speakerMgr:X}");
                        }
                        else
                        {
                            sw.WriteLine("// SpeakerManager: pointer null");
                        }
                    }
                    catch (Exception ex) { sw.WriteLine($"// SpeakerManager dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section NetworkWorld starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── NetworkWorld (GameWorld @ +0x2D0) ────────────────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: NetworkWorld");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        const uint OffNetworkWorld = 0x2D0;
                        if (Memory.TryReadPtr(game.Base + OffNetworkWorld, out var netWorld)
                            && netWorld.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(netWorld, sw,
                                $"NetworkWorld @ 0x{netWorld:X}");
                        }
                        else
                        {
                            sw.WriteLine("// NetworkWorld: pointer null");
                        }
                    }
                    catch (Exception ex) { sw.WriteLine($"// NetworkWorld dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section ObjectsFactory starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── ObjectsFactory (GameWorld @ +0x220) ──────────────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: ObjectsFactory");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        const uint OffObjectsFactory = 0x220;
                        if (Memory.TryReadPtr(game.Base + OffObjectsFactory, out var objFactory)
                            && objFactory.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(objFactory, sw,
                                $"ObjectsFactory @ 0x{objFactory:X}");
                        }
                        else
                        {
                            sw.WriteLine("// ObjectsFactory: pointer null");
                        }
                    }
                    catch (Exception ex) { sw.WriteLine($"// ObjectsFactory dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section SynchronizableObjectLogicProcessor starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── SynchronizableObjectLogicProcessor (GameWorld @ +0x248) ──────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: SynchronizableObjectLogicProcessor");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        const uint OffSyncObjLogicProc = 0x248;
                        if (Memory.TryReadPtr(game.Base + OffSyncObjLogicProc, out var sync)
                            && sync.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(sync, sw,
                                $"SynchronizableObjectLogicProcessor @ 0x{sync:X}");
                        }
                        else
                        {
                            sw.WriteLine("// SynchronizableObjectLogicProcessor: pointer null");
                        }
                    }
                    catch (Exception ex) { sw.WriteLine($"// SynchronizableObjectLogicProcessor dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section GamePlayerOwner starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── GamePlayerOwner (singleton via TypeIndex) ────────────────────
                // NOTE: ResolveKlassByTypeIndex returns the *Il2CppClass* pointer, NOT an
                // instance. Passing a klass pointer to DumpClassFieldsToWriter (which expects
                // an object) caused it to dereference the klass header as if it were an
                // instance and walk a 32-deep "parent" chain on garbage memory — burning many
                // seconds of DMA per dump. Skip the klass dump; dumping the live instance
                // would require resolving the singleton via static fields, which is not
                // implemented for this type.
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: GamePlayerOwner");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        var klassPtr = Il2CppDumper.ResolveKlassByTypeIndex(Offsets.Special.GamePlayerOwner_TypeIndex);
                        if (klassPtr.IsValidVirtualAddress())
                            sw.WriteLine($"// GamePlayerOwner klass @ 0x{klassPtr:X} (instance dump skipped — singleton resolver not implemented)");
                        else
                            sw.WriteLine("// GamePlayerOwner: TypeIndex not resolved");
                    }
                    catch (Exception ex) { sw.WriteLine($"// GamePlayerOwner dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section TarkovApplication starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── TarkovApplication (singleton via TypeIndex) ──────────────────
                // See GamePlayerOwner note above — same klass-vs-instance bug.
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: TarkovApplication");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        var klassPtr = Il2CppDumper.ResolveKlassByTypeIndex(Offsets.Special.TarkovApplication_TypeIndex);
                        if (klassPtr.IsValidVirtualAddress())
                            sw.WriteLine($"// TarkovApplication klass @ 0x{klassPtr:X} (instance dump skipped — singleton resolver not implemented)");
                        else
                            sw.WriteLine("// TarkovApplication: TypeIndex not resolved");
                    }
                    catch (Exception ex) { sw.WriteLine($"// TarkovApplication dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section MainPlayerControllers starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── MainPlayer controllers (Quest / Achievements / Prestige / Dialog / VOIP) ─
                // These pointers hang off the local player object. Confirms whether
                // quest objectives, achievements, prestige and VOIP state are client-readable.
                if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.MainPlayer, out var mainPlayerObj)
                    && mainPlayerObj.IsValidVirtualAddress())
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: MainPlayerControllers");
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine($"// MainPlayer @ 0x{mainPlayerObj:X}");
                    var ctrlMap = new (string Name, uint Off)[]
                    {
                        ("_questController",                 0x990),
                        ("_questLocationObjectsController",  0x998),
                        ("_achievementsController",          0x9A0),
                        ("_prestigeController",              0x9A8),
                        ("_dialogController",                0x9B0),
                        ("VoipController",                   0xAE0),
                        ("DissonanceComms",                  0xAE8),
                        ("BotsGroup",                        0xAA8),
                        ("Tracking",                         0x9C8),
                    };
                    foreach (var (name, off) in ctrlMap)
                    {
                        try
                        {
                            if (!Memory.TryReadPtr(mainPlayerObj + off, out var ptr) || !ptr.IsValidVirtualAddress())
                            {
                                sw.WriteLine($"// {name} @ +0x{off:X}: null");
                                continue;
                            }
                            sw.WriteLine($"// {name} @ +0x{off:X} -> 0x{ptr:X}");
                            Il2CppDumper.DumpClassFieldsToWriter(ptr, sw, $"{name} @ 0x{ptr:X}");
                        }
                        catch (Exception ex) { sw.WriteLine($"// {name} dump failed: {ex.Message}"); }
                    }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section Lamps starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Lamp controllers (HashSet<LampController>) ───────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Lamps");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.LampControllers, out var lampsObj)
                            && lampsObj.IsValidVirtualAddress())
                        {
                            sw.WriteLine($"// _lampControllers @ 0x{lampsObj:X}");
                            Il2CppDumper.DumpClassFieldsToWriter(lampsObj, sw, $"_lampControllers @ 0x{lampsObj:X}");
                            try
                            {
                                using var lampDict = MemDictionary<int, ulong>.Get(lampsObj, false);
                                sw.WriteLine($"//   _lampControllers entry count={lampDict.Count}");
                                int shown = 0;
                                foreach (var entry in lampDict)
                                {
                                    if (!entry.Value.IsValidVirtualAddress()) continue;
                                    if (shown >= 5) { sw.WriteLine("//   ... (capped at 5)"); break; }
                                    Il2CppDumper.DumpClassFieldsToWriter(entry.Value, sw,
                                        $"  LampController[key={entry.Key}] @ 0x{entry.Value:X}");
                                    shown++;
                                }
                            }
                            catch { sw.WriteLine("//   _lampControllers entry walk failed"); }
                        }
                        else sw.WriteLine("// _lampControllers: pointer null");
                    }
                    catch (Exception ex) { sw.WriteLine($"// Lamps dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section Platforms starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Platforms (trains / boats / Streets train) ───────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Platforms");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.Platforms, out var platforms)
                            && platforms.IsValidVirtualAddress())
                            Il2CppDumper.DumpClassFieldsToWriter(platforms, sw, $"_platforms @ 0x{platforms:X}");
                        else sw.WriteLine("// _platforms: null");

                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.PlatformAdapters, out var adapters)
                            && adapters.IsValidVirtualAddress())
                            Il2CppDumper.DumpClassFieldsToWriter(adapters, sw, $"PlatformAdapters @ 0x{adapters:X}");
                        else sw.WriteLine("// PlatformAdapters: null");
                    }
                    catch (Exception ex) { sw.WriteLine($"// Platforms dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section Zones starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── RestrictableZones / BorderZones ──────────────────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: Zones");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.RestrictableZones, out var rz)
                            && rz.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(rz, sw, $"_restrictableZones @ 0x{rz:X}");
                            try
                            {
                                using var rzArr = MemArray<ulong>.Get(rz, false);
                                sw.WriteLine($"//   _restrictableZones count={rzArr.Count}");
                                int max = Math.Min(rzArr.Count, 5);
                                for (int i = 0; i < max; i++)
                                {
                                    var entry = rzArr[i];
                                    if (!entry.IsValidVirtualAddress()) continue;
                                    Il2CppDumper.DumpClassFieldsToWriter(entry, sw, $"  RestrictableZone[{i}] @ 0x{entry:X}");
                                }
                                if (rzArr.Count > max)
                                    sw.WriteLine($"//   ... (capped at {max}, total count above)");
                            }
                            catch (Exception ex2) { sw.WriteLine($"//   _restrictableZones walk failed: {ex2.Message}"); }
                        }
                        else sw.WriteLine("// _restrictableZones: null");

                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.BorderZones, out var bz)
                            && bz.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(bz, sw, $"BorderZones @ 0x{bz:X}");
                            try
                            {
                                using var bzArr = MemArray<ulong>.Get(bz, false);
                                    sw.WriteLine($"//   BorderZones count={bzArr.Count}");
                                    // Collect one example of each class name (up to 3 per class)
                                    var seenClasses = new Dictionary<string, int>(StringComparer.Ordinal);
                                    // Also list ALL class names with indices for diagnosis
                                    sw.WriteLine("//   BorderZones class index map:");
                                    for (int i = 0; i < bzArr.Count; i++)
                                    {
                                        var entry = bzArr[i];
                                        if (!entry.IsValidVirtualAddress()) continue;
                                        var cn = Unity.Il2CppClass.ReadName(entry) ?? "unknown";
                                        sw.WriteLine($"//     [{i}] {cn} @ 0x{entry:X}");
                                    }
                                    // Resolve world positions for every Minefield entry
                                    sw.WriteLine("//   Minefield world positions:");
                                    for (int i = 0; i < bzArr.Count; i++)
                                    {
                                        var entry = bzArr[i];
                                        if (!entry.IsValidVirtualAddress()) continue;
                                        var cn = Unity.Il2CppClass.ReadName(entry) ?? "unknown";
                                        if (!cn.Equals("Minefield", StringComparison.Ordinal)) continue;
                                        try
                                        {
                                            // Read extents (BorderZone._extents = 0x28, two floats)
                                            Memory.TryReadValue<float>(entry + Offsets.BorderZone._extents,     out float ex);
                                            Memory.TryReadValue<float>(entry + Offsets.BorderZone._extents + 4, out float ez);
                                            // World position resolution removed (zone manager deleted)
                                            System.Numerics.Vector3 pos = default;
                                            float yaw = 0f;
                                            // Read Collider IL2CPP wrapper (BorderZone.Collider = 0x20)
                                            // then follow m_CachedPtr (+0x10) to the native C++ BoxCollider object
                                            string colliderInfo = "n/a";
                                            if (Memory.TryReadPtr(entry + 0x20, out ulong collWrapperPtr)
                                                && collWrapperPtr.IsValidVirtualAddress()
                                                && Memory.TryReadPtr(collWrapperPtr + 0x10, out ulong nativeCollPtr)
                                                && nativeCollPtr.IsValidVirtualAddress())
                                            {
                                                // Probe native C++ BoxCollider for center (Vector3) at various offsets.
                                                // Dump 96 bytes as groups of 3 floats (Vector3 candidates).
                                                var sb = new System.Text.StringBuilder();
                                                sb.Append($"native=0x{nativeCollPtr:X} ");
                                                for (uint off = 0x18; off <= 0x60; off += 4)
                                                {
                                                    Memory.TryReadValue<float>(nativeCollPtr + off, out float f);
                                                    sb.Append($"+0x{off:X}={f:F2} ");
                                                }
                                                colliderInfo = sb.ToString();
                                            }
                                            sw.WriteLine($"//     [{i}] pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) extents=({ex:F1},{ez:F1}) yaw={yaw:F1}");
                                            sw.WriteLine($"//          collider: {colliderInfo}");
                                        }
                                        catch (Exception ex2) { sw.WriteLine($"//     [{i}] pos read failed: {ex2.Message}"); }
                                    }
                                    for (int i = 0; i < bzArr.Count; i++)
                                    {
                                        var entry = bzArr[i];
                                        if (!entry.IsValidVirtualAddress()) continue;
                                        var cn = Unity.Il2CppClass.ReadName(entry) ?? "unknown";
                                        seenClasses.TryGetValue(cn, out int seen);
                                        if (seen >= 2) continue;
                                        seenClasses[cn] = seen + 1;
                                        Il2CppDumper.DumpClassFieldsToWriter(entry, sw, $"  BorderZone[{i}:{cn}] @ 0x{entry:X}");
                                        // For Minefield samples, also dump the Collider object
                                        if (cn.Equals("Minefield", StringComparison.Ordinal)
                                            && Memory.TryReadPtr(entry + 0x20, out ulong collPtr)
                                            && collPtr.IsValidVirtualAddress())
                                        {
                                            var collName = Unity.Il2CppClass.ReadName(collPtr) ?? "Collider";
                                            Il2CppDumper.DumpClassFieldsToWriter(collPtr, sw, $"    Collider[{collName}] @ 0x{collPtr:X}");
                                        }
                                    }
                                    sw.WriteLine($"//   Dumped {seenClasses.Sum(kv => kv.Value)} representative entries (up to 2 per class)");
                            }
                            catch (Exception ex2) { sw.WriteLine($"//   BorderZones walk failed: {ex2.Message}"); }
                        }
                        else sw.WriteLine("// BorderZones: null");
                    }
                    catch (Exception ex) { sw.WriteLine($"// Zones dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section TurnablesAndWindows starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Turnables (valves) and Windows ───────────────────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: TurnablesAndWindows");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.Turnables, out var turn)
                            && turn.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(turn, sw, $"Turnables @ 0x{turn:X}");
                            try
                            {
                                using var turnDict = MemDictionary<int, ulong>.Get(turn, false);
                                sw.WriteLine($"//   Turnables entry count={turnDict.Count}");
                                int shown = 0;
                                foreach (var entry in turnDict)
                                {
                                    if (!entry.Value.IsValidVirtualAddress()) continue;
                                    if (shown >= 5) { sw.WriteLine("//   ... (capped at 5)"); break; }
                                    Il2CppDumper.DumpClassFieldsToWriter(entry.Value, sw,
                                        $"  Turnable[key={entry.Key}] @ 0x{entry.Value:X}");
                                    shown++;
                                }
                            }
                            catch { sw.WriteLine("//   Turnables entry walk failed"); }
                        }
                        else sw.WriteLine("// Turnables: null");

                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.Windows, out var wnd)
                            && wnd.IsValidVirtualAddress())
                            Il2CppDumper.DumpClassFieldsToWriter(wnd, sw, $"Windows @ 0x{wnd:X}");
                        else sw.WriteLine("// Windows: null");
                    }
                    catch (Exception ex) { sw.WriteLine($"// Turnables/Windows dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section QuestLootAndItemOwners starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── Quest loot items / item owners (quest pickup tracking) ───────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: QuestLootAndItemOwners");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.QuestLootItems, out var ql)
                            && ql.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(ql, sw, $"_questLootItems @ 0x{ql:X}");
                            try
                            {
                                using var qlList = MemList<ulong>.Get(ql, false);
                                sw.WriteLine($"//   _questLootItems count={qlList.Count}");
                                for (int qi = 0; qi < qlList.Count; qi++)
                                {
                                    var item = qlList[qi];
                                    if (!item.IsValidVirtualAddress()) continue;
                                    Il2CppDumper.DumpClassFieldsToWriter(item, sw,
                                        $"  QuestLootItem[{qi}] @ 0x{item:X}");
                                }
                            }
                            catch { sw.WriteLine("//   _questLootItems entry walk failed"); }
                        }
                        else sw.WriteLine("// _questLootItems: null");

                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.ItemOwners, out var io)
                            && io.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(io, sw, $"ItemOwners @ 0x{io:X}");
                            try
                            {
                                using var ioDict = MemDictionary<int, ulong>.Get(io, false);
                                sw.WriteLine($"//   ItemOwners entry count={ioDict.Count}");
                                int shown = 0;
                                foreach (var entry in ioDict)
                                {
                                    if (!entry.Value.IsValidVirtualAddress()) continue;
                                    if (shown >= 5) { sw.WriteLine("//   ... (capped at 5, total count above)"); break; }
                                    Il2CppDumper.DumpClassFieldsToWriter(entry.Value, sw,
                                        $"  ItemOwner[key={entry.Key}] @ 0x{entry.Value:X}");
                                    shown++;
                                }
                            }
                            catch { sw.WriteLine("//   ItemOwners entry walk failed"); }
                        }
                        else sw.WriteLine("// ItemOwners: null");
                    }
                    catch (Exception ex) { sw.WriteLine($"// QuestLoot/ItemOwners dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                Log.WriteLine($"[MatchDumper] Section AlivePlayerBridges starting ({(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s)");
                // ── AllAlivePlayerBridges ────────────────────────────────────────
                {
                    sw.WriteLine("═══════════════════════════════════════");
                    sw.WriteLine("SECTION: AlivePlayerBridges");
                    sw.WriteLine("═══════════════════════════════════════");
                    try
                    {
                        if (Memory.TryReadPtr(game.Base + Offsets.ClientLocalGameWorld.AllAlivePlayerBridges, out var bridges)
                            && bridges.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(bridges, sw, $"AllAlivePlayerBridges @ 0x{bridges:X}");
                            try
                            {
                                using var bridgeDict = MemDictionary<int, ulong>.Get(bridges, false);
                                sw.WriteLine($"//   AllAlivePlayerBridges entry count={bridgeDict.Count}");
                                foreach (var entry in bridgeDict)
                                {
                                    if (!entry.Value.IsValidVirtualAddress()) continue;
                                    Il2CppDumper.DumpClassFieldsToWriter(entry.Value, sw,
                                        $"  Bridge[raidId={entry.Key}] @ 0x{entry.Value:X}");
                                }
                            }
                            catch { sw.WriteLine("//   AllAlivePlayerBridges entry walk failed"); }
                        }
                        else sw.WriteLine("// AllAlivePlayerBridges: null");
                    }
                    catch (Exception ex) { sw.WriteLine($"// AlivePlayerBridges dump failed: {ex.Message}"); }
                    sw.WriteLine();
                    sw.Flush();
                }

                sw.Flush();
                Log.WriteLine($"[MatchDumper] IL2CPP dump complete in {(DateTime.UtcNow - dumpStart).TotalSeconds:F1}s: {path}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[MatchDumper] IL2CPP dump write failed: {ex.Message}");
            }
        }

        // ── Player sub-object IL2CPP dump helper ─────────────────────────────

        /// <summary>
        /// Dumps gear/hands/health sub-object class hierarchies for a single player.
        /// All pointer walks mirror the chains used by <see cref="GearManager"/> and
        /// <see cref="HandsManager"/> so the offsets are identical.
        /// </summary>
        private static void DumpPlayerSubObjects(
            RegisteredPlayers.PlayerDumpEntry de,
            StreamWriter sw)
        {
            ulong playerBase = de.PlayerBase;
            bool isObserved = de.IsObserved;
            var name = de.Player.Name;

            try
            {
                // ── Top-level player base (ObservedPlayerView for observed, Player for client) ──
                // Dumping the root container makes it easy to spot any new fields BSG adds
                // (rotation/look transforms, body refs, side, etc.) without walking sub-chains.
                Il2CppDumper.DumpClassFieldsToWriter(playerBase, sw,
                    $"  {(isObserved ? "ObservedPlayerView" : "Player")} [{name}] @ 0x{playerBase:X}");
            }
            catch { /* root dump failure — skip */ }

            try
            {
                // ── PlayerBody (skeleton + skins + slot views) ───────────────────
                // Observed:  ObservedPlayerView + 0xD8 (Offsets.ObservedPlayerView.PlayerBody)
                // Client:    Player + 0x190        (Offsets.Player._playerBody)
                // The PlayerBody chain feeds the bone/skeleton path used by the local
                // AimviewWidget; dumping it lets us mirror that fidelity for remote players.
                uint bodyOff = isObserved
                    ? Offsets.ObservedPlayerView.PlayerBody
                    : Offsets.Player._playerBody;

                if (Memory.TryReadPtr(playerBase + bodyOff, out var playerBody)
                    && playerBody.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(playerBody, sw,
                        $"  PlayerBody [{name}] @ 0x{playerBody:X}");

                    // SkeletonRootJoint — root of the bone hierarchy used for projection.
                    if (Memory.TryReadPtr(playerBody + Offsets.PlayerBody.SkeletonRootJoint, out var skel)
                        && skel.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(skel, sw,
                            $"    SkeletonRootJoint [{name}] @ 0x{skel:X}");
                    }

                    // BodySkins — PlayerBodySubclass dictionary; gear-visual references.
                    if (Memory.TryReadPtr(playerBody + Offsets.PlayerBody.BodySkins, out var skins)
                        && skins.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(skins, sw,
                            $"    BodySkins [{name}] @ 0x{skins:X}");
                    }

                    // SlotViews — per-equipment-slot visual nodes (helmet, rig, weapon mounts…).
                    if (Memory.TryReadPtr(playerBody + Offsets.PlayerBody.SlotViews, out var slotViews)
                        && slotViews.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(slotViews, sw,
                            $"    SlotViews [{name}] @ 0x{slotViews:X}");
                    }
                }
            }
            catch { /* PlayerBody dump failure — skip */ }

            try
            {
                // ── InventoryController ──────────────────────────────────────────
                ulong invController = 0;
                if (isObserved)
                {
                    if (Memory.TryReadPtr(playerBase + Offsets.ObservedPlayerView.ObservedPlayerController, out var opc)
                        && opc.IsValidVirtualAddress())
                    {
                        Memory.TryReadPtr(opc + Offsets.ObservedPlayerController.InventoryController, out invController);
                    }
                }
                else
                {
                    Memory.TryReadPtr(playerBase + Offsets.Player._inventoryController, out invController);
                }

                if (invController.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(invController, sw,
                        $"  InventoryController [{name}] @ 0x{invController:X}");

                    // Inventory
                    if (Memory.TryReadPtr(invController + Offsets.InventoryController.Inventory, out var inventory)
                        && inventory.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(inventory, sw,
                            $"    Inventory [{name}] @ 0x{inventory:X}");

                        // Equipment
                        if (Memory.TryReadPtr(inventory + Offsets.Inventory.Equipment, out var equipment)
                            && equipment.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(equipment, sw,
                                $"      Equipment [{name}] @ 0x{equipment:X}");

                            // Individual slot items
                            if (Memory.TryReadPtr(equipment + Offsets.Equipment.Slots, out var slotsPtr)
                                && slotsPtr.IsValidVirtualAddress())
                            {
                                try
                                {
                                    using var slotsArr = MemArray<ulong>.Get(slotsPtr, false);
                                    for (int i = 0; i < slotsArr.Count; i++)
                                    {
                                        var slotPtr = slotsArr[i];
                                        if (!slotPtr.IsValidVirtualAddress()) continue;

                                        // Slot name
                                        string slotName = $"Slot[{i}]";
                                        if (Memory.TryReadPtr(slotPtr + Offsets.Slot.ID, out var namePtr)
                                            && namePtr.IsValidVirtualAddress()
                                            && Memory.TryReadUnityString(namePtr, out var sn)
                                            && sn is not null)
                                            slotName = sn;

                                        Il2CppDumper.DumpClassFieldsToWriter(slotPtr, sw,
                                            $"        Slot '{slotName}' [{name}] @ 0x{slotPtr:X}");

                                        // Contained item
                                        if (Memory.TryReadPtr(slotPtr + Offsets.Slot.ContainedItem, out var item)
                                            && item.IsValidVirtualAddress())
                                        {
                                            Il2CppDumper.DumpClassFieldsToWriter(item, sw,
                                                $"          Item in '{slotName}' [{name}] @ 0x{item:X}");
                                        }
                                    }
                                }
                                catch { /* slot read failure — skip */ }
                            }
                        }
                    }
                }
            }
            catch { /* inventory chain failure — skip */ }

            try
            {
                // ── HandsController ──────────────────────────────────────────────
                bool handsResolved = false;
                ulong handsControllerAddr = 0;
                uint itemOffset = 0;
                if (isObserved)
                {
                    if (Memory.TryReadPtr(playerBase + Offsets.ObservedPlayerView.ObservedPlayerController, out var opc)
                        && opc.IsValidVirtualAddress())
                    {
                        handsControllerAddr = opc + Offsets.ObservedPlayerController.HandsController;
                        itemOffset = Offsets.ObservedHandsController.ItemInHands;
                        handsResolved = true;
                    }
                }
                else
                {
                    handsControllerAddr = playerBase + Offsets.Player._handsController;
                    itemOffset = Offsets.ItemHandsController.Item;
                    handsResolved = true;
                }

                if (handsResolved
                    && Memory.TryReadPtr(handsControllerAddr, out var handsController)
                    && handsController.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(handsController, sw,
                        $"  HandsController [{name}] @ 0x{handsController:X}");

                    // Item in hands
                    if (Memory.TryReadPtr(handsController + itemOffset, out var heldItem)
                        && heldItem.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(heldItem, sw,
                            $"    ItemInHands [{name}] @ 0x{heldItem:X}");

                        // Chambered ammo (weapon chambers)
                        if (Memory.TryReadPtr(heldItem + Offsets.LootItemWeapon.Chambers, out var chambers)
                            && chambers.IsValidVirtualAddress())
                        {
                            Il2CppDumper.DumpClassFieldsToWriter(chambers, sw,
                                $"      Chambers [{name}] @ 0x{chambers:X}");

                            if (Memory.TryReadPtr(chambers + 0x20, out var chamberSlot)
                                && chamberSlot.IsValidVirtualAddress())
                            {
                                Il2CppDumper.DumpClassFieldsToWriter(chamberSlot, sw,
                                    $"        ChamberSlot[0] [{name}] @ 0x{chamberSlot:X}");

                                if (Memory.TryReadPtr(chamberSlot + Offsets.Slot.ContainedItem, out var ammo)
                                    && ammo.IsValidVirtualAddress())
                                {
                                    Il2CppDumper.DumpClassFieldsToWriter(ammo, sw,
                                        $"          ChamberedAmmo [{name}] @ 0x{ammo:X}");
                                }
                            }
                        }
                    }
                }
            }
            catch { /* hands chain failure — skip */ }

            try
            {
                // ── HealthController ─────────────────────────────────────────────
                ulong healthController = de.ObservedHealthControllerAddr;

                // For local/client players the health controller is at a different offset.
                if (healthController == 0 && !isObserved)
                {
                    Memory.TryReadPtr(playerBase + Offsets.Player._healthController, out healthController);
                }

                if (healthController.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(healthController, sw,
                        $"  HealthController [{name}] @ 0x{healthController:X}");
                }
            }
            catch { /* health chain failure — skip */ }

            // ── Observed-only sub-objects ────────────────────────────────────────
            if (!isObserved) return;

            if (!Memory.TryReadPtr(playerBase + Offsets.ObservedPlayerView.ObservedPlayerController, out var opcBase)
                || !opcBase.IsValidVirtualAddress())
                return;

            try
            {
                // ── ObservedPlayerController ─────────────────────────────────────
                Il2CppDumper.DumpClassFieldsToWriter(opcBase, sw,
                    $"  ObservedPlayerController [{name}] @ 0x{opcBase:X}");
            }
            catch { /* OPC dump failure — skip */ }

            try
            {
                // ── InfoContainer (ObservedPlayerInfoContainer) ──────────────────
                if (Memory.TryReadPtr(opcBase + Offsets.ObservedPlayerController.InfoContainer, out var infoContainer)
                    && infoContainer.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(infoContainer, sw,
                        $"  InfoContainer [{name}] @ 0x{infoContainer:X}");
                }
            }
            catch { /* InfoContainer dump failure — skip */ }

            try
            {
                // ── MovementController → ObservedPlayerMovementModel ─────────────
                // OPC+0xD8 → pointer to ObservedPlayerMovementController (step1)
                // step1+0x98 → ObservedMovementController (step2) holding Rotation/Velocity/Pose
                if (Memory.TryReadPtr(opcBase + Offsets.ObservedPlayerController.MovementController[0], out var movCtrl)
                    && movCtrl.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(movCtrl, sw,
                        $"  MovementController step1 [{name}] @ 0x{movCtrl:X}");

                    if (Memory.TryReadPtr(movCtrl + Offsets.ObservedPlayerController.MovementController[1], out var movCtrl2)
                        && movCtrl2.IsValidVirtualAddress())
                    {
                        Il2CppDumper.DumpClassFieldsToWriter(movCtrl2, sw,
                            $"    ObservedMovementController step2 [{name}] @ 0x{movCtrl2:X}");
                    }
                }
            }
            catch { /* MovementController dump failure — skip */ }

            try
            {
                // ── ArmorInfoController ──────────────────────────────────────────
                if (Memory.TryReadPtr(opcBase + Offsets.ObservedPlayerController.ArmorInfoController, out var armorInfo)
                    && armorInfo.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(armorInfo, sw,
                        $"  ArmorInfoController [{name}] @ 0x{armorInfo:X}");
                }
            }
            catch { /* ArmorInfoController dump failure — skip */ }

            try
            {
                // ── AIData (OPV+0x0070) ───────────────────────────────────────────
                // Present on all ObservedPlayerView objects; holds AI-specific state
                // (kills, flare, stationary weapon, power-of-equipment, environment ID, etc.)
                if (Memory.TryReadPtr(playerBase + 0x0070, out var aiData)
                    && aiData.IsValidVirtualAddress())
                {
                    Il2CppDumper.DumpClassFieldsToWriter(aiData, sw,
                        $"  AIData [{name}] @ 0x{aiData:X}");
                }
            }
            catch { /* AIData dump failure — skip */ }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static DumpVec3 ToDumpVec(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
        private static DumpVec3 ToDumpVec(ref Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

        // ── DTOs ─────────────────────────────────────────────────────────────────

        private sealed class MatchSnapshot
        {
            [JsonPropertyName("dumpedAt")]     public DateTime DumpedAt { get; init; }
            [JsonPropertyName("mapId")]        public string MapId { get; init; } = "";
            [JsonPropertyName("gameWorldBase")] public string GameWorldBase { get; init; } = "";
            [JsonPropertyName("localPlayer")]  public DumpLocalPlayer? LocalPlayer { get; init; }
            [JsonPropertyName("players")]      public List<DumpPlayer> Players { get; init; } = [];
            [JsonPropertyName("lootItems")]    public List<DumpLootItem> LootItems { get; init; } = [];
            [JsonPropertyName("lootCorpses")]  public List<DumpLootCorpse> LootCorpses { get; init; } = [];
            [JsonPropertyName("lootContainers")] public List<DumpLootContainer> LootContainers { get; init; } = [];
            [JsonPropertyName("lootAirdrops")] public List<DumpLootAirdrop> LootAirdrops { get; init; } = [];
            [JsonPropertyName("exfils")]       public List<DumpExfil> Exfils { get; init; } = [];
            [JsonPropertyName("transits")]     public List<DumpTransit> Transits { get; init; } = [];
            [JsonPropertyName("doors")]        public List<DumpDoor> Doors { get; init; } = [];
            [JsonPropertyName("switches")]     public List<DumpSwitch> Switches { get; init; } = [];
            [JsonPropertyName("questLocations")] public List<DumpQuestLocation> QuestLocations { get; init; } = [];
            [JsonPropertyName("btr")]          public DumpBtr? Btr { get; init; }
            [JsonPropertyName("explosives")]   public List<DumpExplosive> Explosives { get; init; } = [];
            [JsonPropertyName("killfeed")]     public List<DumpKillfeedEntry> Killfeed { get; init; } = [];
        }

        private sealed class DumpVec3
        {
            [JsonPropertyName("x")] public float X { get; init; }
            [JsonPropertyName("y")] public float Y { get; init; }
            [JsonPropertyName("z")] public float Z { get; init; }
        }

        private sealed class DumpLocalPlayer
        {
            [JsonPropertyName("name")]           public string Name { get; init; } = "";
            [JsonPropertyName("base")]           public string Base { get; init; } = "";
            [JsonPropertyName("moveBase")]       public string? MoveBase { get; init; }
            [JsonPropertyName("healthBase")]     public string? HealthBase { get; init; }
            [JsonPropertyName("inventoryBase")]  public string? InventoryBase { get; init; }
            [JsonPropertyName("playerBodyBase")] public string? PlayerBodyBase { get; init; }
            [JsonPropertyName("profilePtr")]     public string? ProfilePtr { get; init; }
            [JsonPropertyName("profileId")]      public string? ProfileId { get; init; }
            [JsonPropertyName("accountId")]      public string? AccountId { get; init; }
            [JsonPropertyName("isPmc")]          public bool IsPmc { get; init; }
            [JsonPropertyName("isScav")]         public bool IsScav { get; init; }
            [JsonPropertyName("entryPoint")]     public string? EntryPoint { get; init; }
            [JsonPropertyName("position")]       public DumpVec3? Position { get; init; }
            [JsonPropertyName("lookPosition")]   public DumpVec3? LookPosition { get; init; }
            [JsonPropertyName("rotationYaw")]    public float RotationYaw { get; init; }
            [JsonPropertyName("rotationPitch")]  public float RotationPitch { get; init; }
            [JsonPropertyName("isAlive")]        public bool IsAlive { get; init; }
            [JsonPropertyName("isADS")]          public bool IsADS { get; init; }
            [JsonPropertyName("energy")]         public float? Energy { get; init; }
            [JsonPropertyName("hydration")]      public float? Hydration { get; init; }
            [JsonPropertyName("healthStatus")]   public string HealthStatus { get; init; } = "";
            [JsonPropertyName("level")]          public int Level { get; init; }
            [JsonPropertyName("gearValue")]      public int GearValue { get; init; }
            [JsonPropertyName("hasNVG")]         public bool HasNVG { get; init; }
            [JsonPropertyName("hasThermal")]     public bool HasThermal { get; init; }
            [JsonPropertyName("inHandsItem")]    public string? InHandsItem { get; init; }
            [JsonPropertyName("inHandsAmmo")]    public string? InHandsAmmo { get; init; }
            [JsonPropertyName("pwa")]            public string? PWA { get; init; }
            [JsonPropertyName("equipment")]      public Dictionary<string, DumpGearItem>? Equipment { get; init; }
        }

        private sealed class DumpPlayer
        {
            [JsonPropertyName("name")]           public string Name { get; init; } = "";
            [JsonPropertyName("type")]           public string Type { get; init; } = "";
            [JsonPropertyName("side")]           public string Side { get; init; } = "";
            [JsonPropertyName("isLocalPlayer")]  public bool IsLocalPlayer { get; init; }
            [JsonPropertyName("isAlive")]        public bool IsAlive { get; init; }
            [JsonPropertyName("isActive")]       public bool IsActive { get; init; }
            [JsonPropertyName("isHuman")]        public bool IsHuman { get; init; }
            [JsonPropertyName("position")]       public DumpVec3? Position { get; init; }
            [JsonPropertyName("rotationYaw")]    public float RotationYaw { get; init; }
            [JsonPropertyName("rotationPitch")]  public float RotationPitch { get; init; }
            [JsonPropertyName("groupId")]        public int GroupId { get; init; }
            [JsonPropertyName("spawnGroupId")]   public int SpawnGroupId { get; init; }
            [JsonPropertyName("level")]          public int Level { get; init; }
            [JsonPropertyName("profileId")]      public string? ProfileId { get; init; }
            [JsonPropertyName("accountId")]      public string? AccountId { get; init; }
            [JsonPropertyName("healthStatus")]   public string HealthStatus { get; init; } = "";
            [JsonPropertyName("gearValue")]      public int GearValue { get; init; }
            [JsonPropertyName("hasNVG")]         public bool HasNVG { get; init; }
            [JsonPropertyName("hasThermal")]     public bool HasThermal { get; init; }
            [JsonPropertyName("inHandsItem")]    public string? InHandsItem { get; init; }
            [JsonPropertyName("inHandsAmmo")]    public string? InHandsAmmo { get; init; }
            [JsonPropertyName("isWeaponInHands")] public bool IsWeaponInHands { get; init; }
            [JsonPropertyName("voice")]          public string? Voice { get; init; }
            [JsonPropertyName("equipment")]      public Dictionary<string, DumpGearItem>? Equipment { get; init; }
            [JsonPropertyName("base")]           public string Base { get; init; } = "";
        }

        private sealed class DumpGearItem
        {
            [JsonPropertyName("long")]   public string Long { get; init; } = "";
            [JsonPropertyName("short")]  public string Short { get; init; } = "";
            [JsonPropertyName("price")]  public int Price { get; init; }
        }

        private sealed class DumpLootItem
        {
            [JsonPropertyName("id")]           public string Id { get; init; } = "";
            [JsonPropertyName("name")]         public string Name { get; init; } = "";
            [JsonPropertyName("shortName")]    public string ShortName { get; init; } = "";
            [JsonPropertyName("position")]     public DumpVec3? Position { get; init; }
            [JsonPropertyName("displayPrice")] public int DisplayPrice { get; init; }
            [JsonPropertyName("isQuestItem")]  public bool IsQuestItem { get; init; }
            [JsonPropertyName("isImportant")]  public bool IsImportant { get; init; }
        }

        private sealed class DumpLootCorpse
        {
            [JsonPropertyName("name")]             public string Name { get; init; } = "";
            [JsonPropertyName("side")]             public string Side { get; init; } = "";
            [JsonPropertyName("corpseProfileId")]  public string CorpseProfileId { get; init; } = "";
            [JsonPropertyName("position")]         public DumpVec3? Position { get; init; }
            [JsonPropertyName("totalValue")]       public int TotalValue { get; init; }
            [JsonPropertyName("gearReady")]        public bool GearReady { get; init; }
            [JsonPropertyName("equipment")]        public Dictionary<string, DumpCorpseGear>? Equipment { get; init; }
            [JsonPropertyName("interactiveClass")] public string InteractiveClass { get; init; } = "";
        }

        private sealed class DumpCorpseGear
        {
            [JsonPropertyName("shortName")] public string ShortName { get; init; } = "";
            [JsonPropertyName("name")]      public string Name { get; init; } = "";
            [JsonPropertyName("price")]     public int Price { get; init; }
        }

        private sealed class DumpLootContainer
        {
            [JsonPropertyName("id")]       public string Id { get; init; } = "";
            [JsonPropertyName("name")]     public string Name { get; init; } = "";
            [JsonPropertyName("position")] public DumpVec3? Position { get; init; }
            [JsonPropertyName("searched")] public bool Searched { get; init; }
        }

        private sealed class DumpLootAirdrop
        {
            [JsonPropertyName("position")] public DumpVec3? Position { get; init; }
        }

        private sealed class DumpExfil
        {
            [JsonPropertyName("name")]     public string Name { get; init; } = "";
            [JsonPropertyName("position")] public DumpVec3? Position { get; init; }
            [JsonPropertyName("status")]   public string Status { get; init; } = "";
            [JsonPropertyName("isSecret")] public bool IsSecret { get; init; }
        }

        private sealed class DumpTransit
        {
            [JsonPropertyName("name")]     public string Name { get; init; } = "";
            [JsonPropertyName("position")] public DumpVec3? Position { get; init; }
            [JsonPropertyName("isActive")] public bool IsActive { get; init; }
        }

        private sealed class DumpDoor
        {
            [JsonPropertyName("id")]        public string Id { get; init; } = "";
            [JsonPropertyName("keyId")]     public string? KeyId { get; init; }
            [JsonPropertyName("keyName")]   public string? KeyName { get; init; }
            [JsonPropertyName("doorState")] public string DoorState { get; init; } = "";
            [JsonPropertyName("position")]  public DumpVec3? Position { get; init; }
            [JsonPropertyName("base")]      public string Base { get; init; } = "";
        }

        private sealed class DumpSwitch
        {
            [JsonPropertyName("name")]     public string Name { get; init; } = "";
            [JsonPropertyName("type")]     public string Type { get; init; } = "";
            [JsonPropertyName("position")] public DumpVec3? Position { get; init; }
        }

        private sealed class DumpQuestLocation
        {
            [JsonPropertyName("questId")]       public string QuestId { get; init; } = "";
            [JsonPropertyName("questName")]     public string QuestName { get; init; } = "";
            [JsonPropertyName("zoneId")]        public string ZoneId { get; init; } = "";
            [JsonPropertyName("objectiveId")]   public string ObjectiveId { get; init; } = "";
            [JsonPropertyName("objectiveType")] public string ObjectiveType { get; init; } = "";
            [JsonPropertyName("optional")]      public bool Optional { get; init; }
            [JsonPropertyName("position")]      public DumpVec3? Position { get; init; }
        }

        private sealed class DumpBtr
        {
            [JsonPropertyName("position")]         public DumpVec3? Position { get; init; }
            [JsonPropertyName("currentSpeed")]     public float CurrentSpeed { get; init; }
            [JsonPropertyName("isMoving")]         public bool IsMoving { get; init; }
            [JsonPropertyName("state")]            public byte State { get; init; }
            [JsonPropertyName("routeState")]       public byte RouteState { get; init; }
            [JsonPropertyName("timeToEndPauseMs")] public int TimeToEndPauseMs { get; init; }
            [JsonPropertyName("isPaid")]           public bool IsPaid { get; init; }
            [JsonPropertyName("turretYawDeg")]     public float TurretYawDeg { get; init; }
            [JsonPropertyName("gunnerPtr")]        public string GunnerPtr { get; init; } = "";
        }

        private sealed class DumpExplosive
        {
            [JsonPropertyName("name")]     public string Name { get; init; } = "";
            [JsonPropertyName("position")] public DumpVec3? Position { get; init; }
            [JsonPropertyName("isActive")] public bool IsActive { get; init; }
        }

        private sealed class DumpKillfeedEntry
        {
            [JsonPropertyName("killer")]      public string Killer { get; init; } = "";
            [JsonPropertyName("victim")]      public string Victim { get; init; } = "";
            [JsonPropertyName("weapon")]      public string Weapon { get; init; } = "";
            [JsonPropertyName("victimLevel")] public int VictimLevel { get; init; }
            [JsonPropertyName("killerSide")]  public string KillerSide { get; init; } = "";
            [JsonPropertyName("timestamp")]   public DateTime Timestamp { get; init; }
        }
    }
}
