using System;
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
