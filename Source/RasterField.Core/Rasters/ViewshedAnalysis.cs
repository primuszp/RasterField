using System;
using System.Threading;
using System.Threading.Tasks;

namespace RasterField.Rasters
{
    /// <summary>
    /// Line-of-sight visibility analysis from a single observer point over an elevation raster —
    /// the classic "R2" viewshed: straight-line-of-sight against the terrain profile, ignoring
    /// earth curvature and atmospheric refraction (a reasonable simplification for a local or
    /// regional DEM; not intended for very-long-range analysis where curvature matters).
    /// </summary>
    public static class ViewshedAnalysis
    {
        /// <summary>Cell value meaning "visible from the observer" in the returned raster.</summary>
        public const float Visible = 1f;

        /// <summary>Cell value meaning "not visible from the observer" in the returned raster.</summary>
        public const float NotVisible = 0f;

        /// <summary>
        /// Computes visibility from (<paramref name="observerCol"/>, <paramref name="observerRow"/>),
        /// whose eye is <paramref name="observerHeight"/> map units above the terrain there, out to
        /// every cell within <paramref name="maxDistanceCells"/> cells (Euclidean, in cells;
        /// defaults to the raster's own diagonal — i.e. unlimited). A target cell counts as
        /// visible when the straight line from the observer's eye to <paramref name="targetHeight"/>
        /// units above that cell's terrain never dips below the intervening terrain profile.
        /// </summary>
        /// <returns>
        /// A same-size raster: 1 = visible, 0 = not visible, no-data = outside
        /// <paramref name="maxDistanceCells"/>, or the target's own elevation is no-data.
        /// </returns>
        public static Raster Compute(
            Raster elevation, int observerCol, int observerRow,
            double observerHeight = 1.8, double targetHeight = 0.0, int? maxDistanceCells = null,
            CancellationToken cancellationToken = default)
        {
            if (elevation == null) throw new ArgumentNullException(nameof(elevation));
            int w = elevation.Width, h = elevation.Height;
            if ((uint)observerCol >= (uint)w || (uint)observerRow >= (uint)h)
                throw new ArgumentOutOfRangeException(nameof(observerCol), "The observer point must be inside the raster.");
            if (elevation.IsNoData(elevation[observerRow, observerCol]))
                throw new ArgumentException("The observer's own cell is no-data.", nameof(observerCol));
            cancellationToken.ThrowIfCancellationRequested();

            int maxDist = maxDistanceCells ?? (int)Math.Ceiling(Math.Sqrt((double)w * w + (double)h * h));
            if (maxDist < 1) throw new ArgumentOutOfRangeException(nameof(maxDistanceCells));

            double observerZ = elevation[observerRow, observerCol] + observerHeight;
            var output = new Raster(w, h, noDataValue: -1);
            for (int i = 0; i < output.Samples.Length; i++) output.Samples[i] = float.NaN; // start "outside range"

            output.SetValueFast(observerRow, observerCol, Visible);

            int minCol = Math.Max(0, observerCol - maxDist), maxCol = Math.Min(w - 1, observerCol + maxDist);
            int minRow = Math.Max(0, observerRow - maxDist), maxRow = Math.Min(h - 1, observerRow + maxDist);
            double maxDistSq = (double)maxDist * maxDist;

            var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.For(minRow, maxRow + 1, parallelOptions, r =>
            {
                for (int c = minCol; c <= maxCol; c++)
                {
                    if (r == observerRow && c == observerCol) continue;

                    double dCol = c - observerCol, dRow = r - observerRow;
                    if (dCol * dCol + dRow * dRow > maxDistSq) continue;

                    float tz = elevation[r, c];
                    if (elevation.IsNoData(tz)) continue; // stays no-data: target itself unknown

                    output.SetValueFast(r, c, IsVisible(elevation, observerCol, observerRow,
                        observerZ, c, r, tz + targetHeight, cancellationToken) ? Visible : NotVisible);
                }
            });

            output.InvalidateStatistics();
            return output;
        }

        private static bool IsVisible(Raster elevation, int oc, int or_, double observerZ,
            int tc, int tr, double targetZ, CancellationToken cancellationToken)
        {
            int steps = Math.Max(Math.Abs(tc - oc), Math.Abs(tr - or_));
            if (steps <= 1) return true; // adjacent cell: nothing can intervene

            const double epsilon = 1e-6;
            for (int i = 1; i < steps; i++)
            {
                if ((i & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                double t = (double)i / steps;
                double col = oc + (tc - oc) * t;
                double row = or_ + (tr - or_) * t;

                // RasterProfiler's bilinear sampler uses corner-addressed coordinates, where a
                // cell's own value sits at col+0.5/row+0.5 — offset accordingly.
                float? terrain = RasterProfiler.BilinearSample(elevation, col + 0.5, row + 0.5);
                if (terrain == null) continue; // unknown terrain here: assume it doesn't block

                double lineOfSight = observerZ + t * (targetZ - observerZ);
                if (terrain.Value > lineOfSight + epsilon) return false;
            }
            return true;
        }
    }
}
