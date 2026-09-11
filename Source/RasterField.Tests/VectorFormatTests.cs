using System.IO;
using System.Linq;
using RasterField;
using RasterField.ErMapper;
using RasterField.Vectors;
using Xunit;

namespace RasterField.Tests
{
    public class VectorFormatTests
    {
        // The object lines below are taken directly from the "Vector data file" example in the
        // ERDAS ER Mapper Customization Guide, "Vector Datasets and Header Files (.erv)" chapter.
        private const string SampleData =
            "box(,111.437,726.765,222.055,810.293,0,1,0,255,0,0,0).\n" +
            "oval(,120.467,568.74,190.45,640.98,0,1,0,20,63,25,0).\n" +
            "polygon(,5,[319.127,196.253,355.247,412.973,325.9,548.423,321.385,647.753,319.127,582.285],0,1,0,0,-1,-1,-1,0).\n" +
            "poly(,2,[319.127,528.105,219.797,537.135],0,1,0,0,0,0,253,42,234,0).\n" +
            "point(\"a point\",0.5474999,0.7488636,0,25,13,226,1).\n" +
            "map_box(\"Name = \\\"Scale_Bar/Tick\\\"\\n\",0.65749,0.34704,0.8249999,0.4011363,0,1,0,-1,-1,-1,0,1).\n" +
            "vtext(,0.0575,0.4359091,0.0575,41.0659101,877.6655231,0.4359091,Courier,0,40.63,1,0,0,0,253,13,22,1,[\"this is variable text, page relative\"]).\n" +
            "text(\"A line of text\",5836.35,6100.56,Helvetica-Bold,1,12,1,0,0,1,0,0,255,1,[\"This is fixed text.\"]).\n";

        private const string SampleHeader = @"DatasetHeader Begin
	Version = ""5.0""
	DataFile = ""Shared_Data/Australia""
	DataSetType = ERStorage
	DataType = Vector
	ByteOrder = MSBFirst
	CoordinateSpace Begin
		Datum = ""AGD66""
		Projection = ""TMAMG54""
		CoordinateType = EN
		Rotation = 0:0:0.0
	CoordinateSpace End
	VectorInfo Begin
		Type = ERVEC
		FileFormat = ASCII
		Extents Begin
			TopLeftCorner Begin
				Eastings = 453982
				Northings = 6210075
			TopLeftCorner End
			BottomRightCorner Begin
				Eastings = 477048
				Northings = 6182287
			BottomRightCorner End
		Extents End
	VectorInfo End
DatasetHeader End
";

        [Fact]
        public void Header_parses_VectorInfo_and_extents()
        {
            var h = ErsHeader.Parse(SampleHeader);

            Assert.Equal(ErsDataType.Vector, h.DataType);
            Assert.NotNull(h.VectorInfo);
            Assert.Equal(ErvType.ErVec, h.VectorInfo!.Type);
            Assert.Equal(ErvFileFormat.Ascii, h.VectorInfo!.FileFormat);
            Assert.NotNull(h.VectorInfo!.Extents);
            Assert.Equal(453982, h.VectorInfo!.Extents!.TopLeftCorner!.X);
            Assert.Equal(6210075, h.VectorInfo!.Extents!.TopLeftCorner!.Y);
            Assert.Equal(477048, h.VectorInfo!.Extents!.BottomRightCorner!.X);
            Assert.Equal(6182287, h.VectorInfo!.Extents!.BottomRightCorner!.Y);
        }

        [Fact]
        public void Header_round_trips_VectorInfo()
        {
            var h = ErsHeader.Parse(SampleHeader);
            var round = ErsHeader.Parse(h.ToErsText());

            Assert.Equal(ErsDataType.Vector, round.DataType);
            Assert.NotNull(round.VectorInfo);
            Assert.Equal(ErvType.ErVec, round.VectorInfo!.Type);
            Assert.Equal(453982, round.VectorInfo!.Extents!.TopLeftCorner!.X);
            Assert.Equal(6182287, round.VectorInfo!.Extents!.BottomRightCorner!.Y);

            // a vector header must not accidentally emit a RasterInfo block
            Assert.DoesNotContain("RasterInfo", h.ToErsText());
        }

