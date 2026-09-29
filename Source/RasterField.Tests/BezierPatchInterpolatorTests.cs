using System;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class BezierPatchInterpolatorTests
    {
        private static Raster Build(int w, int h, Func<int, int, double> f, double noData = double.NaN)
        {
            var r = new Raster(w, h, noData);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = (float)f(x, y);
            return r;
        }

        // Plane in cell-centre coordinates: value = 3 + 2·(x+0.5) − 1.5·(y+0.5).
        private static double Plane(double px, double py) => 3 + 2 * px - 1.5 * py;

        [Theory]
        [InlineData(1.0)]
        [InlineData(0.5)]
        [InlineData(0.0)]
        public void A_plane_is_reproduced_exactly_for_any_tension(double tension)
        {
            var src = Build(6, 5, (x, y) => Plane(x + 0.5, y + 0.5));
            var fine = BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 4, Tension = tension });

            Assert.Equal(24, fine.Width);
            Assert.Equal(20, fine.Height);
            for (int y = 0; y < fine.Height; y++)
                for (int x = 0; x < fine.Width; x++)
                    Assert.Equal(Plane((x + 0.5) / 4, (y + 0.5) / 4), fine[y, x], 3);
        }

        [Fact]
        public void Factor_one_is_the_identity()
        {
            var src = Build(7, 4, (x, y) => Math.Sin(x) * 10 + y * y);
            var same = BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 1 });
            Assert.Equal(src.Samples, same.Samples);
        }

        [Fact]
        public void An_odd_factor_returns_the_original_value_at_every_source_cell_centre()
        {
            var src = Build(6, 6, (x, y) => Math.Cos(x * 0.7) * 20 + Math.Sin(y * 1.3) * 5);
            var fine = BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 3 });
            for (int y = 0; y < 6; y++)
                for (int x = 0; x < 6; x++)
                    Assert.Equal(src[y, x], fine[y * 3 + 1, x * 3 + 1], 4);
        }

        [Fact]
        public void The_surface_is_continuous_in_value_and_slope_across_a_patch_border()
        {
            var src = Build(8, 8, (x, y) => Math.Sin(x * 0.9) * 30 + Math.Cos(y * 0.6) * 12 + x * y);
            var o = new BezierPatchOptions();
            const double h = 1e-7;
            // The border between patches 2 and 3 in x is at cell centre 3 → pixel coordinate 3.5.
            double bx = 3.5, py = 4.3;
            double left = BezierPatchInterpolator.Sample(src, bx - h, py, o)!.Value;
            double right = BezierPatchInterpolator.Sample(src, bx + h, py, o)!.Value;
            Assert.True(Math.Abs(left - right) < 1e-3, $"value jump {left} vs {right}");

            double slopeLeft = (BezierPatchInterpolator.Sample(src, bx - h, py, o)!.Value - BezierPatchInterpolator.Sample(src, bx - 1e-3, py, o)!.Value) / (1e-3 - h);
            double slopeRight = (BezierPatchInterpolator.Sample(src, bx + 1e-3, py, o)!.Value - BezierPatchInterpolator.Sample(src, bx + h, py, o)!.Value) / (1e-3 - h);
            Assert.True(Math.Abs(slopeLeft - slopeRight) < 0.1, $"slope jump {slopeLeft} vs {slopeRight}");
        }

        [Fact]
        public void Monotone_mode_never_overshoots_a_step()
        {
            var src = Build(10, 3, (x, y) => x < 5 ? 0 : 100);
            var free = BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 8 });
            var mono = BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 8, Monotone = true });

            Assert.True(free.Statistics.Minimum < 0 || free.Statistics.Maximum > 100, "Catmull-Rom is expected to overshoot a step");
            Assert.True(mono.Statistics.Minimum >= 0);
            Assert.True(mono.Statistics.Maximum <= 100);
        }

        [Fact]
        public void No_data_cells_stay_no_data_and_neighbours_fall_back_to_bilinear()
        {
            var src = Build(6, 6, (x, y) => x == 2 && y == 2 ? -9999 : x + y, noData: -9999);
            var fine = BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 2 });

            // The no-data source cell's four sub-cells are all no-data.
            Assert.True(fine.IsNoData(fine[4, 4]));
            Assert.True(fine.IsNoData(fine[5, 5]));
            // A valid cell whose patch stencil reaches the hole falls back to bilinear, which still
            // reproduces the plane there (its own four bilinear corners are all valid).
            Assert.False(fine.IsNoData(fine[4, 8]));
            Assert.Equal((8.5 / 2 - 0.5) + (4.5 / 2 - 0.5), fine[4, 8], 3);
        }

        [Fact]
        public void Strict_no_data_mode_leaves_incomplete_patches_empty()
        {
            var src = Build(8, 8, (x, y) => x == 3 && y == 3 ? -9999 : x * y, noData: -9999);
            var fine = BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 2, NoData = BezierNoDataMode.NoData });
            Assert.True(fine.IsNoData(fine[8, 10])); // cell (5,4): its patches' stencils reach (3,3)
            Assert.False(fine.IsNoData(fine[14, 14])); // far corner untouched
        }

        [Fact]
        public void Subdivide_window_matches_the_same_region_of_a_full_subdivision()
        {
            var src = Build(12, 10, (x, y) => Math.Sin(x * 0.4) * 50 + y * 3);
            var o = new BezierPatchOptions { Factor = 3 };
            var full = BezierPatchInterpolator.Subdivide(src, o);
            var window = BezierPatchInterpolator.SubdivideWindow(RasterClipper.Crop(src, 2, 2, 8, 6), 2, o);

            Assert.Equal(12, window.Width);
            Assert.Equal(6, window.Height);
            for (int y = 0; y < window.Height; y++)
                for (int x = 0; x < window.Width; x++)
                    Assert.Equal(full[12 + y, 12 + x], window[y, x], 4);
        }

        [Fact]
        public void Invalid_options_are_rejected()
        {
            var src = Build(3, 3, (x, y) => x);
            Assert.Throws<ArgumentOutOfRangeException>(() => BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Factor = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => BezierPatchInterpolator.Subdivide(src, new BezierPatchOptions { Tension = 1.5 }));
        }

        [Fact]
        public void Document_subdivision_keeps_the_extent_and_divides_the_cell_size()
        {
            var band = Build(20, 15, (x, y) => x * 2.0 + y);
            var doc = ErsDocument.Create(band, originX: 650000, originY: 240000, cellSizeX: 50, cellSizeY: 40, projection: "BMG:EOV");

            var fine = doc.Subdivide(new BezierPatchOptions { Factor = 4 });

            Assert.Equal(80, fine.Header.RasterInfo.NrOfCellsPerLine);
            Assert.Equal(60, fine.Header.RasterInfo.NrOfLines);
            Assert.Equal(12.5, fine.Header.RasterInfo.CellSizeX, 9);
            Assert.Equal(10.0, fine.Header.RasterInfo.CellSizeY, 9);
            Assert.Equal(doc.GeoReference.WorldBounds(), fine.GeoReference.WorldBounds());
            Assert.Equal(ErMapper.ErsCellType.IEEE4ByteReal, fine.Header.RasterInfo.CellType);
        }

        [Fact]
        public void Document_window_subdivision_is_anchored_at_the_window_corner()
        {
            var band = Build(20, 15, (x, y) => x * 2.0 + y);
            var doc = ErsDocument.Create(band, originX: 1000, originY: 2000, cellSizeX: 10, cellSizeY: 10);

            var fine = doc.Subdivide(5, 3, 6, 4, new BezierPatchOptions { Factor = 2 });

            Assert.Equal(12, fine.Header.RasterInfo.NrOfCellsPerLine);
            Assert.Equal(8, fine.Header.RasterInfo.NrOfLines);
            var (x0, y0) = fine.GeoReference.PixelToWorld(0, 0);
            Assert.Equal(1050, x0, 9);
            Assert.Equal(1970, y0, 9);
            // Interior window cells see their true neighbours — equal to the full subdivision.
            var full = doc.Subdivide(new BezierPatchOptions { Factor = 2 });
            Assert.Equal(full.Bands[0][6, 10], fine.Bands[0][0, 0], 4);
        }
    }
}
