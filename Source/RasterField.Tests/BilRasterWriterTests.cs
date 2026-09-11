using System;
using System.IO;
using RasterField;
using RasterField.ErMapper;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class BilRasterWriterTests
    {
        private static Raster Ramp(int w, int h, int bandOffset = 0)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = bandOffset + y * w + x + 0.25f;
            return r;
        }

        private static ErsHeader Header(ErsCellType type, ErsByteOrder order, int w, int h, int bands)
        {
            return new ErsHeader
            {
                DataSetType = ErsDataSetType.ErStorage,
                DataType = ErsDataType.Raster,
                ByteOrder = order,
                RasterInfo = new RasterInfo
                {
                    CellType = type,
                    NrOfLines = h,
                    NrOfCellsPerLine = w,
                    NrOfBands = bands,
                },
            };
        }

        [Theory]
        [InlineData(ErsCellType.IEEE4ByteReal, ErsByteOrder.LsbFirst)]
        [InlineData(ErsCellType.IEEE4ByteReal, ErsByteOrder.MsbFirst)]
        [InlineData(ErsCellType.IEEE8ByteReal, ErsByteOrder.MsbFirst)]
        public void Float_round_trips_through_write_then_read(ErsCellType type, ErsByteOrder order)
        {
            const int w = 5, h = 4;
            var band = Ramp(w, h);
            var header = Header(type, order, w, h, 1);

            using var ms = new MemoryStream();
            BilRasterWriter.Write(ms, header, band);
            ms.Position = 0;
            var back = BilRasterReader.ReadBand(ms, header);

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Assert.Equal(band[y, x], back[y, x], 3);
        }

        [Fact]
        public void Integer_cell_types_round_and_clamp()
        {
            const int w = 3, h = 1;
            var band = new Raster(w, h) { [0, 0] = -5f, [0, 1] = 2.6f, [0, 2] = 999f };
            var header = Header(ErsCellType.Unsigned8BitInteger, ErsByteOrder.LsbFirst, w, h, 1);

            using var ms = new MemoryStream();
            BilRasterWriter.Write(ms, header, band);
            ms.Position = 0;
            var back = BilRasterReader.ReadBand(ms, header);

            Assert.Equal(0f, back[0, 0]);    // clamped up
            Assert.Equal(3f, back[0, 1]);    // rounded
            Assert.Equal(255f, back[0, 2]);  // clamped down
        }

        [Fact]
        public void Multiband_write_then_read_preserves_each_band()
        {
            const int w = 4, h = 3;
            var b0 = Ramp(w, h, 0);
            var b1 = Ramp(w, h, 1000);
            var header = Header(ErsCellType.Signed16BitInteger, ErsByteOrder.MsbFirst, w, h, 2);

            using var ms = new MemoryStream();
            BilRasterWriter.Write(ms, header, new[] { b0, b1 });
            ms.Position = 0;
            var back = BilRasterReader.ReadAllBands(ms, header);

            Assert.Equal(2, back.Length);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Assert.Equal(MathF.Round(b0[y, x]), back[0][y, x]);
                    Assert.Equal(MathF.Round(b1[y, x]), back[1][y, x]);
                }
        }

        [Fact]
        public void No_data_samples_are_written_as_the_null_cell_value()
        {
            const int w = 3, h = 1;
            var band = new Raster(w, h, noDataValue: -9999) { [0, 0] = 5f, [0, 1] = -9999f, [0, 2] = 7f };
            var header = Header(ErsCellType.IEEE4ByteReal, ErsByteOrder.LsbFirst, w, h, 1);
            header.RasterInfo.NullCellValue = -9999;

            using var ms = new MemoryStream();
            BilRasterWriter.Write(ms, header, band);
            ms.Position = 0;
            var back = BilRasterReader.ReadBand(ms, header);

            Assert.Null(back.GetValueOrNull(0, 1));
            Assert.Equal(5f, back[0, 0]);
        }

        [Fact]
        public void ErsDocument_Save_writes_a_readable_pair()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_" + Guid.NewGuid().ToString("N")));
            try
            {
                var band = Ramp(16, 12);
                var doc = ErsDocument.Create(band, originX: 640000, originY: 240000,
                    cellSizeX: 50, cellSizeY: 50, projection: "BMG:EOV", datum: "EPSG:6237");

                string ers = Path.Combine(dir.FullName, "out.ers");
                doc.Save(ers, new ErsSaveOptions { CellType = ErsCellType.IEEE4ByteReal, ByteOrder = ErsByteOrder.LsbFirst });

                Assert.True(File.Exists(ers));
                Assert.True(File.Exists(Path.Combine(dir.FullName, "out")));

                var reloaded = ErsDocument.Load(ers);
                Assert.Equal(16, reloaded.Band!.Width);
                Assert.Equal(12, reloaded.Band!.Height);
                Assert.Equal("BMG:EOV", reloaded.Header.CoordinateSpace.Projection);
                Assert.Equal(50.0, reloaded.Header.RasterInfo.CellSizeX);

                var (x, y) = reloaded.GeoReference.PixelToWorld(0, 0);
                Assert.Equal(640000, x, 3);
                Assert.Equal(240000, y, 3);

                for (int r = 0; r < 12; r++)
                    for (int c = 0; c < 16; c++)
                        Assert.Equal(band[r, c], reloaded.Band![r, c], 3);
            }
            finally { dir.Delete(recursive: true); }
        }

        [Fact]
        public void ErsDocument_Save_can_convert_the_cell_type()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_" + Guid.NewGuid().ToString("N")));
            try
            {
                var band = Ramp(8, 8);
                var doc = ErsDocument.Create(band, 0, 0, 1, 1);

                string ers = Path.Combine(dir.FullName, "as_int16.ers");
                doc.Save(ers, new ErsSaveOptions { CellType = ErsCellType.Signed16BitInteger });

                var reloaded = ErsDocument.Load(ers);
                Assert.Equal(ErsCellType.Signed16BitInteger, reloaded.Header.RasterInfo.CellType);
                Assert.Equal(MathF.Round(band[7, 7]), reloaded.Band![7, 7]);
            }
            finally { dir.Delete(recursive: true); }
        }
    }
}