        [Fact]
        public void Reads_every_documented_object_type()
        {
            var objs = VectorDataReader.Parse(SampleData);
            Assert.Equal(8, objs.Count);

            var box = Assert.IsType<VectorBox>(objs[0]);
            Assert.Equal(111.437, box.Ltx, 3);
            Assert.Equal(810.293, box.Rby, 3);
            Assert.Equal(255, box.R);

            var oval = Assert.IsType<VectorOval>(objs[1]);
            Assert.Equal(20, oval.R);

            var polygon = Assert.IsType<VectorPolygon>(objs[2]);
            Assert.Equal(5, polygon.Points.Count);
            Assert.Equal((319.127, 196.253), polygon.Points[0]);
            Assert.Equal((319.127, 582.285), polygon.Points[4]);
            Assert.Equal(-1, polygon.R);

            var poly = Assert.IsType<VectorPolyline>(objs[3]);
            Assert.Equal(2, poly.Points.Count);
            Assert.Equal(253, poly.R);

            var point = Assert.IsType<VectorPoint>(objs[4]);
            Assert.Equal("a point", point.Attribute);
            Assert.Equal(0.5474999, point.X, 6);
            Assert.Equal(226, point.B);
            Assert.True(point.Page);

            var mapBox = Assert.IsType<VectorMapBox>(objs[5]);
            Assert.Equal("Name = \"Scale_Bar/Tick\"\n", mapBox.Attribute);
            Assert.False(mapBox.FastPreview);
            Assert.True(mapBox.Page);

            var vtext = Assert.IsType<VectorVariableText>(objs[6]);
            Assert.Equal("Courier", vtext.Font);
            Assert.Equal(0.0575, vtext.X, 4);
            Assert.Single(vtext.Lines);
            Assert.Equal("this is variable text, page relative", vtext.Lines[0]);

            var text = Assert.IsType<VectorText>(objs[7]);
            Assert.Equal("A line of text", text.Attribute);
            Assert.Equal("Helvetica-Bold", text.Font);
            Assert.Equal(255, text.B);
            Assert.Equal("This is fixed text.", text.Lines[0]);
        }

        [Fact]
        public void Writer_output_reparses_to_equivalent_objects()
        {
            var objs = VectorDataReader.Parse(SampleData);

            using var ms = new StringWriter();
            VectorDataWriter.Write(ms, objs);
            var reparsed = VectorDataReader.Parse(ms.ToString());

            Assert.Equal(objs.Count, reparsed.Count);

            var originalPolygon = (VectorPolygon)objs[2];
            var reparsedPolygon = (VectorPolygon)reparsed[2];
            Assert.Equal(originalPolygon.Points.Count, reparsedPolygon.Points.Count);
            for (int i = 0; i < originalPolygon.Points.Count; i++)
            {
                Assert.Equal(originalPolygon.Points[i].X, reparsedPolygon.Points[i].X, 4);
                Assert.Equal(originalPolygon.Points[i].Y, reparsedPolygon.Points[i].Y, 4);
            }

            var originalMapBox = (VectorMapBox)objs[5];
            var reparsedMapBox = (VectorMapBox)reparsed[5];
            Assert.Equal(originalMapBox.Attribute, reparsedMapBox.Attribute);

            var originalText = (VectorText)objs[7];
            var reparsedText = (VectorText)reparsed[7];
            Assert.Equal(originalText.Lines[0], reparsedText.Lines[0]);
        }

        [Fact]
        public void Writer_never_uses_exponent_notation()
        {
            var p = new VectorPoint { X = 0.0000001234, Y = 123456789.5 };
            string text = VectorDataWriter.Format(p);
            Assert.DoesNotContain("E", text.ToUpperInvariant().Replace("HELVETICA", ""));
        }

