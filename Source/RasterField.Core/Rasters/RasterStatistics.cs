using System;

namespace RasterField.Rasters
{
    /// <summary>Summary statistics of the valid (non no-data) samples of a <see cref="Raster"/>.</summary>
    public sealed class RasterStatistics
    {
        internal RasterStatistics(long count, double min, double max, double mean, double stdDev)
        {
            ValidCount = count;
            Minimum = min;
            Maximum = max;
            Mean = mean;
            StandardDeviation = stdDev;
        }

        /// <summary>Number of samples that were not no-data.</summary>
        public long ValidCount { get; }

        /// <summary>Smallest valid sample (0 when the raster has no valid samples).</summary>
        public double Minimum { get; }

        /// <summary>Largest valid sample (0 when the raster has no valid samples).</summary>
        public double Maximum { get; }

        /// <summary>Arithmetic mean of the valid samples.</summary>
        public double Mean { get; }

        /// <summary>Population standard deviation of the valid samples.</summary>
        public double StandardDeviation { get; }

        /// <summary>Difference between <see cref="Maximum"/> and <see cref="Minimum"/>.</summary>
        public double Range => Maximum - Minimum;

        /// <summary>
        /// A robust display range: <c>[mean - n*sd, mean + n*sd]</c> clamped to the data
        /// extent. Useful as a default stretch for palette colouring.
        /// </summary>
        public (double Min, double Max) SigmaRange(double sigma = 2.0)
        {
            double lo = Math.Max(Minimum, Mean - sigma * StandardDeviation);
            double hi = Math.Min(Maximum, Mean + sigma * StandardDeviation);
            if (hi <= lo) { lo = Minimum; hi = Maximum; }
            return (lo, hi);
        }

        /// <summary>Computes statistics over a raster in a single pass (Welford's algorithm).</summary>
        public static RasterStatistics Compute(Raster raster)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));

            float[] s = raster.Samples;
            long count = 0;
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            double mean = 0.0, m2 = 0.0;

            for (int i = 0; i < s.Length; i++)
            {
                float v = s[i];
                if (raster.IsNoData(v)) continue;

                count++;
                if (v < min) min = v;
                if (v > max) max = v;

                double delta = v - mean;
                mean += delta / count;
                m2 += delta * (v - mean);
            }

            if (count == 0)
                return new RasterStatistics(0, 0, 0, 0, 0);

            double variance = count > 1 ? m2 / count : 0.0;
            return new RasterStatistics(count, min, max, mean, Math.Sqrt(variance));
        }
    }
}
