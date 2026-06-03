// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;
using EftPlayer = eft_dma_radar.Silk.Tarkov.GameWorld.Player.Player;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace eft_dma_radar.Silk.UI
{
    internal static partial class RadarWindow
    {
        private static void OnRender(double delta)
        {
            if (_grContext is null || _skSurface is null)
                return;

            try
            {
                // Frame setup
                Interlocked.Increment(ref _fpsCounter);

                // Only reset GL state that ImGui touched — much cheaper than a full reset
                _grContext.ResetContext(
                    GRGlBackendState.RenderTarget |
                    GRGlBackendState.TextureBinding |
                    GRGlBackendState.View |
                    GRGlBackendState.Blend |
                    GRGlBackendState.Vertex |
                    GRGlBackendState.Program |
                    GRGlBackendState.PixelStore);

                // Periodic resource purge — scratch-only so permanent assets (fonts, map
                // tiles, gradients) are not evicted and immediately re-created. Full purge
                // every 1 s caused a visible GPU stall spike; 5 s scratch-only is safe.
                long now = Environment.TickCount64;
                if (now - _lastPurgeTick >= PurgeIntervalMs)
                {
                    _lastPurgeTick = now;
                    _grContext.PurgeUnlockedResources(scratchResourcesOnly: true);
                }

                // Skia scene render
                var fbSize = _window.FramebufferSize;
                DrawSkiaScene(ref fbSize);

                // ImGui UI render
                DrawImGuiUI(ref fbSize, delta);

                // Debounced config auto-save — persists any MarkDirty() call after the debounce interval
                Config.FlushIfDirty();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"***** CRITICAL RENDER ERROR: {ex}");
            }
        }

        private static void DrawSkiaScene(ref Vector2D<int> fbSize)
        {
            _gl.Viewport(0, 0, (uint)fbSize.X, (uint)fbSize.Y);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.StencilBufferBit);

            var canvas = _skSurface.Canvas;
            canvas.Save();
            try
            {
                var scale = UIScale;
                canvas.Scale(scale, scale);

                if (InRaid && LocalPlayer is Player localPlayer)
                {
                    var mapID = MapID;
                    if (!mapID.Equals(MapManager.Map?.ID, StringComparison.OrdinalIgnoreCase))
                        MapManager.LoadMap(mapID);

                    var map = MapManager.Map;
                    if (map is not null && localPlayer.HasValidPosition)
                    {
                        DrawRadar(canvas, localPlayer, map, scale);
                    }
                    else if (MapManager.IsLoading)
                    {
                        DrawStatusMessage(canvas, "Loading Map", scale, animated: true);
                    }
                    else
                    {
                        DrawStatusMessage(canvas, "Waiting for Raid Start", scale);
                    }
                }
                else if (Memory.InHideout)
                {
                    DrawStatusMessage(canvas, "In Hideout", scale);
                }
                else if (!Ready)
                {
                    DrawStatusMessage(canvas, "Starting Up", scale, animated: true);
                }
                else if (!InRaid)
                {
                    var matchingStage = MatchingProgressResolver.GetCachedStage();
                    string statusMsg;
                    if (matchingStage != EMatchingStage.None)
                    {
                        statusMsg = matchingStage.ToDisplayString();
                    }
                    else
                    {
                        statusMsg = "Waiting for Raid Start";
                    }
                    DrawStatusMessage(canvas, statusMsg, scale, animated: true);
                }
            }
            finally
            {
                canvas.Restore();
                _grContext.Flush();
            }
        }

        private static void DrawRadar(SKCanvas canvas, Player localPlayer, IRadarMap map, float scale)
        {
            LootFilter.AdvanceFrame();
            var localPlayerPos    = localPlayer.Position;
            var localPlayerMapPos = MapParams.ToMapPos(localPlayerPos, map.Config);

            var canvasSize = new SKSize(_window.Size.X / scale, _window.Size.Y / scale);
            MapParams mapParams;

            if (_freeMode)
            {
                if (_mapPanPosition == default)
                    _mapPanPosition = localPlayerMapPos;
                mapParams = map.GetParameters(canvasSize, _zoom, ref _mapPanPosition);
            }
            else
            {
                _mapPanPosition = default;
                mapParams = map.GetParameters(canvasSize, _zoom, ref localPlayerMapPos);
            }

            var mapCanvasBounds = new SKRect(0, 0, canvasSize.Width, canvasSize.Height);

            map.Draw(canvas, localPlayerPos.Y, mapParams.Bounds, mapCanvasBounds);

            // Viewport culling — world-space pre-cull avoids coordinate transforms for off-screen entities
            const float CullMargin = 120f;
            var worldBounds = mapParams.GetWorldBounds(CullMargin);
            var mapCfg = map.Config;

            // Snapshot players
            var allPlayersSnapshot = AllPlayers;

            List<Player>? normalPlayers = null;
            if (allPlayersSnapshot is not null)
            {
                _renderPlayers.Clear();
                foreach (var p in allPlayersSnapshot)
                {
                    if (p.IsRadarVisible)
                        _renderPlayers.Add(p);
                }
                _renderPlayers.Sort(static (a, b) => a.DrawPriority.CompareTo(b.DrawPriority));
                normalPlayers = _renderPlayers;
            }

            // Loot (skip in battle mode or if loot is disabled)
            if (!Config.BattleMode && Config.ShowLoot)
            {
                var loot = Memory.Loot;

                if (loot is not null)
                {
                    float playerY = localPlayerPos.Y;

                    int visibleCount = 0;
                    foreach (var item in loot)
                    {
                        int price = item.DisplayPrice;
                        var result = item.Evaluate(price);
                        if (!result.Visible)
                            continue;
                        if (!worldBounds.Contains(item.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(item.Position, mapCfg));
                        float dy = item.Position.Y - playerY;
                        bool underMap = dy < -15f;
                        item.Draw(canvas, sp, price, result, underMap, dy);
                        visibleCount++;
                    }
                    LootFilter.SetCounts(visibleCount, loot.Count);
                }
                else
                {
                    LootFilter.SetCounts(0, 0);
                }
            }
            else
            {
                LootFilter.SetCounts(0, 0);
            }

            // Corpses
            if (!Config.BattleMode && Config.ShowLoot && Config.ShowCorpses)
            {
                var corpses = Memory.Corpses;
                if (corpses is not null)
                {
                    foreach (var corpse in corpses)
                    {
                        if (!worldBounds.Contains(corpse.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(corpse.Position, mapCfg));
                        corpse.Draw(canvas, sp);
                    }
                }
            }

            // Static containers
            if (!Config.BattleMode && Config.ShowLoot && Config.ShowContainers)
            {
                var containers = Memory.Containers;
                if (containers is not null)
                {
                    float playerY = localPlayerPos.Y;
                    bool showNames = Config.ShowContainerNames;
                    bool hideSearched = Config.HideSearchedContainers;
                    var selectedIds = Config.SelectedContainers;

                    foreach (var container in containers)
                    {
                        if (hideSearched && container.Searched)
                            continue;
                        if (!selectedIds.Contains(container.Id))
                            continue;
                        if (!worldBounds.Contains(container.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(container.Position, mapCfg));
                        container.Draw(canvas, sp, showNames);
                    }
                }
            }

            // Exfils (drawn before players so player dots render on top)
            if (Config.ShowExfils)
            {
                var exfils = Memory.Exfils;
                if (exfils is not null)
                {
                    var lp = localPlayer as Tarkov.GameWorld.Player.LocalPlayer;
                    foreach (var exfil in exfils)
                    {
                        if (Config.HideInactiveExfils && lp is not null && !exfil.IsAvailableFor(lp))
                            continue;
                        if (!worldBounds.Contains(exfil.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(exfil.Position, mapCfg));
                        exfil.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Transit points (drawn alongside exfils)
            if (Config.ShowTransits)
            {
                var transits = Memory.Transits;
                if (transits is not null)
                {
                    foreach (var transit in transits)
                    {
                        if (!worldBounds.Contains(transit.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(transit.Position, mapCfg));
                        transit.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Doors (keyed doors with state)
            if (!Config.BattleMode && Config.ShowDoors)
            {
                var doors = Memory.Doors;
                if (doors is not null)
                {
                    bool filterByLoot = Config.DoorsOnlyNearLoot;

                    foreach (var door in doors)
                    {
                        if (!door.ShouldDraw())
                            continue;
                        if (filterByLoot && !door.IsNearLoot)
                            continue;
                        if (!worldBounds.Contains(door.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(door.Position, mapCfg));
                        door.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Quest zones
            if (!Config.BattleMode && Config.ShowQuests)
            {
                var questLocations = Memory.QuestLocations;
                if (questLocations is not null)
                {
                    bool showOptional   = Config.QuestShowOptional;
                    bool showOutlines   = Config.QuestShowOutlines;
                    bool showKill       = Config.QuestShowKillZones;
                    bool showFind       = Config.QuestShowFindZones;
                    bool showPlace      = Config.QuestShowPlaceZones;
                    bool showReach      = Config.QuestShowReachZones;
                    float maxDist       = Config.QuestMaxDistance;
                    bool useMaxDist     = maxDist > 0f;

                    foreach (var loc in questLocations)
                    {
                        if (!showOptional && loc.Optional)
                            continue;

                        // Objective-type filter
                        bool typeAllowed = loc.ObjectiveType switch
                        {
                            Tarkov.GameWorld.Quests.QuestObjectiveType.FindItem      => showFind,
                            Tarkov.GameWorld.Quests.QuestObjectiveType.PlaceItem     => showPlace,
                            Tarkov.GameWorld.Quests.QuestObjectiveType.VisitLocation => showReach,
                            _                                                         => showKill,
                        };
                        if (!typeAllowed)
                            continue;

                        if (!worldBounds.Contains(loc.Position))
                            continue;

                        // Distance cull
                        if (useMaxDist && Vector3.Distance(localPlayer.Position, loc.Position) > maxDist)
                            continue;

                        // Draw outline polygon first (behind marker)
                        if (showOutlines)
                            loc.DrawOutlineProjected(canvas, mapParams, mapCfg);

                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(loc.Position, mapCfg));
                        loc.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Explosives (grenades, tripwires, mortar projectiles)
            if (Config.ShowExplosives)
            {
                var explosives = Memory.Explosives;
                if (explosives is not null)
                {
                    foreach (var item in explosives)
                    {
                        if (!item.IsActive)
                            continue;
                        if (!worldBounds.Contains(item.Position))
                            continue;
                        item.Draw(canvas, mapParams, mapCfg, localPlayer);
                    }
                }

                // Pre-throw arc: local player has a grenade in hand and hasn't thrown yet
                var inHand = Memory.InHandGrenadePrediction;
                if (inHand is not null && inHand.Arc.Count > 1)
                {
                    // Arc path
                    using var arcPath = new SKPath();
                    var firstArcPt = mapParams.ToScreenPos(MapParams.ToMapPos(inHand.Arc[0], mapCfg));
                    arcPath.MoveTo(firstArcPt);
                    for (int i = 1; i < inHand.Arc.Count; i++)
                    {
                        arcPath.LineTo(mapParams.ToScreenPos(MapParams.ToMapPos(inHand.Arc[i], mapCfg)));
                    }
                    canvas.DrawPath(arcPath, SKPaints.PaintGrenadePrediction);

                    // Landing marker
                    var landingPt = mapParams.ToScreenPos(MapParams.ToMapPos(inHand.Landing, mapCfg));
                    canvas.DrawCircle(landingPt, 5f, SKPaints.ShapeBorder);
                    canvas.DrawCircle(landingPt, 5f, SKPaints.PaintGrenadeLanding);

                    // Blast radius circle at predicted landing
                    if (inHand.EffDist > 0f)
                    {
                        float landingRadius = inHand.EffDist * mapCfg.Scale * mapCfg.SvgScale * mapParams.XScale;
                        canvas.DrawCircle(landingPt, landingRadius, SKPaints.PaintExplosivesRadius);
                    }

                    // Grenade name label above landing marker
                    if (!string.IsNullOrEmpty(inHand.Name))
                    {
                        var nameWidth = SKPaints.FontRegular11.MeasureText(inHand.Name, SKPaints.TextExplosives);
                        var namePt = new SKPoint(landingPt.X - nameWidth / 2f, landingPt.Y - 12f);
                        canvas.DrawText(inHand.Name, namePt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
                        canvas.DrawText(inHand.Name, namePt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextExplosives);
                    }

                    // Distance from local player to predicted landing
                    float landingDist = Vector3.Distance(localPlayer.Position, inHand.Landing);
                    var distText = $"{(int)landingDist}m";
                    var distWidth = SKPaints.FontRegular11.MeasureText(distText, SKPaints.TextExplosives);
                    var distPt = new SKPoint(landingPt.X - distWidth / 2f, landingPt.Y + 14f);
                    canvas.DrawText(distText, distPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
                    canvas.DrawText(distText, distPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextExplosives);
                }
            }

            // BTR vehicle
            if (Config.ShowBTR)
            {
                var btr = Memory.Btr;
                if (btr is not null && btr.IsActive)
                {
                    if (worldBounds.Contains(btr.Position))
                        btr.Draw(canvas, mapParams, mapCfg, localPlayer, Config.ShowBTRRoute);
                }
            }

            // Airdrops
            if (Config.ShowAirdrops)
            {
                var airdrops = Memory.Airdrops;
                if (airdrops is not null)
                {
                    foreach (var drop in airdrops)
                    {
                        if (!worldBounds.Contains(drop.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(drop.Position, mapCfg));
                        float dist = Vector3.Distance(localPlayer.Position, drop.Position);
                        drop.Draw(canvas, sp, dist);
                    }
                }
            }

            // Switches (static map data)
            if (Config.ShowSwitches)
            {
                var switches = Memory.Switches;
                if (switches is not null)
                {
                    foreach (var sw in switches)
                    {
                        if (!worldBounds.Contains(sw.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(sw.Position, mapCfg));
                        float dist = Vector3.Distance(localPlayer.Position, sw.Position);
                        sw.Draw(canvas, sp, dist);
                    }
                }
            }

            // Group connectors
            if (Config.ConnectGroups && normalPlayers is not null)
                DrawGroupConnectors(canvas, normalPlayers, map, mapParams);

            // Local player screen position — computed once, shared by rings + draw
            var localScreenPos = mapParams.ToScreenPos(MapParams.ToMapPos(localPlayer.Position, mapCfg));

            localPlayer.Draw(canvas, localScreenPos, localPlayer);

            if (normalPlayers is not null)
            {
                var btr = Memory.Btr;
                foreach (var player in normalPlayers)
                {
                    if (player.IsLocalPlayer)
                        continue;
                    if (!worldBounds.Contains(player.Position))
                        continue;

                    // Snap BTR passengers (turret operator / "scav on top") to the BTR's
                    // own XZ so they stop jittering relative to the moving vehicle.
                    var drawPos = player.Position;
                    btr?.TrySnapPassengerXZ(ref drawPos);

                    var sp = mapParams.ToScreenPos(MapParams.ToMapPos(drawPos, mapCfg));
                    player.Draw(canvas, sp, localPlayer);
                }
            }

            // Mouseover tooltips — drawn last so they're always on top
            DrawMouseoverTooltip(canvas, mapParams, map.Config, localPlayer);

            // Player counter overlay — draggable, top-left by default
            DrawPlayerCounter(canvas, normalPlayers?.Count ?? 0, canvasSize);

            // Killfeed overlay — screen-screen, top-right corner
            if (Config.ShowKillFeed)
                DrawKillfeed(canvas, canvasSize);
        }

        /// <summary>
        /// Draws the killfeed overlay in the top-right corner of the radar canvas.
        /// Uses a lock-free snapshot from <see cref="KillfeedManager"/>; no alloc per frame.
        /// </summary>
        private static void DrawKillfeed(SKCanvas canvas, SKSize canvasSize)
        {
            KillfeedManager.PruneExpired();
            var entries = KillfeedManager.Entries;

            const float LineHeight   = 17f;
            const float PadX         = 6f;
            const float PadY         = 4f;
            const float RightMargin  = 8f;
            const float TopMargin    = 8f;

            float ttl = Config.KillFeedTtlSeconds;

            // Placeholder when empty so users can confirm the overlay toggle is active.
            const string EmptyText = "Killfeed — waiting for kills…";

            // Measure the widest entry (or placeholder) to size the background panel
            float maxW = entries.Length == 0
                ? SKPaints.FontKillfeed.MeasureText(EmptyText)
                : 0f;
            for (int i = 0; i < entries.Length; i++)
            {
                float w = SKPaints.FontKillfeed.MeasureText(entries[i].FormatDisplay());
                if (w > maxW) maxW = w;
            }

            int lines = Math.Max(entries.Length, 1);
            float panelW = maxW + PadX * 2f;
            float panelH = lines * LineHeight + PadY * 2f;
            float panelX, panelY;
            if (Config.KillFeedPosX < 0f || Config.KillFeedPosY < 0f)
            {
                // Default anchor: top-right corner
                panelX = canvasSize.Width - panelW - RightMargin;
                panelY = TopMargin;
            }
            else
            {
                // User-placed — clamp to canvas
                panelX = Math.Clamp(Config.KillFeedPosX, 0f, Math.Max(0f, canvasSize.Width - panelW));
                panelY = Math.Clamp(Config.KillFeedPosY, 0f, Math.Max(0f, canvasSize.Height - panelH));
            }

            // Publish bounds for input hit-testing (drag handle)
            KillfeedBounds = new SKRect(panelX, panelY, panelX + panelW, panelY + panelH);

            // Background panel
            canvas.DrawRect(panelX, panelY, panelW, panelH, SKPaints.KillfeedBackground);

            if (entries.Length == 0)
            {
                float tx0 = panelX + PadX;
                float ty0 = panelY + PadY + LineHeight - 3f;
                canvas.DrawText(EmptyText, tx0 + 1, ty0 + 1, SKPaints.FontKillfeed, SKPaints.TextShadow);
                var scratch0 = SKPaints.KillfeedTextScratch;
                scratch0.Color = SKPaints.TextScav.Color.WithAlpha(180);
                canvas.DrawText(EmptyText, tx0, ty0, SKPaints.FontKillfeed, scratch0);
                return;
            }

            var scratch = SKPaints.KillfeedTextScratch;
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                float alpha = ttl > 0
                    ? Math.Clamp(1f - (float)(entry.AgeSec / ttl), 0.15f, 1f)
                    : 1f;

                // Pick colour by killer side
                SKPaint textPaint = entry.KillerSide switch
                {
                    Tarkov.GameWorld.Player.PlayerType.Teammate     => SKPaints.TextTeammate,
                    Tarkov.GameWorld.Player.PlayerType.USEC         => SKPaints.TextUSEC,
                    Tarkov.GameWorld.Player.PlayerType.BEAR         => SKPaints.TextBEAR,
                    Tarkov.GameWorld.Player.PlayerType.PScav        => SKPaints.TextPScav,
                    _                                               => SKPaints.TextScav,
                };

                float tx = panelX + PadX;
                float ty = panelY + PadY + LineHeight * i + LineHeight - 3f;

                // Retrieve cached display string — no allocation
                string display = entry.FormatDisplay();

                // Shadow
                canvas.DrawText(display, tx + 1, ty + 1, SKPaints.FontKillfeed, SKPaints.TextShadow);

                // Reuse scratch paint — mutate Color only, no Clone/Dispose
                scratch.Color = textPaint.Color.WithAlpha((byte)(alpha * 255f));
                canvas.DrawText(display, tx, ty, SKPaints.FontKillfeed, scratch);
            }
        }

        private static void DrawGroupConnectors(SKCanvas canvas, List<Player> players, IRadarMap map, MapParams mapParams)
        {
            // Reset pooled collections instead of allocating new ones each frame
            _connectorGroups.Clear();
            _connectorPoolIndex = 0;

            foreach (var p in players)
            {
                if (p.IsHuman && p.IsHostile && p.SpawnGroupID != -1)
                {
                    if (!_connectorGroups.TryGetValue(p.SpawnGroupID, out var list))
                    {
                        // Reuse pooled list or create a new one
                        if (_connectorPoolIndex < _connectorPointPool.Count)
                        {
                            list = _connectorPointPool[_connectorPoolIndex];
                            list.Clear();
                        }
                        else
                        {
                            list = new List<SKPoint>(4);
                            _connectorPointPool.Add(list);
                        }
                        _connectorPoolIndex++;
                        _connectorGroups[p.SpawnGroupID] = list;
                    }
                    list.Add(mapParams.ToScreenPos(MapParams.ToMapPos(p.Position, map.Config)));
                }
            }
            if (_connectorGroups.Count == 0)
                return;
            foreach (var grp in _connectorGroups.Values)
            {
                if (grp.Count <= 1)
                    continue;
                for (int i = 0; i < grp.Count - 1; i++)
                {
                    canvas.DrawLine(
                        grp[i].X, grp[i].Y,
                        grp[i + 1].X, grp[i + 1].Y,
                        SKPaints.PaintConnectorGroup);
                }
            }
        }

        private static void DrawStatusMessage(SKCanvas canvas, string message, float scale, bool animated = false)
        {
            var bounds = new SKRect(0, 0, _window.Size.X / scale, _window.Size.Y / scale);

            string dots = "";
            if (animated)
            {
                if (_statusSw.ElapsedMilliseconds > 500)
                {
                    _statusOrder = (_statusOrder % 3) + 1;
                    _statusSw.Restart();
                }
                dots = _statusDots[_statusOrder];
            }

            if (!ReferenceEquals(message, _cachedStatusMessage) || _statusOrder != _cachedStatusOrder)
            {
                _cachedStatusMessage = message;
                _cachedStatusOrder = _statusOrder;
                _cachedStatusComposite = message + dots;
            }

            float textWidth = SKPaints.FontRegular48.MeasureText(_cachedStatusComposite);
            float x = (bounds.Width - textWidth) / 2f;
            float y = bounds.Height / 2f;

            canvas.DrawText(_cachedStatusComposite, x, y, SKPaints.FontRegular48, SKPaints.TextRadarStatus);
        }

        /// <summary>
        /// Draws a compact player counter overlay showing:
        /// shown / tracked / list — where list turns orange when tracked &lt; list
        /// (indicating some players in game are not yet tracked by the radar).
        /// The overlay is draggable; position is persisted in config.
        /// </summary>
        private static void DrawPlayerCounter(SKCanvas canvas, int shown, SKSize canvasSize)
        {
            var players = AllPlayers;
            if (players is null) return;

            int tracked   = players.Count;
            int listCount = players.ListCount;

            // Skip until the game list has been read at least once.
            if (listCount <= 0) return;

            bool hasMissing = tracked < listCount;

            const float PadX    = 8f;
            const float PadY    = 6f;
            const float CornerR = 4f;
            const float Margin  = 8f;

            var font = SKPaints.FontRegular11;

            string label      = "Players  ";
            string shownStr   = shown.ToString();
            string sep        = " / ";
            string trackedStr = tracked.ToString();
            string listStr    = listCount.ToString();

            float wLabel   = font.MeasureText(label);
            float wShown   = font.MeasureText(shownStr);
            float wSep     = font.MeasureText(sep);
            float wTracked = font.MeasureText(trackedStr);
            float wList    = font.MeasureText(listStr);
            float totalW   = wLabel + wShown + wSep + wTracked + wSep + wList;

            float boxW = totalW + PadX * 2f;
            float boxH = font.Size + PadY * 2f;

            // Resolve position: use stored config or fall back to top-left anchor
            float panelX, panelY;
            if (Config.PlayerCounterPosX < 0f || Config.PlayerCounterPosY < 0f)
            {
                panelX = Margin;
                panelY = Margin;
            }
            else
            {
                panelX = Math.Clamp(Config.PlayerCounterPosX, 0f, Math.Max(0f, canvasSize.Width  - boxW));
                panelY = Math.Clamp(Config.PlayerCounterPosY, 0f, Math.Max(0f, canvasSize.Height - boxH));
            }

            // Publish bounds for drag hit-testing
            PlayerCounterBounds = new SKRect(panelX, panelY, panelX + boxW, panelY + boxH);

            var bgRect = new SKRoundRect(new SKRect(panelX, panelY, panelX + boxW, panelY + boxH), CornerR);
            canvas.DrawRoundRect(bgRect, SKPaints.PlayerCounterBackground);

            float baseline = panelY + PadY + font.Size - 1f;
            float cx = panelX + PadX;

            var normal = SKPaints.TextPlayerCounterNormal;
            var warn   = hasMissing ? SKPaints.TextPlayerCounterWarn : normal;

            canvas.DrawText(label,      cx, baseline, font, normal); cx += wLabel;
            canvas.DrawText(shownStr,   cx, baseline, font, normal); cx += wShown;
            canvas.DrawText(sep,        cx, baseline, font, normal); cx += wSep;
            canvas.DrawText(trackedStr, cx, baseline, font, normal); cx += wTracked;
            canvas.DrawText(sep,        cx, baseline, font, normal); cx += wSep;
            canvas.DrawText(listStr,    cx, baseline, font, warn);
        }
    }
}
