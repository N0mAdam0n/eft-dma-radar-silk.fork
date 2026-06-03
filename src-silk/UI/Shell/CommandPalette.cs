// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Numerics;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Shell
{
    /// <summary>
    /// Keyboard-only command palette (Ctrl+K). Lists every registered hotkey
    /// action (and a few synthetic "open panel" entries) and lets the user
    /// fuzzy-search by name and invoke with Enter. This is the power-user
    /// fallback to the sidebar / hotkey flows.
    /// </summary>
    internal static class CommandPalette
    {
        public static bool IsOpen { get; private set; }

        private static string _query = string.Empty;
        private static int _selected;
        private static bool _focusInputNextFrame;

        private sealed record Command(string Title, string Subtitle, Action Run);

        private static readonly Command[] _builtins =
        [
            new(Chinese.T("Open Settings"),      Chinese.T("Panel"),  static () => Panels.SettingsPanel.IsOpen = true),
            new(Chinese.T("Open Hotkeys"),       Chinese.T("Panel"),  static () => Panels.HotkeyManagerPanel.IsOpen = true),
            new(Chinese.T("Open Loot Filters"),  Chinese.T("Panel"),  static () => Panels.LootFiltersPanel.IsOpen = true),
            new(Chinese.T("Open Killfeed"),      Chinese.T("Panel"),  static () => Panels.KillfeedPanel.IsOpen = true),
            new(Chinese.T("Open Player History"),Chinese.T("Panel"),  static () => Panels.PlayerHistoryPanel.IsOpen = true),
            new(Chinese.T("Open Watchlist"),     Chinese.T("Panel"),  static () => Panels.PlayerWatchlistPanel.IsOpen = true),
            new(Chinese.T("Open Quest Planner"), Chinese.T("Panel"),  static () => Panels.QuestPlannerPanel.IsOpen = true),
            new(Chinese.T("Open Hideout"),       Chinese.T("Panel"),  static () => Panels.HideoutPanel.IsOpen = true),
            new(Chinese.T("打开透视设置"),      Chinese.T("Panel"),  static () => Panels.SettingsPanel.OpenEspSettings()),
            new(Chinese.T("Toggle Sidebar"),     Chinese.T("Layout"), static () => Sidebar.ToggleVisibility()),
            new(Chinese.T("Toggle Side Dock"),   Chinese.T("Layout"), static () => { var c = SilkProgram.Config; c.DockSidePanels = !c.DockSidePanels; c.MarkDirty(); }),
            new(Chinese.T("Reset Side Panels"),  Chinese.T("Layout"), static () => RightDock.RequestResnap()),
            new(Chinese.T("Show Welcome Tour"),  Chinese.T("Help"),   static () => FirstRunTour.Open()),
        ];

        // Full command list (builtins + hotkey actions) — rebuilt on Open() so any
        // hotkey changes between palette opens are picked up. Reused across frames
        // while the palette is open.
        private static readonly List<Command> _allCommands = new(32);

        // Filtered matches for the current _query. Rebuilt only when _query changes;
        // otherwise reused frame-to-frame so Draw() does no allocation while idle.
        private static readonly List<Command> _matches = new(32);
        private static readonly List<(Command cmd, int score)> _scored = new(32);
        private static string? _lastFilteredQuery;

        public static void Open()
        {
            IsOpen = true;
            _query = string.Empty;
            _selected = 0;
            _focusInputNextFrame = true;
            RebuildAllCommands();
            _lastFilteredQuery = null; // force first GetMatches() to populate
        }

        public static void Close()
        {
            IsOpen = false;
            _query = string.Empty;
            _selected = 0;
        }

        private static void RebuildAllCommands()
        {
            _allCommands.Clear();
            _allCommands.AddRange(_builtins);
            foreach (var def in Misc.Input.HotkeyManager.AvailableActions)
            {
                var local = def;
                _allCommands.Add(new Command(local.DisplayName, local.Category,
                    () => local.Handler(new Misc.Input.InputManager.KeyInputEventArgs(0, true))));
            }
        }

        /// <summary>
        /// Call once per frame. Detects Ctrl+K via ImGui input so it works
        /// whenever the main window has focus, independent of <see cref="Misc.Input.InputManager"/>.
        /// </summary>
        public static void Update()
        {
            var io = ImGui.GetIO();
            if (!IsOpen && io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.K, false))
                Open();
            else if (IsOpen && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                Close();
        }

        public static void Draw()
        {
            if (!IsOpen)
                return;

            var io = ImGui.GetIO();
            var viewport = ImGui.GetMainViewport();
            var center = viewport.GetCenter();
            float scale = SilkProgram.Config.UIScale;
            var size = new Vector2(560f * scale, 440f * scale);
            var pos = center - size * 0.5f;

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.96f);

            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                           | ImGuiWindowFlags.NoResize
                                           | ImGuiWindowFlags.NoMove
                                           | ImGuiWindowFlags.NoCollapse
                                           | ImGuiWindowFlags.NoSavedSettings;

            if (ImGui.Begin("##commandpalette", flags))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.20f, 0.85f, 1.00f, 1.0f));
                ImGui.TextUnformatted(Chinese.T("Command Palette"));
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.TextDisabled("  " + Chinese.T("(Ctrl+K · Esc to close · ↑/↓ to navigate · Enter to run)"));
                ImGui.Separator();

                if (_focusInputNextFrame)
                {
                    ImGui.SetKeyboardFocusHere();
                    _focusInputNextFrame = false;
                }
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##q", Chinese.T("Type to filter…"), ref _query, 128);

                var matches = GetMatches(_query);

                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true))
                    _selected = Math.Min(_selected + 1, Math.Max(0, matches.Count - 1));
                if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true))
                    _selected = Math.Max(_selected - 1, 0);
                bool runNow = ImGui.IsKeyPressed(ImGuiKey.Enter, false)
                              || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false);

                ImGui.BeginChild("##cmd-list", new Vector2(0, 0), ImGuiChildFlags.Borders);
                for (int i = 0; i < matches.Count; i++)
                {
                    var (title, subtitle, run) = matches[i];
                    bool isSel = i == _selected;
                    if (ImGui.Selectable($"{title}##cmd{i}", isSel, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        _selected = i;
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            runNow = true;
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled($"  {subtitle}");
                    if (isSel)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndChild();

                if (runNow && _selected >= 0 && _selected < matches.Count)
                {
                    try { matches[_selected].Run(); }
                    catch (Exception ex) { Log.WriteLine($"[CommandPalette] '{matches[_selected].Title}' error: {ex.Message}"); }
                    SilkProgram.Config.MarkDirty();
                    Close();
                }
            }
            ImGui.End();
        }

        private static List<Command> GetMatches(string query)
        {
            // Reuse last result while the query hasn't changed — eliminates per-frame
            // allocation + sort while the user is just navigating the list.
            if (ReferenceEquals(_lastFilteredQuery, query) || _lastFilteredQuery == query)
                return _matches;

            _lastFilteredQuery = query;
            _matches.Clear();

            if (string.IsNullOrWhiteSpace(query))
            {
                _matches.AddRange(_allCommands);
                return _matches;
            }

            var q = query.Trim();
            _scored.Clear();
            foreach (var cmd in _allCommands)
            {
                int s = Score(cmd, q);
                if (s > 0)
                    _scored.Add((cmd, s));
            }
            _scored.Sort(static (a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < _scored.Count; i++)
                _matches.Add(_scored[i].cmd);
            return _matches;
        }

        private static int Score(Command cmd, string query)
        {
            int score = 0;
            if (cmd.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 100;
            if (cmd.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 30;

            // Subsequence match (very small fuzzy bonus).
            int qi = 0;
            foreach (var ch in cmd.Title)
            {
                if (qi < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qi]))
                    qi++;
            }
            if (qi == query.Length) score += 10;
            return score;
        }
    }
}
