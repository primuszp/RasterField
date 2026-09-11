using System;
using System.IO;
using RasterField.ErMapper;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class RasterSourceTests
    {
        private static ErsHeader Header(int w, int h, int bands, ErsCellType type = ErsCellType.IEEE4ByteReal, ErsByteOrder order = ErsByteOrder.LsbFirst) =>
            new ErsHeader
            {
                DataSetType = ErsDataSetType.ErStorage,
                DataType = ErsDataType.Raster,
                ByteOrder = order,
                RasterInfo = new RasterInfo { CellType = type, NrOfLines = h, NrOfCellsPerLine = w, NrOfBands = bands },
            };

        private static MemoryStream BuildBil(int w, int h, int bands, Func<int, int, int, float> value)
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            for (int row = 0; row < h; row++)
                for (int b = 0; b < bands; b++)
                    for (int col = 0; col < w; col++)
                        bw.Write(value(row, col, b));
            ms.Position = 0;
            return ms;
        }

        [Fact]
        public void ReadWindow_matches_the_corresponding_region_of_a_full_read()
        {
            const int w = 40, h = 30;
            using var data = BuildBil(w, h, 1, (r, c, b) => r * 100 + c);

            var header = Header(w, h, 1);
            var full = BilRasterReader.ReadBand(new MemoryStream(data.ToArray()), header);

            using var src = RasterSource.FromStream(new MemoryStream(data.ToArray()), header);
            var window = src.ReadWindow(10, 5, 12, 8);

            Assert.Equal(12, window.Width);
            Assert.Equal(8, window.Height);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 12; x++)
                    Assert.Equal(full[5 + y, 10 + x], window[y, x]);
        }

        [Fact]
        public void ReadWindow_clamps_a_window_that_partially_overlaps()
        {
            const int w = 20, h = 20;
            using var data = BuildBil(w, h, 1, (r, c, b) => r * 100 + c);
            var header = Header(w, h, 1);
            using var src = RasterSource.FromStream(data, header);

            var window = src.ReadWindow(15, 15, 10, 10); // extends 5 past each edge
            Assert.Equal(5, window.Width);
            Assert.Equal(5, window.Height);
            Assert.Equal(15 * 100 + 15, window[0, 0]);
        }

        [Fact]
        public void ReadWindow_returns_empty_when_entirely_outside_bounds()
        {
            const int w = 10, h = 10;
            using var data = BuildBil(w, h, 1, (r, c, b) => 0);
            var header = Header(w, h, 1);
            using var src = RasterSource.FromStream(data, header);

            var window = src.ReadWindow(100, 100, 5, 5);
            Assert.Equal(0, window.Width);
            Assert.Equal(0, window.Height);
        }

        [Fact]
        public void ReadWindow_decimates_by_the_requested_step()
        {
            const int w = 20, h = 20;
            using var data = BuildBil(w, h, 1, (r, c, b) => r * 100 + c);
            var header = Header(w, h, 1);
            using var src = RasterSource.FromStream(data, header);

            var decimated = src.ReadWindow(0, 0, w, h, stepX: 4, stepY: 5);
            Assert.Equal(5, decimated.Width);  // 20/4
            Assert.Equal(4, decimated.Height); // 20/5
            Assert.Equal(0, decimated[0, 0]);
            Assert.Equal(0 * 100 + 4, decimated[0, 1]);   // column 4
            Assert.Equal(5 * 100 + 0, decimated[1, 0]);   // row 5
        }

        [Fact]
        public void ReadOverview_fits_within_the_requested_size_and_covers_the_whole_extent()
        {
            const int w = 1000, h = 400;
            using var data = BuildBil(w, h, 1, (r, c, b) => r + c);
            var header = Header(w, h, 1);
            using var src = RasterSource.FromStream(data, header);

            var overview = src.ReadOverview(100, 100);
            Assert.True(overview.Width <= 100 && overview.Height <= 100);
            Assert.True(overview.Width > 10 && overview.Height > 2); // not degenerately tiny

            // corners of the overview correspond to corners of the source
            Assert.Equal(0f, overview[0, 0]);
            var last = overview[overview.Height - 1, overview.Width - 1];
            Assert.True(last > 0);
        }

        [Fact]
        public void Separates_bands_correctly()
        {
            const int w = 8, h = 6, bands = 3;
            using var data = BuildBil(w, h, bands, (r, c, b) => b * 1000 + r * 10 + c);
            var header = Header(w, h, bands);
            using var src = RasterSource.FromStream(data, header);

            var band0 = src.ReadWindow(0, 0, w, h, band: 0);
            var band2 = src.ReadWindow(0, 0, w, h, band: 2);
            Assert.Equal(3 * 10 + 4, band0[3, 4]);
            Assert.Equal(2000 + 3 * 10 + 4, band2[3, 4]);
        }

        [Fact]
        public void Honours_big_endian_byte_order()
        {
            const int w = 4, h = 4;
            var ms = new MemoryStream();
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++)
                {
                    var bytes = BitConverter.GetBytes((float)(row * 10 + col));
                    if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                    ms.Write(bytes, 0, bytes.Length);
                }
            ms.Position = 0;

            var header = Header(w, h, 1, order: ErsByteOrder.MsbFirst);
            using var src = RasterSource.FromStream(ms, header);
            var window = src.ReadWindow(1, 1, 2, 2);
            Assert.Equal(11f, window[0, 0]); // row1,col1
        }

        [Fact]
        public void Maps_null_cell_value_through_to_the_result()
        {
            const int w = 3, h = 1;
            using var data = BuildBil(w, h, 1, (r, c, b) => c == 1 ? -9999f : c);
            var header = Header(w, h, 1);
            header.RasterInfo.NullCellValue = -9999;
            using var src = RasterSource.FromStream(data, header);

            var window = src.ReadWindow(0, 0, w, h);
            Assert.Null(window.GetValueOrNull(0, 1));
            Assert.Equal(0f, window[0, 0]);
        }

        [Fact]
        public void Throws_after_dispose()
        {
            const int w = 4, h = 4;
            using var data = BuildBil(w, h, 1, (r, c, b) => 0);
            var header = Header(w, h, 1);
            var src = RasterSource.FromStream(data, header, ownsStream: false);
            src.Dispose();
            Assert.Throws<ObjectDisposedException>(() => src.ReadWindow(0, 0, 1, 1));
        }

        [Fact]
        public void Open_reads_from_a_real_header_and_data_file_pair()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_src_" + Guid.NewGuid().ToString("N")));
            try
            {
                var band = new Raster(50, 40);
                for (int y = 0; y < 40; y++)
                    for (int x = 0; x < 50; x++)
                        band[y, x] = y * 100 + x;

                var doc = ErsDocument.Create(band, 0, 0, 1, 1);
                string ers = Path.Combine(dir.FullName, "big.ers");
                doc.Save(ers);

                using var src = RasterSource.Open(ers);
                Assert.Equal(50, src.Width);
                Assert.Equal(40, src.Height);

                var window = src.ReadWindow(10, 10, 5, 5);
                Assert.Equal(10 * 100 + 10, window[0, 0]);
            }
            finally { dir.Delete(recursive: true); }
        }
    }
}
