using System;
using RasterField.Rasters;

namespace RasterField.Rendering
{
    /// <summary>
    /// Advanced terrain shading beyond a simple single-light hillshade or a palette-mapped colour
    /// ramp: a multi-directional hillshade (avoiding the harsh, feature-hiding black shadows a
    /// single light source produces — in the spirit of Mark, D.M., "Multidirectional
    /// Oblique-Weighted Shaded-Relief Image of the Island of Hawaii", U.S. Geological Survey Open-File
    /// Report 92-422, 1992) optionally finished with an aerial-perspective tint (higher terrain
    /// rendered paler and cooler, as if seen through atmospheric haze — a long-standing
    /// cartographic depth cue; see e.g. Patterson, T. &amp; Jenny, B., "The Development and
    /// Rationale of Cross-blended Hypsometric Tints", Cartographic Perspectives 69, 2011),
    /// evoking the analytical relief style associated with Eduard Imhof and the Swiss federal
    /// topographic office (Swisstopo). This does not reproduce Swisstopo's own technique exactly
    /// — theirs has always involved a great deal of hand cartographic finishing, which no formula
    /// fully captures — but the same two ingredients, combined, give a recognisably similar
    /// result: soft, non-directional relief with a sense of atmospheric depth.
    /// </summary>
    public static class ReliefShader
    {
        private static readonly double[] DefaultAzimuths = { 225, 270, 315, 360 };

        /// <summary>
        /// Averages a standard <see cref="TerrainAnalysis.Hillshade"/> computed from each of
        /// <paramref name="azimuths"/> (default: 225&#176;/270&#176;/315&#176;/360&#176;, sweeping the
        /// classic north-west quadrant) at the same <paramref name="altitudeDegrees"/> — a simple,
        /// correct-by-construction multi-directional relief that avoids any single direction's
        /// harsh cast shadows while staying cheap to compute and easy to reason about.
        /// </summary>
        public static Raster MultidirectionalHillshade(
            Raster elevation, double cellSizeX, double cellSizeY,
            double altitudeDegrees = 45.0, double zFactor = 1.0, double[]? azimuths = null)
        {
            if (elevation == null) throw new ArgumentNullException(nameof(elevation));
            azimuths ??= DefaultAzimuths;
            if (azimuths.Length == 0) throw new ArgumentException("At least one azimuth is required.", nameof(azimuths));

            int w = elevation.Width, h = elevation.Height;
            var sum = new double[w * h];
            var count = new int[w * h];

            foreach (double az in azimuths)
            {
                Raster shade = TerrainAnalysis.Hillshade(elevation, cellSizeX, cellSizeY, az, altitudeDegrees, zFactor);
                for (int i = 0; i < sum.Length; i++)
                {
                    float v = shade.Samples[i];
                    if (shade.IsNoData(v)) continue;
                    sum[i] += v;
                    count[i]++;
                }
            }

            var output = new Raster(w, h, double.IsNaN(elevation.NoDataValue) ? float.NaN : elevation.NoDataValue);
            for (int i = 0; i < sum.Length; i++)
                output.Samples[i] = count[i] > 0 ? (float)(sum[i] / count[i]) : float.NaN;

            output.InvalidateStatistics();
            return output;
        }

        /// <summary>
        /// Renders a Swiss-style ("Imhof-esque") analytical relief image directly as colour — not
        /// through a value-mapped palette: a <see cref="MultidirectionalHillshade"/> base, tinted
        /// progressively paler and cooler toward <paramref name="hazeColor"/> at higher elevation
        /// to suggest atmospheric depth. A no-data cell (in the elevation or, at the edge, in the
        /// derived shade) renders fully transparent.
        /// </summary>
        /// <param name="elevation">The elevation raster to shade.</param>
        /// <param name="cellSizeX">Cell width, in the same units as elevation (e.g. metres).</param>
        /// <param name="cellSizeY">Cell height, in the same units as elevation.</param>
        /// <param name="altitudeDegrees">Light altitude above the horizon, shared by every azimuth in the multi-directional blend.</param>
        /// <param name="zFactor">Vertical exaggeration applied before shading (1 = none).</param>
        /// <param name="hazeStrength">0 = no aerial perspective (a plain grayscale multidirectional hillshade); 1 = full blend to <paramref name="hazeColor"/> at the highest cell.</param>
        /// <param name="hazeColor">The pale, cool tint blended in at high elevation (default a soft blue-white, as if seen through atmospheric haze).</param>
        public static RasterImage RenderSwissStyle(
            Raster elevation, double cellSizeX, double cellSizeY,
            double altitudeDegrees = 45.0, double zFactor = 1.0,
            double hazeStrength = 0.35, ColorRgba? hazeColor = null)
        {
            if (elevation == null) throw new ArgumentNullException(nameof(elevation));
            if (hazeStrength < 0 || hazeStrength > 1) throw new ArgumentOutOfRangeException(nameof(hazeStrength));

            ColorRgba haze = hazeColor ?? new ColorRgba(225, 235, 240);
            Raster shade = MultidirectionalHillshade(elevation, cellSizeX, cellSizeY, altitudeDegrees, zFactor);

            var stats = elevation.Statistics;
            double range = stats.Maximum - stats.Minimum;

            var image = new RasterImage(elevation.Width, elevation.Height);
            for (int y = 0; y < elevation.Height; y++)
            {
                for (int x = 0; x < elevation.Width; x++)
                {
                    float ez = elevation[y, x];
                    float sv = shade[y, x];
                    if (elevation.IsNoData(ez) || shade.IsNoData(sv))
                    {
                        image.SetPixel(x, y, ColorRgba.Transparent);
                        continue;
                    }

                    double t = range > 1e-9 ? Clamp01((ez - stats.Minimum) / range) : 0.0;
                    double blend = t * hazeStrength;

                    double gray = Clamp255(sv);
                    byte r = (byte)Clamp255(gray + (haze.R - gray) * blend);
                    byte g = (byte)Clamp255(gray + (haze.G - gray) * blend);
                    byte b = (byte)Clamp255(gray + (haze.B - gray) * blend);

                    image.SetPixel(x, y, new ColorRgba(r, g, b));
                }
            }
            return image;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static double Clamp255(double v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
