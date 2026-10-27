using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RasterField.ErMapper;
using RasterField.Rasters;
using RasterField.Vectors;
using static RasterField.L;

namespace RasterField
{
    public sealed partial class MainWindow
    {
        private readonly NumericUpDown _neutralGrade = new() { Value = 5, Minimum = -100, Maximum = 100, Increment = 0.1m, FormatString = "0.###", Width = 115 };
        private readonly NumericUpDown _neutralLength = new() { Value = 1000, Minimum = 1, Maximum = 100000, Increment = 100, Width = 115 };
        private readonly NumericUpDown _neutralHeightTolerance = new() { Value = 0.02m, Minimum = 0.001m, Maximum = 10, Increment = 0.01m, FormatString = "0.###", Width = 115 };
        private readonly NumericUpDown _neutralGradeTolerance = new() { Value = 0.1m, Minimum = 0.001m, Maximum = 5, Increment = 0.01m, FormatString = "0.###", Width = 115 };
        private readonly ComboBox _neutralBranch = new() { MinWidth = 100, SelectedIndex = 0 };
        private readonly TextBlock _neutralStatus = new() { TextWrapping = TextWrapping.Wrap };
        private readonly ProfileChartControl _neutralChart = new() { MinHeight = 140 };
        private IReadOnlyList<NeutralLineBranch> _neutralResults = Array.Empty<NeutralLineBranch>();
        private ProfileSeries[] _neutralSeries = Array.Empty<ProfileSeries>();
        private double _neutralResultGrade;
        private CancellationTokenSource? _neutralCts;
        private string _neutralSourceName = "";

