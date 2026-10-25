using System;

namespace RasterField.Rasters
{
    // Shared Welford/Chan accumulator for loaded, tiled and parallel analyses.
    internal sealed class SampleStatistics
    {
        public long Count { get; private set; }
        public double Minimum { get; private set; } = double.PositiveInfinity;
        public double Maximum { get; private set; } = double.NegativeInfinity;
        public double Sum { get; private set; }
        public double Mean { get; private set; }
        private double _m2;
        public double StandardDeviation => Count == 0 ? double.NaN : Math.Sqrt(Math.Max(0, _m2 / Count));

        public void Add(double value)
        {
            Count++;
            Minimum = Math.Min(Minimum, value);
            Maximum = Math.Max(Maximum, value);
            Sum += value;
            double delta = value - Mean;
            Mean += delta / Count;
            _m2 += delta * (value - Mean);
        }

        public void Merge(SampleStatistics other)
        {
            if (other.Count == 0) return;
            if (Count == 0)
            {
                Count = other.Count; Minimum = other.Minimum; Maximum = other.Maximum;
                Sum = other.Sum; Mean = other.Mean; _m2 = other._m2;
                return;
            }
            long combined = Count + other.Count;
            double delta = other.Mean - Mean;
            _m2 += other._m2 + delta * delta * Count * ((double)other.Count / combined);
            Mean += delta * other.Count / combined;
            Count = combined;
            Minimum = Math.Min(Minimum, other.Minimum);
            Maximum = Math.Max(Maximum, other.Maximum);
            Sum += other.Sum;
        }
    }
}
