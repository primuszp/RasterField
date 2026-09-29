using System;
using System.Threading;
using System.Threading.Tasks;

namespace RasterField.Rasters
{
    /// <summary>What <see cref="BezierPatchInterpolator"/> does where a patch's 4&#215;4 neighbourhood touches no-data.</summary>
    public enum BezierNoDataMode
    {
        /// <summary>Fall back to no-data-tolerant bilinear interpolation for that patch (default).</summary>
        FallbackBilinear,

        /// <summary>Leave the output no-data wherever the patch's neighbourhood is incomplete.</summary>
        NoData,
    }

    /// <summary>Parameters for <see cref="BezierPatchInterpolator"/>.</summary>
    public sealed class BezierPatchOptions
    {
        /// <summary>Subdivision factor <c>k</c>: every source cell becomes <c>k &#215; k</c> output cells. 1 = identity.</summary>
        public int Factor { get; set; } = 4;

        /// <summary>
        /// Tangent tension <c>τ</c> in [0, 1]: 1 = Catmull-Rom tangents (smooth, C¹ across patches);
        /// 0 = the patch reproduces plain bilinear interpolation exactly. In between blends the two.
        /// </summary>
        public double Tension { get; set; } = 1.0;

        /// <summary>
        /// Suppresses overshoot: tangents are limited (Fritsch–Carlson / Hyman style) so every patch
        /// edge is monotone between its corners, the twist is dropped, and the value is clamped to the
        /// patch's corner range. Useful on sharp breaks (embankments, quarry walls).
        /// </summary>
        public bool Monotone { get; set; }

        /// <summary>Behaviour next to no-data cells.</summary>
        public BezierNoDataMode NoData { get; set; } = BezierNoDataMode.FallbackBilinear;

        internal void Validate()
        {
            if (Factor < 1) throw new ArgumentOutOfRangeException(nameof(Factor), "The subdivision factor must be at least 1.");
            if (double.IsNaN(Tension) || Tension < 0 || Tension > 1)
                throw new ArgumentOutOfRangeException(nameof(Tension), "Tension must be between 0 and 1.");
        }
    }

