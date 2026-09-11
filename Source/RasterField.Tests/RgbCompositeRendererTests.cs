using RasterField.Rasters;
using RasterField.Rendering;
using Xunit;

namespace RasterField.Tests
{
    public class RgbCompositeRendererTests
    {
        private static Raster Constant(int w, int h, float value, double noData = double.NaN)
        {
            var r = new Raster(w, h, noData);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = value;
            return r;
        }

        [Fact]
        public void Without_auto_stretch_raw_0_255_values_pass_through_unchanged()
        {
            var r = Constant(2, 2, 10);
            var g = Constant(2, 2, 128);
            var b = Constant(2, 2, 250);

            var image = RgbCompositeRenderer.Render(r, g, b, autoStretch: false);
            var p = image.GetPixel(0, 0);

            Assert.Equal(10, p.R);
            Assert.Equal(128, p.G);
            Assert.Equal(250, p.B);
            Assert.Equal(255, p.A);
        }

        [Fact]
        public void With_auto_stretch_a_bands_own_min_and_max_map_to_0_and_255()
        {
            var r = new Raster(10, 1);
            for (int x = 0; x < 10; x++) r[0, x] = x; // 0..9, a clean linear ramp

            var flat = Constant(10, 1, 0);
            var image = RgbCompositeRenderer.Render(r, flat, flat);

            Assert.Equal(0, image.GetPixel(0, 0).R);
            Assert.Equal(255, image.GetPixel(9, 0).R);
        }

        [Fact]
        public void A_cell_that_is_no_data_in_every_band_renders_fully_transparent()
        {
            var r = new Raster(2, 1, noDataValue: -9999);
            var g = new Raster(2, 1, noDataValue: -9999);
            var b = new Raster(2, 1, noDataValue: -9999);
            r[0, 0] = 5; g[0, 0] = 5; b[0, 0] = 5;
            r[0, 1] = -9999; g[0, 1] = -9999; b[0, 1] = -9999;

            var image = RgbCompositeRenderer.Render(r, g, b, autoStretch: false);

            Assert.Equal(255, image.GetPixel(0, 0).A);
            Assert.Equal(0, image.GetPixel(1, 0).A);
        }

        [Fact]
        public void A_cell_that_is_no_data_in_only_one_band_still_renders_opaque()
        {
            // A shared no-data sentinel (0) must not blank out a pixel that is only
            // coincidentally zero in a single channel — e.g. a pixel that is genuinely pure black
            // in its red channel but has real green/blue data.
            var r = new Raster(1, 1, noDataValue: 0);
            var g = new Raster(1, 1, noDataValue: 0);
            var b = new Raster(1, 1, noDataValue: 0);
            r[0, 0] = 0; g[0, 0] = 200; b[0, 0] = 100;

            var image = RgbCompositeRenderer.Render(r, g, b, autoStretch: false);
            Assert.Equal(255, image.GetPixel(0, 0).A);
        }

        [Fact]
        public void A_perfectly_flat_band_does_not_throw_and_stays_at_zero()
        {
            // min == max: the percentile range collapses; must fall back gracefully rather than divide by zero.
            var flat = Constant(4, 4, 42);
            var image = RgbCompositeRenderer.Render(flat, flat, flat);
            Assert.Equal(0, image.GetPixel(0, 0).R);
        }

        [Fact]
        public void Mismatched_band_dimensions_throw()
        {
            var a = Constant(2, 2, 0);
            var b = Constant(3, 3, 0);
            Assert.Throws<System.ArgumentException>(() => RgbCompositeRenderer.Render(a, b, a));
        }

        [Fact]
        public void Output_dimensions_match_the_input_bands()
        {
            var r = Constant(7, 4, 1);
            var image = RgbCompositeRenderer.Render(r, r, r, autoStretch: false);
            Assert.Equal((7, 4), (image.Width, image.Height));
        }
    }
}
