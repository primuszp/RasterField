using System;
using System.Linq;
using Avalonia;

namespace RasterField
{
    internal static class Program
    {
        /// <summary>
        /// Every <c>.ers</c> path passed on the command line, opened once the main window is
        /// ready — the first as the base dataset, any further ones added as extra layers on top
        /// of it (e.g. <c>RasterField a.ers b.ers c.ers</c>).
        /// </summary>
        public static string[] StartupFiles { get; private set; } = Array.Empty<string>();

        /// <summary>Every <c>.erv</c> path passed on the command line, added as vector layers once the base raster is loaded.</summary>
        public static string[] StartupVectorFiles { get; private set; } = Array.Empty<string>();

        /// <summary>A <c>.rfproj</c> passed on the command line, opened instead of individual files.</summary>
        public static string? StartupProject { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            StartupFiles = args.Where(a => new[] { ".ers", ".tif", ".tiff" }.Any(ext => a.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).ToArray();
            StartupVectorFiles = args.Where(a => new[] { ".erv", ".geojson", ".json", ".csv" }.Any(ext => a.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).ToArray();
            StartupProject = args.FirstOrDefault(a => a.EndsWith(".rfproj", StringComparison.OrdinalIgnoreCase));

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
