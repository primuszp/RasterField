using System;
using System.Collections.Generic;
using System.IO;

namespace RasterField
{
    /// <summary>Locates the bundled and user-writable palette folders, cross-platform.</summary>
    internal static class PaletteStorage
    {
        /// <summary>
        /// A per-user, writable folder for palettes saved from the in-app editor, so they
        /// survive across runs even when the app itself is installed read-only:
        /// <c>%AppData%/RasterField/palettes</c> on Windows, <c>~/.config/RasterField/palettes</c>
        /// on Linux, <c>~/Library/Application Support/RasterField/palettes</c> on macOS.
        /// </summary>
        public static string UserPaletteDirectory()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create);
            return Path.Combine(baseDir, "RasterField", "palettes");
        }

        /// <summary>
        /// Former names of the bundled palettes (their old, Hungarian file names), mapped to the
        /// current English, content-based ones — so settings and projects saved earlier still find
        /// them. The duplicate grayscale file was dropped in favour of the identical built-in one, and
        /// geo2 (geo1 with only its last colour changed) in favour of geo1.
        /// Display names in the UI are translated through <see cref="L.T"/>.
        /// </summary>
        public static IReadOnlyDictionary<string, string> BundledLegacyNames { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["csapadék"] = "Precipitation (brown-blue)",
                ["felszin1"] = "Terrain (yellow-green)",
                ["felszin2"] = "Terrain (brown-green)",
                ["föld-tenger"] = "Land & Sea (hypsometric)",
                ["föld"] = "Hypsometric (green-brown-white)",
                ["geo1"] = "Relief (white-green)",
                ["geo2"] = "Relief (white-green)", // differed from geo1 only in its last colour
                ["magasság"] = "Elevation (cyan-brown-white)",
                ["mars1"] = "Mars (sand-brown)",
                ["spectrum1"] = "Hue wheel (cyclic)",
                ["spektrum"] = "Rainbow (purple-red)",
                ["szürkeskála"] = "Grayscale",
                ["temp1"] = "Heat (yellow-red-blue)",
                ["zöld-barna"] = "Green-Brown (direct)",
            };

        /// <summary>The palette folder bundled next to the running application, if found.</summary>
        public static string? BundledPaletteDirectory()
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "palette"),
                         Path.Combine(Directory.GetCurrentDirectory(), "palette"),
                         Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "palette")),
                     })
            {
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
