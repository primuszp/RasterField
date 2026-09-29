using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RasterField.ErMapper;
using RasterField.Gdal;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class RasterChangeAnalysisTests
    {
        private static readonly float[] FlatTens = { 10f, 10f, 10f, 10f };
        private static readonly float[] ChangedValues = { 8f, 11f, 10f, 13f };
        private static readonly float[] Sequence = { 1f, 2f, 3f, 4f, 5f, 6f };
        private static readonly (double X, double Y)[] LeftColumn = { (0, 2), (1, 2), (1, 0), (0, 0) };

        [Fact]
        public void Computes_difference_threshold_area_and_cut_fill_volumes()
        {
            var first = new Raster(2, 2, FlatTens);
            var second = new Raster(2, 2, ChangedValues);
            var geo = new RasterGeoReference(2, 2, 100, 2, 0, 200, 0, -3);

            RasterChangeResult result = RasterChangeAnalysis.Compute(first, second, geo, absoluteThreshold: 1.5);

            Assert.Collection(result.Difference.Samples,
                value => Assert.Equal(-2f, value), value => Assert.Equal(1f, value),
                value => Assert.Equal(0f, value), value => Assert.Equal(3f, value));
            Assert.Equal(4, result.Count);
            Assert.Equal(-2, result.Minimum, 8);
            Assert.Equal(3, result.Maximum, 8);
            Assert.Equal(0.5, result.Mean, 8);
            Assert.Equal(2, result.ThresholdCellCount);
            Assert.Equal(6, result.CellArea, 8);
            Assert.Equal(12, result.ThresholdArea, 8);
            Assert.Equal(12, result.CutVolume, 8);
            Assert.Equal(24, result.FillVolume, 8);
            Assert.Equal(12, result.NetVolume, 8);
        }

        [Fact]
        public void Polygon_mask_only_includes_cell_centres_inside_zone()
        {
            var first = new Raster(3, 2, new float[6]);
            var second = new Raster(3, 2, Sequence);
            var geo = new RasterGeoReference(3, 2, 0, 1, 0, 2, 0, -1);

            RasterChangeResult result = RasterChangeAnalysis.Compute(first, second, geo, polygon: LeftColumn);

            Assert.Equal(2, result.Count);
            Assert.Equal(5, result.FillVolume, 8);
            Assert.Equal(1f, result.Difference[0, 0]);
            Assert.Equal(4f, result.Difference[1, 0]);
            Assert.True(float.IsNaN(result.Difference[0, 1]));
        }

        [Fact]
        public void Streaming_summary_matches_materialised_change_analysis()
        {
            var first = new Raster(37, 29, noDataValue: -9999);
            var second = new Raster(37, 29, noDataValue: -9999);
            for (int row = 0; row < first.Height; row++)
            {
                for (int col = 0; col < first.Width; col++)
                {
                    first[row, col] = col + row * 0.5f;
                    second[row, col] = first[row, col] + (col % 5 - 2) * 0.75f + row * 0.02f;
                }
            }
            first[8, 9] = -9999;
            second[17, 22] = -9999;
            var geo = new RasterGeoReference(37, 29, 0, 1, 0, 29, 0, -1);
            var polygon = new[] { (3.0, 26.0), (31.0, 24.0), (33.0, 6.0), (7.0, 3.0) };
            RasterChangeResult expected = RasterChangeAnalysis.Compute(first, second, geo, 1.0, polygon);
            using var firstSource = new MemoryRasterSource(first);
            using var secondSource = new MemoryRasterSource(second);

            RasterChangeSummary actual = RasterChangeAnalysis.ComputeSummary(
                firstSource, secondSource, geo, 1.0, polygon, tileSize: 8);

            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.NoDataCount, actual.NoDataCount);
            Assert.Equal(expected.Minimum, actual.Minimum, 9);
            Assert.Equal(expected.Maximum, actual.Maximum, 9);
            Assert.Equal(expected.Mean, actual.Mean, 9);
            Assert.Equal(expected.StandardDeviation, actual.StandardDeviation, 8);
            Assert.Equal(expected.ThresholdCellCount, actual.ThresholdCellCount);
            Assert.Equal(expected.ThresholdArea, actual.ThresholdArea, 9);
            Assert.Equal(expected.CutVolume, actual.CutVolume, 8);
            Assert.Equal(expected.FillVolume, actual.FillVolume, 8);
            Assert.Equal(expected.NetVolume, actual.NetVolume, 8);
        }

        [Fact]
        public void Streaming_summary_can_be_cancelled_before_reading()
        {
            using var first = new MemoryRasterSource(new Raster(100, 100));
            using var second = new MemoryRasterSource(new Raster(100, 100));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => RasterChangeAnalysis.ComputeSummary(
                first, second, new RasterGeoReference(100, 100, 0, 1, 0, 0, 0, 1),
                cancellationToken: cancellation.Token));
        }

        [Fact]
        public void Materialised_comparison_can_be_cancelled_before_work_starts()
        {
            var first = new Raster(100, 100);
            var second = new Raster(100, 100);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => RasterChangeAnalysis.Compute(
                first, second, new RasterGeoReference(100, 100, 0, 1, 0, 0, 0, 1),
                cancellationToken: cancellation.Token));
        }

        [Fact]
        public void Grid_check_rejects_shifted_origin()
        {
            ErsDocument first = Document(new Raster(2, 2), 100);
            ErsDocument second = Document(new Raster(2, 2), 101);

            RasterGridCompatibility result = RasterGridCompatibility.Check(first, second);

            Assert.False(result.IsCompatible);
            Assert.Contains("origins", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GeoTiff_round_trip_preserves_samples_nodata_and_georeference()
        {
            string path = Path.Combine(Path.GetTempPath(), $"rasterfield-{Guid.NewGuid():N}.tif");
            try
            {
                var raster = new Raster(3, 2, new[] { 1f, 2f, -9999f, 4.5f, 5f, 6f }, -9999);
                ErsDocument original = ErsDocument.Create(raster, 17.25, 48.75, 0.5, 0.25,
                    projection: "EPSG:4326", datum: "WGS84");

                GeoTiffDataset.Save(original, path);
                Assert.Equal(Path.GetFullPath(path), original.HeaderPath);
                var opened = GeoTiffDataset.Open(path);
                try
                {
                    Assert.Null(opened.Source);
                    Assert.Equal(3, opened.Document.Band!.Width);
                    Assert.Equal(2, opened.Document.Band.Height);
                    Assert.Equal(raster.Samples, opened.Document.Band.Samples);
                    Assert.Equal(-9999, opened.Document.Band.NoDataValue, 8);
                    Assert.Equal("EPSG:4326", opened.Document.Header.CoordinateSpace.Projection);
                    Assert.Equal(original.GeoReference.GeoTransform, opened.Document.GeoReference.GeoTransform);
                }
                finally
                {
                    opened.Source?.Dispose();
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task Gdal_source_serialises_concurrent_window_reads()
        {
            string path = Path.Combine(Path.GetTempPath(), $"rasterfield-concurrent-{Guid.NewGuid():N}.tif");
            try
            {
                var raster = new Raster(80, 60);
                for (int row = 0; row < raster.Height; row++)
                    for (int column = 0; column < raster.Width; column++)
                        raster[row, column] = row * 1000 + column;
                GeoTiffDataset.Save(ErsDocument.Create(raster, 0, 60, 1, 1), path);

                using var source = new GdalRasterSource(path);
                Task<Raster>[] reads = Enumerable.Range(0, 12)
                    .Select(i => Task.Run(() => source.ReadWindow(i, i + 2, 20, 15)))
                    .ToArray();
                Raster[] results = await Task.WhenAll(reads);

                for (int i = 0; i < results.Length; i++)
                {
                    Assert.Equal((i + 2) * 1000 + i, results[i][0, 0]);
                    Assert.Equal((i + 16) * 1000 + i + 19, results[i][14, 19]);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static ErsDocument Document(Raster raster, double originX) =>
            ErsDocument.Create(raster, originX, 200, 1, 1, projection: "EPSG:4326", datum: "WGS84");
    }
}
