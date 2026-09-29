using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>One point of a cross-section/profile: distance from the start, the sampled location, and the value there (if any).</summary>
    public readonly struct ProfileSample
    {
        /// <summary>Creates a sample with its distance-along-line, location, and sampled value.</summary>
        public ProfileSample(double distance, double x, double y, float? value)
        {
            Distance = distance;
            X = x;
            Y = y;
            Value = value;
        }

        /// <summary>Distance from the start of the profile line, in the same units as the sampled coordinates.</summary>
        public double Distance { get; }

        /// <summary>X coordinate of this sample (cell or world space, matching how it was requested).</summary>
        public double X { get; }

        /// <summary>Y coordinate of this sample.</summary>
        public double Y { get; }

        /// <summary><see langword="null"/> when the point falls outside the raster or on/near a no-data cell.</summary>
        public float? Value { get; }
    }

    /// <summary>
    /// Samples a <see cref="Raster"/> along a straight line — the building block of a
    /// cross-section / elevation-profile tool: drag a line on the raster, get a value curve
    /// along it.
    /// </summary>
    public static class RasterProfiler
    {
        /// <summary>
        /// Samples <paramref name="raster"/> at <paramref name="sampleCount"/> evenly spaced
        /// points along the line from (<paramref name="x0"/>, <paramref name="y0"/>) to
        /// (<paramref name="x1"/>, <paramref name="y1"/>), given in cell (pixel) coordinates.
        /// Each sample is bilinearly interpolated from its four surrounding cells; a sample
        /// whose neighbourhood is entirely no-data (or that falls outside the raster) reports a
        /// <see langword="null"/> value but still appears in the result, so a chart can show the gap.
        /// </summary>
        public static IReadOnlyList<ProfileSample> Sample(Raster raster, double x0, double y0, double x1, double y1, int sampleCount)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (sampleCount < 2) throw new ArgumentOutOfRangeException(nameof(sampleCount), "At least 2 samples are required.");

            double totalDistance = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            var result = new List<ProfileSample>(sampleCount);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = i / (double)(sampleCount - 1);
                double x = x0 + (x1 - x0) * t;
                double y = y0 + (y1 - y0) * t;
                float? value = BilinearSample(raster, x, y);
                result.Add(new ProfileSample(totalDistance * t, x, y, value));
            }
            return result;
        }

        /// <summary>
        /// As <see cref="Sample"/>, but the endpoints and the returned <see cref="ProfileSample.X"/>/<see cref="ProfileSample.Y"/>
        /// are world coordinates (converted through <paramref name="geoReference"/>); the distance
        /// is therefore in the dataset's real-world units.
        /// </summary>
        public static IReadOnlyList<ProfileSample> SampleWorld(
            Raster raster, RasterGeoReference geoReference, double worldX0, double worldY0, double worldX1, double worldY1, int sampleCount)
        {
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (!geoReference.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");
            if (sampleCount < 2) throw new ArgumentOutOfRangeException(nameof(sampleCount));

            double totalDistance = Math.Sqrt((worldX1 - worldX0) * (worldX1 - worldX0) + (worldY1 - worldY0) * (worldY1 - worldY0));
            var result = new List<ProfileSample>(sampleCount);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = i / (double)(sampleCount - 1);
                double wx = worldX0 + (worldX1 - worldX0) * t;
                double wy = worldY0 + (worldY1 - worldY0) * t;
                var (col, row) = geoReference.WorldToPixel(wx, wy);
                float? value = BilinearSample(raster, col, row);
                result.Add(new ProfileSample(totalDistance * t, wx, wy, value));
            }
            return result;
        }

        /// <summary>
        /// Samples along a multi-vertex path given in world coordinates, every
        /// <paramref name="spacing"/> world units (each vertex is always included, so bends are
        /// never cut). <see cref="ProfileSample.Distance"/> is the cumulative distance along the
        /// path. <paramref name="sampler"/> chooses the interpolation (defaults to
        /// <see cref="BilinearSample"/>); it receives pixel coordinates.
        /// </summary>
        public static IReadOnlyList<ProfileSample> SamplePolylineWorld(
            Raster raster, RasterGeoReference geoReference, IReadOnlyList<(double X, double Y)> vertices, double spacing,
            Func<Raster, double, double, float?>? sampler = null)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (vertices.Count < 2) throw new ArgumentException("A path needs at least two vertices.", nameof(vertices));
            if (!(spacing > 0)) throw new ArgumentOutOfRangeException(nameof(spacing), "The sample spacing must be positive.");
            if (!geoReference.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");
            sampler ??= BilinearSample;

            var result = new List<ProfileSample>();
            double travelled = 0;
            void Add(double wx, double wy, double distance)
            {
                var (col, row) = geoReference.WorldToPixel(wx, wy);
                result.Add(new ProfileSample(distance, wx, wy, sampler(raster, col, row)));
            }

            Add(vertices[0].X, vertices[0].Y, 0);
            for (int i = 1; i < vertices.Count; i++)
            {
                var (ax, ay) = vertices[i - 1];
                var (bx, by) = vertices[i];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int steps = Math.Max(1, (int)Math.Ceiling(len / spacing));
                for (int s = 1; s <= steps; s++)
                {
                    double t = s / (double)steps;
                    Add(ax + (bx - ax) * t, ay + (by - ay) * t, travelled + len * t);
                }
                travelled += len;
            }
            return result;
        }

        /// <summary>
        /// Bilinearly interpolated value at pixel coordinate (<paramref name="col"/>,
        /// <paramref name="row"/>), using the same corner-addressed convention as
        /// <see cref="RasterGeoReference"/> (whole numbers are cell corners; a cell's own value
        /// sits at its centre, <c>col+0.5</c>/<c>row+0.5</c>). No-data corners are excluded from
        /// the weighted average rather than poisoning the whole sample; <see langword="null"/>
        /// only when every corner (or the point itself, outside the raster) is unusable.
        /// </summary>
        public static float? BilinearSample(Raster raster, double col, double row)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (col < 0 || row < 0 || col > raster.Width || row > raster.Height) return null;

            // Shift into cell-centre space so a query exactly at a cell's centre resolves to
            // that cell alone, rather than averaging with its neighbour.
            double u = col - 0.5;
            double v = row - 0.5;
            int c0 = (int)Math.Floor(u);
            int r0 = (int)Math.Floor(v);
            int c1 = c0 + 1;
            int r1 = r0 + 1;
            double fx = u - c0;
            double fy = v - r0;

            double weightSum = 0, valueSum = 0;
            AccumulateCorner(raster, c0, r0, (1 - fx) * (1 - fy), ref weightSum, ref valueSum);
            AccumulateCorner(raster, c1, r0, fx * (1 - fy), ref weightSum, ref valueSum);
            AccumulateCorner(raster, c0, r1, (1 - fx) * fy, ref weightSum, ref valueSum);
            AccumulateCorner(raster, c1, r1, fx * fy, ref weightSum, ref valueSum);

            return weightSum > 0 ? (float)(valueSum / weightSum) : (float?)null;
        }

        private static void AccumulateCorner(Raster raster, int c, int r, double weight, ref double weightSum, ref double valueSum)
        {
            if (weight <= 0) return;
            if ((uint)c >= (uint)raster.Width || (uint)r >= (uint)raster.Height) return;
            float v = raster[r, c];
            if (raster.IsNoData(v)) return;
            weightSum += weight;
            valueSum += weight * v;
        }
    }
}
