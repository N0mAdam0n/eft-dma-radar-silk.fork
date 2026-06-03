// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc
{
    /// <summary>
    /// Enumerates physical displays on the local machine via Win32 EnumDisplayMonitors.
    /// Used to position the ESP window on the correct monitor.
    /// </summary>
    internal sealed class MonitorInfo
    {
        public int Index { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }

        public string DisplayName =>
            IsPrimary
                ? $"Monitor {Index + 1} (Primary) - {Width}x{Height}"
                : $"Monitor {Index + 1} - {Width}x{Height}";

        #region Win32

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITORINFOF_PRIMARY = 0x00000001u;

        #endregion

        /// <summary>Returns all connected monitors ordered by enumeration index.</summary>
        public static List<MonitorInfo> GetAllMonitors()
        {
            var monitors = new List<MonitorInfo>();
            int index = 0;

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                    {
                        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            var r = mi.rcMonitor;
                            monitors.Add(new MonitorInfo
                            {
                                Index = index++,
                                Width = r.Right - r.Left,
                                Height = r.Bottom - r.Top,
                                Left = r.Left,
                                Top = r.Top,
                                IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                            });
                        }
                        return true;
                    }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[MonitorInfo] Enumerate error: {ex.Message}");
            }

            if (monitors.Count == 0)
            {
                monitors.Add(new MonitorInfo
                {
                    Index = 0,
                    Width = GetSystemMetrics(0),  // SM_CXSCREEN
                    Height = GetSystemMetrics(1), // SM_CYSCREEN
                    Left = 0,
                    Top = 0,
                    IsPrimary = true,
                });
            }

            return monitors;
        }

        /// <summary>Returns the monitor at <paramref name="index"/>, falling back to primary.</summary>
        public static MonitorInfo GetMonitor(int index)
        {
            var all = GetAllMonitors();
            if (index >= 0 && index < all.Count)
                return all[index];
            return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
        }
    }
}
