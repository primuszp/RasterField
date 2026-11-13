using System;

namespace RasterField.Interpolation
{
    internal static class Sampling
    {
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static SurfaceSample? CheckPosition(ISampleGrid grid, double x, double y)
        {
            if (!Finite(x) || !Finite(y)) return new SurfaceSample(null, SurfaceSampleFlags.InvalidCoordinate);
            if (grid.Width == 0 || grid.Height == 0 || x < 0 || y < 0 || x > grid.Width || y > grid.Height)
                return new SurfaceSample(null, SurfaceSampleFlags.OutsideDomain);
            return null;
        }

        internal static SurfaceSample Bilinear<T>(T grid, double x, double y, bool strict) where T : ISampleGrid
        {
            // Generic grid access keeps the legacy rendering/profile hot path allocation-free.
            if (!Finite(x) || !Finite(y)) return new SurfaceSample(null, SurfaceSampleFlags.InvalidCoordinate);
            if (grid.Width == 0 || grid.Height == 0 || x < 0 || y < 0 || x > grid.Width || y > grid.Height)
                return new SurfaceSample(null, SurfaceSampleFlags.OutsideDomain);
            double u = x - 0.5, v = y - 0.5;
            int c = (int)Math.Floor(u), r = (int)Math.Floor(v);
            double fx = u - c, fy = v - r;
            double weights = 0, sum = 0;
            var flags = SurfaceSampleFlags.None;
            Add(grid, c, r, (1 - fx) * (1 - fy), ref sum, ref weights, ref flags);
            Add(grid, c + 1, r, fx * (1 - fy), ref sum, ref weights, ref flags);
            Add(grid, c, r + 1, (1 - fx) * fy, ref sum, ref weights, ref flags);
            Add(grid, c + 1, r + 1, fx * fy, ref sum, ref weights, ref flags);
            if (weights == 0 || (strict && flags != SurfaceSampleFlags.None))
                return new SurfaceSample(null, flags);
            return new SurfaceSample(sum / weights,
                flags == SurfaceSampleFlags.None ? flags : flags | SurfaceSampleFlags.Renormalized);
        }

        private static void Add<T>(T grid, int x, int y, double weight,
            ref double sum, ref double weights, ref SurfaceSampleFlags flags) where T : ISampleGrid
        {
            if (weight <= 0) return;
            if ((uint)x >= (uint)grid.Width || (uint)y >= (uint)grid.Height)
            {
                flags |= SurfaceSampleFlags.IncompleteNeighborhood;
                return;
            }
            var value = grid.Read(x, y);
            if (!value.HasValue)
            {
                flags |= SurfaceSampleFlags.NoData | SurfaceSampleFlags.IncompleteNeighborhood;
                return;
            }
            sum += weight * value.Value;
            weights += weight;
        }

        internal static SurfaceSample BSpline(ISampleGrid grid, double x, double y)
        {
            var invalid = CheckPosition(grid, x, y);
            if (invalid.HasValue) return invalid.Value;
            double u = x - 0.5, v = y - 0.5;
            int c = (int)Math.Floor(u), r = (int)Math.Floor(v);
            if (c < 1 || r < 1 || c + 2 >= grid.Width || r + 2 >= grid.Height)
                return new SurfaceSample(null, SurfaceSampleFlags.IncompleteNeighborhood);
            double tx = u - c, ty = v - r, sum = 0;
            for (int j = 0; j < 4; j++)
            {
                double row = 0;
                for (int i = 0; i < 4; i++)
                {
                    var z = grid.Read(c + i - 1, r + j - 1);
                    if (!z.HasValue)
                        return new SurfaceSample(null, SurfaceSampleFlags.NoData | SurfaceSampleFlags.IncompleteNeighborhood);
                    row += Weight(i, tx) * z.Value;
                }
                sum += Weight(j, ty) * row;
            }
            return new SurfaceSample(sum);
        }

        private static double Weight(int i, double t)
        {
            double t2 = t * t, t3 = t2 * t;
            return i switch
            {
                0 => (1 - 3 * t + 3 * t2 - t3) / 6,
                1 => (4 - 6 * t2 + 3 * t3) / 6,
                2 => (1 + 3 * t + 3 * t2 - 3 * t3) / 6,
                _ => t3 / 6
            };
        }
    }
}
