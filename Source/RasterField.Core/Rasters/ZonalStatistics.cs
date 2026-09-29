using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>Statistics of the raster cells inside one zone (polygon).</summary>
    public sealed class ZonalResult
    {
        internal ZonalResult(long count, long noDataCount, double min, double max, double sum, double sumSquares, double cellArea)
        {
            Count = count;
            NoDataCount = noDataCount;
            Minimum = count > 0 ? min : double.NaN;
            Maximum = count > 0 ? max : double.NaN;
            Sum = sum;
            Mean = count > 0 ? sum / count : double.NaN;
            StandardDeviation = count > 0 ? Math.Sqrt(Math.Max(0, sumSquares / count - Mean * Mean)) : double.NaN;
            Area = (count + noDataCount) * cellArea;
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
        public static ZonalResult Compute(Raster raster, RasterGeoReference geoReference, IReadOnlyList<(double X, double Y)> polygon)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));
            if (polygon.Count < 3) throw new ArgumentException("A zone needs at least three vertices.", nameof(polygon));
            if (!geoReference.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");

            var px = new double[polygon.Count];
            var py = new double[polygon.Count];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                var (c, r) = geoReference.WorldToPixel(polygon[i].X, polygon[i].Y);
                px[i] = c; py[i] = r;
                minX = Math.Min(minX, c); maxX = Math.Max(maxX, c);
                minY = Math.Min(minY, r); maxY = Math.Max(maxY, r);
            }

            int c0 = Math.Max(0, (int)Math.Floor(minX)), c1 = Math.Min(raster.Width - 1, (int)Math.Ceiling(maxX));
            int r0 = Math.Max(0, (int)Math.Floor(minY)), r1 = Math.Min(raster.Height - 1, (int)Math.Ceiling(maxY));

            var (_, b, cc, _, e, f) = geoReference.GeoTransform;
            double cellArea = Math.Abs(b * f - cc * e);

            long count = 0, noData = 0;
            double min = double.MaxValue, max = double.MinValue, sum = 0, sumSq = 0;
            var crossings = new List<double>();
            for (int row = r0; row <= r1; row++)
            {
                // Scanline through the row's cell centres: the x positions where polygon edges cross it.
                double y = row + 0.5;
                crossings.Clear();
                for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                {
                    if ((py[i] > y) != (py[j] > y))
                        crossings.Add(px[i] + (y - py[i]) / (py[j] - py[i]) * (px[j] - px[i]));
                }
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int from = Math.Max(c0, (int)Math.Ceiling(crossings[k] - 0.5));
                    int to = Math.Min(c1, (int)Math.Floor(crossings[k + 1] - 0.5));
                    for (int col = from; col <= to; col++)
                    {
                        double x = col + 0.5;
                        if (x < crossings[k] || x > crossings[k + 1]) continue;
                        float v = raster[row, col];
                        if (raster.IsNoData(v)) { noData++; continue; }
                        count++;
                        sum += v; sumSq += (double)v * v;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                }
            }
            return new ZonalResult(count, noData, min, max, sum, sumSq, cellArea);
        }
    }

    /// <summary>Planar measurements of paths and polygons in world units, plus the terrain-following (3-D) length.</summary>
    public static class Measurement
    {
        /// <summary>Length of the polyline through <paramref name="points"/>.</summary>
        public static double Length(IReadOnlyList<(double X, double Y)> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            double len = 0;
            for (int i = 1; i < points.Count; i++)
                len += Math.Sqrt(Sq(points[i].X - points[i - 1].X) + Sq(points[i].Y - points[i - 1].Y));
            return len;
        }

        /// <summary>Perimeter of the (implicitly closed) polygon.</summary>
        public static double Perimeter(IReadOnlyList<(double X, double Y)> polygon)
        {
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));
            if (polygon.Count < 2) return 0;
            var last = polygon[polygon.Count - 1];
            return Length(polygon) + Math.Sqrt(Sq(polygon[0].X - last.X) + Sq(polygon[0].Y - last.Y));
        }

        /// <summary>Area of the (implicitly closed, simple) polygon — shoelace formula, always positive.</summary>
        public static double Area(IReadOnlyList<(double X, double Y)> polygon)
        {
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));
            if (polygon.Count < 3) return 0;
            double twice = 0;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                twice += (polygon[j].X * polygon[i].Y) - (polygon[i].X * polygon[j].Y);
            return Math.Abs(twice) / 2;
        }

        /// <summary>
        /// Terrain-following length of a path over an elevation raster: the path is sampled every
        /// <paramref name="spacing"/> world units (bilinear) and each step contributes
        /// √(horizontal² + Δz²). Steps touching no-data count only their horizontal length.
        /// </summary>
        public static double SurfaceLength(Raster elevation, RasterGeoReference geoReference, IReadOnlyList<(double X, double Y)> points, double spacing)
        {
            var samples = RasterProfiler.SamplePolylineWorld(elevation, geoReference, points, spacing);
            double len = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                double d = samples[i].Distance - samples[i - 1].Distance;
                float? z0 = samples[i - 1].Value, z1 = samples[i].Value;
                len += z0.HasValue && z1.HasValue ? Math.Sqrt(d * d + Sq(z1.Value - z0.Value)) : d;
            }
            return len;
        }

        private static double Sq(double v) => v * v;
    }
}
