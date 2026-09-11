using System;
using System.Collections.Generic;
using System.Linq;

namespace RasterField.Rendering
{
    /// <summary>
    /// A ready-made set of gradient palettes so the library is useful without any external
    /// <c>.pal</c> files. Names roughly follow common GIS / DigiTerra palette naming.
    /// </summary>
    public static class BuiltInPalettes
    {
        private static ColorRgba C(string hex) => ColorRgba.ParseHex(hex);

        /// <summary>Black to white.</summary>
        public static Palette Grayscale { get; } = Palette.FromStops("Grayscale",
            (0.0, C("#000000")), (1.0, C("#FFFFFF")));

        /// <summary>White to black.</summary>
        public static Palette GrayscaleInverted { get; } = Palette.FromStops("Grayscale (inverted)",
            (0.0, C("#FFFFFF")), (1.0, C("#000000")));

        /// <summary>Classic rainbow: blue-cyan-green-yellow-red.</summary>
        public static Palette Spectrum { get; } = Palette.FromStops("Spectrum",
            (0.00, C("#000080")), (0.25, C("#0080FF")), (0.50, C("#00C000")),
            (0.75, C("#FFD000")), (1.00, C("#C00000")));

        /// <summary>Perceptually-uniform style ramp (viridis-like): purple-blue-teal-green-yellow.</summary>
        public static Palette Viridis { get; } = Palette.FromStops("Viridis",
            (0.00, C("#440154")), (0.25, C("#3B528B")), (0.50, C("#21908C")),
            (0.75, C("#5DC863")), (1.00, C("#FDE725")));

        /// <summary>Hypsometric land tint: green lowland to brown upland to white peaks.</summary>
        public static Palette Elevation { get; } = Palette.FromStops("Elevation",
            (0.00, C("#1A6837")), (0.30, C("#7BB661")), (0.55, C("#E8D9A0")),
            (0.78, C("#A9772F")), (0.92, C("#7A5230")), (1.00, C("#FFFFFF")));

        /// <summary>Bathymetry + land: deep blue sea through coastline to green/brown land.</summary>
        public static Palette LandSea { get; } = Palette.FromStops("Land &amp; Sea",
            (0.00, C("#08306B")), (0.35, C("#4292C6")), (0.49, C("#C6DBEF")),
            (0.50, C("#1A6837")), (0.72, C("#E8D9A0")), (0.90, C("#8C6D31")), (1.00, C("#FFFFFF")));

        /// <summary>Diverging blue-white-red, good for anomalies around a mid value.</summary>
        public static Palette BlueWhiteRed { get; } = Palette.FromStops("Blue-White-Red",
            (0.0, C("#2166AC")), (0.5, C("#F7F7F7")), (1.0, C("#B2182B")));

        /// <summary>Temperature ramp: cold blue to warm red.</summary>
        public static Palette Temperature { get; } = Palette.FromStops("Temperature",
            (0.00, C("#313695")), (0.25, C("#74ADD1")), (0.45, C("#E0F3F8")),
            (0.55, C("#FEE090")), (0.75, C("#F46D43")), (1.00, C("#A50026")));

        /// <summary>Precipitation ramp: dry tan to wet deep blue.</summary>
        public static Palette Precipitation { get; } = Palette.FromStops("Precipitation",
            (0.00, C("#FFFFD9")), (0.30, C("#C7E9B4")), (0.55, C("#41B6C4")),
            (0.78, C("#225EA8")), (1.00, C("#0C2C84")));

        /// <summary>Green to brown ramp (vegetation / soil).</summary>
        public static Palette GreenBrown { get; } = Palette.FromStops("Green-Brown",
            (0.0, C("#1A9641")), (0.5, C("#FFFFBF")), (1.0, C("#8C510A")));

        /// <summary>The palette used when none is specified.</summary>
        public static Palette Default => Spectrum;

        /// <summary>All built-in palettes, keyed by name (case-insensitive).</summary>
        public static IReadOnlyDictionary<string, Palette> All { get; } =
            new[]
            {
                Grayscale, GrayscaleInverted, Spectrum, Viridis, Elevation, LandSea,
                BlueWhiteRed, Temperature, Precipitation, GreenBrown,
            }.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>Looks up a built-in palette by name, or returns <see cref="Default"/>.</summary>
        public static Palette Get(string name) =>
            All.TryGetValue(name ?? string.Empty, out var p) ? p : Default;
    }
}