    /// <summary>
    /// Bicubic Bézier-patch interpolation of a gridded surface, and subdivision of a raster to a
    /// finer cell size with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cell's value sits at its centre (the same corner-addressed convention as
    /// <see cref="RasterGeoReference"/> and <see cref="RasterProfiler.BilinearSample"/>). One patch
    /// spans the square between four neighbouring cell centres. Its 16 Bézier control points are
    /// built from the node values and their derivatives — estimated by central differences
    /// (Catmull-Rom): <c>Dx = (z[i+1] − z[i−1]) / 2</c>, likewise <c>Dy</c>, and the cross
    /// derivative (twist) <c>Dxy</c> — using the standard Hermite → Bézier conversion
    /// (<c>P10 = z + Dx/3</c>, <c>P11 = z + Dx/3 + Dy/3 + Dxy/9</c>, …). Because neighbouring patches
    /// share node values and derivatives, the surface is C¹ continuous across patch borders.
    /// </para>
    /// <para>
    /// Outside the outermost cell centres (the image's half-cell border) the grid is extended by
    /// point reflection (<c>z[−1] = 2·z[0] − z[1]</c>), i.e. linear extrapolation, so a plane is
    /// reproduced exactly all the way to the image edge. A patch whose 4&#215;4 neighbourhood
    /// contains no-data follows <see cref="BezierPatchOptions.NoData"/>; an output cell whose
    /// containing source cell is itself no-data is always no-data.
    /// </para>
    /// <para>Interpolation smooths — it never adds measured information.</para>
    /// </remarks>
    public static class BezierPatchInterpolator
    {
        /// <summary>
        /// Returns a new raster <see cref="BezierPatchOptions.Factor"/> times finer in both directions,
        /// covering exactly the same extent: output cell (X, Y)'s centre is evaluated at source pixel
        /// coordinate ((X + 0.5) / k, (Y + 0.5) / k).
        /// </summary>
        public static Raster Subdivide(Raster source, BezierPatchOptions options,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            int k = options.Factor;
            long outCells = (long)source.Width * k * source.Height * k;
            if (outCells > int.MaxValue)
                throw new ArgumentException($"A {k}× subdivision of a {source.Width}×{source.Height} raster would have {outCells:N0} cells — too many for one in-memory band.", nameof(options));

            int outW = source.Width * k, outH = source.Height * k;
            var output = new Raster(outW, outH, source.NoDataValue);
            if (outW == 0 || outH == 0) return output;

            float noData = double.IsNaN(source.NoDataValue) ? float.NaN : (float)source.NoDataValue;
            var grid = new Grid(source);

            // Per output column/row: which patch it falls in and its Bernstein weights there.
            var cols = Axis.Build(source.Width, k);
            var rows = Axis.Build(source.Height, k);

            // Group output rows by patch row so each patch row's control points are built once.
            int firstPatchRow = rows.Patch[0], lastPatchRow = rows.Patch[outH - 1];
            int done = 0, total = lastPatchRow - firstPatchRow + 1;
            // The patch index never decreases (and, with k ≥ 1, never skips) down the output rows, so
            // each patch row owns one contiguous run: [rowStart[n], rowStart[n + 1]).
            var rowStart = new int[total + 1];
            rowStart[total] = outH;
            for (int y = outH - 1; y >= 0; y--) rowStart[rows.Patch[y] - firstPatchRow] = y;
            var po = new ParallelOptions { CancellationToken = cancellationToken };

            Parallel.For(firstPatchRow, lastPatchRow + 1, po, j =>
            {
                var patches = new Patch?[source.Width + 1]; // index i+1 for patch column i ∈ [-1, W-1]
                for (int y = rowStart[j - firstPatchRow]; y < rowStart[j - firstPatchRow + 1]; y++)
                {
                    int srcRow = y / k;
                    int rowBase = y * outW;
                    for (int x = 0; x < outW; x++)
                    {
                        int srcCol = x / k;
                        if (source.IsNoData(source[srcRow, srcCol])) { output.Samples[rowBase + x] = noData; continue; }

                        int i = cols.Patch[x];
                        var patch = patches[i + 1] ??= Patch.Build(grid, i, j, options);
                        double value;
                        if (patch.IsComplete)
                            value = patch.Evaluate(cols.W0[x], cols.W1[x], cols.W2[x], cols.W3[x], rows.W0[y], rows.W1[y], rows.W2[y], rows.W3[y]);
                        else if (options.NoData == BezierNoDataMode.FallbackBilinear)
                            value = RasterProfiler.BilinearSample(source, (x + 0.5) / k, (y + 0.5) / k) ?? double.NaN;
                        else
                            value = double.NaN;

                        output.Samples[rowBase + x] = double.IsNaN(value) ? noData : (float)value;
                    }
                }
                if (progress != null) progress.Report(Interlocked.Increment(ref done) / (double)total);
            });

            output.InvalidateStatistics();
            return output;
        }

        /// <summary>
        /// Subdivides a window that was read with <paramref name="marginCells"/> extra source cells
        /// on every side (so its border patches see real neighbours, not reflected ones), then crops
        /// the margin back off. The result covers exactly the inner window at <c>k</c>&#215; resolution.
        /// </summary>
        public static Raster SubdivideWindow(Raster sourceWindow, int marginCells, BezierPatchOptions options)
        {
            if (sourceWindow == null) throw new ArgumentNullException(nameof(sourceWindow));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (marginCells < 0 || (marginCells > 0 && 2 * marginCells >= Math.Min(sourceWindow.Width, sourceWindow.Height)))
                throw new ArgumentOutOfRangeException(nameof(marginCells), "The margin must leave a non-empty inner window.");

            var full = Subdivide(sourceWindow, options);
            if (marginCells == 0) return full;
            int k = options.Factor, m = marginCells * k;
            return RasterClipper.Crop(full, m, m, full.Width - 2 * m, full.Height - 2 * m);
        }

        /// <summary>
        /// Evaluates the Bézier surface at a (fractional) pixel coordinate, corner-addressed like
        /// <see cref="RasterGeoReference"/>. <see langword="null"/> outside the raster, on a no-data
        /// cell, or where the neighbourhood is incomplete and <see cref="BezierPatchOptions.NoData"/>
        /// is <see cref="BezierNoDataMode.NoData"/>. <see cref="BezierPatchOptions.Factor"/> is ignored.
        /// </summary>
        public static float? Sample(Raster source, double pixelX, double pixelY, BezierPatchOptions options)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();
            if (pixelX < 0 || pixelY < 0 || pixelX > source.Width || pixelY > source.Height) return null;

