using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RasterField.Rasters
{
    /// <summary>How a convolution treats window cells that are no-data (or outside the raster).</summary>
    public enum ConvolutionNoData
    {
        /// <summary>
        /// A missing neighbour takes the centre cell's value (edges likewise), so a gap or the
        /// raster border never creates an artificial step; only a no-data centre stays no-data.
        /// </summary>
        FillFromCentre,

        /// <summary>Any no-data cell inside the window makes the output cell no-data.</summary>
        Propagate,
    }

    /// <summary>
    /// A square, odd-sized convolution matrix with a divisor and an offset:
    /// <c>output = Σ weight·value / divisor + offset</c>. Presets cover the classic image /
    /// terrain filters; <see cref="Parse"/> reads a user-typed matrix.
    /// </summary>
    public sealed class ConvolutionKernel
    {
        private readonly double[] _weights;

        /// <summary>Creates a kernel from row-major <paramref name="weights"/> (<paramref name="size"/>² values).</summary>
        public ConvolutionKernel(int size, IReadOnlyList<double> weights, double divisor = 1.0, double offset = 0.0)
        {
            if (size < 1 || size % 2 == 0) throw new ArgumentOutOfRangeException(nameof(size), "The kernel size must be a positive odd number.");
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (weights.Count != size * size) throw new ArgumentException($"A {size}×{size} kernel needs {size * size} weights.", nameof(weights));
            if (divisor == 0 || double.IsNaN(divisor) || double.IsInfinity(divisor)) throw new ArgumentOutOfRangeException(nameof(divisor), "The divisor must be a non-zero number.");
            Size = size;
            _weights = weights.ToArray();
            Divisor = divisor;
            Offset = offset;
        }

        /// <summary>Width and height of the matrix (odd).</summary>
        public int Size { get; }

        /// <summary>Row-major weights.</summary>
        public IReadOnlyList<double> Weights => _weights;

        /// <summary>The weighted sum is divided by this.</summary>
        public double Divisor { get; }

        /// <summary>Added after the division (e.g. 128 to centre an emboss on mid-grey).</summary>
        public double Offset { get; }

        /// <summary>Sum of the weights: 1-ish for a smoothing/sharpening filter, 0 for an edge/relief filter.</summary>
        public double Sum => _weights.Sum();

        /// <summary>Weight at row <paramref name="dy"/>, column <paramref name="dx"/> offsets from the centre.</summary>
        public double this[int dy, int dx] => _weights[(dy + Size / 2) * Size + dx + Size / 2];

        /// <summary>
        /// Directional emboss ("domborítás") with light from compass <paramref name="azimuthDegrees"/>
        /// (0 = north, 315 = north-west): positive where the surface rises away from the light, i.e.
        /// faces it. For the 8 principal directions the weights are the classic integers
        /// (north-west: <c>-2 -1 0 / -1 0 1 / 0 1 2</c>); the divisor scales a uniform slope to its
        /// rise per cell along that direction, so the result is comparable between sizes.
        /// </summary>
        public static ConvolutionKernel Emboss(double azimuthDegrees, int size = 3)
        {
            double a = azimuthDegrees * Math.PI / 180.0;
            // Screen axes (x right, y down): the direction pointing away from the light.
            double ax = -Math.Sin(a), ay = Math.Cos(a);
            double m = Math.Max(Math.Abs(ax), Math.Abs(ay));
            ax /= m; ay /= m;
            ax = Snap(ax); ay = Snap(ay);
            int r = size / 2;
            var w = new double[size * size];
            double sumSquares = 0;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    double v = dx * ax + dy * ay;
                    w[(dy + r) * size + dx + r] = v;
                    sumSquares += v * v;
                }
            // For z = g·(dx·ax + dy·ay)·m the weighted sum is g·m·Σw², so this divisor returns g.
            return new ConvolutionKernel(size, w, sumSquares * m);
        }

        /// <summary>Box (mean) smoothing over a <paramref name="size"/>×<paramref name="size"/> window.</summary>
        public static ConvolutionKernel Mean(int size = 3) =>
            new ConvolutionKernel(size, Enumerable.Repeat(1.0, size * size).ToArray(), size * size);

        /// <summary>Unsharp-mask sharpening: <c>value + amount·(value − local mean)</c>.</summary>
        public static ConvolutionKernel Sharpen(int size = 3, double amount = 1.0)
        {
            int n = size * size;
            var w = Enumerable.Repeat(-amount / n, n).ToArray();
            w[n / 2] = 1 + amount - amount / n;
            return new ConvolutionKernel(size, w);
        }

        /// <summary>Sobel west→east gradient (rise per cell).</summary>
        public static ConvolutionKernel SobelX() => new ConvolutionKernel(3, new double[] { -1, 0, 1, -2, 0, 2, -1, 0, 1 }, 8);

        /// <summary>Sobel north→south gradient (rise per cell, positive downwards on the map).</summary>
        public static ConvolutionKernel SobelY() => new ConvolutionKernel(3, new double[] { -1, -2, -1, 0, 0, 0, 1, 2, 1 }, 8);

        /// <summary>
        /// Negative Laplacian: positive on convex spots (peaks, ridges, embankment edges), negative in
        /// concave ones (pits, ditches), zero on planes. With <paramref name="diagonals"/> the 8-neighbour form.
        /// </summary>
        public static ConvolutionKernel Laplace(bool diagonals = false) => diagonals
            ? new ConvolutionKernel(3, new double[] { -1, -1, -1, -1, 8, -1, -1, -1, -1 })
            : new ConvolutionKernel(3, new double[] { 0, -1, 0, -1, 4, -1, 0, -1, 0 });

        /// <summary>
        /// Reads a matrix typed as rows of numbers: rows separated by new lines or <c>;</c>, values by
        /// spaces or tabs; a decimal comma is accepted. It must be square and odd-sized (1–15).
        /// </summary>
        public static ConvolutionKernel Parse(string text, double divisor = 1.0, double offset = 0.0)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var rows = text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim()).Where(r => r.Length > 0)
                .Select(r => r.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseNumber).ToArray())
                .ToList();
            int size = rows.Count;
            if (size == 0) throw new FormatException("The matrix is empty.");
            if (size % 2 == 0 || size > 15) throw new FormatException("The matrix must have an odd number of rows (1–15).");
            if (rows.Any(r => r.Length != size)) throw new FormatException($"Every row must have {size} values (a square matrix).");
            return new ConvolutionKernel(size, rows.SelectMany(r => r).ToArray(), divisor, offset);
        }

        /// <summary>The weights as <see cref="Parse"/> reads them: rows joined by <c>;</c>, invariant numbers.</summary>
        public string Format(string rowSeparator = "; ")
        {
            var sb = new StringBuilder();
            for (int r = 0; r < Size; r++)
            {
                if (r > 0) sb.Append(rowSeparator);
                for (int c = 0; c < Size; c++)
                {
                    if (c > 0) sb.Append(' ');
                    sb.Append(_weights[r * Size + c].ToString("0.####", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        private static double ParseNumber(string token)
        {
            string t = token.Replace(',', '.');
            if (t.Contains('/'))
            {
                var parts = t.Split('/');
                if (parts.Length == 2) return ParseNumber(parts[0]) / ParseNumber(parts[1]);
            }
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : throw new FormatException($"“{token}” is not a number.");
        }

        private static double Snap(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? Math.Round(v) : v;
    }

    /// <summary>
    /// Sliding-window (convolution) filters over a raster band: emboss, smoothing, sharpening,
    /// edge detection, and the local relief model (the surface minus its smoothed trend) that
    /// makes micro-relief visible on flat LiDAR terrain. The output has the input's size.
    /// </summary>
    public static class ConvolutionFilter
    {
        private const int ParallelRowThreshold = 64;

        /// <summary>Applies <paramref name="kernel"/> to every cell.</summary>
        public static Raster Convolve(Raster source, ConvolutionKernel kernel, ConvolutionNoData noData = ConvolutionNoData.FillFromCentre,
            CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (kernel == null) throw new ArgumentNullException(nameof(kernel));

            int w = source.Width, h = source.Height, r = kernel.Size / 2;
            var weights = kernel.Weights;
            var output = NewOutput(source);
            float[] src = source.Samples, dst = output.Samples;

            void Row(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    float centre = src[y * w + x];
                    if (source.IsNoData(centre)) { dst[y * w + x] = float.NaN; continue; }

                    double sum = 0;
                    bool missing = false;
                    for (int dy = -r; dy <= r && !missing; dy++)
                    {
                        int yy = Math.Min(h - 1, Math.Max(0, y + dy));
                        for (int dx = -r; dx <= r; dx++)
                        {
                            double wt = weights[(dy + r) * kernel.Size + dx + r];
                            if (wt == 0) continue;
                            int xx = Math.Min(w - 1, Math.Max(0, x + dx));
                            float v = src[yy * w + xx];
                            if (source.IsNoData(v))
                            {
                                if (noData == ConvolutionNoData.Propagate) { missing = true; break; }
                                v = centre;
                            }
                            sum += wt * v;
                        }
                    }
                    dst[y * w + x] = missing ? float.NaN : (float)(sum / kernel.Divisor + kernel.Offset);
                }
            }

            ForRows(h, Row, cancellationToken);
            output.InvalidateStatistics();
            return output;
        }

        /// <summary>
        /// Gaussian smoothing with standard deviation <paramref name="sigma"/> cells (window ±3σ),
        /// computed separably. No-data cells are left out and the weights renormalised, so a gap
        /// neither darkens nor spreads; a no-data centre stays no-data.
        /// </summary>
        public static Raster Gaussian(Raster source, double sigma, CancellationToken cancellationToken = default)
        {
            if (!(sigma > 0)) throw new ArgumentOutOfRangeException(nameof(sigma), "Sigma must be positive.");
            int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var weights = new double[2 * radius + 1];
            for (int i = -radius; i <= radius; i++) weights[i + radius] = Math.Exp(-(i * i) / (2 * sigma * sigma));
            return Separable(source, weights, cancellationToken);
        }

        /// <summary>Box (mean) smoothing over a (2·<paramref name="radius"/>+1)² window, separably and no-data aware.</summary>
        public static Raster BoxMean(Raster source, int radius, CancellationToken cancellationToken = default)
        {
            if (radius < 1) throw new ArgumentOutOfRangeException(nameof(radius), "The radius must be at least 1.");
            return Separable(source, Enumerable.Repeat(1.0, 2 * radius + 1).ToArray(), cancellationToken);
        }

        /// <summary>
        /// Local relief model: the surface minus its Gaussian-smoothed trend over ±<paramref name="radius"/>
        /// cells (σ = radius / 3). Removes the regional slope and keeps features smaller than the
        /// radius (ditches, banks, field boundaries, old channels) as positive/negative relief.
        /// </summary>
        public static Raster LocalRelief(Raster source, double radius, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!(radius >= 1)) throw new ArgumentOutOfRangeException(nameof(radius), "The radius must be at least 1 cell.");
            var trend = Gaussian(source, radius / 3.0, cancellationToken);
            var output = NewOutput(source);
            float[] src = source.Samples, t = trend.Samples, dst = output.Samples;
            for (int i = 0; i < src.Length; i++)
                dst[i] = source.IsNoData(src[i]) || float.IsNaN(t[i]) ? float.NaN : src[i] - t[i];
            output.InvalidateStatistics();
            return output;
        }

        /// <summary>Sobel gradient magnitude, √(gx² + gy²), in rise per cell: high on edges, 0 on planes.</summary>
        public static Raster SobelMagnitude(Raster source, ConvolutionNoData noData = ConvolutionNoData.FillFromCentre,
            CancellationToken cancellationToken = default)
        {
            var gx = Convolve(source, ConvolutionKernel.SobelX(), noData, cancellationToken);
            var gy = Convolve(source, ConvolutionKernel.SobelY(), noData, cancellationToken);
            var output = NewOutput(source);
            float[] x = gx.Samples, y = gy.Samples, dst = output.Samples;
            for (int i = 0; i < dst.Length; i++)
                dst[i] = float.IsNaN(x[i]) || float.IsNaN(y[i]) ? float.NaN : (float)Math.Sqrt((double)x[i] * x[i] + (double)y[i] * y[i]);
            output.InvalidateStatistics();
            return output;
        }

        /// <summary>
        /// Separable weighted mean (weights along x, then the same along y) over the valid cells only:
        /// <c>Σ wx·wy·v / Σ wx·wy</c> across the valid window, exact because both passes carry the
        /// weighted sum and the weight total. Out-of-raster cells are simply absent.
        /// </summary>
        private static Raster Separable(Raster source, double[] weights, CancellationToken cancellationToken)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int w = source.Width, h = source.Height, r = weights.Length / 2;
            float[] src = source.Samples;
            var sums = new double[src.Length];
            var totals = new double[src.Length];

            ForRows(h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double s = 0, t = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int xx = x + k;
                        if ((uint)xx >= (uint)w) continue;
                        float v = src[y * w + xx];
                        if (source.IsNoData(v)) continue;
                        double wt = weights[k + r];
                        s += wt * v;
                        t += wt;
                    }
                    sums[y * w + x] = s;
                    totals[y * w + x] = t;
                }
            }, cancellationToken);

            var output = NewOutput(source);
            float[] dst = output.Samples;
            ForRows(h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    if (source.IsNoData(src[y * w + x])) { dst[y * w + x] = float.NaN; continue; }
                    double s = 0, t = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int yy = y + k;
                        if ((uint)yy >= (uint)h) continue;
                        double wt = weights[k + r];
                        s += wt * sums[yy * w + x];
                        t += wt * totals[yy * w + x];
                    }
                    dst[y * w + x] = t > 0 ? (float)(s / t) : float.NaN;
                }
            }, cancellationToken);
            output.InvalidateStatistics();
            return output;
        }

        private static Raster NewOutput(Raster source) =>
            new Raster(source.Width, source.Height, double.IsNaN(source.NoDataValue) ? double.NaN : source.NoDataValue);

        private static void ForRows(int height, Action<int> row, CancellationToken cancellationToken)
        {
            if (height >= ParallelRowThreshold)
            {
                Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, row);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    row(y);
                }
            }
        }
    }
}
