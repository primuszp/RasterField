using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>One traced contour line: a single level value and the ordered points that form it.</summary>
    public sealed class ContourLine
    {
        internal ContourLine(double level, IReadOnlyList<(double X, double Y)> points, bool isIndex = false)
        {
            Level = level;
            Points = points;
            IsIndex = isIndex;
        }

        /// <summary>The elevation (or other value) this line traces.</summary>
        public double Level { get; }

        /// <summary>The line's vertices, in world coordinates, in walk order.</summary>
        public IReadOnlyList<(double X, double Y)> Points { get; }

        /// <summary><see langword="true"/> for an index (major) contour — every <see cref="ContourOptions.IndexEvery"/>-th level.</summary>
        public bool IsIndex { get; }

        /// <summary><see langword="true"/> when the line closes on itself (first point equals last point).</summary>
        public bool IsClosed => Points.Count > 2 && Points[0].X == Points[Points.Count - 1].X && Points[0].Y == Points[Points.Count - 1].Y;

        /// <summary>Total length of the polyline, in world units.</summary>
        public double Length
        {
            get
            {
                double len = 0;
                for (int i = 1; i < Points.Count; i++)
                {
                    double dx = Points[i].X - Points[i - 1].X, dy = Points[i].Y - Points[i - 1].Y;
                    len += Math.Sqrt(dx * dx + dy * dy);
                }
                return len;
            }
        }
    }

    /// <summary>Parameters for <see cref="ContourGenerator.Trace(Raster, RasterGeoReference, ContourOptions)"/>.</summary>
    public sealed class ContourOptions
    {
        /// <summary>Lowest level to trace (rounded up to a multiple of <see cref="Interval"/>).</summary>
        public double Minimum { get; set; }

        /// <summary>Highest level to trace.</summary>
        public double Maximum { get; set; }

        /// <summary>Contour interval (distance between successive levels). Must be positive.</summary>
        public double Interval { get; set; } = 10;

        /// <summary>Every n-th level (counted from level 0, i.e. multiples of <c>n × Interval</c>) is an index contour; 0 = none.</summary>
        public int IndexEvery { get; set; } = 5;

        /// <summary>Chaikin corner-cutting passes applied to every line (0 = raw marching-squares output).</summary>
        public int SmoothingIterations { get; set; }

        /// <summary>Lines shorter than this (world units) are dropped as noise; 0 keeps everything.</summary>
        public double MinimumLength { get; set; }
    }

    /// <summary>
    /// Traces contour lines (lines of constant value) through a raster using the marching-squares
    /// algorithm — each 2&#215;2 cell of samples is classified against the level, and the resulting
    /// line segment(s) through that cell are linearly interpolated along its edges, then chained
    /// into polylines.
    /// </summary>
    /// <remarks>
    /// The two "saddle" cases (where diagonally-opposite corners are on the same side of the
    /// level, cases 5 and 10 of the standard 16-case table) are disambiguated using the average of
    /// the four corner values, the common convention that keeps contours from crossing themselves
    /// at a saddle point.
    /// </remarks>
    public static class ContourGenerator
    {
        /// <summary>Traces a single level and returns its (possibly several, possibly open) polylines.</summary>
        public static IReadOnlyList<ContourLine> TraceLevel(Raster raster, RasterGeoReference geoReference, double level)
        {
            return TraceLevels(raster, geoReference, new[] { level });
        }

        /// <summary>
        /// Traces every level in <paramref name="levels"/> in one pass over the raster (cheaper
        /// than calling <see cref="TraceLevel"/> repeatedly for many levels).
        /// </summary>
        public static IReadOnlyList<ContourLine> TraceLevels(Raster raster, RasterGeoReference geoReference, IReadOnlyList<double> levels)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (levels == null || levels.Count == 0) throw new ArgumentException("At least one level is required.", nameof(levels));

            var result = new List<ContourLine>();
            foreach (double level in levels)
            {
                var segments = new List<((double X, double Y) A, (double X, double Y) B)>();

                for (int row = 0; row < raster.Height - 1; row++)
                {
                    for (int col = 0; col < raster.Width - 1; col++)
                    {
                        float tl = raster[row, col];
                        float tr = raster[row, col + 1];
                        float bl = raster[row + 1, col];
                        float br = raster[row + 1, col + 1];
                        if (raster.IsNoData(tl) || raster.IsNoData(tr) || raster.IsNoData(bl) || raster.IsNoData(br)) continue;

                        CellSegments(row, col, tl, tr, bl, br, level, geoReference, segments);
                    }
                }

                foreach (var line in Chain(segments))
                    result.Add(new ContourLine(level, line));
            }
            return result;
        }

        /// <summary>
        /// Builds the list of levels between <paramref name="minimum"/> and <paramref name="maximum"/>
        /// (inclusive) that are whole multiples of <paramref name="interval"/>.
        /// </summary>
        public static IReadOnlyList<double> BuildLevels(double minimum, double maximum, double interval)
        {
            var levels = new List<double>();
            if (!(interval > 0) || maximum < minimum) return levels;

            long first = (long)Math.Ceiling(minimum / interval - 1e-9);
            long last = (long)Math.Floor(maximum / interval + 1e-9);
            for (long n = first; n <= last; n++)
                levels.Add(n * interval);
            return levels;
        }

        /// <summary>
        /// Traces all levels described by <paramref name="options"/>, marks index contours, smooths
        /// and filters the result.
        /// </summary>
        public static IReadOnlyList<ContourLine> Trace(Raster raster, RasterGeoReference geoReference, ContourOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!(options.Interval > 0)) throw new ArgumentOutOfRangeException(nameof(options), "The contour interval must be positive.");
            if (options.SmoothingIterations < 0) throw new ArgumentOutOfRangeException(nameof(options), "Smoothing iterations cannot be negative.");

            var levels = BuildLevels(options.Minimum, options.Maximum, options.Interval);
            if (levels.Count == 0) return Array.Empty<ContourLine>();

            var result = new List<ContourLine>();
            foreach (var line in TraceLevels(raster, geoReference, levels))
            {
                var points = line.Points;
                for (int i = 0; i < options.SmoothingIterations; i++)
                    points = Chaikin(points);

                bool isIndex = false;
                if (options.IndexEvery > 0)
                {
                    long n = (long)Math.Round(line.Level / options.Interval);
                    isIndex = n % options.IndexEvery == 0;
                }

                var contour = new ContourLine(line.Level, points, isIndex);
                if (options.MinimumLength > 0 && contour.Length < options.MinimumLength) continue;
                result.Add(contour);
            }
            return result;
        }

        /// <summary>
        /// One pass of Chaikin's corner cutting: every segment is replaced by points at 1/4 and 3/4
        /// of its length. An open line keeps its endpoints (so it still meets the raster edge / a
        /// no-data gap); a closed line stays closed.
        /// </summary>
        public static IReadOnlyList<(double X, double Y)> Chaikin(IReadOnlyList<(double X, double Y)> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (points.Count < 3) return points;

            bool closed = points[0].X == points[points.Count - 1].X && points[0].Y == points[points.Count - 1].Y;
            var output = new List<(double X, double Y)>(points.Count * 2);
            if (!closed) output.Add(points[0]);
            for (int i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                var q = (0.75 * a.X + 0.25 * b.X, 0.75 * a.Y + 0.25 * b.Y);
                var r = (0.25 * a.X + 0.75 * b.X, 0.25 * a.Y + 0.75 * b.Y);
                if (closed || i > 0) output.Add(q);
                if (closed || i < points.Count - 2) output.Add(r);
            }
            if (closed) output.Add(output[0]);
            else output.Add(points[points.Count - 1]);
            return output;
        }

        /// <summary>
        /// Emits the 0, 1 or 2 line segments (in world coordinates) that a single 2&#215;2 cell
        /// contributes at <paramref name="level"/>, given its four corner values (top-left,
        /// top-right, bottom-left, bottom-right) at image coordinates (col,row)..(col+1,row+1).
        /// </summary>
        private static void CellSegments(
            int row, int col, double tl, double tr, double bl, double br, double level,
            RasterGeoReference geo, List<((double X, double Y) A, (double X, double Y) B)> segments)
        {
            int mask = (tl >= level ? 8 : 0) | (tr >= level ? 4 : 0) | (br >= level ? 2 : 0) | (bl >= level ? 1 : 0);
            if (mask == 0 || mask == 15) return;

            // Edge midpoint interpolants, in image (col,row) space.
            (double X, double Y) Top() => (col + Frac(tl, tr, level), row);
            (double X, double Y) Bottom() => (col + Frac(bl, br, level), row + 1);
            (double X, double Y) Left() => (col, row + Frac(tl, bl, level));
            (double X, double Y) Right() => (col + 1, row + Frac(tr, br, level));

            // (col, row) here indexes samples, and a sample sits at its cell's centre — half a cell
            // in from the corner-addressed pixel coordinate the georeference expects.
            void Emit((double X, double Y) a, (double X, double Y) b)
            {
                var wa = geo.PixelToWorld(a.X + 0.5, a.Y + 0.5);
                var wb = geo.PixelToWorld(b.X + 0.5, b.Y + 0.5);
                segments.Add((wa, wb));
            }

            switch (mask)
            {
                case 1: case 14: Emit(Left(), Bottom()); break;
                case 2: case 13: Emit(Bottom(), Right()); break;
                case 3: case 12: Emit(Left(), Right()); break;
                case 4: case 11: Emit(Top(), Right()); break;
                case 6: case 9: Emit(Top(), Bottom()); break;
                case 7: case 8: Emit(Left(), Top()); break;

                case 5: // TL & BR above, TR & BL below: saddle
                    if ((tl + tr + bl + br) / 4.0 >= level) { Emit(Left(), Top()); Emit(Bottom(), Right()); }
                    else { Emit(Left(), Bottom()); Emit(Top(), Right()); }
                    break;

                case 10: // TR & BL above, TL & BR below: saddle
                    if ((tl + tr + bl + br) / 4.0 >= level) { Emit(Top(), Right()); Emit(Left(), Bottom()); }
                    else { Emit(Left(), Top()); Emit(Bottom(), Right()); }
                    break;
            }
        }

        /// <summary>Fraction of the way from <paramref name="a"/> to <paramref name="b"/> where the value crosses <paramref name="level"/>.</summary>
        private static double Frac(double a, double b, double level)
        {
            double d = b - a;
            return Math.Abs(d) < 1e-12 ? 0.5 : Clamp01((level - a) / d);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// Chains unordered line segments sharing endpoints into polylines. Segments are matched by
        /// exact coordinate equality, which holds here because shared cell edges always interpolate
        /// to the same point from either adjacent cell.
        /// </summary>
        private static List<List<(double X, double Y)>> Chain(List<((double X, double Y) A, (double X, double Y) B)> segments)
        {
            var byPoint = new Dictionary<(double, double), List<int>>();
            void Index(int i, (double X, double Y) p)
            {
                var key = (p.X, p.Y);
                if (!byPoint.TryGetValue(key, out var list)) byPoint[key] = list = new List<int>();
                list.Add(i);
            }
            for (int i = 0; i < segments.Count; i++) { Index(i, segments[i].A); Index(i, segments[i].B); }

            var used = new bool[segments.Count];
            var lines = new List<List<(double X, double Y)>>();

            for (int start = 0; start < segments.Count; start++)
            {
                if (used[start]) continue;
                used[start] = true;
                var line = new LinkedList<(double X, double Y)>();
                line.AddLast(segments[start].A);
                line.AddLast(segments[start].B);

                ExtendFrom(line, atFront: false, byPoint, segments, used);
                ExtendFrom(line, atFront: true, byPoint, segments, used);

                var points = new List<(double X, double Y)>(line.Count);
                points.AddRange(line);
                lines.Add(points);
            }
            return lines;
        }

        private static void ExtendFrom(
            LinkedList<(double X, double Y)> line, bool atFront,
            Dictionary<(double, double), List<int>> byPoint,
            List<((double X, double Y) A, (double X, double Y) B)> segments,
            bool[] used)
        {
            while (true)
            {
                var tip = atFront ? line.First!.Value : line.Last!.Value;
                if (!byPoint.TryGetValue((tip.X, tip.Y), out var candidates)) return;

                int nextSeg = -1;
                (double X, double Y) other = default;
                foreach (int idx in candidates)
                {
                    if (used[idx]) continue;
                    var (a, b) = segments[idx];
                    if (a.X == tip.X && a.Y == tip.Y) { nextSeg = idx; other = b; break; }
                    if (b.X == tip.X && b.Y == tip.Y) { nextSeg = idx; other = a; break; }
                }
                if (nextSeg < 0) return;

                used[nextSeg] = true;
                if (atFront) line.AddFirst(other); else line.AddLast(other);
            }
        }
    }
}
