using System;
using System.IO;
using RasterField.ErMapper;
using RasterField.Rasters;
using RasterField.Rendering;
using Xunit;

namespace RasterField.Tests
{
    /// <summary>
    /// End-to-end coverage for the "true-colour RGB composite" workflow: render a colour image,
    /// split it into three bands, save as a 3-band Unsigned8BitInteger .ers dataset (exactly what
    /// the app's "Swiss-style relief -> .ers" export does), reload it, and confirm both the pixel
    /// data and the composite re-render match the original image.
    /// </summary>
    public class RgbDatasetIntegrationTests
    {
        [Fact]
        public void A_rendered_image_round_trips_through_a_3_band_ers_dataset()
        {
            var image = new RasterImage(4, 3);
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    image.SetPixel(x, y, new ColorRgba((byte)(x * 20), (byte)(y * 40), (byte)(x + y * 4)));

            var (r, g, b) = image.ToRgbBands();

            var header = new ErsHeader
            {
                DataSetType = ErsDataSetType.ErStorage,
                DataType = ErsDataType.Raster,
                ByteOrder = ErsByteOrder.LsbFirst,
                CoordinateSpace = new CoordinateSpace { Datum = "RAW", Projection = "RAW", CoordinateType = ErsCoordinateType.Raw },
                RasterInfo = new RasterInfo
                {
                    CellType = ErsCellType.Unsigned8BitInteger,
                    NullCellValue = 0,
                    NrOfLines = 3,
                    NrOfCellsPerLine = 4,
                    NrOfBands = 3,
                },
            };
            header.RasterInfo.Bands.Add(new BandInfo { Value = "Red" });
            header.RasterInfo.Bands.Add(new BandInfo { Value = "Green" });
            header.RasterInfo.Bands.Add(new BandInfo { Value = "Blue" });

            var doc = ErsDocument.Create(header, new[] { r, g, b });

            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_rgb_" + Guid.NewGuid().ToString("N")));
            try
            {
                string path = Path.Combine(dir.FullName, "rgb.ers");
                doc.Save(path);

                var reloaded = ErsDocument.Load(path);
                Assert.Equal(3, reloaded.Bands.Count);
                Assert.Equal("Red", reloaded.Header.RasterInfo.Bands[0].Value);
                Assert.Equal("Green", reloaded.Header.RasterInfo.Bands[1].Value);
                Assert.Equal("Blue", reloaded.Header.RasterInfo.Bands[2].Value);

                var reRendered = RgbCompositeRenderer.Render(
                    reloaded.Bands[0], reloaded.Bands[1], reloaded.Bands[2], autoStretch: false);

                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        var original = image.GetPixel(x, y);
                        var final = reRendered.GetPixel(x, y);
                        Assert.Equal(original.R, final.R);
                        Assert.Equal(original.G, final.G);
                        Assert.Equal(original.B, final.B);
                    }
                }
            }
            finally { dir.Delete(recursive: true); }
        }
    }
}
