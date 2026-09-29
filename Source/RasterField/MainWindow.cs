using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    /// Cross-platform host window: menu, palette / stretch controls and a live legend on the
    /// right, a status bar with the world coordinate and sampled value under the pointer, and
    /// the <see cref="RasterView"/> filling the rest. Reads and writes <c>.ers</c> + BIL.
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        private readonly RasterView _view = new RasterView();
        private readonly LegendControl _legend = new LegendControl { Width = 150 };
        private readonly PaletteLibrary _palettes;

        private readonly ComboBox _paletteBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly CheckBox _reverseBox = new CheckBox { Content = T("Reverse palette") };
        private readonly ComboBox _stretchBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _modeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly NumericUpDown _minBox = new NumericUpDown { FormatString = "0.###", Increment = 1, ShowButtonSpinner = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly NumericUpDown _maxBox = new NumericUpDown { FormatString = "0.###", Increment = 1, ShowButtonSpinner = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly Slider _gammaSlider = new Slider { Minimum = 0.1, Maximum = 3.0, Value = 1.0, TickFrequency = 0.1 };
        private readonly ComboBox _outTypeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _outOrderBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        private readonly TextBlock _coordText = new TextBlock { MinWidth = 260 };
        private readonly TextBlock _cellText = new TextBlock { MinWidth = 150 };
        private readonly TextBlock _valueText = new TextBlock { MinWidth = 170 };
        private readonly TextBlock _scaleText = new TextBlock { MinWidth = 190 };
        private readonly TextBlock _infoText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };

        private readonly Border _clipBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 0x1A, 0x1A, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 210, 0)),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        private readonly TextBlock _clipTitleText = new TextBlock { Text = T("Clip tool —"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _clipInfoText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _clipCropBtn = new Button { Content = T("Crop → new layer"), IsEnabled = false };

        private readonly Border _pathBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 0x1A, 0x1A, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 200, 255)),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        private readonly TextBlock _pathTitleText = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _pathInfoText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        private readonly ComboBox _bandBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly Border _bandGroup = new Border(); // wraps the band header+box; hidden for single-band datasets
        private readonly CheckBox _rgbCompositeBox = new CheckBox { Content = T("True colour (RGB composite)") };
        private readonly StackPanel _layersPanel = new StackPanel { Spacing = 2 };

        private readonly TextBlock _busyText = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly Border _busyOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            IsVisible = false,
            IsHitTestVisible = true,
        };

        private readonly AppSettings _settings = AppSettings.Load();
        private MenuItem? _recentMenu;

        // File-picker filters, hoisted to static fields (rather than built fresh in every dialog
        // call) per CA1861 — they're immutable and shared across every Open/Save dialog that uses them.
        private static readonly FilePickerFileType ErsHeaderFileType =
            new("ER Mapper header (*.ers)") { Patterns = new[] { "*.ers" } };
        private static readonly FilePickerFileType[] ErsOpenFileTypeFilter = { ErsHeaderFileType, FilePickerFileTypes.All };
        private static readonly FilePickerFileType[] ErsSaveFileTypeChoices = { ErsHeaderFileType };
        private static readonly FilePickerFileType[] PngFileTypeChoices =
            { new("PNG image (*.png)") { Patterns = new[] { "*.png" } } };
        private static readonly FilePickerFileType[] ErvFileTypeChoices =
            { new("ER Mapper vector header (*.erv)") { Patterns = new[] { "*.erv" } } };
        private static readonly FilePickerFileType _ersOrErvFileType =
            new("Layers (*.ers, *.erv, *.geojson, *.json, *.csv)") { Patterns = new[] { "*.ers", "*.erv", "*.geojson", "*.json", "*.csv" } };
        private static readonly FilePickerFileType[] LayerOpenFileTypeFilter =
        {
            _ersOrErvFileType, ErsHeaderFileType, ErvFileTypeChoices[0],
            new("GeoJSON (*.geojson, *.json)") { Patterns = new[] { "*.geojson", "*.json" } },
            new("CSV points (*.csv, *.txt)") { Patterns = new[] { "*.csv", "*.txt" } },
            FilePickerFileTypes.All,
        };

        private bool _syncing;
        private Avalonia.Controls.NativeMenu? _nativeRecentMenu;
        private Border? _sidePanelBorder;
        private DockPanel? _statusBarHost;

        public MainWindow()
        {
            Title = T("RasterField — ER Mapper raster viewer");
            Width = 1180;
            Height = 720;
            MinWidth = 900;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // A native-feeling backdrop where the platform offers one (Windows 11's Mica, macOS's
            // vibrancy) — Avalonia tries each in order and silently falls back to a plain
            // background wherever none apply, so one shared hint list is safe cross-platform.
            TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None,
            };

            if (OperatingSystem.IsMacOS())
            {
                // The in-window Menu below is skipped on macOS in favour of the system menu bar
                // (see BuildMenu) — extending into the title bar gives the traffic lights a
                // unified, native-looking toolbar area instead of a separate bare title strip.
                ExtendClientAreaToDecorationsHint = true;
                ExtendClientAreaTitleBarHeightHint = -1;
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            }

            _palettes = PaletteLibrary.CreateDefault(PaletteStorage.BundledPaletteDirectory());
            _palettes.AddPalFiles(PaletteStorage.UserPaletteDirectory());
            LoadImageStripPalettes();

            Content = BuildLayout();
            if (OperatingSystem.IsMacOS())
                Avalonia.Controls.NativeMenu.SetMenu(this, BuildNativeMenu());
            PopulateControls();
            WireEvents();
            SetControlsEnabled(false);
            RebuildLayersPanel();
            RebuildRecentMenu();
            ApplyWindowSettings();
            ApplyTheme();
            Application.Current!.ActualThemeVariantChanged += (_, _) => ApplyTheme();

            KeyDown += OnWindowKeyDown;
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects =
                e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
            Closing += OnWindowClosing;
            UpdateTitle();
            Closed += (_, _) => Dispose();
        }

        /// <summary>
        /// Re-applies <see cref="AppTheme"/>'s current colours to every piece of chrome that
        /// isn't already following the platform theme automatically (FluentTheme handles the
        /// built-in controls' own look; this covers the app's own custom-drawn/coloured pieces).
        /// Called once at startup and again whenever the effective theme changes — system-follow
        /// switching live, or the user picking a different one from *View ▸ Theme*.
        /// </summary>
        private void ApplyTheme()
        {
            Background = AppTheme.WindowBackground;
            if (_sidePanelBorder != null) _sidePanelBorder.Background = AppTheme.PanelBackground;
            if (_leftPanelBorder != null) _leftPanelBorder.Background = AppTheme.PanelBackground;
            if (_statusBarHost != null) _statusBarHost.Background = AppTheme.PanelBackground;
            foreach (var t in new[] { _coordText, _cellText, _valueText, _scaleText, _infoText }) t.Foreground = AppTheme.TextSecondary;
            _clipBar.Background = AppTheme.BarBackground;
            _pathBar.Background = AppTheme.BarBackground;
            _clipTitleText.Foreground = AppTheme.Accent;
            _clipInfoText.Foreground = AppTheme.TextPrimary;
            _pathTitleText.Foreground = AppTheme.Accent;
            _pathInfoText.Foreground = AppTheme.TextPrimary;
            _busyOverlay.Background = AppTheme.OverlayScrim;
            _view.Background = AppTheme.CanvasBackground;
            _view.InvalidateVisual();
            RebuildLayersPanel(); // the active-layer row highlight is theme-aware too
        }

        private void SetThemeMode(ThemeMode mode)
        {
            _settings.Theme = mode;
            _settings.Save();
            Application.Current!.RequestedThemeVariant = mode switch
            {
                ThemeMode.Light => Avalonia.Styling.ThemeVariant.Light,
                ThemeMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }

        /// <summary>Releases the raster view's rendered bitmap and streaming reader.</summary>
        public void Dispose() => _view.Dispose();

        // ---- app settings (recent files, window geometry, last palette/stretch) -----

        private void ApplyWindowSettings()
        {
            if (_settings.WindowWidth is > 0 && _settings.WindowHeight is > 0)
            {
                Width = _settings.WindowWidth.Value;
                Height = _settings.WindowHeight.Value;
            }
            if (_settings.WindowX.HasValue && _settings.WindowY.HasValue)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint((int)_settings.WindowX.Value, (int)_settings.WindowY.Value);
            }
            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

            _syncing = true;
            if (_settings.LastPaletteName != null && _palettes.Names.Contains(_settings.LastPaletteName))
                _paletteBox.SelectedItem = _settings.LastPaletteName;
            _reverseBox.IsChecked = _settings.LastPaletteReversed;
            if (_settings.LastStretchIndex >= 0 && _settings.LastStretchIndex < 4)
                _stretchBox.SelectedIndex = _settings.LastStretchIndex;
            _syncing = false;
        }

        private void SaveWindowSettings()
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
            _settings.LastPaletteName = _paletteBox.SelectedItem as string;
            _settings.LastPaletteReversed = _reverseBox.IsChecked == true;
            _settings.LastStretchIndex = _stretchBox.SelectedIndex;
            _settings.Save();
        }

        private void RebuildRecentMenu()
        {
            if (_recentMenu != null)
            {
                _recentMenu.Items.Clear();
                if (_settings.RecentFiles.Count == 0)
                {
                    _recentMenu.Items.Add(new MenuItem { Header = T("(no recent files)"), IsEnabled = false });
                }
                else
                {
                    foreach (string path in _settings.RecentFiles)
                    {
                        var item = new MenuItem { Header = path };
                        item.Click += (_, _) => OpenRecent(path);
                        _recentMenu.Items.Add(item);
                    }
                    _recentMenu.Items.Add(new Separator());
                    var clear = new MenuItem { Header = T("Clear recent files") };
                    clear.Click += (_, _) => { _settings.RecentFiles.Clear(); RebuildRecentMenu(); };
                    _recentMenu.Items.Add(clear);
                }
            }

            if (_nativeRecentMenu != null)
            {
                _nativeRecentMenu.Items.Clear();
                if (_settings.RecentFiles.Count == 0)
                {
                    _nativeRecentMenu.Items.Add(new Avalonia.Controls.NativeMenuItem(T("(no recent files)")) { IsEnabled = false });
                }
                else
                {
                    foreach (string path in _settings.RecentFiles)
                    {
                        var item = new Avalonia.Controls.NativeMenuItem(path);
                        item.Click += (_, _) => OpenRecent(path);
                        _nativeRecentMenu.Items.Add(item);
                    }
                    _nativeRecentMenu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());
                    var clear = new Avalonia.Controls.NativeMenuItem(T("Clear recent files"));
                    clear.Click += (_, _) => { _settings.RecentFiles.Clear(); RebuildRecentMenu(); };
                    _nativeRecentMenu.Items.Add(clear);
                }
            }
        }

        // ---- layout -----------------------------------------------------------------

        private DockPanel BuildStatusBar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 20,
                Margin = new Thickness(12, 4),
            };
            foreach (var t in new[] { _coordText, _cellText, _valueText, _scaleText })
            {
                t.FontSize = AppTheme.FontCaption;
                t.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") }; // tabular digits: the readout does not jitter
                panel.Children.Add(t);
            }
            _coordText.MinWidth = 230; _cellText.MinWidth = 110; _valueText.MinWidth = 130; _scaleText.MinWidth = 170;

            var host = new DockPanel();
            DockPanel.SetDock(panel, Dock.Left);
            host.Children.Add(panel);
            _infoText.Margin = new Thickness(12, 4);
            _infoText.FontSize = AppTheme.FontCaption;
            host.Children.Add(_infoText);
            return host;
        }

        /// <summary>The floating toolbar shown over the raster while the clip tool is active.</summary>
        private Border BuildClipBar()
        {
            var cancelBtn = new Button { Content = T("Cancel") };
            cancelBtn.Click += (_, _) => SetClipToolActive(false);
            _clipCropBtn.Click += async (_, _) => await CropSelectionAsync();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(_clipTitleText);
            row.Children.Add(_clipInfoText);
            row.Children.Add(_clipCropBtn);
            row.Children.Add(cancelBtn);
            _clipBar.Child = row;
            return _clipBar;
        }

        private void SetClipToolActive(bool active)
        {
            _view.SelectionMode = active;
            _clipBar.IsVisible = active;
            if (active) { SetPathTool(PathTool.None); _view.IdentifyMode = false; }
            UpdateClipPanel();
            RefreshMenuChecks();
        }

        /// <summary>The dimming overlay + centred message shown over the view while a heavy computation runs.</summary>
        private Border BuildBusyOverlay()
        {
            _busyOverlay.Child = _busyText;
            return _busyOverlay;
        }

        /// <summary>
        /// Toggles the busy overlay and disables the whole window (so no second heavy operation
        /// can be started, and no control's state can drift out from under an in-flight one)
        /// while <paramref name="busy"/> is <see langword="true"/>.
        /// </summary>
        private void SetBusy(bool busy, string? message = null)
        {
            _busyText.Text = message ?? T("Working…");
            _busyOverlay.IsVisible = busy;
            IsEnabled = !busy;
        }

        private void UpdateClipPanel()
        {
            var rect = _view.CurrentSelection;
            if (rect == null)
            {
                _clipInfoText.Text = T("drag a rectangle on the raster to mark the area to keep");
                _clipCropBtn.IsEnabled = false;
                return;
            }

            _clipCropBtn.IsEnabled = true;
            string text = $"{rect.Value.Width} × {rect.Value.Height} px";
            if (_view.Document != null)
            {
                var (_, b, c, _, e, f) = _view.Document.GeoReference.GeoTransform;
                double worldW = Math.Sqrt(b * b + e * e) * rect.Value.Width;
                double worldH = Math.Sqrt(c * c + f * f) * rect.Value.Height;
                string unit = _view.Document.Header.CoordinateSpace.EffectiveUnits;
                text += string.Format(CultureInfo.InvariantCulture, "  ({0:0.##} × {1:0.##} {2}) — drag the handles to adjust", worldW, worldH, unit);
            }
            _clipInfoText.Text = text;
        }

        private async Task CropSelectionAsync()
        {
            var rect = _view.CurrentSelection;
            if (rect == null || _view.Document == null) return;
            var doc = _view.Document;

            SetBusy(true, T("Cropping…"));
            try
            {
                var clip = await Task.Run(() => doc.Clip(rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height));
                SetBusy(false);
                await PerformClipAndSaveAsync(clip);
                SetClipToolActive(false);
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Clip failed"), ex.Message); }
        }

        // ---- populate & wire -------------------------------------------------------

        private void PopulateControls()
        {
            _syncing = true;

            _paletteBox.ItemsSource = _palettes.Names.ToList();
            _paletteBox.SelectedItem = _palettes.Names.Contains("Elevation") ? "Elevation" : _palettes.Names.FirstOrDefault();

            _stretchBox.ItemsSource = new[] { T("Full min / max"), T("Mean ± 2σ"), T("2 – 98 %"), T("Manual") };
            _stretchBox.SelectedIndex = 2;

            _modeBox.ItemsSource = new[] { T("Continuous"), T("Discrete (8)"), T("Discrete (16)"), T("Nearest") };
            _modeBox.SelectedIndex = 0;

            _outTypeBox.ItemsSource = new[]
            {
                T("Keep current"),
                "IEEE4ByteReal", "IEEE8ByteReal",
                "Unsigned8BitInteger", "Signed16BitInteger", "Unsigned16BitInteger", "Signed32BitInteger",
            };
            _outTypeBox.SelectedIndex = 0;

            _outOrderBox.ItemsSource = new[] { T("Keep current"), T("LSBFirst (little-endian)"), T("MSBFirst (big-endian)") };
            _outOrderBox.SelectedIndex = 0;

            _syncing = false;
        }

        private void WireEvents()
        {
            _view.PointerReadout += (_, r) => UpdateStatus(r);
            // Wrapped: an exception here (e.g. while refreshing the info/legend panel for the
            // newly-active layer) must never silently swallow the rest of the event chain — it
            // used to abort before LayersChanged could fire, leaving the layer panel's active
            // highlight visibly stuck on the old layer even though the switch had happened.
            _view.RasterLoaded += (_, _) => RunLayerActionSafely(OnRasterLoaded);
            // Deferred: rebuilding the panel synchronously, from inside the very button Click
            // that triggered it, tears down and replaces that button mid-click. Posting it lets
            // the click finish first.
            _view.LayersChanged += (_, _) => QueueRebuildLayersPanel();
            _view.ViewChanged += (_, _) => UpdateScaleText();
            _view.SelectionChanged += (_, _) => UpdateClipPanel();
            _view.PathChanged += (_, _) => OnPathChanged();
            _view.PathFinished += (_, _) => OnPathChanged();
            _view.HistoryChanged += (_, _) => RefreshMenuChecks();
            _view.IdentifyRequested += OnIdentifyRequested;

            _bandBox.SelectionChanged += (_, _) => { if (!_syncing) _view.SetActiveBand(_bandBox.SelectedIndex); };
            _rgbCompositeBox.IsCheckedChanged += (_, _) => { if (!_syncing) _view.ShowRgbComposite = _rgbCompositeBox.IsChecked == true; };
            _paletteBox.SelectionChanged += (_, _) => ApplyPalette();
            _reverseBox.IsCheckedChanged += (_, _) => ApplyPalette();
            _modeBox.SelectionChanged += (_, _) => ApplyMode();
            _stretchBox.SelectionChanged += (_, _) => ApplyStretch();
            _gammaSlider.PropertyChanged += (_, e) =>
            {
                if (!_syncing && e.Property == RangeBase.ValueProperty)
                {
                    _view.SetGamma(_gammaSlider.Value);
                    _legend.SetColorizer(_view.Colorizer, UnitLabel());
                }
            };
            _minBox.ValueChanged += (_, _) => ApplyManualRange();
            _maxBox.ValueChanged += (_, _) => ApplyManualRange();
        }

        // ---- open ----------------------------------------------------------------

        private async void OpenRecent(string path)
        {
            try
            {
                if (path.EndsWith(".rfproj", StringComparison.OrdinalIgnoreCase)) { if (await ConfirmDiscardAsync(T("Open project"))) await LoadProjectAsync(path); }
                else if (IsVectorFile(path)) AddVectorFromFile(path);
                else OpenDataset(path);
            }
            catch (Exception ex) { await MessageAsync(T("Could not open"), ex.Message); }
        }

        /// <summary>Opens a dataset from a path (also used for the command-line argument).</summary>
        public async void OpenDataset(string path)
        {
            try
            {
                _view.LoadErs(path, CurrentPalette());
                _settings.AddRecentFile(Path.GetFullPath(path));
                _settings.LastOpenDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
                _settings.Save();
                RebuildRecentMenu();
            }
            catch (Exception ex)
            {
                await MessageAsync(T("Could not open dataset"), ex.Message);
            }
        }

        /// <summary>Adds a dataset from a path as a new layer alongside whatever is already loaded (also used for extra command-line arguments).</summary>
        public async void AddLayer(string path)
        {
            try
            {
                _view.AddLayerFromPath(path, CurrentPalette());
                _settings.AddRecentFile(Path.GetFullPath(path));
                _settings.LastOpenDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
                _settings.Save();
                RebuildRecentMenu();
            }
            catch (Exception ex)
            {
                await MessageAsync(T("Could not add layer"), ex.Message);
            }
        }

        /// <summary>Adds a <c>.erv</c> vector dataset as a new vector layer (also used for command-line arguments).</summary>
        public async void AddVectorLayer(string path)
        {
            try
            {
                AddVectorFromFile(path);
                _settings.AddRecentFile(Path.GetFullPath(path));
                _settings.LastOpenDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
                _settings.Save();
                RebuildRecentMenu();
            }
            catch (Exception ex)
            {
                await MessageAsync(T("Could not add vector layer"), ex.Message);
            }
        }

        private async Task OpenDialogAsync()
        {
            IStorageFolder? startLocation = null;
            if (_settings.LastOpenDirectory != null && Directory.Exists(_settings.LastOpenDirectory))
                startLocation = await StorageProvider.TryGetFolderFromPathAsync(_settings.LastOpenDirectory);

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Open ER Mapper raster header"),
                AllowMultiple = false,
                SuggestedStartLocation = startLocation,
                FileTypeFilter = ErsOpenFileTypeFilter,
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path)) OpenDataset(path!);
        }

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            var paths = (e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>()).Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
            foreach (var path in paths)
            {
                try
                {
                    if (path.EndsWith(".rfproj", StringComparison.OrdinalIgnoreCase))
                    {
                        if (await ConfirmDiscardAsync(T("Open project"))) await LoadProjectAsync(path);
                        return;
                    }
                    if (IsVectorFile(path)) AddVectorFromFile(path);
                    else if (path.EndsWith(".ers", StringComparison.OrdinalIgnoreCase))
                    {
                        // Layers already loaded: add this one alongside them rather than replacing everything.
                        if (_view.DrawOrder.Count == 0) OpenDataset(path);
                        else AddLayer(path);
                        continue;
                    }
                    else continue;
                    _settings.AddRecentFile(Path.GetFullPath(path));
                    _settings.Save();
                    RebuildRecentMenu();
                }
                catch (Exception ex) { await MessageAsync(T("Add layer failed"), $"{Path.GetFileName(path)}: {ex.Message}"); }
            }
        }

        // ---- layers --------------------------------------------------------------

        private async Task AddLayerDialogAsync()
        {
            IStorageFolder? startLocation = null;
            if (_settings.LastOpenDirectory != null && Directory.Exists(_settings.LastOpenDirectory))
                startLocation = await StorageProvider.TryGetFolderFromPathAsync(_settings.LastOpenDirectory);

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Add layer(s) — raster (.ers) or vector (.erv, .geojson, .csv)"),
                AllowMultiple = true,
                SuggestedStartLocation = startLocation,
                FileTypeFilter = LayerOpenFileTypeFilter,
            });
            if (files.Count == 0) return;

            string? lastDir = null;
            foreach (var f in files)
            {
                var path = f.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    if (IsVectorFile(path!))
                        AddVectorFromFile(path!);
                    else
                        _view.AddLayerFromPath(path!, CurrentPalette());
                    _settings.AddRecentFile(Path.GetFullPath(path));
                    lastDir = Path.GetDirectoryName(Path.GetFullPath(path));
                }
                catch (Exception ex) { await MessageAsync(T("Add layer failed"), $"{Path.GetFileName(path)}: {ex.Message}"); }
            }

            if (lastDir != null) _settings.LastOpenDirectory = lastDir;
            _settings.Save();
            RebuildRecentMenu();
        }

        private bool _layersPanelRebuildQueued;

        /// <summary>
        /// Defers the actual rebuild to the next UI-thread dispatch instead of running it
        /// synchronously inside whatever raised <see cref="RasterView.LayersChanged"/> — which,
        /// for a layer-panel button, is that very button's own Click handler. Rebuilding the
        /// panel there tears down and replaces that button (and every other row control) while
        /// it is still mid-click, which is timing that should never be relied on to "just work".
        /// Coalesces bursts of the event into a single rebuild.
        /// </summary>
        private void QueueRebuildLayersPanel()
        {
            if (_layersPanelRebuildQueued) return;
            _layersPanelRebuildQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _layersPanelRebuildQueued = false;
                RebuildLayersPanel();
            });
        }

        /// <summary>
        /// Runs a layer-panel action and surfaces any exception via a dialog instead of letting
        /// it vanish inside an event handler — the layer panel's buttons previously failed
        /// silently on some environments, and a swallowed exception looks identical to "nothing
        /// happened" from the user's side. If this ever fires again, the dialog's message is the
        /// actual root cause.
        /// </summary>
        private void RunLayerActionSafely(Action action)
        {
            try { action(); }
            catch (Exception ex) { _ = MessageAsync(T("Layer action failed"), ex.Message); }
        }

        // ---- save --------------------------------------------------------------

        private async Task SaveHeaderAsAsync()
        {
            if (_view.Document == null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Save ER Mapper header"),
                DefaultExtension = "ers",
                SuggestedFileName = SuggestName() + ".ers",
                FileTypeChoices = ErsSaveFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                _view.Document.SaveHeader(path!);
                Flash(L.F("Header written: {0}", Path.GetFileName(path)));
            }
            catch (Exception ex) { await MessageAsync(T("Save failed"), ex.Message); }
        }

        private async Task SaveDatasetAsAsync()
        {
            if (_view.Document == null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Save ER Mapper dataset (writes .ers + binary data file)"),
                DefaultExtension = "ers",
                SuggestedFileName = SuggestName() + "_out.ers",
                FileTypeChoices = ErsSaveFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var options = new ErsSaveOptions
                {
                    CellType = SelectedOutputCellType(),
                    ByteOrder = SelectedOutputByteOrder(),
                };
                _view.Document.Save(path!, options);
                if (_view.ActiveLayer is { IsUnsaved: true } saved) _view.MarkSaved(saved, path!);
                Flash(L.F("Dataset written: {0} (+ data file)", Path.GetFileName(path)));
            }
            catch (Exception ex) { await MessageAsync(T("Save failed"), ex.Message); }
        }

        private async Task ExportPngAsync()
        {
            var image = _view.RenderToImage();
            if (image == null) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Export coloured raster as PNG"),
                DefaultExtension = "png",
                SuggestedFileName = SuggestName() + ".png",
                FileTypeChoices = PngFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                SaveImageAsPng(image, path!);
                Flash(L.F("PNG written: {0}", Path.GetFileName(path)));
            }
            catch (Exception ex) { await MessageAsync(T("Export failed"), ex.Message); }
        }

        private ErsCellType? SelectedOutputCellType() => (_outTypeBox.SelectedItem as string) switch
        {
            "IEEE4ByteReal" => ErsCellType.IEEE4ByteReal,
            "IEEE8ByteReal" => ErsCellType.IEEE8ByteReal,
            "Unsigned8BitInteger" => ErsCellType.Unsigned8BitInteger,
            "Signed16BitInteger" => ErsCellType.Signed16BitInteger,
            "Unsigned16BitInteger" => ErsCellType.Unsigned16BitInteger,
            "Signed32BitInteger" => ErsCellType.Signed32BitInteger,
            _ => null,
        };

        private ErsByteOrder? SelectedOutputByteOrder() => _outOrderBox.SelectedIndex switch
        {
            1 => ErsByteOrder.LsbFirst,
            2 => ErsByteOrder.MsbFirst,
            _ => null,
        };

        // ---- colourisation glue ------------------------------------------------

        private Palette CurrentPalette()
        {
            var p = _palettes.GetOrDefault(_paletteBox.SelectedItem as string);
            return _reverseBox.IsChecked == true ? p.Reversed() : p;
        }

        private void ApplyPalette()
        {
            if (_syncing || _view.Colorizer == null) return;
            _view.SetPalette(CurrentPalette());
            _legend.SetColorizer(_view.Colorizer, UnitLabel());
        }

        private void ApplyMode()
        {
            if (_view.Colorizer == null) return;
            switch (_modeBox.SelectedIndex)
            {
                case 1: _view.SetRenderMode(PaletteRenderMode.Discrete, 8); break;
                case 2: _view.SetRenderMode(PaletteRenderMode.Discrete, 16); break;
                case 3: _view.SetRenderMode(PaletteRenderMode.Nearest); break;
                default: _view.SetRenderMode(PaletteRenderMode.Continuous); break;
            }
            _legend.SetColorizer(_view.Colorizer, UnitLabel());
        }

        private void ApplyStretch()
        {
            if (_syncing || _view.Colorizer == null) return;
            switch (_stretchBox.SelectedIndex)
            {
                case 0: _view.AutoRange(RangeMode.MinMax); break;
                case 1: _view.AutoRange(RangeMode.TwoSigma); break;
                case 2: _view.AutoRange(RangeMode.Percentile); break;
                default: ApplyManualRange(); return;
            }
            _syncing = true;
            _minBox.Value = (decimal)_view.Colorizer.Minimum;
            _maxBox.Value = (decimal)_view.Colorizer.Maximum;
            _syncing = false;
            _legend.SetColorizer(_view.Colorizer, UnitLabel());
        }

        private void ApplyManualRange()
        {
            if (_syncing || _view.Colorizer == null) return;
            if (_stretchBox.SelectedIndex != 3) { _syncing = true; _stretchBox.SelectedIndex = 3; _syncing = false; }

            double min = (double)(_minBox.Value ?? 0);
            double max = (double)(_maxBox.Value ?? 0);
            if (max > min)
            {
                _view.SetValueRange(min, max);
                _legend.SetColorizer(_view.Colorizer, UnitLabel());
            }
        }

        private void UpdateBandSelector(ErsHeader header)
        {
            if (_view.BandCount <= 1)
            {
                _bandGroup.IsVisible = false;
                return;
            }

            var bandInfos = header.RasterInfo.Bands;
            var names = new List<string>();
            for (int i = 0; i < _view.BandCount; i++)
            {
                string label = bandInfos != null && i < bandInfos.Count && !string.IsNullOrWhiteSpace(bandInfos[i].Value)
                    ? L.F("Band {0} — {1}", i + 1, bandInfos[i].Value)
                    : L.F("Band {0}", i + 1);
                names.Add(label);
            }

            _syncing = true;
            _bandBox.ItemsSource = names;
            _bandBox.SelectedIndex = _view.ActiveBand;
            _rgbCompositeBox.IsVisible = _view.CanShowRgbComposite;
            _rgbCompositeBox.IsChecked = _view.ShowRgbComposite;
            _syncing = false;
            _bandGroup.IsVisible = true;
        }

        private void UpdateScaleText()
        {
            double? gsd = _view.GroundSampleDistance;
            if (gsd == null) { _scaleText.Text = ""; return; }

            string unit = _view.Document?.Header.CoordinateSpace.EffectiveUnits ?? "m";
            double dpi = (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0) * 96.0;
            double? denom = _view.MapScaleDenominator(dpi);
            _scaleText.Text = denom.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1}/px   1:{2:N0}", gsd.Value, unit, denom.Value)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1}/px", gsd.Value, unit);
        }

        private string? UnitLabel()
        {
            var bands = _view.Document?.Header.RasterInfo.Bands;
            return bands != null && bands.Count > 0 ? bands[0].Units : null;
        }

        /// <summary>
        /// The palette/stretch/gamma controls (and the band selector, which picks what the
        /// palette is applied to) don't mean anything while a true-colour RGB composite is
        /// showing — bands 1-3 are all in use at once, with no single value to map through a
        /// palette. Disable them (without hiding — the dataset can still be a normal multi-band
        /// one where the user might switch back).
        /// </summary>
        private void UpdatePaletteControlsEnabled()
        {
            bool rgb = _view.ShowRgbComposite;
            foreach (var c in new Control[] { _bandBox, _paletteBox, _reverseBox, _modeBox, _stretchBox, _minBox, _maxBox, _gammaSlider })
                c.IsEnabled = !rgb;
            _legend.SetColorizer(rgb ? null : _view.Colorizer, UnitLabel());
        }

        // ---- palette editor -----------------------------------------------------

        private void OpenPaletteEditor(Palette? seed)
        {
            Palette? original = _view.Colorizer?.Palette;
            bool saved = false;

            var editor = new PaletteEditorWindow(_palettes, seed,
                onPreview: p => { if (_view.Colorizer != null) _view.SetPalette(p); },
                onSavedToLibrary: p =>
                {
                    saved = true;
                    _paletteBox.ItemsSource = _palettes.Names.ToList();
                    _paletteBox.SelectedItem = p.Name;
                    Flash(L.F("Palette saved: {0}", p.Name));
                });

            editor.Closed += (_, _) =>
            {
                if (!saved && original != null) _view.SetPalette(original);
            };
            editor.Show(this);
        }

        // ---- no-data gap fill -----------------------------------------------------

        private enum FillMethod { InverseDistanceWeighted, Nearest }

        private async Task FillNoDataAsync()
        {
            if (_view.Document == null)
            {
                await MessageAsync(T("Fill no-data gaps"), T("Open a dataset first."));
                return;
            }
            if (_view.Raster == null)
            {
                await MessageAsync(T("Fill no-data gaps"),
                    _view.IsStreaming
                        ? T("This dataset is large and is shown in streaming mode, so it is never fully loaded into memory. Clip a smaller region first (Tools ▸ Clip tool), then run this on the clipped layer.")
                        : T("The raster is not loaded."));
                return;
            }

            // Only cells within the convex hull of the valid data count as real "gaps" — the
            // no-data margin outside the data's actual footprint (a rotated scene's background
            // corners, a mosaic's missing corner, …) is left alone and isn't a gap to report.
            long gaps = NoDataFiller.CountNoDataWithinHull(_view.Raster);
            if (gaps == 0)
            {
                await MessageAsync(T("Fill no-data gaps"), T("This band has no no-data gaps within its data footprint to fill."));
                return;
            }

            var choice = await ShowFillNoDataDialogAsync(gaps);
            if (choice == null) return;

            var raster = _view.Raster;
            SetBusy(true, T("Filling no-data gaps…"));
            Raster filled = await Task.Run(() => choice.Value.Method == FillMethod.Nearest
                ? NoDataFiller.FillNearest(raster)
                : NoDataFiller.FillInverseDistanceWeighted(raster, choice.Value.MaxSearchDistance, choice.Value.SmoothingIterations));
            SetBusy(false);

            _view.Document.ReplaceBand(0, filled);
            _view.RefreshFromDocument();
            _legend.SetColorizer(_view.Colorizer, UnitLabel());

            // Every in-hull gap is now guaranteed filled (see NoDataFiller's remarks) — anything
            // still no-data afterwards is, by construction, outside the data's footprint.
            Flash(L.F("Filled all {0:N0} gap cell(s) within the data's footprint.", gaps));
        }

        private async Task<(FillMethod Method, int MaxSearchDistance, int SmoothingIterations)?> ShowFillNoDataDialogAsync(long gapCount)
        {
            var tcs = new TaskCompletionSource<(FillMethod, int, int)?>();

            var methodBox = new ComboBox
            {
                ItemsSource = new[] { T("Inverse-distance weighted (recommended)"), T("Nearest neighbour (fast)") },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var distBox = new NumericUpDown { Minimum = 1, Maximum = 100000, Value = 100, Increment = 10, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            var smoothBox = new NumericUpDown { Minimum = 0, Maximum = 10, Value = 1, Increment = 1, Width = 100, HorizontalAlignment = HorizontalAlignment.Left };

            var dialog = new Window
            {
                Title = T("Fill no-data gaps"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = T("Fill"), MinWidth = 80 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };
            okBtn.Click += (_, _) =>
            {
                tcs.TrySetResult((methodBox.SelectedIndex == 0 ? FillMethod.InverseDistanceWeighted : FillMethod.Nearest,
                    (int)(distBox.Value ?? 100), (int)(smoothBox.Value ?? 0)));
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = L.F("{0:N0} gap cell(s) found within the data's footprint.", gapCount), TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = T("Only gaps inside the convex hull of the valid data are ever touched — the no-data margin outside the data's actual footprint (a rotated scene's background corners, a mosaic's missing corner, …) is left exactly as it is. Every in-hull gap is guaranteed to be filled. IDW searches outward along several directions for the nearest valid pixels and takes their inverse-distance-weighted average (the method behind GDAL's FillNodata); Nearest just copies the closest valid cell."),
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 11,
                    },
                    methodBox,
                    new TextBlock { Text = T("Max search distance (cells, IDW only)") },
                    distBox,
                    new TextBlock { Text = T("Smoothing passes (IDW only)") },
                    smoothBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        // ---- clip / cutout tool ---------------------------------------------------

        private async Task ClipByExtentAsync()
        {
            if (_view.Document == null)
            {
                await MessageAsync(T("Clip by extent"), T("Open a dataset first."));
                return;
            }

            var bounds = _view.Document.GeoReference.WorldBounds();
            var extent = await ShowClipExtentDialogAsync(bounds);
            if (extent == null) return;

            var doc = _view.Document;
            SetBusy(true, T("Cropping…"));
            try
            {
                var clip = await Task.Run(() => doc.ClipToWorldExtent(extent.Value.MinX, extent.Value.MinY, extent.Value.MaxX, extent.Value.MaxY));
                SetBusy(false);
                await PerformClipAndSaveAsync(clip);
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Clip failed"), ex.Message); }
        }

        private async Task<(double MinX, double MinY, double MaxX, double MaxY)?> ShowClipExtentDialogAsync(
            (double MinX, double MinY, double MaxX, double MaxY) current)
        {
            var tcs = new TaskCompletionSource<(double, double, double, double)?>();

            NumericUpDown Box(double v) => new NumericUpDown { Value = (decimal)v, FormatString = "0.###", Increment = 1, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            var minXBox = Box(current.MinX);
            var minYBox = Box(current.MinY);
            var maxXBox = Box(current.MaxX);
            var maxYBox = Box(current.MaxY);

            var dialog = new Window
            {
                Title = T("Clip by extent"),
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = T("Clip…"), MinWidth = 80 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };
            okBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(((double)(minXBox.Value ?? 0), (double)(minYBox.Value ?? 0),
                                   (double)(maxXBox.Value ?? 0), (double)(maxYBox.Value ?? 0)));
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            TextBlock Label(string t) => AppTheme.Caption(t);
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = T("World-coordinate extent to keep (pre-filled with the full raster bounds):"), TextWrapping = TextWrapping.Wrap },
                    Label(T("Min E (west)")), minXBox,
                    Label(T("Max E (east)")), maxXBox,
                    Label(T("Min N (south)")), minYBox,
                    Label(T("Max N (north)")), maxYBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async Task PerformClipAndSaveAsync(ErsDocument clip) => await PerformDerivedSaveAsync(clip, "_clip", "Clip");

        /// <summary>
        /// Adds a computed result (a clip, a terrain/band-math/mosaic raster, …) as a new derived
        /// layer in memory — nothing is written until the user saves it (the layer card's ⤓, or
        /// Layer ▸ Save active layer).
        /// </summary>
        private Task PerformDerivedSaveAsync(ErsDocument result, string suggestedSuffix, string label)
        {
            string source = _view.ActiveLayer?.Name ?? SuggestName();
            string name = source + suggestedSuffix;
            AddDerivedRasterLayer(result, name, $"{label} of {source}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Wraps a computed single-band result (terrain derivative, band-math output, …) into a
        /// new document sharing the current dataset's georeference (origin, cell size, rotation
        /// dropped — these outputs are never rotated) and coordinate system.
        /// </summary>
        private ErsDocument BuildDerivedDocument(Raster result, ErsDocument? source = null)
        {
            var doc = source ?? _view.Document ?? throw new InvalidOperationException("No dataset is open.");
            var (originX, originY) = doc.GeoReference.PixelToWorld(0, 0);
            var (_, b, c, _, e, f) = doc.GeoReference.GeoTransform;
            double cellSizeX = Math.Sqrt(b * b + e * e);
            double cellSizeY = Math.Sqrt(c * c + f * f);

            return ErsDocument.Create(result, originX, originY, cellSizeX, cellSizeY,
                ErsCellType.IEEE4ByteReal,
                doc.Header.ByteOrder == ErsByteOrder.Unknown ? ErsByteOrder.LsbFirst : doc.Header.ByteOrder,
                doc.Header.CoordinateSpace.Projection, doc.Header.CoordinateSpace.Datum);
        }

        /// <summary>
        /// Gets the loaded band and its cell size for a terrain/band-math computation, showing the
        /// usual "this dataset is streaming" message when the raster is not (fully) loaded.
        /// </summary>
        private async Task<(Raster Raster, double CellSizeX, double CellSizeY)?> TryGetLoadedRasterAsync(string toolName)
        {
            if (_view.Document == null || _view.ActiveLayer?.IsFrame == true)
            {
                await MessageAsync(toolName, T("Open a raster dataset first."));
                return null;
            }
            if (_view.Raster == null)
            {
                await MessageAsync(toolName,
                    _view.IsStreaming
                        ? T("This dataset is large and is shown in streaming mode, so it is never fully loaded into memory. Clip a smaller region first (Tools ▸ Clip tool), then run this on the clipped layer.")
                        : T("The raster is not loaded."));
                return null;
            }

            var (_, b, c, _, e, f) = _view.Document.GeoReference.GeoTransform;
            double cellSizeX = Math.Sqrt(b * b + e * e);
            double cellSizeY = Math.Sqrt(c * c + f * f);
            return (_view.Raster, cellSizeX, cellSizeY);
        }

        // ---- terrain analysis ---------------------------------------------------

        private enum TerrainProduct { Slope, Aspect, Hillshade }

        private async Task ComputeTerrainAsync(TerrainProduct product)
        {
            var loaded = await TryGetLoadedRasterAsync(T(product.ToString()));
            if (loaded == null) return;
            string op = product.ToString().ToLowerInvariant();
            await CreateDerivedAsync(new LayerRecipe(op, _view.ActiveLayer!), L.F("Computing {0}…", T(product.ToString())));
        }

        // ---- curvature -------------------------------------------------------------

        private async Task ComputeCurvatureAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Curvature"));
            if (loaded == null) return;
            var type = await ShowCurvatureDialogAsync();
            if (type == null) return;
            await CreateDerivedAsync(new LayerRecipe("curvature", _view.ActiveLayer!, new Dictionary<string, string> { ["type"] = type.Value.ToString() }),
                L.F("Computing {0} curvature…", T(type.Value.ToString())));
        }

        private async Task<CurvatureType?> ShowCurvatureDialogAsync()
        {
            var tcs = new TaskCompletionSource<CurvatureType?>();

            var typeBox = new ComboBox
            {
                ItemsSource = new[] { T("General (convex/concave overall shape)"), T("Profile (along the slope — affects flow speed)"), T("Plan (across the slope — affects flow convergence)") },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var dialog = new Window
            {
                Title = T("Curvature"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = T("Compute"), MinWidth = 80 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };
            okBtn.Click += (_, _) =>
            {
                var type = typeBox.SelectedIndex switch
                {
                    1 => CurvatureType.Profile,
                    2 => CurvatureType.Plan,
                    _ => CurvatureType.General,
                };
                tcs.TrySetResult(type);
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = T("Positive = convex (dome/ridge); negative = concave (bowl/valley); zero = planar."), TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 11 },
                    typeBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        // ---- hydrology --------------------------------------------------------------

        private async Task ComputeFlowDirectionAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Flow direction"));
            if (loaded == null) return;
            await CreateDerivedAsync(new LayerRecipe("flowdir", _view.ActiveLayer!), T("Computing flow direction…"));
        }

        private async Task ComputeFlowAccumulationAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Flow accumulation"));
            if (loaded == null) return;
            await CreateDerivedAsync(new LayerRecipe("flowacc", _view.ActiveLayer!), T("Computing flow accumulation…"));
        }

        // ---- viewshed ----------------------------------------------------------------

        private async Task ComputeViewshedAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Viewshed"));
            if (loaded == null) return;
            var (raster, _, _) = loaded.Value;

            var options = await ShowViewshedDialogAsync(raster.Width, raster.Height);
            if (options == null) return;

            SetBusy(true, T("Computing viewshed…"));
            try
            {
                Raster result = await Task.Run(() => ViewshedAnalysis.Compute(
                    raster, options.Value.ObserverCol, options.Value.ObserverRow,
                    options.Value.ObserverHeight, options.Value.TargetHeight, options.Value.MaxDistanceCells));
                SetBusy(false);

                var doc = BuildDerivedDocument(result);
                await PerformDerivedSaveAsync(doc, "_viewshed", "Viewshed");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Viewshed failed"), ex.Message); }
        }

        private async Task<(int ObserverCol, int ObserverRow, double ObserverHeight, double TargetHeight, int? MaxDistanceCells)?> ShowViewshedDialogAsync(int width, int height)
        {
            var tcs = new TaskCompletionSource<(int, int, double, double, int?)?>();

            var colBox = new NumericUpDown { Minimum = 0, Maximum = width - 1, Value = width / 2, Increment = 1, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            var rowBox = new NumericUpDown { Minimum = 0, Maximum = height - 1, Value = height / 2, Increment = 1, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            var observerHeightBox = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = 1.8M, Increment = 0.5M, FormatString = "0.#", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            var targetHeightBox = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = 0M, Increment = 0.5M, FormatString = "0.#", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            var maxDistBox = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Value = 0M, Increment = 50, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };

            var dialog = new Window
            {
                Title = T("Viewshed"),
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = T("Compute…"), MinWidth = 80 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };
            okBtn.Click += (_, _) =>
            {
                int maxDist = (int)(maxDistBox.Value ?? 0);
                tcs.TrySetResult((
                    (int)(colBox.Value ?? 0), (int)(rowBox.Value ?? 0),
                    (double)(observerHeightBox.Value ?? 1.8M), (double)(targetHeightBox.Value ?? 0M),
                    maxDist > 0 ? maxDist : (int?)null));
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = T("Observer location (raster cell — column/row, 0-based):"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = T("Column") }, colBox,
                    new TextBlock { Text = T("Row") }, rowBox,
                    new TextBlock { Text = T("Observer eye height above ground") }, observerHeightBox,
                    new TextBlock { Text = T("Target height above ground") }, targetHeightBox,
                    new TextBlock { Text = T("Max distance in cells (0 = unlimited)") }, maxDistBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        // ---- Swiss-style relief shading -----------------------------------------------

        /// <summary>
        /// Filters for the relief export dialog — an ERS choice first (a real 3-band true-colour
        /// dataset, consistent with every other Terrain product, and reopenable in the app), a
        /// plain PNG as the lightweight alternative for sharing/printing.
        /// </summary>
        private static readonly FilePickerFileType[] ReliefFileTypeChoices =
        {
            new("ER Mapper true-colour raster (*.ers)") { Patterns = new[] { "*.ers" } },
            new("PNG image (*.png)") { Patterns = new[] { "*.png" } },
        };

        private async Task ExportSwissReliefAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Swiss-style relief"));
            if (loaded == null) return;
            var (raster, cellSizeX, cellSizeY) = loaded.Value;

            SetBusy(true, T("Rendering Swiss-style relief…"));
            RasterImage image;
            try
            {
                image = await Task.Run(() => ReliefShader.RenderSwissStyle(raster, cellSizeX, cellSizeY));
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Swiss-style relief failed"), ex.Message); return; }
            SetBusy(false);

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Export Swiss-style relief"),
                DefaultExtension = "ers",
                SuggestedFileName = SuggestName() + "_relief.ers",
                FileTypeChoices = ReliefFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                if (path!.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    SaveImageAsPng(image, path);
                    Flash(L.F("Relief image written: {0}", Path.GetFileName(path)));
                }
                else
                {
                    var doc = BuildRgbDocument(image);
                    doc.Save(path);
                    Flash(L.F("Relief dataset written: {0} (3-band true colour)", Path.GetFileName(path)));
                    OpenDataset(path);
                }
            }
            catch (Exception ex) { await MessageAsync(T("Export failed"), ex.Message); }
        }

        /// <summary>
        /// Wraps a rendered colour image as a 3-band (Red/Green/Blue) 8-bit dataset sharing the
        /// current document's georeference and coordinate system — a real true-colour raster, not
        /// a flat image file. Note: the app's own viewer does not yet composite multi-band data as
        /// true colour on open (see the Band selector in the side panel) — it shows one band at a
        /// time through the current palette, same as any other multi-band dataset.
        /// </summary>
        private ErsDocument BuildRgbDocument(RasterImage image)
        {
            var doc = _view.Document ?? throw new InvalidOperationException("No dataset is open.");
            var (originX, originY) = doc.GeoReference.PixelToWorld(0, 0);
            var (_, b, c, _, e, f) = doc.GeoReference.GeoTransform;
            double cellSizeX = Math.Sqrt(b * b + e * e);
            double cellSizeY = Math.Sqrt(c * c + f * f);

            var (r, g, bl) = image.ToRgbBands();

            var header = new ErsHeader
            {
                DataSetType = ErsDataSetType.ErStorage,
                DataType = ErsDataType.Raster,
                ByteOrder = ErsByteOrder.LsbFirst,
                CoordinateSpace = new CoordinateSpace
                {
                    Datum = doc.Header.CoordinateSpace.Datum,
                    Projection = doc.Header.CoordinateSpace.Projection,
                    CoordinateType = doc.Header.CoordinateSpace.CoordinateType,
                    CoordinateTypeRaw = doc.Header.CoordinateSpace.CoordinateTypeRaw,
                    Units = doc.Header.CoordinateSpace.Units,
                    Rotation = doc.Header.CoordinateSpace.Rotation,
                },
                RasterInfo = new RasterInfo
                {
                    CellType = ErsCellType.Unsigned8BitInteger,
                    NullCellValue = 0,
                    CellInfo = new CellInfo { XDimension = cellSizeX, YDimension = cellSizeY },
                    NrOfLines = r.Height,
                    NrOfCellsPerLine = r.Width,
                    NrOfBands = 3,
                    RegistrationCellX = 0,
                    RegistrationCellY = 0,
                    RegistrationCoord = new RegistrationCoord
                    {
                        X = originX,
                        Y = originY,
                        Kind = doc.Header.RasterInfo.RegistrationCoord?.Kind ?? RegistrationCoordKind.EastingsNorthings,
                    },
                },
            };
            header.RasterInfo.Bands.Add(new BandInfo { Value = "Red" });
            header.RasterInfo.Bands.Add(new BandInfo { Value = "Green" });
            header.RasterInfo.Bands.Add(new BandInfo { Value = "Blue" });

            return ErsDocument.Create(header, new[] { r, g, bl });
        }

        private static void SaveImageAsPng(RasterImage image, string path)
        {
            using var bmp = new WriteableBitmap(new PixelSize(image.Width, image.Height),
                new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
            using (var fb = bmp.Lock())
            {
                for (int y = 0; y < image.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        image.Pixels, y * image.Stride, fb.Address + y * fb.RowBytes, image.Stride);
            }
            bmp.Save(path);
        }

        // ---- band math -----------------------------------------------------------

        private async Task BandMathAsync()
        {
            var layer = _view.ActiveLayer;
            var doc = layer?.Document;
            if (layer == null || layer.IsFrame || doc == null) { await MessageAsync(T("Band math"), T("Open a raster dataset first.")); return; }
            if (doc.Bands.Count == 0)
            {
                await MessageAsync(T("Band math"), T("This dataset is large and is shown in streaming mode, so its bands are never fully loaded into memory."));
                return;
            }
            var bandNames = Enumerable.Range(1, doc.Bands.Count).Select(i => $"b{i}").ToList();
            var expr = await ShowBandMathDialogAsync(bandNames);
            if (string.IsNullOrWhiteSpace(expr)) return;
            await CreateDerivedAsync(new LayerRecipe("bandmath", layer, new Dictionary<string, string> { ["expr"] = expr! }), T("Evaluating expression…"));
        }

        private async Task<string?> ShowBandMathDialogAsync(IReadOnlyList<string> bandNames, string initial = "")
        {
            var tcs = new TaskCompletionSource<string?>();

            var exprBox = new TextBox { Watermark = T("e.g. (b1 - b2) / (b1 + b2)"), Text = initial };

            var dialog = new Window
            {
                Title = T("Band math"),
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = T("Compute"), MinWidth = 80 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };
            okBtn.Click += (_, _) => { tcs.TrySetResult(exprBox.Text); dialog.Close(); };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = L.F("Available bands: {0}", string.Join(", ", bandNames)), TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = T("Operators + - * / and comparisons (> >= < <= == !=); functions abs, sqrt, exp, log, log10, min, max, pow, iif(cond, a, b); constants pi, e. A cell is no-data in the output if any band it references is no-data there."),
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 11,
                    },
                    exprBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        // ---- mosaic ----------------------------------------------------------------

        private async Task MosaicAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Select rasters to mosaic (2 or more)"),
                AllowMultiple = true,
                FileTypeFilter = ErsOpenFileTypeFilter,
            });

            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
            if (paths.Count < 2)
            {
                if (paths.Count == 1) await MessageAsync(T("Mosaic rasters"), T("Select at least two datasets to mosaic."));
                return;
            }

            List<ErsDocument> docs;
            SetBusy(true, T("Loading rasters…"));
            try { docs = await Task.Run(() => paths.Select(ErsDocument.Load).ToList()); }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Mosaic rasters"), $"Could not load one of the selected files: {ex.Message}"); return; }
            SetBusy(false);

            var (_, b, c, _, e, f) = docs[0].GeoReference.GeoTransform;
            double defaultCellX = Math.Sqrt(b * b + e * e), defaultCellY = Math.Sqrt(c * c + f * f);

            var options = await ShowMosaicOptionsDialogAsync(docs.Count, defaultCellX, defaultCellY);
            if (options == null) return;

            SetBusy(true, T("Merging rasters…"));
            try
            {
                var mosaic = await Task.Run(() => ErsDocument.Mosaic(docs, options.Value.CellSizeX, options.Value.CellSizeY, options.Value.OverlapMode));
                SetBusy(false);
                await PerformDerivedSaveAsync(mosaic, "_mosaic", "Mosaic");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Mosaic failed"), ex.Message); }
        }

        private async Task<(double CellSizeX, double CellSizeY, MosaicOverlapMode OverlapMode)?> ShowMosaicOptionsDialogAsync(
            int fileCount, double defaultCellX, double defaultCellY)
        {
            var tcs = new TaskCompletionSource<(double, double, MosaicOverlapMode)?>();

            var cellXBox = new NumericUpDown { Value = (decimal)defaultCellX, FormatString = "0.####", Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var cellYBox = new NumericUpDown { Value = (decimal)defaultCellY, FormatString = "0.####", Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var overlapBox = new ComboBox
            {
                ItemsSource = new[] { T("Last wins (later files overwrite)"), T("First wins (earlier files kept)"), T("Average") },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var dialog = new Window
            {
                Title = T("Mosaic rasters"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = T("Mosaic…"), MinWidth = 80 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };
            okBtn.Click += (_, _) =>
            {
                var mode = overlapBox.SelectedIndex switch
                {
                    1 => MosaicOverlapMode.FirstWins,
                    2 => MosaicOverlapMode.Average,
                    _ => MosaicOverlapMode.LastWins,
                };
                tcs.TrySetResult(((double)(cellXBox.Value ?? (decimal)defaultCellX), (double)(cellYBox.Value ?? (decimal)defaultCellY), mode));
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = L.F("{0} datasets selected. Output cell size:", fileCount), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = T("Cell size X") }, cellXBox,
                    new TextBlock { Text = T("Cell size Y") }, cellYBox,
                    new TextBlock { Text = T("Where datasets overlap") }, overlapBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        // ---- misc -------------------------------------------------------------

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled) return;
            if (HandleToolKey(e) || HandleMenuGesture(e)) e.Handled = true;
        }

        private void Flash(string message) => _infoText.Text = message;

        private async Task ShowAboutAsync() => await MessageAsync(T("About RasterField"), T(
            "RasterField — a cross-platform pan/zoom viewer for the ERDAS ER Mapper raster format\n" +
            "(.ers header + Band-Interleaved-by-Line data file).\n\n" +
            "Built with Avalonia and the RasterField library: robust .ers parser and writer,\n" +
            "BIL reader/writer with byte-order handling, georeferencing and palette colourisation.\n\n" +
            "Drag to pan · wheel to zoom · arrows / +/- · 0 or F to fit."));

        private async Task MessageAsync(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var ok = new Button { Content = T("OK"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
            ok.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    ok,
                },
            };
            await dialog.ShowDialog(this);
        }

        private string SuggestName()
        {
            var hp = _view.Document?.HeaderPath;
            if (hp != null) return Path.GetFileNameWithoutExtension(hp);
            return _view.ActiveLayer != null ? SafeFileName(_view.ActiveLayer.Name) : "raster";
        }

        private void LoadImageStripPalettes()
        {
            string? dir = PaletteStorage.BundledPaletteDirectory();
            if (dir == null) return;

            foreach (var file in Directory.EnumerateFiles(dir)
                         .Where(f => new[] { ".png", ".bmp" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                try
                {
                    using var bmp = new Bitmap(file);
                    int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
                    int stride = w * 4;
                    var buffer = new byte[stride * h];
                    var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, stride);
                    }
                    finally { handle.Free(); }

                    _palettes.Add(Palette.FromImage(Path.GetFileNameWithoutExtension(file), buffer, w, h, stride));
                }
                catch
                {
                    // ignore an unreadable strip
                }
            }
        }
    }
}
