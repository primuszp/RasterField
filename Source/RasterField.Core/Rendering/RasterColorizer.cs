using System;
using RasterField.Rasters;

namespace RasterField.Rendering
{
    /// <summary>How palette entries are applied across the value range.</summary>
    public enum PaletteRenderMode
    {
        /// <summary>Smoothly interpolate between palette entries.</summary>
        Continuous,
        /// <summary>Quantise the normalised value into <see cref="RasterColorizer.ClassCount"/> flat bands.</summary>
        Discrete,
        /// <summary>Pick the nearest palette entry with no interpolation.</summary>
        Nearest,
    }

    /// <summary>
    /// Turns raster sample values into colours: a linear stretch from
    /// <see cref="Minimum"/>..<see cref="Maximum"/> onto [0, 1], an optional gamma curve
    /// and inversion, a <see cref="Palette"/> lookup, and a dedicated colour for no-data.
    /// </summary>
    public sealed class RasterColorizer
    {
        /// <summary>Creates a colorizer for a palette and value range.</summary>
        public RasterColorizer(Palette palette, double minimum, double maximum)
        {
            Palette = palette ?? throw new ArgumentNullException(nameof(palette));
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <summary>The palette to sample.</summary>
        public Palette Palette { get; set; }

        /// <summary>Value mapped to the low end of the palette.</summary>
        public double Minimum { get; set; }

        /// <summary>Value mapped to the high end of the palette.</summary>
        public double Maximum { get; set; }

        /// <summary>Gamma applied to the normalised value (1 = linear, &lt;1 brightens, &gt;1 darkens).</summary>
        public double Gamma { get; set; } = 1.0;

        /// <summary>Reverses the palette direction when <see langword="true"/>.</summary>
        public bool Invert { get; set; }

        /// <summary>Colour used for no-data / NaN samples.</summary>
        public ColorRgba NoDataColor { get; set; } = ColorRgba.Transparent;

        /// <summary>Global opacity applied to every non-no-data colour (0-255).</summary>
        public byte Opacity { get; set; } = 255;

        /// <summary>How palette entries are spread across the range.</summary>
        public PaletteRenderMode Mode { get; set; } = PaletteRenderMode.Continuous;

        /// <summary>Number of classes used by <see cref="PaletteRenderMode.Discrete"/>.</summary>
        public int ClassCount { get; set; } = 8;

        /// <summary>Maps a single raster value to a colour.</summary>
        public ColorRgba Map(double value)
        {
            if (double.IsNaN(value)) return NoDataColor;

            double span = Maximum - Minimum;
            double t = span > 0 ? (value - Minimum) / span : 0.0;
            if (t < 0) t = 0; else if (t > 1) t = 1;

            if (Gamma > 0 && Gamma != 1.0) t = Math.Pow(t, Gamma);
            if (Invert) t = 1.0 - t;

            switch (Mode)
            {
                case PaletteRenderMode.Discrete:
                {
                    int n = Math.Max(1, ClassCount);
                    int cls = (int)(t * n);
                    if (cls >= n) cls = n - 1;
                    t = n > 1 ? cls / (double)(n - 1) : 0.0;
                    var d = Palette.Sample(t);
                    return WithOpacity(d);
                }
                case PaletteRenderMode.Nearest:
                {
                    int idx = (int)(t * 255.0 + 0.5);
                    return WithOpacity(Palette[idx]);
                }
                default:
                    return WithOpacity(Palette.Sample(t));
            }
        }

        /// <summary>Maps a nullable value (as returned by <see cref="Raster.GetValueOrNull"/>).</summary>
        public ColorRgba MapNullable(float? value) => value.HasValue ? Map((double)value.Value) : NoDataColor;

        /// <summary>Builds a 256-entry ARGB lookup table for the current settings (fast path for renderers).</summary>
        public int[] BuildArgbLut(int size = 256)
        {
            if (size < 2) size = 2;
            var lut = new int[size];
            for (int i = 0; i < size; i++)
            {
                double value = Minimum + (Maximum - Minimum) * (i / (double)(size - 1));
                lut[i] = Map(value).ToArgb();
            }
            return lut;
        }

        private ColorRgba WithOpacity(ColorRgba c)
        {
            if (Opacity == 255) return c;
            byte a = (byte)(c.A * Opacity / 255);
            return new ColorRgba(c.R, c.G, c.B, a);
        }

        // ----- factory helpers -----------------------------------------------------------

        /// <summary>Colorizer stretched across the full data range.</summary>
        public static RasterColorizer MinMax(Raster raster, Palette palette)
        {
            var s = raster.Statistics;
            return new RasterColorizer(palette, s.Minimum, s.Maximum);
        }

        /// <summary>Colorizer stretched to <c>mean &#177; sigma&#183;sd</c>, clamped to the data range.</summary>
        public static RasterColorizer Sigma(Raster raster, Palette palette, double sigma = 2.0)
        {
            var (min, max) = raster.Statistics.SigmaRange(sigma);
            return new RasterColorizer(palette, min, max);
        }

        /// <summary>Colorizer stretched to the [<paramref name="lowPercent"/>, <paramref name="highPercent"/>] percentiles.</summary>
        public static RasterColorizer Percentile(Raster raster, Palette palette, double lowPercent = 2.0, double highPercent = 98.0)
        {
            var (min, max) = RasterHistogram.Build(raster).PercentileRange(lowPercent, highPercent);
            return new RasterColorizer(palette, min, max);
        }
    }
}
