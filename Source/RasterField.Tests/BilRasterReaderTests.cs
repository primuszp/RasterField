using System;
using System.IO;
using RasterField.ErMapper;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class BilRasterReaderTests
    {
        private static string Header(string cellType, string byteOrder, int lines, int cells, int bands, double? nullValue = null) =>
            "DatasetHeader Begin\n" +
            "  DataSetType = ERStorage\n" +
            "  DataType = Raster\n" +
            $"  ByteOrder = {byteOrder}\n" +
            "  CoordinateSpace Begin\n    Datum = \"RAW\"\n    Projection = \"RAW\"\n    CoordinateType = RAW\n    Rotation = 0:0:0.0\n  CoordinateSpace End\n" +
            "  RasterInfo Begin\n" +
            $"    CellType = {cellType}\n" +
            (nullValue.HasValue ? $"    NullCellValue = {nullValue.Value}\n" : "") +
            $"    NrOfLines = {lines}\n    NrOfCellsPerLine = {cells}\n    NrOfBands = {bands}\n" +
            "  RasterInfo End\n" +
            "DatasetHeader End\n";

        /// <summary>Writes samples in BIL order: for each line, for each band, all cells.</summary>
        private static byte[] BuildBil(float[][,] bands, int lines, int cells, Func<float, byte[]> encode)
        {
            using var ms = new MemoryStream();
            for (int row = 0; row < lines; row++)
                foreach (var band in bands)
                    for (int col = 0; col < cells; col++)
                        ms.Write(encode(band[row, col]), 0, encode(band[row, col]).Length);
            return ms.ToArray();
        }

        [Fact]
        public void Reads_single_band_float_little_endian()
        {
            const int lines = 3, cells = 4;
            var band = new float[lines, cells];
            for (int r = 0; r < lines; r++)
                for (int c = 0; c < cells; c++)
                    band[r, c] = r * 10 + c + 0.5f;

            var header = ErsHeader.Parse(Header("IEEE4ByteReal", "LSBFirst", lines, cells, 1));
            byte[] data = BuildBil(new[] { band }, lines, cells, v =>
            {
                var b = BitConverter.GetBytes(v);
                if (!BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            });

            using var ms = new MemoryStream(data);
            Raster raster = BilRasterReader.ReadBand(ms, header);

            Assert.Equal(cells, raster.Width);
            Assert.Equal(lines, raster.Height);
            Assert.Equal(21.5f, raster[2, 1]);
            Assert.Equal(0.5, raster.Statistics.Minimum, 3);
            Assert.Equal(23.5, raster.Statistics.Maximum, 3); // row 2, col 3 -> 2*10 + 3 + 0.5
        }

        [Fact]
        public void Honours_big_endian_byte_order()
        {
            const int lines = 2, cells = 2;
            var band = new float[lines, cells] { { 1f, 2f }, { 3f, 4f } };

            var header = ErsHeader.Parse(Header("IEEE4ByteReal", "MSBFirst", lines, cells, 1));
            byte[] data = BuildBil(new[] { band }, lines, cells, v =>
            {
                var b = BitConverter.GetBytes(v);
                if (BitConverter.IsLittleEndian) Array.Reverse(b); // store big-endian
                return b;
            });

            using var ms = new MemoryStream(data);
            Raster raster = BilRasterReader.ReadBand(ms, header);

            Assert.Equal(1f, raster[0, 0]);
            Assert.Equal(4f, raster[1, 1]);
        }

        [Fact]
        public void Separates_interleaved_bands()
        {
            const int lines = 2, cells = 3, bands = 2;
            var b0 = new float[lines, cells] { { 10, 11, 12 }, { 13, 14, 15 } };
            var b1 = new float[lines, cells] { { 20, 21, 22 }, { 23, 24, 25 } };

            var header = ErsHeader.Parse(Header("Signed16BitInteger", "LSBFirst", lines, cells, bands));
            byte[] data = BuildBil(new[] { b0, b1 }, lines, cells, v =>
            {
                var b = BitConverter.GetBytes((short)v);
                if (!BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            });

            using var ms = new MemoryStream(data);
            Raster[] rasters = BilRasterReader.ReadAllBands(ms, header);

            Assert.Equal(2, rasters.Length);
            Assert.Equal(12f, rasters[0][0, 2]);
            Assert.Equal(23f, rasters[1][1, 0]);
        }

        [Fact]
        public void Maps_null_cell_value_to_no_data()
        {
            const int lines = 1, cells = 3;
            var band = new float[lines, cells] { { 5f, -9999f, 7f } };

            var header = ErsHeader.Parse(Header("IEEE4ByteReal", "LSBFirst", lines, cells, 1, nullValue: -9999));
            byte[] data = BuildBil(new[] { band }, lines, cells, v =>
            {
                var b = BitConverter.GetBytes(v);
                if (!BitConverter.IsLittleEndian) Array.Reverse(b);
                return b;
            });

            using var ms = new MemoryStream(data);
            Raster raster = BilRasterReader.ReadBand(ms, header);

            Assert.Null(raster.GetValueOrNull(0, 1));
            Assert.Equal(7f, raster.GetValueOrNull(0, 2));
            Assert.Equal(2, raster.Statistics.ValidCount);
        }

        [Fact]
        public void Throws_when_data_is_truncated()
        {
            var header = ErsHeader.Parse(Header("IEEE4ByteReal", "LSBFirst", 10, 10, 1));
            using var ms = new MemoryStream(new byte[16]);
            Assert.Throws<EndOfStreamException>(() => BilRasterReader.ReadBand(ms, header));
        }
    }
}
