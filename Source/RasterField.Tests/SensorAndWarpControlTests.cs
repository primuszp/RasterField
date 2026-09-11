using RasterField.ErMapper;
using Xunit;

namespace RasterField.Tests
{
    public class SensorAndWarpControlTests
    {
        private const string Header = @"DatasetHeader Begin
	Version = ""5.7""
	DataSetType = ERStorage
	DataType = Raster
	ByteOrder = MSBFirst
	CoordinateSpace Begin
		Datum = ""NAD27""
		Projection = ""NUTM11""
		CoordinateType = EN
		Rotation = 0:0:0.0
	CoordinateSpace End
	RasterInfo Begin
		CellType = Unsigned8BitInteger
		NrOfLines = 694
		NrOfCellsPerLine = 652
		NrOfBands = 1
		SensorInfo Begin
			CameraManufacturer = ""Leica""
			CameraModel = ""RC20""
			LensSerialNr = ""100100""
			CalibrationDate = Mon Jun 01 00:00:00 GMT 1970
			SensorType = MetricCamera
			PlatformType = Aerial
			FiducialInfo Begin
				PrinciplePointOffsetX = 0.006
				PrinciplePointOffsetY = 0
				FiducialPointTopLeft Begin
					IsOn = Yes
					IsLocked = No
					CellX = 210.4633887599771
					CellY = 320.1004736175112
					OffsetX = -105.987
					OffsetY = 106.007
				FiducialPointTopLeft End
				FiducialPointBottomRight Begin
					IsOn = Yes
					IsLocked = No
					CellX = 5213.025106596184
					CellY = 5331.571326150155
					OffsetX = 106.01
					OffsetY = -105.99
				FiducialPointBottomRight End
			FiducialInfo End
			FocalLength = 152.793
			PrinciplePointX = 1
			PrinciplePointY = 1
		SensorInfo End
		WarpControl Begin
			WarpType = Ortho
			WarpSampling = Nearest
			Rotation = 0
			DemFile = ""San_Diego_DEM.ers""
			DemBandNr = 1
			ChooseGcpsFromDigitizer = No
			ChooseGcpsFromImage = Yes
			GcpsChosenFrom = ""San_Diego_rectified.alg""
			OutputFile = ""San_Diego_Airphoto_34_rectified.ers""
			UseAverageHeight = No
			AverageHeight = 0
			OutputCellSizeX = 0.8784871184780805
			OutputCellSizeY = 0.8784871184780805
			OutputHasNullCells = Yes
			OutputNullCellValue = 0
			Correction Begin
				RadialLens = No
				PolyLens = No
				Atmospheric = No
				EarthCurvature = No
			Correction End
			GivenOrthoInfo Begin
				AttitudeOmega = 0
				AttitudePhi = 0
				AttitudeKappa = 0
				ExposureCenterX = 0
				ExposureCenterY = 0
				ExposureCenterZ = 0
				SCALE = 0
				CoordinateSpace Begin
					Datum = ""RAW""
					Projection = ""RAW""
					CoordinateType = RAW
					Rotation = 0:0:0.0
				CoordinateSpace End
			GivenOrthoInfo End
			CoordinateSpace Begin
				Datum = ""NAD27""
				Projection = ""NUTM11""
				CoordinateType = EN
				Units = ""METERS""
				Rotation = 0:0:0.0
			CoordinateSpace End
			Extents Begin
				TopLeftCorner Begin
					Eastings = 481254.9675890448
					Northings = 3623804.501505829
				TopLeftCorner End
				BottomRightCorner Begin
					Eastings = 485756.9891375387
					Northings = 3619284.200966488
				BottomRightCorner End
			Extents End
			ControlPoints = {
				""1035"" Yes No 2344.650885 3546.419458 3620906.21 3.105
				""165"" Yes No 753.008933 3870.075769 3620609.47 3.956
			}
		WarpControl End
	RasterInfo End
DatasetHeader End
";

