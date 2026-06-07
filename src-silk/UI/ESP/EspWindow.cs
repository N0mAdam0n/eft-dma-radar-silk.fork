// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Runtime.CompilerServices;
using eft_dma_radar.Silk.Tarkov.GameWorld.Player;
using eft_dma_radar.Silk.Tarkov.Unity;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SilkWindow = Silk.NET.Windowing.Window;

namespace eft_dma_radar.Silk.UI.ESP
{
    /// <summary>
    /// Separate Silk.NET window for ESP overlay rendering.
    /// Runs on its own thread with its own GL context + SkiaSharp GPU surface.
    /// Projects game entities via <see cref="CameraManager.WorldToScreen"/> and
    /// draws them using SkiaSharp. Designed to be positioned over the game and
    /// used with a screen fuser.
    ///
    /// Non-raid behavior: completely clean window (solid black, no text, no "Waiting for Raid", no instructions).
    /// All UI (players, loot, crosshair, HUD, hidden hints, etc.) only appears after entering a raid.
    ///
    /// Window behavior:
    /// - Default open: borderless fullscreen (no titlebar/borders).
    /// - Double-click: toggle to resizable windowed (borders + title, smaller, movable) &lt;-&gt; back to fullscreen.
    /// - Fullscreen mode never shows borders.
    /// - F2 hotkey (DMA + local): toggles _renderEnabled (show/hide perspective *inside an already-open window*). NEVER opens or closes the window.
    ///   This is strictly "在透视窗口内切换显示/不显示".
    /// - ESC (local): always closes window (escape hatch).
    /// - In non-raid: single left-click also closes (silent convenience).
    /// </summary>
    internal static class EspWindow
    {
        #region Fields

        private static IWindow? _window;
        private static GL? _gl;
        private static GRContext? _grContext;
        private static SKSurface? _skSurface;
        private static GRBackendRenderTarget? _skBackendRenderTarget;
        private static Thread? _thread;
        private static volatile bool _running;

        // Render enabled flag: when false, ESP perspective (players/loot/entities) is not drawn.
        // Window stays open; user uses double-click to shrink to bordered windowed mode to avoid covering game.
        // Controlled by F2 hotkey (DMA + local) and UI toggle (only affects display, never opens/closes window).
        private static bool _renderEnabled = true;

        // Current window presentation mode. Default on Open(): borderless fullscreen (no borders per spec).
        // Double-click toggles to windowed (resizable + titlebar) or back.
        // Fullscreen mode never has borders; windowed mode has them.
        private static bool _isFullscreen = true;

        // For double-click detection on local mouse (within ~280ms)
        private static long _lastMouseDownTick;

        // Input context for local keyboard/mouse escape hatch on the ESP window itself.
        // Works even in non-raid (clean black window) and when DMA/radar UI not available:
        // ESC = close, F2 = toggle render, double-click = windowed/fullscreen mode, single-click (non-raid) = close.
        private static IInputContext? _input;

        // FPS tracking
        private static int _fpsCounter;
        private static int _fps;
        private static long _lastFpsTick;

        // Player standing height offset (feet → head) in world units (fallback only)
        private const float PlayerHeight = 1.8f;
        // Box aspect ratio (width = height / ratio) — matches WPF Skeleton.GetESPBox
        private const float BoxAspectRatio = 2.05f;
        // Health bar width (viewport pixels)
        private const float HealthBarWidth = 3f;
        // Health bar gap from box
        private const float HealthBarGap = 6f;
        // Corner length fraction for cornered box style
        private const float CornerFraction = 0.25f;
        // Minimum box height in pixels to draw a box (below this, draw a head-dot + label only)
        private const float MinBoxHeight = 10f;
        // Sanity ceiling for distance (meters) — rejects garbage world positions
        private const float MaxSaneDistance = 2000f;

        /// <summary>Effective scale for all ESP UI chrome (fonts, strokes, radii, offsets). Applied in addition to viewport projection.</summary>
        private static float UiScale => Config?.EspUIScale ?? 1f;

        #endregion

        #region Properties

        /// <summary>Whether the ESP window is currently open and rendering.</summary>
        public static bool IsOpen => _running && _window is not null;

        private static SilkConfig Config => SilkProgram.Config;

        #endregion

        #region Open / Close

        /// <summary>
        /// Opens the ESP window on a dedicated thread.
        /// Safe to call multiple times — no-op if already open.
        /// </summary>
        public static void Open()
        {
            if (_running)
                return;

            _renderEnabled = true;
            _isFullscreen = true;
            _lastMouseDownTick = 0;

            _running = true;
            _thread = new Thread(RunWindow)
            {
                Name = "EspWindow",
                IsBackground = true,
            };
            _thread.Start();
            Log.WriteLine("[EspWindow] Opening (default: fullscreen borderless, render on)...");
        }

        /// <summary>
        /// Closes the ESP window. Safe to call from any thread.
        /// </summary>
        public static void Close()
        {
            if (!_running)
                return;

            _running = false;
            try { _window?.Close(); } catch { }
            Log.WriteLine("[EspWindow] Close requested.");
        }

        /// <summary>
        /// Toggles the ESP window open/closed.
        /// </summary>
        public static void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        #endregion

        #region Window Thread

        private static void RunWindow()
        {
            try
            {
                var monitor = MonitorInfo.GetMonitor(Config.EspTargetScreen);

                // Always open as borderless fullscreen by default (per user requirement).
                // Non-raid: the window is intentionally kept 100% clean (no drawn text/UI whatsoever).
                // We rely on:
                // - Local input (ESC closes, F2 toggles render, double-click toggles windowed/fullscreen)
                // - Silent single-click close in !InRaid
                // - Radar app controls (sidebar ESP button, ImGui E, settings "开启透视窗口" toggle)
                // This gives true fullscreen no-border on open.
                // User can double-click to "shrink" to resizable titled window (which has borders, clean title "ESP (Windowed)").
                // Fullscreen mode *never* has borders.

                var options = WindowOptions.Default;
                options.Size = new Vector2D<int>(monitor.Width, monitor.Height);
                options.Position = new Vector2D<int>(monitor.Left, monitor.Top);
                options.Title = "ESP";
                options.VSync = false;
                options.FramesPerSecond = Config.EspTargetFps;
                options.UpdatesPerSecond = Config.EspTargetFps;
                options.PreferredStencilBufferBits = 8;
                options.PreferredBitDepth = new Vector4D<int>(8, 8, 8, 8);
                options.WindowBorder = WindowBorder.Hidden; // default: no border, full screen

                _isFullscreen = true;
                _renderEnabled = true;

                _window = SilkWindow.Create(options);

                _window.Load += OnLoad;
                _window.Render += OnRender;
                _window.Resize += OnResize;
                _window.Closing += OnClosing;

                _window.Run();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[EspWindow] Thread fatal: {ex}");
            }
            finally
            {
                _running = false;
                try { _input?.Dispose(); } catch { }
                _input = null;
                _window = null;
                _thread = null;
                Log.WriteLine("[EspWindow] Thread exited.");
            }
        }

