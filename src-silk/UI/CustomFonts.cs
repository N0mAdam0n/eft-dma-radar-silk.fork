// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.IO;

namespace eft_dma_radar.Silk.UI
{
    /// <summary>
    /// Loads embedded Neo Sans Std font resources for SkiaSharp rendering.
    /// Also discovers system CJK fonts (e.g. Microsoft YaHei) so that Chinese text in the
    /// pure-Skia ESP overlay (F2 hidden hint, corpse labels, etc.) renders without garbled
    /// characters or partial glyphs. Mirrors the merge strategy used for ImGui.
    /// </summary>
    internal static class CustomFonts
    {
        private const string FontResourceName = "eft_dma_radar.Silk.NeoSansStdRegular.otf";

        public static SKTypeface Regular { get; }

        /// <summary>
        /// Optional system CJK typeface (YaHei etc.). Null if no common Chinese font found on the machine.
        /// Used by EspPaints to create CJK-capable SKFont instances for localized hint text.
        /// </summary>
        public static SKTypeface? Cjk { get; private set; }

        static CustomFonts()
        {
            Regular = LoadFont(FontResourceName);

            var cjkPath = FindSystemChineseFontPath();
            if (cjkPath != null)
            {
                try
                {
                    Cjk = SKTypeface.FromFile(cjkPath);
                    Log.WriteLine($"[CustomFonts] Loaded system CJK font for ESP: {Path.GetFileName(cjkPath)}");
                }
                catch (Exception ex)
                {
                    Log.WriteLine($"[CustomFonts] Failed to load CJK font {cjkPath}: {ex.Message}");
                    Cjk = null;
                }
            }
            else
            {
                Log.WriteLine("[CustomFonts] No system Chinese font found (msyh.ttc etc.). ESP Chinese text (F2 hint, corpses) may render garbled or with tofu glyphs. Install Microsoft YaHei.");
            }
        }

        /// <summary>
        /// Returns the raw embedded font file bytes.
        /// Used by ImGui contexts that need to load the font from memory.
        /// </summary>
        internal static byte[]? GetEmbeddedFontData()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FontResourceName);
                if (stream is null)
                    return null;

                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                return data;
            }
            catch
            {
                return null;
            }
        }

        private static SKTypeface LoadFont(string resourceName)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded font resource '{resourceName}' not found.");
            return SKTypeface.FromStream(stream);
        }

        /// <summary>
        /// Tries to find a common Windows system CJK font (for simplified/traditional Chinese).
        /// Shared helper so both ImGui (merge) and Skia/ESP (dedicated typeface) use the same discovery.
        /// </summary>
        internal static string? FindSystemChineseFontPath()
        {
            string[] candidates = { "msyh.ttc", "msyhbd.ttc", "simsun.ttc", "simhei.ttf", "simkai.ttf" };
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var name in candidates)
            {
                string p = Path.Combine(fontsDir, name);
                if (File.Exists(p))
                    return p;
            }
            return null;
        }
    }
}
