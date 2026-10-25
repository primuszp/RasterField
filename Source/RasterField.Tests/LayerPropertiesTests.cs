using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using RasterField.Projects;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests;

[Collection("Avalonia")]
public sealed class LayerPropertiesTests
{
    private readonly HeadlessUnitTestSession _session;
    public LayerPropertiesTests(AvaloniaTestFixture fixture) => _session = fixture.Session;

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Invoke(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    [Fact]
    public Task Emboss_overlay_and_saved_metadata_reach_properties_panel() => _session.Dispatch(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var window = new MainWindow();
            var view = Field<RasterView>(window, "_view");
            var raster = new Raster(3, 3);
            for (int i = 0; i < raster.Samples.Length; i++) raster.Samples[i] = i;
            var source = view.AddLayer(ErsDocument.Create(raster, 0, 3, 1, 1), "source");
            var layer = view.AddLayer(ErsDocument.Create(raster, 0, 3, 1, 1), "emboss");
            view.MarkDerived(layer, "test derivation");
            Invoke(window, "ApplyDerivedStyle", layer, new LayerRecipe("filter", source));
            Assert.Equal(LayerBlendMode.Overlay, layer.BlendMode);
            Assert.Equal(0.7, layer.Opacity);
            Assert.Equal(70, Field<Slider>(window, "_opacitySlider").Value);
            Assert.Equal(Array.IndexOf(Enum.GetValues<LayerBlendMode>(), LayerBlendMode.Overlay),
                Field<ComboBox>(window, "_blendBox").SelectedIndex);
            Assert.True(Field<Button>(window, "_saveLayerBtn").IsVisible);
            string path = Path.Combine(directory, "saved.ers");
            layer.Document.Save(path);
            view.MarkSaved(layer, path);
            Assert.False(Field<Button>(window, "_saveLayerBtn").IsVisible);
            Assert.Equal("test derivation", Field<TextBlock>(window, "_lineageText").Text);
            Assert.Contains(path, Field<TextBlock>(window, "_metaText").Text);
        }
        finally { Directory.Delete(directory, true); }
    }, CancellationToken.None);

    [Fact]
    public Task Project_final_active_layer_refreshes_panel_without_reselection() => _session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = AppSettings.SettingsPath();
        byte[]? previousSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            string rasterPath = Path.Combine(directory, "terrain.ers");
            ErsDocument.Create(new Raster(3, 3), 0, 3, 1, 1).Save(rasterPath);
            var project = new ProjectDocument { ActiveLayerId = "terrain" };
            project.Layers.Add(new ProjectLayer
            {
                Id = "terrain", Kind = "raster", Name = "restored", Path = rasterPath,
                Opacity = 0.42, BlendMode = "SoftLight", Minimum = -2, Maximum = 7,
                Gamma = 1.3, RenderMode = "Discrete", ClassCount = 12,
            });
            string path = Path.Combine(directory, "test.rfproj");
            project.Save(path);
            using var window = new MainWindow();
            await window.LoadProjectAsync(path);
            var view = Field<RasterView>(window, "_view");
            Assert.Equal(42, Field<Slider>(window, "_opacitySlider").Value);
            Assert.Equal(-2, Field<NumericUpDown>(window, "_minBox").Value);
            Assert.Equal(7, Field<NumericUpDown>(window, "_maxBox").Value);
            Assert.Equal(1.3, Field<Slider>(window, "_gammaSlider").Value);
            Assert.Equal(12, view.Colorizer!.ClassCount);
            Assert.Equal(LayerBlendMode.SoftLight, view.ActiveLayer!.BlendMode);
            Assert.Equal(Array.IndexOf(Enum.GetValues<LayerBlendMode>(), LayerBlendMode.SoftLight),
                Field<ComboBox>(window, "_blendBox").SelectedIndex);
        }
        finally
        {
            Directory.Delete(directory, true);
            if (previousSettings != null) File.WriteAllBytes(settingsPath, previousSettings);
            else File.Delete(settingsPath);
        }
    }, CancellationToken.None);
}
