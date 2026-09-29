using System;
using System.IO;
using System.Linq;
using RasterField.Projects;
using RasterField.Rasters;
using RasterField.Vectors;
using Xunit;

namespace RasterField.Tests
{
    public class GisAnalysisTests
    {
        private static readonly RasterGeoReference Identity = new RasterGeoReference(100, 100, 0, 1, 0, 0, 0, 1);
        private static readonly (double, double)[] Diagonal = { (0.0, 0.0), (5.0, 5.0) };
        private static readonly string[] Abcd = { "a", "b", "c", "d" };

        private static Raster Build(int w, int h, Func<int, int, double> f, double noData = double.NaN)
        {
            var r = new Raster(w, h, noData);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = (float)f(x, y);
            return r;
        }

        // ---- GeoJSON / CSV ------------------------------------------------------------

        [Fact]
        public void GeoJson_reads_features_with_attributes_and_splits_polygon_holes()
        {
            const string json = @"{ ""type"": ""FeatureCollection"", ""features"": [
                { ""type"": ""Feature"", ""properties"": { ""name"": ""peak"", ""h"": 812 }, ""geometry"": { ""type"": ""Point"", ""coordinates"": [650000.5, 240000] } },
                { ""type"": ""Feature"", ""properties"": { ""kind"": ""road"", ""lanes"": 2 }, ""geometry"": { ""type"": ""LineString"", ""coordinates"": [[0,0],[10,0],[10,5]] } },
                { ""type"": ""Feature"", ""id"": 7, ""properties"": null, ""geometry"": { ""type"": ""Polygon"", ""coordinates"": [
                    [[0,0],[10,0],[10,10],[0,10],[0,0]], [[2,2],[3,2],[3,3],[2,2]] ] } },
                { ""type"": ""Feature"", ""properties"": {}, ""geometry"": { ""type"": ""MultiPoint"", ""coordinates"": [[1,1],[2,2]] } }
            ] }";

            var objs = GeoJsonFormat.Read(json);

