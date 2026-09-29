using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RasterField.ErMapper;
using RasterField.Rasters;
using RasterField.Rendering;
using RasterField.Vectors;
using static RasterField.L;

namespace RasterField
{
    /// <summary>
    /// The three-column mini-GIS layout (docs/UI-UX-TERV.md §3): layers + analysis on the left,
    /// the map in the middle, the active layer's properties on the right; plus the layer cards,
    /// the path tools' analysis views, the interactive histogram and the command palette.
    /// </summary>
    public sealed partial class MainWindow
    {
        private Border? _leftPanelBorder;
        private ColumnDefinition? _leftColumn, _rightColumn;
        private GridSplitter? _leftSplitter, _rightSplitter;
        private readonly TabControl _analysisTabs = new TabControl { Padding = new Thickness(0) };
        private readonly SelectableTextBlock _identifyText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption + 0.5, LineHeight = 17 };
        private readonly ProfileChartControl _profileChart = new ProfileChartControl { MinHeight = 120 };
        private readonly CheckBox _profileBezierBox = new CheckBox { IsChecked = true };
        private readonly CheckBox _profileAllLayersBox = new CheckBox { IsChecked = true };
        private readonly CheckBox _profileDeltaBox = new CheckBox { IsChecked = false };
        private readonly TextBlock _profileDeltaText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption, VerticalAlignment = VerticalAlignment.Center };
        private readonly SelectableTextBlock _measureText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption + 0.5, LineHeight = 17 };
        private readonly HistogramStretchControl _histogram = new HistogramStretchControl { Height = 70, Margin = new Thickness(0, 4, 0, 2) };
        private readonly Slider _opacitySlider = new Slider { Minimum = 0, Maximum = 100, Value = 100 };
        private readonly TextBlock _opacityText = new TextBlock { Width = 40, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, FontSize = AppTheme.FontCaption };
        private readonly ComboBox _blendBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly Border _derivationGroup = new Border { IsVisible = false };
        private readonly TextBlock _lineageText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption, LineHeight = 16 };
        private readonly Button _paramsBtn = new Button();
        private readonly Button _saveLayerBtn = new Button();
        private readonly SelectableTextBlock _metaText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption, LineHeight = 16 };
        private readonly StackPanel _rasterProps = new StackPanel { Spacing = 4 };
        private IReadOnlyList<ProfileSeries> _lastProfile = Array.Empty<ProfileSeries>();

        private static readonly FilePickerFileType[] CsvTypes = { new("CSV (*.csv)") { Patterns = new[] { "*.csv" } } };
        private static readonly FilePickerFileType[] GeoJsonTypes = { new("GeoJSON (*.geojson)") { Patterns = new[] { "*.geojson", "*.json" } } };
        private static readonly LayerBlendMode[] BlendModes = Enum.GetValues<LayerBlendMode>();

        // ---- layout -----------------------------------------------------------------

        private DockPanel BuildLayout()
        {
            var root = new DockPanel();

            // Windows/Linux: the horizontal in-window menu strip on top; macOS uses the system menu bar.
            var menu = OperatingSystem.IsMacOS() ? null : BuildMenu();
            _menuHost = menu;
            if (menu != null) { DockPanel.SetDock(menu, Dock.Top); root.Children.Add(menu); }
            var statusBar = BuildStatusBar();
            _statusBarHost = statusBar;
            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            _leftColumn = new ColumnDefinition(new GridLength(Math.Max(0, _settings.LeftDockWidth)));
            _rightColumn = new ColumnDefinition(new GridLength(Math.Max(0, _settings.RightDockWidth)));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(_leftColumn);
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(_rightColumn);

            var left = BuildLeftDock();
            _leftPanelBorder = left;
            Grid.SetColumn(left, 0);

            _leftSplitter = new GridSplitter { Width = 3, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(_leftSplitter, 1);

            var viewHost = new Grid();
            viewHost.Children.Add(_view);
            viewHost.Children.Add(BuildClipBar());
            viewHost.Children.Add(BuildPathBar());
            viewHost.Children.Add(BuildBusyOverlay());
            Grid.SetColumn(viewHost, 2);

            _rightSplitter = new GridSplitter { Width = 3, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(_rightSplitter, 3);

            var right = BuildRightDock();
            _sidePanelBorder = right;
            Grid.SetColumn(right, 4);

            grid.Children.Add(left);
            grid.Children.Add(_leftSplitter);
            grid.Children.Add(viewHost);
            grid.Children.Add(_rightSplitter);
            grid.Children.Add(right);
            ApplyDockVisibility();

            root.Children.Add(grid);
            return root;
        }

        private Menu? _menuHost;

        private static TextBlock Header(string text) => AppTheme.SectionLabel(text);

        private Border BuildLeftDock()
        {
            var addLayerBtn = new Button { Content = T("+ Add layer…"), HorizontalAlignment = HorizontalAlignment.Stretch };
            addLayerBtn.Click += async (_, _) => await AddLayerDialogAsync();

            var layersStack = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 10, 8) };
            layersStack.Children.Add(Header(T("Layers (top = drawn in front)")));
            layersStack.Children.Add(_layersPanel);
            layersStack.Children.Add(addLayerBtn);
            var layersScroll = new ScrollViewer { Content = layersStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            // Analysis tabs: identify / profile / measure & zone.
            var identifyTab = new TabItem
            {
                Header = T("Identify"),
                Content = new ScrollViewer { Content = new Border { Padding = new Thickness(10, 8), Child = _identifyText } },
            };
            _identifyText.Text = T("Tools ▸ Identify (I), then click the map.");

            _profileBezierBox.Content = T("Bézier curve");
            _profileAllLayersBox.Content = T("All visible layers");
            _profileBezierBox.IsCheckedChanged += (_, _) => UpdateProfile();
            _profileAllLayersBox.IsCheckedChanged += (_, _) => UpdateProfile();
            _profileDeltaBox.Content = T("Δ Bézier − bilinear");
            ToolTip.SetTip(_profileDeltaBox, T("Plot the difference between the Bézier and the bilinear profile of the active layer, scaled to fit — on a smooth surface the two curves otherwise overlap."));
            _profileDeltaBox.IsCheckedChanged += (_, _) => UpdateProfile();
            var profileWindowBtn = new Button { Content = T("Window…") };
            profileWindowBtn.Click += (_, _) => { if (_lastProfile.Count > 0) new ProfileWindow(_lastProfile, DistanceUnit(), UnitLabel()).Show(this); };
            var profileCsvBtn = new Button { Content = "CSV…" };
            profileCsvBtn.Click += async (_, _) => await ProfileWindow.ExportCsvAsync(this, _lastProfile, DistanceUnit());
            var profileStart = new Button { Content = T("Draw profile (P)") };
            profileStart.Click += (_, _) => SetPathTool(PathTool.Profile);
            var profilePanel = new DockPanel { Margin = new Thickness(10, 6, 10, 10) };
            var profileOptions = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var c in new Control[] { profileStart, _profileBezierBox, _profileAllLayersBox, profileWindowBtn, profileCsvBtn })
            {
                c.Margin = new Thickness(0, 0, 6, 4);
                profileOptions.Children.Add(c);
            }
            DockPanel.SetDock(profileOptions, Dock.Top);
            profilePanel.Children.Add(profileOptions);
            // Under the chart: the Δ toggle and the measured Bézier − bilinear difference.
            var deltaRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            _profileDeltaBox.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(_profileDeltaBox, Dock.Left);
            deltaRow.Children.Add(_profileDeltaBox);
            deltaRow.Children.Add(_profileDeltaText);
            DockPanel.SetDock(deltaRow, Dock.Bottom);
            profilePanel.Children.Add(deltaRow);
            profilePanel.Children.Add(_profileChart);
            var profileTab = new TabItem { Header = T("Profile"), Content = profilePanel };

            var measureBtn = new Button { Content = T("Measure (M)") };
            measureBtn.Click += (_, _) => SetPathTool(PathTool.Measure);
            var zoneBtn = new Button { Content = T("Zone (Z)") };
            zoneBtn.Click += (_, _) => SetPathTool(PathTool.Zone);
            var zonalLayerBtn = new Button { Content = T("By polygon layer…") };
            zonalLayerBtn.Click += async (_, _) => await ZonalByLayerAsync(null);
            var measureTools = new WrapPanel();
            foreach (var c in new Control[] { measureBtn, zoneBtn, zonalLayerBtn })
            {
                c.Margin = new Thickness(0, 0, 6, 4);
                measureTools.Children.Add(c);
            }
            _measureText.Text = T("Measure: click points on the map; double-click to close the polygon for its area.\nZone: draw a polygon to get the statistics of every visible raster inside it.");
            var measurePanel = new StackPanel { Margin = new Thickness(10, 8), Spacing = 8, Children = { measureTools, _measureText } };
            var measureTab = new TabItem { Header = T("Measure / zone"), Content = new ScrollViewer { Content = measurePanel } };

            _analysisTabs.ItemsSource = new[] { identifyTab, profileTab, measureTab };
            _analysisTabs.SelectedIndex = 0;

            var split = new Grid { RowDefinitions = new RowDefinitions("*,Auto,*") };
            Grid.SetRow(layersScroll, 0);
            var hSplitter = new GridSplitter { Height = 5, ResizeDirection = GridResizeDirection.Rows };
            Grid.SetRow(hSplitter, 1);
            var analysisHost = new DockPanel();
            var analysisHeader = Header(T("Analysis"));
            analysisHeader.Margin = new Thickness(10, 10, 10, 0);
            DockPanel.SetDock(analysisHeader, Dock.Top);
            analysisHost.Children.Add(analysisHeader);
            analysisHost.Children.Add(_analysisTabs);
            Grid.SetRow(analysisHost, 2);
            split.Children.Add(layersScroll);
            split.Children.Add(hSplitter);
            split.Children.Add(analysisHost);

            return new Border { Background = AppTheme.PanelBackground, BorderBrush = AppTheme.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = split };
        }

        private Border BuildRightDock()
        {
            var stack = new StackPanel { Spacing = 2, Margin = new Thickness(14, 2, 14, 10) };

            // Band / RGB
            var bandStack = new StackPanel { Spacing = 4 };
            bandStack.Children.Add(Header(T("Band")));
            bandStack.Children.Add(_bandBox);
            _rgbCompositeBox.Content = T("True colour (RGB composite)");
            bandStack.Children.Add(_rgbCompositeBox);
            _bandGroup.Child = bandStack;
            _bandGroup.IsVisible = false;

            _reverseBox.Content = T("Reverse palette");
            _rasterProps.Children.Add(_bandGroup);
            _rasterProps.Children.Add(Header(T("Palette")));
            _rasterProps.Children.Add(_paletteBox);
            _rasterProps.Children.Add(_reverseBox);
            _rasterProps.Children.Add(Header(T("Palette mode")));
            _rasterProps.Children.Add(_modeBox);
            _rasterProps.Children.Add(Header(T("Stretch")));
            _rasterProps.Children.Add(_stretchBox);

            _histogram.RangeChanged += (min, max) =>
            {
                _syncing = true;
                _minBox.Value = (decimal)min;
                _maxBox.Value = (decimal)max;
                _syncing = false;
                if (_stretchBox.SelectedIndex != 3) { _syncing = true; _stretchBox.SelectedIndex = 3; _syncing = false; }
                _view.SetValueRange(min, max);
                _legend.SetColorizer(_view.Colorizer, UnitLabel());
                _histogram.SetColorizer(_view.Colorizer);
            };
            ToolTip.SetTip(_histogram, T("Drag the two handles to set the stretch (min / max)."));
            _rasterProps.Children.Add(_histogram);

            var minMax = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
            var minWrap = new StackPanel();
            minWrap.Children.Add(AppTheme.Caption(T("Min")));
            minWrap.Children.Add(_minBox);
            var maxWrap = new StackPanel();
            maxWrap.Children.Add(AppTheme.Caption(T("Max")));
            maxWrap.Children.Add(_maxBox);
            Grid.SetColumn(minWrap, 0);
            Grid.SetColumn(maxWrap, 2);
            minMax.Children.Add(minWrap);
            minMax.Children.Add(maxWrap);
            _rasterProps.Children.Add(minMax);

            _rasterProps.Children.Add(Header(T("Gamma")));
            _rasterProps.Children.Add(_gammaSlider);

            // Layer compositing
            _rasterProps.Children.Add(Header(T("Layer")));
            var opacityRow = new DockPanel();
            DockPanel.SetDock(_opacityText, Dock.Right);
            opacityRow.Children.Add(_opacityText);
            opacityRow.Children.Add(_opacitySlider);
            _rasterProps.Children.Add(AppTheme.Caption(T("Opacity")));
            _rasterProps.Children.Add(opacityRow);
            _rasterProps.Children.Add(AppTheme.Caption(T("Blend mode")));
            _blendBox.ItemsSource = BlendModes.Select(m => T(m switch
            {
                LayerBlendMode.Multiply => "Multiply (for hillshade)",
                LayerBlendMode.SoftLight => "Soft light",
                _ => m.ToString(),
            })).ToList();
            _rasterProps.Children.Add(_blendBox);
            _opacitySlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                _opacityText.Text = $"{_opacitySlider.Value:0} %";
                if (!_syncing && _view.ActiveLayer != null) _view.SetLayerOpacity(_view.ActiveLayer, _opacitySlider.Value / 100.0);
            };
            _blendBox.SelectionChanged += (_, _) =>
            {
                if (!_syncing && _view.ActiveLayer != null && _blendBox.SelectedIndex >= 0)
                    _view.SetLayerBlendMode(_view.ActiveLayer, BlendModes[_blendBox.SelectedIndex]);
            };

            // Derivation (recipe)
            _paramsBtn.Content = T("Parameters…");
            _paramsBtn.Click += async (_, _) => { if (_view.ActiveLayer != null) await EditRecipeAsync(_view.ActiveLayer); };
            _saveLayerBtn.Content = T("Save…");
            _saveLayerBtn.Click += async (_, _) => { if (_view.ActiveLayer != null) await SaveRasterLayerAsync(_view.ActiveLayer); };
            _derivationGroup.Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    Header(T("Derivation")),
                    _lineageText,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _paramsBtn, _saveLayerBtn } },
                },
            };
            _rasterProps.Children.Add(_derivationGroup);

            stack.Children.Add(_rasterProps);

            // Information
            var headerBtn = new Button { Content = T("Full ERS header…") };
            headerBtn.Click += async (_, _) => await ShowHeaderAsync();
            stack.Children.Add(Header(T("Information")));
            stack.Children.Add(_metaText);
            stack.Children.Add(headerBtn);

            stack.Children.Add(Header(T("Save / export format")));
            stack.Children.Add(AppTheme.Caption(T("Output cell type")));
            stack.Children.Add(_outTypeBox);
            stack.Children.Add(AppTheme.Caption(T("Output byte order")));
            stack.Children.Add(_outOrderBox);

            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var legendGroup = new DockPanel { Margin = new Thickness(8, 0, 8, 8) };
            var legendLabel = AppTheme.SectionLabel(T("Legend"));
            legendLabel.Margin = new Thickness(4, 8, 0, 4);
            DockPanel.SetDock(_legend, Dock.Bottom);
            DockPanel.SetDock(legendLabel, Dock.Bottom);
            _legend.Height = 136;
            legendGroup.Children.Add(_legend);
            legendGroup.Children.Add(legendLabel);
            legendGroup.Children.Add(scroll);

            return new Border { Background = AppTheme.PanelBackground, BorderBrush = AppTheme.Border, BorderThickness = new Thickness(1, 0, 0, 0), Child = legendGroup };
        }

        // ---- docks ------------------------------------------------------------------

        private bool _leftDockVisible = true, _rightDockVisible = true;

        private void ToggleLeftDock() { _leftDockVisible = !_leftDockVisible; ApplyDockVisibility(); }
        private void ToggleRightDock() { _rightDockVisible = !_rightDockVisible; ApplyDockVisibility(); }

        private void ToggleMapOnly()
        {
            bool anyVisible = _leftDockVisible || _rightDockVisible;
            _leftDockVisible = _rightDockVisible = !anyVisible;
            ApplyDockVisibility();
        }

        private void ApplyDockVisibility()
        {
            if (_leftColumn == null || _rightColumn == null) return;
            if (_leftPanelBorder != null) _leftPanelBorder.IsVisible = _leftDockVisible;
            if (_sidePanelBorder != null) _sidePanelBorder.IsVisible = _rightDockVisible;
            if (_leftSplitter != null) _leftSplitter.IsVisible = _leftDockVisible;
            if (_rightSplitter != null) _rightSplitter.IsVisible = _rightDockVisible;
            _leftColumn.Width = _leftDockVisible ? new GridLength(Math.Max(180, _settings.LeftDockWidth)) : new GridLength(0);
            _rightColumn.Width = _rightDockVisible ? new GridLength(Math.Max(200, _settings.RightDockWidth)) : new GridLength(0);
            RefreshMenuChecks();
        }

        private void RememberDockWidths()
        {
            if (_leftColumn != null && _leftDockVisible) _settings.LeftDockWidth = _leftColumn.ActualWidth;
            if (_rightColumn != null && _rightDockVisible) _settings.RightDockWidth = _rightColumn.ActualWidth;
        }

        // ---- path tools (profile / measure / zone) -----------------------------------------

        private Border BuildPathBar()
        {
            var finishBtn = new Button { Content = T("Finish (Enter)") };
            finishBtn.Click += (_, _) => _view.FinishPath();
            var clearBtn = new Button { Content = T("Clear") };
            clearBtn.Click += (_, _) => _view.ClearPath();
            var closeBtn = new Button { Content = T("Close") };
            closeBtn.Click += (_, _) => SetPathTool(PathTool.None);

            var row = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { finishBtn, clearBtn, closeBtn } };
            DockPanel.SetDock(buttons, Dock.Right);
            DockPanel.SetDock(_pathTitleText, Dock.Left);
            _pathTitleText.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(buttons);
            row.Children.Add(_pathTitleText);
            row.Children.Add(_pathInfoText);
            _pathBar.Child = row;
            return _pathBar;
        }

        private void SetPathTool(PathTool tool)
        {
            if (tool != PathTool.None)
            {
                SetClipToolActive(false);
                _view.IdentifyMode = false;
                if (!_leftDockVisible) { _leftDockVisible = true; ApplyDockVisibility(); }
                _analysisTabs.SelectedIndex = tool == PathTool.Profile ? 1 : 2;
            }
            _view.PathToolMode = tool;
            _pathBar.IsVisible = tool != PathTool.None;
            _pathTitleText.Text = tool switch
            {
                PathTool.Profile => T("Profile —"),
                PathTool.Measure => T("Measure —"),
                PathTool.Zone => T("Zone —"),
                _ => "",
            };
            OnPathChanged();
            RefreshMenuChecks();
        }

        private void OnPathChanged()
        {
            var tool = _view.PathToolMode;
            int n = _view.CurrentPath.Count;
            _pathInfoText.Text = n == 0
                ? T("click to add points · drag a point to move it · drag a segment midpoint to insert a point · double-click / right-click / Enter to finish · Backspace removes the last point")
                : L.F("{0} point(s) — double-click / Enter to finish, Backspace to undo a point, Esc to clear", n);
            switch (tool)
            {
                case PathTool.Profile: UpdateProfile(); break;
                case PathTool.Measure: UpdateMeasure(); break;
                case PathTool.Zone: UpdateZone(); break;
            }
        }

        private string DistanceUnit() => _view.Document?.Header.CoordinateSpace.EffectiveUnits ?? "m";

        private static readonly Color[] SeriesColors =
        {
            Color.FromRgb(80, 200, 255), Color.FromRgb(255, 170, 60), Color.FromRgb(140, 230, 120),
            Color.FromRgb(240, 110, 200), Color.FromRgb(200, 200, 90),
        };

        private void UpdateProfile()
        {
            var path = _view.CurrentPath.ToList();
            var active = _view.ActiveLayer;
            var series = new List<ProfileSeries>();
            IReadOnlyList<ProfileSample>? bilinear = null, bezier = null;
            if (path.Count >= 2 && active != null && !active.IsFrame)
            {
                // Bézier and bilinear agree at the cell centres and differ only between them, so
                // sample at least 4 points per cell of the active layer (capped for responsiveness).
                double length = Measurement.Length(path);
                var (_, gb, gc, _, ge, gf) = active.Document.GeoReference.GeoTransform;
                double cell = Math.Min(Math.Sqrt(gb * gb + ge * ge), Math.Sqrt(gc * gc + gf * gf));
                double spacing = Math.Max(Math.Min(length / 600.0, cell / 4.0), Math.Max(length / 8000.0, 1e-9));
                var layers = _profileAllLayersBox.IsChecked == true
                    ? _view.DrawOrder.OfType<RasterLayer>().Where(l => l.IsVisible && !l.IsFrame).Reverse().ToList()
                    : new List<RasterLayer> { active };
                if (!layers.Contains(active)) layers.Insert(0, active);
                layers.Remove(active);
                layers.Insert(0, active);

                int ci = 0;
                foreach (var layer in layers)
                {
                    var raster = layer.Raster;
                    if (raster == null || layer.ShowRgbComposite) continue; // streaming / RGB layers aren't profiled
                    var geo = layer.Document.GeoReference;
                    if (!geo.IsInvertible) continue;
                    var samples = RasterProfiler.SamplePolylineWorld(raster, geo, path, spacing);
                    series.Add(new ProfileSeries(layer.Name, samples, SeriesColors[ci++ % SeriesColors.Length]));
                    if (ReferenceEquals(layer, active) && _profileBezierBox.IsChecked == true)
                    {
                        var o = new BezierPatchOptions();
                        bilinear = samples;
                        bezier = RasterProfiler.SamplePolylineWorld(raster, geo, path, spacing, (r, c, row) => BezierPatchInterpolator.Sample(r, c, row, o));
                        series.Add(new ProfileSeries(layer.Name + " · " + T("Bézier"), bezier,
                            AppTheme.IsDark ? Colors.White : Color.FromRgb(0x1F, 0x26, 0x30), dashed: true));
                    }
                }
            }
            _lastProfile = series; // CSV, the profile window and the PDF report always get the real profile

            _profileDeltaBox.IsEnabled = _profileBezierBox.IsChecked == true;
            var delta = bilinear != null && bezier != null ? ProfileDifference(bilinear, bezier) : null;
            string unit = UnitLabel() ?? "";
            if (delta == null || !delta.Any(s => s.Value.HasValue))
            {
                _profileDeltaText.IsVisible = false;
                _profileChart.SetSeries(series, DistanceUnit(), UnitLabel());
                return;
            }

            var values = delta.Where(s => s.Value.HasValue).Select(s => Math.Abs(s.Value!.Value)).ToList();
            _profileDeltaText.IsVisible = true;
            _profileDeltaText.Foreground = AppTheme.TextSecondary;
            _profileDeltaText.Text = L.F("max |Δ| {0:g3} {1} · mean {2:g3} {1}", values.Max(), unit, values.Average()).Replace("  ", " ", StringComparison.Ordinal);
            _profileChart.SetSeries(
                _profileDeltaBox.IsChecked == true
                    ? new[] { new ProfileSeries(T("Δ Bézier − bilinear"), delta, AppTheme.IsDark ? AppTheme.DarkAccentColor : AppTheme.LightAccentColor) }
                    : series,
                DistanceUnit(), UnitLabel());
        }

        /// <summary>Sample-by-sample <paramref name="bezier"/> − <paramref name="bilinear"/> (same path, same spacing).</summary>
        private static List<ProfileSample> ProfileDifference(IReadOnlyList<ProfileSample> bilinear, IReadOnlyList<ProfileSample> bezier) =>
            bilinear.Zip(bezier, (a, b) => new ProfileSample(a.Distance, a.X, a.Y,
                a.Value.HasValue && b.Value.HasValue ? b.Value.Value - a.Value.Value : null)).ToList();

        private void UpdateMeasure()
        {
            var path = _view.CurrentPath.ToList();
            string unit = DistanceUnit();
            if (path.Count < 2) { _measureText.Text = T("Click at least two points on the map."); return; }

            var sb = new StringBuilder();
            sb.AppendLine(L.F("Length: {0:N2} {1}", Measurement.Length(path), unit));
            var active = _view.ActiveLayer;
            if (active?.Raster != null && !active.IsFrame && active.Document.GeoReference.IsInvertible)
            {
                double surface = Measurement.SurfaceLength(active.Raster, active.Document.GeoReference, path, Math.Max(Measurement.Length(path) / 1000.0, 1e-9));
                sb.AppendLine(L.F("Surface length over “{0}”: {1:N2} {2}", active.Name, surface, unit));
            }
            if (path.Count >= 3)
            {
                sb.AppendLine(L.F("Perimeter (closed): {0:N2} {1}", Measurement.Perimeter(path), unit));
                sb.AppendLine(L.F("Area (closed): {0:N2} {1}²", Measurement.Area(path), unit));
            }
            if (!_view.IsPathFinished) sb.AppendLine().Append(T("(still drawing — double-click to finish)"));
            _measureText.Text = sb.ToString().TrimEnd();
        }

        private void UpdateZone()
        {
            var path = _view.CurrentPath.ToList();
            if (path.Count < 3) { _measureText.Text = T("Draw a polygon with at least three points."); return; }
            string unit = DistanceUnit();
            var sb = new StringBuilder();
            sb.AppendLine(L.F("Zone area: {0:N2} {1}² · perimeter {2:N2} {1}", Measurement.Area(path), unit, Measurement.Perimeter(path)));
            foreach (var layer in _view.DrawOrder.OfType<RasterLayer>().Where(l => l.IsVisible && !l.IsFrame).Reverse())
            {
                sb.AppendLine();
                if (layer.Raster == null) { sb.AppendLine(layer.Name + ": " + T("streaming layer — clip it first for zonal statistics")); continue; }
                if (!layer.Document.GeoReference.IsInvertible) continue;
                var z = ZonalStatistics.Compute(layer.Raster, layer.Document.GeoReference, path);
                sb.AppendLine(layer.Name);
                sb.AppendLine(z.Count == 0
                    ? "  " + T("no valid cells inside")
                    : L.F("  cells {0:N0} (+{1:N0} no-data) · min {2:g6} · max {3:g6}\n  mean {4:g6} · σ {5:g6} · sum {6:g6}", z.Count, z.NoDataCount, z.Minimum, z.Maximum, z.Mean, z.StandardDeviation, z.Sum));
            }
            _measureText.Text = sb.ToString().TrimEnd();
        }

        /// <summary>Zonal statistics of the active raster for every polygon of a vector layer, as a table with CSV export.</summary>
        private async Task ZonalByLayerAsync(VectorLayer? layer)
        {
            var raster = _view.ActiveLayer;
            if (raster?.Raster == null || raster.IsFrame)
            {
                await MessageAsync(T("Zonal statistics"), T("Select a (fully loaded) raster layer first."));
                return;
            }
            layer ??= _view.VectorLayers.FirstOrDefault(v => v.Document.Objects.OfType<VectorPolyObject>().Any(p => p is VectorPolygon or VectorMapPolygon));
            if (layer == null)
            {
                await MessageAsync(T("Zonal statistics"), T("Add a vector layer with polygons (e.g. .erv, GeoJSON) first."));
                return;
            }

            var polygons = layer.Document.Objects.OfType<VectorPolyObject>().Where(p => (p is VectorPolygon or VectorMapPolygon) && p.Points.Count >= 3).ToList();
            var boxes = layer.Document.Objects.OfType<VectorBox>().ToList();
            var geo = raster.Document.GeoReference;
            var rows = new List<string> { "zone,attribute,cells,nodata,min,max,mean,std,sum,area" };
            var text = new StringBuilder();
            int i = 0;
            IEnumerable<(string? Attr, IReadOnlyList<(double X, double Y)> Ring)> zones =
                polygons.Select(p => (p.Attribute, (IReadOnlyList<(double X, double Y)>)p.Points.ToList()))
                .Concat(boxes.Select(b => (b.Attribute, (IReadOnlyList<(double X, double Y)>)new[] { (b.Ltx, b.Lty), (b.Rbx, b.Lty), (b.Rbx, b.Rby), (b.Ltx, b.Rby) })));
            foreach (var (attr, ring) in zones)
            {
                i++;
                var z = ZonalStatistics.Compute(raster.Raster, geo, ring);
                rows.Add(string.Join(",", i, "\"" + (attr ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", z.Count, z.NoDataCount,
                    Inv(z.Minimum), Inv(z.Maximum), Inv(z.Mean), Inv(z.StandardDeviation), Inv(z.Sum), Inv(z.Area)));
                text.AppendLine(L.F("#{0} {1}: n={2:N0}  min {3:g5}  max {4:g5}  mean {5:g5}  σ {6:g5}", i, attr ?? "", z.Count, z.Minimum, z.Maximum, z.Mean, z.StandardDeviation));
            }
            if (i == 0) { await MessageAsync(T("Zonal statistics"), T("The layer has no polygons.")); return; }

            var dialog = new Window
            {
                Title = L.F("Zonal statistics — {0} × {1}", raster.Name, layer.Name),
                Width = 640, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var export = new Button { Content = T("Export CSV…") };
            export.Click += async (_, _) =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = T("Export zonal statistics"), DefaultExtension = "csv", SuggestedFileName = "zonal.csv",
                    FileTypeChoices = CsvTypes,
                });
                var path = file?.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path)) File.WriteAllLines(path!, rows);
            };
            var close = new Button { Content = T("Close") };
            close.Click += (_, _) => dialog.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12), Children = { export, close } };
            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer
            {
                Margin = new Thickness(12, 12, 12, 0),
                Content = new SelectableTextBlock { Text = text.ToString(), FontFamily = new FontFamily("monospace"), FontSize = 12 },
            });
            dialog.Content = root;
            await dialog.ShowDialog(this);
        }

        private static string Inv(double v) => double.IsNaN(v) ? "" : v.ToString("g9", CultureInfo.InvariantCulture);

        // ---- active-layer panel -------------------------------------------------------------

        private void OnRasterLoaded()
        {
            var active = _view.ActiveLayer;
            if (active == null || active.IsFrame)
            {
                SetControlsEnabled(false);
                _bandGroup.IsVisible = false;
                _derivationGroup.IsVisible = false;
                _histogram.SetData(null, null);
                _legend.SetColorizer(null);
                _metaText.Text = active?.IsFrame == true ? T("Vector-only view — add a raster layer for values, palettes and analysis.") : T("No layers loaded.");
                _infoText.Text = active?.IsFrame == true ? T("Vector-only view") : T("No layers loaded.");
                _coordText.Text = _cellText.Text = _valueText.Text = "";
                UpdateScaleText();
                return;
            }

            SetControlsEnabled(true);
            var c = _view.Colorizer!;
            _syncing = true;
            _minBox.Value = (decimal)c.Minimum;
            _maxBox.Value = (decimal)c.Maximum;
            _gammaSlider.Value = c.Gamma;
            _modeBox.SelectedIndex = c.Mode switch
            {
                PaletteRenderMode.Discrete when c.ClassCount >= 16 => 2,
                PaletteRenderMode.Discrete => 1,
                PaletteRenderMode.Nearest => 3,
                _ => 0,
            };
            // Show THIS layer's own palette in the side panel.
            string paletteName = c.Palette.Name;
            bool reversed = paletteName.EndsWith(" (reversed)", StringComparison.Ordinal);
            if (reversed) paletteName = paletteName.Substring(0, paletteName.Length - " (reversed)".Length);
            if (_palettes.Resolve(paletteName) is { } knownPalette) _paletteBox.SelectedItem = knownPalette;
            _reverseBox.IsChecked = reversed;
            _opacitySlider.Value = active.Opacity * 100;
            _opacityText.Text = $"{active.Opacity * 100:0} %";
            _blendBox.SelectedIndex = Array.IndexOf(BlendModes, active.BlendMode);
            _syncing = false;

            var header = _view.Document!.Header;
            UpdateBandSelector(header);
            var stats = _view.CurrentStatistics;
            string crs = header.CoordinateSpace.Projection ?? "RAW";
            if (header.CoordinateSpace.TryGetEpsg(out int epsg)) crs += $" (EPSG:{epsg})";
            string statsText = stats == null
                ? "—"
                : L.F("data {0:g4} … {1:g4}  (µ {2:g4}, σ {3:g4}){4}", stats.Minimum, stats.Maximum, stats.Mean, stats.StandardDeviation,
                    _view.IsStreaming ? T(", approx.") : "");
            _infoText.Text = string.Format(CultureInfo.InvariantCulture,
                "{0}  |  {1}×{2}  {3}  {4}  |  {5}{6}",
                active.Name, header.RasterInfo.NrOfCellsPerLine, header.RasterInfo.NrOfLines,
                header.RasterInfo.CellType, crs, statsText, _view.IsStreaming ? "  |  " + T("streaming (large dataset)") : "");

            _metaText.Text = L.F("{0}\n{1}×{2} cells · {3} band(s) · {4} · {5}\ncell {6:g6} × {7:g6} {8} · rotation {9:g4}°\n{10}\n{11}",
                active.Name, header.RasterInfo.NrOfCellsPerLine, header.RasterInfo.NrOfLines, active.BandCount,
                header.RasterInfo.CellType, header.ByteOrder, header.RasterInfo.CellSizeX, header.RasterInfo.CellSizeY,
                header.CoordinateSpace.EffectiveUnits, header.CoordinateSpace.Rotation.Degrees, crs,
                active.Document.HeaderPath ?? T("(in memory — not saved)"));

            _derivationGroup.IsVisible = active.Lineage != null || active.IsUnsaved;
            _lineageText.Text = (active.Lineage ?? T("Derived layer")) + (active.IsUnsaved ? "\n● " + T("In memory only — not saved yet") : "");
            _paramsBtn.IsVisible = active.Recipe != null;
            _saveLayerBtn.IsVisible = active.IsUnsaved;

            ApplyMode();
            _legend.SetColorizer(c, UnitLabel());
            _histogram.SetData(active.ActiveRaster, c);
            UpdateScaleText();
            UpdatePaletteControlsEnabled(); // must come last: overrides the legend/enabled-state above when in RGB composite mode
        }

        private void UpdateStatus(RasterReadoutEventArgs r)
        {
            if (_view.Document == null)
            {
                _coordText.Text = _cellText.Text = _valueText.Text = "";
                return;
            }
            _coordText.Text = string.Format(CultureInfo.InvariantCulture, "E {0:0.###}   N {1:0.###}", r.WorldX, r.WorldY);
            if (_view.ActiveLayer?.IsFrame == true) { _cellText.Text = _valueText.Text = ""; return; }
            _cellText.Text = r.InsideRaster ? L.F("cell [{0}, {1}]", r.Column, r.Row) : T("cell —");
            _valueText.Text = r.Value.HasValue
                ? T("value") + " " + r.Value.Value.ToString("g6", CultureInfo.InvariantCulture)
                : (r.InsideRaster ? T("value (no-data)") : T("value —"));
        }

        private void SetControlsEnabled(bool on)
        {
            foreach (var c in new Control[] { _bandBox, _paletteBox, _reverseBox, _stretchBox, _modeBox, _minBox, _maxBox, _gammaSlider, _outTypeBox, _outOrderBox, _opacitySlider, _blendBox, _histogram })
                c.IsEnabled = on;
        }

        private async Task ShowHeaderAsync()
        {
            var doc = _view.Document;
            if (doc == null || _view.ActiveLayer?.IsFrame == true) return;
            var dialog = new Window { Title = T("ERS header") + " — " + _view.ActiveLayer!.Name, Width = 620, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var close = new Button { Content = T("Close"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
            close.Click += (_, _) => dialog.Close();
            var root = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(new ScrollViewer
            {
                Margin = new Thickness(12, 12, 12, 0),
                Content = new SelectableTextBlock { Text = doc.Header.ToErsText(), FontFamily = new FontFamily("monospace"), FontSize = 12 },
            });
            dialog.Content = root;
            await dialog.ShowDialog(this);
        }

        // ---- layer cards --------------------------------------------------------------------

        private void RebuildLayersPanel()
        {
            _layersPanel.Children.Clear();
            var drawOrder = _view.DrawOrder;

            if (drawOrder.Count == 0)
            {
                _layersPanel.Children.Add(new TextBlock
                {
                    Text = T("No layers yet — drop .ers, .tif, .tiff, .erv, .geojson or .csv files here, or use + Add layer."),
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontStyle = FontStyle.Italic, Margin = new Thickness(2, 4),
                });
                return;
            }

            // The panel reads from the exact shared draw stack, top to bottom.
            for (int i = drawOrder.Count - 1; i >= 0; i--)
            {
                switch (drawOrder[i])
                {
                    case VectorLayer vectorLayer: AddVectorLayerCard(vectorLayer); break;
                    case RasterLayer rasterLayer when !rasterLayer.IsFrame: AddRasterLayerCard(rasterLayer); break;
                }
            }
        }

        private void AddVectorLayerCard(VectorLayer layer)
        {
            var visBox = new CheckBox { IsChecked = layer.IsVisible, VerticalAlignment = VerticalAlignment.Center };
            visBox.IsCheckedChanged += (_, _) => RunLayerActionSafely(() => _view.SetVectorLayerVisible(layer, visBox.IsChecked == true));

            var nameText = new TextBlock
            {
                Text = (layer.Lineage != null ? "↳▤ " : "▤ ") + layer.Name + (layer.IsUnsaved ? " ●" : ""),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(nameText, LayerTip(layer.Name, layer.Lineage, layer.IsUnsaved) + "\n" + L.F("{0} object(s)", layer.Document.Objects.Count));
            var upBtn = CircleIconButton("▲");
            upBtn.IsEnabled = _view.CanMoveLayerUp(layer);
            upBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveVectorLayerUp(layer));
            var downBtn = CircleIconButton("▼");
            downBtn.IsEnabled = _view.CanMoveLayerDown(layer);
            downBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveVectorLayerDown(layer));
            var menuBtn = CircleIconButton("⋮");
            // Avalonia only opens a ContextMenu on the control it is attached to, so the ⋮ button
            // and the card's right-click each get their own instance of the same menu.
            var buttonMenu = BuildVectorContextMenu(layer);
            menuBtn.ContextMenu = buttonMenu;
            menuBtn.Click += (_, _) => buttonMenu.Open(menuBtn);
            var menu = BuildVectorContextMenu(layer);

            // Recompute lives in the ⋮ menu here: the style row has no room for another button.
            var styleRow = BuildVectorStyleRow(layer);
            if (layer.IsUnsaved)
            {
                var saveBtn = CircleIconButton("⤓");
                ToolTip.SetTip(saveBtn, T("Save this derived layer as .erv"));
                saveBtn.Click += async (_, _) => await SaveVectorLayerAsync(layer);
                styleRow.Children.Add(saveBtn);
            }
            var card = BuildLayerCard(visBox, nameText, upBtn, downBtn, menuBtn, isActive: false, styleRow);
            card.ContextMenu = menu;
            _layersPanel.Children.Add(card);
        }

        private void AddRasterLayerCard(RasterLayer layer)
        {
            bool isActive = ReferenceEquals(layer, _view.ActiveLayer);

            var visBox = new CheckBox { IsChecked = layer.IsVisible, VerticalAlignment = VerticalAlignment.Center };
            // Bound to the layer object, not its momentary list position.
            visBox.IsCheckedChanged += (_, _) => RunLayerActionSafely(() => _view.SetLayerVisible(layer, visBox.IsChecked == true));

            var nameBtn = new Button
            {
                // A TextBlock, not a plain string: string Content treats "_" as an access key.
                Content = new TextBlock
                {
                    Text = (layer.Lineage != null ? "↳ " : "") + layer.Name + (layer.IsUnsaved ? " ●" : ""),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0),
            };
            nameBtn.Click += (_, _) => RunLayerActionSafely(() => _view.SetActiveLayer(layer));
            ToolTip.SetTip(nameBtn, LayerTip(layer.Name, layer.Lineage, layer.IsUnsaved, layer.IsStreaming));

            var upBtn = CircleIconButton("▲");
            upBtn.IsEnabled = _view.CanMoveLayerUp(layer);
            upBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveLayerUp(layer));
            var downBtn = CircleIconButton("▼");
            downBtn.IsEnabled = _view.CanMoveLayerDown(layer);
            downBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveLayerDown(layer));
            var menuBtn = CircleIconButton("⋮");
            // Avalonia only opens a ContextMenu on the control it is attached to, so the ⋮ button
            // and the card's right-click each get their own instance of the same menu.
            var buttonMenu = BuildRasterContextMenu(layer);
            menuBtn.ContextMenu = buttonMenu;
            menuBtn.Click += (_, _) => buttonMenu.Open(menuBtn);
            var menu = BuildRasterContextMenu(layer);

            var card = BuildLayerCard(visBox, nameBtn, upBtn, downBtn, menuBtn, isActive, BuildRasterLayerRow(layer));
            card.ContextMenu = menu;
            _layersPanel.Children.Add(card);
        }

        /// <summary>A raster card's second row: opacity, blend-mode hint, streaming badge, recompute and save buttons.</summary>
        private StackPanel BuildRasterLayerRow(RasterLayer layer)
        {
            var opacity = new Slider { Minimum = 0, Maximum = 100, Value = layer.Opacity * 100, Width = 120, VerticalAlignment = VerticalAlignment.Center };
            var percent = new TextBlock { Text = $"{layer.Opacity * 100:0} %", VerticalAlignment = VerticalAlignment.Center, Width = 36, FontSize = AppTheme.FontCaption, Foreground = AppTheme.TextSecondary };
            ToolTip.SetTip(opacity, T("Layer opacity"));
            opacity.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                _view.SetLayerOpacity(layer, opacity.Value / 100.0);
                percent.Text = $"{opacity.Value:0} %";
                if (ReferenceEquals(layer, _view.ActiveLayer)) { _syncing = true; _opacitySlider.Value = opacity.Value; _syncing = false; }
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(28, 0, 0, 0) };
            row.Children.Add(opacity);
            row.Children.Add(percent);
            if (layer.BlendMode != LayerBlendMode.Normal)
                row.Children.Add(new TextBlock { Text = "◐ " + T(layer.BlendMode.ToString()), Foreground = AppTheme.TextSecondary, FontSize = AppTheme.FontCaption, VerticalAlignment = VerticalAlignment.Center });
            if (layer.IsStreaming) row.Children.Add(new TextBlock { Text = "⇶ " + T("streaming"), Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, FontSize = AppTheme.FontCaption });
            if (layer.Recipe != null)
            {
                var gear = CircleIconButton("↻");
                ToolTip.SetTip(gear, T("Change parameters and recompute"));
                gear.Click += async (_, _) => await EditRecipeAsync(layer);
                row.Children.Add(gear);
            }
            if (layer.IsUnsaved)
            {
                var saveBtn = CircleIconButton("⤓");
                ToolTip.SetTip(saveBtn, T("Save this derived layer (.ers + data)"));
                saveBtn.Click += async (_, _) => await SaveRasterLayerAsync(layer);
                row.Children.Add(saveBtn);
            }
            return row;
        }

        private ContextMenu BuildRasterContextMenu(RasterLayer layer)
        {
            MenuItem Item(string header, Action run) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => run(); return mi; }
            void Activate() => _view.SetActiveLayer(layer);
            var menu = new ContextMenu();
            menu.Items.Add(Item(T("Zoom to layer"), () => { var b = layer.Document.GeoReference.WorldBounds(); _view.ZoomToWorld(b.MinX, b.MinY, b.MaxX, b.MaxY); }));
            menu.Items.Add(Item(T("Make active"), Activate));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(T("Statistics & histogram…"), () => { Activate(); _ = ShowStatisticsAsync(); }));
            menu.Items.Add(Item(T("Generate contours…"), () => { Activate(); _ = GenerateContoursAsync(); }));
            menu.Items.Add(Item(T("Bézier-patch subdivision…"), () => { Activate(); _ = BezierSubdivisionAsync(); }));
            menu.Items.Add(Item(T("Hillshade"), () => { Activate(); _ = ComputeTerrainAsync(TerrainProduct.Hillshade); }));
            menu.Items.Add(Item(T("Slope"), () => { Activate(); _ = ComputeTerrainAsync(TerrainProduct.Slope); }));
            if (layer.Recipe != null)
                menu.Items.Add(Item(T("Parameters / recompute…"), () => _ = EditRecipeAsync(layer)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(T("Save layer as…"), () => _ = SaveRasterLayerAsync(layer)));
            menu.Items.Add(Item(T("Full ERS header…"), () => { Activate(); _ = ShowHeaderAsync(); }));
            if (layer.Document.HeaderPath != null)
                menu.Items.Add(Item(T("Copy file path"), () => _ = Clipboard?.SetTextAsync(layer.Document.HeaderPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(T("Remove"), () => RunLayerActionSafely(() => _view.RemoveLayer(layer))));
            return menu;
        }

        private ContextMenu BuildVectorContextMenu(VectorLayer layer)
        {
            MenuItem Item(string header, Action run) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => run(); return mi; }
            var menu = new ContextMenu();
            menu.Items.Add(Item(T("Zoom to layer"), () => { if (layer.Extent() is { } e) _view.ZoomToWorld(e.MinX, e.MinY, e.MaxX, e.MaxY); }));
            var firstLine = layer.Document.Objects.OfType<VectorPolyline>().FirstOrDefault(l => l.Points.Count >= 2);
            if (firstLine != null)
                menu.Items.Add(Item(T("Profile along the first line"), () => { SetPathTool(PathTool.Profile); _view.SetPath(firstLine.Points.ToList()); }));
            if (layer.Document.Objects.Any(o => o is VectorPolygon or VectorMapPolygon or VectorBox))
                menu.Items.Add(Item(T("Zonal statistics of the active raster…"), () => _ = ZonalByLayerAsync(layer)));
            if (layer.Recipe != null)
                menu.Items.Add(Item(T("Parameters / recompute…"), () => _ = EditRecipeAsync(layer)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(T("Save as .erv…"), () => _ = SaveVectorLayerAsync(layer)));
            menu.Items.Add(Item(T("Export GeoJSON…"), () => _ = ExportVectorAsync(layer, "geojson")));
            if (layer.Document.Objects.OfType<VectorPoint>().Any())
                menu.Items.Add(Item(T("Export points as CSV…"), () => _ = ExportVectorAsync(layer, "csv")));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(T("Remove"), () => RunLayerActionSafely(() => _view.RemoveVectorLayer(layer))));
            return menu;
        }

        private static string LayerTip(string name, string? lineage, bool unsaved, bool streaming = false)
        {
            string tip = name;
            if (lineage != null) tip += "\n" + T("Made from:") + " " + lineage;
            if (unsaved) tip += "\n● " + T("In memory only — not saved yet");
            if (streaming) tip += "\n⇶ " + T("Large dataset, streamed from disk");
            return tip;
        }

        /// <summary>A vector layer's inline style row: a hex-colour swatch/box and a line-width stepper.</summary>
        private StackPanel BuildVectorStyleRow(VectorLayer layer)
        {
            var swatch = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(layer.Color), BorderBrush = AppTheme.Border, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var hexBox = new TextBox { Text = FormatHex(layer.Color), Width = 76, VerticalContentAlignment = VerticalAlignment.Center, Watermark = "#RRGGBB" };
            void ApplyHex()
            {
                RunLayerActionSafely(() =>
                {
                    var color = Color.Parse(hexBox.Text ?? string.Empty);
                    _view.SetVectorLayerColor(layer, color);
                    swatch.Background = new SolidColorBrush(color);
                    hexBox.Text = FormatHex(color);
                });
            }
            hexBox.LostFocus += (_, _) => ApplyHex();
            hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };

            var widthBox = new NumericUpDown
            {
                Value = (decimal)layer.LineWidth, Minimum = 0.5m, Maximum = 20m, Increment = 0.5m, FormatString = "0.#",
                Width = 100, HorizontalAlignment = HorizontalAlignment.Left,
            };
            ToolTip.SetTip(widthBox, T("Line width"));
            widthBox.ValueChanged += (_, e) =>
            {
                if (e.NewValue is decimal v) RunLayerActionSafely(() => _view.SetVectorLayerLineWidth(layer, (double)v));
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(swatch);
            row.Children.Add(hexBox);
            row.Children.Add(widthBox);
            return row;
        }

        private static string FormatHex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

        /// <summary>A small, flat icon button (styled by the "icon" class in <see cref="AppStyles"/>).</summary>
        private static Button CircleIconButton(string glyph, IBrush? foreground = null)
        {
            var b = new Button
            {
                Content = glyph,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = foreground ?? AppTheme.TextSecondary,
            };
            b.Classes.Add("icon");
            return b;
        }

        /// <summary>Wraps one layer's row controls in a rounded card; the active raster layer gets an accent border and tint.</summary>
        private static Border BuildLayerCard(Control visBox, Control name, Control upBtn, Control downBtn, Control menuBtn, bool isActive, Control? styleRow = null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 2 };
            Grid.SetColumn(visBox, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(upBtn, 2);
            Grid.SetColumn(downBtn, 3);
            Grid.SetColumn(menuBtn, 4);
            row.Children.Add(visBox);
            row.Children.Add(name);
            row.Children.Add(upBtn);
            row.Children.Add(downBtn);
            row.Children.Add(menuBtn);

            Control content = row;
            if (styleRow != null)
            {
                var stack = new StackPanel { Spacing = 0 };
                stack.Children.Add(row);
                stack.Children.Add(styleRow);
                content = stack;
            }

            return new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(8),
                Background = isActive ? AppTheme.ActiveHighlight : AppTheme.BarBackground,
                BorderBrush = isActive ? AppTheme.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 5, 4, 5),
                Margin = new Thickness(0, 2),
            };
        }

        // ---- vector export --------------------------------------------------------------

        private async Task ExportVectorAsync(VectorLayer layer, string format)
        {
            bool csv = format == "csv";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = csv ? T("Export points as CSV") : T("Export GeoJSON"),
                DefaultExtension = csv ? "csv" : "geojson",
                SuggestedFileName = SafeFileName(layer.Name) + (csv ? ".csv" : ".geojson"),
                FileTypeChoices = csv ? CsvTypes : GeoJsonTypes,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (csv) File.WriteAllText(path!, CsvPointFormat.Write(layer.Document.Objects.OfType<VectorPoint>()));
                else GeoJsonFormat.WriteFile(path!, layer.Document.Objects);
                Flash(L.F("Exported: {0}", Path.GetFileName(path)));
            }
            catch (Exception ex) { await MessageAsync(T("Export failed"), ex.Message); }
        }

        // ---- command palette --------------------------------------------------------------

        private async Task ShowCommandPaletteAsync()
        {
            var commands = new List<(string Path, Action Run)>();
            void Walk(Cmd cmd, string prefix)
            {
                string name = T(cmd.Header).Replace("_", string.Empty, StringComparison.Ordinal).Replace("&&", "&", StringComparison.Ordinal);
                string path = prefix.Length == 0 ? name : prefix + " ▸ " + name;
                if (cmd.Run != null) commands.Add((path, cmd.Run));
                foreach (var child in cmd.Children) if (child != null) Walk(child, path);
            }
            foreach (var top in BuildMenuModel()) Walk(top, "");

            var box = new TextBox { Watermark = T("Type a command… (e.g. contour, bézier, undo)") };
            var list = new ListBox { Height = 320 };
            var dialog = new Window
            {
                Title = T("Command palette"), Width = 520, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            };

            List<(string Path, Action Run)> shown = commands;
            void Filter()
            {
                string q = Fold(box.Text ?? "");
                var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                shown = commands.Where(c => words.All(w => Fold(c.Path).Contains(w, StringComparison.Ordinal))).ToList();
                list.ItemsSource = shown.Select(c => c.Path).ToList();
                if (shown.Count > 0) list.SelectedIndex = 0;
            }
            void Run()
            {
                int i = list.SelectedIndex;
                if (i < 0 || i >= shown.Count) return;
                var action = shown[i].Run;
                dialog.Close();
                Dispatcher.UIThread.Post(() => { action(); RefreshMenuChecks(); });
            }
            box.TextChanged += (_, _) => Filter();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Down && list.ItemCount > 0) { list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1); e.Handled = true; }
                else if (e.Key == Key.Up && list.ItemCount > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
                else if (e.Key == Key.Enter) { Run(); e.Handled = true; }
                else if (e.Key == Key.Escape) dialog.Close();
            };
            list.DoubleTapped += (_, _) => Run();
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Run(); };

            dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { box, list } };
            dialog.Opened += (_, _) => box.Focus();
            Filter();
            await dialog.ShowDialog(this);
        }

        /// <summary>Lower-case, accent-free text for forgiving matching ("bezier" finds "Bézier", "szintvonal" finds "Szintvonal").</summary>
        private static string Fold(string s)
        {
            var normalized = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char ch in normalized)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString();
        }

        // ---- interactive histogram ---------------------------------------------------------

        /// <summary>
        /// Histogram of the active layer (log scale, bars in the palette's colours) with two draggable
        /// handles for the stretch minimum and maximum.
        /// </summary>
        private sealed class HistogramStretchControl : Control
        {
            private long[] _bins = Array.Empty<long>();
            private double _dataMin, _dataMax;
            private RasterColorizer? _colorizer;
            private int _drag; // 0 none, 1 min, 2 max

            public event Action<double, double>? RangeChanged;

            public HistogramStretchControl() { ClipToBounds = true; Cursor = new Cursor(StandardCursorType.SizeWestEast); }

            public void SetData(Raster? raster, RasterColorizer? colorizer)
            {
                _colorizer = colorizer;
                _bins = Array.Empty<long>();
                if (raster != null)
                {
                    var stats = raster.Statistics;
                    _dataMin = stats.Minimum; _dataMax = stats.Maximum;
                    // Extend the axis to include the current stretch, so handles outside the data stay grabbable.
                    if (colorizer != null) { _dataMin = Math.Min(_dataMin, colorizer.Minimum); _dataMax = Math.Max(_dataMax, colorizer.Maximum); }
                    if (!(_dataMax > _dataMin)) _dataMax = _dataMin + 1;
                    const int n = 96;
                    _bins = new long[n];
                    double range = _dataMax - _dataMin;
                    var samples = raster.Samples;
                    int step = Math.Max(1, samples.Length / 2_000_000); // sample very large rasters
                    for (int i = 0; i < samples.Length; i += step)
                    {
                        float v = samples[i];
                        if (raster.IsNoData(v)) continue;
                        _bins[Math.Clamp((int)((v - _dataMin) / range * n), 0, n - 1)]++;
                    }
                }
                InvalidateVisual();
            }

            public void SetColorizer(RasterColorizer? colorizer) { _colorizer = colorizer; InvalidateVisual(); }

            private double X(double v) => (v - _dataMin) / (_dataMax - _dataMin) * Bounds.Width;
            private double V(double x) => _dataMin + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * (_dataMax - _dataMin);

            public override void Render(DrawingContext context)
            {
                double w = Bounds.Width, h = Bounds.Height;
                context.FillRectangle(AppTheme.BarBackground, new Rect(0, 0, w, h));
                if (_bins.Length == 0 || w <= 0) return;
                long max = _bins.Max();
                double bw = w / _bins.Length;
                for (int i = 0; i < _bins.Length; i++)
                {
                    if (_bins[i] == 0) continue;
                    double bh = Math.Log(1 + _bins[i]) / Math.Log(1 + Math.Max(1, max)) * (h - 14);
                    double value = _dataMin + (i + 0.5) / _bins.Length * (_dataMax - _dataMin);
                    IBrush brush = AppTheme.Accent;
                    if (_colorizer != null)
                    {
                        var c = _colorizer.Map(value);
                        brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                    }
                    context.FillRectangle(brush, new Rect(i * bw, h - 12 - bh, Math.Max(1, bw - 0.5), bh));
                }
                if (_colorizer == null) return;
                double x0 = X(_colorizer.Minimum), x1 = X(_colorizer.Maximum);
                var shade = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
                if (x0 > 0) context.FillRectangle(shade, new Rect(0, 0, x0, h - 12));
                if (x1 < w) context.FillRectangle(shade, new Rect(x1, 0, w - x1, h - 12));
                var pen = new Pen(Brushes.White, 2);
                foreach (double x in new[] { x0, x1 })
                {
                    context.DrawLine(pen, new Point(x, 0), new Point(x, h - 12));
                    var tri = new StreamGeometry();
                    using (var gc = tri.Open())
                    {
                        gc.BeginFigure(new Point(x, h - 12), true);
                        gc.LineTo(new Point(x - 6, h));
                        gc.LineTo(new Point(x + 6, h));
                        gc.EndFigure(true);
                    }
                    context.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), tri);
                }
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                if (_colorizer == null || !IsEnabled) return;
                double x = e.GetPosition(this).X;
                _drag = Math.Abs(x - X(_colorizer.Minimum)) <= Math.Abs(x - X(_colorizer.Maximum)) ? 1 : 2;
                e.Pointer.Capture(this);
                Move(x);
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                if (_drag != 0) Move(e.GetPosition(this).X);
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                _drag = 0;
                e.Pointer.Capture(null);
            }

            private void Move(double x)
            {
                if (_colorizer == null) return;
                double v = V(x);
                double min = _colorizer.Minimum, max = _colorizer.Maximum;
                double eps = (_dataMax - _dataMin) / 1000;
                if (_drag == 1) min = Math.Min(v, max - eps); else max = Math.Max(v, min + eps);
                RangeChanged?.Invoke(min, max);
                InvalidateVisual();
            }
        }
    }
}
