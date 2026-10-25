using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;

namespace RasterField
{
    internal static class Program
    {
        /// <summary>Raster formats the app opens (lower-case, with the dot).</summary>
        public static readonly IReadOnlyCollection<string> RasterExtensions = new[] { ".ers", ".tif", ".tiff" };

        /// <summary>Vector formats the app adds as layers.</summary>
        public static readonly IReadOnlyCollection<string> VectorExtensions = new[] { ".erv", ".geojson", ".json", ".csv", ".txt" };

        /// <summary>
        /// Files passed on the command line, opened once the main window is ready (see
        /// <see cref="MainWindow.OpenPathsAsync"/>): a project, or rasters — the first as the base
        /// layer, the rest on top — and vector files, e.g. <c>RasterField a.ers b.ers roads.erv</c>.
        /// </summary>
        public static IReadOnlyList<string> StartupPaths { get; private set; } = Array.Empty<string>();

        [STAThread]
        public static void Main(string[] args)
        {
            StartupPaths = args.Where(a => !a.StartsWith('-') && File.Exists(a)).ToArray();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The application menu is built by App, localised, instead of Avalonia's English defaults.
                .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
                .WithInterFont()
                .LogToTrace();
    }
}
