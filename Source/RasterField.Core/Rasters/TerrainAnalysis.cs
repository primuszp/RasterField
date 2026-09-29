using System;
using System.Threading.Tasks;

namespace RasterField.Rasters
{
    /// <summary>Units for <see cref="TerrainAnalysis.Slope"/>.</summary>
    public enum SlopeUnits
    {
        /// <summary>0&#8211;90&#176;, flat to vertical.</summary>
        Degrees,
        /// <summary>Rise/run &#215; 100 (unbounded above, 100 = 45&#176;).</summary>
        Percent,
    }

    /// <summary>Which curvature <see cref="TerrainAnalysis.Curvature"/> reports.</summary>
    public enum CurvatureType
    {
        /// <summary>Overall surface curvature (profile + plan combined) — positive on a dome/ridge, negative in a bowl/valley, zero on a plane.</summary>
        General,
        /// <summary>Curvature measured along the direction of steepest slope — governs how flow accelerates/decelerates down the slope.</summary>
        Profile,
        /// <summary>Curvature measured across the direction of steepest slope (along a contour) — governs whether flow converges or diverges.</summary>
        Plan,
    }

    /// <summary>
    /// Terrain derivatives from an elevation raster: slope, aspect and hillshade, using Horn's
    /// 3&#215;3 method for the gradient (Horn, B.K.P., "Hill Shading and the Reflectance Map",
    /// Proceedings of the IEEE, 69(1), 1981 — the default algorithm behind
    /// <c>gdaldem slope/aspect/hillshade</c> and Esri's Slope/Aspect/Hillshade tools) and the
    /// standard Lambertian reflectance model for hillshade.
    /// </summary>
    /// <remarks>
    /// Edge cells replicate their nearest interior neighbour (the usual convention) rather than
    /// wrapping or shrinking the output — the result is always the same size as the input. A 3&#215;3
    /// window touching a no-data cell propagates no-data to the output cell.
    /// </remarks>
    public static class TerrainAnalysis
    {
        private const int ParallelRowThreshold = 64;

        /// <summary>Computes slope (steepness), in <paramref name="units"/>.</summary>
        public static Raster Slope(Raster elevation, double cellSizeX, double cellSizeY, SlopeUnits units = SlopeUnits.Degrees, double zFactor = 1.0)
        {
            return Compute(elevation, cellSizeX, cellSizeY, (dzdx, dzdy) =>
            {
                dzdx *= zFactor; dzdy *= zFactor;
                double riseRun = Math.Sqrt(dzdx * dzdx + dzdy * dzdy);
                return units == SlopeUnits.Percent ? riseRun * 100.0 : Math.Atan(riseRun) * 180.0 / Math.PI;
            });
        }

        /// <summary>
        /// Computes aspect — compass direction of steepest descent, in degrees (0&#176; = north,
        /// 90&#176; = east, clockwise). Flat cells (zero gradient) report -1.
        /// </summary>
        public static Raster Aspect(Raster elevation, double cellSizeX, double cellSizeY)
        {
            return Compute(elevation, cellSizeX, cellSizeY, (dzdx, dzdy) =>
            {
                if (dzdx == 0 && dzdy == 0) return -1.0;
                double aspect = Math.Atan2(dzdy, -dzdx) * 180.0 / Math.PI;
                // Convert from mathematical (CCW from east) to compass (CW from north) bearing.
                double compass = 90.0 - aspect;
                if (compass < 0) compass += 360.0;
                if (compass >= 360) compass -= 360.0;
                return compass;
            });
        }

