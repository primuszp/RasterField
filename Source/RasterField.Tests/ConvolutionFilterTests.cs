using System;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class ConvolutionFilterTests
    {
        private static Raster Plane(int w, int h, Func<int, int, double> z, double noData = double.NaN)
        {
            var r = new Raster(w, h, noData);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r.SetValueFast(y, x, (float)z(x, y));
            r.InvalidateStatistics();
            return r;
        }

        [Fact]
        public void North_west_emboss_is_the_classic_integer_kernel()
        {
            var k = ConvolutionKernel.Emboss(315);
            Assert.Equal(new double[] { -2, -1, 0, -1, 0, 1, 0, 1, 2 }, k.Weights);
            Assert.Equal(0, k.Sum, 9);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        public void Emboss_returns_the_rise_per_cell_away_from_the_light(int size)
        {
            // Rises 2 per cell towards the east: light from the west sees it facing the light (+2),
            // light from the east sees it facing away (−2), light from the north sees no slope (0).
            var ramp = Plane(20, 20, (x, _) => 2.0 * x);
            Assert.Equal(2.0, ConvolutionFilter.Convolve(ramp, ConvolutionKernel.Emboss(270, size))[10, 10], 4);
            Assert.Equal(-2.0, ConvolutionFilter.Convolve(ramp, ConvolutionKernel.Emboss(90, size))[10, 10], 4);
            Assert.Equal(0.0, ConvolutionFilter.Convolve(ramp, ConvolutionKernel.Emboss(0, size))[10, 10], 4);
        }

        [Fact]
        public void Edge_and_relief_filters_are_zero_on_a_flat_surface_and_smoothing_keeps_it()
        {
            var flat = Plane(12, 9, (_, _) => 231.5);
            foreach (var k in new[] { ConvolutionKernel.Emboss(315, 5), ConvolutionKernel.Laplace(), ConvolutionKernel.Laplace(true) })
                Assert.All(ConvolutionFilter.Convolve(flat, k).Samples, v => Assert.Equal(0f, v, 4));
            Assert.All(ConvolutionFilter.Convolve(flat, ConvolutionKernel.Mean(5)).Samples, v => Assert.Equal(231.5f, v, 3));
            Assert.All(ConvolutionFilter.Convolve(flat, ConvolutionKernel.Sharpen()).Samples, v => Assert.Equal(231.5f, v, 3));
            Assert.All(ConvolutionFilter.Gaussian(flat, 2).Samples, v => Assert.Equal(231.5f, v, 3));
            Assert.All(ConvolutionFilter.LocalRelief(flat, 6).Samples, v => Assert.Equal(0f, v, 3));
        }

        [Fact]
        public void Laplace_is_zero_on_a_tilted_plane_and_positive_on_a_peak()
        {
            var tilted = Plane(9, 9, (x, y) => 3 * x - 2 * y);
            Assert.Equal(0, ConvolutionFilter.Convolve(tilted, ConvolutionKernel.Laplace())[4, 4], 4);

            var peak = Plane(9, 9, (x, y) => x == 4 && y == 4 ? 10 : 0);
            Assert.True(ConvolutionFilter.Convolve(peak, ConvolutionKernel.Laplace())[4, 4] > 0);
        }

        [Fact]
        public void Sobel_magnitude_is_the_gradient_per_cell()
        {
            var ramp = Plane(10, 10, (x, y) => 3 * x + 4 * y); // |∇| = 5 per cell
            Assert.Equal(5.0, ConvolutionFilter.SobelMagnitude(ramp)[5, 5], 4);
        }

        [Fact]
        public void Local_relief_removes_the_regional_slope_but_keeps_a_small_ditch()
        {
            // A 1-cell ditch 0.5 deep cut into a gentle slope.
            var surface = Plane(41, 41, (x, y) => 0.05 * x + (x == 20 ? -0.5 : 0));
            var lrm = ConvolutionFilter.LocalRelief(surface, 9);
            Assert.True(lrm[20, 20] < -0.35, $"ditch {lrm[20, 20]}");
            Assert.InRange(lrm[20, 5], -0.08, 0.08);
        }

        [Fact]
        public void No_data_centre_stays_no_data_and_gaps_are_filled_or_propagated_as_asked()
        {
            var r = Plane(7, 7, (_, _) => 5, noData: -9999);
            r.SetValueFast(3, 3, -9999);
            r.SetValueFast(3, 4, -9999);
            r.InvalidateStatistics();

            var filled = ConvolutionFilter.Convolve(r, ConvolutionKernel.Mean());
            Assert.True(float.IsNaN(filled[3, 3]));
            Assert.Equal(5f, filled[3, 2], 4);     // neighbour gap filled from the centre: no artificial step

            var strict = ConvolutionFilter.Convolve(r, ConvolutionKernel.Mean(), ConvolutionNoData.Propagate);
            Assert.True(float.IsNaN(strict[3, 2]));
            Assert.Equal(5f, strict[0, 0], 4);

            var gauss = ConvolutionFilter.Gaussian(r, 1.5);
            Assert.True(float.IsNaN(gauss[3, 3]));
            Assert.Equal(5f, gauss[3, 2], 4);      // renormalised around the gap
        }

        [Fact]
        public void Identity_matrix_parsed_with_decimal_commas_returns_the_input()
        {
            var k = ConvolutionKernel.Parse("0 0 0\n0 1,0 0\n0 0 0");
            var r = Plane(5, 5, (x, y) => x * 10 + y);
            var result = ConvolutionFilter.Convolve(r, k);
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 5; x++)
                    Assert.Equal(r[y, x], result[y, x], 4);
        }

        [Theory]
        [InlineData(10)]   // sequential rows
        [InlineData(100)]  // parallel rows
        public void A_cancelled_filter_stops_with_OperationCanceledException(int size)
        {
            var r = Plane(size, size, (x, y) => x + y);
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => ConvolutionFilter.Convolve(r, ConvolutionKernel.Emboss(315), cancellationToken: cts.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => ConvolutionFilter.LocalRelief(r, 5, cts.Token));
        }

        [Fact]
        public void Parse_round_trips_format_and_rejects_bad_matrices()
        {
            var k = ConvolutionKernel.Emboss(315, 5);
            var again = ConvolutionKernel.Parse(k.Format(), k.Divisor);
            Assert.Equal(k.Weights, again.Weights);

            Assert.Throws<FormatException>(() => ConvolutionKernel.Parse("1 2; 3 4"));      // even size
            Assert.Throws<FormatException>(() => ConvolutionKernel.Parse("1 2 3; 4 5; 6 7 8")); // ragged
            Assert.Throws<FormatException>(() => ConvolutionKernel.Parse("1 x 3; 4 5 6; 7 8 9"));
            Assert.Throws<ArgumentOutOfRangeException>(() => ConvolutionKernel.Parse("1", divisor: 0));
        }
    }
}
