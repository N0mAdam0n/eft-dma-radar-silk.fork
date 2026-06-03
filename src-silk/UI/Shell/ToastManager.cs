// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Numerics;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Shell
{
    /// <summary>
    /// Lightweight transient notification system. Toasts appear in the
    /// bottom-right corner (above the status bar), stack vertically, fade
    /// in/out, and never steal input. Designed for short, non-blocking
    /// messages — e.g. "✓ Config saved", "Preset: PvP", "Waiting for raid".
    /// </summary>
    internal static class ToastManager
    {
        public enum Severity
        {
            Info,
            Success,
            Warn,
            Error,
        }

        private sealed class Toast
        {
            public string Message = string.Empty;
            public Severity Severity;
            public float TtlSeconds;
            public float Age;
        }

        private const float DefaultTtl = 3.0f;
        private const float FadeInSeconds = 0.15f;
        private const float FadeOutSeconds = 0.4f;
        private const int MaxToasts = 5;

        private static readonly List<Toast> _toasts = new();
        private static readonly Lock _gate = new();

        public static void Info(string message, float? ttl = null)
            => Push(message, Severity.Info, ttl ?? DefaultTtl);

        public static void Success(string message, float? ttl = null)
            => Push(message, Severity.Success, ttl ?? DefaultTtl);

        public static void Warn(string message, float? ttl = null)
            => Push(message, Severity.Warn, ttl ?? (DefaultTtl + 1f));

        public static void Error(string message, float? ttl = null)
            => Push(message, Severity.Error, ttl ?? (DefaultTtl + 2f));

        private static void Push(string message, Severity severity, float ttl)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            lock (_gate)
            {
                // Coalesce identical back-to-back toasts so spammy callers (per-frame
                // status pings) don't flood the corner.
                if (_toasts.Count > 0)
                {
                    var last = _toasts[^1];
                    if (last.Message == message && last.Severity == severity)
                    {
                        last.Age = 0f;
                        last.TtlSeconds = ttl;
                        return;
                    }
                }

                _toasts.Add(new Toast { Message = message, Severity = severity, TtlSeconds = ttl });
                while (_toasts.Count > MaxToasts)
                    _toasts.RemoveAt(0);
            }
        }

        /// <summary>Draws and ages toasts. Call once per frame.</summary>
        public static void Draw()
        {
            float dt = ImGui.GetIO().DeltaTime;
            var viewport = ImGui.GetMainViewport();
            float scale = SilkProgram.Config.UIScale;

            float margin = 12f * scale;
            float toastW = 320f * scale;
            float toastPadX = 12f * scale;
            float toastPadY = 8f * scale;
            float gap = 6f * scale;

            // Anchor above the status bar (or its collapsed handle), and to the
            // left of the right dock when it's reserved.
            float bottomReserved = Sidebar.StatusBarHeight + margin;
            float rightReserved = margin;
            float anchorX = viewport.Pos.X + viewport.Size.X - rightReserved - toastW;
            float anchorY = viewport.Pos.Y + viewport.Size.Y - bottomReserved;

            Toast[] snapshot;
            lock (_gate)
            {
                snapshot = _toasts.ToArray();
            }

            float yCursor = anchorY;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                var t = snapshot[i];
                t.Age += dt;

                float remaining = t.TtlSeconds - t.Age;
                float alpha = 1f;
                if (t.Age < FadeInSeconds)
                    alpha = t.Age / FadeInSeconds;
                else if (remaining < FadeOutSeconds)
                    alpha = Math.Max(0f, remaining / FadeOutSeconds);

                if (remaining <= 0f)
                    continue;

                // Measure to compute height.
                // Single-line measure is fine — toasts are short status pings.
                var textSize = ImGui.CalcTextSize(t.Message);
                float toastH = textSize.Y + toastPadY * 2f;
                yCursor -= toastH + gap;

                var pos = new Vector2(anchorX, yCursor);
                var size = new Vector2(toastW, toastH);

                Vector4 accent = t.Severity switch
                {
                    Severity.Success => new Vector4(0.30f, 0.80f, 0.50f, 1f),
                    Severity.Warn    => new Vector4(0.95f, 0.70f, 0.30f, 1f),
                    Severity.Error   => new Vector4(0.95f, 0.40f, 0.40f, 1f),
                    _                => new Vector4(0.28f, 0.78f, 0.85f, 1f),
                };

                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(size, ImGuiCond.Always);
                ImGui.SetNextWindowBgAlpha(0.92f * alpha);

                var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
                            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing |
                            ImGuiWindowFlags.NoNav;

                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(toastPadX, toastPadY));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.10f, 0.11f, 0.13f, 1f));
                ImGui.PushStyleColor(ImGuiCol.Border, accent with { W = accent.W * alpha });

                if (ImGui.Begin($"##toast_{i}_{t.GetHashCode()}", flags))
                {
                    // Left accent bar
                    var drawList = ImGui.GetWindowDrawList();
                    var p0 = ImGui.GetWindowPos();
                    drawList.AddRectFilled(
                        p0,
                        new Vector2(p0.X + 3f * scale, p0.Y + toastH),
                        ImGui.GetColorU32(accent with { W = accent.W * alpha }));

                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, alpha));
                    ImGui.TextWrapped(t.Message);
                    ImGui.PopStyleColor();
                }
                ImGui.End();

                ImGui.PopStyleColor(2);
                ImGui.PopStyleVar(2);
            }

            // Reap expired toasts.
            lock (_gate)
            {
                _toasts.RemoveAll(static x => x.Age >= x.TtlSeconds);
            }
        }
    }
}