        private TabItem BuildNeutralLineTab()
        {
            var rows = new StackPanel { Spacing = 5 };
            void Field(string label, Control input)
            {
                var row = new DockPanel();
                DockPanel.SetDock(input, Dock.Right);
                row.Children.Add(input);
                row.Children.Add(new TextBlock { Text = T(label), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                rows.Children.Add(row);
            }
            Field("Grade (%) · + rise / − fall", _neutralGrade);
            Field("Maximum length per branch (m)", _neutralLength);
            Field("Height tolerance (m)", _neutralHeightTolerance);
            Field("Grade tolerance (percentage points)", _neutralGradeTolerance);
            rows.Children.Add(new TextBlock { Text = T("Elevation values must be in metres. Uses the bilinear terrain, including between vertices."), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            var start = new Button { Content = T("Select start point") };
            start.Click += (_, _) => SetPathTool(PathTool.NeutralLine);
            var cancel = new Button { Content = T("Cancel") };
            cancel.Click += (_, _) => { _neutralCts?.Cancel(); SetPathTool(PathTool.None); };
            var buttons = new WrapPanel(); buttons.Children.Add(start); buttons.Children.Add(cancel);
            rows.Children.Add(buttons);
            _neutralBranch.ItemsSource = new[] { T("Branch 1"), T("Branch 2") };
            _neutralBranch.SelectedIndex = 0;
            _neutralBranch.SelectionChanged += (_, _) => ShowNeutralBranch();
            var csv = new Button { Content = "CSV…" };
            csv.Click += async (_, _) => await ProfileWindow.ExportCsvAsync(this, _neutralSeries, "m");
            var window = new Button { Content = T("Window…") };
            window.Click += (_, _) => { if (_neutralSeries.Length > 0) new ProfileWindow(_neutralSeries, "m", "m", _view.SetProfileHover).Show(this); };
            var actions = new WrapPanel(); actions.Children.Add(_neutralBranch); actions.Children.Add(window); actions.Children.Add(csv);
            rows.Children.Add(actions); rows.Children.Add(_neutralStatus);
            _neutralStatus.Text = T("Click the terrain to trace both branches.");
            _neutralChart.EmptyMessage = _neutralStatus.Text;
            _neutralChart.HoverChanged += (_, e) => _view.SetProfileHover(e.Sample);
            var root = new DockPanel { Margin = new Thickness(10, 6, 10, 10) };
            DockPanel.SetDock(rows, Dock.Top); root.Children.Add(rows); root.Children.Add(_neutralChart);
            return new TabItem { Header = T("Neutral line"), Content = new ScrollViewer { Content = root } };
        }

        private async Task TraceNeutralLineAsync(double x, double y)
        {
            _neutralCts?.Cancel();
            _neutralCts?.Dispose();
            var cts = _neutralCts = new CancellationTokenSource();
            var token = cts.Token;
            UseElevationSourceForTerrain();
            var loaded = await TryGetLoadedRasterAsync(T("Neutral line"));
            if (loaded == null || token.IsCancellationRequested) return;
            var source = _view.ActiveLayer!;
            int sourceBand = source.ActiveBand;
            var space = source.Document.Header.CoordinateSpace;
            // "natural" is common for EOV headers without an explicit Units field.
            string units = space.EffectiveUnits.ToUpperInvariant();
            bool metres = units is "M" or "METER" or "METERS" or "METRE" or "METRES" ||
                          (units == "NATURAL" && space.EpsgCode == 23700);
            var bands = source.Document.Header.RasterInfo.Bands;
            string zUnits = source.ActiveBand < bands.Count ? bands[source.ActiveBand].Units ?? "" : "";
            bool zMetres = string.IsNullOrWhiteSpace(zUnits) || zUnits.ToUpperInvariant() is "M" or "METER" or "METERS" or "METRE" or "METRES";
            if (space.CoordinateType == ErsCoordinateType.Ll || !metres || !zMetres)
            {
                await MessageAsync(T("Neutral line"), T("Use a projected terrain model with horizontal and elevation units in metres. For missing elevation metadata, metres are assumed."));
                return;
            }
            if (_neutralGrade.Value == null || _neutralLength.Value == null || _neutralHeightTolerance.Value == null || _neutralGradeTolerance.Value == null) return;
            var options = new NeutralLineOptions
            {
                GradePercent = (double)_neutralGrade.Value.Value,
                MaximumLength = (double)_neutralLength.Value.Value,
                HeightTolerance = (double)_neutralHeightTolerance.Value.Value,
                GradeTolerancePercent = (double)_neutralGradeTolerance.Value.Value,
            };
            _neutralResults = Array.Empty<NeutralLineBranch>();
            _neutralSeries = Array.Empty<ProfileSeries>();
            _neutralChart.SetSeries(_neutralSeries, "m", "m");
            _neutralStatus.Text = T("Tracing both branches…");
            try
            {
                var results = await Task.Run(() => NeutralLineTracer.Trace(loaded.Value.Raster, source.Document.GeoReference, x, y, options, token), token);
                if (token.IsCancellationRequested) return;
                if (!ReferenceEquals(source, _view.ActiveLayer) || !_view.Layers.Contains(source) || source.ActiveBand != sourceBand)
                { _neutralStatus.Text = T("Source layer changed; select a new start point."); return; }
                _neutralResults = results;
                _neutralResultGrade = options.GradePercent;
                _neutralSourceName = source.Name;
                for (int i = 0; i < results.Count; i++)
                {
                    var branch = results[i];
                    if (branch.Samples.Count < 2) continue;
                    var recipe = new LayerRecipe("neutral-line", source, new Dictionary<string, string>
                    {
                        ["x"] = LayerRecipe.Num(x), ["y"] = LayerRecipe.Num(y),
                        ["grade"] = LayerRecipe.Num(options.GradePercent),
                        ["length"] = LayerRecipe.Num(options.MaximumLength),
                        ["heightTolerance"] = LayerRecipe.Num(options.HeightTolerance),
                        ["gradeTolerance"] = LayerRecipe.Num(options.GradeTolerancePercent),
                        ["branch"] = LayerRecipe.Num(i), ["band"] = LayerRecipe.Num(sourceBand),
                    });
                    var result = NeutralRecipeResult(source, recipe, branch);
                    var layer = (VectorLayer)AddRecipeResult(recipe, result);
                    layer.LineWidth = 2.5;

                }
                ShowNeutralBranch();
            }
            catch (OperationCanceledException) { if (ReferenceEquals(_neutralCts, cts)) _neutralStatus.Text = T("Cancelled."); }
            catch (Exception ex) { if (ReferenceEquals(_neutralCts, cts)) _neutralStatus.Text = ex.Message; }
        }

        private static RecipeResult NeutralRecipeResult(RasterLayer source, LayerRecipe recipe, NeutralLineBranch branch)
        {
            var space = source.Document.Header.CoordinateSpace;
            int index = recipe.Get("branch", 0);
            double grade = recipe.Get("grade", 5.0);
            var document = ErvDocument.Create(space.Projection, space.Datum);
            document.Header.CoordinateSpace.Units = space.Units;
            document.Header.CoordinateSpace.CoordinateType = space.CoordinateType;
            if (branch.Samples.Count >= 2)
            {
                var line = new VectorPolyline { Attribute = L.F("Branch {0} · {1:0.###}%", index + 1, grade) };
                foreach (var sample in branch.Samples) line.Points.Add((sample.X, sample.Y));
                document.Objects.Add(line);
            }
            return new RecipeResult(null, document, null, null,
                L.F("{0} · neutral line {1} · {2:0.###}%", source.Name, index + 1, grade),
                L.F("Start {0:0.###}, {1:0.###} · length {2:0.##} m · Δz ≤ {3:0.###} m · Δgrade ≤ {4:0.###} pp",
                    recipe.Get("x", 0.0), recipe.Get("y", 0.0), branch.Length,
                    recipe.Get("heightTolerance", 0.02), recipe.Get("gradeTolerance", 0.1)), SeriesColors[index]);
        }

        private void ShowNeutralBranch()
        {
            int index = _neutralBranch.SelectedIndex;
            if (index < 0 || index >= _neutralResults.Count) return;
            var branch = _neutralResults[index];
            double z0 = branch.Samples.Count > 0 ? branch.Samples[0].Value ?? 0 : 0;
            var target = branch.Samples.Select(p => new ProfileSample(p.Distance, p.X, p.Y, (float)(z0 + _neutralResultGrade / 100 * p.Distance))).ToArray();
            _neutralSeries = new[]
            {
                new ProfileSeries(T("Terrain profile"), branch.Samples, SeriesColors[index]),
                new ProfileSeries(L.F("Target · {0:0.###}%", _neutralResultGrade), target, Colors.OrangeRed, dashed: true),
            };
            _neutralChart.SetSeries(_neutralSeries, "m", "m");
            _neutralStatus.Text = _neutralSourceName + "\n" + L.F("Length {0:0.##} m · max |Δz| {1:0.####} m · max |Δgrade| {2:0.####} pp", branch.Length, branch.MaximumHeightError, branch.MaximumGradeErrorPercent) + "\n" + T(branch.Stop switch
            {
                NeutralLineStop.MaximumLength => "Requested length reached.",
                NeutralLineStop.BoundaryOrNoData => "Stopped at the data boundary or NoData.",
                NeutralLineStop.InsufficientSlope => "Terrain is too gentle for the requested grade.",
                NeutralLineStop.FlatSurface => "Flat terrain: no unique tracing direction.",
                NeutralLineStop.StepLimit => "Step limit reached; partial result.",
                _ => "No further forward step within tolerance; partial result.",
            });
        }
    }
}
