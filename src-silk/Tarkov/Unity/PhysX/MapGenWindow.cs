using System.IO;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace eft_dma_radar.Silk.Tarkov.Unity.PhysX
{
    /// <summary>
    /// Live tuning panel for <see cref="MapImageGenerator"/>. Renders the current
    /// PhysX snapshot to an in-memory bitmap, uploads it to a GL texture, and
    /// shows it via <c>ImGui.Image</c> with an interactive zoom/pan canvas, a
    /// world-Y histogram for placing deck splits, and full control over
    /// resolution, filtering, classification, render style, and colours — then
    /// writes the PNG layers + JSON to disk without the close / rebuild /
    /// relaunch / regenerate cycle.
    /// <para>
    /// The preview is capped to <see cref="PreviewMaxDim"/> px so it stays cheap;
    /// "Generate to Disk" uses the full <see cref="MapGenOptions"/> (real px/m).
    /// </para>
    /// <para>Toggled via the VisCheck "Map Generator" hotkey or the More popup.</para>
    /// </summary>
    internal static class MapGenWindow
    {
        public static bool IsVisible { get; set; }
        public static void Toggle() => IsVisible = !IsVisible;

        private const int PreviewMaxDim = 1400;
        private const long DebounceMs = 160;
        private static readonly SKColor PreviewBg = new(18, 18, 20, 255);

        private static MapGenOptions _opts = new();
        private static MapGenPlan? _plan;
        private static int _selectedLayer;
        private static PreviewTexture? _preview;

        // Preview freshness.
        private static SceneSnapshot? _planSnapshotRef;
        private static bool _previewDirty = true;
        private static long _lastEditMs;
        private static bool _autoRefresh = true;

        // Status + last write.
        private static string _status = "";
        private static long _statusMs;
        private static Vector4 _statusCol = new(0.40f, 0.85f, 0.40f, 1f);
        private static string _lastOutputDir = "";

        private static string _outputRoot = "generated_maps";

        // Interactive view state.
        private static float _zoom = 1f;
        private static Vector2 _panPx = Vector2.Zero;
        private static bool _showGrid;
        private static Vector2? _hoverWorld;

        // Height histogram (cached per plan).
        private static MapGenPlan? _histoPlanRef;
        private static float[] _histo = Array.Empty<float>();
        private static float _histoYMin, _histoYMax, _histoMax;
        private static int _dragSplit = -1;

        // Deck-height paste box.
        private static string _deckPaste = "";
        private static bool _showDeckPaste;

        // ── Frame entry ──────────────────────────────────────────────────────

        public static void Draw()
        {
            if (!IsVisible) return;

            var io = ImGui.GetIO();
            ImGui.SetNextWindowSizeConstraints(new Vector2(900f, 560f), io.DisplaySize);
            ImGui.SetNextWindowSize(new Vector2(1240f, 800f), ImGuiCond.FirstUseEver);

            bool open = IsVisible;
            if (!ImGui.Begin("Map Generator", ref open, ImGuiWindowFlags.NoCollapse))
            {
                IsVisible = open;
                ImGui.End();
                return;
            }
            IsVisible = open;

            try
            {
                EnsurePreviewFresh();

                DrawHeader();
                ImGui.Separator();

                const float ControlsWidth = 340f;
                float regionW = ImGui.GetContentRegionAvail().X;
                float previewW = MathF.Max(280f, regionW - ControlsWidth - 8f);

                if (ImGui.BeginChild("##mg_preview", new Vector2(previewW, 0),
                        ImGuiChildFlags.Borders,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                    DrawPreviewColumn();
                ImGui.EndChild();

                ImGui.SameLine();

                if (ImGui.BeginChild("##mg_ctrl", new Vector2(0, 0), ImGuiChildFlags.Borders))
                    DrawControlsColumn();
                ImGui.EndChild();
            }
            finally
            {
                ImGui.End();
            }
        }

        // ── Header ───────────────────────────────────────────────────────────

        private static void DrawHeader()
        {
            var snap = SceneCache.Snapshot;
            var (lbl, col) = SceneCache.State switch
            {
                SceneCacheState.Ready    => ("READY",    new Vector4(0.30f, 0.85f, 0.30f, 1f)),
                SceneCacheState.Building => ("BUILDING", new Vector4(0.95f, 0.85f, 0.20f, 1f)),
                SceneCacheState.Failed   => ("FAILED",   new Vector4(0.95f, 0.30f, 0.25f, 1f)),
                _                        => ("IDLE",     new Vector4(0.65f, 0.65f, 0.65f, 1f)),
            };
            ImGui.Text("Snapshot:"); ImGui.SameLine();
            ImGui.TextColored(col, lbl);
            ImGui.SameLine(0, 12);
            ImGui.TextDisabled($"actors={snap.Actors.Length}   map={CurrentMapId(snap)}");

            bool busy = SceneCache.State == SceneCacheState.Building;
            bool hasData = !snap.IsEmpty;

            if (ImGui.Button("Refresh Preview"))
                RebuildPreview(SceneCache.Snapshot);

            ImGui.SameLine();
            ImGui.BeginDisabled(!hasData || busy);
            if (ImGui.Button("Generate to Disk"))
                DoGenerate();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered() && hasData)
                ImGui.SetTooltip("Write full-resolution PNG layers + JSON using the current settings.");

            ImGui.SameLine();
            if (ImGui.Button("Open Output"))
                OpenFolder();

            ImGui.SameLine(0, 16);
            ImGui.Checkbox("Auto-refresh", ref _autoRefresh);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Re-render the preview automatically a moment after you change a setting.");

            if (!string.IsNullOrEmpty(_status) && Environment.TickCount64 - _statusMs < 8000)
                ImGui.TextColored(_statusCol, _status);
            else if (!string.IsNullOrEmpty(_lastOutputDir))
                ImGui.TextDisabled($"Last output: {_lastOutputDir}");
            else
                ImGui.TextDisabled("Scroll = zoom · drag = pan · double-click = fit. Drag the histogram below to place deck splits.");
        }

        // ── Preview column ───────────────────────────────────────────────────

        private static void DrawPreviewColumn()
        {
            // Toolbar: layer selector + view controls.
            if (_plan is { Layers.Count: > 0 } p)
            {
                ImGui.SetNextItemWidth(360f);
                string cur = p.Layers[Math.Clamp(_selectedLayer, 0, p.Layers.Count - 1)].DisplayName;
                if (ImGui.BeginCombo("##mg_layer", cur))
                {
                    for (int i = 0; i < p.Layers.Count; i++)
                    {
                        bool sel = i == _selectedLayer;
                        if (ImGui.Selectable(p.Layers[i].DisplayName, sel))
                        {
                            _selectedLayer = i;
                            RenderSelectedLayer();
                        }
                        if (sel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                ImGui.SameLine(0, 12);
                ImGui.Checkbox("Grid", ref _showGrid);
                ImGui.SameLine();
                if (ImGui.SmallButton("-")) _zoom = Math.Clamp(_zoom * 0.8f, 0.1f, 40f);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Zoom out (or scroll over the preview)");
                ImGui.SameLine();
                if (ImGui.SmallButton("+")) _zoom = Math.Clamp(_zoom * 1.25f, 0.1f, 40f);
                ImGui.SameLine();
                if (ImGui.Button("Fit")) ResetView();
                ImGui.SameLine();
                ImGui.TextDisabled($"{_zoom * 100f:F0}%");

                DrawPreviewStats();
            }
            else
            {
                ImGui.TextDisabled("No preview — build a snapshot (VisCheck Rebuild) then Refresh.");
            }

            // Reserve the histogram strip at the bottom; canvas gets the rest.
            float histoH = 150f;
            float avail = ImGui.GetContentRegionAvail().Y;
            float canvasH = MathF.Max(120f, avail - histoH - 6f);

            DrawCanvas(canvasH);
            ImGui.Spacing();
            DrawHistogramStrip(histoH);
        }

        private static void DrawPreviewStats()
        {
            if (_plan is null) return;
            var p = _plan;
            string capped = p.Ppm < _opts.PixelsPerMeter - 0.01f ? "  (preview scaled to fit cap)" : "";
            string hover = _hoverWorld is { } w ? $"   cursor X {w.X:F1}  Z {w.Y:F1}" : "";
            ImGui.TextDisabled(
                $"{p.Width}×{p.Height}px @ {p.Ppm:F1}px/m   kept={p.Actors.Count}/{p.TotalActorsConsidered}{capped}{hover}");
        }

        // ── Interactive image canvas ─────────────────────────────────────────

        private static void DrawCanvas(float height)
        {
            Vector2 origin = ImGui.GetCursorScreenPos();
            Vector2 size = new(ImGui.GetContentRegionAvail().X, height);
            if (size.X < 4f || size.Y < 4f) return;

            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(origin, origin + size, Col(0.07f, 0.07f, 0.09f, 1f), 4f);

            ImGui.InvisibleButton("##mg_canvas", size,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            bool hovered = ImGui.IsItemHovered();
            var io = ImGui.GetIO();

            _hoverWorld = null;

            if (_preview is not { Valid: true } tex || tex.Width <= 0 || tex.Height <= 0 || _plan is null)
            {
                drawList.AddText(origin + new Vector2(10, 10), Col(0.6f, 0.6f, 0.6f, 1f), "No image");
                return;
            }

            float fit = MathF.Min(size.X / tex.Width, size.Y / tex.Height);
            float dispScale = fit * _zoom;
            Vector2 texSize = new(tex.Width, tex.Height);
            Vector2 dispSize = texSize * dispScale;

            // Zoom to cursor.
            if (hovered && io.MouseWheel != 0f)
            {
                float newZoom = Math.Clamp(_zoom * (1f + io.MouseWheel * 0.12f), 0.1f, 40f);
                float newScale = fit * newZoom;
                Vector2 preTL = origin + (size - dispSize) * 0.5f + _panPx;
                Vector2 pImg = (io.MousePos - preTL) / dispScale;        // image px under cursor
                Vector2 newDisp = texSize * newScale;
                _panPx = io.MousePos - pImg * newScale - origin - (size - newDisp) * 0.5f;
                _zoom = newZoom;
                dispScale = newScale; dispSize = newDisp;
            }

            // Drag to pan.
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                _panPx += io.MouseDelta;
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                ResetView();

            // Keep the image from being dragged completely off-screen. When the
            // image is smaller than the canvas the bounds invert, so centre it.
            const float keep = 40f;
            Vector2 baseTL = origin + (size - dispSize) * 0.5f;
            _panPx.X = ClampSafe(_panPx.X, origin.X + keep - dispSize.X - baseTL.X, origin.X + size.X - keep - baseTL.X);
            _panPx.Y = ClampSafe(_panPx.Y, origin.Y + keep - dispSize.Y - baseTL.Y, origin.Y + size.Y - keep - baseTL.Y);
            Vector2 tl = baseTL + _panPx;
            Vector2 br = tl + dispSize;

            drawList.PushClipRect(origin, origin + size, true);
            drawList.AddImage(tex.Handle, tl, br);

            float ppm = _plan.Ppm, cfgX = _plan.CfgX, cfgY = _plan.CfgY;
            // image px → screen
            Vector2 ImgToScreen(float ix, float iy) => new(tl.X + ix * dispScale, tl.Y + iy * dispScale);
            // world (X,Z) → screen
            Vector2 WorldToScreen(float wx, float wz) => ImgToScreen(cfgX + wx * ppm, cfgY - wz * ppm);

            // Visible world rect (from canvas corners).
            float wxL = (((origin.X - tl.X) / dispScale) - cfgX) / ppm;
            float wxR = (((origin.X + size.X - tl.X) / dispScale) - cfgX) / ppm;
            float wzT = (cfgY - ((origin.Y - tl.Y) / dispScale)) / ppm;
            float wzB = (cfgY - ((origin.Y + size.Y - tl.Y) / dispScale)) / ppm;

            float step = NiceStep(120f / MathF.Max(0.0001f, ppm * dispScale));

            if (_showGrid && step > 0f)
            {
                uint gcol = Col(1f, 1f, 1f, 0.06f);
                float x0 = MathF.Floor(MathF.Min(wxL, wxR) / step) * step;
                float x1 = MathF.Max(wxL, wxR);
                for (float wx = x0; wx <= x1; wx += step)
                {
                    float sx = WorldToScreen(wx, 0).X;
                    drawList.AddLine(new Vector2(sx, origin.Y), new Vector2(sx, origin.Y + size.Y), gcol, 1f);
                }
                float z0 = MathF.Floor(MathF.Min(wzT, wzB) / step) * step;
                float z1 = MathF.Max(wzT, wzB);
                for (float wz = z0; wz <= z1; wz += step)
                {
                    float sy = WorldToScreen(0, wz).Y;
                    drawList.AddLine(new Vector2(origin.X, sy), new Vector2(origin.X + size.X, sy), gcol, 1f);
                }
            }

            drawList.PopClipRect();

            // Scale bar (bottom-left).
            {
                float barPx = step * ppm * dispScale;
                Vector2 b0 = new(origin.X + 12f, origin.Y + size.Y - 16f);
                Vector2 b1 = new(b0.X + barPx, b0.Y);
                uint white = Col(0.9f, 0.92f, 0.95f, 0.9f);
                drawList.AddLine(b0, b1, white, 2f);
                drawList.AddLine(b0, b0 + new Vector2(0, -5), white, 2f);
                drawList.AddLine(b1, b1 + new Vector2(0, -5), white, 2f);
                drawList.AddText(new Vector2(b0.X, b0.Y - 18f), white, $"{step:0.#} m");
            }

            // Hover readout.
            if (hovered)
            {
                Vector2 pImg = (io.MousePos - tl) / dispScale;
                float wx = (pImg.X - cfgX) / ppm;
                float wz = (cfgY - pImg.Y) / ppm;
                _hoverWorld = new Vector2(wx, wz);
                string t = $"X {wx:F1}  Z {wz:F1}";
                Vector2 tp = io.MousePos + new Vector2(14, 8);
                Vector2 ts = ImGui.CalcTextSize(t);
                drawList.AddRectFilled(tp - new Vector2(4, 2), tp + ts + new Vector2(4, 2), Col(0f, 0f, 0f, 0.6f), 3f);
                drawList.AddText(tp, Col(0.9f, 0.95f, 1f, 1f), t);
            }
        }

        // ── World-Y histogram with draggable deck splits ─────────────────────

        private static void DrawHistogramStrip(float height)
        {
            if (_plan is null)
                return;

            EnsureHistogram(_plan);

            // Small toolbar.
            bool custom = _opts.FloorSplitsY is { Count: > 0 };
            ImGui.TextDisabled(custom
                ? $"Decks: custom — {_opts.FloorSplitsY!.Count} split(s) → {_opts.FloorSplitsY!.Count + 1} decks"
                : "Decks: uniform bands (drag below, paste heights, or auto-detect to customise)");
            ImGui.SameLine(0, 14);
            if (ImGui.SmallButton("From doors")) DetectDecksFromDoors();
            ImGui.SameLine();
            if (ImGui.SmallButton("From geometry")) AutoDetectDecks();
            ImGui.SameLine();
            ImGui.BeginDisabled(!custom);
            if (ImGui.SmallButton("Clear")) { _opts.FloorSplitsY = null; MarkDirty(); }
            ImGui.EndDisabled();

            Vector2 origin = ImGui.GetCursorScreenPos();
            Vector2 size = new(ImGui.GetContentRegionAvail().X, MathF.Max(60f, height - ImGui.GetTextLineHeightWithSpacing() - 6f));
            if (size.X < 8f) return;

            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(origin, origin + size, Col(0.10f, 0.10f, 0.13f, 1f), 4f);
            dl.AddRect(origin, origin + size, Col(0.25f, 0.28f, 0.32f, 0.6f), 4f);

            float yMin = _histoYMin, yMax = _histoYMax, span = MathF.Max(0.001f, yMax - yMin);
            float YToX(float y) => origin.X + (y - yMin) / span * size.X;
            float XToY(float x) => yMin + (x - origin.X) / size.X * span;

            // Bars.
            if (_histoMax > 0f && _histo.Length > 0)
            {
                int n = _histo.Length;
                float bw = size.X / n;
                uint barCol = Col(0.45f, 0.55f, 0.62f, 0.9f);
                for (int i = 0; i < n; i++)
                {
                    float h = _histo[i] / _histoMax * (size.Y - 6f);
                    if (h < 0.5f) continue;
                    float x = origin.X + i * bw;
                    dl.AddRectFilled(new Vector2(x, origin.Y + size.Y - h), new Vector2(x + bw + 0.5f, origin.Y + size.Y - 1f), barCol);
                }
            }

            ImGui.InvisibleButton("##mg_histo", size,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            bool hovered = ImGui.IsItemHovered();
            var io = ImGui.GetIO();

            // Begin drag only when grabbing a nearby cut (so a stray click in
            // empty space doesn't silently convert uniform → custom).
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var cur = (_opts.FloorSplitsY is { Count: > 0 } l) ? l : EffectiveCuts();
                int near = NearestCut(cur, io.MousePos.X, YToX, 6f);
                if (near >= 0) { EnsureSplitsInitialized(); _dragSplit = near; }
                else _dragSplit = -1;
            }
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                EnsureSplitsInitialized();
                float y = Math.Clamp(XToY(io.MousePos.X), yMin, yMax);
                _opts.FloorSplitsY!.Add(y);
                _opts.FloorSplitsY!.Sort();
                _dragSplit = _opts.FloorSplitsY!.IndexOf(y);
                MarkDirty();
            }
            if (_dragSplit >= 0 && _opts.FloorSplitsY is { } cuts2 && _dragSplit < cuts2.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                cuts2[_dragSplit] = Math.Clamp(XToY(io.MousePos.X), yMin, yMax);
                MarkDirty();
            }
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && _dragSplit >= 0)
            {
                _opts.FloorSplitsY?.Sort();
                _dragSplit = -1;
            }
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && _opts.FloorSplitsY is { Count: > 0 } rc)
            {
                int near = NearestCut(rc, io.MousePos.X, YToX, 7f);
                if (near >= 0) { rc.RemoveAt(near); if (rc.Count == 0) _opts.FloorSplitsY = null; MarkDirty(); }
            }

            // Draw cut lines.
            var drawCuts = (_opts.FloorSplitsY is { Count: > 0 } lc) ? lc : EffectiveCuts();
            bool live = _opts.FloorSplitsY is { Count: > 0 };
            for (int i = 0; i < drawCuts.Count; i++)
            {
                float x = YToX(drawCuts[i]);
                uint c = live ? Col(0.30f, 0.80f, 0.85f, 0.95f) : Col(0.5f, 0.5f, 0.55f, 0.5f);
                dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), c, live ? 2f : 1f);
                if (live)
                    dl.AddTriangleFilled(new Vector2(x - 5, origin.Y), new Vector2(x + 5, origin.Y), new Vector2(x, origin.Y + 7), c);
            }

            // Axis labels + hover line.
            dl.AddText(new Vector2(origin.X + 3, origin.Y + size.Y - 14), Col(0.6f, 0.6f, 0.65f, 1f), $"{yMin:F0}m");
            string hi = $"{yMax:F0}m";
            dl.AddText(new Vector2(origin.X + size.X - ImGui.CalcTextSize(hi).X - 3, origin.Y + size.Y - 14), Col(0.6f, 0.6f, 0.65f, 1f), hi);
            if (hovered)
            {
                float y = XToY(io.MousePos.X);
                dl.AddLine(new Vector2(io.MousePos.X, origin.Y), new Vector2(io.MousePos.X, origin.Y + size.Y), Col(1f, 1f, 1f, 0.25f), 1f);
                string t = $"{y:F1}m";
                dl.AddText(new Vector2(io.MousePos.X + 6, origin.Y + 3), Col(0.9f, 0.95f, 1f, 1f), t);
            }
        }

        // ── Controls column ──────────────────────────────────────────────────

        private static void DrawControlsColumn()
        {
            DrawDeckSection();
            DrawRenderStyleSection();

            ImGui.SeparatorText("Resolution & Size");
            float ppm = _opts.PixelsPerMeter;
            if (Slider("Pixels / metre", ref ppm, 1f, 16f, "%.1f")) _opts.PixelsPerMeter = ppm;
            HelpTip("Output resolution. Higher = crisper, bigger files. Preview is always capped.");
            float margin = _opts.MarginPx;
            if (Slider("Margin (px)", ref margin, 0f, 128f, "%.0f")) _opts.MarginPx = margin;
            int maxDim = _opts.MaxImageDimensionPx;
            if (SliderI("Max dimension (px)", ref maxDim, 1024, 16000)) _opts.MaxImageDimensionPx = maxDim;

            ImGui.SeparatorText("Filtering");
            float maxExtent = _opts.MaxActorExtentMeters;
            if (Slider("Max actor extent (m)", ref maxExtent, 20f, 400f, "%.0f")) _opts.MaxActorExtentMeters = maxExtent;
            HelpTip("Drop actors larger than this in X or Z — kills skybox / world-bound colliders.");
            float minFeature = _opts.MinFeatureMeters;
            if (Slider("Min feature (m)", ref minFeature, 0f, 3f, "%.2f")) _opts.MinFeatureMeters = minFeature;
            HelpTip("Drop clutter smaller than this. Raise it if the map looks busy.");
            float trim = _opts.BoundsTrimPercent;
            if (Slider("Trim outliers (%)", ref trim, 0f, 5f, "%.1f")) _opts.BoundsTrimPercent = trim;
            HelpTip("Discard the outermost % of actors before framing so far strays\n(open water, terrain skirts) don't shrink the subject to a speck and\ncrush resolution. ~0.5–1 for a ship in open sea. 0 = legacy raw bounds.");

            ImGui.SeparatorText("Surface Classification");
            float wallNy = _opts.WallMaxNormalY;
            if (Slider("Wall max |normal.Y|", ref wallNy, 0f, 1f, "%.2f")) _opts.WallMaxNormalY = wallNy;
            float slab = _opts.SlabMaxThicknessMeters;
            if (Slider("Slab max thickness (m)", ref slab, 0f, 3f, "%.2f")) _opts.SlabMaxThicknessMeters = slab;

            ImGui.SeparatorText("Appearance");
            int alpha = _opts.FloorAlpha;
            if (SliderI("Deck alpha", ref alpha, 0, 255)) _opts.FloorAlpha = (byte)Math.Clamp(alpha, 0, 255);
            var deck = _opts.DeckColor;
            if (Color3("Deck color", ref deck)) _opts.DeckColor = deck;
            var wallLo = _opts.WallLowColor;
            if (Color3("Wall (low)", ref wallLo)) _opts.WallLowColor = wallLo;
            var wallHi = _opts.WallHighColor;
            if (Color3("Wall (high)", ref wallHi)) _opts.WallHighColor = wallHi;
            HelpTip("Walls are height-shaded from the low colour (bottom) to the high colour (top).");

            ImGui.SeparatorText("Output");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##mg_outroot", ref _outputRoot, 260))
                _opts.OutputRoot = _outputRoot;
            ImGui.TextDisabled("Folder (relative to CWD) for output.");

            ImGui.Separator();
            if (ImGui.Button("Reset to Defaults"))
            {
                _opts = new MapGenOptions();
                _outputRoot = _opts.OutputRoot;
                MarkDirty();
            }
        }

        private static void DrawDeckSection()
        {
            ImGui.SeparatorText("Decks / Floors");

            bool custom = _opts.FloorSplitsY is { Count: > 0 };

            if (ImGui.Button(_showDeckPaste ? "Hide paste box" : "Paste deck heights…"))
                _showDeckPaste = !_showDeckPaste;
            ImGui.SameLine();
            if (ImGui.Button("From doors")) DetectDecksFromDoors();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Detect decks from door colliders in the snapshot — their floor\nlevel clusters into deck heights. Most reliable signal on EFT maps.");
            ImGui.SameLine();
            if (ImGui.Button("From geometry")) AutoDetectDecks();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Fallback: detect decks from peaks in the actor height histogram.\nNoisier than doors.");
            ImGui.SameLine();
            ImGui.BeginDisabled(!custom);
            if (ImGui.Button("Clear")) { _opts.FloorSplitsY = null; MarkDirty(); }
            ImGui.EndDisabled();

            if (_showDeckPaste)
            {
                ImGui.TextDisabled("One deck height per line; labels/ranges OK (numbers averaged).");
                ImGui.InputTextMultiline("##mg_deckpaste", ref _deckPaste, 4000, new Vector2(-1f, 90f));
                if (ImGui.Button("Apply heights"))
                {
                    var cuts = MapGenOptions.ParseDeckHeightsToCuts(_deckPaste);
                    if (cuts.Count > 0)
                    {
                        _opts.FloorSplitsY = cuts;
                        MarkDirty();
                        SetStatus($"Parsed {cuts.Count + 1} decks ({cuts.Count} splits).", true);
                    }
                    else SetStatus("No numbers found to parse.", false);
                }
            }

            // Uniform-band controls (only relevant when not using custom splits).
            ImGui.BeginDisabled(custom);
            float band = _opts.FloorBandMeters;
            if (Slider("Uniform band (m)", ref band, 1f, 8f, "%.1f")) _opts.FloorBandMeters = band;
            int maxFloors = _opts.MaxFloors;
            if (SliderI("Max floors", ref maxFloors, 1, 32)) _opts.MaxFloors = maxFloors;
            ImGui.EndDisabled();
            if (custom) ImGui.TextDisabled("(custom deck splits active — drag the histogram)");
        }

        private static readonly string[] _wallStyleNames = { "Filled", "Outline (silhouette)", "Edges (line-art)" };

        private static void DrawRenderStyleSection()
        {
            ImGui.SeparatorText("Render Style");

            int style = (int)_opts.WallStyle;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.Combo("Wall style", ref style, _wallStyleNames, _wallStyleNames.Length))
            { _opts.WallStyle = (MapWallStyle)style; MarkDirty(); }
            HelpTip("Filled = solid height-shaded.\nOutline = outer silhouette only.\nEdges = feature-edge line-art that keeps interior rooms (the clean blueprint look).");

            if (_opts.WallStyle != MapWallStyle.Filled)
            {
                float ow = _opts.OutlineWidthPx;
                if (Slider("Line width (px)", ref ow, 0.5f, 4f, "%.1f")) _opts.OutlineWidthPx = ow;
            }

            if (_opts.WallStyle == MapWallStyle.Edges)
            {
                bool det = _opts.ShowDetailEdges;
                if (ImGui.Checkbox("Detail edges (floors / grids)", ref det)) { _opts.ShowDetailEdges = det; MarkDirty(); }
                HelpTip("Faint lines for floor structure and terrain grids (e.g. the helipad grid).");

                float crease = _opts.EdgeCreaseDegrees;
                if (Slider("Edge crease (°)", ref crease, 5f, 60f, "%.0f")) _opts.EdgeCreaseDegrees = crease;
                HelpTip("Lower = more edges (finer, noisier). Higher = only sharp corners.\nTip: pair Edges with Supersample 2 and a ~1px line for the crispest result.");
            }

            bool shadow = _opts.WallShadow;
            if (ImGui.Checkbox("Wall shadow (AO)", ref shadow)) { _opts.WallShadow = shadow; MarkDirty(); }
            HelpTip("Soft dark blur under walls for depth (filled/outline styles).");

            int ss = _opts.Supersample;
            if (SliderI("Supersample", ref ss, 1, 4)) _opts.Supersample = ss;
            HelpTip("Render at N× then downscale for crisper edges (disk output; preview uses it too).");
        }

        // ── Preview pipeline ─────────────────────────────────────────────────

        private static void EnsurePreviewFresh()
        {
            var snap = SceneCache.Snapshot;
            bool snapChanged = !ReferenceEquals(snap, _planSnapshotRef);
            if (snapChanged) _previewDirty = true;
            if (!_previewDirty) return;

            if (!snapChanged)
            {
                if (!_autoRefresh) return;
                if (Environment.TickCount64 - _lastEditMs < DebounceMs) return;
            }
            RebuildPreview(snap);
        }

        private static void RebuildPreview(SceneSnapshot snap)
        {
            _planSnapshotRef = snap;
            _previewDirty = false;

            var pOpts = _opts.Clone();
            pOpts.MaxImageDimensionPx = Math.Min(_opts.MaxImageDimensionPx, PreviewMaxDim);

            if (!MapImageGenerator.TryPlan(snap, CurrentMapId(snap), pOpts, out var plan, out var err) || plan is null)
            {
                _plan = null;
                if (!string.IsNullOrEmpty(err)) SetStatus(err, false);
                return;
            }
            _plan = plan;
            if ((uint)_selectedLayer >= (uint)plan.Layers.Count) _selectedLayer = 0;
            RenderSelectedLayer();
        }

        private static void RenderSelectedLayer()
        {
            if (_plan is null) return;
            var gl = RadarWindow.GlApi;
            if (gl is null) return;
            if ((uint)_selectedLayer >= (uint)_plan.Layers.Count) _selectedLayer = 0;

            using var bmp = MapImageGenerator.RenderLayerToBitmap(_plan, _selectedLayer, PreviewBg);
            if (bmp is null) return;
            _preview ??= new PreviewTexture(gl);
            _preview.Upload(bmp);
        }

        // ── Histogram helpers ────────────────────────────────────────────────

        private static void EnsureHistogram(MapGenPlan plan)
        {
            if (ReferenceEquals(plan, _histoPlanRef) && _histo.Length > 0) return;
            _histoPlanRef = plan;

            const int Bins = 160;
            var h = new float[Bins];

            // Robust Y range — clip stray actors at extreme Y (a lone collider
            // far below the map was seen at Y≈-1109) that would otherwise crush
            // all real geometry into a couple of bins and break detection.
            var ys = new List<float>(plan.Actors.Count);
            foreach (var a in plan.Actors) ys.Add((a.WorldAabbMin.Y + a.WorldAabbMax.Y) * 0.5f);
            ys.Sort();
            float yMin = Percentile(ys, 0.005f);
            float yMax = Percentile(ys, 0.999f);
            float span = MathF.Max(0.001f, yMax - yMin);

            foreach (var a in plan.Actors)
            {
                var mn = a.WorldAabbMin; var mx = a.WorldAabbMax;
                float sx = MathF.Max(0f, mx.X - mn.X);
                float sz = MathF.Max(0f, mx.Z - mn.Z);
                float w = MathF.Sqrt(sx * sz) + 0.05f;        // footprint emphasises deck slabs
                float cy = (mn.Y + mx.Y) * 0.5f;
                int bi = (int)((cy - yMin) / span * (Bins - 1));
                if (bi < 0) bi = 0; else if (bi >= Bins) bi = Bins - 1;
                h[bi] += w;
            }
            float max = 0f; foreach (var v in h) max = MathF.Max(max, v);
            _histo = h; _histoYMin = yMin; _histoYMax = yMax; _histoMax = max;
        }

        /// <summary>Interior band boundaries of the current plan (uniform mode), ascending.</summary>
        private static List<float> EffectiveCuts()
        {
            var cuts = new List<float>();
            if (_plan is null) return cuts;
            for (int i = 1; i < _plan.Layers.Count; i++)
            {
                var hi = _plan.Layers[i].Hi;
                if (hi.HasValue) cuts.Add(hi.Value);
            }
            cuts.Sort();
            return cuts;
        }

        private static void EnsureSplitsInitialized()
        {
            if (_opts.FloorSplitsY is { Count: > 0 }) return;
            var seed = EffectiveCuts();
            _opts.FloorSplitsY = seed.Count > 0 ? seed : new List<float>();
        }

        private static int NearestCut(List<float>? cuts, float mouseX, Func<float, float> yToX, float tolPx)
        {
            if (cuts is null) return -1;
            int best = -1; float bestD = tolPx;
            for (int i = 0; i < cuts.Count; i++)
            {
                float d = MathF.Abs(yToX(cuts[i]) - mouseX);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static void AutoDetectDecks()
        {
            if (_plan is null) { SetStatus("No snapshot to analyse.", false); return; }
            EnsureHistogram(_plan);
            float span = MathF.Max(0.001f, _histoYMax - _histoYMin);
            var decks = FindLevelsByPeaks(_histo, _histoYMin, span, 0.12f);
            if (decks.Count < 2) { SetStatus("From geometry: fewer than 2 decks found.", false); return; }
            ApplyDeckLevels(decks, "geometry");
        }

        /// <summary>
        /// Detect decks from door colliders in the snapshot — the most reliable
        /// signal on EFT maps (doors sit on the walkable floor, so their bottom
        /// edge clusters tightly at each deck level). Pure PhysX: matches actors
        /// whose GameObject name contains "door", bins their floor height, and
        /// picks histogram peaks. Validated against the door-derived Icebreaker
        /// deck list (9/12 decks within ~0.2 m; misses only the door-less bilge
        /// and bridge).
        /// </summary>
        private static void DetectDecksFromDoors()
        {
            var snap = _plan?.Snapshot ?? SceneCache.Snapshot;
            var ys = new List<float>();
            foreach (var a in snap.Actors)
            {
                var nm = a.Name;
                if (string.IsNullOrEmpty(nm)) continue;
                if (nm.IndexOf("door", StringComparison.OrdinalIgnoreCase) < 0) continue;
                ys.Add(a.WorldAabbMin.Y);     // door bottom ≈ deck floor
            }
            if (ys.Count < 6)
            {
                SetStatus($"From doors: only {ys.Count} door shapes — try 'From geometry'.", false);
                return;
            }
            ys.Sort();

            // Clip extreme outliers, bin door floor heights, then peak-pick.
            float yMin = Percentile(ys, 0.01f), yMax = Percentile(ys, 0.99f);
            float span = MathF.Max(0.5f, yMax - yMin);
            const int Bins = 256;
            var h = new float[Bins];
            foreach (var y in ys)
            {
                int b = (int)((y - yMin) / span * (Bins - 1));
                if (b >= 0 && b < Bins) h[b] += 1f;
            }
            var decks = FindLevelsByPeaks(h, yMin, span, 0.08f);
            if (decks.Count < 2) { SetStatus("From doors: fewer than 2 deck levels found.", false); return; }
            ApplyDeckLevels(decks, $"doors ({ys.Count} shapes)");
        }

        private static void ApplyDeckLevels(List<float> decks, string source)
        {
            decks.Sort();
            var cuts = new List<float>(Math.Max(0, decks.Count - 1));
            for (int k = 1; k < decks.Count; k++) cuts.Add((decks[k - 1] + decks[k]) * 0.5f);
            _opts.FloorSplitsY = cuts;
            MarkDirty();
            SetStatus($"From {source}: {decks.Count} decks → {cuts.Count} splits.", true);
        }

        /// <summary>
        /// Histogram peak picker: 3-tap smooth, keep local maxima above
        /// <paramref name="threshFrac"/> of the peak, enforce ~1.2 m min spacing.
        /// Returns the world-Y of each detected level.
        /// </summary>
        private static List<float> FindLevelsByPeaks(float[] hist, float yMin, float span, float threshFrac)
        {
            int n = hist.Length;
            var levels = new List<float>();
            if (n < 3) return levels;

            var s = new float[n];
            for (int i = 0; i < n; i++)
                s[i] = (hist[Math.Max(0, i - 1)] + hist[i] + hist[Math.Min(n - 1, i + 1)]) / 3f;
            float max = 0f; foreach (var v in s) max = MathF.Max(max, v);
            if (max <= 0f) return levels;

            float thresh = max * threshFrac;
            float minGap = MathF.Max(1f, 1.2f / span * n);
            var peaks = new List<int>();
            for (int i = 1; i < n - 1; i++)
            {
                if (s[i] < thresh) continue;
                if (s[i] >= s[i - 1] && s[i] >= s[i + 1])
                {
                    if (peaks.Count > 0 && i - peaks[^1] < minGap)
                    { if (s[i] > s[peaks[^1]]) peaks[^1] = i; }
                    else peaks.Add(i);
                }
            }
            foreach (var p in peaks) levels.Add(yMin + (p + 0.5f) / n * span);
            return levels;
        }

        /// <summary><paramref name="sorted"/> must be ascending.</summary>
        private static float Percentile(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return 0f;
            int i = Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }

        // ── Actions ──────────────────────────────────────────────────────────

        private static void DoGenerate()
        {
            var snap = SceneCache.Snapshot;
            _opts.OutputRoot = _outputRoot;
            if (MapImageGenerator.Generate(snap, CurrentMapId(snap), _opts, out var dir, out var err))
            {
                _lastOutputDir = dir;
                SetStatus($"Wrote map → {Path.GetFullPath(dir)}", true);
            }
            else SetStatus($"Generate failed: {err}", false);
        }

        private static void OpenFolder()
        {
            string path = !string.IsNullOrEmpty(_lastOutputDir) && Directory.Exists(_lastOutputDir)
                ? _lastOutputDir : _opts.OutputRoot;
            try
            {
                string full = Path.GetFullPath(path);
                Directory.CreateDirectory(full);
                Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus($"Open folder failed: {ex.Message}", false); }
        }

        // ── Small helpers ────────────────────────────────────────────────────

        private static void ResetView() { _zoom = 1f; _panPx = Vector2.Zero; }

        private static string CurrentMapId(SceneSnapshot snap)
        {
            var m = Memory.Game?.MapID;
            if (!string.IsNullOrWhiteSpace(m)) return m!;
            if (!string.IsNullOrWhiteSpace(snap.MapId)) return snap.MapId;
            return "unknown";
        }

        private static void MarkDirty()
        {
            _previewDirty = true;
            _lastEditMs = Environment.TickCount64;
        }

        private static void SetStatus(string msg, bool ok)
        {
            _status = msg;
            _statusMs = Environment.TickCount64;
            _statusCol = ok ? new Vector4(0.40f, 0.85f, 0.40f, 1f) : new Vector4(0.95f, 0.45f, 0.40f, 1f);
        }

        private static bool Slider(string label, ref float v, float lo, float hi, string fmt)
        {
            bool c = ImGui.SliderFloat(label, ref v, lo, hi, fmt);
            if (c) MarkDirty();
            return c;
        }

        private static bool SliderI(string label, ref int v, int lo, int hi)
        {
            bool c = ImGui.SliderInt(label, ref v, lo, hi);
            if (c) MarkDirty();
            return c;
        }

        private static bool Color3(string label, ref Vector3 v)
        {
            bool c = ImGui.ColorEdit3(label, ref v);
            if (c) MarkDirty();
            return c;
        }

        private static void HelpTip(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }

        private static uint Col(float r, float g, float b, float a) => ImGui.GetColorU32(new Vector4(r, g, b, a));

        private static float ClampSafe(float v, float lo, float hi) => lo <= hi ? Math.Clamp(v, lo, hi) : 0f;

        private static float NiceStep(float rough)
        {
            if (rough <= 0f || float.IsNaN(rough) || float.IsInfinity(rough)) return 1f;
            float exp = MathF.Floor(MathF.Log10(rough));
            float baseV = MathF.Pow(10f, exp);
            float f = rough / baseV;
            float nice = f < 1.5f ? 1f : f < 3.5f ? 2f : f < 7.5f ? 5f : 10f;
            return nice * baseV;
        }

        /// <summary>
        /// Wraps a single GL texture that mirrors an <see cref="SKBitmap"/> for
        /// display via <c>ImGui.Image</c>. Re-uploads in place each refresh. Safe
        /// to interleave with SkiaSharp: the radar's render loop resets GL
        /// texture-binding / pixel-store state every frame.
        /// </summary>
        private sealed class PreviewTexture : IDisposable
        {
            private readonly GL _gl;
            private uint _tex;

            public int Width { get; private set; }
            public int Height { get; private set; }
            public nint Handle => (nint)_tex;
            public bool Valid => _tex != 0;

            public PreviewTexture(GL gl) => _gl = gl;

            public unsafe void Upload(SKBitmap bmp)
            {
                if (bmp.ColorType != SKColorType.Rgba8888) return;
                nint pixels = bmp.GetPixels();
                if (pixels == 0) return;

                if (_tex == 0)
                {
                    _tex = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, _tex);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
                }
                else
                {
                    _gl.BindTexture(TextureTarget.Texture2D, _tex);
                }

                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

                bool sizeChanged = bmp.Width != Width || bmp.Height != Height;
                Width = bmp.Width;
                Height = bmp.Height;

                if (sizeChanged)
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                        (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
                else
                    _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                        (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);

                _gl.BindTexture(TextureTarget.Texture2D, 0);
            }

            public void Dispose()
            {
                if (_tex != 0) { _gl.DeleteTexture(_tex); _tex = 0; }
            }
        }
    }
}