            Assert.Equal(6, objs.Count);
            var p = Assert.IsType<VectorPoint>(objs[0]);
            Assert.Equal("peak", p.Attribute);
            Assert.Equal(650000.5, p.X);
            var line = Assert.IsType<VectorPolyline>(objs[1]);
            Assert.Equal("kind=road; lanes=2", line.Attribute);
            Assert.Equal(3, line.Points.Count);
            var outer = Assert.IsType<VectorPolygon>(objs[2]);
            Assert.Equal("7", outer.Attribute);
            Assert.Equal(4, outer.Points.Count); // closing vertex dropped
            Assert.Equal("7 (hole)", objs[3].Attribute);
        }

        [Fact]
        public void GeoJson_round_trips_points_lines_polygons_and_boxes()
        {
            var objects = new VectorObject[]
            {
                new VectorPoint { X = 1.25, Y = -3, Attribute = "a" },
                new VectorPolyline { Attribute = "b", Points = { (0, 0), (5, 5) } },
                new VectorPolygon { Attribute = "c", Points = { (0, 0), (4, 0), (4, 3) } },
                new VectorBox { Attribute = "d", Ltx = 0, Lty = 10, Rbx = 5, Rby = 0 },
            };

            var back = GeoJsonFormat.Read(GeoJsonFormat.Write(objects));

            Assert.Equal(4, back.Count);
            Assert.Equal((1.25, -3.0), (((VectorPoint)back[0]).X, ((VectorPoint)back[0]).Y));
            Assert.Equal(Diagonal, ((VectorPolyline)back[1]).Points);
            Assert.Equal(3, ((VectorPolygon)back[2]).Points.Count);
            Assert.Equal(4, ((VectorPolygon)back[3]).Points.Count);
            Assert.Equal(Abcd, back.Select(o => o.Attribute));
        }

        [Fact]
        public void Csv_detects_header_named_columns_and_extra_values()
        {
            var pts = CsvPointFormat.Read("id,EOV_Y,EOV_X,z\n1,650000,240000,812.5\n2,650100,240050,800\n");
            Assert.Equal(2, pts.Count);
            Assert.Equal(650000, pts[0].X);
            Assert.Equal(240000, pts[0].Y);
            Assert.Equal("id=1; z=812.5", pts[0].Attribute);
        }

        [Fact]
        public void Csv_accepts_semicolons_decimal_commas_quotes_and_no_header()
        {
            var pts = CsvPointFormat.Read("12,5;7,25;\"forrás; kút\"\n1;2\n");
            Assert.Equal(2, pts.Count);
            Assert.Equal(12.5, pts[0].X);
            Assert.Equal(7.25, pts[0].Y);
            Assert.Equal("forrás; kút", pts[0].Attribute);
            Assert.Null(pts[1].Attribute);

            var back = CsvPointFormat.Read(CsvPointFormat.Write(pts));
            Assert.Equal(pts.Select(p => (p.X, p.Y, p.Attribute)), back.Select(p => (p.X, p.Y, p.Attribute)));
        }

        // ---- zonal statistics / measurement ---------------------------------------------

        [Fact]
        public void Zonal_statistics_use_cells_whose_centre_is_inside_the_polygon()
        {
            var r = Build(10, 10, (x, y) => x + 10 * y, noData: -1);
            r[2, 2] = -1; // one no-data cell inside
            // Square from (1,1) to (4,4) in pixel = world (identity): centres 1.5..3.5 → 3×3 cells.
            var zone = ZonalStatistics.Compute(r, Identity, new[] { (1.0, 1.0), (4.0, 1.0), (4.0, 4.0), (1.0, 4.0) });

            Assert.Equal(8, zone.Count);
            Assert.Equal(1, zone.NoDataCount);
            Assert.Equal(11, zone.Minimum);
            Assert.Equal(33, zone.Maximum);
            Assert.Equal((11 + 12 + 13 + 21 + 23 + 31 + 32 + 33) / 8.0, zone.Mean, 9);
            Assert.Equal(9, zone.Area, 9);
        }

        [Fact]
        public void Zonal_statistics_handle_a_triangle_and_a_georeference()
        {
            var r = Build(20, 20, (x, y) => 1);
            var geo = new RasterGeoReference(20, 20, 1000, 10, 0, 5000, 0, -10); // 10 m cells, north-up
            var zone = ZonalStatistics.Compute(r, geo, new[] { (1000.0, 5000.0), (1200.0, 5000.0), (1000.0, 4800.0) });
            Assert.InRange(zone.Count, 180, 230); // half of 400 cells, give or take the diagonal
            Assert.Equal(zone.Count * 100.0, zone.Area, 6);
            Assert.Equal(1, zone.Mean);
        }

        [Fact]
        public void Measurement_length_perimeter_area_and_surface_length()
        {
            var square = new[] { (0.0, 0.0), (3.0, 0.0), (3.0, 4.0), (0.0, 4.0) };
            Assert.Equal(10, Measurement.Length(square));
            Assert.Equal(14, Measurement.Perimeter(square));
            Assert.Equal(12, Measurement.Area(square));
            Assert.Equal(12, Measurement.Area(square.Reverse().ToArray()));

            // A ramp rising 1 per unit east: the surface length along x is √2 × the planar length.
            var ramp = Build(50, 10, (x, y) => x);
            double surface = Measurement.SurfaceLength(ramp, Identity, new[] { (5.0, 5.0), (25.0, 5.0) }, 0.5);
            Assert.Equal(20 * Math.Sqrt(2), surface, 6);
        }

        [Fact]
        public void Polyline_profile_includes_every_vertex_and_cumulative_distance()
        {
            var r = Build(30, 30, (x, y) => x + y);
            var verts = new[] { (2.5, 2.5), (12.5, 2.5), (12.5, 7.5) };
            var samples = RasterProfiler.SamplePolylineWorld(r, Identity, verts, 1.0);

            Assert.Equal(16, samples.Count); // 1 + 10 + 5
            Assert.Equal(15, samples[samples.Count - 1].Distance, 9);
            Assert.Contains(samples, s => s.X == 12.5 && s.Y == 2.5 && Math.Abs(s.Distance - 10) < 1e-9);
            foreach (var s in samples) Assert.Equal(s.X - 0.5 + s.Y - 0.5, s.Value!.Value, 4);

            var bez = RasterProfiler.SamplePolylineWorld(r, Identity, verts, 1.0,
                (ras, c, row) => BezierPatchInterpolator.Sample(ras, c, row, new BezierPatchOptions()));
            Assert.Equal(samples.Select(s => (double)s.Value!.Value), bez.Select(s => (double)s.Value!.Value), new ToleranceComparer(1e-3));
        }

        private sealed class ToleranceComparer : System.Collections.Generic.IEqualityComparer<double>
        {
            private readonly double _tol;
            public ToleranceComparer(double tol) { _tol = tol; }
            public bool Equals(double a, double b) => Math.Abs(a - b) <= _tol;
            public int GetHashCode(double v) => 0;
        }

        // ---- project file -----------------------------------------------------------------

        [Fact]
        public void Project_round_trips_with_paths_relative_to_the_project_folder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rf_proj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            try
            {
                string dem = Path.Combine(dir, "data", "dem.ers");
                var project = new ProjectDocument
                {
                    ActiveLayerId = "a",
                    View = new ProjectView { CenterX = 650000, CenterY = 240000, WorldPerPixel = 12.5 },
                };
                project.Layers.Add(new ProjectLayer { Id = "a", Name = "DEM", Path = dem, Palette = "Elevation", Minimum = 100, Maximum = 400, Opacity = 0.8, BlendMode = "Multiply" });
                project.Layers.Add(new ProjectLayer
                {
                    Id = "b", Kind = "vector", Name = "contours", Color = "#8B4A1C", LineWidth = 1.5,
                    Recipe = new ProjectRecipe { Operation = "contours", SourceLayerId = "a", Parameters = { ["interval"] = "10" } },
                });
                project.Bookmarks.Add(new ProjectBookmark { Name = "peak", View = new ProjectView { CenterX = 1, CenterY = 2, WorldPerPixel = 3 } });

                string file = Path.Combine(dir, "test.rfproj");
                project.Save(file);

                string text = File.ReadAllText(file);
                Assert.Contains("\"path\": \"data", text.Replace("\\\\", "/"));
                Assert.DoesNotContain(dir.Replace("\\", "\\\\"), text);

                var back = ProjectDocument.Load(file);
                Assert.Equal(dem, back.Layers[0].Path);
                Assert.Equal(0.8, back.Layers[0].Opacity);
                Assert.Equal("Multiply", back.Layers[0].BlendMode);
                Assert.Equal("a", back.Layers[1].Recipe!.SourceLayerId);
                Assert.Equal("10", back.Layers[1].Recipe!.Parameters["interval"]);
                Assert.Equal(12.5, back.View!.WorldPerPixel);
                Assert.Equal("peak", Assert.Single(back.Bookmarks).Name);
                Assert.Null(back.Layers[1].Path);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Project_from_a_newer_version_is_rejected()
        {
            string file = Path.Combine(Path.GetTempPath(), "rf_new_" + Guid.NewGuid().ToString("N") + ".rfproj");
            File.WriteAllText(file, "{ \"version\": 99, \"layers\": [] }");
            try { Assert.Throws<FormatException>(() => ProjectDocument.Load(file)); }
            finally { File.Delete(file); }
        }
    }
}