        private static void OnLoad()
        {
            try
            {
                _gl = GL.GetApi(_window!);

                // Create input context here (inside OnLoad, after the window has started its loop),
                // matching the pattern in RadarWindow. This allows local keyboard/mouse handlers
                // for ESC (close) / F2 (toggle render) / double-click (mode) even in non-raid clean-window state
                // or when DMA/InputManager/radar UI not available.
                // Doing it too early (right after Create) can throw or fail to initialize the input context.
                _input = _window!.CreateInput();
                foreach (var kb in _input.Keyboards)
                    kb.KeyDown += OnEspKeyDown;
                foreach (var m in _input.Mice)
                    m.MouseDown += OnEspMouseDown;

                var glInterface = GRGlInterface.Create(name =>
                    _window!.GLContext!.TryGetProcAddress(name, out var addr) ? addr : 0);

                if (glInterface is null || !glInterface.Validate())
                {
                    Log.WriteLine("[EspWindow] ERROR: GRGlInterface creation/validation failed!");
                    _window!.Close();
                    return;
                }

                _grContext = GRContext.CreateGl(glInterface);
                if (_grContext is null)
                {
                    Log.WriteLine("[EspWindow] ERROR: GRContext.CreateGl returned null!");
                    _window!.Close();
                    return;
                }
                _grContext.SetResourceCacheLimit(128 * 1024 * 1024); // 128 MB

                _gl.ClearColor(0f, 0f, 0f, 1f);

                CreateSkiaSurface();
                if (_skSurface is null)
                {
                    Log.WriteLine("[EspWindow] ERROR: SKSurface creation failed!");
                    _window!.Close();
                    return;
                }

                Log.WriteLine($"[EspWindow] Loaded — {_window!.Size.X}x{_window.Size.Y}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[EspWindow] OnLoad FATAL: {ex}");
                try { _window?.Close(); } catch { }
            }
        }

        private static void OnResize(Vector2D<int> size)
        {
            _gl?.Viewport(size);
            CreateSkiaSurface();
        }

        private static void OnClosing()
        {
            _running = false;
            _input?.Dispose();
            _input = null;
            _skSurface?.Dispose();
            _skBackendRenderTarget?.Dispose();
            _grContext?.Dispose();
            _gl = null;
            _grContext = null;
            _skSurface = null;
            _skBackendRenderTarget = null;
            Log.WriteLine("[EspWindow] Closed.");
        }

        private static void CreateSkiaSurface()
        {
            _skSurface?.Dispose();
            _skBackendRenderTarget?.Dispose();

            var size = _window!.FramebufferSize;
            if (size.X <= 0 || size.Y <= 0 || _grContext is null || _gl is null)
            {
                _skSurface = null;
                _skBackendRenderTarget = null;
                return;
            }

            _gl.GetInteger(GetPName.SampleBuffers, out int sampleBuffers);
            _gl.GetInteger(GetPName.Samples, out int samples);
            if (sampleBuffers == 0)
                samples = 0;

            int stencilBits = 0;
            try
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                _gl.GetFramebufferAttachmentParameter(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.StencilAttachment,
                    FramebufferAttachmentParameterName.StencilSize,
                    out stencilBits);
            }
            catch
            {
                stencilBits = 8;
            }

            var fbInfo = new GRGlFramebufferInfo(0, (uint)InternalFormat.Rgba8);

            _skBackendRenderTarget = new GRBackendRenderTarget(
                size.X, size.Y, samples, stencilBits, fbInfo);

            _skSurface = SKSurface.Create(
                _grContext,
                _skBackendRenderTarget,
                GRSurfaceOrigin.BottomLeft,
                SKColorType.Rgba8888);
        }

        #endregion

        #region Render Loop

