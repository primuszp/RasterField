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
            Field<ComboBox>(window,"_neutralMode").SelectedIndex = 1;
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

}