            int srcCol = Math.Min((int)pixelX, source.Width - 1), srcRow = Math.Min((int)pixelY, source.Height - 1);
            if (source.IsNoData(source[srcRow, srcCol])) return null;

            var (i, t) = Axis.Locate(pixelX, source.Width);
            var (j, s) = Axis.Locate(pixelY, source.Height);
            var patch = Patch.Build(new Grid(source), i, j, options);
            if (!patch.IsComplete)
                return options.NoData == BezierNoDataMode.FallbackBilinear ? RasterProfiler.BilinearSample(source, pixelX, pixelY) : null;

            Bernstein(t, out double u0, out double u1, out double u2, out double u3);
            Bernstein(s, out double v0, out double v1, out double v2, out double v3);
            return (float)patch.Evaluate(u0, u1, u2, u3, v0, v1, v2, v3);
        }

        private static void Bernstein(double t, out double b0, out double b1, out double b2, out double b3)
        {
            double m = 1 - t;
            b0 = m * m * m;
            b1 = 3 * t * m * m;
            b2 = 3 * t * t * m;
            b3 = t * t * t;
        }

        /// <summary>Per-axis lookup: patch index and Bernstein weights for every output position.</summary>
        private sealed class Axis
        {
            public int[] Patch = Array.Empty<int>();
            public double[] W0 = Array.Empty<double>(), W1 = Array.Empty<double>(), W2 = Array.Empty<double>(), W3 = Array.Empty<double>();

            public static Axis Build(int sourceLength, int k)
            {
                int n = sourceLength * k;
                var a = new Axis { Patch = new int[n], W0 = new double[n], W1 = new double[n], W2 = new double[n], W3 = new double[n] };
                for (int x = 0; x < n; x++)
                {
                    var (p, t) = Locate((x + 0.5) / k, sourceLength);
                    a.Patch[x] = p;
                    Bernstein(t, out a.W0[x], out a.W1[x], out a.W2[x], out a.W3[x]);
                }
                return a;
            }

            /// <summary>Pixel coordinate → (patch index in [-1, n-1], local parameter in [0, 1]).</summary>
            public static (int Patch, double T) Locate(double pixel, int n)
            {
                double u = pixel - 0.5; // cell-centre space: node i sits at u = i
                int p = (int)Math.Floor(u);
                if (p < -1) p = -1;
                if (p > n - 1) p = n - 1;
                double t = u - p;
                return (p, t < 0 ? 0 : t > 1 ? 1 : t);
            }
        }

        /// <summary>The source grid, extended beyond its edges by point reflection; NaN = no-data.</summary>
        private readonly struct Grid
        {
            private readonly Raster _r;
            public Grid(Raster r) { _r = r; }

            public double this[int i, int j]
            {
                get
                {
                    int w = _r.Width, h = _r.Height;
                    if (i < 0) return 2 * this[0, j] - this[Math.Min(-i, w - 1), j];
                    if (i >= w) return 2 * this[w - 1, j] - this[Math.Max(2 * (w - 1) - i, 0), j];
                    if (j < 0) return 2 * this[i, 0] - this[i, Math.Min(-j, h - 1)];
                    if (j >= h) return 2 * this[i, h - 1] - this[i, Math.Max(2 * (h - 1) - j, 0)];
                    float v = _r[j, i];
                    return _r.IsNoData(v) ? double.NaN : v;
                }
            }
        }

        /// <summary>The 16 control points of one patch between nodes (i, j) and (i+1, j+1).</summary>
        private sealed class Patch
        {
            private readonly double[] _p = new double[16]; // _p[a * 4 + b]: a along x (u), b along y (v)
            private double _min, _max;
            private bool _clamp;
            public bool IsComplete { get; private set; }

            public double Evaluate(double u0, double u1, double u2, double u3, double v0, double v1, double v2, double v3)
            {
                var p = _p;
                double r0 = v0 * p[0] + v1 * p[1] + v2 * p[2] + v3 * p[3];
                double r1 = v0 * p[4] + v1 * p[5] + v2 * p[6] + v3 * p[7];
                double r2 = v0 * p[8] + v1 * p[9] + v2 * p[10] + v3 * p[11];
                double r3 = v0 * p[12] + v1 * p[13] + v2 * p[14] + v3 * p[15];
                double value = u0 * r0 + u1 * r1 + u2 * r2 + u3 * r3;
                if (_clamp) value = value < _min ? _min : value > _max ? _max : value;
                return value;
            }

