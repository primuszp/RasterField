using System;
using System.Collections.Generic;
using System.Threading;

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
            return SampleLineCore(x0, y0, x1, y1, sampleCount,
                (x, y) => BilinearSample(raster, x, y));
        }

        /// <summary>
        /// As <see cref="Sample"/>, but the endpoints and the returned <see cref="ProfileSample.X"/>/<see cref="ProfileSample.Y"/>
        /// are world coordinates (converted through <paramref name="geoReference"/>); the distance
        /// is therefore in the dataset's real-world units.
        /// </summary>
        public static IReadOnlyList<ProfileSample> SampleWorld(
            Raster raster, RasterGeoReference geoReference, double worldX0, double worldY0, double worldX1, double worldY1, int sampleCount)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (!geoReference.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");
            return SampleLineCore(worldX0, worldY0, worldX1, worldY1, sampleCount, (wx, wy) =>
            {
                var (col, row) = geoReference.WorldToPixel(wx, wy);
                return BilinearSample(raster, col, row);
            });
        }

        private static IReadOnlyList<ProfileSample> SampleLineCore(
            double x0, double y0, double x1, double y1, int sampleCount, Func<double, double, float?> sample)
        {
            if (sampleCount < 2) throw new ArgumentOutOfRangeException(nameof(sampleCount), "At least 2 samples are required.");
            double dx = x1 - x0, dy = y1 - y0;
            double totalDistance = Math.Sqrt(dx * dx + dy * dy);
            var result = new List<ProfileSample>(sampleCount);
            for (int i = 0; i < sampleCount; i++)
            {
                double t = i / (double)(sampleCount - 1);
                double x = x0 + dx * t, y = y0 + dy * t;
                result.Add(new ProfileSample(totalDistance * t, x, y, sample(x, y)));
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
            sampler ??= BilinearSample;
            return SamplePolylineWorldCore(geoReference, vertices, spacing,
                (col, row) => sampler(raster, col, row), CancellationToken.None);
        }

        /// <summary>Samples a path using any shared interpolation method, without creating a refined raster.</summary>
        public static IReadOnlyList<ProfileSample> SamplePolylineWorld(
            Interpolation.ISurfaceSampler surface, RasterGeoReference geoReference,
            IReadOnlyList<(double X, double Y)> vertices, double spacing, CancellationToken cancellationToken = default)
        {
            // Validate the surface/transform pairing before evaluating any point.
            _ = new Interpolation.WorldSurfaceSampler(surface, geoReference);
            return SamplePolylineWorldCore(geoReference, vertices, spacing,
                (x, y) => (float?)surface.Sample(x, y).Value, cancellationToken);
        }

        /// <summary>
        /// Samples a multi-vertex world-space path directly from a windowed raster source. Only
        /// the small source tiles touched by the path are read, so profiles over very large
        /// datasets do not require the complete band in memory. Samples are bilinearly
        /// interpolated with the same cell-centre convention as <see cref="BilinearSample"/>.
        /// </summary>
        public static IReadOnlyList<ProfileSample> SamplePolylineWorld(
            IRasterSource source, RasterGeoReference geoReference,
            IReadOnlyList<(double X, double Y)> vertices, double spacing,
            int band = 0, int tileSize = 256, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (band < 0 || band >= source.BandCount) throw new ArgumentOutOfRangeException(nameof(band));
            if (tileSize < 2) throw new ArgumentOutOfRangeException(nameof(tileSize), "A source tile must be at least 2 × 2 cells.");
            var sampler = Interpolation.SurfaceSampler.Bilinear(source, band, strict: false, tileSize: tileSize, cancellationToken: cancellationToken);
            return SamplePolylineWorldCore(geoReference, vertices, spacing, (x, y) => (float?)sampler.Sample(x, y).Value, cancellationToken);
        }

        private static IReadOnlyList<ProfileSample> SamplePolylineWorldCore(
            RasterGeoReference geoReference, IReadOnlyList<(double X, double Y)> vertices,
            double spacing, Func<double, double, float?> sample, CancellationToken cancellationToken)
        {
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (vertices.Count < 2) throw new ArgumentException("A path needs at least two vertices.", nameof(vertices));
            if (!(spacing > 0) || double.IsInfinity(spacing)) throw new ArgumentOutOfRangeException(nameof(spacing));
            if (!geoReference.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");
            foreach (var point in vertices)
                if (double.IsNaN(point.X) || double.IsInfinity(point.X) || double.IsNaN(point.Y) || double.IsInfinity(point.Y))
                    throw new ArgumentException("Path coordinates must be finite.", nameof(vertices));
            var result = new List<ProfileSample>();
            double travelled = 0;

            void Add(double wx, double wy, double distance)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (col, row) = geoReference.WorldToPixel(wx, wy);
                result.Add(new ProfileSample(distance, wx, wy, sample(col, row)));
            }

            Add(vertices[0].X, vertices[0].Y, 0);
            for (int i = 1; i < vertices.Count; i++)
            {
                var (ax, ay) = vertices[i - 1];
                var (bx, by) = vertices[i];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                double requiredSteps = Math.Ceiling(len / spacing);
                if (requiredSteps >= int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(spacing), "The path requires too many samples.");
                int steps = Math.Max(1, (int)requiredSteps);
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
            return (float?)Interpolation.Sampling.Bilinear(new Interpolation.LoadedSampleGrid(raster), col, row, false).Value;
        }

    }
}
