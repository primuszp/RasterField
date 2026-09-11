using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>
    /// Fills no-data gaps ("holes") in a <see cref="Raster"/>. Both fill methods restrict
    /// themselves to the <b>convex hull of the raster's valid data</b>: a no-data cell is only
    /// ever a candidate for filling when it lies inside that hull — a genuine internal gap
    /// (sensor dropout, cloud mask, stripe, …) — never when it lies outside the data's actual
    /// footprint (the background margin of a rotated scene, an L-shaped mosaic's missing corner,
    /// …), which is left untouched exactly as it was. Within the hull, every gap is guaranteed to
    /// end up filled — there is no "gap beyond the search radius" left over.
    /// </summary>
    /// <remarks>
    /// The hull is computed from a provably exact and much cheaper reduction of the full valid-cell
    /// point set: for each row, only the leftmost and rightmost valid cell can possibly be a hull
    /// vertex (every other cell in that row lies on the segment between them, since they share the
    /// same Y), so the convex hull of just those row extrema is identical to the convex hull of
    /// every valid cell — but computed from O(height) points instead of up to O(width&#183;height).
    /// </remarks>
    public static class NoDataFiller
    {
        /// <summary>Number of no-data cells in the raster.</summary>
        public static long CountNoData(Raster source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            long n = 0;
            float[] s = source.Samples;
            for (int i = 0; i < s.Length; i++)
                if (source.IsNoData(s[i])) n++;
            return n;
        }

        /// <summary><see langword="true"/> when the raster has at least one no-data cell.</summary>
        public static bool HasNoData(Raster source) => CountNoData(source) > 0;

        /// <summary>
        /// Number of no-data cells that lie <i>within the convex hull</i> of the valid data —
        /// the actual gaps that <see cref="FillNearest"/> / <see cref="FillInverseDistanceWeighted"/>
        /// will address. This is typically far smaller than <see cref="CountNoData"/>, which also
        /// counts the (intentionally untouched) no-data margin outside the data's footprint.
        /// </summary>
        public static long CountNoDataWithinHull(Raster source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var spans = ComputeHullRowSpans(source);

            long n = 0;
            int w = source.Width;
            float[] s = source.Samples;
            for (int y = 0; y < source.Height; y++)
            {
                var span = spans[y];
                if (span == null) continue;
                int rowStart = y * w;
                for (int x = span.Value.MinCol; x <= span.Value.MaxCol; x++)
                    if (source.IsNoData(s[rowStart + x])) n++;
            }
            return n;
        }

        /// <summary>
        /// Fills every gap within the convex hull of the valid data with the value of the
        /// nearest valid cell ("nearest neighbour" / "nibble" fill — the same operation as Esri
        /// Spatial Analyst's <c>Nibble</c> or GRASS GIS's <c>r.grow.distance</c>). Implemented as
        /// the classical two-pass sequential distance transform of Rosenfeld &amp; Pfaltz,
        /// "Sequential Operations in Digital Picture Processing", J. ACM 13(4), 1966: a forward
        /// and a backward raster scan each propagate the coordinates of the nearest already-seen
        /// valid cell using a small local neighbourhood, giving an O(width&#183;height) approximate
        /// Euclidean nearest-neighbour fill with no search-radius limit — every in-hull gap ends
        /// up filled, since there is always a nearest valid cell to propagate from.
        /// </summary>
        public static Raster FillNearest(Raster source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int w = source.Width, h = source.Height;
            var hullSpans = ComputeHullRowSpans(source);

            var outSamples = (float[])source.Samples.Clone();
            PropagateNearest(outSamples, w, h, source.NoDataValue, hullSpans);
            return new Raster(w, h, outSamples, source.NoDataValue);
        }

        /// <summary>
        /// Fills gaps within the convex hull of the valid data using the inverse-distance-weighted
        /// directional search algorithm behind GDAL's <c>GDALFillNodata</c> / <c>gdal_fillnodata.py</c>:
        /// from each gap cell, the nearest valid pixel is located along a fixed number of
        /// angularly spaced search rays (within <paramref name="maxSearchDistance"/> cells); the
        /// fill value is the inverse distance weighted average of those hits. An optional
        /// smoothing pass (a 3&#215;3 mean filter applied only to filled cells, repeated
        /// <paramref name="smoothingIterations"/> times) blends the result, exactly as GDAL's
        /// implementation does. Finally, any in-hull cell IDW could not resolve within
        /// <paramref name="maxSearchDistance"/> (a gap larger than the search radius) is mopped up
        /// with the same nearest-neighbour propagation <see cref="FillNearest"/> uses, so every
        /// in-hull gap is guaranteed to end up filled regardless of the chosen search distance.
        /// </summary>
        /// <param name="source">The raster to fill (not modified).</param>
        /// <param name="maxSearchDistance">Maximum search radius in cells along each ray, for the (higher-quality) IDW pass.</param>
        /// <param name="smoothingIterations">Number of 3&#215;3 smoothing passes over the filled cells (0 = none).</param>
        /// <param name="directions">Number of angularly spaced search rays (8 or 16 are typical).</param>
        /// <param name="power">Inverse-distance weighting power (2 = standard IDW).</param>
        public static Raster FillInverseDistanceWeighted(
            Raster source, int maxSearchDistance = 100, int smoothingIterations = 0, int directions = 16, double power = 2.0)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (maxSearchDistance < 1) throw new ArgumentOutOfRangeException(nameof(maxSearchDistance));
            if (directions < 4) throw new ArgumentOutOfRangeException(nameof(directions), "At least 4 search directions are required.");

            int w = source.Width, h = source.Height;
            float[] src = source.Samples;
            var outSamples = (float[])src.Clone();
            var wasFilled = new bool[w * h];
            var hullSpans = ComputeHullRowSpans(source);

            var dx = new double[directions];
            var dy = new double[directions];
            for (int d = 0; d < directions; d++)
            {
                double angle = 2.0 * Math.PI * d / directions;
                dx[d] = Math.Cos(angle);
                dy[d] = Math.Sin(angle);
            }

            for (int y = 0; y < h; y++)
            {
                var span = hullSpans[y];
                if (span == null) continue; // this row is entirely outside the data's footprint

                int row = y * w;
                for (int x = span.Value.MinCol; x <= span.Value.MaxCol; x++)
                {
                    int idx = row + x;
                    if (!source.IsNoData(src[idx])) continue;

                    double weightSum = 0.0, valueSum = 0.0;
                    int hits = 0;

                    for (int d = 0; d < directions; d++)
                    {
                        int lastCx = x, lastCy = y;
                        for (int step = 1; step <= maxSearchDistance; step++)
                        {
                            int cx = (int)Math.Round(x + dx[d] * step, MidpointRounding.AwayFromZero);
                            int cy = (int)Math.Round(y + dy[d] * step, MidpointRounding.AwayFromZero);
                            if (cx < 0 || cy < 0 || cx >= w || cy >= h) break;
                            if (cx == lastCx && cy == lastCy) continue; // ray hasn't advanced to a new cell yet
                            lastCx = cx; lastCy = cy;

                            float v = src[cy * w + cx];
                            if (!source.IsNoData(v))
                            {
                                double dist = Math.Sqrt((cx - x) * (double)(cx - x) + (cy - y) * (double)(cy - y));
                                double weight = 1.0 / Math.Pow(Math.Max(dist, 1e-6), power);
                                weightSum += weight;
                                valueSum += weight * v;
                                hits++;
                                break; // nearest hit along this ray only
                            }
                        }
                    }

                    if (hits > 0)
                    {
                        outSamples[idx] = (float)(valueSum / weightSum);
                        wasFilled[idx] = true;
                    }
                }
            }

            for (int pass = 0; pass < smoothingIterations; pass++)
                Smooth(outSamples, wasFilled, w, h, source.NoDataValue);

            // Completeness guarantee: anything still no-data within the hull (a gap wider than
            // maxSearchDistance) gets mopped up by nearest-neighbour propagation, which has no
            // distance limit — propagating from both the original valid cells and the IDW fill
            // just applied.
            PropagateNearest(outSamples, w, h, source.NoDataValue, hullSpans);

            return new Raster(w, h, outSamples, source.NoDataValue);
        }

        /// <summary>
        /// Fills every no-data cell that is (a) still no-data in <paramref name="samples"/> and
        /// (b) within its row's hull span, with the value of its nearest non-no-data cell in
        /// <paramref name="samples"/> (which may itself be an already-filled cell from an earlier
        /// pass) — the two-pass Rosenfeld &amp; Pfaltz distance transform, gated to the hull.
        /// </summary>
        private static void PropagateNearest(float[] samples, int w, int h, double noDataValue, (int MinCol, int MaxCol)?[] hullSpans)
        {
            var nearest = new int[w * h];
            var distSq = new double[w * h];

            for (int i = 0; i < w * h; i++)
            {
                bool valid = !IsNoDataValue(samples[i], noDataValue);
                nearest[i] = valid ? i : -1;
                distSq[i] = valid ? 0.0 : double.PositiveInfinity;
            }

            void Consider(int idx, int ni, double stepDistSq)
            {
                if (ni < 0 || nearest[ni] < 0) return;

                // Chamfer-style propagated distance: straight-line distance to the neighbour's
                // recorded source, plus the neighbour-to-here step. Not exact Euclidean distance
                // (that would require carrying the source's (x,y) through the scan), but a very
                // close, standard approximation for this two-pass scheme.
                double propagated = Math.Sqrt(distSq[ni]) + Math.Sqrt(stepDistSq);
                double propagatedSq = propagated * propagated;
                if (propagatedSq < distSq[idx])
                {
                    distSq[idx] = propagatedSq;
                    nearest[idx] = nearest[ni];
                }
            }

            const double ortho = 1.0, diag = 2.0; // squared step lengths (1^2, sqrt(2)^2)

            // Forward pass: top-left -> bottom-right, looking at already-visited neighbours.
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = row + x;
                    if (nearest[idx] == idx) continue; // already a genuine source, nothing nearer possible

                    if (x > 0) Consider(idx, idx - 1, ortho);
                    if (y > 0)
                    {
                        Consider(idx, idx - w, ortho);
                        if (x > 0) Consider(idx, idx - w - 1, diag);
                        if (x < w - 1) Consider(idx, idx - w + 1, diag);
                    }
                }
            }

            // Backward pass: bottom-right -> top-left.
            for (int y = h - 1; y >= 0; y--)
            {
                int row = y * w;
                for (int x = w - 1; x >= 0; x--)
                {
                    int idx = row + x;
                    if (nearest[idx] == idx) continue;

                    if (x < w - 1) Consider(idx, idx + 1, ortho);
                    if (y < h - 1)
                    {
                        Consider(idx, idx + w, ortho);
                        if (x < w - 1) Consider(idx, idx + w + 1, diag);
                        if (x > 0) Consider(idx, idx + w - 1, diag);
                    }
                }
            }

            // Only now, gated by the hull, actually overwrite — cells outside the hull (or whose
            // row has no hull span at all) are left exactly as they were.
            for (int y = 0; y < h; y++)
            {
                var span = hullSpans[y];
                if (span == null) continue;
                int row = y * w;
                for (int x = span.Value.MinCol; x <= span.Value.MaxCol; x++)
                {
                    int idx = row + x;
                    if (nearest[idx] >= 0 && nearest[idx] != idx)
                        samples[idx] = samples[nearest[idx]];
                }
            }
        }

        private static bool IsNoDataValue(float value, double noDataValue) =>
            float.IsNaN(value) || (!double.IsNaN(noDataValue) && value == (float)noDataValue);

        private static void Smooth(float[] samples, bool[] filledMask, int w, int h, double noDataValue)
        {
            var result = (float[])samples.Clone();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (!filledMask[idx]) continue;

                    double sum = 0; int count = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int ny = y + oy;
                        if (ny < 0 || ny >= h) continue;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox;
                            if (nx < 0 || nx >= w) continue;
                            float v = samples[ny * w + nx];
                            if (IsNoDataValue(v, noDataValue)) continue;
                            sum += v;
                            count++;
                        }
                    }
                    if (count > 0) result[idx] = (float)(sum / count);
                }
            }
            Array.Copy(result, samples, samples.Length);
        }

        // ---- convex hull of the valid data ------------------------------------------------

        /// <summary>
        /// For every row, the [MinCol, MaxCol] span (inclusive) that the convex hull of the
        /// raster's valid cells covers at that row — or <see langword="null"/> when the hull
        /// does not reach that row at all. Built from the exact row-extrema reduction described
        /// in the type's remarks, then rasterised with a standard convex-polygon scanline pass.
        /// </summary>
        private static (int MinCol, int MaxCol)?[] ComputeHullRowSpans(Raster source)
        {
            int w = source.Width, h = source.Height;
            float[] s = source.Samples;
            var result = new (int MinCol, int MaxCol)?[h];

            var candidates = new List<(int X, int Y)>();
            for (int y = 0; y < h; y++)
            {
                int rowStart = y * w;
                int minX = -1, maxX = -1;
                for (int x = 0; x < w; x++)
                {
                    if (!source.IsNoData(s[rowStart + x]))
                    {
                        if (minX < 0) minX = x;
                        maxX = x;
                    }
                }
                if (minX < 0) continue; // no valid data in this row at all

                candidates.Add((minX, y));
                if (maxX != minX) candidates.Add((maxX, y));
            }

            if (candidates.Count == 0) return result; // no valid data anywhere: nothing to fill

            var hull = ConvexHull.Compute(candidates);
            int n = hull.Count;

            for (int y = 0; y < h; y++)
            {
                double? minX = null, maxX = null;
                for (int i = 0; i < n; i++)
                {
                    var p0 = hull[i];
                    var p1 = hull[(i + 1) % n];

                    if (p0.Y == p1.Y)
                    {
                        if (p0.Y != y) continue;
                        Track(ref minX, ref maxX, p0.X);
                        Track(ref minX, ref maxX, p1.X);
                        continue;
                    }

                    int lo = Math.Min(p0.Y, p1.Y), hi = Math.Max(p0.Y, p1.Y);
                    if (y < lo || y > hi) continue;

                    double t = (double)(y - p0.Y) / (p1.Y - p0.Y);
                    Track(ref minX, ref maxX, p0.X + t * (p1.X - p0.X));
                }

                if (minX.HasValue)
                    result[y] = ((int)Math.Floor(minX.Value), (int)Math.Ceiling(maxX!.Value));
            }

            return result;
        }

        private static void Track(ref double? min, ref double? max, double v)
        {
            if (min == null || v < min) min = v;
            if (max == null || v > max) max = v;
        }
    }
}
