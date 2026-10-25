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
            var sampler = new WindowedBilinearSampler(source, band, tileSize, cancellationToken);
            return SamplePolylineWorldCore(geoReference, vertices, spacing, sampler.Sample, cancellationToken);
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
            return SampleBilinear(new LoadedGrid(raster), col, row);
        }

        private interface ISampleGrid
        {
            int Width { get; }
            int Height { get; }
            float? Read(int col, int row);
        }

        private readonly struct LoadedGrid : ISampleGrid
        {
            private readonly Raster _raster;
            public LoadedGrid(Raster raster) => _raster = raster;
            public int Width => _raster.Width;
            public int Height => _raster.Height;
            public float? Read(int col, int row) => _raster.GetValueOrNull(row, col);
        }

        // Generic accessor avoids allocating a delegate/boxing per sample in terrain rendering.
        private static float? SampleBilinear<T>(T grid, double col, double row) where T : ISampleGrid
        {
            if (!(col >= 0 && row >= 0 && col <= grid.Width && row <= grid.Height)) return null;
            double u = col - 0.5, v = row - 0.5;
            int c0 = (int)Math.Floor(u), r0 = (int)Math.Floor(v);
            double fx = u - c0, fy = v - r0;
            double weightSum = 0, valueSum = 0;
            AccumulateCorner(grid, c0, r0, (1 - fx) * (1 - fy), ref weightSum, ref valueSum);
            AccumulateCorner(grid, c0 + 1, r0, fx * (1 - fy), ref weightSum, ref valueSum);
            AccumulateCorner(grid, c0, r0 + 1, (1 - fx) * fy, ref weightSum, ref valueSum);
            AccumulateCorner(grid, c0 + 1, r0 + 1, fx * fy, ref weightSum, ref valueSum);
            return weightSum > 0 ? (float)(valueSum / weightSum) : (float?)null;
        }

        private static void AccumulateCorner<T>(T grid, int col, int row, double weight,
            ref double weightSum, ref double valueSum) where T : ISampleGrid
        {
            if (weight <= 0 || (uint)col >= (uint)grid.Width || (uint)row >= (uint)grid.Height) return;
            float? value = grid.Read(col, row);
            if (!value.HasValue) return;
            weightSum += weight;
            valueSum += weight * value.Value;
        }

        /// <summary>
        /// Small bounded LRU cache around <see cref="IRasterSource.ReadWindow"/>. A sequential
        /// profile normally touches each tile once; keeping a few recent neighbours also avoids
        /// repeated reads where bilinear interpolation crosses a tile edge or the path bends.
        /// </summary>
        private sealed class WindowedBilinearSampler : ISampleGrid
        {
            private const int MaxCachedTiles = 16;
            private readonly IRasterSource _source;
            private readonly int _band;
            private readonly int _tileSize;
            private readonly CancellationToken _cancellationToken;
            private readonly Dictionary<long, CachedTile> _tiles = new Dictionary<long, CachedTile>();
            private readonly LinkedList<long> _recent = new LinkedList<long>();

            public WindowedBilinearSampler(IRasterSource source, int band, int tileSize, CancellationToken cancellationToken)
            {
                _source = source;
                _band = band;
                _tileSize = tileSize;
                _cancellationToken = cancellationToken;
            }

            public int Width => _source.Width;
            public int Height => _source.Height;
            public float? Sample(double col, double row) => SampleBilinear(this, col, row);

            public float? Read(int col, int row)
            {
                Raster tile = GetTile(col / _tileSize, row / _tileSize);
                return tile.GetValueOrNull(row % _tileSize, col % _tileSize);
            }

            private Raster GetTile(int tileX, int tileY)
            {
                long key = ((long)tileY << 32) | (uint)tileX;
                if (_tiles.TryGetValue(key, out CachedTile? cached))
                {
                    _recent.Remove(cached.Node);
                    _recent.AddFirst(cached.Node);
                    return cached.Raster;
                }

                _cancellationToken.ThrowIfCancellationRequested();
                int x = tileX * _tileSize;
                int y = tileY * _tileSize;
                int width = Math.Min(_tileSize, _source.Width - x);
                int height = Math.Min(_tileSize, _source.Height - y);
                Raster raster = _source.ReadWindow(x, y, width, height, band: _band);
                var node = _recent.AddFirst(key);
                _tiles.Add(key, new CachedTile(raster, node));

                if (_tiles.Count > MaxCachedTiles)
                {
                    LinkedListNode<long>? oldest = _recent.Last;
                    if (oldest != null)
                    {
                        _recent.RemoveLast();
                        _tiles.Remove(oldest.Value);
                    }
                }
                return raster;
            }

            private sealed class CachedTile
            {
                public CachedTile(Raster raster, LinkedListNode<long> node)
                {
                    Raster = raster;
                    Node = node;
                }

                public Raster Raster { get; }
                public LinkedListNode<long> Node { get; }
            }
        }
    }
}
