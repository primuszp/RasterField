using System;
using System.Linq;
using System.IO;
using RasterField.Gdal;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class SharedAnalysisTests
    {
        private static Raster Pair(float first, float second) => new Raster(2, 1, new[] { first, second });

        private static RasterGeoReference Geo(int width, int height) => new RasterGeoReference(width, height, 0, 1, 0, 0, 0, 1);

        [Fact]
        public void Huge_enclosing_polygon_matches_whole_grid_in_all_analysis_paths()
        {
            var raster = new Raster(3, 3, Enumerable.Range(1, 9).Select(i => (float)i).ToArray());
            var zero = new Raster(3, 3);
            var polygon = new[] { (-1e20, -1e20), (1e20, -1e20), (1e20, 1e20), (-1e20, 1e20) };
            using var source = new MemoryRasterSource(raster);
            using var empty = new MemoryRasterSource(zero);
            var zone = ZonalStatistics.Compute(raster, Geo(3, 3), polygon);
            var tiledZone = ZonalStatistics.Compute(source, Geo(3, 3), polygon, tileSize: 2);
            var change = RasterChangeAnalysis.Compute(zero, raster, Geo(3, 3), polygon: polygon);
            var tiledChange = RasterChangeAnalysis.ComputeSummary(empty, source, Geo(3, 3), polygon: polygon, tileSize: 2);
            Assert.Equal(9, zone.Count);
            Assert.Equal(zone.Count, tiledZone.Count);
            Assert.Equal(zone.Count, change.Count);
            Assert.Equal(zone.Count, tiledChange.Count);
            Assert.Equal(45, zone.Sum);
            Assert.Equal(zone.Sum, change.FillVolume);
            Assert.Equal(zone.StandardDeviation, tiledChange.StandardDeviation, 10);
        }

        [Fact]
        public void Self_intersection_does_not_count_zero_width_scanline_intervals()
        {
            var raster = new Raster(3, 3, Enumerable.Repeat(1f, 9).ToArray());
            var polygon = new[] { (0.0, 0.0), (3.0, 3.0), (0.0, 3.0), (3.0, 0.0) };
            var zone = ZonalStatistics.Compute(raster, Geo(3, 3), polygon);
            var change = RasterChangeAnalysis.Compute(new Raster(3, 3), raster, Geo(3, 3), polygon: polygon);
            Assert.Equal(4, zone.Count);
            Assert.Equal(zone.Count, change.Count);
            Assert.True(change.Difference.IsNoData(change.Difference[1, 1]));
        }

        [Fact]
        public void Change_statistics_preserve_small_differences_on_a_large_offset()
        {
            var first = Pair(1f, 2f);
            var second = Pair(100000000f, 100000000f);
            using var a = new MemoryRasterSource(first);
            using var b = new MemoryRasterSource(second);
            foreach (var result in new RasterChangeSummary[] {
                RasterChangeAnalysis.Compute(first, second, Geo(2, 1)),
                RasterChangeAnalysis.ComputeSummary(a, b, Geo(2, 1), tileSize: 1) })
            {
                Assert.Equal(99999998.5, result.Mean, 8);
                Assert.Equal(0.5, result.StandardDeviation, 8);
            }
        }

        [Fact]
        public void Stream_graph_rejects_cycles_and_invalid_direction_codes()
        {
            var acc = Pair(1f, 2f);
            Assert.Throws<ArgumentException>(() => StreamNetwork.Extract(
                Pair(1f, 16f), acc, Geo(2, 1), 0));
            var invalid = Pair(1.25f, 0f);
            Assert.Throws<ArgumentException>(() => HydrologyAnalysis.FlowAccumulation(invalid));
            Assert.Throws<ArgumentException>(() => StreamNetwork.Extract(invalid, acc, Geo(2, 1), 0));
        }

        [Fact]
        public void Stream_order_depends_on_topology_even_when_accumulations_are_equal()
        {
            var dir = new Raster(4, 4);
            var acc = new Raster(4, 4);
            Array.Fill(dir.Samples, float.NaN);
            Array.Fill(acc.Samples, 16777216f); // float precision can collapse consecutive counts
            dir[1, 3] = 16; dir[1, 2] = 16;
            dir[3, 1] = 64; dir[2, 1] = 64;
            dir[1, 1] = 16; dir[1, 0] = 0;
            var streams = StreamNetwork.Extract(dir, acc, Geo(4, 4), 1);
            var downstream = Assert.Single(streams, stream => stream.Order == 2);
            Assert.Equal((1.5, 1.5), downstream.Points[0]);
            Assert.Equal((0.5, 1.5), downstream.Points[1]);
        }

        [Fact]
        public void Loaded_and_tiled_profiles_match_across_tile_edges_and_missing_cells()
        {
            var raster = new Raster(7, 5, Enumerable.Range(0, 35).Select(i => (float)i).ToArray());
            raster[2, 2] = float.NaN;
            var path = new[] { (0.0, 0.0), (6.5, 4.5), (0.5, 3.5) };
            using var source = new MemoryRasterSource(raster);
            var loaded = RasterProfiler.SamplePolylineWorld(raster, Geo(7, 5), path, 0.3);
            var streamed = RasterProfiler.SamplePolylineWorld(source, Geo(7, 5), path, 0.3, tileSize: 2);
            Assert.Equal(loaded, streamed);
            Assert.Null(RasterProfiler.BilinearSample(raster, double.NaN, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RasterProfiler.SamplePolylineWorld(raster, Geo(7, 5), path, double.PositiveInfinity));
        }
        [Fact]
        public void Cropping_with_numeric_no_data_keeps_the_outside_margin_missing()
        {
            var raster = new Raster(2, 2, noDataValue: -9999);
            Array.Fill(raster.Samples, 5);
            var crop = RasterClipper.Crop(raster, 1, 1, 3, 3);
            Assert.Equal(5, crop[0, 0]);
            Assert.True(crop.IsNoData(crop[2, 2]));
            Assert.Equal(1, crop.Statistics.ValidCount);
        }

        [Fact]
        public void All_source_adapters_clip_large_windows_without_integer_overflow()
        {
            var raster = new Raster(3, 3, Enumerable.Range(0, 9).Select(i => (float)i).ToArray());
            var doc = ErsDocument.Create(raster, 0, 0, 1, 1);
            string path = Path.Combine(Path.GetTempPath(), $"rasterfield-window-{Guid.NewGuid():N}.tif");
            try
            {
                GeoTiffDataset.Save(doc, path);
                using var bytes = new MemoryStream();
                BilRasterWriter.Write(bytes, doc.Header, new[] { raster });
                bytes.Position = 0;
                using var memory = new MemoryRasterSource(raster);
                using var bil = RasterSource.FromStream(bytes, doc.Header);
                using var tiff = new GdalRasterSource(path);
                foreach (var source in new IRasterSource[] { memory, bil, tiff })
                {
                    var window = source.ReadWindow(1, 1, int.MaxValue, int.MaxValue);
                    Assert.Equal((2, 2), (window.Width, window.Height));
                    Assert.Equal(4, window[0, 0]);
                    Assert.Equal(8, window[1, 1]);
                    var outside = source.ReadWindow(int.MaxValue, 0, 10, 1);
                    Assert.Equal((0, 0), (outside.Width, outside.Height));
                    var overview = source.ReadOverview(int.MaxValue, int.MaxValue);
                    Assert.Equal(raster.Samples, overview.Samples);
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

    }
}
