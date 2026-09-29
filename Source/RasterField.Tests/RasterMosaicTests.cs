using System.Collections.Generic;
using System.Threading;
using RasterField.ErMapper;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class RasterMosaicTests
    {
        private static Raster Constant(int w, int h, float value)
        {
            var r = new Raster(w, h);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = value;
            return r;
        }

        // Two 10x5 tiles overlapping in columns [5,10): A at world x[0,10), B at world x[5,15).
        private static (MosaicSource A, MosaicSource B) OverlappingTiles(float valueA, float valueB)
        {
            var geoA = new RasterGeoReference(10, 5, 0, 1, 0, 0, 0, -1);
            var geoB = new RasterGeoReference(10, 5, 5, 1, 0, 0, 0, -1);
            return (new MosaicSource(Constant(10, 5, valueA), geoA), new MosaicSource(Constant(10, 5, valueB), geoB));
        }

        [Fact]
        public void Merge_produces_the_union_bounding_box()
        {
            var (a, b) = OverlappingTiles(1, 2);
            var (merged, geo) = RasterMosaic.Merge(new[] { a, b }, 1, 1);

            Assert.Equal(15, merged.Width);
            Assert.Equal(5, merged.Height);
            var (originX, originY) = geo.PixelToWorld(0, 0);
            Assert.Equal(0.0, originX, 6);
            Assert.Equal(0.0, originY, 6);
        }

        [Fact]
        public void Non_overlapping_regions_keep_their_single_source_value()
        {
            var (a, b) = OverlappingTiles(1, 2);
            var (merged, _) = RasterMosaic.Merge(new[] { a, b }, 1, 1, MosaicOverlapMode.LastWins);

            Assert.Equal(1f, merged[2, 2]);   // only covered by A
            Assert.Equal(2f, merged[2, 12]);  // only covered by B
        }

        [Fact]
        public void FirstWins_keeps_the_first_sources_value_in_the_overlap()
        {
            var (a, b) = OverlappingTiles(1, 2);
            var (merged, _) = RasterMosaic.Merge(new[] { a, b }, 1, 1, MosaicOverlapMode.FirstWins);

            Assert.Equal(1f, merged[2, 7]); // overlap column
        }

        [Fact]
        public void LastWins_lets_the_later_source_overwrite_the_overlap()
        {
            var (a, b) = OverlappingTiles(1, 2);
            var (merged, _) = RasterMosaic.Merge(new[] { a, b }, 1, 1, MosaicOverlapMode.LastWins);

            Assert.Equal(2f, merged[2, 7]); // overlap column
        }

        [Fact]
        public void Average_blends_both_sources_in_the_overlap()
        {
            var (a, b) = OverlappingTiles(1, 3);
            var (merged, _) = RasterMosaic.Merge(new[] { a, b }, 1, 1, MosaicOverlapMode.Average);

            Assert.Equal(2f, merged[2, 7]); // (1+3)/2
            Assert.Equal(1f, merged[2, 2]); // unaffected outside the overlap
        }

        [Fact]
        public void No_data_cells_do_not_contribute_to_an_average()
        {
            var geoA = new RasterGeoReference(4, 4, 0, 1, 0, 0, 0, -1);
            var geoB = new RasterGeoReference(4, 4, 0, 1, 0, 0, 0, -1); // fully overlapping
            var a = new Raster(4, 4, noDataValue: -9999);
            for (int i = 0; i < a.Samples.Length; i++) a.Samples[i] = -9999;
            a[1, 1] = 10; // only one real cell

            var b = Constant(4, 4, 20);

            var (merged, _) = RasterMosaic.Merge(
                new[] { new MosaicSource(a, geoA), new MosaicSource(b, geoB) }, 1, 1, MosaicOverlapMode.Average);

            Assert.Equal(20f, merged[0, 0]);   // a is no-data there -> only b contributes
            Assert.Equal(15f, merged[1, 1]);   // both contribute -> (10+20)/2
        }

        [Fact]
        public void ErsDocument_Mosaic_merges_two_loaded_documents()
        {
            var docA = ErsDocument.Create(Constant(10, 5, 1), originX: 0, originY: 0, cellSizeX: 1, cellSizeY: 1,
                cellType: ErsCellType.IEEE4ByteReal, byteOrder: ErsByteOrder.LsbFirst, projection: "BMG:EOV", datum: "EPSG:6237");
            var docB = ErsDocument.Create(Constant(10, 5, 2), originX: 5, originY: 0, cellSizeX: 1, cellSizeY: 1,
                cellType: ErsCellType.IEEE4ByteReal, byteOrder: ErsByteOrder.LsbFirst, projection: "BMG:EOV", datum: "EPSG:6237");

            var mosaic = ErsDocument.Mosaic(new[] { docA, docB }, 1, 1, MosaicOverlapMode.LastWins);

            Assert.Equal(15, mosaic.Band!.Width);
            Assert.Equal(5, mosaic.Band!.Height);
            Assert.Equal("BMG:EOV", mosaic.Header.CoordinateSpace.Projection);
            Assert.Equal(2f, mosaic.Band![2, 12]);
        }

        [Fact]
        public void Empty_source_list_throws()
        {
            Assert.Throws<System.ArgumentException>(() => RasterMosaic.Merge(new System.Collections.Generic.List<MosaicSource>(), 1, 1));
        }

        [Fact]
        public void Merge_can_be_cancelled_before_work_starts()
        {
            var (a, b) = OverlappingTiles(1, 2);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<System.OperationCanceledException>(() => RasterMosaic.Merge(
                new[] { a, b }, 1, 1, cancellationToken: cancellation.Token));
        }
    }
}