        [Fact]
        public void SensorInfo_is_parsed()
        {
            var h = ErsHeader.Parse(Header);
            var s = h.RasterInfo.SensorInfo;
            Assert.NotNull(s);
            Assert.Equal("Leica", s!.CameraManufacturer);
            Assert.Equal("RC20", s.CameraModel);
            Assert.Equal(ErsSensorType.MetricCamera, s.SensorType);
            Assert.Equal(ErsPlatformType.Aerial, s.PlatformType);
            Assert.Equal(152.793, s.FocalLength);

            Assert.NotNull(s.FiducialInfo);
            Assert.Equal(0.006, s.FiducialInfo!.PrincipalPointOffsetX);
            Assert.NotNull(s.FiducialInfo!.TopLeft);
            Assert.Equal(210.4633887599771, s.FiducialInfo!.TopLeft!.CellX, 6);
            Assert.True(s.FiducialInfo!.TopLeft!.IsOn);
            Assert.False(s.FiducialInfo!.TopLeft!.IsLocked);
            Assert.NotNull(s.FiducialInfo!.BottomRight);
            Assert.Null(s.FiducialInfo!.TopRight); // not present in this trimmed fixture
        }

        [Fact]
        public void WarpControl_is_parsed()
        {
            var h = ErsHeader.Parse(Header);
            var wc = h.RasterInfo.WarpControl;
            Assert.NotNull(wc);
            Assert.Equal(ErsWarpType.Ortho, wc!.WarpType);
            Assert.Equal("Nearest", wc.WarpSampling);
            Assert.Equal("San_Diego_DEM.ers", wc.DemFile);
            Assert.Equal(1, wc.DemBandNr);
            Assert.False(wc.ChooseGcpsFromDigitizer);
            Assert.True(wc.ChooseGcpsFromImage);
            Assert.True(wc.OutputHasNullCells);
            Assert.Equal(0.8784871184780805, wc.OutputCellSizeX!.Value, 10);

            Assert.NotNull(wc.Correction);
            Assert.False(wc.Correction!.RadialLens);

            Assert.NotNull(wc.GivenOrthoInfo);
            Assert.Equal(0, wc.GivenOrthoInfo!.Scale);
            Assert.NotNull(wc.GivenOrthoInfo!.CoordinateSpace);
            Assert.Equal("RAW", wc.GivenOrthoInfo!.CoordinateSpace!.Projection);

            Assert.NotNull(wc.CoordinateSpace);
            Assert.Equal("NUTM11", wc.CoordinateSpace!.Projection);

            Assert.NotNull(wc.Extents);
            Assert.Equal(481254.9675890448, wc.Extents!.TopLeftCorner!.X, 6);
            Assert.Equal(3619284.200966488, wc.Extents!.BottomRightCorner!.Y, 6);
        }

        [Fact]
        public void SensorInfo_and_WarpControl_round_trip_including_unmodelled_ControlPoints()
        {
            var h = ErsHeader.Parse(Header);
            string text = h.ToErsText();
            var round = ErsHeader.Parse(text);

            Assert.NotNull(round.RasterInfo.SensorInfo);
            Assert.Equal("Leica", round.RasterInfo.SensorInfo!.CameraManufacturer);
            Assert.Equal(210.4633887599771, round.RasterInfo.SensorInfo!.FiducialInfo!.TopLeft!.CellX, 6);

            Assert.NotNull(round.RasterInfo.WarpControl);
            Assert.Equal(ErsWarpType.Ortho, round.RasterInfo.WarpControl!.WarpType);
            Assert.Equal("San_Diego_DEM.ers", round.RasterInfo.WarpControl!.DemFile);

            // ControlPoints is not individually modelled; it must still survive the round trip.
            var warpBlock = round.RawBlock!.Block("RasterInfo")!.Block("WarpControl")!;
            Assert.True(warpBlock.TryGet("ControlPoints", out var cp));
            Assert.True(cp.IsArray);
            Assert.Contains("\"1035\"", cp.ArrayItems);
            Assert.Contains("\"165\"", cp.ArrayItems);
        }
    }
}
