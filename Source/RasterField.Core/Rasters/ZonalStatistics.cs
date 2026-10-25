using System;
using System.Collections.Generic;
using System.Threading;

namespace RasterField.Rasters
{
    /// <summary>Statistics of the raster cells inside one zone (polygon).</summary>
    public sealed class ZonalResult
    {
        internal ZonalResult(SampleStatistics stats, long noDataCount, double cellArea)
        {
            Count = stats.Count;
            NoDataCount = noDataCount;
            Minimum = Count > 0 ? stats.Minimum : double.NaN;
            Maximum = Count > 0 ? stats.Maximum : double.NaN;
            Sum = stats.Sum;
            Mean = Count > 0 ? stats.Mean : double.NaN;
            StandardDeviation = stats.StandardDeviation;
            Area = (Count + noDataCount) * cellArea;
        }

        /// <summary>Valid cells whose centre lies inside the zone.</summary>
        public long Count { get; }

        /// <summary>No-data cells whose centre lies inside the zone.</summary>
        public long NoDataCount { get; }

        /// <summary>Smallest valid value (NaN when the zone has none).</summary>
        public double Minimum { get; }

        /// <summary>Largest valid value (NaN when the zone has none).</summary>
        public double Maximum { get; }

        /// <summary>Mean of the valid values (NaN when the zone has none).</summary>
        public double Mean { get; }

        /// <summary>Population standard deviation of the valid values.</summary>
        public double StandardDeviation { get; }

        /// <summary>Sum of the valid values (e.g. a volume once multiplied by the cell area).</summary>
        public double Sum { get; }

        /// <summary>Area covered by the zone's cells (valid and no-data), in squared world units.</summary>
        public double Area { get; }
    }

    /// <summary>
    /// Zonal statistics: summarises the raster cells whose <i>centre</i> falls inside a polygon given
    /// in world coordinates (even-odd rule; the polygon is implicitly closed). Works under rotation,
    /// since the test is done in the raster's own pixel space.
    /// </summary>
    public static class ZonalStatistics
    {
        /// <summary>Computes the statistics of <paramref name="raster"/> inside <paramref name="polygon"/>.</summary>
        public static ZonalResult Compute(Raster raster, RasterGeoReference geoReference,
            IReadOnlyList<(double X, double Y)> polygon,
            CancellationToken cancellationToken = default)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            using (var source = new MemoryRasterSource(raster))
                return Compute(source, geoReference, polygon, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Computes zonal statistics directly from a windowed source. The polygon scanlines are
        /// grouped into bounded tiles and every intersected tile is read at most once, so even a
        /// very large raster can be analysed without materialising its complete band.
        /// </summary>
        public static ZonalResult Compute(
            IRasterSource source, RasterGeoReference geoReference,
            IReadOnlyList<(double X, double Y)> polygon, int band = 0, int tileSize = 256,
            CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));
            if (polygon.Count < 3) throw new ArgumentException("A zone needs at least three vertices.", nameof(polygon));
            if (!geoReference.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");
            if (band < 0 || band >= source.BandCount) throw new ArgumentOutOfRangeException(nameof(band));
            if (tileSize < 1) throw new ArgumentOutOfRangeException(nameof(tileSize));

            cancellationToken.ThrowIfCancellationRequested();
            var region = new PolygonCellRegion(geoReference, polygon, source.Width, source.Height);
            var (_, b, c, _, e, f) = geoReference.GeoTransform;
            double cellArea = Math.Abs(b * f - c * e);
            var stats = new SampleStatistics();
            long noData = 0;
            region.VisitTiles(tileSize, cancellationToken, (x, y, width, height, spans) =>
            {
                Raster tile = source.ReadWindow(x, y, width, height, band: band);
                foreach (var span in spans)
                    for (int col = span.From; col <= span.To; col++)
                    {
                        float value = tile[span.Row, col];
                        if (tile.IsNoData(value)) noData++;
                        else stats.Add(value);
                    }
            });
            return new ZonalResult(stats, noData, cellArea);
        }
    }

}
