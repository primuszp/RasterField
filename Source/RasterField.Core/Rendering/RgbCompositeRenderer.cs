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
        /// <summary>Rows at or above which independent scanlines are rendered concurrently.</summary>
        private const int ParallelRowThreshold = 64;

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
            float[] red = r.Samples;
            float[] green = g.Samples;
            float[] blue = b.Samples;
            byte[] pixels = image.Pixels;
            int width = r.Width;

            bool redHasSentinel = !double.IsNaN(r.NoDataValue);
            bool greenHasSentinel = !double.IsNaN(g.NoDataValue);
            bool blueHasSentinel = !double.IsNaN(b.NoDataValue);
            float redSentinel = (float)r.NoDataValue;
            float greenSentinel = (float)g.NoDataValue;
            float blueSentinel = (float)b.NoDataValue;

            void RenderRow(int y)
            {
                int sample = y * width;
                int target = sample * RasterImage.BytesPerPixel;
                int end = sample + width;
                for (; sample < end; sample++, target += RasterImage.BytesPerPixel)
                {
                    float rv = red[sample];
                    float gv = green[sample];
                    float bv = blue[sample];
                    bool redMissing = float.IsNaN(rv) || (redHasSentinel && rv == redSentinel);
                    bool greenMissing = float.IsNaN(gv) || (greenHasSentinel && gv == greenSentinel);
                    bool blueMissing = float.IsNaN(bv) || (blueHasSentinel && bv == blueSentinel);
                    if (redMissing && greenMissing && blueMissing)
                    {
                        // RGB was zero-initialised with the image; only alpha needs stating.
                        pixels[target + 3] = 0;
                        continue;
                    }

                    // RasterImage is BGRA in memory. Writing the backing buffer directly avoids
                    // an index calculation and ColorRgba construction per sample.
                    pixels[target] = Stretch(bv, bLo, bHi);
                    pixels[target + 1] = Stretch(gv, gLo, gHi);
                    pixels[target + 2] = Stretch(rv, rLo, rHi);
                    pixels[target + 3] = 255;
                }
            }

            if (r.Height >= ParallelRowThreshold)
                System.Threading.Tasks.Parallel.For(0, r.Height, RenderRow);
            else
                for (int y = 0; y < r.Height; y++) RenderRow(y);

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
