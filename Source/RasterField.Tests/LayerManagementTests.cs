using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using RasterField.Rasters;
using RasterField.Vectors;
using Xunit;

namespace RasterField.Tests
{
    [Collection("Avalonia")]
    public sealed class LayerManagementTests
    {
        private readonly HeadlessUnitTestSession _session;

        public LayerManagementTests(AvaloniaTestFixture fixture) => _session = fixture.Session;

        private static RasterView View()
        {
            var view = new RasterView();
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            return view;
        }

        private static ErsDocument Document(double origin = 0, double cell = 1) =>
            ErsDocument.Create(new Raster(20, 20), origin, 100, cell, cell);

        private static ErvDocument VectorDocument()
        {
            var doc = ErvDocument.Create();
            doc.Objects.Add(new VectorPoint { X = 5, Y = 90 });
            return doc;
        }

        private static void AssertView((double X, double Y, double WorldPerPixel)? expected, RasterView view)
        {
            var actual = view.GetViewState();
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected.Value.X, actual.Value.X, 8);
            Assert.Equal(expected.Value.Y, actual.Value.Y, 8);
            Assert.Equal(expected.Value.WorldPerPixel, actual.Value.WorldPerPixel, 8);
        }

        [Fact]
        public Task Removing_active_layer_preserves_world_view() => _session.Dispatch(() =>
        {
            using var view = View();
            var first = view.AddLayer(Document(), "first");
            var second = view.AddLayer(Document(1000, 5), "second");
            view.SetViewState(1010, 90, 0.25);
            var before = view.GetViewState();
            view.RemoveLayer(second);
            Assert.Same(first, view.ActiveLayer);
            AssertView(before, view);
        }, CancellationToken.None);

        [Fact]
        public Task Removing_last_raster_preserves_view_when_vectors_remain() => _session.Dispatch(() =>
        {
            using var view = View();
            var raster = view.AddLayer(Document(1000, 5), "raster");
            var vector = view.AddVectorLayer(VectorDocument(), "vector");
            view.SetViewState(1020, 80, 0.25);
            var before = view.GetViewState();
            view.RemoveLayer(raster);
            Assert.True(view.ActiveLayer!.IsFrame);
            Assert.Single(view.DrawOrder);
            Assert.Same(vector, view.DrawOrder[0]);
            AssertView(before, view);
            view.RemoveVectorLayer(vector);
            Assert.Null(view.ActiveLayer);
            Assert.Empty(view.Layers);
        }, CancellationToken.None);

        [Fact]
        public Task Mixed_layer_order_and_visibility_keep_identity_through_undo_redo() => _session.Dispatch(() =>
        {
            using var view = View();
            var first = view.AddLayer(Document(), "first");
            var vector = view.AddVectorLayer(VectorDocument(), "vector");
            var second = view.AddLayer(Document(500, 2), "second");
            view.ClearHistory();
            view.MoveLayerDown(second);
            Assert.Equal(new object[] { first, second, vector }, view.DrawOrder);
            Assert.Same(second, view.ActiveLayer);
            view.SetLayerVisible(first, false);
            Assert.False(first.IsVisible);
            Assert.True(second.IsVisible);
            view.Undo();
            Assert.True(first.IsVisible);
            view.Undo();
            Assert.Equal(new object[] { first, vector, second }, view.DrawOrder);
            view.Redo();
            Assert.Equal(new object[] { first, second, vector }, view.DrawOrder);
            view.RemoveLayer(first);
            Assert.Same(second, view.ActiveLayer);
            Assert.Equal(new object[] { second, vector }, view.DrawOrder);
        }, CancellationToken.None);

        [Fact]
        public Task Switching_active_layer_preserves_world_view_and_display_settings() => _session.Dispatch(() =>
        {
            using var view = View();
            var first = view.AddLayer(Document(), "first");
            var second = view.AddLayer(Document(500, 2), "second");
            view.SetLayerOpacity(first, 0.4);
            view.SetViewState(510, 90, 0.25);
            var before = view.GetViewState();
            view.SetActiveLayer(first);
            AssertView(before, view);
            Assert.Equal(0.4, first.Opacity);
            Assert.Equal(1, second.Opacity);
            Assert.NotSame(first.Colorizer, second.Colorizer);
        }, CancellationToken.None);
        [Fact]
        public Task Undoing_layer_addition_keeps_the_current_world_view() => _session.Dispatch(() =>
        {
            using var view = View();
            var first = view.AddLayer(Document(), "first");
            view.ClearHistory();
            var second = view.AddLayer(Document(500, 2), "second");
            view.SetViewState(510, 90, 0.25);
            var before = view.GetViewState();
            view.Undo();
            Assert.Same(first, view.ActiveLayer);
            AssertView(before, view);
            view.Redo();
            Assert.Same(second, view.ActiveLayer);
            AssertView(before, view);
        }, CancellationToken.None);

        [Fact]
        public Task Recomputing_active_grid_and_undoing_it_preserve_world_view() => _session.Dispatch(() =>
        {
            using var view = View();
            var layer = view.AddLayer(Document(), "derived");
            view.SetViewState(10, 90, 0.25);
            var before = view.GetViewState();
            view.ReplaceLayerDocument(layer, Document(1000, 5));
            AssertView(before, view);
            view.Undo();
            AssertView(before, view);
            view.Redo();
            AssertView(before, view);
        }, CancellationToken.None);

        [Fact]
        public Task Switching_layers_notifies_consumers_that_selection_was_cleared() => _session.Dispatch(() =>
        {
            using var view = View();
            var first = view.AddLayer(Document(), "first");
            int notifications = 0;
            view.SelectionChanged += (_, _) => notifications++;
            var second = view.AddLayer(Document(1000, 5), "second");
            Assert.True(notifications > 0);
            notifications = 0;
            view.SetActiveLayer(first);
            Assert.True(notifications > 0);
            notifications = 0;
            view.RemoveLayer(first);
            Assert.Same(second, view.ActiveLayer);
            Assert.True(notifications > 0);
        }, CancellationToken.None);

    }
}