        [Fact]
        public void Multiline_and_embedded_comma_objects_parse_correctly()
        {
            // exercise the two format quirks explicitly: values wrapped across lines, and a
            // text-line array element containing a literal comma inside quotes.
            string data =
                "poly(,3,[0,0,\n1,1,\n2,2],0,1,0,0,0,0,-1,-1,-1,0).\n" +
                "text(,0,0,Arial,0,10,2,0,0,0,-1,-1,-1,0,[\"line, with a comma\",\"second line\"]).\n";

            var objs = VectorDataReader.Parse(data);
            Assert.Equal(2, objs.Count);

            var poly = Assert.IsType<VectorPolyline>(objs[0]);
            Assert.Equal(3, poly.Points.Count);
            Assert.Equal((2, 2), poly.Points[2]);

            var text = Assert.IsType<VectorText>(objs[1]);
            Assert.Equal(2, text.Lines.Count);
            Assert.Equal("line, with a comma", text.Lines[0]);
            Assert.Equal("second line", text.Lines[1]);
        }

        [Fact]
        public void Malformed_object_reports_a_clear_error()
        {
            Assert.Throws<System.FormatException>(() => VectorDataReader.Parse("box(,1,2,3).\n"));
            Assert.Throws<System.FormatException>(() => VectorDataReader.Parse("point(,1,2,3,4,5,6,1"));
        }

        [Fact]
        public void ErvDocument_round_trips_through_save()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_erv_" + System.Guid.NewGuid().ToString("N")));
            try
            {
                var doc = ErvDocument.Create(projection: "BMG:EOV", datum: "EPSG:6237");
                doc.Objects.Add(new VectorPoint { Attribute = "peak", X = 650000, Y = 240000, R = 255, G = 0, B = 0 });
                doc.Objects.Add(new VectorPolygon
                {
                    Attribute = "region1",
                    Points = { (0, 0), (100, 0), (100, 100), (0, 100) },
                    R = 0, G = 255, B = 0,
                });

                string path = Path.Combine(dir.FullName, "out.erv");
                doc.Save(path);

                Assert.True(File.Exists(path));
                Assert.True(File.Exists(Path.Combine(dir.FullName, "out")));

                var reloaded = ErvDocument.Load(path);
                Assert.Equal(2, reloaded.Objects.Count);
                Assert.Equal("BMG:EOV", reloaded.Header.CoordinateSpace.Projection);
                Assert.Equal(ErsDataType.Vector, reloaded.Header.DataType);

                var point = Assert.IsType<VectorPoint>(reloaded.Objects[0]);
                Assert.Equal(650000, point.X, 3);
                Assert.Equal("peak", point.Attribute);

                var polygon = Assert.IsType<VectorPolygon>(reloaded.Objects[1]);
                Assert.Equal(4, polygon.Points.Count);

                // extents auto-computed from the objects
                Assert.NotNull(reloaded.Header.VectorInfo!.Extents);
                Assert.Equal(0, reloaded.Header.VectorInfo!.Extents!.TopLeftCorner!.X, 3);
                Assert.Equal(650000, reloaded.Header.VectorInfo!.Extents!.BottomRightCorner!.X, 3);
            }
            finally { dir.Delete(recursive: true); }
        }

        [Fact]
        public void VectorMapPolygon_round_trips_through_save()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_mappoly_" + System.Guid.NewGuid().ToString("N")));
            try
            {
                var doc = ErvDocument.Create();
                doc.Objects.Add(new VectorMapPolygon
                {
                    Attribute = "composition region",
                    Points = { (10, 10), (20, 10), (20, 20), (10, 20) },
                    Fill = 3,
                    Pen = 2,
                    Curved = false,
                    R = 10, G = 20, B = 30,
                    FastPreview = true,
                    Page = true,
                });

                string path = Path.Combine(dir.FullName, "out.erv");
                doc.Save(path);

                var reloaded = ErvDocument.Load(path);
                var mapPolygon = Assert.IsType<VectorMapPolygon>(Assert.Single(reloaded.Objects));
                Assert.Equal("composition region", mapPolygon.Attribute);
                Assert.Equal(4, mapPolygon.Points.Count);
                Assert.Equal((10, 10), mapPolygon.Points[0]);
                Assert.Equal(3, mapPolygon.Fill);
                Assert.Equal(2, mapPolygon.Pen);
                Assert.Equal(10, mapPolygon.R);
                Assert.Equal(20, mapPolygon.G);
                Assert.Equal(30, mapPolygon.B);
                Assert.True(mapPolygon.FastPreview);
                Assert.True(mapPolygon.Page);
            }
            finally { dir.Delete(recursive: true); }
        }
    }
}
