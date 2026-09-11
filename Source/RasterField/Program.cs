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

        [STAThread]
        public static void Main(string[] args)
        {
            StartupFiles = args.Where(a => a.EndsWith(".ers", StringComparison.OrdinalIgnoreCase)).ToArray();
            StartupVectorFiles = args.Where(a => a.EndsWith(".erv", StringComparison.OrdinalIgnoreCase)).ToArray();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