        private static void OnRender(double delta)
        {
            if (_grContext is null || _skSurface is null || _gl is null)
                return;

            try
            {
                // FPS
                _fpsCounter++;
                long now = Environment.TickCount64;
                if (now - _lastFpsTick >= 1000)
                {
                    _fps = _fpsCounter;
                    _fpsCounter = 0;
                    _lastFpsTick = now;
                }

                _grContext.ResetContext(
                    GRGlBackendState.RenderTarget |
                    GRGlBackendState.TextureBinding |
                    GRGlBackendState.View |
                    GRGlBackendState.Blend |
                    GRGlBackendState.Vertex |
                    GRGlBackendState.Program |
                    GRGlBackendState.PixelStore);

                var fbSize = _window!.FramebufferSize;
                _gl.Viewport(0, 0, (uint)fbSize.X, (uint)fbSize.Y);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.StencilBufferBit);

                var canvas = _skSurface.Canvas;
                canvas.Clear(SKColors.Black);

                // Apply current ESP UI scale (fonts + strokes + sizes). Live updates when changed from settings.
                // Must be before any ESP-specific drawing (affects both viewport entities and window-space HUD).
                EspPaints.SetEspScale(UiScale);

                var localPlayer = Memory.LocalPlayer;
                var allPlayers = Memory.Players;

                bool inRaid = Memory.InRaid && localPlayer is not null && CameraManager.IsActive;

                if (!_renderEnabled && inRaid)
                {
                    // Only draw the "hidden" hint when inside a raid but the user has explicitly toggled
                    // perspective display off via F2 / settings. This is useful feedback in-raid.
                    // In non-raid (pre-raid / waiting / lobby etc.): NEVER draw any UI/text at all.
                    // The window stays a clean black overlay. Interaction still possible via:
                    // local ESC (close), double-click (shrink/expand window), F2 (render toggle, no visual until raid).
                    DrawEspHiddenHint(canvas);
                    _grContext.Flush();
                    return;
                }

                if (inRaid)
                {
                    // Scale from game viewport coordinates to ESP window coordinates
                    int vpW = CameraManager.ViewportWidth;
                    int vpH = CameraManager.ViewportHeight;
                    var winSize = _window.Size;

                    if (vpW > 0 && vpH > 0)
                    {
                        float scaleX = winSize.X / (float)vpW;
                        float scaleY = winSize.Y / (float)vpH;
                        canvas.Save();
                        canvas.Scale(scaleX, scaleY);
                        DrawEspEntities(canvas, localPlayer!, allPlayers);
                        canvas.Restore();
                    }

                    // HUD overlays (drawn in window space, not viewport space)
                    if (Config.EspShowCrosshair)
                        DrawCrosshair(canvas);

                    if (Config.EspShowStatusText)
                        DrawStatusText(canvas);

                    if (Config.EspShowEnergyHydration && localPlayer is LocalPlayer lp && lp.HealthReady)
                        DrawEnergyHydration(canvas, lp);

                    if (Config.EspShowFps)
                        DrawFpsOverlay(canvas);
                }
                // Non-raid state: draw nothing at all beyond the black clear above.
                // This fulfills the request for a completely clean window (no text, no instructions, no hints)
                // until the player actually enters a raid.
                // Escape mechanisms (ESC, double-click, radar UI toggles, single-click) remain functional via input handlers.

                _grContext.Flush();
            }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "esp_render", TimeSpan.FromSeconds(5),
                    $"[EspWindow] Render error: {ex.Message}");
            }
        }

        #endregion

        #region ESP Drawing

        private static void DrawEspEntities(SKCanvas canvas, Player localPlayer, RegisteredPlayers? allPlayers)
        {
            var localPos = localPlayer.Position;

            // Players
            if (Config.EspShowPlayers && allPlayers is not null)
            {
                float maxDist = MathF.Min(Config.EspPlayerDistance, MaxSaneDistance);
                float maxDistSq = maxDist * maxDist;

                foreach (var player in allPlayers)
                {
                    if (!player.IsEspVisible)
                        continue;

                    var pPos = player.Position;
                    // Reject invalid / near-origin / NaN world positions (common source of 40000000m labels)
                    if (!IsFinite(pPos) || pPos.LengthSquared() < 1f)
                        continue;

                    float distSq = Vector3.DistanceSquared(localPos, pPos);
                    if (!float.IsFinite(distSq) || distSq > maxDistSq)
                        continue;

                    try
                    {
                        DrawPlayer(canvas, player, MathF.Sqrt(distSq));
                    }
                    catch (Exception ex)
                    {
                        Log.WriteRateLimited(AppLogLevel.Warning, "esp_player_draw", TimeSpan.FromSeconds(5),
                            $"[EspWindow] DrawPlayer failed: {ex.Message}");
                    }
                }
            }

            // Loot
            if (Config.EspShowLoot)
            {
                var loot = Memory.Loot;
                if (loot is not null)
                {
                    float maxDistSq = Config.EspLootDistance * Config.EspLootDistance;

                    foreach (var item in loot)
                    {
                        int price = item.DisplayPrice;
                        var result = item.Evaluate(price);
                        if (!result.Visible)
                            continue;

                        var iPos = item.Position;
                        if (!IsFinite(iPos) || iPos.LengthSquared() < 1f)
                            continue;

                        float distSq = Vector3.DistanceSquared(localPos, iPos);
                        if (!float.IsFinite(distSq) || distSq > maxDistSq)
                            continue;

                        DrawLootItem(canvas, item, price, result, MathF.Sqrt(distSq));
                    }
                }
            }

            // Corpses (dead players with gear value). Separate from loose loot.
            if (Config.EspShowCorpses)
            {
                var corpses = Memory.Corpses;
                if (corpses is not null)
                {
                    float maxDistSq = Config.EspCorpseDistance * Config.EspCorpseDistance;

                    foreach (var corpse in corpses)
                    {
                        var cPos = corpse.Position;
                        if (!IsFinite(cPos) || cPos.LengthSquared() < 1f)
                            continue;

                        float distSq = Vector3.DistanceSquared(localPos, cPos);
                        if (!float.IsFinite(distSq) || distSq > maxDistSq)
                            continue;

                        try
                        {
                            DrawCorpse(canvas, corpse, MathF.Sqrt(distSq));
                        }
                        catch (Exception ex)
                        {
                            Log.WriteRateLimited(AppLogLevel.Warning, "esp_corpse_draw", TimeSpan.FromSeconds(5),
                                $"[EspWindow] DrawCorpse failed: {ex.Message}");
                        }
                    }
                }
            }

            // Ballistics overlay — predicted trajectory + live shot trails.
            try { BallisticsRenderer.Draw(canvas); }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "esp_ballistics_draw", TimeSpan.FromSeconds(5),
                    $"[EspWindow] BallisticsRenderer.Draw failed: {ex.Message}");
            }

            // Grenade trajectory trails (drag tail + bold current dot) + in-hand prediction arc.
            // Controlled from 透视设置 (EspTab). Trails persist briefly after the grenade is gone.
            try { GrenadeEspRenderer.Draw(canvas); }
            catch (Exception ex)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "esp_grenade_draw", TimeSpan.FromSeconds(5),
                    $"[EspWindow] GrenadeEspRenderer.Draw failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        /// <summary>
        /// Draws a single player with box, name, distance, and health bar.
        /// <para>
        /// NOTE: <see cref="Player.Position"/> comes from <c>_playerLookRaycastTransform</c>
        /// (the eye/head raycast point), NOT the feet. We derive the box from skeleton bones
        /// (head + feet) when available, and fall back to an eye-level approximation.
        /// </para>
        /// </summary>
        private static void DrawPlayer(SKCanvas canvas, Player player, float distance)
        {
            var skeleton = player.Skeleton;
            bool haveSkeleton = skeleton is not null && skeleton.IsInitialized;

            // ---- Determine head (top) and feet (bottom) world positions ----
            Vector3 headWorld;
            Vector3 feetWorld;
            var eyePos = player.Position; // eye/raycast, NOT feet

            if (haveSkeleton)
            {
                var headBone = skeleton!.GetBonePosition(Bones.HumanHead);
                var lFoot = skeleton.GetBonePosition(Bones.HumanLFoot);
                var rFoot = skeleton.GetBonePosition(Bones.HumanRFoot);
                var pelvis = skeleton.GetBonePosition(Bones.HumanPelvis);

                // Head
                if (headBone.HasValue && IsFinite(headBone.Value))
                    headWorld = headBone.Value;
                else
                    headWorld = eyePos; // eye ≈ head

                // Feet — prefer the LOWER of the two feet bones; fall back to pelvis minus offset
                Vector3? footCandidate = null;
                if (lFoot.HasValue && IsFinite(lFoot.Value) && rFoot.HasValue && IsFinite(rFoot.Value))
                    footCandidate = lFoot.Value.Y < rFoot.Value.Y ? lFoot.Value : rFoot.Value;
                else if (lFoot.HasValue && IsFinite(lFoot.Value))
                    footCandidate = lFoot.Value;
                else if (rFoot.HasValue && IsFinite(rFoot.Value))
                    footCandidate = rFoot.Value;
                else if (pelvis.HasValue && IsFinite(pelvis.Value))
                    footCandidate = new Vector3(pelvis.Value.X, pelvis.Value.Y - 0.95f, pelvis.Value.Z);

                feetWorld = footCandidate ?? new Vector3(eyePos.X, eyePos.Y - PlayerHeight, eyePos.Z);

                // Sanity: head must be above feet by a plausible margin
                float heightDiff = headWorld.Y - feetWorld.Y;
                if (heightDiff < 0.5f || heightDiff > 3.0f)
                {
                    headWorld = eyePos;
                    feetWorld = new Vector3(eyePos.X, eyePos.Y - PlayerHeight, eyePos.Z);
                }
            }
            else
            {
                // No skeleton yet — approximate head at eye level, feet one body below
                headWorld = eyePos;
                feetWorld = new Vector3(eyePos.X, eyePos.Y - PlayerHeight, eyePos.Z);
            }

            // Snap BTR passengers (turret operator / "scav on top") to the BTR's own XZ
            // so the ESP box/bones stop jittering relative to the moving vehicle.
            // Applied after skeleton/fallback resolution so both head and feet move together.
            var btr = Memory.Btr;
            if (btr is not null && btr.TrySnapPassengerXZ(ref feetWorld))
            {
                headWorld.X = feetWorld.X;
                headWorld.Z = feetWorld.Z;
            }

            // Project both points
            if (!CameraManager.WorldToScreen(ref headWorld, out var headScreen, true, true))
                return;
            if (!CameraManager.WorldToScreen(ref feetWorld, out var feetScreen, true, true))
                return;

            var (boxPaint, textPaint) = EspPaints.GetPlayerPaints(player.Type);

            // ---- Box dimensions (WPF pattern) ----
            float boxHeight = MathF.Abs(feetScreen.Y - headScreen.Y);
            float centerX = (headScreen.X + feetScreen.X) * 0.5f;
            float topY = MathF.Min(headScreen.Y, feetScreen.Y);
            float bottomY = MathF.Max(headScreen.Y, feetScreen.Y);

            // Scale the min-box threshold inversely with UI scale: larger UI scale makes small projections more usable (thicker lines + bigger labels).
            float minBoxH = MinBoxHeight / MathF.Max(0.1f, UiScale);

            // Independent checkboxes (primary control from 透视设置). Hotkey cycles presets into these flags.
            bool drawBox = Config.EspShowBox && boxHeight >= minBoxH;
            bool drawBones = Config.EspShowBones && haveSkeleton;
            // Head dot shown if explicitly enabled, and not overridden by a large box (when both box+dot selected).
            bool drawHeadDot = Config.EspShowHeadDot && (boxHeight < minBoxH || !Config.EspShowBox);

            SKRect box = default;
            if (drawBox)
            {
                float boxWidth = boxHeight / BoxAspectRatio;
                box = new SKRect(
                    centerX - boxWidth * 0.5f,
                    topY,
                    centerX + boxWidth * 0.5f,
                    bottomY);

                DrawCorneredBox(canvas, box, boxPaint);

                // Health bar on left side of box
                DrawHealthBar(canvas, player, box);
            }
            else if (drawHeadDot)
            {
                canvas.DrawCircle(centerX, topY, 3f * UiScale, boxPaint);
            }

            if (drawBones)
                DrawBones(canvas, player);

            // ---- Labels ----
            string name = player.Name;
            float nameY = topY - 4f * UiScale;
            if (!string.IsNullOrEmpty(name))
            {
                float nameWidth = EspPaints.FontName.MeasureText(name);
                float nameX = centerX - nameWidth * 0.5f;
                canvas.DrawText(name, nameX + 1, nameY + 1, EspPaints.FontName, EspPaints.TextShadow);
                canvas.DrawText(name, nameX, nameY, EspPaints.FontName, textPaint);
            }

            // ---- Weapon / Ammo / Player Status (under name, controlled from ESP settings) ----
            if (player.HandsReady)
            {
                string? infoLine = null;

                if (Config.EspShowWeapon && !string.IsNullOrEmpty(player.InHandsItem))
                {
                    infoLine = player.InHandsItem;

                    bool hasAmmoCount = Config.EspShowAmmo && player.IsWeaponInHands && player.AmmoInMag >= 0;
                    bool hasBulletType = !string.IsNullOrEmpty(player.InHandsAmmo);

                    if (hasAmmoCount || hasBulletType)
                    {
                        string ammoPart = "";
                        if (hasAmmoCount)
                        {
                            ammoPart = player.MagCapacity > 0
                                ? $"{player.AmmoInMag}/{player.MagCapacity}"
                                : $"{player.AmmoInMag}";
                        }
                        if (hasBulletType)
                        {
                            if (!string.IsNullOrEmpty(ammoPart))
                                ammoPart += " ";
                            ammoPart += player.InHandsAmmo;
                        }
                        infoLine += $" ({ammoPart})";
                    }

                    if (Config.EspShowPlayerStatus && !string.IsNullOrEmpty(player.FireMode))
                    {
                        infoLine += $" [{player.FireMode}]";
                    }
                }

                // Health status as additional player status (if not healthy)
                if (Config.EspShowPlayerStatus && player.HealthStatus != EHealthStatus.Healthy)
                {
                    string hs = player.HealthStatus switch
                    {
                        EHealthStatus.Injured => "受伤",
                        EHealthStatus.BadlyInjured => "重伤",
                        EHealthStatus.Dying => "垂死",
                        _ => player.HealthStatus.ToString()
                    };
                    if (infoLine == null)
                        infoLine = hs;
                    else
                        infoLine += $" | {hs}";
                }

                if (!string.IsNullOrEmpty(infoLine))
                {
                    float iw = EspPaints.FontInfo.MeasureText(infoLine);
                    float ix = centerX - iw / 2f;
                    float iy = nameY + EspPaints.FontName.Size + 2f * UiScale; // directly under name (or top if no name)
                    canvas.DrawText(infoLine, ix + 1, iy + 1, EspPaints.FontInfo, EspPaints.TextShadow);
                    canvas.DrawText(infoLine, ix, iy, EspPaints.FontInfo, textPaint);
                }
            }

            string distText = $"{(int)distance}m";
            float distWidth = EspPaints.FontInfo.MeasureText(distText);
            float distX = centerX - distWidth * 0.5f;
            float distY = bottomY + EspPaints.FontInfo.Size + 2f * UiScale;
            canvas.DrawText(distText, distX + 1, distY + 1, EspPaints.FontInfo, EspPaints.TextShadow);
            canvas.DrawText(distText, distX, distY, EspPaints.FontInfo, textPaint);
        }

        /// <summary>
        /// Draws a cornered box (only corners drawn, not full rectangle).
        /// </summary>
        private static void DrawCorneredBox(SKCanvas canvas, SKRect box, SKPaint paint)
        {
            float w = box.Width;
            float h = box.Height;
            float cw = w * CornerFraction;
            float ch = h * CornerFraction;

            // Outline (thicker, black)
            // Top-left
            canvas.DrawLine(box.Left, box.Top, box.Left + cw, box.Top, EspPaints.BoxOutline);
            canvas.DrawLine(box.Left, box.Top, box.Left, box.Top + ch, EspPaints.BoxOutline);
            // Top-right
            canvas.DrawLine(box.Right, box.Top, box.Right - cw, box.Top, EspPaints.BoxOutline);
            canvas.DrawLine(box.Right, box.Top, box.Right, box.Top + ch, EspPaints.BoxOutline);
            // Bottom-left
            canvas.DrawLine(box.Left, box.Bottom, box.Left + cw, box.Bottom, EspPaints.BoxOutline);
            canvas.DrawLine(box.Left, box.Bottom, box.Left, box.Bottom - ch, EspPaints.BoxOutline);
            // Bottom-right
            canvas.DrawLine(box.Right, box.Bottom, box.Right - cw, box.Bottom, EspPaints.BoxOutline);
            canvas.DrawLine(box.Right, box.Bottom, box.Right, box.Bottom - ch, EspPaints.BoxOutline);

            // Colored corners
            // Top-left
            canvas.DrawLine(box.Left, box.Top, box.Left + cw, box.Top, paint);
            canvas.DrawLine(box.Left, box.Top, box.Left, box.Top + ch, paint);
            // Top-right
            canvas.DrawLine(box.Right, box.Top, box.Right - cw, box.Top, paint);
            canvas.DrawLine(box.Right, box.Top, box.Right, box.Top + ch, paint);
            // Bottom-left
            canvas.DrawLine(box.Left, box.Bottom, box.Left + cw, box.Bottom, paint);
            canvas.DrawLine(box.Left, box.Bottom, box.Left, box.Bottom - ch, paint);
            // Bottom-right
            canvas.DrawLine(box.Right, box.Bottom, box.Right - cw, box.Bottom, paint);
            canvas.DrawLine(box.Right, box.Bottom, box.Right, box.Bottom - ch, paint);
        }

        /// <summary>
        /// Draws skeleton bones for a player by projecting bone world positions to screen.
        /// </summary>
        private static void DrawBones(SKCanvas canvas, Player player)
        {
            var skeleton = player.Skeleton;
            if (skeleton is null || !skeleton.IsInitialized)
                return;

            // Spine
            DrawBoneLine(canvas, skeleton, Bones.HumanHead, Bones.HumanNeck);
            DrawBoneLine(canvas, skeleton, Bones.HumanNeck, Bones.HumanSpine3);
            DrawBoneLine(canvas, skeleton, Bones.HumanSpine3, Bones.HumanSpine2);
            DrawBoneLine(canvas, skeleton, Bones.HumanSpine2, Bones.HumanSpine1);
            DrawBoneLine(canvas, skeleton, Bones.HumanSpine1, Bones.HumanPelvis);

            // Arms
            DrawBoneLine(canvas, skeleton, Bones.HumanNeck, Bones.HumanLCollarbone);
            DrawBoneLine(canvas, skeleton, Bones.HumanNeck, Bones.HumanRCollarbone);
            DrawBoneLine(canvas, skeleton, Bones.HumanLCollarbone, Bones.HumanLForearm2);
            DrawBoneLine(canvas, skeleton, Bones.HumanRCollarbone, Bones.HumanRForearm2);
            DrawBoneLine(canvas, skeleton, Bones.HumanLForearm2, Bones.HumanLPalm);
            DrawBoneLine(canvas, skeleton, Bones.HumanRForearm2, Bones.HumanRPalm);

            // Legs
            DrawBoneLine(canvas, skeleton, Bones.HumanPelvis, Bones.HumanLThigh2);
            DrawBoneLine(canvas, skeleton, Bones.HumanPelvis, Bones.HumanRThigh2);
            DrawBoneLine(canvas, skeleton, Bones.HumanLThigh2, Bones.HumanLFoot);
            DrawBoneLine(canvas, skeleton, Bones.HumanRThigh2, Bones.HumanRFoot);
        }

        /// <summary>
        /// Projects two bones and draws a line between them if both succeed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawBoneLine(SKCanvas canvas, Skeleton skeleton, Bones from, Bones to)
        {
            var fromPos = skeleton.GetBonePosition(from);
            var toPos = skeleton.GetBonePosition(to);
            if (!fromPos.HasValue || !toPos.HasValue)
                return;

            var fromWorld = fromPos.Value;
            var toWorld = toPos.Value;
            if (!IsFinite(fromWorld) || !IsFinite(toWorld))
                return;

            if (!CameraManager.WorldToScreen(ref fromWorld, out var fromScreen, false, false))
                return;
            if (!CameraManager.WorldToScreen(ref toWorld, out var toScreen, false, false))
                return;

            canvas.DrawLine(fromScreen.X, fromScreen.Y, toScreen.X, toScreen.Y, EspPaints.BoneLine);
        }

        /// <summary>
        /// Draws a vertical health bar to the left of the player box.
        /// </summary>
        private static void DrawHealthBar(SKCanvas canvas, Player player, SKRect box)
        {
            float hbw = HealthBarWidth * UiScale;
            float hbg = HealthBarGap * UiScale;
            float barLeft = box.Left - hbg - hbw;
            float barTop = box.Top;
            float barBottom = box.Bottom;
            float barHeight = barBottom - barTop;

            // Background
            canvas.DrawRect(barLeft, barTop, hbw, barHeight, EspPaints.HealthBarBg);

            // Health fill
            float healthPct = player.HealthStatus switch
            {
                EHealthStatus.Healthy => 1f,
                EHealthStatus.Injured => 0.65f,
                EHealthStatus.BadlyInjured => 0.35f,
                EHealthStatus.Dying => 0.15f,
                _ => 1f,
            };

            var healthPaint = healthPct switch
            {
                > 0.5f => EspPaints.HealthGreen,
                > 0.25f => EspPaints.HealthYellow,
                _ => EspPaints.HealthRed,
            };

            float fillHeight = barHeight * healthPct;
            canvas.DrawRect(barLeft, barBottom - fillHeight, hbw, fillHeight, healthPaint);
        }

        /// <summary>
        /// Draws a loot item label at its projected screen position.
        /// </summary>
        private static void DrawLootItem(SKCanvas canvas, LootItem item, int price, LootFilter.FilterResult result, float distance)
        {
            var pos = item.Position;
            if (!CameraManager.WorldToScreen(ref pos, out var screenPos, false, false))
                return;

            var textPaint = result.QuestRequired ? EspPaints.TextLootQuest
                : result.Wishlisted ? EspPaints.TextLootWishlist
                : result.Important ? EspPaints.TextLootImportant
                : EspPaints.TextLoot;

            string label = price > 0
                ? $"{item.ShortName} ({LootFilter.FormatPrice(price)}) [{(int)distance}m]"
                : $"{item.ShortName} [{(int)distance}m]";

            float labelWidth = EspPaints.FontLoot.MeasureText(label);
            float lx = screenPos.X - labelWidth / 2f;
            float ly = screenPos.Y;

            canvas.DrawText(label, lx + 1, ly + 1, EspPaints.FontLoot, EspPaints.TextShadow);
            canvas.DrawText(label, lx, ly, EspPaints.FontLoot, textPaint);
        }

        private static void DrawCorpse(SKCanvas canvas, LootCorpse corpse, float distance)
        {
            var pos = corpse.Position;
            if (!CameraManager.WorldToScreen(ref pos, out var screenPos, false, false))
                return;

            string name = (corpse.Name == "Corpse") ? "尸体" : corpse.Name;

            string label = (corpse.GearReady && corpse.TotalValue > 0)
                ? $"{name} ({LootFilter.FormatPrice(corpse.TotalValue)}) [{(int)distance}m]"
                : $"{name} [{(int)distance}m]";

            // Use CJK font for corpse labels ("尸体" + possible CN names) to avoid garbled text.
            // (Regular loot items keep the NeoSans FontLoot for visual style.)
            float labelWidth = EspPaints.FontCjk.MeasureText(label);
            float lx = screenPos.X - labelWidth / 2f;
            float ly = screenPos.Y;

            canvas.DrawText(label, lx + 1, ly + 1, EspPaints.FontCjk, EspPaints.TextShadow);
            canvas.DrawText(label, lx, ly, EspPaints.FontCjk, EspPaints.TextCorpse);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Drawn ONLY when inside a raid (_renderEnabled == false via F2 / UI).
        /// In non-raid state we deliberately draw zero UI (clean black window per requirement).
        /// The hint tells the user the perspective is intentionally hidden and how to re-enable or shrink.
        /// </summary>
        private static void DrawEspHiddenHint(SKCanvas canvas)
        {
            // Use FramebufferSize (physical pixels) not .Size (logical / DPI scaled) so that
            // centering is correct on high-DPI / scaled displays, and text doesn't appear
            // shifted or only partially visible.
            var size = _window!.FramebufferSize;
            float s = UiScale;

            string main = "ESP 透视已隐藏";
            string sub1 = "按 F2 切换显示";
            string sub2 = "双击 切换窗口模式（缩小带边框 / 全屏无边）";
            string sub3 = "ESC 关闭窗口";

            // Use CJK-capable font (system YaHei etc. if present) so Chinese renders without
            // garbled characters / tofu / partial glyphs. This is the text users see centered
            // after pressing F2 to hide ESP content while in-raid.
            // center main
            float tw = EspPaints.FontHint.MeasureText(main);
            float cx = (size.X - tw) / 2f;
            float cy = size.Y * 0.42f;
            canvas.DrawText(main, cx + 1, cy + 1, EspPaints.FontHint, EspPaints.TextShadow);
            canvas.DrawText(main, cx, cy, EspPaints.FontHint, EspPaints.TextBar);

            float y = cy + EspPaints.FontHint.Size + 18 * s;
            foreach (var line in new[] { sub1, sub2, sub3 })
            {
                float lw = EspPaints.FontHint.MeasureText(line);
                float lx = (size.X - lw) / 2f;
                canvas.DrawText(line, lx + 1, y + 1, EspPaints.FontHint, EspPaints.TextShadow);
                canvas.DrawText(line, lx, y, EspPaints.FontHint, EspPaints.TextBar);
                y += EspPaints.FontHint.Size + 8 * s;
            }
        }

        private static void DrawFpsOverlay(SKCanvas canvas)
        {
            // (FPS overlay uses small fixed offsets; no full-size centering needed.
            // Framebuffer correctness is handled by the GL viewport.)
            float s = UiScale;
            string fpsText = $"{_fps} FPS";
            canvas.DrawText(fpsText, 7 * s, 17 * s, EspPaints.FontInfo, EspPaints.TextShadow);
            canvas.DrawText(fpsText, 6 * s, 16 * s, EspPaints.FontInfo, EspPaints.TextBar);
        }

        /// <summary>
        /// Draws a center crosshair overlay in one of 6 styles.
        /// </summary>
        private static void DrawCrosshair(SKCanvas canvas)
        {
            var size = _window!.FramebufferSize;
            if (size.X <= 0 || size.Y <= 0)
                return;

            float cscale = Config.EspCrosshairScale;
            float ui = UiScale;
            float cx = size.X * 0.5f;
            float cy = size.Y * 0.5f;
            float s = 10f * ui * cscale;
            float dot = 3f * ui * cscale;

            switch (Config.EspCrosshairType)
            {
                case 0: // Plus
                    canvas.DrawLine(cx - s, cy, cx + s, cy, EspPaints.Crosshair);
                    canvas.DrawLine(cx, cy - s, cx, cy + s, EspPaints.Crosshair);
                    break;
                case 1: // Cross
                    canvas.DrawLine(cx - s, cy - s, cx + s, cy + s, EspPaints.Crosshair);
                    canvas.DrawLine(cx + s, cy - s, cx - s, cy + s, EspPaints.Crosshair);
                    break;
                case 2: // Circle
                    canvas.DrawCircle(cx, cy, s, EspPaints.Crosshair);
                    break;
                case 3: // Dot
                    canvas.DrawCircle(cx, cy, dot, EspPaints.CrosshairDot);
                    break;
                case 4: // Square
                    canvas.DrawRect(cx - s, cy - s, s * 2, s * 2, EspPaints.Crosshair);
                    break;
                case 5: // Diamond
                    using (var path = new SKPath())
                    {
                        path.MoveTo(cx, cy - s);
                        path.LineTo(cx + s, cy);
                        path.LineTo(cx, cy + s);
                        path.LineTo(cx - s, cy);
                        path.Close();
                        canvas.DrawPath(path, EspPaints.Crosshair);
                    }
                    break;
            }
        }

        /// <summary>
        /// (Memory write status banner removed — no active write features.)
        /// </summary>
        private static void DrawStatusText(SKCanvas canvas)
        {
            // Intentionally left empty after removal of memory write features.
        }

        /// <summary>
        /// Draws local player Energy + Hydration bars at the bottom-right.
        /// </summary>
        private static void DrawEnergyHydration(SKCanvas canvas, LocalPlayer lp)
        {
            var size = _window!.FramebufferSize;
            float s = UiScale;
            float barW = 150f * s;
            float barH = 12f * s;
            float spacing = 6f * s;
            float margin = 15f * s;

            float right = size.X - margin;
            float x = right - barW;
            float yEnergy = size.Y * 0.80f - (barH * 2 + spacing);
            float yHydration = yEnergy + barH + spacing;

            DrawBar(canvas, x, yEnergy, barW, barH, lp.Energy, 100f, EspPaints.EnergyFill);
            DrawBar(canvas, x, yHydration, barW, barH, lp.Hydration, 100f, EspPaints.HydrationFill);

            DrawBarText(canvas, x, yEnergy, barW, barH, lp.Energy.ToString("F1"));
            DrawBarText(canvas, x, yHydration, barW, barH, lp.Hydration.ToString("F1"));
        }

        private static void DrawBar(SKCanvas canvas, float x, float y, float w, float h,
            float current, float max, SKPaint fillPaint)
        {
            var bg = new SKRect(x, y, x + w, y + h);
            canvas.DrawRect(bg, EspPaints.StatusBarBg);

            float pct = Math.Clamp(current / max, 0f, 1f);
            if (pct > 0f)
                canvas.DrawRect(x, y, w * pct, h, fillPaint);

            canvas.DrawRect(bg, EspPaints.StatusBarBorder);
        }

        private static void DrawBarText(SKCanvas canvas, float x, float y, float w, float h, string text)
        {
            float tw = EspPaints.FontBar.MeasureText(text);
            float tx = x + (w - tw) * 0.5f;
            float ty = y + h * 0.5f + EspPaints.FontBar.Size / 3f;
            canvas.DrawText(text, tx + 1, ty + 1, EspPaints.FontBar, EspPaints.TextShadow);
            canvas.DrawText(text, tx, ty, EspPaints.FontBar, EspPaints.TextBar);
        }

        /// <summary>
        /// Cycles <see cref="SilkConfig.EspRenderMode"/> through 0 → 1 → 2 → 3 → 0.
        /// </summary>
        public static void CycleRenderMode()
        {
            Config.EspRenderMode = (Config.EspRenderMode + 1) % 4;

            // Map the legacy mode to the independent checkbox flags so the hotkey
            // visibly affects what is drawn in ESP and what the settings show.
            switch (Config.EspRenderMode)
            {
                case 0: // labels / nothing
                    Config.EspShowBones = false;
                    Config.EspShowBox = false;
                    Config.EspShowHeadDot = false;
                    break;
                case 1: // bones only
                    Config.EspShowBones = true;
                    Config.EspShowBox = false;
                    Config.EspShowHeadDot = false;
                    break;
                case 2: // box (with bones)
                    Config.EspShowBox = true;
                    Config.EspShowHeadDot = false;
                    Config.EspShowBones = true;
                    break;
                case 3: // head dot only
                    Config.EspShowBones = false;
                    Config.EspShowBox = false;
                    Config.EspShowHeadDot = true;
                    break;
            }

            Config.MarkDirty();
        }

        /// <summary>
        /// Toggles whether the ESP perspective (entities, players, loot, etc.) is drawn.
        /// Does NOT open or close the window — only affects rendering inside an open window.
        /// Called by the (now repurposed) F2 hotkey and the settings toggle row.
        /// </summary>
        public static void ToggleRender()
        {
            if (!IsOpen)
                return; // F2 (and local F2 inside the window) is *only* for toggling display inside the open window. Never opens/closes.
            _renderEnabled = !_renderEnabled;
            Log.WriteLine($"[EspWindow] Render toggled: {(_renderEnabled ? "ON (perspective visible)" : "OFF (hidden)")}");
        }

        /// <summary>
        /// Sets the render enabled state directly (from the "显示透视内容" checkbox in ESP settings).
        /// This ONLY affects whether perspective content is drawn. It NEVER opens or closes the window.
        /// The window open/close is controlled exclusively by the dedicated "开启透视窗口" toggle / E key / sidebar.
        /// F2 hotkey (DMA + local) calls ToggleRender which also strictly guards against window state changes.
        /// </summary>
        public static void SetRenderEnabled(bool enabled)
        {
            _renderEnabled = enabled;
            Log.WriteLine($"[EspWindow] Render set: {(_renderEnabled ? "ON" : "OFF")}");
        }

        /// <summary>
        /// Public accessor for current render state (used by EspTab checkbox and hotkey registration).
        /// </summary>
        public static bool RenderEnabled => _renderEnabled;

        /// <summary>
        /// Toggles between borderless fullscreen (default) and resizable windowed (with borders/titlebar).
        /// Triggered by double-click on the ESP window.
        /// - Fullscreen: always WindowBorder.Hidden, full monitor size/pos.
        /// - Windowed: Resizable border + title, smaller centered size (user can drag/resize/close via OS).
        /// </summary>
        public static void ToggleWindowMode()
        {
            if (_window is null)
                return;
            try
            {
                var m = MonitorInfo.GetMonitor(Config.EspTargetScreen);
                if (_isFullscreen)
                {
                    // shrink
                    int w = Math.Min(m.Width, 1280);
                    int h = Math.Min(m.Height, 720);
                    _window.Size = new Vector2D<int>(w, h);
                    _window.Position = new Vector2D<int>(m.Left + (m.Width - w) / 2, m.Top + (m.Height - h) / 2);
                    _window.WindowBorder = WindowBorder.Resizable;
                    _window.Title = "ESP (Windowed)";
                    _isFullscreen = false;
                    Log.WriteLine("[EspWindow] Switched to WINDOWED mode (borders enabled, user can drag/resize/X)");
                }
                else
                {
                    // expand to full borderless
                    _window.Size = new Vector2D<int>(m.Width, m.Height);
                    _window.Position = new Vector2D<int>(m.Left, m.Top);
                    _window.WindowBorder = WindowBorder.Hidden;
                    _window.Title = "ESP";
                    _isFullscreen = true;
                    Log.WriteLine("[EspWindow] Switched to FULLSCREEN borderless (no borders)");
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[EspWindow] ToggleWindowMode failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the current <see cref="SilkConfig.EspTargetFps"/> to the live window.
        /// Safe to call from the UI thread while the window is running.
        /// </summary>
        public static void ApplyTargetFps()
        {
            if (_window is null)
                return;
            try
            {
                int fps = Config.EspTargetFps;
                _window.FramesPerSecond = fps;
                _window.UpdatesPerSecond = fps;
            }
            catch { }
        }

        /// <summary>
        /// Moves and resizes the live ESP window to the currently selected monitor.
        /// Safe to call from the UI thread while the window is running.
        /// </summary>
        public static void ApplyTargetMonitor()
        {
            if (_window is null)
                return;
            try
            {
                var m = MonitorInfo.GetMonitor(Config.EspTargetScreen);

                // Honor the current user-chosen presentation mode (set by double-click or initial default).
                // "将透视移动到显示器" should keep fullscreen vs windowed preference.
                if (_isFullscreen)
                {
                    _window.Size = new Vector2D<int>(m.Width, m.Height);
                    _window.Position = new Vector2D<int>(m.Left, m.Top);
                    _window.WindowBorder = WindowBorder.Hidden;
                    _window.Title = "ESP";
                }
                else
                {
                    int w = Math.Min(m.Width, 1280);
                    int h = Math.Min(m.Height, 720);
                    _window.Size = new Vector2D<int>(w, h);
                    _window.Position = new Vector2D<int>(m.Left + (m.Width - w) / 2, m.Top + (m.Height - h) / 2);
                    _window.WindowBorder = WindowBorder.Resizable;
                    _window.Title = "ESP (Windowed)";
                }

                Log.WriteLine($"[EspWindow] Moved to Monitor {m.Index + 1} ({m.Width}x{m.Height} @ {m.Left},{m.Top}) (fullscreen={_isFullscreen})");
            }
            catch { }
        }

        #endregion

        #region Local Input (escape hatch for ESP window — works even on clean non-raid black window)

        /// <summary>
        /// Local key handler on the ESP GLFW window.
        /// ESC: always close the window (reliable escape hatch).
        /// F2: toggle render (perspective display on/off) — does not close window.
        /// Works even in non-raid (clean black window, no drawn UI) or before DMA/InputManager ready.
        /// </summary>
        private static void OnEspKeyDown(IKeyboard keyboard, Key key, int scancode)
        {
            if (key == Key.Escape)
            {
                Log.WriteLine("[EspWindow] Local ESC: close requested.");
                Close();
            }
            else if (key == Key.F2)
            {
                Log.WriteLine("[EspWindow] Local F2: toggle render (perspective visibility).");
                ToggleRender();
            }
        }

        /// <summary>
        /// Mouse handler on the ESP window (local input).
        /// - Double-click (Left): toggle between fullscreen borderless &lt;-&gt; windowed (with borders/titlebar).
        ///   Primary way to shrink the overlay (get a titled small window) or expand back.
        /// - Single-click (Left) when !InRaid: silently closes the window (convenience for pre-raid black overlay).
        ///
        /// Note: in non-raid the ESP window is deliberately blank (no drawn text or UI at all).
        /// Users rely on muscle memory, radar app controls (E key / sidebar / settings), or local ESC/double-click.
        /// In-raid clicks do not pass through (the overlay captures input); shrink first if needed.
        /// </summary>
        private static void OnEspMouseDown(IMouse mouse, MouseButton button)
        {
            if (button != MouseButton.Left)
                return;

            long now = Environment.TickCount64;
            bool isDouble = (now - _lastMouseDownTick) < 280;
            _lastMouseDownTick = now;

            if (isDouble)
            {
                Log.WriteLine("[EspWindow] Double-click detected: toggling window mode (fullscreen &lt;-&gt; windowed with border)");
                ToggleWindowMode();
                return;
            }

            // Single click in non-raid: silent close (no visual prompt since window must stay clean).
            // Helps dismiss the black overlay quickly before raid starts.
            if (!Memory.InRaid || !CameraManager.IsActive)
            {
                Log.WriteLine("[EspWindow] Single-click close in non-raid state.");
                Close();
            }
        }

        #endregion
    }
}
