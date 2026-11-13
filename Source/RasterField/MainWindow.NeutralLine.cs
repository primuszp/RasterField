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
        private readonly NumericUpDown _neutralGrade = new() { Value = 5, Minimum = -100, Maximum = 100, Increment = 0.1m, FormatString = "0.###", Width = 92 };
        private readonly NumericUpDown _neutralLength = new() { Value = 1000, Minimum = 1, Maximum = 100000, Increment = 100, Width = 104 };
        private readonly NumericUpDown _neutralHeightTolerance = new() { Value = 0.02m, Minimum = 0.001m, Maximum = 10, Increment = 0.01m, FormatString = "0.###", Width = 104 };
        private readonly NumericUpDown _neutralGradeTolerance = new() { Value = 0.1m, Minimum = 0.001m, Maximum = 5, Increment = 0.01m, FormatString = "0.###", Width = 104 };
        private readonly ListBox _neutralMode = SegmentedList();
        private readonly NumericUpDown _neutralSearchStates = new() { Value = 25000, Minimum = 1, Maximum = 200000, Increment = 1000, Width = 104 };
        private readonly TextBlock _neutralPoints = new() { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption, Opacity = 0.8 };
        private NeutralRouteResult? _neutralRouteResult;
        private (double X, double Y, RasterLayer Source, int Band)? _neutralPendingStart;
        private readonly ListBox _neutralBranch = SegmentedList();
        private readonly TextBlock _neutralStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption + 0.5, LineHeight = 17 };
        private readonly ProfileChartControl _neutralChart = new() { Height = 170 };
        // Square-root scale: half the track covers 0–5 %, so gentle grades (0.05 %, 0.2 %) can be set as finely as steep ones.
        private readonly Slider _neutralGradeSlider = new() { Minimum = 0, Maximum = 1 };
        private const double SliderMaxGrade = 20;
        private bool _neutralSyncingGrade;
        private readonly Button _neutralRetry = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        private (double X, double Y)? _neutralLastStart;
        private readonly ListBox _neutralDirection = SegmentedList();
        private readonly TextBlock _neutralGradeHint = new() { FontSize = AppTheme.FontCaption, TextWrapping = TextWrapping.Wrap };
        private readonly Button _neutralAction = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 34 };
        private readonly ProgressBar _neutralProgress = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, IsVisible = false };
        private readonly Border _neutralCallout = new();
        private readonly Grid _neutralStepper = new();
        private readonly StackPanel _neutralResultPanel = new() { Spacing = 8, IsVisible = false };
        private readonly TextBlock[] _neutralStatValues = new TextBlock[3];
        private NeutralStage _neutralStage = NeutralStage.Ready;

        /// <summary>Where the user is in the neutral-line workflow; drives the stepper, the main button and the callout.</summary>
        private enum NeutralStage { Ready, PickStart, PickEnd, Working, Done, Failed }

        private static ListBox SegmentedList()
        {
            var list = new ListBox { SelectionMode = SelectionMode.Single };
            list.Classes.Add("segmented");
            AppStyles.UseSegmentedSelectionBrushes(list);
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new Avalonia.Controls.Primitives.UniformGrid { Rows = 1 });
            ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            return list;
        }
        private IReadOnlyList<NeutralLineBranch> _neutralResults = Array.Empty<NeutralLineBranch>();
        private ProfileSeries[] _neutralSeries = Array.Empty<ProfileSeries>();
        private double _neutralResultGrade;
        private CancellationTokenSource? _neutralCts;
        private string _neutralSourceName = "";
        private double? _neutralStartSlope;

        private TabItem BuildAutomaticNeutralLineTab()
        {
            var root = new StackPanel { Spacing = 10, Margin = new Thickness(10, 4, 10, 12) };

            // 1 · what to trace: two branches from one point, or a connection between two points.
            _neutralMode.ItemsSource = new[] { T("From one point"), T("Between two points") };
            _neutralMode.SelectedIndex = 0;
            root.Children.Add(_neutralMode);
            root.Children.Add(_neutralStepper);

            // 2 · the grade: direction, magnitude (slider + exact value), quick presets and what it means.
            _neutralDirection.ItemsSource = new Control[] { AppTheme.IconLabel(AppTheme.Icons.Rising, T("Rising")), AppTheme.IconLabel(AppTheme.Icons.Falling, T("Falling")) };
            _neutralDirection.SelectedIndex = 0;
            void SetGrade(double magnitude, bool rising)
            {
                _neutralSyncingGrade = true;
                _neutralGrade.Value = (decimal)Math.Round(rising ? magnitude : -magnitude, 3);
                _neutralSyncingGrade = false;
                SyncGradeControls();
            }
            _neutralDirection.SelectionChanged += (_, _) =>
            {
                if (!_neutralSyncingGrade && _neutralDirection.SelectedIndex >= 0)
                    SetGrade(Math.Abs((double)(_neutralGrade.Value ?? 0)), _neutralDirection.SelectedIndex == 0);
            };
            _neutralGradeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !_neutralSyncingGrade)
                    SetGrade(NiceGrade(SliderMaxGrade * _neutralGradeSlider.Value * _neutralGradeSlider.Value), (_neutralGrade.Value ?? 0) >= 0);
            };
            _neutralGrade.ValueChanged += (_, _) => { if (!_neutralSyncingGrade) SyncGradeControls(); };

            var gradeHeader = new DockPanel();
            var percent = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), Foreground = AppTheme.TextSecondary };
            var gradeValue = new StackPanel { Orientation = Orientation.Horizontal, Children = { _neutralGrade, percent } };
            DockPanel.SetDock(gradeValue, Dock.Right);
            gradeHeader.Children.Add(gradeValue);
            gradeHeader.Children.Add(new TextBlock { Text = T("Grade"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

            var presets = new Avalonia.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 2, 0, 0) };
            // From near-level (regional DEMs, drainage) to road and path grades.
            foreach (double preset in new[] { 0.1, 0.5, 1, 2, 5, 10 })
            {
                var chip = new Button { Content = preset.ToString(System.Globalization.CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                chip.Classes.Add("chip");
                ToolTip.SetTip(chip, L.F("{0} % grade", preset));
                chip.Click += (_, _) => SetGrade(preset, (_neutralGrade.Value ?? 0) >= 0);
                presets.Children.Add(chip);
            }
            _neutralGradeHint.Foreground = AppTheme.TextSecondary;
            var gradeCard = new Border
            {
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children = { gradeHeader, _neutralDirection, _neutralGradeSlider, presets, _neutralGradeHint },
                },
            };
            gradeCard.Classes.Add("stat");
            gradeCard.Padding = new Thickness(10, 8, 10, 10);
            root.Children.Add(gradeCard);

            // 3 · limits: the length up front, tolerances and the search budget behind a disclosure.
            Grid FieldRow(string label, Control input, string? tip = null)
            {
                var text = new TextBlock { Text = T(label), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
                if (tip != null) ToolTip.SetTip(text, T(tip));
                Grid.SetColumn(input, 1);
                return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { text, input } };
            }
            root.Children.Add(FieldRow("Maximum length (m)", _neutralLength));
            var searchRow = FieldRow("Maximum search states", _neutralSearchStates, "How many partial routes the A → B search may examine before giving up.");
            searchRow.IsVisible = false;
            var advanced = new StackPanel
            {
                Spacing = 6,
                IsVisible = false,
                Margin = new Thickness(0, 2, 0, 0),
                Children =
                {
                    FieldRow("Height tolerance (m)", _neutralHeightTolerance, "Largest allowed deviation of the terrain from the target grade line, in metres."),
                    FieldRow("Grade tolerance (percentage points)", _neutralGradeTolerance, "Largest allowed deviation of any segment's grade from the target grade."),
                    searchRow,
                },
            };
            var chevron = AppTheme.Icon(AppTheme.Icons.ChevronRight);
            chevron.RenderTransform = new RotateTransform(0);
            var disclosure = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { chevron, new TextBlock { Text = T("Tolerances and search"), VerticalAlignment = VerticalAlignment.Center } } },
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            disclosure.Classes.Add("disclosure");
            disclosure.Click += (_, _) =>
            {
                advanced.IsVisible = !advanced.IsVisible;
                chevron.RenderTransform = new RotateTransform(advanced.IsVisible ? 90 : 0);
            };
            root.Children.Add(new StackPanel { Children = { disclosure, advanced } });

            // 4 · the one main action, the progress hairline and the guidance / result callout.
            _neutralAction.Click += (_, _) =>
            {
                if (_neutralStage is NeutralStage.PickStart or NeutralStage.PickEnd or NeutralStage.Working)
                {
                    _neutralCts?.Cancel();
                    _neutralPendingStart = null;
                    _view.ClearPath();
                    SetPathTool(PathTool.None);
                    SetNeutralStage(NeutralStage.Ready);
                    return;
                }
                _neutralCts?.Cancel();
                _neutralPendingStart = null;
                _view.ClearPath();
                SetPathTool(PathTool.NeutralLine);
                SetNeutralStage(NeutralStage.PickStart);
            };
            root.Children.Add(new StackPanel { Spacing = 0, Children = { _neutralAction, _neutralProgress } });
            _neutralCallout.Classes.Add("callout");
            _neutralRetry.Click += async (_, _) =>
            {
                if (_neutralRetry.Tag is not double suggested || _neutralLastStart is not { } start) return;
                _neutralGrade.Value = (decimal)(_neutralResultGrade < 0 ? -suggested : suggested);
                await TraceNeutralLineAsync(start.X, start.Y);
            };
            _neutralCallout.Child = new StackPanel { Spacing = 4, Children = { _neutralStatus, _neutralPoints, _neutralRetry } };
            root.Children.Add(_neutralCallout);

            // 5 · results: branch switch, the three checks as tiles, the profile and its exports.
            _neutralBranch.ItemsSource = new[] { T("Branch 1"), T("Branch 2") };
            _neutralBranch.SelectedIndex = 0;
            _neutralBranch.SelectionChanged += (_, _) => ShowNeutralBranch();
            var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*,6,*") };
            string[] statLabels = { T("Length"), T("max |Δz|"), T("max |Δgrade|") };
            for (int i = 0; i < 3; i++)
            {
                _neutralStatValues[i] = new TextBlock { Text = "—", FontSize = AppTheme.FontBody + 2, FontWeight = FontWeight.SemiBold, FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") }, TextTrimming = TextTrimming.CharacterEllipsis };
                var tile = new Border
                {
                    Child = new StackPanel
                    {
                        Spacing = 1,
                        Children = { new TextBlock { Text = statLabels[i], FontSize = AppTheme.FontCaption - 0.5, Foreground = AppTheme.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis }, _neutralStatValues[i] },
                    },
                };
                tile.Classes.Add("stat");
                Grid.SetColumn(tile, i * 2);
                stats.Children.Add(tile);
            }
            var csv = new Button { Content = "CSV…" };
            csv.Click += async (_, _) => await ProfileWindow.ExportCsvAsync(this, _neutralSeries, "m");
            var window = new Button { Content = T("Window…") };
            window.Click += (_, _) => { if (_neutralSeries.Length > 0) new ProfileWindow(_neutralSeries, "m", "m", _view.SetProfileHover).Show(this); };
            var exports = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { window, csv } };
            _neutralChart.HoverChanged += (_, e) => _view.SetProfileHover(e.Sample);
            _neutralResultPanel.Children.Add(AppTheme.SectionLabel(T("Result")));
            _neutralResultPanel.Children.Add(_neutralBranch);
            _neutralResultPanel.Children.Add(stats);
            _neutralResultPanel.Children.Add(_neutralChart);
            _neutralResultPanel.Children.Add(exports);
            _neutralResultPanel.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(260) },
            };
            root.Children.Add(_neutralResultPanel);

            var note = new TextBlock
            {
                Text = T("Elevation values must be in metres. Uses the bilinear terrain, including between vertices."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = AppTheme.FontCaption - 0.5,
                Foreground = AppTheme.TextSecondary,
            };
            root.Children.Add(note);

            _neutralMode.SelectionChanged += (_, _) =>
            {
                _neutralCts?.Cancel();
                _neutralPendingStart = null;
                _neutralRouteResult = null;
                _neutralResults = Array.Empty<NeutralLineBranch>();
                _neutralSeries = Array.Empty<ProfileSeries>();
                _neutralChart.SetSeries(_neutralSeries, "m", "m");
                _view.ClearPath();
                bool route = _neutralMode.SelectedIndex == 1;
                searchRow.IsVisible = route;
                _neutralPoints.Text = "";
                _neutralBranch.IsVisible = !route;
                _neutralBranch.ItemsSource = route ? new[] { T("Connection A → B") } : new[] { T("Branch 1"), T("Branch 2") };
                _neutralBranch.SelectedIndex = 0;
                // A new mode is a fresh start: picking resumes only from the main button.
                if (_view.PathToolMode == PathTool.NeutralLine) SetPathTool(PathTool.None);
                SetNeutralStats(null, null, null);
                SetNeutralStage(NeutralStage.Ready);
            };

            SyncGradeControls();
            SetNeutralStage(NeutralStage.Ready);
            return new TabItem { Header = T("Neutral line"), Content = new ScrollViewer { Content = root } };
        }

        /// <summary>
        /// A grade rounded to a readable step: 0.01 % below 1 %, 0.1 % below 10 %, else 0.5 %;
        /// <paramref name="down"/> rounds down (a suggestion must stay below the terrain slope).
        /// </summary>
        internal static double NiceGrade(double grade, bool down = false)
        {
            double step = grade < 1 ? 0.01 : grade < 10 ? 0.1 : 0.5;
            double units = grade / step;
            return Math.Round((down ? Math.Floor(units + 1e-9) : Math.Round(units)) * step, 3);
        }

        /// <summary>Mirrors the signed grade into the direction switch, the slider and the plain-language hint.</summary>
        private void SyncGradeControls()
        {
            double grade = (double)(_neutralGrade.Value ?? 0);
            double magnitude = Math.Abs(grade);
            if (_neutralDirection.SelectedIndex != (grade >= 0 ? 0 : 1)) _neutralDirection.SelectedIndex = grade >= 0 ? 0 : 1;
            double sliderValue = Math.Sqrt(Math.Min(SliderMaxGrade, magnitude) / SliderMaxGrade);
            _neutralSyncingGrade = true;
            if (Math.Abs(_neutralGradeSlider.Value - sliderValue) > 1e-9) _neutralGradeSlider.Value = sliderValue;
            _neutralSyncingGrade = false;
            _neutralGradeHint.Text = magnitude <= 0
                ? T("A level line: it follows a contour.")
                : L.F(grade > 0 ? "Rises {0:0.##} m per 100 m · 1 : {1:0.#}" : "Falls {0:0.##} m per 100 m · 1 : {1:0.#}", magnitude, 100 / magnitude);
        }

        /// <summary>Moves the workflow to <paramref name="stage"/>: stepper, main button, progress and callout follow.</summary>
        private void SetNeutralStage(NeutralStage stage, string? message = null)
        {
            _neutralStage = stage;
            bool route = _neutralMode.SelectedIndex == 1;
            _view.NeutralRouteMarkers = route;
            _view.NeutralGuideActive = stage == NeutralStage.PickEnd;

            // Stepper: grade → start (→ end) → result.
            var steps = route ? new[] { T("Grade"), T("Point A"), T("Point B"), T("Result") } : new[] { T("Grade"), T("Start"), T("Result") };
            int current = stage switch
            {
                NeutralStage.Ready => 0,
                NeutralStage.PickStart => 1,
                NeutralStage.PickEnd => 2,
                _ => steps.Length - 1,
            };
            bool finished = stage == NeutralStage.Done;
            bool failed = stage == NeutralStage.Failed;
            IBrush warn = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(0xE0, 0xA4, 0x58) : Color.FromRgb(0xB4, 0x70, 0x0F));
            _neutralStepper.Children.Clear();
            _neutralStepper.ColumnDefinitions.Clear();
            for (int i = 0; i < steps.Length; i++)
            {
                if (i > 0) _neutralStepper.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                _neutralStepper.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            }
            for (int i = 0; i < steps.Length; i++)
            {
                bool done = i < current || finished;
                bool active = i == current && !finished;
                var dot = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1.5),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = done ? "✓" : failed && active ? "!" : (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                        FontSize = AppTheme.FontCaption - 0.5,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = active ? Brushes.White : done ? AppTheme.Accent : AppTheme.TextSecondary,
                    },
                };
                // A failed run ends on an amber "!" instead of the accent step.
                var tone = failed && active ? warn : AppTheme.Accent;
                dot.Background = active ? tone : Brushes.Transparent;
                dot.BorderBrush = active || done ? tone : AppTheme.Border;
                if (active && !failed) dot.BoxShadow = BoxShadows.Parse(AppTheme.IsDark ? "0 0 0 3 #384FB3A9" : "0 0 0 3 #2E1D867C");
                var label = new TextBlock
                {
                    Text = steps[i],
                    FontSize = AppTheme.FontCaption - 0.5,
                    FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = active ? AppTheme.TextPrimary : AppTheme.TextSecondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var cell = new StackPanel { Spacing = 3, Children = { dot, label } };
                Grid.SetColumn(cell, i * 2);
                _neutralStepper.Children.Add(cell);
                if (i > 0)
                {
                    var line = new Border
                    {
                        Height = 1.5,
                        Margin = new Thickness(4, 9, 4, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        Background = i <= current || finished ? AppTheme.Accent : AppTheme.Border,
                    };
                    Grid.SetColumn(line, i * 2 - 1);
                    _neutralStepper.Children.Add(line);
                }
            }

            // Main button: start picking, or cancel while picking / computing, or pick again afterwards.
            bool busy = stage is NeutralStage.PickStart or NeutralStage.PickEnd or NeutralStage.Working;
            _neutralAction.Classes.Set("accent", !busy);
            _neutralAction.Content = AppTheme.IconLabel(busy ? AppTheme.Icons.Close : AppTheme.Icons.Target,
                busy ? T("Cancel") : stage is NeutralStage.Done or NeutralStage.Failed
                    ? T(route ? "Pick new points A and B" : "Pick a new start point")
                    : T(route ? "Pick points A and B on the map" : "Pick the start point on the map"),
                FontWeight.SemiBold);
            _neutralProgress.IsVisible = stage == NeutralStage.Working;
            _neutralRetry.IsVisible = false; // offered again by the result that warrants it
            // An outcome below the fold is easy to miss: bring it into view once laid out.
            if (stage is NeutralStage.Done or NeutralStage.Failed)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => (_neutralResultPanel.IsVisible ? (Control)_neutralResultPanel : _neutralCallout).BringIntoView(),
                    Avalonia.Threading.DispatcherPriority.Background);

            // Callout: what to do next, or the outcome.
            _neutralCallout.Classes.Set("warn", stage == NeutralStage.Failed);
            _neutralStatus.Text = message ?? stage switch
            {
                NeutralStage.PickStart => T(route ? "Click start point A on the map." : "Click the terrain to trace both branches."),
                NeutralStage.PickEnd => T("Start A selected. Click endpoint B."),
                NeutralStage.Working => T(route ? "Searching for a checked connection A → B…" : "Tracing both branches…"),
                _ => T(route ? "Set the grade, then pick points A and B: the line connects them at a constant grade." : "Set the grade, then pick a start point: the line runs both ways from it at a constant grade."),
            };
            _neutralPoints.IsVisible = !string.IsNullOrEmpty(_neutralPoints.Text);
            _neutralChart.EmptyMessage = stage == NeutralStage.Failed ? _neutralStatus.Text : "";

            bool showResults = stage == NeutralStage.Done && _neutralSeries.Length > 0;
            if (showResults && !_neutralResultPanel.IsVisible) { _neutralResultPanel.Opacity = 0; _neutralResultPanel.IsVisible = true; _neutralResultPanel.Opacity = 1; }
            else if (!showResults) _neutralResultPanel.IsVisible = false;
        }

        /// <summary>Fills the three result tiles (dashes when there is no result).</summary>
        private void SetNeutralStats(double? length, double? heightError, double? gradeError)
        {
            _neutralStatValues[0].Text = length is { } l ? L.F("{0:N0} m", l) : "—";
            _neutralStatValues[1].Text = heightError is { } h ? L.F("{0:0.###} m", h) : "—";
            _neutralStatValues[2].Text = gradeError is { } g ? L.F("{0:0.###} pp", g) : "—";
        }


        private async Task TraceNeutralLineAsync(double x, double y)
        {
            _neutralCts?.Cancel();
            _neutralCts?.Dispose();
            var cts = _neutralCts = new CancellationTokenSource();
            var token = cts.Token;
            var loaded = await NeutralSourceAsync();
            if (loaded == null || token.IsCancellationRequested) return;
            var source = loaded.Value.Source;
            int sourceBand = source.ActiveBand;
            if (_neutralGrade.Value == null || _neutralLength.Value == null || _neutralHeightTolerance.Value == null || _neutralGradeTolerance.Value == null) return;
            var options = new NeutralLineOptions
            {
                GradePercent = (double)_neutralGrade.Value.Value,
                MaximumLength = (double)_neutralLength.Value.Value,
                HeightTolerance = (double)_neutralHeightTolerance.Value.Value,
                GradeTolerancePercent = (double)_neutralGradeTolerance.Value.Value,
            };
            _neutralRouteResult = null;
            _neutralResults = Array.Empty<NeutralLineBranch>();
            _neutralSeries = Array.Empty<ProfileSeries>();
            _neutralChart.SetSeries(_neutralSeries, "m", "m");
            _neutralPoints.Text = L.F("Start: {0:0.##}, {1:0.##}", x, y);
            _view.SetPath(new[] { (x, y) });
            SetNeutralStage(NeutralStage.Working);
            try
            {
                var results = await Task.Run(() => NeutralLineTracer.Trace(loaded.Value.Raster, source.Document.GeoReference, x, y, options, token), token);
                if (token.IsCancellationRequested) return;
                if (!ReferenceEquals(source, _view.ActiveLayer) || !_view.Layers.Contains(source) || source.ActiveBand != sourceBand)
                { SetNeutralStage(NeutralStage.Failed, T("Source layer changed; select a new start point.")); return; }
                _neutralResults = results;
                _neutralResultGrade = options.GradePercent;
                _neutralSourceName = source.Name;
                _neutralLastStart = (x, y);
                _neutralStartSlope = NeutralLineTracer.SlopePercentAt(loaded.Value.Raster, source.Document.GeoReference, x, y);
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
                if (_neutralBranch.SelectedIndex != 0) _neutralBranch.SelectedIndex = 0; // also redraws
                else ShowNeutralBranch();
            }
            catch (OperationCanceledException) { if (ReferenceEquals(_neutralCts, cts)) SetNeutralStage(NeutralStage.Ready, T("Cancelled.")); }
            catch (Exception ex) { if (ReferenceEquals(_neutralCts, cts)) SetNeutralStage(NeutralStage.Failed, ex.Message); }
        }

        private async Task<(Raster Raster, RasterLayer Source)?> NeutralSourceAsync()
        {
            UseElevationSourceForTerrain();
            var loaded = await TryGetLoadedRasterAsync(T("Neutral line"));
            if (loaded == null) return null;
            var source = _view.ActiveLayer!;
            var bands = source.Document.Header.RasterInfo.Bands;
            string? zUnits = source.ActiveBand < bands.Count ? bands[source.ActiveBand].Units : null;
            var (usable, note) = NeutralUnits(source.Document.Header.CoordinateSpace, zUnits);
            if (!usable)
            {
                await MessageAsync(T("Neutral line"), T("Neutral lines need horizontal and elevation units in metres: geographic (degree) coordinates and feet are not supported."));
                return null;
            }
            _neutralUnitNote = note;
            return (loaded.Value.Raster, source);
        }

        /// <summary>The unit assumption shown under a result (null when the header states metres).</summary>
        private string? _neutralUnitNote;

        /// <summary>
        /// Whether a terrain model can carry a neutral line, which needs metres both ways (a grade is
        /// metres per metre). Only what is known to be wrong is refused — degree coordinates and feet;
        /// a grid without a projection, a local or unrecognised projection, "natural" units or a
        /// free-text elevation unit are taken as metres, and the returned note says so.
        /// </summary>
        internal static (bool Usable, string? Note) NeutralUnits(CoordinateSpace space, string? zUnits)
        {
            static string Key(string? unit) => (unit ?? "").Trim().ToUpperInvariant().Replace(' ', '_');
            static bool IsMetres(string key) => key is "M" or "METER" or "METERS" or "METRE" or "METRES";
            static bool IsForeign(string key) => key is "FEET" or "FOOT" or "FT" or "US_SURVEY_FEET" or "USFEET" or "US_FT"
                or "DEGREES" or "DEGREE" or "DEG" or "KILOMETERS" or "KILOMETRES" or "KM";

            if (space.CoordinateType == ErsCoordinateType.Ll) return (false, null);
            string horizontal = Key(space.Units);
            string vertical = Key(zUnits);
            if (IsForeign(horizontal) || IsForeign(vertical)) return (false, null);

            var notes = new List<string>();
            bool statedMetres = IsMetres(horizontal) || space.IsRaw || space.EpsgCode != null;
            if (!statedMetres) notes.Add(T("The header names no metric projection: map units are taken as metres."));
            if (vertical.Length > 0 && !IsMetres(vertical)) notes.Add(L.F("Elevation unit \"{0}\" is taken as metres.", zUnits!.Trim()));
            return (true, notes.Count == 0 ? null : string.Join("\n", notes));
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
            if (_neutralRouteResult != null) { ShowNeutralRoute(); return; }
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
            SetNeutralStats(branch.Length, branch.MaximumHeightError, branch.MaximumGradeErrorPercent);
            string message = _neutralSourceName + " · " + T(branch.Stop switch
            {
                NeutralLineStop.MaximumLength => "Requested length reached.",
                NeutralLineStop.BoundaryOrNoData => "Stopped at the data boundary or NoData.",
                NeutralLineStop.InsufficientSlope => "Terrain is too gentle for the requested grade.",
                NeutralLineStop.FlatSurface => "Flat terrain: no unique tracing direction.",
                NeutralLineStop.StepLimit => "Step limit reached; partial result.",
                _ => "No further forward step within tolerance; partial result.",
            });
            // Stopped (almost) at once on gentle terrain: say what grade the start point can carry.
            if (branch.Stop == NeutralLineStop.InsufficientSlope && _neutralStartSlope is { } slope && slope < Math.Abs(_neutralResultGrade))
                message += "\n" + L.F("The terrain at the start point is at most {0:0.###} % steep: a neutral line here needs a grade below that.", slope);
            if (_neutralUnitNote != null) message += "\n" + _neutralUnitNote;
            // Nothing traced on either side: a failure, not a result — with a grade that fits, one click away.
            if (_neutralResults.All(b => b.Length <= 0))
            {
                SetNeutralStats(null, null, null);
                SetNeutralStage(NeutralStage.Failed, message);
                if (_neutralStartSlope is { } fit && NiceGrade(fit * 0.8, down: true) is var suggested && suggested > 0)
                {
                    _neutralRetry.Tag = suggested;
                    _neutralRetry.Content = AppTheme.IconLabel(AppTheme.Icons.Target, L.F("Retry here at {0:0.###} %", suggested));
                    _neutralRetry.IsVisible = true;
                }
                return;
            }
            SetNeutralStage(NeutralStage.Done, message);
            // A branch cut short is still shown, but the callout turns amber.
            _neutralCallout.Classes.Set("warn", branch.Stop != NeutralLineStop.MaximumLength);
        }
    }
}