        /// <summary>
        /// Computes a hillshade (0-255 illumination) as if lit by a sun at
        /// <paramref name="azimuthDegrees"/> (compass bearing, default 315 = NW) and
        /// <paramref name="altitudeDegrees"/> above the horizon (default 45&#176;).
        /// </summary>
        public static Raster Hillshade(Raster elevation, double cellSizeX, double cellSizeY,
            double azimuthDegrees = 315.0, double altitudeDegrees = 45.0, double zFactor = 1.0)
        {
            double zenithRad = (90.0 - altitudeDegrees) * Math.PI / 180.0;
            double azimuthRad = azimuthDegrees * Math.PI / 180.0;
            double cosZenith = Math.Cos(zenithRad), sinZenith = Math.Sin(zenithRad);

            return Compute(elevation, cellSizeX, cellSizeY, (dzdx, dzdy) =>
            {
                dzdx *= zFactor; dzdy *= zFactor;
                double slopeRad = Math.Atan(Math.Sqrt(dzdx * dzdx + dzdy * dzdy));
                double aspectRad = (dzdx == 0 && dzdy == 0) ? 0.0 : Math.Atan2(dzdy, -dzdx);

                double shade = cosZenith * Math.Cos(slopeRad) + sinZenith * Math.Sin(slopeRad) * Math.Cos(azimuthRad - aspectRad);
                if (shade < 0) shade = 0;
                return shade * 255.0;
            });
        }

        /// <summary>
        /// Computes surface curvature using the quadratic-surface method of Zevenbergen, L.W. &amp;
        /// Thorne, C.R., "Quantitative Analysis of Land Surface Topography", Earth Surface
        /// Processes and Landforms 12(1), 1987 — the standard behind Esri's Curvature tool
        /// (though this returns raw units, not &#215;100). A positive value means the surface is
        /// convex (dome/ridge-like) there; negative means concave (bowl/valley-like); zero means
        /// planar. <see cref="CurvatureType.Profile"/>/<see cref="CurvatureType.Plan"/> are
        /// direction-dependent quantities that become undefined at a perfectly flat gradient (no
        /// downslope direction left to measure along/across); there, this reports the average of
        /// the directional formula over every possible approach direction, which has the clean
        /// closed form <c>-(D+E)</c> — exactly half of <see cref="CurvatureType.General"/> — and
        /// happens to also be the exact (not just averaged) value at an isotropic extremum, such
        /// as the apex of a rotationally symmetric hill.
        /// </summary>
        public static Raster Curvature(Raster elevation, double cellSizeX, double cellSizeY, CurvatureType type = CurvatureType.General)
        {
            if (elevation == null) throw new ArgumentNullException(nameof(elevation));
            if (cellSizeX <= 0 || cellSizeY <= 0) throw new ArgumentOutOfRangeException(nameof(cellSizeX), "Cell sizes must be positive.");

            int w = elevation.Width, h = elevation.Height;
            var output = new Raster(w, h, double.IsNaN(elevation.NoDataValue) ? float.NaN : elevation.NoDataValue);

            double cx2 = cellSizeX * cellSizeX, cy2 = cellSizeY * cellSizeY;

            void ComputeRow(int r)
            {
                for (int c = 0; c < w; c++)
                {
                    if (!TryGetWindow(elevation, c, r, out Window3x3 z))
                    {
                        output.SetValueFast(r, c, float.NaN);
                        continue;
                    }

                    // z[0..8] row-major 3x3 (Z1..Z9 in Zevenbergen & Thorne's own numbering).
                    double d = ((z.Z3 + z.Z5) / 2.0 - z.Z4) / cx2;
                    double e = ((z.Z1 + z.Z7) / 2.0 - z.Z4) / cy2;
                    double f = (-z.Z0 + z.Z2 + z.Z6 - z.Z8) / (4.0 * cellSizeX * cellSizeY);
                    double g = (z.Z5 - z.Z3) / (2.0 * cellSizeX);
                    double hh = (z.Z1 - z.Z7) / (2.0 * cellSizeY);

                    double general = -2.0 * (d + e);
                    double value;
                    double gh2 = g * g + hh * hh;

                    if (type == CurvatureType.General)
                    {
                        value = general;
                    }
                    else if (gh2 < 1e-12)
                    {
                        value = general / 2.0; // directional average over all approach angles (see remarks)
                    }
                    else if (type == CurvatureType.Profile)
                    {
                        value = -2.0 * (d * g * g + e * hh * hh + f * g * hh) / gh2;
                    }
                    else // Plan
                    {
                        value = -2.0 * (d * hh * hh + e * g * g - f * g * hh) / gh2;
                    }

                    output.SetValueFast(r, c, (float)value);
                }
            }

            if (h >= ParallelRowThreshold)
                Parallel.For(0, h, ComputeRow);
            else
                for (int r = 0; r < h; r++) ComputeRow(r);

            output.InvalidateStatistics();
            return output;
        }

