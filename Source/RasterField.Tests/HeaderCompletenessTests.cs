using System;
using System.IO;
using System.Linq;
using RasterField;
using RasterField.ErMapper;
using Xunit;

namespace RasterField.Tests
{
    public class HeaderCompletenessTests
    {
        private const string RichHeader = @"DatasetHeader Begin
	Version = ""7.2""
	Name = ""complex.ers""
	DataFile = ""complex""
	SourceDataset = ""c:\img\src.tif""
	Comments = ""balanced 2019""
	DataSetType = ERStorage
	DataType = Raster
	ByteOrder = MSBFirst
	CoordinateSpace Begin
		Datum = ""NAD27""
		Projection = ""NUTM11""
		CoordinateType = EN
		Units = ""METERS""
		Rotation = 0:0:0.0
	CoordinateSpace End
	RasterInfo Begin
		CellType = Unsigned8BitInteger
		NullCellValue = 0
		CellInfo Begin
			Xdimension = 25
			Ydimension = 25
		CellInfo End
		NrOfLines = 500
		NrOfCellsPerLine = 400
		RegistrationCoord Begin
			Eastings = 484875.86
			Northings = 3620515.08
		RegistrationCoord End
		NrOfBands = 1
		BandId Begin
			Value = ""Red""
		BandId End
		SensorInfo Begin
			CameraManufacturer = ""Leica""
			FocalLength = 152.793
		SensorInfo End
		RegionInfo Begin
			Type = ""Polygon""
			RegionName = ""training1""
			SubRegion = { 33.6 55.6 219.3 68.4 100.0 12.5 }
			Stats Begin
				MinValue = { 12 }
			Stats End
		RegionInfo End
	RasterInfo End
	FFTInfo Begin
		ForwardTransform = FFT
		SpectrumAmount = Full
		SpatialDataset = ""orig.ers""
		PadCellX = 10
		PadCellY = 12
	FFTInfo End
DatasetHeader End
";

        [Fact]
        public void Lossless_write_preserves_unmodelled_blocks_and_scalars()
        {
            var first = ErsHeader.Parse(RichHeader);
            string text = first.ToErsText();
            var round = ErsHeader.Parse(text);

            // scalars that were previously dropped
            Assert.Equal(@"c:\img\src.tif", round.SourceDataset);
            Assert.Equal("balanced 2019", round.Comments);

            // whole blocks that were previously dropped
            var ri = round.RawBlock!.Block("RasterInfo")!;
            Assert.NotNull(ri.Block("SensorInfo"));
            Assert.Equal("Leica", ri.Block("SensorInfo")!["CameraManufacturer"].AsString());
            Assert.NotNull(ri.Block("RegionInfo"));
            Assert.NotNull(ri.Block("RegionInfo")!.Block("Stats"));
            Assert.NotNull(round.RawBlock!.Block("FFTInfo"));

            // still no duplication of the modelled ones
            Assert.Single(ri.Blocks("CellInfo"));
            Assert.Single(ri.Blocks("BandId"));
            Assert.Single(round.RawBlock!.Blocks("RasterInfo"));
        }

        [Fact]
        public void RegionInfo_is_exposed_as_a_typed_view()
        {
            var h = ErsHeader.Parse(RichHeader);
            var region = Assert.Single(h.RasterInfo.Regions);

            Assert.Equal("Polygon", region.Type);
            Assert.Equal("training1", region.RegionName);
            Assert.Equal(3, region.SubRegion.Count);
            Assert.Equal((33.6, 55.6), region.SubRegion[0]);
            Assert.Equal((100.0, 12.5), region.SubRegion[2]);
            Assert.NotNull(region.Stats);
        }

        [Fact]
        public void FftInfo_is_exposed_as_a_typed_view()
        {
            var h = ErsHeader.Parse(RichHeader);
            Assert.NotNull(h.FftInfo);
            Assert.Equal("FFT", h.FftInfo!.ForwardTransform);
            Assert.Equal("Full", h.FftInfo!.SpectrumAmount);
            Assert.Equal("orig.ers", h.FftInfo!.SpatialDataset);
            Assert.Equal(10, h.FftInfo!.PadCellX);
            Assert.Equal(12, h.FftInfo!.PadCellY);
        }

        [Theory]
        [InlineData("CoordinateType = LOCAL")]
        [InlineData("CoordinateSystem = LOCAL")]
        public void Unknown_coordinate_type_keyword_round_trips(string line)
        {
            string text =
                "DatasetHeader Begin\n  DataType = Raster\n  CoordinateSpace Begin\n" +
                "    Datum = \"RAW\"\n    Projection = \"RAW\"\n    " + line + "\n    Rotation = 0:0:0.0\n" +
                "  CoordinateSpace End\n  RasterInfo Begin\n    CellType = IEEE4ByteReal\n" +
                "    NrOfLines = 1\n    NrOfCellsPerLine = 1\n  RasterInfo End\nDatasetHeader End\n";

            var h = ErsHeader.Parse(text);
            Assert.Equal(ErsCoordinateType.None, h.CoordinateSpace.CoordinateType);
            Assert.Equal("LOCAL", h.CoordinateSpace.CoordinateTypeRaw);

            var round = ErsHeader.Parse(h.ToErsText());
            Assert.Equal("LOCAL", round.CoordinateSpace.CoordinateTypeRaw);
        }

