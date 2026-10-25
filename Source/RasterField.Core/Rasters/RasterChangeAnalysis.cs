using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RasterField.ErMapper;

namespace RasterField.Rasters
{
    /// <summary>Result of checking whether two raster grids can be compared cell-for-cell.</summary>
    public sealed class RasterGridCompatibility
    {
        private RasterGridCompatibility(bool compatible, string message)
        {
            IsCompatible = compatible;
            Message = message;
        }

        /// <summary>Gets whether both rasters use the same grid and coordinate reference system.</summary>
        public bool IsCompatible { get; }

        /// <summary>Gets a user-facing explanation of the compatibility result.</summary>
        public string Message { get; }

        /// <summary>Checks dimensions, coordinate system and all affine grid coefficients.</summary>
        public static RasterGridCompatibility Check(ErsDocument first, ErsDocument second, double tolerance = 1e-9)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));

            var ai = first.Header.RasterInfo;
            var bi = second.Header.RasterInfo;
            if (ai.NrOfCellsPerLine != bi.NrOfCellsPerLine || ai.NrOfLines != bi.NrOfLines)
                return No($"Grid dimensions differ ({ai.NrOfCellsPerLine}×{ai.NrOfLines} vs. {bi.NrOfCellsPerLine}×{bi.NrOfLines}).");

            int? aEpsg = ProjectionRegistry.GetEpsg(first.Header.CoordinateSpace.Projection, first.Header.CoordinateSpace.Datum);
            int? bEpsg = ProjectionRegistry.GetEpsg(second.Header.CoordinateSpace.Projection, second.Header.CoordinateSpace.Datum);
            if (aEpsg.HasValue && bEpsg.HasValue && aEpsg.Value != bEpsg.Value)
                return No($"Coordinate reference systems differ (EPSG:{aEpsg} vs. EPSG:{bEpsg}).");

            if (aEpsg.HasValue != bEpsg.HasValue)
                return No("Only one raster has a recognised coordinate reference system.");

            if (!aEpsg.HasValue)
            {
                string aw = Normalise(first.CoordinateReferenceWkt);
                string bw = Normalise(second.CoordinateReferenceWkt);
                if (aw != "RAW" || bw != "RAW")
                {
                    if (!string.Equals(aw, bw, StringComparison.Ordinal))
                        return No("Coordinate reference system definitions differ.");
                }

                string ap = Normalise(first.Header.CoordinateSpace.Projection);
                string bp = Normalise(second.Header.CoordinateSpace.Projection);
                string ad = Normalise(first.Header.CoordinateSpace.Datum);
                string bd = Normalise(second.Header.CoordinateSpace.Datum);
                if (!string.Equals(ap, bp, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ad, bd, StringComparison.OrdinalIgnoreCase))
                    return No("Coordinate reference system names differ or cannot be resolved safely.");
            }

            var ag = first.GeoReference.GeoTransform;
            var bg = second.GeoReference.GeoTransform;
            if (!Near(ag.A, bg.A, tolerance) || !Near(ag.B, bg.B, tolerance) ||
                !Near(ag.C, bg.C, tolerance) || !Near(ag.D, bg.D, tolerance) ||
                !Near(ag.E, bg.E, tolerance) || !Near(ag.F, bg.F, tolerance))
                return No("Grid origins, cell sizes or rotations differ; resampling is required.");

            return new RasterGridCompatibility(true, "The grids are aligned and can be compared cell-for-cell.");
        }

        private static RasterGridCompatibility No(string message) => new RasterGridCompatibility(false, message);

        private static string Normalise(string? value) => string.IsNullOrWhiteSpace(value) ? "RAW" : value!.Trim();

        private static bool Near(double a, double b, double tolerance) =>
            Math.Abs(a - b) <= tolerance * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
    }

    /// <summary>Statistics and volume totals of a second-minus-first raster comparison.</summary>
    public class RasterChangeSummary
    {
        internal RasterChangeSummary(SampleStatistics stats, long noDataCount,
            long thresholdCount, double cellArea, double cutVolume, double fillVolume)
        {
            Count = stats.Count;
            NoDataCount = noDataCount;
            Minimum = Count == 0 ? double.NaN : stats.Minimum;
            Maximum = Count == 0 ? double.NaN : stats.Maximum;
            Mean = Count == 0 ? double.NaN : stats.Mean;
            StandardDeviation = stats.StandardDeviation;
            ThresholdCellCount = thresholdCount;
            CellArea = cellArea;
            ThresholdArea = thresholdCount * cellArea;
            CutVolume = cutVolume;
            FillVolume = fillVolume;
            NetVolume = fillVolume - cutVolume;
        }

        /// <summary>Gets the number of valid cells included in the result.</summary>
        public long Count { get; }

        /// <summary>Gets the number of no-data pairs encountered inside the analysed area.</summary>
        public long NoDataCount { get; }

        /// <summary>Gets the minimum elevation change.</summary>
        public double Minimum { get; }

        /// <summary>Gets the maximum elevation change.</summary>
        public double Maximum { get; }

        /// <summary>Gets the arithmetic mean elevation change.</summary>
        public double Mean { get; }

        /// <summary>Gets the population standard deviation of elevation change.</summary>
        public double StandardDeviation { get; }

        /// <summary>Gets the number of cells whose absolute change exceeds the threshold.</summary>
        public long ThresholdCellCount { get; }

        /// <summary>Gets the affine cell area in squared coordinate-system units.</summary>
        public double CellArea { get; }

        /// <summary>Gets the total area whose absolute change exceeds the threshold.</summary>
        public double ThresholdArea { get; }

        /// <summary>Gets the magnitude of negative change volume.</summary>
        public double CutVolume { get; }

        /// <summary>Gets the positive change volume.</summary>
        public double FillVolume { get; }

        /// <summary>Gets fill volume minus cut volume.</summary>
        public double NetVolume { get; }
    }

    /// <summary>A change summary together with the materialised second-minus-first raster.</summary>
    public sealed class RasterChangeResult : RasterChangeSummary
    {
        internal RasterChangeResult(Raster difference, SampleStatistics stats, long noDataCount,
            long thresholdCount, double cellArea, double cutVolume, double fillVolume)
            : base(stats, noDataCount, thresholdCount, cellArea, cutVolume, fillVolume)
        {
            Difference = difference;
        }

        /// <summary>Gets the calculated second-minus-first raster.</summary>
        public Raster Difference { get; }
    }

    /// <summary>Cell-aligned elevation change, threshold-area and cut/fill-volume analysis.</summary>
    public static class RasterChangeAnalysis
    {
        /// <summary>
        /// Computes <c>second - first</c>. When <paramref name="polygon"/> is supplied, cells
        /// outside it are left no-data and do not contribute to statistics or volumes.
        /// Cut and fill are returned as positive magnitudes; net volume is fill minus cut.
        /// </summary>
        public static RasterChangeResult Compute(Raster first, Raster second, RasterGeoReference geoReference,
            double absoluteThreshold = 0, IReadOnlyList<(double X, double Y)>? polygon = null,
            CancellationToken cancellationToken = default)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (first.Width != second.Width || first.Height != second.Height)
                throw new ArgumentException("Raster dimensions must match.", nameof(second));
            if (absoluteThreshold < 0 || double.IsNaN(absoluteThreshold))
                throw new ArgumentOutOfRangeException(nameof(absoluteThreshold));
            if (polygon != null && polygon.Count < 3)
                throw new ArgumentException("A comparison zone needs at least three vertices.", nameof(polygon));
            cancellationToken.ThrowIfCancellationRequested();

            int width = first.Width;
            int height = first.Height;
            var difference = new Raster(width, height, float.NaN);
            for (int i = 0; i < difference.Samples.Length; i++) difference.Samples[i] = float.NaN;

            var region = polygon == null ? null
                : new PolygonCellRegion(geoReference, polygon, first.Width, first.Height);

            var transform = geoReference.GeoTransform;
            double cellArea = Math.Abs(transform.B * transform.F - transform.C * transform.E);
            var total = new Accumulator();
            object gate = new object();

            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken },
                () => new Accumulator(),
                (row, _, local) =>
                {
                    if (region == null)
                    {
                        ProcessRange(row, 0, width - 1, first, second, difference, absoluteThreshold, cellArea, local);
                        return local;
                    }

                    foreach (var span in region.RowSpans(row))
                        ProcessRange(row, span.From, span.To, first, second, difference, absoluteThreshold, cellArea, local);
                    return local;
                },
                local =>
                {
                    lock (gate) total.Merge(local);
                });

            difference.InvalidateStatistics();
            return new RasterChangeResult(difference, total.Statistics, total.NoData, total.ThresholdCount, cellArea, total.CutVolume, total.FillVolume);
        }

        /// <summary>
        /// Computes change statistics and cut/fill volumes directly from two aligned windowed
        /// sources. Only matching bounded tiles are held in memory and no full difference raster
        /// is created, making this suitable for very large datasets.
        /// </summary>
        public static RasterChangeSummary ComputeSummary(
            IRasterSource first, IRasterSource second, RasterGeoReference geoReference,
            double absoluteThreshold = 0, IReadOnlyList<(double X, double Y)>? polygon = null,
            int firstBand = 0, int secondBand = 0, int tileSize = 256,
            CancellationToken cancellationToken = default)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (first.Width != second.Width || first.Height != second.Height)
                throw new ArgumentException("Raster dimensions must match.", nameof(second));
            if (firstBand < 0 || firstBand >= first.BandCount) throw new ArgumentOutOfRangeException(nameof(firstBand));
            if (secondBand < 0 || secondBand >= second.BandCount) throw new ArgumentOutOfRangeException(nameof(secondBand));
            if (absoluteThreshold < 0 || double.IsNaN(absoluteThreshold))
                throw new ArgumentOutOfRangeException(nameof(absoluteThreshold));
            if (polygon != null && polygon.Count < 3)
                throw new ArgumentException("A comparison zone needs at least three vertices.", nameof(polygon));
            if (tileSize < 1) throw new ArgumentOutOfRangeException(nameof(tileSize));

            var region = polygon == null ? null
                : new PolygonCellRegion(geoReference, polygon, first.Width, first.Height);

            var transform = geoReference.GeoTransform;
            double cellArea = Math.Abs(transform.B * transform.F - transform.C * transform.E);
            var total = new Accumulator();
            cancellationToken.ThrowIfCancellationRequested();

            if (region == null)
            {
                // For a whole-grid comparison, full-width horizontal strips are much faster for
                // BIL sources than square tiles: every source row stays one contiguous read. Cap
                // each strip near one million cells so the two input windows remain bounded.
                const int targetStripCells = 1_048_576;
                int stripHeight = Math.Max(1, Math.Min(tileSize,
                    Math.Max(1, targetStripCells / Math.Max(1, first.Width))));
                for (int originY = 0; originY < first.Height; originY += stripHeight)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int height = Math.Min(stripHeight, first.Height - originY);
                    Raster a = first.ReadWindow(0, originY, first.Width, height, band: firstBand);
                    Raster b = second.ReadWindow(0, originY, first.Width, height, band: secondBand);
                    for (int row = 0; row < height; row++)
                        ProcessRange(row, 0, first.Width - 1, a, b, null,
                            absoluteThreshold, cellArea, total);
                }
            }
            else
            {
                region.VisitTiles(tileSize, cancellationToken, (x, y, width, height, spans) =>
                {
                    Raster a = first.ReadWindow(x, y, width, height, band: firstBand);
                    Raster b = second.ReadWindow(x, y, width, height, band: secondBand);
                    foreach (var span in spans)
                        ProcessRange(span.Row, span.From, span.To, a, b, null, absoluteThreshold, cellArea, total);
                });
            }

            return new RasterChangeSummary(total.Statistics, total.NoData, total.ThresholdCount, cellArea, total.CutVolume,
                total.FillVolume);
        }

        private static void ProcessRange(int row, int from, int to, Raster first, Raster second,
            Raster? output, double threshold, double cellArea, Accumulator acc)
        {
            int offset = row * first.Width;
            float[] a = first.Samples, b = second.Samples;
            for (int col = from; col <= to; col++)
            {
                int index = offset + col;
                float av = a[index], bv = b[index];
                if (first.IsNoData(av) || second.IsNoData(bv))
                {
                    acc.NoData++;
                    continue;
                }

                double delta = (double)bv - av;
                if (output != null) output.Samples[index] = (float)delta;
                acc.Add(delta, threshold, cellArea);
            }
        }

        private sealed class Accumulator
        {
            public SampleStatistics Statistics { get; } = new SampleStatistics();
            public long NoData;
            public long ThresholdCount;
            public double CutVolume;
            public double FillVolume;

            public void Add(double value, double threshold, double cellArea)
            {
                Statistics.Add(value);
                if (Math.Abs(value) > threshold) ThresholdCount++;
                if (value < 0) CutVolume += -value * cellArea;
                else FillVolume += value * cellArea;
            }

            public void Merge(Accumulator other)
            {
                Statistics.Merge(other.Statistics);
                NoData += other.NoData;
                ThresholdCount += other.ThresholdCount;
                CutVolume += other.CutVolume;
                FillVolume += other.FillVolume;
            }
        }
    }
}