        private static Raster Compute(Raster elevation, double cellSizeX, double cellSizeY, Func<double, double, double> derive)
        {
            if (elevation == null) throw new ArgumentNullException(nameof(elevation));
            if (cellSizeX <= 0 || cellSizeY <= 0) throw new ArgumentOutOfRangeException(nameof(cellSizeX), "Cell sizes must be positive.");

            int w = elevation.Width, h = elevation.Height;
            var output = new Raster(w, h, double.IsNaN(elevation.NoDataValue) ? float.NaN : elevation.NoDataValue);

            void ComputeRow(int r)
            {
                for (int c = 0; c < w; c++)
                {
                    if (!TryGetWindow(elevation, c, r, out Window3x3 z))
                    {
                        output.SetValueFast(r, c, float.NaN);
                        continue;
                    }

                    // Horn's method: z[0..8] = a..i, row-major 3x3 (a b c / d e f / g h i)
                    double dzdx = ((z.Z2 + 2 * z.Z5 + z.Z8) - (z.Z0 + 2 * z.Z3 + z.Z6)) / (8 * cellSizeX);
                    double dzdy = ((z.Z6 + 2 * z.Z7 + z.Z8) - (z.Z0 + 2 * z.Z1 + z.Z2)) / (8 * cellSizeY);
                    output.SetValueFast(r, c, (float)derive(dzdx, dzdy));
                }
            }

            if (h >= ParallelRowThreshold)
                Parallel.For(0, h, ComputeRow);
            else
                for (int r = 0; r < h; r++) ComputeRow(r);

            output.InvalidateStatistics();
            return output;
        }

        /// <summary>Gathers the 3&#215;3 neighbourhood around (col,row), replicating the edge; <see langword="false"/> if any cell is no-data.</summary>
        private static bool TryGetWindow(Raster raster, int col, int row, out Window3x3 z)
        {
            int width = raster.Width;
            int top = Clamp(row - 1, raster.Height) * width;
            int middle = row * width;
            int bottom = Clamp(row + 1, raster.Height) * width;
            int left = Clamp(col - 1, width);
            int right = Clamp(col + 1, width);
            float[] samples = raster.Samples;

            float z0 = samples[top + left], z1 = samples[top + col], z2 = samples[top + right];
            float z3 = samples[middle + left], z4 = samples[middle + col], z5 = samples[middle + right];
            float z6 = samples[bottom + left], z7 = samples[bottom + col], z8 = samples[bottom + right];

            if (raster.IsNoData(z0) || raster.IsNoData(z1) || raster.IsNoData(z2) ||
                raster.IsNoData(z3) || raster.IsNoData(z4) || raster.IsNoData(z5) ||
                raster.IsNoData(z6) || raster.IsNoData(z7) || raster.IsNoData(z8))
            {
                z = default;
                return false;
            }

            z = new Window3x3(z0, z1, z2, z3, z4, z5, z6, z7, z8);
            return true;
        }

        /// <summary>
        /// Stack-only neighbourhood value. The earlier double[9] representation allocated one
        /// managed object per output cell, which dominated both GC traffic and terrain-analysis
        /// time on large rasters.
        /// </summary>
        private readonly struct Window3x3
        {
            public Window3x3(
                double z0, double z1, double z2,
                double z3, double z4, double z5,
                double z6, double z7, double z8)
            {
                Z0 = z0; Z1 = z1; Z2 = z2;
                Z3 = z3; Z4 = z4; Z5 = z5;
                Z6 = z6; Z7 = z7; Z8 = z8;
            }

            public double Z0 { get; }
            public double Z1 { get; }
            public double Z2 { get; }
            public double Z3 { get; }
            public double Z4 { get; }
            public double Z5 { get; }
            public double Z6 { get; }
            public double Z7 { get; }
            public double Z8 { get; }
        }

        private static int Clamp(int v, int length) => v < 0 ? 0 : v >= length ? length - 1 : v;
    }
}
