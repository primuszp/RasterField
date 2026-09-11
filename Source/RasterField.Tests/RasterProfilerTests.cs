using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class RasterProfilerTests
    {
        private static Raster Ramp(int w, int h)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = x; // pure horizontal ramp: value == column
            return r;
        }

        [Fact]
        public void BilinearSample_at_a_cell_centre_returns_the_exact_value()
        {
            var r = Ramp(10, 10);
            Assert.Equal(5f, RasterProfiler.BilinearSample(r, 5.5, 5.5));
        }

        [Fact]
        public void BilinearSample_interpolates_between_columns()
        {
            var r = Ramp(10, 10);
            // Halfway between column 4's and column 5's centres -> value 4.5.
            float? v = RasterProfiler.BilinearSample(r, 5.0, 5.5);
            Assert.NotNull(v);
            Assert.Equal(4.5f, v!.Value, 3);
        }

        [Fact]
        public void BilinearSample_outside_the_raster_is_null()
        {
            var r = Ramp(10, 10);
            Assert.Null(RasterProfiler.BilinearSample(r, -5, -5));
            Assert.Null(RasterProfiler.BilinearSample(r, 50, 50));
        }

        [Fact]
        public void BilinearSample_excludes_no_data_corners_from_the_average()
        {
            var r = new Raster(4, 4, noDataValue: -9999);
            r[0, 0] = 10; r[0, 1] = 10;
            r[1, 0] = -9999; r[1, 1] = -9999; // bottom corners are no-data

            // Sample dead centre of the 4 cells around (0.5..1, 0.5..1): only top corners contribute.
            float? v = RasterProfiler.BilinearSample(r, 1.0, 1.0);
            Assert.NotNull(v);
            Assert.Equal(10f, v!.Value, 3);
        }

        [Fact]
        public void BilinearSample_is_null_when_every_corner_is_no_data()
        {
            var r = new Raster(2, 2, noDataValue: -9999);
            r[0, 0] = -9999; r[0, 1] = -9999; r[1, 0] = -9999; r[1, 1] = -9999;
            Assert.Null(RasterProfiler.BilinearSample(r, 1.0, 1.0));
        }

        [Fact]
        public void Sample_returns_the_requested_number_of_evenly_spaced_points()
        {
            var r = Ramp(10, 10);
            var samples = RasterProfiler.Sample(r, 0.5, 5.5, 9.5, 5.5, sampleCount: 10);

            Assert.Equal(10, samples.Count);
            Assert.Equal(0.0, samples[0].Distance, 6);
            Assert.Equal(9.0, samples[^1].Distance, 6);
            // Values should be monotonically non-decreasing along the ramp.
            for (int i = 1; i < samples.Count; i++)
                Assert.True(samples[i].Value >= samples[i - 1].Value);
        }

        [Fact]
        public void SampleWorld_converts_through_the_georeference()
        {
            var band = Ramp(10, 10);
            var doc = ErsDocument.Create(band, originX: 1000, originY: 2000, cellSizeX: 10, cellSizeY: 10);
            var geo = doc.GeoReference;

            var (x0, y0) = geo.PixelToWorld(0.5, 5.5);
            var (x1, y1) = geo.PixelToWorld(9.5, 5.5);

            var samples = RasterProfiler.SampleWorld(band, geo, x0, y0, x1, y1, sampleCount: 5);

            Assert.Equal(5, samples.Count);
            Assert.Equal(0f, samples[0].Value);
            Assert.True(samples[^1].Value > samples[0].Value);
        }
    }
}
