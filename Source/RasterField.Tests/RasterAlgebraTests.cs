using System.Collections.Generic;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class RasterAlgebraTests
    {
        private static Raster Constant(int w, int h, float value, double noData = double.NaN)
        {
            var r = new Raster(w, h, noData);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = value;
            return r;
        }

        [Fact]
        public void Evaluates_simple_arithmetic_over_two_bands()
        {
            var b1 = Constant(3, 3, 10);
            var b2 = Constant(3, 3, 4);
            var bands = new Dictionary<string, Raster> { ["b1"] = b1, ["b2"] = b2 };

            var result = RasterAlgebra.Evaluate("b1 + b2", bands);

            for (int i = 0; i < result.Samples.Length; i++)
                Assert.Equal(14f, result.Samples[i]);
        }

        [Fact]
        public void Computes_an_ndvi_style_normalized_difference()
        {
            var nir = Constant(2, 2, 0.5f);
            var red = Constant(2, 2, 0.1f);
            var bands = new Dictionary<string, Raster> { ["nir"] = nir, ["red"] = red };

            var result = RasterAlgebra.Evaluate("(nir - red) / (nir + red)", bands);

            Assert.Equal(4f / 6f, result[0, 0], 4);
        }

        [Fact]
        public void Respects_operator_precedence_and_parentheses()
        {
            var b1 = Constant(1, 1, 2);
            var bands = new Dictionary<string, Raster> { ["b1"] = b1 };

            Assert.Equal(14f, RasterAlgebra.Evaluate("2 + 3 * b1 * 2", bands)[0, 0]);
            Assert.Equal(20f, RasterAlgebra.Evaluate("(2 + 3) * b1 * 2", bands)[0, 0]);
        }

        [Fact]
        public void Supports_unary_minus_and_functions()
        {
            var b1 = Constant(1, 1, 4);
            var bands = new Dictionary<string, Raster> { ["b1"] = b1 };

            Assert.Equal(-4f, RasterAlgebra.Evaluate("-b1", bands)[0, 0]);
            Assert.Equal(2f, RasterAlgebra.Evaluate("sqrt(b1)", bands)[0, 0]);
            Assert.Equal(4f, RasterAlgebra.Evaluate("abs(-b1)", bands)[0, 0]);
            Assert.Equal(16f, RasterAlgebra.Evaluate("pow(b1, 2)", bands)[0, 0]);
            Assert.Equal(4f, RasterAlgebra.Evaluate("max(b1, 1)", bands)[0, 0]);
            Assert.Equal(1f, RasterAlgebra.Evaluate("min(b1, 1)", bands)[0, 0]);
        }

        [Fact]
        public void Supports_comparisons_and_iif()
        {
            var b1 = Constant(1, 1, 4);
            var bands = new Dictionary<string, Raster> { ["b1"] = b1 };

            Assert.Equal(1f, RasterAlgebra.Evaluate("b1 > 2", bands)[0, 0]);
            Assert.Equal(0f, RasterAlgebra.Evaluate("b1 < 2", bands)[0, 0]);
            Assert.Equal(99f, RasterAlgebra.Evaluate("iif(b1 > 2, 99, -1)", bands)[0, 0]);
            Assert.Equal(-1f, RasterAlgebra.Evaluate("iif(b1 < 2, 99, -1)", bands)[0, 0]);
        }

        [Fact]
        public void Supports_constants_pi_and_e()
        {
            var b1 = Constant(1, 1, 0);
            var bands = new Dictionary<string, Raster> { ["b1"] = b1 };

            Assert.Equal((float)System.Math.PI, RasterAlgebra.Evaluate("pi + b1", bands)[0, 0], 4);
            Assert.Equal((float)System.Math.E, RasterAlgebra.Evaluate("e + b1", bands)[0, 0], 4);
        }

        [Fact]
        public void No_data_in_any_referenced_band_propagates_to_the_output()
        {
            var b1 = new Raster(2, 2, noDataValue: -9999);
            b1[0, 0] = 5; b1[0, 1] = -9999; b1[1, 0] = 5; b1[1, 1] = 5;
            var b2 = Constant(2, 2, 1);
            var bands = new Dictionary<string, Raster> { ["b1"] = b1, ["b2"] = b2 };

            var result = RasterAlgebra.Evaluate("b1 / b2", bands);

            Assert.Equal(5f, result[0, 0]);
            Assert.True(result.IsNoData(result[0, 1]));
        }

        [Fact]
        public void An_unreferenced_bands_no_data_does_not_affect_the_result()
        {
            // b2 has a no-data cell, but the expression only uses b1 there.
            var b1 = Constant(2, 2, 7);
            var b2 = new Raster(2, 2, noDataValue: -9999);
            b2[0, 0] = -9999; b2[0, 1] = 1; b2[1, 0] = 1; b2[1, 1] = 1;
            var bands = new Dictionary<string, Raster> { ["b1"] = b1, ["b2"] = b2 };

            var result = RasterAlgebra.Evaluate("b1 * 2", bands);
            Assert.Equal(14f, result[0, 0]);
        }

        [Fact]
        public void Unknown_band_name_throws()
        {
            var bands = new Dictionary<string, Raster> { ["b1"] = Constant(1, 1, 1) };
            Assert.Throws<RasterAlgebraException>(() => RasterAlgebra.Evaluate("b1 + nosuch", bands));
        }

        [Fact]
        public void Malformed_expression_throws()
        {
            var bands = new Dictionary<string, Raster> { ["b1"] = Constant(1, 1, 1) };
            Assert.Throws<RasterAlgebraException>(() => RasterAlgebra.Evaluate("b1 + ", bands));
            Assert.Throws<RasterAlgebraException>(() => RasterAlgebra.Evaluate("(b1 + 1", bands));
        }

        [Fact]
        public void Mismatched_band_dimensions_throws()
        {
            var bands = new Dictionary<string, Raster>
            {
                ["b1"] = Constant(2, 2, 1),
                ["b2"] = Constant(3, 3, 1),
            };
            Assert.Throws<System.ArgumentException>(() => RasterAlgebra.Evaluate("b1 + b2", bands));
        }
    }
}
