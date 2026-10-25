using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RasterField.Rasters
{
    /// <summary>How a convolution treats no-data cells. Coordinates outside the raster are clamped to its edge.</summary>
    public enum ConvolutionNoData
    {
        /// <summary>
        /// A missing neighbour takes the centre cell's value; only a no-data centre stays no-data.
        /// </summary>
        FillFromCentre,

        /// <summary>Any no-data cell inside the window makes the output cell no-data.</summary>
        Propagate,
    }

    /// <summary>
    /// An odd-sized rectangular convolution matrix with a divisor and an offset:
    /// <c>output = Σ weight·value / divisor + offset</c>. Presets cover the classic image /
    /// terrain filters; <see cref="Parse"/> reads a user-typed matrix.
    /// </summary>
    public sealed class ConvolutionKernel
    {
        private readonly double[] _weights;

        /// <summary>Creates a kernel from row-major <paramref name="weights"/> (<paramref name="size"/>² values).</summary>
        public ConvolutionKernel(int size, IReadOnlyList<double> weights, double divisor = 1.0, double offset = 0.0)
            : this(size, size, weights, divisor, offset) { }

        /// <summary>Creates an odd rectangular matrix with independent column and row counts.</summary>
        public ConvolutionKernel(int width, int height, IReadOnlyList<double> weights, double divisor = 1.0, double offset = 0.0)
        {
            ValidateDimension(width, nameof(width));
            ValidateDimension(height, nameof(height));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (weights.Count != (long)width * height) throw new ArgumentException("The weight count must match width × height.", nameof(weights));
            if (divisor == 0 || !Finite(divisor)) throw new ArgumentOutOfRangeException(nameof(divisor), "The divisor must be finite and non-zero.");
            if (!Finite(offset)) throw new ArgumentOutOfRangeException(nameof(offset), "The offset must be finite.");
            if (weights.Any(v => !Finite(v))) throw new ArgumentException("Matrix weights must be finite.", nameof(weights));
            Width = width;
            Height = height;
            _weights = weights.ToArray();
            Divisor = divisor;
            Offset = offset;
        }

        /// <summary>Column count; retained for callers using square kernels. Use Width/Height for rectangles.</summary>
        public int Size => Width;
        /// <summary>Number of columns (odd).</summary>
        public int Width { get; }
        /// <summary>Number of rows (odd).</summary>
        public int Height { get; }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static void ValidateDimension(int value, string name)
        {
            if (value < 1 || value % 2 == 0 || value > 46339)
                throw new ArgumentOutOfRangeException(name, "Kernel dimensions must be positive odd numbers no greater than 46339.");
        }

        /// <summary>Row-major weights.</summary>
        public IReadOnlyList<double> Weights => _weights;

        /// <summary>The weighted sum is divided by this.</summary>
        public double Divisor { get; }

        /// <summary>Added after the division (e.g. 128 to centre an emboss on mid-grey).</summary>
        public double Offset { get; }

        /// <summary>Sum of the weights: 1-ish for a smoothing/sharpening filter, 0 for an edge/relief filter.</summary>
        public double Sum => _weights.Sum();

        /// <summary>Weight at row <paramref name="dy"/>, column <paramref name="dx"/> offsets from the centre.</summary>
        public double this[int dy, int dx] => _weights[(dy + Height / 2) * Width + dx + Width / 2];

        /// <summary>
        /// Directional emboss ("domborítás") with light from compass <paramref name="azimuthDegrees"/>
        /// (0 = north, 315 = north-west): positive where the surface rises away from the light, i.e.
        /// faces it. For the 8 principal directions the weights are the classic integers
        /// (north-west: <c>-2 -1 0 / -1 0 1 / 0 1 2</c>); the divisor scales a uniform slope to its
        /// rise per cell along that direction, so the result is comparable between sizes.
        /// </summary>
        public static ConvolutionKernel Emboss(double azimuthDegrees, int size = 3)
        {
            ValidateDimension(size, nameof(size));
            if (size < 3)
                throw new ArgumentOutOfRangeException(nameof(size), "Emboss needs an odd window size of at least 3.");
            if (double.IsNaN(azimuthDegrees) || double.IsInfinity(azimuthDegrees))
                throw new ArgumentOutOfRangeException(nameof(azimuthDegrees), "The light direction must be finite.");
            double a = (azimuthDegrees % 360.0) * Math.PI / 180.0;
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
        public static ConvolutionKernel Mean(int size = 3)
        {
            ValidateDimension(size, nameof(size));
            return new ConvolutionKernel(size, Enumerable.Repeat(1.0, size * size).ToArray(), size * size);
        }

        /// <summary>The exact 5×5 Gauss matrix illustrated in DigiTerra Map's reference manual, C.IV.5–5 (PDF p.65).</summary>
        public static ConvolutionKernel DigiTerraGaussian5() => new ConvolutionKernel(5, new double[]
        {
            1, 2, 3, 2, 1,
            2, 7, 11, 7, 2,
            3, 11, 17, 11, 3,
            2, 7, 11, 7, 2,
            1, 2, 3, 2, 1,
        }, 121);

        /// <summary>Unsharp-mask sharpening: <c>value + amount·(value − local mean)</c>.</summary>
        public static ConvolutionKernel Sharpen(int size = 3, double amount = 1.0)
        {
            ValidateDimension(size, nameof(size));
            if (!Finite(amount)) throw new ArgumentOutOfRangeException(nameof(amount));
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
        /// spaces or tabs; a decimal comma is accepted. Both row and column counts must be odd (1–15).
        /// </summary>
        public static ConvolutionKernel Parse(string text, double divisor = 1.0, double offset = 0.0)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var rows = text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim()).Where(r => r.Length > 0)
                .Select(r => r.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseNumber).ToArray())
                .ToList();
            int height = rows.Count;
            if (height == 0) throw new FormatException("The matrix is empty.");
            int width = rows[0].Length;
            if (height % 2 == 0 || height > 15 || width % 2 == 0 || width > 15)
                throw new FormatException("The matrix must have odd row and column counts (1–15).");
            if (rows.Any(r => r.Length != width)) throw new FormatException($"Every row must have {width} values.");
            return new ConvolutionKernel(width, height, rows.SelectMany(r => r).ToArray(), divisor, offset);
        }

        /// <summary>The weights as <see cref="Parse"/> reads them: rows joined by <c>;</c>, invariant numbers.</summary>
        public string Format(string rowSeparator = "; ")
        {
            var sb = new StringBuilder();
            for (int r = 0; r < Height; r++)
            {
                if (r > 0) sb.Append(rowSeparator);
                for (int c = 0; c < Width; c++)
                {
                    if (c > 0) sb.Append(' ');
                    sb.Append(_weights[r * Width + c].ToString("R", CultureInfo.InvariantCulture));
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

            int w = source.Width, h = source.Height, rx = kernel.Width / 2, ry = kernel.Height / 2;
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
                    for (int dy = -ry; dy <= ry && !missing; dy++)
                    {
                        int yy = Math.Min(h - 1, Math.Max(0, y + dy));
                        for (int dx = -rx; dx <= rx; dx++)
                        {
                            double wt = weights[(dy + ry) * kernel.Width + dx + rx];
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
            if (!(sigma > 0) || double.IsInfinity(sigma) || sigma > (int.MaxValue - 3) / 6.0) throw new ArgumentOutOfRangeException(nameof(sigma), "Sigma must be positive.");
            int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var weights = new double[2 * radius + 1];
            for (int i = -radius; i <= radius; i++) weights[i + radius] = Math.Exp(-((double)i * i) / (2 * sigma * sigma));
            return Separable(source, weights, cancellationToken);
        }

        /// <summary>Box (mean) smoothing over a (2·<paramref name="radius"/>+1)² window, separably and no-data aware.</summary>
        public static Raster BoxMean(Raster source, int radius, CancellationToken cancellationToken = default)
        {
            if (radius < 1 || radius > (int.MaxValue - 1) / 2) throw new ArgumentOutOfRangeException(nameof(radius), "The radius must be at least 1.");
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

        /// <summary>Sobel gradient magnitude, √(gx² + gy²), in rise per cell: high on steep slopes, 0 on horizontal surfaces.</summary>
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
            new Raster(source.Width, source.Height, double.NaN);

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
