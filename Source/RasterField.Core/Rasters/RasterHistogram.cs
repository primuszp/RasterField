using System;

namespace RasterField.Rasters
{
    /// <summary>
    /// A fixed-bin histogram of a raster's valid samples, used to derive percentile-based
    /// display stretches.
    /// </summary>
    public sealed class RasterHistogram
    {
        private readonly long[] _bins;
        private readonly double _min;
        private readonly double _max;
        private readonly double _scale;

        private RasterHistogram(long[] bins, double min, double max, long total)
        {
            _bins = bins;
            _min = min;
            _max = max;
            _scale = max > min ? bins.Length / (max - min) : 0.0;
            Total = total;
        }

        /// <summary>Number of valid samples that went into the histogram.</summary>
        public long Total { get; }

        /// <summary>Number of bins.</summary>
        public int BinCount => _bins.Length;

        /// <summary>Builds a histogram over <paramref name="binCount"/> equal-width bins spanning the data range.</summary>
        public static RasterHistogram Build(Raster raster, int binCount = 1024)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (binCount < 2) binCount = 2;

            var stats = raster.Statistics;
            double min = stats.Minimum;
            double max = stats.Maximum;
            var bins = new long[binCount];

            if (max <= min)
                return new RasterHistogram(bins, min, max, stats.ValidCount);

            double scale = binCount / (max - min);
            float[] s = raster.Samples;
            long total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                float v = s[i];
                if (raster.IsNoData(v)) continue;
                int bin = (int)((v - min) * scale);
                if (bin < 0) bin = 0;
                else if (bin >= binCount) bin = binCount - 1;
                bins[bin]++;
                total++;
            }

            return new RasterHistogram(bins, min, max, total);
        }

        /// <summary>Approximate value at the given percentile (0-100) of the valid samples.</summary>
        public double Percentile(double percentile)
        {
            if (Total == 0 || _scale == 0.0) return _min;
            percentile = percentile < 0 ? 0 : percentile > 100 ? 100 : percentile;

            long target = (long)Math.Round(percentile / 100.0 * Total);
            long cumulative = 0;
            for (int i = 0; i < _bins.Length; i++)
            {
                cumulative += _bins[i];
                if (cumulative >= target)
                    return _min + (i + 0.5) / _scale;
            }
            return _max;
        }

        /// <summary>Convenience: the [<paramref name="low"/>, <paramref name="high"/>] percentile pair.</summary>
        public (double Min, double Max) PercentileRange(double low = 2.0, double high = 98.0)
        {
            double lo = Percentile(low);
            double hi = Percentile(high);
            if (hi <= lo) { lo = _min; hi = _max; }
            return (lo, hi);
        }
    }
}
