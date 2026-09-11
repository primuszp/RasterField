using System;
using RasterField.Rasters;

namespace RasterField.Rendering
{
    /// <summary>
    /// Renders three raster bands directly as a true-colour RGB composite — no palette; one cell
    /// becomes one pixel's (R, G, B) triple. The counterpart of <see cref="RasterImageRenderer"/>
    /// for genuinely multi-band (not single-value-mapped) imagery.
    /// </summary>
    public static class RgbCompositeRenderer
    {
        /// <summary>
        /// Renders <paramref name="r"/>/<paramref name="g"/>/<paramref name="b"/> (same
        /// dimensions required) as an RGB image. When <paramref name="autoStretch"/> is
        /// <see langword="true"/> (the default), each band is independently stretched from its
        /// own <paramref name="lowPercentile"/>&#8211;<paramref name="highPercentile"/> range into
        /// 0-255 (falling back to the band's plain min/max, then to a unit range, if the
        /// percentile range collapses) — appropriate for raw sensor/analysis values in an
        /// arbitrary range. Pass <see langword="false"/> when the bands already hold display-ready
        /// 0-255 values (e.g. an 8-bit true-colour dataset) to use them as-is. A cell that is
        /// no-data in <i>every</i> one of the three bands renders fully transparent — the common
        /// "shared background fill value" convention (e.g. (0,0,0) as a satellite RGB browse
        /// image's fill colour), chosen over "no-data in any one band" specifically so a pixel
        /// that is genuinely, say, pure black in just its red channel isn't mistaken for missing
        /// data and blanked out entirely.
        /// </summary>
        public static RasterImage Render(
            Raster r, Raster g, Raster b,
            bool autoStretch = true, double lowPercentile = 2.0, double highPercentile = 98.0)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (r.Width != g.Width || r.Width != b.Width || r.Height != g.Height || r.Height != b.Height)
                throw new ArgumentException("All three bands must have the same dimensions.");

            var (rLo, rHi) = StretchRange(r, autoStretch, lowPercentile, highPercentile);
            var (gLo, gHi) = StretchRange(g, autoStretch, lowPercentile, highPercentile);
            var (bLo, bHi) = StretchRange(b, autoStretch, lowPercentile, highPercentile);

            var image = new RasterImage(r.Width, r.Height);
            for (int y = 0; y < r.Height; y++)
            {
                for (int x = 0; x < r.Width; x++)
                {
                    float rv = r[y, x], gv = g[y, x], bv = b[y, x];
                    if (r.IsNoData(rv) && g.IsNoData(gv) && b.IsNoData(bv))
                    {
                        image.SetPixel(x, y, ColorRgba.Transparent);
                        continue;
                    }

                    image.SetPixel(x, y, new ColorRgba(Stretch(rv, rLo, rHi), Stretch(gv, gLo, gHi), Stretch(bv, bLo, bHi)));
                }
            }
            return image;
        }

        private static (double Lo, double Hi) StretchRange(Raster band, bool autoStretch, double lowPercentile, double highPercentile)
        {
            if (!autoStretch) return (0, 255);

            var (lo, hi) = RasterHistogram.Build(band).PercentileRange(lowPercentile, highPercentile);
            if (hi - lo < 1e-9) { lo = band.Statistics.Minimum; hi = band.Statistics.Maximum; }
            if (hi - lo < 1e-9) hi = lo + 1;
            return (lo, hi);
        }

        private static byte Stretch(float v, double lo, double hi)
        {
            double t = (v - lo) / (hi - lo);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return (byte)Math.Round(t * 255.0);
        }
    }
}
