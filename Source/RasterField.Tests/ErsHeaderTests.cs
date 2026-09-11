using System;
using System.Text;
using RasterField.ErMapper;
using RasterField.ErMapper.Text;
using Xunit;

namespace RasterField.Tests
{
    public class ErsHeaderTests
    {
        [Fact]
        public void Parser_reads_block_tree()
        {
            ErsBlock root = ErsTextParser.Parse(SampleData.P0001Ers);
            ErsBlock header = Assert.Single(root.Blocks("DatasetHeader"));

            Assert.Equal("P_00_01.dat", header["DataFile"].AsString());
            Assert.NotNull(header.Block("CoordinateSpace"));
            Assert.NotNull(header.Block("RasterInfo"));
            Assert.NotNull(header.Block("RasterInfo")!.Block("CellInfo"));
            Assert.NotNull(header.Block("RasterInfo")!.Block("RegistrationCoord"));
        }

        [Fact]
        public void Typed_header_exposes_expected_values()
        {
            var h = ErsHeader.Parse(SampleData.P0001Ers);

            Assert.Equal(ErsDataSetType.ErStorage, h.DataSetType);
            Assert.Equal(ErsDataType.Raster, h.DataType);
            Assert.Equal(ErsByteOrder.LsbFirst, h.ByteOrder);

            Assert.Equal("EPSG:6237", h.CoordinateSpace.Datum);
            Assert.Equal("BMG:EOV", h.CoordinateSpace.Projection);
            Assert.Equal(ErsCoordinateType.En, h.CoordinateSpace.CoordinateType);
            Assert.Equal(0.0, h.CoordinateSpace.Rotation.Degrees);

            Assert.Equal(ErsCellType.IEEE4ByteReal, h.RasterInfo.CellType);
            Assert.Equal(450, h.RasterInfo.NrOfLines);
            Assert.Equal(640, h.RasterInfo.NrOfCellsPerLine);
            Assert.Equal(1, h.RasterInfo.NrOfBands);
            Assert.Equal(1000.0, h.RasterInfo.CellSizeX);
            Assert.Equal(1000.0, h.RasterInfo.CellSizeY);
            Assert.NotNull(h.RasterInfo.RegistrationCoord);
            Assert.Equal(360000.0, h.RasterInfo.RegistrationCoord!.X);
            Assert.Equal(430000.0, h.RasterInfo.RegistrationCoord!.Y);
            Assert.Equal(4, h.BytesPerSample);
        }

        [Fact]
        public void Comments_and_inline_hashes_are_ignored()
        {
            string text =
                "# a leading comment\n" +
                "DatasetHeader Begin\n" +
                "  DataType = Raster   # trailing comment\n" +
                "  Name = \"a # not a comment\"\n" +
                "DatasetHeader End\n";

            var h = ErsHeader.Parse(text);
            Assert.Equal(ErsDataType.Raster, h.DataType);
            Assert.Equal("a # not a comment", h.Name);
        }

        [Fact]
        public void ToErsText_round_trips_core_values()
        {
            var original = ErsHeader.Parse(SampleData.P0001Ers);
            string text = original.ToErsText();
            var reparsed = ErsHeader.Parse(text);

            Assert.Equal(original.RasterInfo.NrOfLines, reparsed.RasterInfo.NrOfLines);
            Assert.Equal(original.RasterInfo.NrOfCellsPerLine, reparsed.RasterInfo.NrOfCellsPerLine);
            Assert.Equal(original.RasterInfo.CellType, reparsed.RasterInfo.CellType);
            Assert.Equal(original.ByteOrder, reparsed.ByteOrder);
            Assert.Equal(original.CoordinateSpace.Projection, reparsed.CoordinateSpace.Projection);
            Assert.Equal(original.RasterInfo.RegistrationCoord!.X, reparsed.RasterInfo.RegistrationCoord!.X);
            Assert.Equal(original.RasterInfo.RegistrationCoord!.Y, reparsed.RasterInfo.RegistrationCoord!.Y);
        }

        [Fact]
        public void Parse_from_stream_handles_utf8_bom()
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(SampleData.P0001Ers);
            using var ms = new System.IO.MemoryStream(bytes);
            var h = ErsHeader.Parse(ms);
            Assert.Equal(640, h.RasterInfo.NrOfCellsPerLine);
        }

        [Theory]
        [InlineData("0:0:0.0", 0.0)]
        [InlineData("23:45:34.6", 23.7596111)]
        [InlineData("-1:30:0", -1.5)]
        [InlineData("12.5", 12.5)]
        public void Angle_parses_dms(string text, double expectedDegrees)
        {
            Assert.Equal(expectedDegrees, Angle.Parse(text).Degrees, 5);
        }
    }
}
