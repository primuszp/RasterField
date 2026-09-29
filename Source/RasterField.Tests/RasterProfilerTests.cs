using System;
using System.Collections.Generic;
using System.Threading;
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

        [Fact]
        public void Streaming_polyline_profile_matches_the_in_memory_result_using_bounded_windows()
        {
            var band = Ramp(300, 100);
            var doc = ErsDocument.Create(band, originX: 1000, originY: 2000, cellSizeX: 10, cellSizeY: 10);
            var geo = doc.GeoReference;
            var start = geo.PixelToWorld(2.25, 8.75);
            var bend = geo.PixelToWorld(155.5, 45.5);
            var end = geo.PixelToWorld(294.75, 88.25);
            var vertices = new[] { start, bend, end };

            IReadOnlyList<ProfileSample> expected = RasterProfiler.SamplePolylineWorld(band, geo, vertices, 17.0);
            using var source = new CountingSource(300, 100, (x, _) => x);
            IReadOnlyList<ProfileSample> actual = RasterProfiler.SamplePolylineWorld(
                source, geo, vertices, 17.0, tileSize: 32);

            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Distance, actual[i].Distance, 8);
                Assert.Equal(expected[i].X, actual[i].X, 8);
                Assert.Equal(expected[i].Y, actual[i].Y, 8);
                Assert.Equal(expected[i].Value, actual[i].Value);
            }
            Assert.True(source.ReadCount > 1);
            Assert.True(source.MaxRequestedCellCount <= 32 * 32);
        }

        [Fact]
        public void Streaming_polyline_profile_can_be_cancelled_before_reading()
        {
            using var source = new CountingSource(1000, 1000, (x, y) => x + y);
            var geo = ErsDocument.Create(new Raster(1, 1), 0, 0, 1, 1).GeoReference;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => RasterProfiler.SamplePolylineWorld(
                source, geo, new[] { (0.5, 0.5), (999.5, 999.5) }, 1.0,
                cancellationToken: cancellation.Token));
            Assert.Equal(0, source.ReadCount);
        }

        private sealed class CountingSource : IRasterSource
        {
            private readonly Func<int, int, float> _value;

            public CountingSource(int width, int height, Func<int, int, float> value)
            {
                Width = width;
                Height = height;
                _value = value;
            }

            public int Width { get; }
            public int Height { get; }
            public int BandCount => 1;
            public int ReadCount { get; private set; }
            public int MaxRequestedCellCount { get; private set; }

            public Raster ReadWindow(int x, int y, int width, int height, int stepX = 1, int stepY = 1, int band = 0)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(stepX, 1);
                ArgumentOutOfRangeException.ThrowIfLessThan(stepY, 1);
                ArgumentOutOfRangeException.ThrowIfNotEqual(band, 0);
                ReadCount++;
                MaxRequestedCellCount = Math.Max(MaxRequestedCellCount, Math.Max(0, width) * Math.Max(0, height));

                int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
                int x1 = Math.Min(Width, x + Math.Max(0, width));
                int y1 = Math.Min(Height, y + Math.Max(0, height));
                int outputWidth = Math.Max(0, (x1 - x0 + stepX - 1) / stepX);
                int outputHeight = Math.Max(0, (y1 - y0 + stepY - 1) / stepY);
                var raster = new Raster(outputWidth, outputHeight);
                for (int row = 0; row < outputHeight; row++)
                    for (int col = 0; col < outputWidth; col++)
                        raster.SetValueFast(row, col, _value(x0 + col * stepX, y0 + row * stepY));
                return raster;
            }

            public Raster ReadOverview(int maxWidth, int maxHeight, int band = 0)
            {
                int step = Math.Max(1, Math.Max(
                    (Width + maxWidth - 1) / maxWidth,
                    (Height + maxHeight - 1) / maxHeight));
                return ReadWindow(0, 0, Width, Height, step, step, band);
            }

            public void Dispose() { }
        }
    }
}
