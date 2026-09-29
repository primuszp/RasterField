using System.Linq;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class ContourGeneratorTests
    {
        // Identity georeference: world coordinates equal pixel coordinates, for easy assertions.
        private static readonly RasterGeoReference Identity = new RasterGeoReference(100, 100, 0, 1, 0, 0, 0, 1);

        private static Raster ColumnRamp(int w, int h)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = x;
            return r;
        }

        [Fact]
        public void A_vertical_ramp_produces_one_continuous_line_at_the_crossing_column()
        {
            var raster = ColumnRamp(5, 4); // values 0..4 across columns, 4 rows
            var lines = ContourGenerator.TraceLevel(raster, Identity, 2.5);

            var line = Assert.Single(lines);
            Assert.Equal(2.5, line.Level);
            Assert.Equal(4, line.Points.Count);

            // Samples sit at cell centres (x + 0.5), so 2.5 falls midway between the centres of
            // columns 2 and 3 — pixel coordinate 3.0 — spanning the first to the last row centre.
            foreach (var p in line.Points)
                Assert.Equal(3.0, p.X, 6);

            double minY = line.Points.Min(p => p.Y);
            double maxY = line.Points.Max(p => p.Y);
            Assert.Equal(0.5, minY, 6);
            Assert.Equal(3.5, maxY, 6);
        }

        [Fact]
        public void A_level_outside_the_data_range_produces_no_lines()
        {
            var raster = ColumnRamp(5, 4); // values range 0..4
            var lines = ContourGenerator.TraceLevel(raster, Identity, 100.0);
            Assert.Empty(lines);
        }

        [Fact]
        public void A_saddle_cell_produces_two_disjoint_segments_not_a_crossing_x()
        {
            var raster = new Raster(2, 2);
            raster[0, 0] = 0; raster[0, 1] = 10;  // tl, tr
            raster[1, 0] = 10; raster[1, 1] = 0;  // bl, br
            var lines = ContourGenerator.TraceLevel(raster, Identity, 5.0);

            Assert.Equal(2, lines.Count);
            foreach (var line in lines)
                Assert.Equal(2, line.Points.Count); // each is a single, unconnected segment
        }

        private static readonly double[] ThreeLevels = { 1.5, 2.5, 3.5 };

        [Fact]
        public void TraceLevels_traces_several_levels_in_one_pass()
        {
            var raster = ColumnRamp(5, 4);
            var lines = ContourGenerator.TraceLevels(raster, Identity, ThreeLevels);

            var levelsFound = lines.Select(l => l.Level).Distinct().OrderBy(v => v).ToArray();
            Assert.Equal(ThreeLevels, levelsFound);
        }

        [Fact]
        public void A_cell_touching_no_data_is_skipped()
        {
            var raster = ColumnRamp(5, 4);
            raster[1, 2] = float.NaN;
            var lines = ContourGenerator.TraceLevel(raster, Identity, 2.5);

            // The no-data cell removes some segments, but the level still exists elsewhere.
            Assert.NotEmpty(lines);
            foreach (var line in lines)
                foreach (var p in line.Points)
                    Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));
        }

        [Fact]
        public void Contour_coordinates_pass_through_the_georeference()
        {
            var raster = ColumnRamp(5, 4);
            var geo = new RasterGeoReference(5, 4, 1000, 10, 0, 5000, 0, -10); // origin (1000,5000), 10-unit cells

            var lines = ContourGenerator.TraceLevel(raster, geo, 2.5);
            var line = Assert.Single(lines);

            // Pixel x = 2.5 + 0.5 (cell-centre offset) → x = 1000 + 3.0*10 = 1030 on this vertical contour.
            foreach (var p in line.Points)
                Assert.Equal(1030.0, p.X, 6);
        }
    }
}
