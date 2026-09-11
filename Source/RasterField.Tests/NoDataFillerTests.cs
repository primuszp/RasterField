using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class NoDataFillerTests
    {
        private static Raster GradientWithHole(int size, int holeX, int holeY, int holeRadius)
        {
            var r = new Raster(size, size, noDataValue: -9999);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    r[y, x] = x; // simple horizontal gradient, easy to reason about

            for (int y = -holeRadius; y <= holeRadius; y++)
                for (int x = -holeRadius; x <= holeRadius; x++)
                {
                    int px = holeX + x, py = holeY + y;
                    if (px >= 0 && py >= 0 && px < size && py < size)
                        r[py, px] = -9999;
                }
            return r;
        }

        [Fact]
        public void CountNoData_and_HasNoData_report_the_gap()
        {
            var r = GradientWithHole(20, 10, 10, 2);
            Assert.True(NoDataFiller.HasNoData(r));
            Assert.True(NoDataFiller.CountNoData(r) > 0);
        }

        [Fact]
        public void FillNearest_leaves_valid_cells_untouched()
        {
            var r = GradientWithHole(20, 10, 10, 2);
            var filled = NoDataFiller.FillNearest(r);

            Assert.Equal(0f, filled[0, 0]);
            Assert.Equal(19f, filled[0, 19]);
            Assert.False(NoDataFiller.HasNoData(filled));
        }

        [Fact]
        public void FillNearest_fills_a_hole_with_a_plausible_neighbouring_value()
        {
            var r = GradientWithHole(20, 10, 10, 2);
            var filled = NoDataFiller.FillNearest(r);

            // the gradient at column 10 is 10; the nearest-neighbour fill must land close to it
            float center = filled[10, 10];
            Assert.InRange(center, 7, 13);
        }

        [Fact]
        public void IDW_fill_reproduces_a_linear_gradient_closely()
        {
            var r = GradientWithHole(20, 10, 10, 3);
            var filled = NoDataFiller.FillInverseDistanceWeighted(r, maxSearchDistance: 20, directions: 16);

            Assert.False(NoDataFiller.HasNoData(filled));
            // for a perfectly linear field, IDW from surrounding points should be very close to truth
            Assert.InRange(filled[10, 10], 9.0, 11.0);
            Assert.InRange(filled[8, 9], 7.5, 10.5);
        }

        /// <summary>
        /// A right-triangle footprint (valid where column &#8805; row) with a small hole punched
        /// well inside it — the standard fixture for exercising the convex-hull restriction:
        /// everything below/left of the hypotenuse is "outside the footprint" and must never be
        /// touched, while the punched hole is a genuine in-hull gap that must always end up filled.
        /// </summary>
        private static Raster TriangleWithHole(int size, double noData, int holeX, int holeY, int holeSize)
        {
            var r = new Raster(size, size, noData);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = (float)noData;

            for (int y = 0; y < size; y++)
                for (int x = y; x < size; x++)
                    r[y, x] = x;

            for (int y = holeY; y < holeY + holeSize; y++)
                for (int x = holeX; x < holeX + holeSize; x++)
                    r[y, x] = (float)noData;

            return r;
        }

        [Fact]
        public void FillNearest_fills_the_in_hull_hole_but_leaves_the_outside_footprint_untouched()
        {
            var r = TriangleWithHole(20, -9999, holeX: 14, holeY: 5, holeSize: 2);
            var filled = NoDataFiller.FillNearest(r);

            // inside the hull (the punched hole): now filled
            Assert.NotNull(filled.GetValueOrNull(5, 14));
            Assert.NotNull(filled.GetValueOrNull(6, 15));

            // outside the hull (below/left of the triangle's hypotenuse): left exactly as it was
            Assert.Null(filled.GetValueOrNull(10, 2));
            Assert.Null(filled.GetValueOrNull(19, 0));
        }

        [Fact]
        public void CountNoDataWithinHull_excludes_the_outside_footprint_background()
        {
            var r = TriangleWithHole(20, -9999, holeX: 14, holeY: 5, holeSize: 2);

            long total = NoDataFiller.CountNoData(r);
            long withinHull = NoDataFiller.CountNoDataWithinHull(r);

            Assert.Equal(4, withinHull); // just the 2x2 punched hole
            Assert.True(withinHull < total); // the triangle's background dwarfs the real gap
        }

        [Fact]
        public void IDW_fill_completes_every_in_hull_gap_even_when_it_exceeds_the_search_radius()
        {
            // A 5x5 hole with maxSearchDistance = 1: IDW's own rays cannot reach past it, so this
            // exercises the nearest-neighbour completeness backstop.
            var r = TriangleWithHole(30, -9999, holeX: 15, holeY: 10, holeSize: 5);
            var filled = NoDataFiller.FillInverseDistanceWeighted(r, maxSearchDistance: 1, directions: 8);

            for (int y = 10; y < 15; y++)
                for (int x = 15; x < 20; x++)
                    Assert.NotNull(filled.GetValueOrNull(y, x));

            // still outside the hull: untouched no matter how the fill was parameterised
            Assert.Null(filled.GetValueOrNull(25, 2));
        }

        [Fact]
        public void IDW_smoothing_pass_does_not_touch_originally_valid_cells()
        {
            var r = GradientWithHole(16, 8, 8, 2);
            var filled = NoDataFiller.FillInverseDistanceWeighted(r, maxSearchDistance: 16, smoothingIterations: 2);

            Assert.Equal(0f, filled[0, 0]);
            Assert.Equal(15f, filled[0, 15]);
        }

        [Fact]
        public void Original_raster_is_not_mutated()
        {
            var r = GradientWithHole(10, 5, 5, 1);
            long before = NoDataFiller.CountNoData(r);
            NoDataFiller.FillNearest(r);
            NoDataFiller.FillInverseDistanceWeighted(r);
            Assert.Equal(before, NoDataFiller.CountNoData(r));
        }
    }
}
