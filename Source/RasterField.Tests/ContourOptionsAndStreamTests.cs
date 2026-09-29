using System;
using System.Linq;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class ContourOptionsAndStreamTests
    {
        private static readonly RasterGeoReference Identity = new RasterGeoReference(100, 100, 0, 1, 0, 0, 0, 1);
        private static readonly double[] TensToThirty = { 10.0, 20.0, 30.0 };
        private static readonly double[] FivesAroundZero = { -5.0, 0.0, 5.0 };

        private static Raster Cone(int size)
        {
            var r = new Raster(size, size);
            double c = size / 2.0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    r[y, x] = (float)(100 - Math.Sqrt((x + 0.5 - c) * (x + 0.5 - c) + (y + 0.5 - c) * (y + 0.5 - c)) * 5);
            return r;
        }

        [Fact]
        public void Build_levels_uses_whole_multiples_of_the_interval()
        {
            Assert.Equal(TensToThirty, ContourGenerator.BuildLevels(7, 30, 10));
            Assert.Equal(FivesAroundZero, ContourGenerator.BuildLevels(-7, 6, 5));
            Assert.Empty(ContourGenerator.BuildLevels(1, 2, 0));
        }

        [Fact]
        public void Index_contours_are_every_nth_level()
        {
            var lines = ContourGenerator.Trace(Cone(40), Identity, new ContourOptions { Minimum = 10, Maximum = 95, Interval = 10, IndexEvery = 5 });
            Assert.NotEmpty(lines);
            foreach (var line in lines)
                Assert.Equal(line.Level % 50 == 0, line.IsIndex);
            Assert.Contains(lines, l => l.IsIndex);
        }

        [Fact]
        public void Closed_rings_stay_closed_after_smoothing_and_gain_points()
        {
            var raw = ContourGenerator.Trace(Cone(40), Identity, new ContourOptions { Minimum = 80, Maximum = 80, Interval = 10 });
            var smooth = ContourGenerator.Trace(Cone(40), Identity, new ContourOptions { Minimum = 80, Maximum = 80, Interval = 10, SmoothingIterations = 2 });

            var r = Assert.Single(raw);
            var s = Assert.Single(smooth);
            Assert.True(r.IsClosed);
            Assert.True(s.IsClosed);
            Assert.True(s.Points.Count > r.Points.Count);
            Assert.True(s.Length < r.Length); // corner cutting only shortens a ring
        }

        [Fact]
        public void Chaikin_keeps_the_endpoints_of_an_open_line()
        {
            var pts = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) };
            var s = ContourGenerator.Chaikin(pts);
            Assert.Equal((0.0, 0.0), s[0]);
            Assert.Equal((10.0, 10.0), s[s.Count - 1]);
            Assert.Equal(4, s.Count);
        }

        [Fact]
        public void Short_lines_are_dropped_by_minimum_length()
        {
            var all = ContourGenerator.Trace(Cone(40), Identity, new ContourOptions { Minimum = 10, Maximum = 99, Interval = 1 });
            var filtered = ContourGenerator.Trace(Cone(40), Identity, new ContourOptions { Minimum = 10, Maximum = 99, Interval = 1, MinimumLength = 20 });
            Assert.True(filtered.Count < all.Count);
            Assert.All(filtered, l => Assert.True(l.Length >= 20));
        }

        [Fact]
        public void Stream_network_breaks_at_a_confluence_and_assigns_strahler_order()
        {
            // Main stream along row 2 flowing east; a tributary from (2,0) flows south into (2,2).
            var dir = new Raster(5, 5, float.NaN);
            var acc = new Raster(5, 5, float.NaN);
            for (int i = 0; i < 25; i++) { dir.Samples[i] = float.NaN; acc.Samples[i] = float.NaN; }
            void Set(int x, int y, int d, float a) { dir[y, x] = d; acc[y, x] = a; }
            Set(0, 2, 1, 1); Set(1, 2, 1, 2);
            Set(2, 0, 4, 1); Set(2, 1, 4, 2);
            Set(2, 2, 1, 5); Set(3, 2, 1, 6); Set(4, 2, 1, 7);

            var segs = StreamNetwork.Extract(dir, acc, Identity, threshold: 1);

            Assert.Equal(3, segs.Count);
            Assert.Equal(2, segs.Count(s => s.Order == 1));
            var main = Assert.Single(segs, s => s.Order == 2);
            Assert.Equal((2.5, 2.5), main.Points[0]);
            Assert.Equal((4.5, 2.5), main.Points[main.Points.Count - 1]);
            Assert.Equal(7, main.Accumulation);
            Assert.All(segs.Where(s => s.Order == 1), s => Assert.Equal((2.5, 2.5), s.Points[s.Points.Count - 1]));
        }

        [Fact]
        public void Stream_network_on_a_real_valley_is_connected()
        {
            // A tilted V-valley: every cell drains toward the centre column, which drains south.
            var dem = new Raster(21, 30);
            for (int y = 0; y < 30; y++)
                for (int x = 0; x < 21; x++)
                    dem[y, x] = (float)(Math.Abs(x - 10) * 2 + (30 - y) * 0.5);
            var dir = HydrologyAnalysis.FlowDirection(dem, 1, 1);
            var acc = HydrologyAnalysis.FlowAccumulation(dir);

            var segs = StreamNetwork.Extract(dir, acc, Identity, threshold: 20);
            Assert.NotEmpty(segs);
            Assert.All(segs, s => Assert.True(s.Points.Count >= 2));
            Assert.Contains(segs, s => s.Points.All(p => Math.Abs(p.X - 10.5) < 1e-9));
        }
    }
}
