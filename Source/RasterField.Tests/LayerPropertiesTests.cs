using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using RasterField.Rendering;
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
    [Fact]
    public Task Changing_filter_kind_updates_automatic_name_and_scale_but_keeps_layer_identity() => _session.Dispatch(() =>
    {
        using var window = new MainWindow();
        var view = Field<RasterView>(window, "_view");
        var terrain = new Raster(3, 3);
        for (int i = 0; i < terrain.Samples.Length; i++) terrain.Samples[i] = 300 + i;
        var source = view.AddLayer(ErsDocument.Create(terrain, 0, 3, 1, 1), "source");
        var initial = new LayerRecipe("filter", source, new Dictionary<string, string> { ["kind"] = "Mean", ["size"] = "5" });
        var layer = view.AddLayer(ErsDocument.Create(terrain, 0, 3, 1, 1), $"source · {L.F("Mean smoothing {0}×{0}", 5)}");
        layer.Recipe = initial;
        // Use the same label generator as recipe results, without relying on the UI language.
        var specType = typeof(MainWindow).GetNestedType("FilterSpec", BindingFlags.NonPublic)!;
        var spec = specType.GetMethod("FromRecipe")!.Invoke(null, new object[] { initial })!;
        layer.Name = "source · " + specType.GetMethod("Label")!.Invoke(spec, null);
        string oldName = layer.Name;
        view.ApplyDisplaySettings(layer, BuiltInPalettes.Viridis, 300, 400);
        view.SetLayerOpacity(layer, 0.4);
        var result = new Raster(3, 3);
        for (int i = 0; i < 9; i++) result.Samples[i] = i - 4;
        var updated = new LayerRecipe("filter", source, new Dictionary<string, string> { ["kind"] = "Custom", ["kernel"] = "-1 0 1" });
        Invoke(window, "ApplyRecomputedRaster", layer, ErsDocument.Create(result, 0, 3, 1, 1), "new filter", "new lineage", updated);
        Assert.Same(layer, view.ActiveLayer);
        Assert.Equal(2, view.Layers.Count);
        Assert.Equal("new filter", layer.Name);
        Assert.Equal(0.4, layer.Opacity);
        Assert.True(layer.Colorizer!.Minimum < 0);
        Assert.True(layer.Colorizer.Maximum < 10);
        Assert.Equal((decimal)layer.Colorizer.Minimum, Field<NumericUpDown>(window, "_minBox").Value);
        view.Undo();
        Assert.Equal(oldName, view.ActiveLayer!.Name);
        Assert.Equal(300, view.ActiveLayer.Colorizer!.Minimum);
    }, CancellationToken.None);

    [Theory]
    [InlineData("Gaussian")]
    [InlineData("Mean")]
    [InlineData("Sharpen")]
    [InlineData("Sobel")]
    [InlineData("Laplace")]
    [InlineData("LocalRelief")]
    [InlineData("Custom")]
    [InlineData("DigiTerraGaussian")]
    public Task Filter_layers_keep_values_style_and_order_through_history_and_project_reload(string kind) => _session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = AppSettings.SettingsPath();
        byte[]? previousSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            using var window = new MainWindow();
            var view = Field<RasterView>(window, "_view");
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            var raster = new Raster(21, 21);
            for (int y = 0; y < 21; y++)
                for (int x = 0; x < 21; x++) raster.SetValueFast(y, x, 100 + x + y + (x == 10 && y == 10 ? 30 : 0));
            raster.SetValueFast(2, 2, float.NaN);
            var document = ErsDocument.Create(raster, 0, 21, 1, 1);
            string sourcePath = Path.Combine(directory, "source.ers");
            document.Save(sourcePath);
            var source = view.AddLayer(document, "source", BuiltInPalettes.Viridis);
            view.SetViewState(10, 10, 0.1);
            var before = view.GetViewState();
            var parameters = new Dictionary<string, string>
            {
                ["kind"] = kind, ["size"] = "5", ["radius"] = "3", ["kernel"] = "1 2 1", ["divisor"] = "4",
            };
            var recipe = new LayerRecipe("filter", source, parameters);
            var create = (Task<object?>)typeof(MainWindow).GetMethod("CreateDerivedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { recipe, "test" })!;
            var layer = Assert.IsType<RasterLayer>(await create);
            var values = layer.Raster!.Samples.ToArray();
            Assert.Equal(before, view.GetViewState());
            Assert.True(float.IsNaN(values[2 * 21 + 2]));
            Assert.Equal(LayerBlendMode.Normal, layer.BlendMode);
            if (kind is "Gaussian" or "Mean" or "Sharpen" or "Custom" or "DigiTerraGaussian")
                Assert.Equal(source.Colorizer!.Palette.Name, layer.Colorizer!.Palette.Name);
            view.SetLayerVisible(layer, false);
            view.SetLayerOpacity(layer, 0.4);
            view.MoveLayerDown(layer);
            Assert.Same(source, view.DrawOrder[^1]);
            view.Undo();
            Assert.Same(layer, view.DrawOrder[^1]);
            view.Redo();
            Assert.Same(source, view.DrawOrder[^1]);
            Assert.Equal(before, view.GetViewState());
            view.MoveLayerUp(layer);
            view.SetLayerVisible(layer, true);
            var project = new ProjectDocument { ActiveLayerId = "result" };
            project.Layers.Add(new ProjectLayer { Id = "source", Name = "source", Path = sourcePath });
            project.Layers.Add(new ProjectLayer
            {
                Id = "result", Name = kind, Opacity = 0.4, BlendMode = "Normal",
                Recipe = new ProjectRecipe { Operation = "filter", SourceLayerId = "source", Parameters = parameters },
            });
            string path = Path.Combine(directory, "filters.rfproj");
            project.Save(path);
            await window.LoadProjectAsync(path);
            Assert.Equal(2, view.Layers.Count);
            Assert.Equal(values, view.ActiveLayer!.Raster!.Samples);
            Assert.Equal(0.4, view.ActiveLayer.Opacity);
            Assert.Equal(40, Field<Slider>(window, "_opacitySlider").Value);
            Assert.Equal(kind, view.ActiveLayer.Recipe!.Get("kind", ""));
        }
        finally
        {
            Directory.Delete(directory, true);
            if (previousSettings != null) File.WriteAllBytes(settingsPath, previousSettings);
            else File.Delete(settingsPath);
        }
    }, CancellationToken.None);

}