        [Fact]
        public void CoordinateSystem_alias_is_read()
        {
            string text =
                "DatasetHeader Begin\n  CoordinateSpace Begin\n    CoordinateSystem = EN\n    Rotation = 0:0:0.0\n  CoordinateSpace End\n" +
                "  RasterInfo Begin\n    CellType = IEEE4ByteReal\n    NrOfLines = 1\n    NrOfCellsPerLine = 1\n  RasterInfo End\nDatasetHeader End\n";
            var h = ErsHeader.Parse(text);
            Assert.Equal(ErsCoordinateType.En, h.CoordinateSpace.CoordinateType);
        }

        [Fact]
        public void Effective_units_apply_the_documented_default()
        {
            var raw = ErsHeader.Parse(SampleData.P0001Ers).CoordinateSpace; // projected EOV, has Units? no
            // sample has no Units entry and CoordinateType = EN -> "natural"
            Assert.Null(raw.Units);
            Assert.Equal("natural", raw.EffectiveUnits);

            var rawSpace = new CoordinateSpace { CoordinateType = ErsCoordinateType.Raw };
            Assert.Equal("METERS", rawSpace.EffectiveUnits);

            var explicitUnits = new CoordinateSpace { Units = "U.S. SURVEY FOOT" };
            Assert.Equal("U.S. SURVEY FOOT", explicitUnits.EffectiveUnits);
        }

        [Fact]
        public void Save_stamps_name_and_timestamp_by_default()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_stamp_" + Guid.NewGuid().ToString("N")));
            try
            {
                var band = new Rasters.Raster(4, 4);
                var doc = ErsDocument.Create(band, 0, 0, 1, 1);
                var before = DateTime.UtcNow.AddSeconds(-2);

                string ers = Path.Combine(dir.FullName, "stamped.ers");
                doc.Save(ers);

                var reloaded = ErsHeader.Load(ers);
                Assert.Equal("stamped.ers", reloaded.Name);
                Assert.NotNull(reloaded.LastUpdated);
                Assert.True(reloaded.LastUpdated!.Value >= before);
            }
            finally { dir.Delete(recursive: true); }
        }

        [Fact]
        public void Save_can_opt_out_of_stamping()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_nostamp_" + Guid.NewGuid().ToString("N")));
            try
            {
                var band = new Rasters.Raster(4, 4);
                var doc = ErsDocument.Create(band, 0, 0, 1, 1);
                doc.Header.Name = "keep-me";

                string ers = Path.Combine(dir.FullName, "x.ers");
                doc.Save(ers, new ErsSaveOptions { UpdateName = false, UpdateTimestamp = false });

                var reloaded = ErsHeader.Load(ers);
                Assert.Equal("keep-me", reloaded.Name);
                Assert.Null(reloaded.LastUpdated);
            }
            finally { dir.Delete(recursive: true); }
        }

        [Theory]
        [InlineData("BMG:EOV", null, 23700)]
        [InlineData("EOV", "HD72", 23700)]
        [InlineData("NUTM33", "WGS84", 32633)]
        [InlineData("SUTM56", "WGS84", 32756)]
        [InlineData("TMAMG55", "AGD66", 20255)]
        [InlineData("MGA55", "GDA94", 28355)]
        [InlineData("EPSG:32633", null, 32633)]
        [InlineData("GEODETIC", "WGS84", 4326)]
        [InlineData("RAW", "NAD27", 4267)]
        public void ProjectionRegistry_resolves_common_names(string proj, string? datum, int expected)
        {
            Assert.True(ProjectionRegistry.TryGetEpsg(proj, datum, out int epsg));
            Assert.Equal(expected, epsg);
        }

        [Fact]
        public void ProjectionRegistry_returns_null_for_pure_raw()
        {
            Assert.Null(ProjectionRegistry.GetEpsg("RAW", "RAW"));
            Assert.Null(ProjectionRegistry.GetEpsg(null, null));
            Assert.Null(ProjectionRegistry.GetEpsg("SomethingVendorSpecific", null));
        }

        [Fact]
        public void Sample_header_maps_to_EOV()
        {
            var h = ErsHeader.Parse(SampleData.P0001Ers);
            Assert.True(h.CoordinateSpace.TryGetEpsg(out int epsg));
            Assert.Equal(23700, epsg);
        }
    }
}
