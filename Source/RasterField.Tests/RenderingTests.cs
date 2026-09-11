using System.IO;
using System.Text;
using RasterField.Rasters;
using RasterField.Rendering;
using Xunit;

namespace RasterField.Tests
{
    public class RenderingTests
    {
        [Fact]
        public void FromStops_hits_endpoints_and_midpoint()
        {
            var p = Palette.FromStops("bwr",
                (0.0, new ColorRgba(0, 0, 255)),
                (0.5, new ColorRgba(255, 255, 255)),
                (1.0, new ColorRgba(255, 0, 0)));

            Assert.Equal(new ColorRgba(0, 0, 255), p.Sample(0.0));
            Assert.Equal(new ColorRgba(255, 0, 0), p.Sample(1.0));

            var mid = p.Sample(0.5);
            Assert.InRange(mid.R, 250, 255);
            Assert.InRange(mid.G, 250, 255);
            Assert.InRange(mid.B, 250, 255);
        }

        [Fact]
        public void PaletteFile_reads_rgb_rows_and_skips_comment()
        {
            using var ms = new MemoryStream(Encoding.ASCII.GetBytes(SampleData.TinyPal));
            var p = PaletteFile.Read(ms, "tiny");

            Assert.Equal(new ColorRgba(0, 0, 0), p.Entries[0]);
            Assert.Equal(new ColorRgba(0, 0, 255), p.Entries[255]); // last row repeats to fill 256
        }

        [Fact]
        public void PaletteFile_round_trips_through_disk()
        {
            var original = BuiltInPalettes.Elevation;
            string path = Path.Combine(Path.GetTempPath(), "gevi_pal_" + System.Guid.NewGuid().ToString("N") + ".pal");
            try
            {
                PaletteFile.Save(original, path);
                var reloaded = PaletteFile.Load(path);
                for (int i = 0; i < Palette.Size; i++)
                    Assert.Equal(original.Entries[i], reloaded.Entries[i]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Colorizer_clamps_outside_the_range()
        {
            var c = new RasterColorizer(BuiltInPalettes.Grayscale, 0, 100);

            Assert.Equal(BuiltInPalettes.Grayscale.Sample(0.0), c.Map(-50.0));
            Assert.Equal(BuiltInPalettes.Grayscale.Sample(1.0), c.Map(9999.0));
        }

        [Fact]
        public void Colorizer_maps_no_data_to_its_own_colour()
        {
            var c = new RasterColorizer(BuiltInPalettes.Spectrum, 0, 10)
            {
                NoDataColor = new ColorRgba(1, 2, 3, 0),
            };
            Assert.Equal(new ColorRgba(1, 2, 3, 0), c.MapNullable(null));
        }

        [Fact]
        public void RasterImageRenderer_writes_bgra_with_transparent_no_data()
        {
            var raster = new Raster(2, 1, noDataValue: -1)
            {
                [0, 0] = 0f,
                [0, 1] = -1f, // no-data
            };
            var colorizer = new RasterColorizer(BuiltInPalettes.Grayscale, 0, 1)
            {
                NoDataColor = ColorRgba.Transparent,
            };

            RasterImage img = RasterImageRenderer.Render(raster, colorizer);

            Assert.Equal(2, img.Width);
            Assert.Equal(1, img.Height);
            Assert.Equal(8, img.Stride);
            Assert.Equal(0, img.GetPixel(1, 0).A);            // no-data -> transparent
            Assert.Equal(255, img.GetPixel(0, 0).A);          // valid   -> opaque

            // byte order is B, G, R, A
            var black = BuiltInPalettes.Grayscale.Sample(0.0);
            Assert.Equal(black.B, img.Pixels[0]);
            Assert.Equal(black.G, img.Pixels[1]);
            Assert.Equal(black.R, img.Pixels[2]);
            Assert.Equal(255, img.Pixels[3]);
        }

        [Fact]
        public void FromImage_reads_a_vertical_bgra_strip()
        {
            // 1 x 256 strip: entry i -> grey level i, BGRA
            int h = 256, stride = 4;
            var bytes = new byte[h * stride];
            for (int y = 0; y < h; y++)
            {
                bytes[y * stride + 0] = (byte)y;
                bytes[y * stride + 1] = (byte)y;
                bytes[y * stride + 2] = (byte)y;
                bytes[y * stride + 3] = 255;
            }

            var p = Palette.FromImage("strip", bytes, 1, h, stride);
            Assert.Equal(new ColorRgba(0, 0, 0), p.Entries[0]);
            Assert.Equal(new ColorRgba(255, 255, 255), p.Entries[255]);
        }

        [Fact]
        public void ToRgbBands_splits_pixels_into_three_matching_single_band_rasters()
        {
            var image = new RasterImage(2, 1);
            image.SetPixel(0, 0, new ColorRgba(10, 20, 30));
            image.SetPixel(1, 0, new ColorRgba(200, 150, 100));

            var (r, g, b) = image.ToRgbBands();

            Assert.Equal(10f, r[0, 0]); Assert.Equal(20f, g[0, 0]); Assert.Equal(30f, b[0, 0]);
            Assert.Equal(200f, r[0, 1]); Assert.Equal(150f, g[0, 1]); Assert.Equal(100f, b[0, 1]);
        }

        [Fact]
        public void ToRgbBands_maps_a_transparent_pixel_to_no_data_in_every_band()
        {
            var image = new RasterImage(1, 1);
            image.SetPixel(0, 0, ColorRgba.Transparent);

            var (r, g, b) = image.ToRgbBands();

            Assert.Null(r.GetValueOrNull(0, 0));
            Assert.Null(g.GetValueOrNull(0, 0));
            Assert.Null(b.GetValueOrNull(0, 0));
        }
    }
}
