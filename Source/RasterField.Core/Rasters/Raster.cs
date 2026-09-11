using System;

namespace RasterField.Rasters
{
    /// <summary>
    /// A single-band, in-memory raster of <see cref="float"/> samples stored row-major
    /// (line 0 at the top). This is the evolution of the original <c>RasterField.Data.Raster</c>:
    /// a flat backing array (lower memory, cache friendly), explicit no-data handling and
    /// statistics computed on demand.
    /// </summary>
    public sealed class Raster
    {
        private readonly float[] _samples;
        private RasterStatistics? _statistics;

        /// <summary>Creates an all-zero raster of the given size.</summary>
        public Raster(int width, int height, double noDataValue = double.NaN)
        {
            if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            NoDataValue = noDataValue;
            _samples = new float[checked(width * height)];
        }

        /// <summary>Wraps an existing row-major sample array (no copy).</summary>
        public Raster(int width, int height, float[] samples, double noDataValue = double.NaN)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (samples.Length != checked(width * height))
                throw new ArgumentException("Sample count does not match width * height.", nameof(samples));

            Width = width;
            Height = height;
            NoDataValue = noDataValue;
            _samples = samples;
        }

        /// <summary>Number of columns (cells per line).</summary>
        public int Width { get; }

        /// <summary>Number of rows (lines).</summary>
        public int Height { get; }

        /// <summary>Alias for <see cref="Height"/> kept for compatibility with earlier code.</summary>
        public int Rows => Height;

        /// <summary>Alias for <see cref="Width"/> kept for compatibility with earlier code.</summary>
        public int Columns => Width;

        /// <summary>The value that represents "no data"; <see cref="double.NaN"/> when there is none.</summary>
        public double NoDataValue { get; }

        /// <summary>The raw row-major sample buffer.</summary>
        public float[] Samples => _samples;

        /// <summary>Indexer by row (line) and column (cell).</summary>
        public float this[int row, int column]
        {
            get => _samples[row * Width + column];
            set
            {
                _samples[row * Width + column] = value;
                _statistics = null;
            }
        }

        /// <summary><see langword="true"/> when the sample equals the no-data value (or is NaN).</summary>
        public bool IsNoData(float value) =>
            float.IsNaN(value) || (!double.IsNaN(NoDataValue) && value == (float)NoDataValue);

        /// <summary>Sample at (row, column), or <see langword="null"/> when it is no-data or out of range.</summary>
        public float? GetValueOrNull(int row, int column)
        {
            if ((uint)row >= (uint)Height || (uint)column >= (uint)Width) return null;
            float v = _samples[row * Width + column];
            return IsNoData(v) ? (float?)null : v;
        }

        /// <summary>Bulk setter used by loaders; does not invalidate statistics per-call.</summary>
        public void SetValueFast(int row, int column, float value) => _samples[row * Width + column] = value;

        /// <summary>Invalidates cached <see cref="Statistics"/> after a batch of <see cref="SetValueFast"/> calls.</summary>
        public void InvalidateStatistics() => _statistics = null;

        /// <summary>Min / max / mean / standard deviation over valid samples, computed once and cached.</summary>
        public RasterStatistics Statistics => _statistics ??= RasterStatistics.Compute(this);
    }
}
