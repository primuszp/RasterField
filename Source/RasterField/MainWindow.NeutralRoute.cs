using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RasterField.Rasters;
using RasterField.Vectors;
using static RasterField.L;

namespace RasterField
{
    public sealed partial class MainWindow
    {
        private async Task HandleNeutralPointAsync(double x, double y)
        {
            if (_neutralMode.SelectedIndex != 1) { await TraceNeutralLineAsync(x, y); return; }
            if (_neutralPendingStart == null)
            {
                _neutralCts?.Cancel();
                var loaded = await NeutralSourceAsync();
                if (loaded == null) return;
                _neutralPendingStart = (x, y, loaded.Value.Source, loaded.Value.Source.ActiveBand);
                _neutralRouteResult = null;
                _neutralResults = Array.Empty<NeutralLineBranch>();
                _neutralSeries = Array.Empty<ProfileSeries>();
                _neutralChart.SetSeries(_neutralSeries, "m", "m");
                _view.SetProfileHover(null);
                _view.SetPath(new[] { (x, y) });
                _neutralPoints.Text = L.F("A: {0:0.###}, {1:0.###}", x, y);
                _neutralStatus.Text = T("Start A selected. Click endpoint B.");
                _neutralChart.EmptyMessage = _neutralStatus.Text;
                return;
            }
            var start = _neutralPendingStart.Value;
            _neutralPendingStart = null;
            if (!ReferenceEquals(start.Source, _view.ActiveLayer) || start.Band != start.Source.ActiveBand)
            {
                _view.ClearPath();
                _neutralStatus.Text = T("Source layer changed; select a new start point.");
                return;
            }
            _view.SetPath(new[] { (start.X, start.Y), (x, y) });
            _neutralPoints.Text = L.F("A: {0:0.###}, {1:0.###} · B: {2:0.###}, {3:0.###}", start.X, start.Y, x, y);
            await SearchNeutralRouteAsync(start.X, start.Y, x, y);
        }

        private async Task SearchNeutralRouteAsync(double ax, double ay, double bx, double by)
        {
            _neutralCts?.Cancel();
            _neutralCts?.Dispose();
            var cts = _neutralCts = new CancellationTokenSource();
            var token = cts.Token;
            var loaded = await NeutralSourceAsync();
            if (loaded == null || token.IsCancellationRequested) return;
            var source = loaded.Value.Source;
            int band = source.ActiveBand;
            if (_neutralGrade.Value == null || _neutralLength.Value == null || _neutralHeightTolerance.Value == null ||
                _neutralGradeTolerance.Value == null || _neutralSearchStates.Value == null) return;
            var options = new NeutralRouteOptions
            {
                GradePercent = (double)_neutralGrade.Value.Value,
                MaximumLength = (double)_neutralLength.Value.Value,
                HeightTolerance = (double)_neutralHeightTolerance.Value.Value,
                GradeTolerancePercent = (double)_neutralGradeTolerance.Value.Value,
                MaximumStates = (int)_neutralSearchStates.Value.Value,
            };
            _neutralRouteResult = null;
            _neutralResults = Array.Empty<NeutralLineBranch>();
            _neutralSeries = Array.Empty<ProfileSeries>();
            _neutralChart.SetSeries(_neutralSeries, "m", "m");
            _neutralStatus.Text = T("Searching for a checked connection A → B…");
            try
            {
                var result = await Task.Run(() => NeutralLineTracer.FindRoute(loaded.Value.Raster, source.Document.GeoReference,
                    ax, ay, bx, by, options, token), token);
                if (token.IsCancellationRequested) return;
                if (!ReferenceEquals(source, _view.ActiveLayer) || !_view.Layers.Contains(source) || source.ActiveBand != band)
                { _neutralStatus.Text = T("Source layer changed; select a new start point."); return; }
                _neutralRouteResult = result;
                _neutralSourceName = source.Name;
                _neutralResultGrade = options.GradePercent;
                if (result.Found)
                {
                    var recipe = new LayerRecipe("neutral-route", source, new Dictionary<string, string>
                    {
                        ["x"] = LayerRecipe.Num(ax), ["y"] = LayerRecipe.Num(ay),
                        ["endX"] = LayerRecipe.Num(bx), ["endY"] = LayerRecipe.Num(by),
                        ["grade"] = LayerRecipe.Num(options.GradePercent),
                        ["length"] = LayerRecipe.Num(options.MaximumLength),
                        ["heightTolerance"] = LayerRecipe.Num(options.HeightTolerance),
                        ["gradeTolerance"] = LayerRecipe.Num(options.GradeTolerancePercent),
                        ["states"] = LayerRecipe.Num(options.MaximumStates), ["band"] = LayerRecipe.Num(band),
                    });
                    var layer = (VectorLayer)AddRecipeResult(recipe, NeutralRouteRecipeResult(source, recipe, result));
                    layer.LineWidth = 2.5;
                }
                ShowNeutralRoute();
            }
            catch (OperationCanceledException) { if (ReferenceEquals(_neutralCts, cts)) _neutralStatus.Text = T("Cancelled."); }
            catch (Exception ex) { if (ReferenceEquals(_neutralCts, cts)) _neutralStatus.Text = ex.Message; }
        }