            public static Patch Build(Grid g, int i, int j, BezierPatchOptions o)
            {
                var patch = new Patch();

                // 4×4 neighbourhood z[a, b] = g[i - 1 + a, j - 1 + b].
                var z = new double[4, 4];
                for (int a = 0; a < 4; a++)
                    for (int b = 0; b < 4; b++)
                    {
                        z[a, b] = g[i - 1 + a, j - 1 + b];
                        if (double.IsNaN(z[a, b])) return patch; // incomplete
                    }
                patch.IsComplete = true;

                double tau = o.Tension;
                double z00 = z[1, 1], z10 = z[2, 1], z01 = z[1, 2], z11 = z[2, 2];

                // Bilinear reference derivatives of this patch (what τ = 0 reproduces).
                double linX0 = z10 - z00, linX1 = z11 - z01; // ∂/∂u along the v=0 and v=1 edges
                double linY0 = z01 - z00, linY1 = z11 - z10; // ∂/∂v along the u=0 and u=1 edges
                double linXY = z11 - z10 - z01 + z00;

                double Dx(int a, int b) => Tangent(z[a - 1, b], z[a, b], z[a + 1, b], o.Monotone);
                double Dy(int a, int b) => Tangent(z[a, b - 1], z[a, b], z[a, b + 1], o.Monotone);
                double Dxy(int a, int b) => o.Monotone ? 0 : (z[a + 1, b + 1] - z[a + 1, b - 1] - z[a - 1, b + 1] + z[a - 1, b - 1]) / 4;

                double Mix(double catmull, double linear) => tau * catmull + (1 - tau) * linear;

                double dx00 = Mix(Dx(1, 1), linX0), dx10 = Mix(Dx(2, 1), linX0), dx01 = Mix(Dx(1, 2), linX1), dx11 = Mix(Dx(2, 2), linX1);
                double dy00 = Mix(Dy(1, 1), linY0), dy10 = Mix(Dy(2, 1), linY1), dy01 = Mix(Dy(1, 2), linY0), dy11 = Mix(Dy(2, 2), linY1);
                double dxy00 = Mix(Dxy(1, 1), linXY), dxy10 = Mix(Dxy(2, 1), linXY), dxy01 = Mix(Dxy(1, 2), linXY), dxy11 = Mix(Dxy(2, 2), linXY);

                var p = patch._p;
                // corners
                p[0 * 4 + 0] = z00; p[3 * 4 + 0] = z10; p[0 * 4 + 3] = z01; p[3 * 4 + 3] = z11;
                // edges along u
                p[1 * 4 + 0] = z00 + dx00 / 3; p[2 * 4 + 0] = z10 - dx10 / 3;
                p[1 * 4 + 3] = z01 + dx01 / 3; p[2 * 4 + 3] = z11 - dx11 / 3;
                // edges along v
                p[0 * 4 + 1] = z00 + dy00 / 3; p[0 * 4 + 2] = z01 - dy01 / 3;
                p[3 * 4 + 1] = z10 + dy10 / 3; p[3 * 4 + 2] = z11 - dy11 / 3;
                // interior (twist)
                p[1 * 4 + 1] = z00 + dx00 / 3 + dy00 / 3 + dxy00 / 9;
                p[2 * 4 + 1] = z10 - dx10 / 3 + dy10 / 3 - dxy10 / 9;
                p[1 * 4 + 2] = z01 + dx01 / 3 - dy01 / 3 - dxy01 / 9;
                p[2 * 4 + 2] = z11 - dx11 / 3 - dy11 / 3 + dxy11 / 9;

                if (o.Monotone)
                {
                    patch._clamp = true;
                    patch._min = Math.Min(Math.Min(z00, z10), Math.Min(z01, z11));
                    patch._max = Math.Max(Math.Max(z00, z10), Math.Max(z01, z11));
                }
                return patch;
            }

            /// <summary>Central-difference tangent, optionally limited so the 1-D Hermite segment stays monotone.</summary>
            private static double Tangent(double prev, double here, double next, bool monotone)
            {
                double d = (next - prev) / 2;
                if (!monotone) return d;
                double dm = here - prev, dp = next - here;
                if (dm * dp <= 0) return 0; // local extremum (or flat): no overshoot allowed
                double limit = 3 * Math.Min(Math.Abs(dm), Math.Abs(dp));
                return Math.Sign(d) * Math.Min(Math.Abs(d), limit);
            }
        }
    }
}
