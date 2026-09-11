using RasterField.ErMapper;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class RasterGeoReferenceTests
    {
        private static RasterGeoReference SampleGeo() =>
            RasterGeoReference.FromHeader(ErsHeader.Parse(SampleData.P0001Ers));

        [Fact]
        public void Top_left_corner_maps_to_registration_coordinate()
        {
            var geo = SampleGeo();
            var (x, y) = geo.PixelToWorld(0, 0);
            Assert.Equal(360000.0, x, 6);
            Assert.Equal(430000.0, y, 6);
        }

        [Fact]
        public void East_increases_with_column_north_decreases_with_row()
        {
            var geo = SampleGeo();
            var (x1, y1) = geo.PixelToWorld(1, 0);
            var (x2, y2) = geo.PixelToWorld(0, 1);

            Assert.Equal(361000.0, x1, 6);   // +1 column  -> +1000 m easting
            Assert.Equal(430000.0, y1, 6);
            Assert.Equal(360000.0, x2, 6);
            Assert.Equal(429000.0, y2, 6);   // +1 row     -> -1000 m northing
        }

        [Fact]
        public void WorldToCell_is_inverse_of_cell_centre()
        {
            var geo = SampleGeo();
            var (cx, cy) = geo.CellCentreToWorld(123, 77);
            var cell = geo.WorldToCell(cx, cy);

            Assert.NotNull(cell);
            Assert.Equal((123, 77), (cell!.Value.Column, cell.Value.Row));
        }

        [Fact]
        public void WorldToCell_returns_null_outside_image()
        {
            var geo = SampleGeo();
            Assert.Null(geo.WorldToCell(100.0, 100.0));
        }

        [Fact]
        public void World_bounds_cover_the_whole_image()
        {
            var geo = SampleGeo();
            var (minX, minY, maxX, maxY) = geo.WorldBounds();

            Assert.Equal(360000.0, minX, 6);
            Assert.Equal(360000.0 + 640 * 1000.0, maxX, 6);
            Assert.Equal(430000.0 - 450 * 1000.0, minY, 6);
            Assert.Equal(430000.0, maxY, 6);
        }
    }
}
