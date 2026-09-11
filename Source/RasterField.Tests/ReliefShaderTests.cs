using RasterField.Rasters;
using RasterField.Rendering;
using Xunit;

namespace RasterField.Tests
{
    public class ReliefShaderTests
    {
        private static Raster FlatPlane(int w, int h, float elevation)
        {
            var r = new Raster(w, h);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = elevation;
            return r;
        }

        private static Raster EastRisingRamp(int w, int h, double k)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = (float)(k * x);
            return r;
        }

        [Fact]
        public void MultidirectionalHillshade_of_a_flat_plane_is_uniform()
        {
            var elevation = FlatPlane(10, 10, 50f);
            var shade = ReliefShader.MultidirectionalHillshade(elevation, 1, 1);

            float first = shade[1, 1];
            for (int y = 1; y < 9; y++)
                for (int x = 1; x < 9; x++)
                    Assert.Equal(first, shade[y, x], 3);
        }

        [Fact]
        public void MultidirectionalHillshade_matches_the_plain_average_of_its_azimuths()
        {
            var elevation = EastRisingRamp(12, 12, 3.0);
            double[] azimuths = { 0, 90, 180, 270 };

            var multi = ReliefShader.MultidirectionalHillshade(elevation, 1, 1, azimuths: azimuths);

            double expected = 0;
            foreach (double az in azimuths)
                expected += TerrainAnalysis.Hillshade(elevation, 1, 1, az, 45.0)[6, 6];
            expected /= azimuths.Length;

            Assert.Equal(expected, multi[6, 6], 3);
        }

        [Fact]
        public void MultidirectionalHillshade_stays_within_the_0_to_255_range()
        {
            var elevation = EastRisingRamp(20, 20, 5.0);
            var shade = ReliefShader.MultidirectionalHillshade(elevation, 1, 1);

            for (int y = 1; y < 19; y++)
                for (int x = 1; x < 19; x++)
                {
                    Assert.True(shade[y, x] >= 0);
                    Assert.True(shade[y, x] <= 255);
                }
        }

        [Fact]
        public void RenderSwissStyle_is_uniform_grayscale_on_a_flat_plane_with_no_elevation_range()
        {
            var elevation = FlatPlane(10, 10, 100f);
            var image = ReliefShader.RenderSwissStyle(elevation, 1, 1, hazeStrength: 1.0);

            var first = image.GetPixel(1, 1);
            Assert.Equal(first.R, first.G); // no haze tint possible: min==max elevation
            Assert.Equal(first.G, first.B);

            for (int y = 1; y < 9; y++)
                for (int x = 1; x < 9; x++)
                {
                    var p = image.GetPixel(x, y);
                    Assert.Equal(first.R, p.R);
                    Assert.Equal(first.G, p.G);
                    Assert.Equal(first.B, p.B);
                }
        }

        [Fact]
        public void RenderSwissStyle_tints_higher_terrain_toward_the_haze_colour()
        {
            // A ramp rising eastward gives every column a different elevation, so higher columns
            // should sit closer to the (cool, pale) haze colour than lower ones.
            var elevation = EastRisingRamp(20, 5, 10.0);
            var haze = new ColorRgba(200, 220, 255);
            var image = ReliefShader.RenderSwissStyle(elevation, 1, 1, hazeStrength: 1.0, hazeColor: haze);

            var low = image.GetPixel(1, 2);
            var high = image.GetPixel(18, 2);

            // Distance to the haze colour should shrink as elevation rises.
            double DistToHaze(ColorRgba c) =>
                System.Math.Abs(c.R - haze.R) + System.Math.Abs(c.G - haze.G) + System.Math.Abs(c.B - haze.B);

            Assert.True(DistToHaze(high) < DistToHaze(low));
        }

        [Fact]
        public void RenderSwissStyle_makes_no_data_cells_fully_transparent()
        {
            var elevation = FlatPlane(6, 6, 10f);
            elevation[3, 3] = float.NaN;

            var image = ReliefShader.RenderSwissStyle(elevation, 1, 1);
            Assert.Equal(0, image.GetPixel(3, 3).A);
        }

        [Fact]
        public void Zero_haze_strength_produces_a_pure_grayscale_image()
        {
            var elevation = EastRisingRamp(15, 15, 4.0);
            var image = ReliefShader.RenderSwissStyle(elevation, 1, 1, hazeStrength: 0.0);

            var p = image.GetPixel(7, 7);
            Assert.Equal(p.R, p.G);
            Assert.Equal(p.G, p.B);
        }

        [Fact]
        public void Output_dimensions_match_the_input()
        {
            var elevation = FlatPlane(9, 5, 1f);
            var shade = ReliefShader.MultidirectionalHillshade(elevation, 1, 1);
            var image = ReliefShader.RenderSwissStyle(elevation, 1, 1);

            Assert.Equal((9, 5), (shade.Width, shade.Height));
            Assert.Equal((9, 5), (image.Width, image.Height));
        }
    }
}
