using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using RasterField.Rasters;
using RasterField.Vectors;
using Xunit;

namespace RasterField.Tests;

[Collection("Avalonia")]
public sealed class NeutralLineUiTests
{
    private readonly HeadlessUnitTestSession _session;
    public NeutralLineUiTests(AvaloniaTestFixture fixture) => _session = fixture.Session;
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Call(MainWindow window, string name, params object[] args) =>
        (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;

    [Fact]
    public Task Trace_profile_export_and_saved_project_reproduce_both_branches() => _session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "neutral-line-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settings = AppSettings.SettingsPath();
        byte[]? priorSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        try
        {
            var raster = new Raster(100,100);
            for (int r = 0; r < 100; r++)
            for (int c = 0; c < 100; c++) raster[r,c] = (float)(100+0.125*(c+0.5));
            string sourcePath = Path.Combine(directory,"terrain.ers");
            ErsDocument.Create(raster,0,100,1,1).Save(sourcePath);
            using var window = new MainWindow();
            var view = Field<RasterView>(window,"_view");
            view.AddLayerFromPath(sourcePath);
            Field<NumericUpDown>(window,"_neutralGrade").Value = -5;
            Field<NumericUpDown>(window,"_neutralLength").Value = 20;
            await Call(window,"TraceNeutralLineAsync",50.0,50.0);
            var vectors = view.DrawOrder.OfType<VectorLayer>().ToArray();
            Assert.Equal(2,vectors.Length);
            var series = Field<ProfileSeries[]>(window,"_neutralSeries");
            Assert.Equal(2,series.Length);
            Assert.Equal(20,series[0].Samples[^1].Distance,6);
            Assert.InRange(Math.Abs(series[0].Samples[^1].Value!.Value-series[1].Samples[^1].Value!.Value),0,0.00002);
            string projectPath = Path.Combine(directory,"test.rfproj");
            typeof(MainWindow).GetField("_projectPath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,projectPath);
            await Call(window,"SaveProjectAsync",false);
            using var restored = new MainWindow();
            await restored.LoadProjectAsync(projectPath);
            var restoredVectors = Field<RasterView>(restored,"_view").DrawOrder.OfType<VectorLayer>().ToArray();
            Assert.Equal(2,restoredVectors.Length);
            for (int i = 0; i < 2; i++)
            {
                var original = Assert.IsType<VectorPolyline>(Assert.Single(vectors[i].Document.Objects));
                var regenerated = Assert.IsType<VectorPolyline>(Assert.Single(restoredVectors[i].Document.Objects));
                Assert.Equal(original.Points,regenerated.Points);
                string export = Path.Combine(directory,$"branch{i}.erv");
                vectors[i].Document.Save(export);
                var reread = ErvDocument.Load(export);
                Assert.Equal(original.Points,Assert.IsType<VectorPolyline>(Assert.Single(reread.Objects)).Points);
            }
        }
        finally
        {
            if (priorSettings != null) File.WriteAllBytes(settings,priorSettings);
            else if (File.Exists(settings)) File.Delete(settings);
            Directory.Delete(directory,true);
        }
    },CancellationToken.None);
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public Task Two_point_selection_profile_and_project_restore_a_complete_connection(int sign) => _session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "neutral-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settings = AppSettings.SettingsPath();
        byte[]? priorSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        try
        {
            var raster = new Raster(100,100);
            for (int r = 0; r < 100; r++)
            for (int c = 0; c < 100; c++) raster[r,c] = (float)(100+0.125*(c+0.5));
            string sourcePath = Path.Combine(directory,"terrain.ers");
            ErsDocument.Create(raster,0,100,1,1).Save(sourcePath);
            using var window = new MainWindow();
            var view = Field<RasterView>(window,"_view");
            view.AddLayerFromPath(sourcePath);
            Field<Avalonia.Controls.Primitives.SelectingItemsControl>(window,"_neutralMode").SelectedIndex = 1;
            Field<NumericUpDown>(window,"_neutralGrade").Value = sign*5;
            Field<NumericUpDown>(window,"_neutralLength").Value = 20;
            await Call(window,"HandleNeutralPointAsync",40.0,50.0);
            Assert.Single(view.CurrentPath);
            Assert.Empty(view.DrawOrder.OfType<VectorLayer>());
            await Call(window,"HandleNeutralPointAsync",40.0+sign*4,50.0);
            var layer = Assert.Single(view.DrawOrder.OfType<VectorLayer>());
            Assert.Equal("neutral-route",layer.Recipe!.Operation);
            Assert.Equal(2,view.CurrentPath.Count);
            var result = Field<NeutralRouteResult>(window,"_neutralRouteResult");
            Assert.True(result.Found);
            Assert.Equal(10,result.Length,6);
            var profile = Field<ProfileSeries[]>(window,"_neutralSeries");
            Assert.Equal(2,profile.Length);
            Assert.InRange(Math.Abs(profile[0].Samples[^1].Value!.Value-profile[1].Samples[^1].Value!.Value),0,0.00002);
            string path = Path.Combine(directory,"route.rfproj");
            typeof(MainWindow).GetField("_projectPath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,path);
            await Call(window,"SaveProjectAsync",false);
            using var restored = new MainWindow();
            await restored.LoadProjectAsync(path);
            var regenerated = Assert.Single(Field<RasterView>(restored,"_view").DrawOrder.OfType<VectorLayer>());
            var expected = Assert.IsType<VectorPolyline>(Assert.Single(layer.Document.Objects));
            var actual = Assert.IsType<VectorPolyline>(Assert.Single(regenerated.Document.Objects));
            Assert.Equal(expected.Points,actual.Points);
            string export = Path.Combine(directory,"route.erv");
            layer.Document.Save(export);
            Assert.Equal(expected.Points,Assert.IsType<VectorPolyline>(Assert.Single(ErvDocument.Load(export).Objects)).Points);
        }
        finally
        {
            if (priorSettings != null) File.WriteAllBytes(settings,priorSettings);
            else if (File.Exists(settings)) File.Delete(settings);
            Directory.Delete(directory,true);
        }
    },CancellationToken.None);

    [Theory]
    [InlineData(0.104, false, 0.1)]
    [InlineData(0.106, true, 0.1)]
    [InlineData(4.27, false, 4.3)]
    [InlineData(12.7, false, 12.5)]
    public void Grades_round_to_readable_steps(double grade, bool down, double expected) =>
        Assert.Equal(expected, MainWindow.NiceGrade(grade, down), 9);

    [Fact]
    public void Unit_check_refuses_only_degrees_and_feet()
    {
        static (bool, string?) Check(RasterField.ErMapper.CoordinateSpace space, string? z = null) => MainWindow.NeutralUnits(space, z);
        // No projection at all (RAW) and a recognised metric projection: metres, no note.
        Assert.Equal((true, null), Check(new() { Projection = "RAW", Datum = "RAW", CoordinateType = RasterField.ErMapper.ErsCoordinateType.Raw }));
        Assert.Equal((true, null), Check(new() { Projection = "BMG:EOV", Datum = "EPSG:6237", CoordinateType = RasterField.ErMapper.ErsCoordinateType.En }));
        // A local / unknown projection in natural units: usable, with the assumption stated.
        var local = Check(new() { Projection = "LOCAL", Datum = "LOCAL", CoordinateType = RasterField.ErMapper.ErsCoordinateType.En });
        Assert.True(local.Item1);
        Assert.NotNull(local.Item2);
        // Free-text elevation unit: usable, noted.
        var z = Check(new() { Projection = "RAW", Datum = "RAW", CoordinateType = RasterField.ErMapper.ErsCoordinateType.Raw }, "Elevation");
        Assert.True(z.Item1);
        Assert.Contains("Elevation", z.Item2);
        // Known-wrong units are refused.
        Assert.False(Check(new() { Projection = "GEODETIC", Datum = "WGS84", CoordinateType = RasterField.ErMapper.ErsCoordinateType.Ll }).Item1);
        Assert.False(Check(new() { Projection = "LOCAL", Datum = "LOCAL", CoordinateType = RasterField.ErMapper.ErsCoordinateType.En, Units = "FEET" }).Item1);
        Assert.False(Check(new() { Projection = "RAW", Datum = "RAW", CoordinateType = RasterField.ErMapper.ErsCoordinateType.Raw }, "ft").Item1);
    }

    [Fact]
    public Task Unprojected_terrain_traces_and_gentle_start_reports_its_slope() => _session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "neutral-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settings = AppSettings.SettingsPath();
        byte[]? priorSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        try
        {
            // A 1 % plane in a local grid with no projection.
            var raster = new Raster(100,100);
            for (int r = 0; r < 100; r++)
            for (int c = 0; c < 100; c++) raster[r,c] = (float)(100+0.01*(c+0.5));
            string sourcePath = Path.Combine(directory,"local.ers");
            ErsDocument.Create(raster,0,100,1,1,projection:"LOCAL",datum:"LOCAL").Save(sourcePath);
            using var window = new MainWindow();
            var view = Field<RasterView>(window,"_view");
            view.AddLayerFromPath(sourcePath);

            // 0.5 % fits the 1 % terrain: both branches are traced, with the unit assumption noted.
            Field<NumericUpDown>(window,"_neutralGrade").Value = 0.5m;
            Field<NumericUpDown>(window,"_neutralLength").Value = 30;
            await Call(window,"TraceNeutralLineAsync",50.0,50.0);
            Assert.Equal(2,view.DrawOrder.OfType<VectorLayer>().Count());
            Assert.Equal(0.5m,Field<NumericUpDown>(window,"_neutralGrade").Value); // the slider did not snap it away
            Assert.NotNull(Field<string?>(window,"_neutralUnitNote"));

            // 5 % is steeper than the terrain: the message names the slope at the start (1 %).
            Field<NumericUpDown>(window,"_neutralGrade").Value = 5;
            await Call(window,"TraceNeutralLineAsync",50.0,50.0);
            string status = Field<TextBlock>(window,"_neutralStatus").Text!;
            Assert.Contains(L.F("{0:0.###}", 1.0) + " %", status);
            // Nothing was traced, so it is a failure with a one-click retry below the terrain slope.
            Assert.Equal("Failed",Field<object>(window,"_neutralStage").ToString());
            var retry = Field<Button>(window,"_neutralRetry");
            Assert.True(retry.IsVisible);
            Assert.Equal(0.8,(double)retry.Tag!,9);
            Field<NumericUpDown>(window,"_neutralGrade").Value = 0.8m;
            await Call(window,"TraceNeutralLineAsync",50.0,50.0);
            Assert.Equal("Done",Field<object>(window,"_neutralStage").ToString());
            Assert.False(retry.IsVisible);
        }
        finally
        {
            if (priorSettings != null) File.WriteAllBytes(settings,priorSettings);
            else if (File.Exists(settings)) File.Delete(settings);
            Directory.Delete(directory,true);
        }
    },CancellationToken.None);
}
