using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class HydrologyAnalysisTests
    {
        /// <summary>A single-direction ramp: elevation strictly decreases eastward, so every cell should flow east (code 1).</summary>
        private static Raster EastDescendingRamp(int w, int h)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = (w - x) * 10f; // higher in the west, lower in the east
            return r;
        }

        [Fact]
        public void FlowDirection_points_downhill_along_a_simple_ramp()
        {
            var elevation = EastDescendingRamp(5, 5);
            var dir = HydrologyAnalysis.FlowDirection(elevation, 1, 1);

            // every interior cell flows east (code 1) — the only strictly downhill neighbour
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 4; x++)
                    Assert.Equal(1f, dir[y, x]);
        }

        [Fact]
        public void FlowDirection_marks_a_local_minimum_as_a_sink()
        {
            // a bowl: centre is the lowest cell, nothing to flow to.
            var r = new Raster(5, 5);
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 5; x++)
                    r[y, x] = (x - 2) * (x - 2) + (y - 2) * (y - 2);

            var dir = HydrologyAnalysis.FlowDirection(r, 1, 1);
            Assert.Equal(0f, dir[2, 2]); // the centre has no downhill neighbour
        }

        [Fact]
        public void FlowDirection_picks_the_single_steepest_downhill_neighbour()
        {
            // Centre cell surrounded by neighbours of varying height; the steepest drop must win.
            var r = new Raster(3, 3);
            r[0, 0] = 10; r[0, 1] = 10; r[0, 2] = 10;
            r[1, 0] = 10; r[1, 1] = 10; r[1, 2] = 1; // east neighbour: much lower
            r[2, 0] = 10; r[2, 1] = 5; r[2, 2] = 10; // south neighbour: lower, but less steep

            var dir = HydrologyAnalysis.FlowDirection(r, 1, 1);
            Assert.Equal(1f, dir[1, 1]); // east (code 1) beats south (code 4)
        }

        [Fact]
        public void FlowDirection_never_flows_to_a_no_data_or_out_of_bounds_neighbour()
        {
            var r = new Raster(3, 3, noDataValue: -9999);
            r[0, 0] = 10; r[0, 1] = 10; r[0, 2] = 10;
            r[1, 0] = 10; r[1, 1] = 10; r[1, 2] = -9999; // the only lower value is no-data
            r[2, 0] = 10; r[2, 1] = 10; r[2, 2] = 10;

            var dir = HydrologyAnalysis.FlowDirection(r, 1, 1);
            Assert.Equal(0f, dir[1, 1]); // no valid downhill neighbour -> sink
        }

        [Fact]
        public void FlowDirection_propagates_no_data_from_the_source_cell()
        {
            var r = new Raster(3, 3, noDataValue: -9999);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = 5;
            r[1, 1] = -9999;

            var dir = HydrologyAnalysis.FlowDirection(r, 1, 1);
            Assert.True(dir.IsNoData(dir[1, 1]));
        }

        [Fact]
        public void FlowAccumulation_counts_upstream_cells_along_a_simple_ramp()
        {
            var elevation = EastDescendingRamp(5, 1); // a single row, flowing straight east
            var dir = HydrologyAnalysis.FlowDirection(elevation, 1, 1);
            var acc = HydrologyAnalysis.FlowAccumulation(dir);

            // column 0 is the source (nothing flows into it): accumulation 0.
            // column k has k cells upstream of it (columns 0..k-1).
            for (int x = 0; x < 5; x++)
                Assert.Equal(x, acc[0, x]);
        }

        [Fact]
        public void FlowAccumulation_sums_two_converging_branches()
        {
            // A simple V-shaped valley: two ramps on row 0 and row 2 both draining into row 1,
            // which itself drains east. Sized so every interior slope is unambiguous.
            var r = new Raster(3, 3);
            r[0, 0] = 30; r[0, 1] = 20; r[0, 2] = 10;
            r[1, 0] = 25; r[1, 1] = 15; r[1, 2] = 5;
            r[2, 0] = 30; r[2, 1] = 20; r[2, 2] = 10;

            var dir = HydrologyAnalysis.FlowDirection(r, 1, 1);
            var acc = HydrologyAnalysis.FlowAccumulation(dir);

            // Hand-verified: (0,0), (1,0), (2,0), (0,1), (2,1), (0,2) and (2,2) are all pure
            // sources (nothing flows into them); (1,1) receives from the three sources upstream
            // of it ((1,0), (0,0) via SE, (2,0) via NE); (1,2) — the outlet — receives from (1,1)
            // plus the four remaining sources that all drain directly into it.
            Assert.Equal(0f, acc[1, 0]);
            Assert.Equal(0f, acc[0, 1]);
            Assert.Equal(0f, acc[2, 1]);
            Assert.Equal(0f, acc[0, 2]);
            Assert.Equal(0f, acc[2, 2]);
            Assert.Equal(3f, acc[1, 1]);
            Assert.Equal(8f, acc[1, 2]);
        }

        [Fact]
        public void FlowAccumulation_propagates_no_data()
        {
            var elevation = EastDescendingRamp(4, 4);
            var dir = HydrologyAnalysis.FlowDirection(elevation, 1, 1);
            dir[2, 2] = float.NaN;

            var acc = HydrologyAnalysis.FlowAccumulation(dir);
            Assert.True(acc.IsNoData(acc[2, 2]));
        }

        [Fact]
        public void Output_dimensions_match_the_input()
        {
            var elevation = EastDescendingRamp(6, 3);
            var dir = HydrologyAnalysis.FlowDirection(elevation, 1, 1);
            var acc = HydrologyAnalysis.FlowAccumulation(dir);

            Assert.Equal((6, 3), (dir.Width, dir.Height));
            Assert.Equal((6, 3), (acc.Width, acc.Height));
        }
    }
}
