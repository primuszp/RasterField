using System;
using System.IO;
using RasterField;
using RasterField.ErMapper;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class ClipTests
    {
        private static Raster Ramp(int w, int h)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = y * w + x;
            return r;
        }

        [Fact]
        public void RasterClipper_crops_the_requested_window()
        {
            var r = Ramp(10, 10);
            var cropped = RasterClipper.Crop(r, 2, 3, 4, 5);

            Assert.Equal(4, cropped.Width);
            Assert.Equal(5, cropped.Height);
            Assert.Equal(r[3, 2], cropped[0, 0]);
            Assert.Equal(r[7, 5], cropped[4, 3]);
        }

        [Fact]
        public void RasterClipper_clamps_a_window_that_only_partially_overlaps()
        {
            var r = Ramp(10, 10);
            var cropped = RasterClipper.Crop(r, 8, 8, 5, 5); // extends past the edge

            Assert.Equal(5, cropped.Width); // caller-requested size preserved
            Assert.Equal(r[8, 8], cropped[0, 0]);
            Assert.Null(cropped.GetValueOrNull(4, 4)); // outside the source -> no-data
        }

        [Fact]
        public void Document_Clip_reanchors_the_georeference()
        {
            var band = Ramp(20, 20);
            var doc = ErsDocument.Create(band, originX: 100000, originY: 500000, cellSizeX: 10, cellSizeY: 10);

            var clip = doc.Clip(x: 5, y: 4, width: 6, height: 6);

            Assert.Equal(6, clip.Band!.Width);
            Assert.Equal(6, clip.Band!.Height);
            Assert.Equal(band[4, 5], clip.Band![0, 0]);

            // cell (0,0) of the clip must sit at the same world point as cell (5,4) of the source
            var expected = doc.GeoReference.PixelToWorld(5, 4);
            var actual = clip.GeoReference.PixelToWorld(0, 0);
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);

            // cell size / world extent per cell must be unchanged
            var a = clip.GeoReference.PixelToWorld(1, 0);
            var b = clip.GeoReference.PixelToWorld(0, 0);
            Assert.Equal(10.0, a.X - b.X, 6);
        }

        [Fact]
        public void Document_Clip_clamps_an_out_of_bounds_window()
        {
            var band = Ramp(10, 10);
            var doc = ErsDocument.Create(band, 0, 0, 1, 1);

            var clip = doc.Clip(x: 7, y: 7, width: 10, height: 10);

            Assert.Equal(3, clip.Band!.Width);
            Assert.Equal(3, clip.Band!.Height);
        }

        [Fact]
        public void Document_ClipToWorldExtent_matches_pixel_clip()
        {
            var band = Ramp(20, 20);
            var doc = ErsDocument.Create(band, originX: 0, originY: 200, cellSizeX: 10, cellSizeY: 10);

            // world window covering cells [5..10) x [3..8) (row 0 at northing 200, decreasing)
            var (minX, _) = doc.GeoReference.PixelToWorld(5, 8);
            var (maxX, _) = doc.GeoReference.PixelToWorld(10, 3);
            var (_, topY) = doc.GeoReference.PixelToWorld(5, 3);
            var (_, botY) = doc.GeoReference.PixelToWorld(5, 8);

            var byExtent = doc.ClipToWorldExtent(minX, botY, maxX, topY);
            var byPixels = doc.Clip(5, 3, 5, 5);

            Assert.Equal(byPixels.Band!.Width, byExtent.Band!.Width);
            Assert.Equal(byPixels.Band!.Height, byExtent.Band!.Height);
            Assert.Equal(byPixels.Band![2, 2], byExtent.Band![2, 2]);
        }

        [Fact]
        public void Clipped_document_round_trips_through_save()
        {
            var band = Ramp(30, 30);
            var doc = ErsDocument.Create(band, 640000, 240000, 25, 25, ErsCellType.IEEE4ByteReal, ErsByteOrder.LsbFirst, "BMG:EOV", "EPSG:6237");
            var clip = doc.Clip(10, 10, 8, 8);

            var dir = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gevi_clip_" + System.Guid.NewGuid().ToString("N")));
            try
            {
                string path = System.IO.Path.Combine(dir.FullName, "clip.ers");
                clip.Save(path);

                var reloaded = ErsDocument.Load(path);
                Assert.Equal(8, reloaded.Band!.Width);
                Assert.Equal("BMG:EOV", reloaded.Header.CoordinateSpace.Projection);
                Assert.Equal(clip.Band![3, 3], reloaded.Band![3, 3]);
            }
            finally { dir.Delete(recursive: true); }
        }

        [Fact]
        public void Clip_works_in_streaming_mode_without_loading_the_raster()
        {
            var band = Ramp(60, 50);
            var source = ErsDocument.Create(band, 100000, 500000, 10, 10, ErsCellType.Signed32BitInteger);

            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_stream_" + Guid.NewGuid().ToString("N")));
            try
            {
                string path = Path.Combine(dir.FullName, "src.ers");
                source.Save(path);

                // header-only: never calls LoadRaster(), so Bands is empty
                var streamed = ErsDocument.LoadHeaderOnly(path);
                Assert.Empty(streamed.Bands);

                var clip = streamed.Clip(20, 15, 10, 8);

                Assert.Equal(10, clip.Band!.Width);
                Assert.Equal(8, clip.Band!.Height);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 10; x++)
                        Assert.Equal(band[15 + y, 20 + x], clip.Band![y, x]);

                var expectedOrigin = streamed.GeoReference.PixelToWorld(20, 15);
                var actualOrigin = clip.GeoReference.PixelToWorld(0, 0);
                Assert.Equal(expectedOrigin.X, actualOrigin.X, 6);
                Assert.Equal(expectedOrigin.Y, actualOrigin.Y, 6);
            }
            finally { dir.Delete(recursive: true); }
        }

        [Fact]
        public void IsLargeDataset_reflects_the_configured_threshold()
        {
            var original = ErsDocument.LargeDatasetCellThreshold;
            try
            {
                ErsDocument.LargeDatasetCellThreshold = 100;
                var small = ErsDocument.Create(Ramp(5, 5), 0, 0, 1, 1);
                var large = ErsDocument.Create(Ramp(20, 20), 0, 0, 1, 1);

                Assert.False(small.IsLargeDataset);
                Assert.True(large.IsLargeDataset);
            }
            finally { ErsDocument.LargeDatasetCellThreshold = original; }
        }
    }
}