        private static NeutralRouteOptions NeutralRouteRecipeOptions(LayerRecipe recipe) => new()
        {
            GradePercent = recipe.Get("grade", 5.0), MaximumLength = recipe.Get("length", 1000.0),
            HeightTolerance = recipe.Get("heightTolerance", 0.02), GradeTolerancePercent = recipe.Get("gradeTolerance", 0.1),
            MaximumStates = recipe.Get("states", 25000),
        };

        private static RecipeResult NeutralRouteRecipeResult(RasterLayer source, LayerRecipe recipe, NeutralRouteResult result)
        {
            if (!result.Found) throw new InvalidOperationException(NeutralRouteStatusText(result.Status));
            var space = source.Document.Header.CoordinateSpace;
            var document = ErvDocument.Create(space.Projection, space.Datum);
            document.Header.CoordinateSpace.Units = space.Units;
            document.Header.CoordinateSpace.CoordinateType = space.CoordinateType;
            var line = new VectorPolyline { Attribute = L.F("Connection A → B · {0:0.###}%", recipe.Get("grade", 5.0)) };
            foreach (var sample in result.Samples) line.Points.Add((sample.X, sample.Y));
            document.Objects.Add(line);
            return new RecipeResult(null, document, null, null,
                L.F("{0} · neutral connection · {1:0.###}%", source.Name, recipe.Get("grade", 5.0)),
                L.F("A {0:0.###}, {1:0.###} · B {2:0.###}, {3:0.###} · length {4:0.##} m",
                    recipe.Get("x", 0.0), recipe.Get("y", 0.0), recipe.Get("endX", 0.0), recipe.Get("endY", 0.0), result.Length), SeriesColors[0]);
        }

        private void ShowNeutralRoute()
        {
            var result = _neutralRouteResult;
            if (result == null) return;
            _neutralSeries = result.Found ? new[]
            {
                new ProfileSeries(T("Terrain profile"), result.Samples, SeriesColors[0]),
                new ProfileSeries(L.F("Target · {0:0.###}%", _neutralResultGrade), result.Samples.Select(p =>
                    new ProfileSample(p.Distance, p.X, p.Y, (float)(result.StartElevation + _neutralResultGrade / 100 * p.Distance))).ToArray(), Avalonia.Media.Colors.OrangeRed, dashed: true),
            } : Array.Empty<ProfileSeries>();
            _neutralChart.SetSeries(_neutralSeries, "m", "m");
            _neutralStatus.Text = _neutralSourceName + "\n" + NeutralRouteStatusText(result.Status);
            if (result.RequiredLength.HasValue)
                _neutralStatus.Text += "\n" + L.F("Nominal required length: {0:0.###} m", result.RequiredLength.Value);
            if (result.Found)
                _neutralStatus.Text += "\n" + L.F("Length {0:0.##} m · max |Δz| {1:0.####} m · max |Δgrade| {2:0.####} pp", result.Length, result.MaximumHeightError, result.MaximumGradeErrorPercent);
            _neutralStatus.Text += "\n" + L.F("Expanded search states: {0}", result.ExpandedStates);
            _neutralChart.EmptyMessage = result.Found ? "" : NeutralRouteStatusText(result.Status);
        }

        private static string NeutralRouteStatusText(NeutralRouteStatus status) => T(status switch
        {
            NeutralRouteStatus.Success => "Connection reaches the exact endpoint; full profile verified.",
            NeutralRouteStatus.CoincidentEndpoints => "Choose two different endpoints.",
            NeutralRouteStatus.EndpointNoData => "An endpoint is outside valid terrain or touches NoData.",
            NeutralRouteStatus.IncompatibleGrade => "Endpoint elevations, distance and grade are incompatible within the height tolerance.",
            NeutralRouteStatus.LengthLimit => "The connection requires more than the maximum permitted length.",
            NeutralRouteStatus.SearchLimit => "Search limit reached. No complete route; increase the search budget or length limit.",
            _ => "No connection found at this search resolution. This does not prove that no route exists.",
        });
    }
}
