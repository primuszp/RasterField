using System;
using System.IO;
using PdfSharp.Pdf.IO;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public sealed class InspectionReportTests
    {
        [Fact]
        public void Writer_creates_summary_and_profile_pages()
        {
            Program.BuildAvaloniaApp().SetupWithoutStarting();
            string? retainedPath = Environment.GetEnvironmentVariable("RASTERFIELD_REPORT_TEST_OUTPUT");
            string path = retainedPath ?? Path.Combine(Path.GetTempPath(), $"rasterfield-report-{Guid.NewGuid():N}.pdf");
            try
            {
                var samples = new[]
                {
                    new ProfileSample(0, 0, 0, 10),
                    new ProfileSample(5, 5, 0, 12),
                    new ProfileSample(10, 10, 0, 11),
                };
                var data = new InspectionReportData
                {
                    Title = "Test raster - Raster inspection report",
                    CreatedAt = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
                    LayerName = "Test raster",
                    SourcePath = "test.ers",
                    Dimensions = "10 × 10 · 1 band",
                    Band = "Band 1",
                    CellType = "IEEE4ByteReal · LsbFirst",
                    CellSize = "1 × 1 METERS",
                    CoordinateReferenceSystem = "EPSG:23700",
                    NoDataValue = "-9999",
                    Lineage = "ΔZ: newer − baseline · threshold 0.1 m",
                    ValidCount = 99,
                    NoDataCount = 1,
                    Minimum = -2,
                    Maximum = 4,
                    Mean = 1,
                    StandardDeviation = 0.5,
                    DisplayMinimum = -1,
                    DisplayMaximum = 3,
                    ValueUnit = "m",
                    DistanceUnit = "m",
                    MapPng = Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
                    Palette = new[] { new ReportColor(0, 50, 180), new ReportColor(255, 255, 255), new ReportColor(190, 30, 35) },
                    ProfileSeries = new[] { new ReportProfileSeries("Elevation", samples, new ReportColor(20, 151, 147), false) },
                };

                InspectionReportWriter.Write(path, data);

                Assert.True(new FileInfo(path).Length > 10_000);
                using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.Equal(2, pdf.PageCount);
                Assert.Equal(data.Title, pdf.Info.Title);
            }
            finally
            {
                if (retainedPath == null && File.Exists(path)) File.Delete(path);
            }
        }
    }
}
