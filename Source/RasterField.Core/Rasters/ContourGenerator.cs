using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>One traced contour line: a single level value and the ordered points that form it.</summary>
    public sealed class ContourLine
    {
        internal ContourLine(double level, IReadOnlyList<(double X, double Y)> points)
        {
            Level = level;
            Points = points;
        }

        /// <summary>The elevation (or other value) this line traces.</summary>
        public double Level { get; }

        /// <summary>The line's vertices, in world coordinates, in walk order.</summary>
        public IReadOnlyList<(double X, double Y)> Points { get; }
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

            void Emit((double X, double Y) a, (double X, double Y) b)
            {
                var wa = geo.PixelToWorld(a.X, a.Y);
                var wb = geo.PixelToWorld(b.X, b.Y);
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
