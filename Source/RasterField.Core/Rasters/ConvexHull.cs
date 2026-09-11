using System;
using System.Collections.Generic;
using System.Linq;

namespace RasterField.Rasters
{
    /// <summary>
    /// Computes the 2D convex hull of a point set using Andrew's monotone chain algorithm
    /// (Andrew, A. M., "Another Efficient Algorithm for Convex Hulls in Two Dimensions",
    /// Information Processing Letters 9(5), 1979) — O(n log n), no external dependencies.
    /// </summary>
    public static class ConvexHull
    {
        /// <summary>
        /// Returns the hull vertices in counter-clockwise order, starting from the
        /// lowest-leftmost point. Collinear points along a hull edge are dropped (only the two
        /// endpoints of that edge are kept). Fewer than 3 distinct points are returned as-is
        /// (there is no meaningful "hull" to compute).
        /// </summary>
        public static IReadOnlyList<(int X, int Y)> Compute(IReadOnlyList<(int X, int Y)> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));

            var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
            if (pts.Length < 3) return pts;

            var lower = new List<(int X, int Y)>();
            foreach (var p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<(int X, int Y)>();
            for (int i = pts.Length - 1; i >= 0; i--)
            {
                var p = pts[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            // Each of lower/upper repeats the other's endpoint; drop the duplicate before joining.
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        /// <summary>Z-component of (a-o) &#215; (b-o); positive = counter-clockwise turn at a.</summary>
        private static long Cross((int X, int Y) o, (int X, int Y) a, (int X, int Y) b) =>
            (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);
    }
}
