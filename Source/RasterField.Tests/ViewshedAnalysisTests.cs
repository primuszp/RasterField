using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class ViewshedAnalysisTests
    {
        private static Raster FlatPlane(int w, int h, float elevation)
        {
            var r = new Raster(w, h);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = elevation;
            return r;
        }

        [Fact]
        public void Every_cell_on_a_flat_plane_is_visible()
        {
            var elevation = FlatPlane(15, 15, 0f);
            var v = ViewshedAnalysis.Compute(elevation, observerCol: 7, observerRow: 7);

            for (int y = 0; y < 15; y++)
                for (int x = 0; x < 15; x++)
                    Assert.Equal(ViewshedAnalysis.Visible, v[y, x]);
        }

        [Fact]
        public void The_observers_own_cell_is_always_visible()
        {
            var elevation = FlatPlane(10, 10, 0f);
            var v = ViewshedAnalysis.Compute(elevation, 5, 5);
            Assert.Equal(ViewshedAnalysis.Visible, v[5, 5]);
        }

        [Fact]
        public void A_tall_wall_blocks_the_view_of_cells_behind_it()
        {
            // Observer at the west edge, a tall ridge running north-south a few cells east of it,
            // and a target cell further east, on the far side of the ridge.
            var elevation = FlatPlane(20, 5, 0f);
            for (int y = 0; y < 5; y++) elevation[y, 5] = 100f; // a wall at column 5

            var v = ViewshedAnalysis.Compute(elevation, observerCol: 0, observerRow: 2, observerHeight: 1.8, targetHeight: 1.8);

            // Just past the wall, close to the ground: blocked.
            Assert.Equal(ViewshedAnalysis.NotVisible, v[2, 10]);
            // Before the wall: visible.
            Assert.Equal(ViewshedAnalysis.Visible, v[2, 4]);
        }

        [Fact]
        public void Raising_the_observer_can_restore_visibility_behind_an_obstacle()
        {
            var elevation = FlatPlane(20, 5, 0f);
            for (int y = 0; y < 5; y++) elevation[y, 5] = 10f; // a modest ridge

            var low = ViewshedAnalysis.Compute(elevation, 0, 2, observerHeight: 0.1, targetHeight: 0.1);
            var high = ViewshedAnalysis.Compute(elevation, 0, 2, observerHeight: 50.0, targetHeight: 0.1);

            Assert.Equal(ViewshedAnalysis.NotVisible, low[2, 10]);
            Assert.Equal(ViewshedAnalysis.Visible, high[2, 10]);
        }

        [Fact]
        public void Cells_beyond_maxDistanceCells_are_reported_as_no_data()
        {
            var elevation = FlatPlane(50, 50, 0f);
            var v = ViewshedAnalysis.Compute(elevation, 25, 25, maxDistanceCells: 5);

            Assert.Equal(ViewshedAnalysis.Visible, v[25, 27]); // within range
            Assert.True(v.IsNoData(v[0, 0])); // far outside range
        }

        [Fact]
        public void A_no_data_target_cell_is_reported_as_no_data_not_a_visibility_verdict()
        {
            var elevation = FlatPlane(10, 10, 0f, noData: -9999);
            var v = ViewshedAnalysis.Compute(elevation, 0, 0);
            Assert.True(v.IsNoData(v[5, 5]));
        }

        private static Raster FlatPlane(int w, int h, float elevation, double noData)
        {
            var r = new Raster(w, h, noData);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = elevation;
            r[5, 5] = (float)noData;
            return r;
        }

        [Fact]
        public void Observer_on_a_no_data_cell_throws()
        {
            var r = new Raster(5, 5, noDataValue: -9999);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = -9999;
            Assert.Throws<System.ArgumentException>(() => ViewshedAnalysis.Compute(r, 2, 2));
        }

        [Fact]
        public void Observer_outside_the_raster_throws()
        {
            var elevation = FlatPlane(5, 5, 0f);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => ViewshedAnalysis.Compute(elevation, 10, 10));
        }

        [Fact]
        public void Output_dimensions_match_the_input()
        {
            var elevation = FlatPlane(8, 6, 0f);
            var v = ViewshedAnalysis.Compute(elevation, 4, 3);
            Assert.Equal((8, 6), (v.Width, v.Height));
        }
    }
}
