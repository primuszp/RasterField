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

            var stats = new SampleStatistics();
            foreach (float value in raster.Samples)
                if (!raster.IsNoData(value)) stats.Add(value);
            return stats.Count == 0 ? new RasterStatistics(0, 0, 0, 0, 0)
                : new RasterStatistics(stats.Count, stats.Minimum, stats.Maximum, stats.Mean, stats.StandardDeviation);
        }
    }
}
