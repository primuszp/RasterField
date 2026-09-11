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

namespace RasterField
{
    /// <summary>
    /// Cross-platform host window: menu, palette / stretch controls and a live legend on the
    /// right, a status bar with the world coordinate and sampled value under the pointer, and
    /// the <see cref="RasterView"/> filling the rest. Reads and writes <c>.ers</c> + BIL.
    /// </summary>
    public sealed class MainWindow : Window, IDisposable
    {
        private readonly RasterView _view = new RasterView();
        private readonly LegendControl _legend = new LegendControl { Width = 150 };
        private readonly PaletteLibrary _palettes;

        private readonly ComboBox _paletteBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly CheckBox _reverseBox = new CheckBox { Content = "Reverse palette" };
        private readonly ComboBox _stretchBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _modeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly NumericUpDown _minBox = new NumericUpDown { FormatString = "0.###", Increment = 1, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly NumericUpDown _maxBox = new NumericUpDown { FormatString = "0.###", Increment = 1, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
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
        private readonly TextBlock _clipTitleText = new TextBlock { Text = "Clip tool —", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _clipInfoText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _clipCropBtn = new Button { Content = "Crop & save as…", IsEnabled = false };

        private readonly Border _profileBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 0x1A, 0x1A, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 200, 255)),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        private readonly TextBlock _profileTitleText = new TextBlock { Text = "Profile tool —", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _profileInfoText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _profileShowBtn = new Button { Content = "Show profile…", IsEnabled = false };

        private readonly ComboBox _bandBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly Border _bandGroup = new Border(); // wraps the band header+box; hidden for single-band datasets
        private readonly CheckBox _rgbCompositeBox = new CheckBox { Content = "True colour (RGB composite)" };
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
            new("ER Mapper header (*.ers, *.erv)") { Patterns = new[] { "*.ers", "*.erv" } };
        private static readonly FilePickerFileType[] LayerOpenFileTypeFilter = { _ersOrErvFileType, ErsHeaderFileType, ErvFileTypeChoices[0], FilePickerFileTypes.All };

        private bool _syncing;
        private MenuItem? _clipSelectToggle;
        private MenuItem? _profileSelectToggle;
        private Avalonia.Controls.NativeMenuItem? _nativeClipToggle;
        private Avalonia.Controls.NativeMenuItem? _nativeProfileToggle;
        private Avalonia.Controls.NativeMenu? _nativeRecentMenu;
        private Border? _sidePanelBorder;
        private DockPanel? _statusBarHost;

        public MainWindow()
        {
            Title = "RasterField — ER Mapper raster viewer";
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
            Closing += (_, _) => SaveWindowSettings();
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
            if (_statusBarHost != null) _statusBarHost.Background = AppTheme.BarBackground;
            _clipBar.Background = AppTheme.BarBackground;
            _profileBar.Background = AppTheme.BarBackground;
            _clipTitleText.Foreground = AppTheme.Accent;
            _clipInfoText.Foreground = AppTheme.TextPrimary;
            _profileTitleText.Foreground = AppTheme.Accent;
            _profileInfoText.Foreground = AppTheme.TextPrimary;
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
                    _recentMenu.Items.Add(new MenuItem { Header = "(no recent files)", IsEnabled = false });
                }
                else
                {
                    foreach (string path in _settings.RecentFiles)
                    {
                        var item = new MenuItem { Header = path };
                        item.Click += (_, _) => OpenDataset(path);
                        _recentMenu.Items.Add(item);
                    }
                    _recentMenu.Items.Add(new Separator());
                    var clear = new MenuItem { Header = "Clear recent files" };
                    clear.Click += (_, _) => { _settings.RecentFiles.Clear(); RebuildRecentMenu(); };
                    _recentMenu.Items.Add(clear);
                }
            }

            if (_nativeRecentMenu != null)
            {
                _nativeRecentMenu.Items.Clear();
                if (_settings.RecentFiles.Count == 0)
                {
                    _nativeRecentMenu.Items.Add(new Avalonia.Controls.NativeMenuItem("(no recent files)") { IsEnabled = false });
                }
                else
                {
                    foreach (string path in _settings.RecentFiles)
                    {
                        var item = new Avalonia.Controls.NativeMenuItem(path);
                        item.Click += (_, _) => OpenDataset(path);
                        _nativeRecentMenu.Items.Add(item);
                    }
                    _nativeRecentMenu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());
                    var clear = new Avalonia.Controls.NativeMenuItem("Clear recent files");
                    clear.Click += (_, _) => { _settings.RecentFiles.Clear(); RebuildRecentMenu(); };
                    _nativeRecentMenu.Items.Add(clear);
                }
            }
        }

        // ---- layout -----------------------------------------------------------------

        private DockPanel BuildLayout()
        {
            var root = new DockPanel();

            // On macOS the app's menu lives in the system menu bar (see BuildMenu), not in-window.
            var menu = OperatingSystem.IsMacOS() ? null : BuildMenu();
            var statusBar = BuildStatusBar();
            _statusBarHost = statusBar;
            if (menu != null) { DockPanel.SetDock(menu, Dock.Top); root.Children.Add(menu); }
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,300"),
            };

            // _view and the floating clip toolbar share one grid cell so the toolbar overlays the raster.
            var viewHost = new Grid();
            viewHost.Children.Add(_view);
            viewHost.Children.Add(BuildClipBar());
            viewHost.Children.Add(BuildProfileBar());
            viewHost.Children.Add(BuildBusyOverlay());
            Grid.SetColumn(viewHost, 0);

            var splitter = new GridSplitter { Width = 4, Background = Brushes.Gray, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(splitter, 1);

            var side = BuildSidePanel();
            _sidePanelBorder = side;
            Grid.SetColumn(side, 2);

            grid.Children.Add(viewHost);
            grid.Children.Add(splitter);
            grid.Children.Add(side);

            root.Children.Add(grid);
            return root;
        }

        private Menu BuildMenu()
        {
            MenuItem Item(string header, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click, KeyGesture? gesture = null)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += click;
                if (gesture != null) mi.InputGesture = gesture;
                return mi;
            }

            var file = new MenuItem { Header = "_File" };
            file.Items.Add(Item("_Open .ers…", async (_, _) => await OpenDialogAsync(), new KeyGesture(Key.O, KeyModifiers.Control)));
            file.Items.Add(Item("_Add layer(s)…", async (_, _) => await AddLayerDialogAsync(), new KeyGesture(Key.O, KeyModifiers.Control | KeyModifiers.Shift)));
            var recent = new MenuItem { Header = "Open _recent" };
            file.Items.Add(recent);
            _recentMenu = recent;
            file.Items.Add(new Separator());
            file.Items.Add(Item("Save _header as .ers…", async (_, _) => await SaveHeaderAsAsync()));
            file.Items.Add(Item("Save _dataset as… (.ers + data)", async (_, _) => await SaveDatasetAsAsync(), new KeyGesture(Key.S, KeyModifiers.Control)));
            file.Items.Add(Item("_Export view as PNG…", async (_, _) => await ExportPngAsync()));
            file.Items.Add(new Separator());
            file.Items.Add(Item("_Mosaic rasters…", async (_, _) => await MosaicAsync()));
            file.Items.Add(new Separator());
            file.Items.Add(Item("E_xit", (_, _) => Close()));

            var view = new MenuItem { Header = "_View" };
            view.Items.Add(Item("Zoom to _fit", (_, _) => _view.ZoomToFit(), new KeyGesture(Key.D0, KeyModifiers.Control)));
            view.Items.Add(Item("Zoom _in", (_, _) => _view.ZoomBy(1.25), new KeyGesture(Key.OemPlus, KeyModifiers.Control)));
            view.Items.Add(Item("Zoom _out", (_, _) => _view.ZoomBy(0.8), new KeyGesture(Key.OemMinus, KeyModifiers.Control)));
            view.Items.Add(new Separator());
            var gridToggle = new MenuItem { Header = "Show cell _grid", ToggleType = MenuItemToggleType.CheckBox };
            gridToggle.Click += (_, _) => { _view.ShowGrid = gridToggle.IsChecked; _view.InvalidateVisual(); };
            view.Items.Add(gridToggle);
            var smoothToggle = new MenuItem { Header = "_Smooth magnification", ToggleType = MenuItemToggleType.CheckBox };
            smoothToggle.Click += (_, _) => { _view.SmoothScaling = smoothToggle.IsChecked; _view.InvalidateVisual(); };
            view.Items.Add(smoothToggle);
            view.Items.Add(new Separator());
            var themeMenu = new MenuItem { Header = "_Theme" };
            themeMenu.Items.Add(Item("_System", (_, _) => SetThemeMode(ThemeMode.System)));
            themeMenu.Items.Add(Item("_Light", (_, _) => SetThemeMode(ThemeMode.Light)));
            themeMenu.Items.Add(Item("_Dark", (_, _) => SetThemeMode(ThemeMode.Dark)));
            view.Items.Add(themeMenu);

            var palette = new MenuItem { Header = "_Palette" };
            palette.Items.Add(Item("_Edit current palette…", (_, _) => OpenPaletteEditor(CurrentPalette())));
            palette.Items.Add(Item("_New palette…", (_, _) => OpenPaletteEditor(null)));

            var tools = new MenuItem { Header = "_Tools" };
            tools.Items.Add(Item("Fill _no-data gaps…", async (_, _) => await FillNoDataAsync()));
            tools.Items.Add(new Separator());
            var clipSelectToggle = new MenuItem { Header = "_Clip tool (drag a rectangle)", ToggleType = MenuItemToggleType.CheckBox };
            clipSelectToggle.Click += (_, _) => SetClipToolActive(clipSelectToggle.IsChecked);
            tools.Items.Add(clipSelectToggle);
            _clipSelectToggle = clipSelectToggle;
            tools.Items.Add(Item("Clip by _extent (E/N)…", async (_, _) => await ClipByExtentAsync()));
            tools.Items.Add(new Separator());
            var profileToggle = new MenuItem { Header = "_Profile tool (drag a line)", ToggleType = MenuItemToggleType.CheckBox };
            profileToggle.Click += (_, _) => SetProfileToolActive(profileToggle.IsChecked);
            tools.Items.Add(profileToggle);
            _profileSelectToggle = profileToggle;
            tools.Items.Add(new Separator());
            tools.Items.Add(Item("_Band math…", async (_, _) => await BandMathAsync()));
            tools.Items.Add(Item("Generate _contours…", async (_, _) => await GenerateContoursAsync()));

            var terrain = new MenuItem { Header = "_Terrain" };
            terrain.Items.Add(Item("_Slope…", async (_, _) => await ComputeTerrainAsync(TerrainProduct.Slope)));
            terrain.Items.Add(Item("_Aspect…", async (_, _) => await ComputeTerrainAsync(TerrainProduct.Aspect)));
            terrain.Items.Add(Item("_Hillshade…", async (_, _) => await ComputeTerrainAsync(TerrainProduct.Hillshade)));
            terrain.Items.Add(Item("_Curvature…", async (_, _) => await ComputeCurvatureAsync()));
            terrain.Items.Add(new Separator());
            terrain.Items.Add(Item("_Flow direction (D8)…", async (_, _) => await ComputeFlowDirectionAsync()));
            terrain.Items.Add(Item("Flow acc_umulation…", async (_, _) => await ComputeFlowAccumulationAsync()));
            terrain.Items.Add(new Separator());
            terrain.Items.Add(Item("_Viewshed…", async (_, _) => await ComputeViewshedAsync()));
            terrain.Items.Add(new Separator());
            terrain.Items.Add(Item("S_wiss-style relief (export PNG)…", async (_, _) => await ExportSwissReliefAsync()));

            var help = new MenuItem { Header = "_Help" };
            help.Items.Add(Item("_About…", async (_, _) => await ShowAboutAsync()));

            return new Menu { Items = { file, view, palette, tools, terrain, help } };
        }

        /// <summary>
        /// The same menu structure as <see cref="BuildMenu"/>, built for macOS's system menu bar
        /// instead of an in-window one — the single biggest "this looks like a real Mac app"
        /// signal Avalonia offers. Shortcuts use Cmd (<see cref="KeyModifiers.Meta"/>), the
        /// platform convention, rather than Ctrl.
        /// </summary>
        private Avalonia.Controls.NativeMenu BuildNativeMenu()
        {
            Avalonia.Controls.NativeMenuItem Item(string header, Action click, KeyGesture? gesture = null)
            {
                var mi = new Avalonia.Controls.NativeMenuItem(header);
                mi.Click += (_, _) => click();
                if (gesture != null) mi.Gesture = gesture;
                return mi;
            }
            Avalonia.Controls.NativeMenuItem Sub(string header, params Avalonia.Controls.NativeMenuItemBase[] items)
            {
                var mi = new Avalonia.Controls.NativeMenuItem(header) { Menu = new Avalonia.Controls.NativeMenu() };
                foreach (var item in items) mi.Menu.Items.Add(item);
                return mi;
            }
            Avalonia.Controls.NativeMenuItemSeparator Sep() => new Avalonia.Controls.NativeMenuItemSeparator();

            var root = new Avalonia.Controls.NativeMenu();

            // The app menu: macOS supplies the app's name for this first entry automatically.
            root.Items.Add(Sub("RasterField",
                Item("About RasterField…", () => _ = ShowAboutAsync()),
                Sep(),
                Item("Quit RasterField", Close, new KeyGesture(Key.Q, KeyModifiers.Meta))));

            var recent = new Avalonia.Controls.NativeMenu();
            var recentItem = new Avalonia.Controls.NativeMenuItem("Open Recent") { Menu = recent };
            _nativeRecentMenu = recent;

            root.Items.Add(Sub("File",
                Item("Open .ers…", () => _ = OpenDialogAsync(), new KeyGesture(Key.O, KeyModifiers.Meta)),
                Item("Add layer(s)…", () => _ = AddLayerDialogAsync(), new KeyGesture(Key.O, KeyModifiers.Meta | KeyModifiers.Shift)),
                recentItem,
                Sep(),
                Item("Save header as .ers…", () => _ = SaveHeaderAsAsync()),
                Item("Save dataset as… (.ers + data)", () => _ = SaveDatasetAsAsync(), new KeyGesture(Key.S, KeyModifiers.Meta)),
                Item("Export view as PNG…", () => _ = ExportPngAsync()),
                Sep(),
                Item("Mosaic rasters…", () => _ = MosaicAsync())));

            var gridToggle = new Avalonia.Controls.NativeMenuItem("Show cell grid") { ToggleType = Avalonia.Controls.NativeMenuItemToggleType.CheckBox };
            gridToggle.Click += (_, _) => { _view.ShowGrid = gridToggle.IsChecked; _view.InvalidateVisual(); };
            var smoothToggle = new Avalonia.Controls.NativeMenuItem("Smooth magnification") { ToggleType = Avalonia.Controls.NativeMenuItemToggleType.CheckBox };
            smoothToggle.Click += (_, _) => { _view.SmoothScaling = smoothToggle.IsChecked; _view.InvalidateVisual(); };

            root.Items.Add(Sub("View",
                Item("Zoom to fit", () => _view.ZoomToFit(), new KeyGesture(Key.D0, KeyModifiers.Meta)),
                Item("Zoom in", () => _view.ZoomBy(1.25), new KeyGesture(Key.OemPlus, KeyModifiers.Meta)),
                Item("Zoom out", () => _view.ZoomBy(0.8), new KeyGesture(Key.OemMinus, KeyModifiers.Meta)),
                Sep(),
                gridToggle,
                smoothToggle,
                Sep(),
                Sub("Theme",
                    Item("System", () => SetThemeMode(ThemeMode.System)),
                    Item("Light", () => SetThemeMode(ThemeMode.Light)),
                    Item("Dark", () => SetThemeMode(ThemeMode.Dark)))));

            root.Items.Add(Sub("Palette",
                Item("Edit current palette…", () => OpenPaletteEditor(CurrentPalette())),
                Item("New palette…", () => OpenPaletteEditor(null))));

            var clipToggle = new Avalonia.Controls.NativeMenuItem("Clip tool (drag a rectangle)") { ToggleType = Avalonia.Controls.NativeMenuItemToggleType.CheckBox };
            clipToggle.Click += (_, _) => SetClipToolActive(clipToggle.IsChecked);
            _nativeClipToggle = clipToggle;
            var profileToggle = new Avalonia.Controls.NativeMenuItem("Profile tool (drag a line)") { ToggleType = Avalonia.Controls.NativeMenuItemToggleType.CheckBox };
            profileToggle.Click += (_, _) => SetProfileToolActive(profileToggle.IsChecked);
            _nativeProfileToggle = profileToggle;

            root.Items.Add(Sub("Tools",
                Item("Fill no-data gaps…", () => _ = FillNoDataAsync()),
                Sep(),
                clipToggle,
                Item("Clip by extent (E/N)…", () => _ = ClipByExtentAsync()),
                Sep(),
                profileToggle,
                Sep(),
                Item("Band math…", () => _ = BandMathAsync()),
                Item("Generate contours…", () => _ = GenerateContoursAsync())));

            root.Items.Add(Sub("Terrain",
                Item("Slope…", () => _ = ComputeTerrainAsync(TerrainProduct.Slope)),
                Item("Aspect…", () => _ = ComputeTerrainAsync(TerrainProduct.Aspect)),
                Item("Hillshade…", () => _ = ComputeTerrainAsync(TerrainProduct.Hillshade)),
                Item("Curvature…", () => _ = ComputeCurvatureAsync()),
                Sep(),
                Item("Flow direction (D8)…", () => _ = ComputeFlowDirectionAsync()),
                Item("Flow accumulation…", () => _ = ComputeFlowAccumulationAsync()),
                Sep(),
                Item("Viewshed…", () => _ = ComputeViewshedAsync()),
                Sep(),
                Item("Swiss-style relief…", () => _ = ExportSwissReliefAsync())));

            return root;
        }

        private DockPanel BuildStatusBar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                Margin = new Thickness(8, 4),
            };
            panel.Children.Add(_coordText);
            panel.Children.Add(_cellText);
            panel.Children.Add(_valueText);
            panel.Children.Add(_scaleText);

            var host = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)) };
            DockPanel.SetDock(panel, Dock.Left);
            host.Children.Add(panel);
            _infoText.Margin = new Thickness(8, 4);
            host.Children.Add(_infoText);
            return host;
        }

        /// <summary>The floating toolbar shown over the raster while the clip tool is active.</summary>
        private Border BuildClipBar()
        {
            var cancelBtn = new Button { Content = "Cancel" };
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
            if (_clipSelectToggle != null) _clipSelectToggle.IsChecked = active;
            if (_nativeClipToggle != null) _nativeClipToggle.IsChecked = active;
            if (active) SetProfileToolActive(false);
            UpdateClipPanel();
        }

        /// <summary>The floating toolbar shown over the raster while the profile tool is active.</summary>
        private Border BuildProfileBar()
        {
            var cancelBtn = new Button { Content = "Cancel" };
            cancelBtn.Click += (_, _) => SetProfileToolActive(false);
            _profileShowBtn.Click += async (_, _) => await ShowProfileAsync();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(_profileTitleText);
            row.Children.Add(_profileInfoText);
            row.Children.Add(_profileShowBtn);
            row.Children.Add(cancelBtn);
            _profileBar.Child = row;
            return _profileBar;
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
        private void SetBusy(bool busy, string message = "Working…")
        {
            _busyText.Text = message;
            _busyOverlay.IsVisible = busy;
            IsEnabled = !busy;
        }

        private void SetProfileToolActive(bool active)
        {
            _view.LineToolMode = active;
            _profileBar.IsVisible = active;
            if (_profileSelectToggle != null) _profileSelectToggle.IsChecked = active;
            if (_nativeProfileToggle != null) _nativeProfileToggle.IsChecked = active;
            if (active) SetClipToolActive(false);
            UpdateProfilePanel();
        }

        private void UpdateProfilePanel()
        {
            var line = _view.CurrentLine;
            if (line == null)
            {
                _profileInfoText.Text = "drag a line on the raster to sample a cross-section";
                _profileShowBtn.IsEnabled = false;
                return;
            }

            _profileShowBtn.IsEnabled = true;
            double lengthPx = Math.Sqrt(Math.Pow(line.Value.X1 - line.Value.X0, 2) + Math.Pow(line.Value.Y1 - line.Value.Y0, 2));
            _profileInfoText.Text = $"line drawn ({lengthPx:0} px) — drag either end to adjust, then Show profile";
        }

        private async Task ShowProfileAsync()
        {
            var line = _view.CurrentLine;
            var doc = _view.Document;
            if (line == null || doc == null) return;

            if (_view.Raster == null)
            {
                await MessageAsync("Profile tool",
                    _view.IsStreaming
                        ? "This dataset is large and is shown in streaming mode, so it is never fully loaded into memory. " +
                          "Clip a smaller region first (Tools ▸ Clip tool), open the clipped result, then profile that."
                        : "The raster is not loaded.");
                return;
            }

            var (x0, y0, x1, y1) = line.Value;
            var (wx0, wy0) = doc.GeoReference.PixelToWorld(x0, y0);
            var (wx1, wy1) = doc.GeoReference.PixelToWorld(x1, y1);

            int sampleCount = Math.Max(2, (int)Math.Round(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0))) + 1);
            var samples = RasterProfiler.SampleWorld(_view.Raster, doc.GeoReference, wx0, wy0, wx1, wy1, sampleCount);

            string unit = doc.Header.CoordinateSpace.EffectiveUnits;
            var window = new ProfileWindow(samples, unit, UnitLabel());
            window.Show(this);
        }

        private void UpdateClipPanel()
        {
            var rect = _view.CurrentSelection;
            if (rect == null)
            {
                _clipInfoText.Text = "drag a rectangle on the raster to mark the area to keep";
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

            SetBusy(true, "Cropping…");
            try
            {
                var clip = await Task.Run(() => doc.Clip(rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height));
                SetBusy(false);
                await PerformClipAndSaveAsync(clip);
                SetClipToolActive(false);
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Clip failed", ex.Message); }
        }

        private Border BuildSidePanel()
        {
            TextBlock Header(string t) => new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 2) };

            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(10) };

            var addLayerBtn = new Button { Content = "+ Add layer…", HorizontalAlignment = HorizontalAlignment.Stretch };
            addLayerBtn.Click += async (_, _) => await AddLayerDialogAsync();
            stack.Children.Add(Header("Layers (top = drawn in front)"));
            stack.Children.Add(_layersPanel);
            stack.Children.Add(addLayerBtn);

            var bandStack = new StackPanel { Spacing = 4 };
            bandStack.Children.Add(Header("Band"));
            bandStack.Children.Add(_bandBox);
            bandStack.Children.Add(_rgbCompositeBox);
            _bandGroup.Child = bandStack;
            _bandGroup.IsVisible = false;
            stack.Children.Add(_bandGroup);

            stack.Children.Add(Header("Palette"));
            stack.Children.Add(_paletteBox);
            stack.Children.Add(_reverseBox);
            stack.Children.Add(Header("Palette mode"));
            stack.Children.Add(_modeBox);
            stack.Children.Add(Header("Stretch"));
            stack.Children.Add(_stretchBox);

            var minMax = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
            var minWrap = new StackPanel();
            minWrap.Children.Add(new TextBlock { Text = "Min", Opacity = 0.8 });
            minWrap.Children.Add(_minBox);
            var maxWrap = new StackPanel();
            maxWrap.Children.Add(new TextBlock { Text = "Max", Opacity = 0.8 });
            maxWrap.Children.Add(_maxBox);
            Grid.SetColumn(minWrap, 0);
            Grid.SetColumn(maxWrap, 2);
            minMax.Children.Add(minWrap);
            minMax.Children.Add(maxWrap);
            stack.Children.Add(minMax);

            stack.Children.Add(Header("Gamma"));
            stack.Children.Add(_gammaSlider);

            stack.Children.Add(Header("Save / export format"));
            stack.Children.Add(new TextBlock { Text = "Output cell type", Opacity = 0.8 });
            stack.Children.Add(_outTypeBox);
            stack.Children.Add(new TextBlock { Text = "Output byte order", Opacity = 0.8 });
            stack.Children.Add(_outOrderBox);

            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var legendGroup = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(scroll, Dock.Top);
            var legendLabel = new TextBlock { Text = "Legend", FontWeight = FontWeight.Bold, Margin = new Thickness(4, 6, 0, 2) };
            DockPanel.SetDock(legendLabel, Dock.Top);
            legendGroup.Children.Add(scroll);
            legendGroup.Children.Add(legendLabel);
            legendGroup.Children.Add(_legend);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x29)),
                Child = legendGroup,
            };
        }

        // ---- populate & wire -------------------------------------------------------

        private void PopulateControls()
        {
            _syncing = true;

            _paletteBox.ItemsSource = _palettes.Names.ToList();
            _paletteBox.SelectedItem = _palettes.Names.Contains("Elevation") ? "Elevation" : _palettes.Names.FirstOrDefault();

            _stretchBox.ItemsSource = new[] { "Full min / max", "Mean ± 2σ", "2 – 98 %", "Manual" };
            _stretchBox.SelectedIndex = 2;

            _modeBox.ItemsSource = new[] { "Continuous", "Discrete (8)", "Discrete (16)", "Nearest" };
            _modeBox.SelectedIndex = 0;

            _outTypeBox.ItemsSource = new[]
            {
                "Keep current",
                "IEEE4ByteReal", "IEEE8ByteReal",
                "Unsigned8BitInteger", "Signed16BitInteger", "Unsigned16BitInteger", "Signed32BitInteger",
            };
            _outTypeBox.SelectedIndex = 0;

            _outOrderBox.ItemsSource = new[] { "Keep current", "LSBFirst (little-endian)", "MSBFirst (big-endian)" };
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
            _view.LineChanged += (_, _) => UpdateProfilePanel();

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
                await MessageAsync("Could not open dataset", ex.Message);
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
                await MessageAsync("Could not add layer", ex.Message);
            }
        }

        /// <summary>Adds a <c>.erv</c> vector dataset as a new vector layer (also used for command-line arguments).</summary>
        public async void AddVectorLayer(string path)
        {
            try
            {
                _view.AddVectorLayerFromPath(path);
                _settings.AddRecentFile(Path.GetFullPath(path));
                _settings.LastOpenDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
                _settings.Save();
                RebuildRecentMenu();
            }
            catch (Exception ex)
            {
                await MessageAsync("Could not add vector layer", ex.Message);
            }
        }

        private async Task OpenDialogAsync()
        {
            IStorageFolder? startLocation = null;
            if (_settings.LastOpenDirectory != null && Directory.Exists(_settings.LastOpenDirectory))
                startLocation = await StorageProvider.TryGetFolderFromPathAsync(_settings.LastOpenDirectory);

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open ER Mapper raster header",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation,
                FileTypeFilter = ErsOpenFileTypeFilter,
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path)) OpenDataset(path!);
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            var file = e.Data.GetFiles()?.FirstOrDefault();
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            if (path!.EndsWith(".erv", StringComparison.OrdinalIgnoreCase))
            {
                try { _view.AddVectorLayerFromPath(path); _settings.AddRecentFile(Path.GetFullPath(path)); _settings.Save(); RebuildRecentMenu(); }
                catch (Exception ex) { _ = MessageAsync("Add vector layer failed", ex.Message); }
                return;
            }
            if (!path.EndsWith(".ers", StringComparison.OrdinalIgnoreCase)) return;

            // Layers already loaded: add this one alongside them rather than replacing everything.
            if (_view.Layers.Count == 0) OpenDataset(path);
            else AddLayer(path);
        }

        // ---- layers --------------------------------------------------------------

        private async Task AddLayerDialogAsync()
        {
            IStorageFolder? startLocation = null;
            if (_settings.LastOpenDirectory != null && Directory.Exists(_settings.LastOpenDirectory))
                startLocation = await StorageProvider.TryGetFolderFromPathAsync(_settings.LastOpenDirectory);

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add layer(s) — raster (.ers) or vector (.erv)",
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
                    if (path!.EndsWith(".erv", StringComparison.OrdinalIgnoreCase))
                        _view.AddVectorLayerFromPath(path);
                    else
                        _view.AddLayerFromPath(path, CurrentPalette());
                    _settings.AddRecentFile(Path.GetFullPath(path));
                    lastDir = Path.GetDirectoryName(Path.GetFullPath(path));
                }
                catch (Exception ex) { await MessageAsync("Add layer failed", $"{Path.GetFileName(path)}: {ex.Message}"); }
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
            catch (Exception ex) { _ = MessageAsync("Layer action failed", ex.Message); }
        }

        private void RebuildLayersPanel()
        {
            _layersPanel.Children.Clear();
            var drawOrder = _view.DrawOrder;

            if (drawOrder.Count == 0)
            {
                _layersPanel.Children.Add(new TextBlock { Text = "No layers loaded.", Opacity = 0.6, FontStyle = FontStyle.Italic, Margin = new Thickness(2, 4) });
                return;
            }

            // The panel reads from the exact shared draw stack, top to bottom. A vector can be
            // moved across a raster layer, so it must not live in a separate fixed section.
            for (int i = drawOrder.Count - 1; i >= 0; i--)
            {
                switch (drawOrder[i])
                {
                    case VectorLayer vectorLayer:
                        AddVectorLayerCard(vectorLayer);
                        break;
                    case RasterLayer rasterLayer:
                        AddRasterLayerCard(rasterLayer);
                        break;
                }
            }
        }

        private void AddVectorLayerCard(VectorLayer layer)
        {
                var visBox = new CheckBox { IsChecked = layer.IsVisible, VerticalAlignment = VerticalAlignment.Center };
                visBox.IsCheckedChanged += (_, _) => RunLayerActionSafely(() => _view.SetVectorLayerVisible(layer, visBox.IsChecked == true));

                var nameText = new TextBlock { Text = "▤ " + layer.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var upBtn = CircleIconButton("▲");
                upBtn.IsEnabled = _view.CanMoveLayerUp(layer);
                upBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveVectorLayerUp(layer));
                var downBtn = CircleIconButton("▼");
                downBtn.IsEnabled = _view.CanMoveLayerDown(layer);
                downBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveVectorLayerDown(layer));
                var removeBtn = CircleIconButton("✕", AppTheme.Danger);
                removeBtn.Click += (_, _) => RunLayerActionSafely(() => _view.RemoveVectorLayer(layer));

                _layersPanel.Children.Add(BuildLayerCard(visBox, nameText, upBtn, downBtn, removeBtn, isActive: false, BuildVectorStyleRow(layer)));
        }

        private void AddRasterLayerCard(RasterLayer layer)
        {
                bool isActive = ReferenceEquals(layer, _view.ActiveLayer);

                var visBox = new CheckBox { IsChecked = layer.IsVisible, VerticalAlignment = VerticalAlignment.Center };
                // Bind every action to the layer object, rather than to its momentary list
                // position.  The panel is rebuilt asynchronously after a move, so an index
                // captured by an old card can otherwise point at a different file.
                visBox.IsCheckedChanged += (_, _) => RunLayerActionSafely(() => _view.SetLayerVisible(layer, visBox.IsChecked == true));

                var nameBtn = new Button
                {
                    Content = layer.Name,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal,
                    Background = Brushes.Transparent, // the card itself conveys "active" — no double highlight
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2, 0),
                };
                nameBtn.Click += (_, _) => RunLayerActionSafely(() => _view.SetActiveLayer(layer));

                var upBtn = CircleIconButton("▲");
                upBtn.IsEnabled = _view.CanMoveLayerUp(layer);
                upBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveLayerUp(layer));
                var downBtn = CircleIconButton("▼");
                downBtn.IsEnabled = _view.CanMoveLayerDown(layer);
                downBtn.Click += (_, _) => RunLayerActionSafely(() => _view.MoveLayerDown(layer));
                var removeBtn = CircleIconButton("✕", AppTheme.Danger);
                removeBtn.Click += (_, _) => RunLayerActionSafely(() => _view.RemoveLayer(layer));

                // Raster display options live in one place: the right-side panel. Its controls
                // always target ActiveLayer, so selecting this card first makes it unambiguous
                // which file's palette, stretch, band and gamma are being changed.
                _layersPanel.Children.Add(BuildLayerCard(visBox, nameBtn, upBtn, downBtn, removeBtn, isActive));
        }

        /// <summary>A vector layer's inline style row: a hex-colour swatch/box and a line-width stepper, both scoped to THIS layer.</summary>
        private StackPanel BuildVectorStyleRow(VectorLayer layer)
        {
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(layer.Color),
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var hexBox = new TextBox
            {
                Text = FormatHex(layer.Color),
                Width = 84,
                VerticalContentAlignment = VerticalAlignment.Center,
                Watermark = "#RRGGBB",
            };
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
                Value = (decimal)layer.LineWidth,
                Minimum = 0.5m,
                Maximum = 20m,
                Increment = 0.5m,
                FormatString = "0.#",
                Width = 96,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            widthBox.ValueChanged += (_, e) =>
            {
                if (e.NewValue is decimal v) RunLayerActionSafely(() => _view.SetVectorLayerLineWidth(layer, (double)v));
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(swatch);
            row.Children.Add(hexBox);
            row.Children.Add(widthBox);
            return row;
        }

        private static string FormatHex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

        /// <summary>A small, circular icon button — a clear, comfortably-clickable target, instead of a cramped default-sized square one.</summary>
        private static Button CircleIconButton(string glyph, IBrush? foreground = null)
        {
            return new Button
            {
                Content = glyph,
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(0),
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = AppTheme.BarBackground,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                Foreground = foreground ?? AppTheme.TextPrimary,
            };
        }

        /// <summary>
        /// Wraps one layer's row controls in a rounded card — the active raster layer gets an
        /// accent-coloured border and a tinted background, so which layer is "current" (the one
        /// the side panel's palette/band controls, the pointer readout and every tool target) is
        /// obvious at a glance, and so a reorder is visibly a card moving, not just numbers
        /// changing behind an otherwise identical-looking row.
        /// </summary>
        private static Border BuildLayerCard(Control visBox, Control name, Control upBtn, Control downBtn, Control removeBtn, bool isActive, Control? styleRow = null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 4 };
            Grid.SetColumn(visBox, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(upBtn, 2);
            Grid.SetColumn(downBtn, 3);
            Grid.SetColumn(removeBtn, 4);
            row.Children.Add(visBox);
            row.Children.Add(name);
            row.Children.Add(upBtn);
            row.Children.Add(downBtn);
            row.Children.Add(removeBtn);

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
                CornerRadius = new CornerRadius(10),
                Background = isActive ? AppTheme.ActiveHighlight : AppTheme.BarBackground,
                BorderBrush = isActive ? AppTheme.Accent : AppTheme.Border,
                BorderThickness = new Thickness(isActive ? 2 : 1),
                Padding = new Thickness(6, 4),
                Margin = new Thickness(0, 3),
            };
        }

        // ---- save --------------------------------------------------------------

        private async Task SaveHeaderAsAsync()
        {
            if (_view.Document == null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save ER Mapper header",
                DefaultExtension = "ers",
                SuggestedFileName = SuggestName() + ".ers",
                FileTypeChoices = ErsSaveFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                _view.Document.SaveHeader(path!);
                Flash($"Header written: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { await MessageAsync("Save failed", ex.Message); }
        }

        private async Task SaveDatasetAsAsync()
        {
            if (_view.Document == null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save ER Mapper dataset (writes .ers + binary data file)",
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
                Flash($"Dataset written: {Path.GetFileName(path)} (+ data file)");
            }
            catch (Exception ex) { await MessageAsync("Save failed", ex.Message); }
        }

        private async Task ExportPngAsync()
        {
            var image = _view.RenderToImage();
            if (image == null) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export coloured raster as PNG",
                DefaultExtension = "png",
                SuggestedFileName = SuggestName() + ".png",
                FileTypeChoices = PngFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                SaveImageAsPng(image, path!);
                Flash($"PNG written: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { await MessageAsync("Export failed", ex.Message); }
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

        private void OnRasterLoaded()
        {
            if (_view.ActiveLayer == null)
            {
                // The last layer was just removed: nothing to show.
                SetControlsEnabled(false);
                _bandGroup.IsVisible = false;
                _infoText.Text = "No layers loaded.";
                _legend.SetColorizer(null);
                _coordText.Text = _cellText.Text = _valueText.Text = _scaleText.Text = "—";
                return;
            }

            SetControlsEnabled(true);

            var c = _view.Colorizer!;
            _syncing = true;
            _minBox.Value = (decimal)c.Minimum;
            _maxBox.Value = (decimal)c.Maximum;
            _syncing = false;

            var header = _view.Document!.Header;
            UpdateBandSelector(header);
            var stats = _view.CurrentStatistics;
            string crs = header.CoordinateSpace.Projection ?? "RAW";
            if (header.CoordinateSpace.TryGetEpsg(out int epsg)) crs += $" (EPSG:{epsg})";
            string statsText = stats == null
                ? "—"
                : string.Format(CultureInfo.InvariantCulture, "data {0:g4} … {1:g4}  (µ {2:g4}, σ {3:g4}){4}",
                    stats.Minimum, stats.Maximum, stats.Mean, stats.StandardDeviation,
                    _view.IsStreaming ? ", approx." : "");
            _infoText.Text = string.Format(CultureInfo.InvariantCulture,
                "{0}×{1}  {2}  {3}  |  {4}  |  {5}{6}",
                header.RasterInfo.NrOfCellsPerLine, header.RasterInfo.NrOfLines,
                header.RasterInfo.CellType, crs, statsText,
                header.ByteOrder, _view.IsStreaming ? "  |  streaming (large dataset)" : "");

            ApplyMode();
            _legend.SetColorizer(c, UnitLabel());
            UpdateScaleText();
            UpdatePaletteControlsEnabled(); // must come last: overrides the legend/enabled-state above when in RGB composite mode
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
                    ? $"Band {i + 1} — {bandInfos[i].Value}"
                    : $"Band {i + 1}";
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
            if (gsd == null) { _scaleText.Text = "—"; return; }

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

        private void UpdateStatus(RasterReadoutEventArgs r)
        {
            if (_view.Document == null)
            {
                _coordText.Text = _cellText.Text = _valueText.Text = "—";
                return;
            }
            _coordText.Text = string.Format(CultureInfo.InvariantCulture, "E {0:0.###}   N {1:0.###}", r.WorldX, r.WorldY);
            _cellText.Text = r.InsideRaster ? $"cell [{r.Column}, {r.Row}]" : "cell —";
            _valueText.Text = r.Value.HasValue
                ? "value " + r.Value.Value.ToString("g6", CultureInfo.InvariantCulture)
                : (r.InsideRaster ? "value (no-data)" : "value —");
        }

        private void SetControlsEnabled(bool on)
        {
            foreach (var c in new Control[] { _bandBox, _paletteBox, _reverseBox, _stretchBox, _modeBox, _minBox, _maxBox, _gammaSlider, _outTypeBox, _outOrderBox })
                c.IsEnabled = on;
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
                    Flash($"Palette saved: {p.Name}");
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
                await MessageAsync("Fill no-data gaps", "Open a dataset first.");
                return;
            }
            if (_view.Raster == null)
            {
                await MessageAsync("Fill no-data gaps",
                    _view.IsStreaming
                        ? "This dataset is large and is shown in streaming mode, so it is never fully loaded into memory. " +
                          "Clip a smaller region first (Tools ▸ Clip tool), open the clipped result, then fill that."
                        : "The raster is not loaded.");
                return;
            }

            // Only cells within the convex hull of the valid data count as real "gaps" — the
            // no-data margin outside the data's actual footprint (a rotated scene's background
            // corners, a mosaic's missing corner, …) is left alone and isn't a gap to report.
            long gaps = NoDataFiller.CountNoDataWithinHull(_view.Raster);
            if (gaps == 0)
            {
                await MessageAsync("Fill no-data gaps", "This band has no no-data gaps within its data footprint to fill.");
                return;
            }

            var choice = await ShowFillNoDataDialogAsync(gaps);
            if (choice == null) return;

            var raster = _view.Raster;
            SetBusy(true, "Filling no-data gaps…");
            Raster filled = await Task.Run(() => choice.Value.Method == FillMethod.Nearest
                ? NoDataFiller.FillNearest(raster)
                : NoDataFiller.FillInverseDistanceWeighted(raster, choice.Value.MaxSearchDistance, choice.Value.SmoothingIterations));
            SetBusy(false);

            _view.Document.ReplaceBand(0, filled);
            _view.RefreshFromDocument();
            _legend.SetColorizer(_view.Colorizer, UnitLabel());

            // Every in-hull gap is now guaranteed filled (see NoDataFiller's remarks) — anything
            // still no-data afterwards is, by construction, outside the data's footprint.
            Flash($"Filled all {gaps:N0} gap cell(s) within the data's footprint.");
        }

        private async Task<(FillMethod Method, int MaxSearchDistance, int SmoothingIterations)?> ShowFillNoDataDialogAsync(long gapCount)
        {
            var tcs = new TaskCompletionSource<(FillMethod, int, int)?>();

            var methodBox = new ComboBox
            {
                ItemsSource = new[] { "Inverse-distance weighted (recommended)", "Nearest neighbour (fast)" },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var distBox = new NumericUpDown { Minimum = 1, Maximum = 100000, Value = 100, Increment = 10, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            var smoothBox = new NumericUpDown { Minimum = 0, Maximum = 10, Value = 1, Increment = 1, Width = 100, HorizontalAlignment = HorizontalAlignment.Left };

            var dialog = new Window
            {
                Title = "Fill no-data gaps",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = "Fill", MinWidth = 80 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
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
                    new TextBlock { Text = $"{gapCount:N0} gap cell(s) found within the data's footprint.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = "Only gaps inside the convex hull of the valid data are ever touched — the no-data margin outside the data's actual footprint (a rotated scene's background corners, a mosaic's missing corner, …) is left exactly as it is. Every in-hull gap is guaranteed to be filled. IDW searches outward along several directions for the nearest valid pixels and takes their inverse-distance-weighted average (the method behind GDAL's FillNodata); Nearest just copies the closest valid cell.",
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 11,
                    },
                    methodBox,
                    new TextBlock { Text = "Max search distance (cells, IDW only)" },
                    distBox,
                    new TextBlock { Text = "Smoothing passes (IDW only)" },
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
                await MessageAsync("Clip by extent", "Open a dataset first.");
                return;
            }

            var bounds = _view.Document.GeoReference.WorldBounds();
            var extent = await ShowClipExtentDialogAsync(bounds);
            if (extent == null) return;

            var doc = _view.Document;
            SetBusy(true, "Cropping…");
            try
            {
                var clip = await Task.Run(() => doc.ClipToWorldExtent(extent.Value.MinX, extent.Value.MinY, extent.Value.MaxX, extent.Value.MaxY));
                SetBusy(false);
                await PerformClipAndSaveAsync(clip);
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Clip failed", ex.Message); }
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
                Title = "Clip by extent",
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = "Clip…", MinWidth = 80 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
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

            TextBlock Label(string t) => new TextBlock { Text = t, Opacity = 0.8 };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "World-coordinate extent to keep (pre-filled with the full raster bounds):", TextWrapping = TextWrapping.Wrap },
                    Label("Min E (west)"), minXBox,
                    Label("Max E (east)"), maxXBox,
                    Label("Min N (south)"), minYBox,
                    Label("Max N (north)"), maxYBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async Task PerformClipAndSaveAsync(ErsDocument clip) => await PerformDerivedSaveAsync(clip, "_clip", "Clip");

        /// <summary>
        /// Prompts for a save location, writes <paramref name="result"/> (a clip, a computed
        /// terrain/band-math/mosaic raster, …) as a new <c>.ers</c> dataset, and opens it.
        /// </summary>
        private async Task PerformDerivedSaveAsync(ErsDocument result, string suggestedSuffix, string label)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save {label.ToLowerInvariant()} dataset (writes .ers + binary data file)",
                DefaultExtension = "ers",
                SuggestedFileName = SuggestName() + suggestedSuffix + ".ers",
                FileTypeChoices = ErsSaveFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            result.Save(path!);
            Flash($"{label} saved: {Path.GetFileName(path)}  ({result.Band!.Width}×{result.Band!.Height})");
            OpenDataset(path!);
        }

        /// <summary>
        /// Wraps a computed single-band result (terrain derivative, band-math output, …) into a
        /// new document sharing the current dataset's georeference (origin, cell size, rotation
        /// dropped — these outputs are never rotated) and coordinate system.
        /// </summary>
        private ErsDocument BuildDerivedDocument(Raster result)
        {
            var doc = _view.Document ?? throw new InvalidOperationException("No dataset is open.");
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
            if (_view.Document == null)
            {
                await MessageAsync(toolName, "Open a dataset first.");
                return null;
            }
            if (_view.Raster == null)
            {
                await MessageAsync(toolName,
                    _view.IsStreaming
                        ? "This dataset is large and is shown in streaming mode, so it is never fully loaded into memory. " +
                          "Clip a smaller region first (Tools ▸ Clip tool), open the clipped result, then run this on that."
                        : "The raster is not loaded.");
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
            var loaded = await TryGetLoadedRasterAsync(product.ToString());
            if (loaded == null) return;
            var (raster, cellSizeX, cellSizeY) = loaded.Value;

            SetBusy(true, $"Computing {product}…");
            try
            {
                Raster result = await Task.Run(() => product switch
                {
                    TerrainProduct.Slope => TerrainAnalysis.Slope(raster, cellSizeX, cellSizeY),
                    TerrainProduct.Aspect => TerrainAnalysis.Aspect(raster, cellSizeX, cellSizeY),
                    _ => TerrainAnalysis.Hillshade(raster, cellSizeX, cellSizeY),
                });
                SetBusy(false);

                var doc = BuildDerivedDocument(result);
                await PerformDerivedSaveAsync(doc, "_" + product.ToString().ToLowerInvariant(), product.ToString());
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync($"{product} failed", ex.Message); }
        }

        // ---- curvature -------------------------------------------------------------

        private async Task ComputeCurvatureAsync()
        {
            var loaded = await TryGetLoadedRasterAsync("Curvature");
            if (loaded == null) return;
            var (raster, cellSizeX, cellSizeY) = loaded.Value;

            var type = await ShowCurvatureDialogAsync();
            if (type == null) return;

            SetBusy(true, $"Computing {type} curvature…");
            try
            {
                Raster result = await Task.Run(() => TerrainAnalysis.Curvature(raster, cellSizeX, cellSizeY, type.Value));
                SetBusy(false);

                var doc = BuildDerivedDocument(result);
                await PerformDerivedSaveAsync(doc, "_curvature_" + type.Value.ToString().ToLowerInvariant(), type.Value + " curvature");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Curvature failed", ex.Message); }
        }

        private async Task<CurvatureType?> ShowCurvatureDialogAsync()
        {
            var tcs = new TaskCompletionSource<CurvatureType?>();

            var typeBox = new ComboBox
            {
                ItemsSource = new[] { "General (convex/concave overall shape)", "Profile (along the slope — affects flow speed)", "Plan (across the slope — affects flow convergence)" },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var dialog = new Window
            {
                Title = "Curvature",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = "Compute", MinWidth = 80 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
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
                    new TextBlock { Text = "Positive = convex (dome/ridge); negative = concave (bowl/valley); zero = planar.", TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 11 },
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
            var loaded = await TryGetLoadedRasterAsync("Flow direction");
            if (loaded == null) return;
            var (raster, cellSizeX, cellSizeY) = loaded.Value;

            SetBusy(true, "Computing flow direction…");
            try
            {
                Raster result = await Task.Run(() => HydrologyAnalysis.FlowDirection(raster, cellSizeX, cellSizeY));
                SetBusy(false);

                var doc = BuildDerivedDocument(result);
                await PerformDerivedSaveAsync(doc, "_flowdir", "Flow direction");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Flow direction failed", ex.Message); }
        }

        private async Task ComputeFlowAccumulationAsync()
        {
            var loaded = await TryGetLoadedRasterAsync("Flow accumulation");
            if (loaded == null) return;
            var (raster, cellSizeX, cellSizeY) = loaded.Value;

            SetBusy(true, "Computing flow accumulation…");
            try
            {
                Raster result = await Task.Run(() =>
                {
                    Raster direction = HydrologyAnalysis.FlowDirection(raster, cellSizeX, cellSizeY);
                    return HydrologyAnalysis.FlowAccumulation(direction);
                });
                SetBusy(false);

                var doc = BuildDerivedDocument(result);
                await PerformDerivedSaveAsync(doc, "_flowacc", "Flow accumulation");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Flow accumulation failed", ex.Message); }
        }

        // ---- viewshed ----------------------------------------------------------------

        private async Task ComputeViewshedAsync()
        {
            var loaded = await TryGetLoadedRasterAsync("Viewshed");
            if (loaded == null) return;
            var (raster, _, _) = loaded.Value;

            var options = await ShowViewshedDialogAsync(raster.Width, raster.Height);
            if (options == null) return;

            SetBusy(true, "Computing viewshed…");
            try
            {
                Raster result = await Task.Run(() => ViewshedAnalysis.Compute(
                    raster, options.Value.ObserverCol, options.Value.ObserverRow,
                    options.Value.ObserverHeight, options.Value.TargetHeight, options.Value.MaxDistanceCells));
                SetBusy(false);

                var doc = BuildDerivedDocument(result);
                await PerformDerivedSaveAsync(doc, "_viewshed", "Viewshed");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Viewshed failed", ex.Message); }
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
                Title = "Viewshed",
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = "Compute…", MinWidth = 80 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
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
                    new TextBlock { Text = "Observer location (raster cell — column/row, 0-based):", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Column" }, colBox,
                    new TextBlock { Text = "Row" }, rowBox,
                    new TextBlock { Text = "Observer eye height above ground" }, observerHeightBox,
                    new TextBlock { Text = "Target height above ground" }, targetHeightBox,
                    new TextBlock { Text = "Max distance in cells (0 = unlimited)" }, maxDistBox,
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
            var loaded = await TryGetLoadedRasterAsync("Swiss-style relief");
            if (loaded == null) return;
            var (raster, cellSizeX, cellSizeY) = loaded.Value;

            SetBusy(true, "Rendering Swiss-style relief…");
            RasterImage image;
            try
            {
                image = await Task.Run(() => ReliefShader.RenderSwissStyle(raster, cellSizeX, cellSizeY));
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Swiss-style relief failed", ex.Message); return; }
            SetBusy(false);

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Swiss-style relief",
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
                    Flash($"Relief image written: {Path.GetFileName(path)}");
                }
                else
                {
                    var doc = BuildRgbDocument(image);
                    doc.Save(path);
                    Flash($"Relief dataset written: {Path.GetFileName(path)} (3-band true colour)");
                    OpenDataset(path);
                }
            }
            catch (Exception ex) { await MessageAsync("Export failed", ex.Message); }
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
            var doc = _view.Document;
            if (doc == null) { await MessageAsync("Band math", "Open a dataset first."); return; }
            if (doc.Bands.Count == 0)
            {
                await MessageAsync("Band math",
                    _view.IsStreaming
                        ? "This dataset is large and is shown in streaming mode, so its bands are never fully loaded into memory."
                        : "The raster is not loaded.");
                return;
            }

            var bandNames = new List<string>();
            var bands = new Dictionary<string, Raster>();
            for (int i = 0; i < doc.Bands.Count; i++)
            {
                string name = $"b{i + 1}";
                bandNames.Add(name);
                bands[name] = doc.Bands[i];
            }

            var expr = await ShowBandMathDialogAsync(bandNames);
            if (string.IsNullOrWhiteSpace(expr)) return;

            SetBusy(true, "Evaluating expression…");
            try
            {
                Raster result = await Task.Run(() => RasterAlgebra.Evaluate(expr!, bands));
                SetBusy(false);
                var newDoc = BuildDerivedDocument(result);
                await PerformDerivedSaveAsync(newDoc, "_bandmath", "Band math result");
            }
            catch (RasterAlgebraException ex) { SetBusy(false); await MessageAsync("Band math failed", ex.Message); }
        }

        private async Task<string?> ShowBandMathDialogAsync(IReadOnlyList<string> bandNames)
        {
            var tcs = new TaskCompletionSource<string?>();

            var exprBox = new TextBox { Watermark = "e.g. (b1 - b2) / (b1 + b2)" };

            var dialog = new Window
            {
                Title = "Band math",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = "Compute", MinWidth = 80 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
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
                    new TextBlock { Text = $"Available bands: {string.Join(", ", bandNames)}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = "Operators + - * / and comparisons (> >= < <= == !=); functions abs, sqrt, exp, log, log10, min, max, pow, iif(cond, a, b); constants pi, e. A cell is no-data in the output if any band it references is no-data there.",
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
                Title = "Select rasters to mosaic (2 or more)",
                AllowMultiple = true,
                FileTypeFilter = ErsOpenFileTypeFilter,
            });

            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
            if (paths.Count < 2)
            {
                if (paths.Count == 1) await MessageAsync("Mosaic rasters", "Select at least two datasets to mosaic.");
                return;
            }

            List<ErsDocument> docs;
            SetBusy(true, "Loading rasters…");
            try { docs = await Task.Run(() => paths.Select(ErsDocument.Load).ToList()); }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Mosaic rasters", $"Could not load one of the selected files: {ex.Message}"); return; }
            SetBusy(false);

            var (_, b, c, _, e, f) = docs[0].GeoReference.GeoTransform;
            double defaultCellX = Math.Sqrt(b * b + e * e), defaultCellY = Math.Sqrt(c * c + f * f);

            var options = await ShowMosaicOptionsDialogAsync(docs.Count, defaultCellX, defaultCellY);
            if (options == null) return;

            SetBusy(true, "Merging rasters…");
            try
            {
                var mosaic = await Task.Run(() => ErsDocument.Mosaic(docs, options.Value.CellSizeX, options.Value.CellSizeY, options.Value.OverlapMode));
                SetBusy(false);
                await PerformDerivedSaveAsync(mosaic, "_mosaic", "Mosaic");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Mosaic failed", ex.Message); }
        }

        private async Task<(double CellSizeX, double CellSizeY, MosaicOverlapMode OverlapMode)?> ShowMosaicOptionsDialogAsync(
            int fileCount, double defaultCellX, double defaultCellY)
        {
            var tcs = new TaskCompletionSource<(double, double, MosaicOverlapMode)?>();

            var cellXBox = new NumericUpDown { Value = (decimal)defaultCellX, FormatString = "0.####", Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var cellYBox = new NumericUpDown { Value = (decimal)defaultCellY, FormatString = "0.####", Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var overlapBox = new ComboBox
            {
                ItemsSource = new[] { "Last wins (later files overwrite)", "First wins (earlier files kept)", "Average" },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var dialog = new Window
            {
                Title = "Mosaic rasters",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = "Mosaic…", MinWidth = 80 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
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
                    new TextBlock { Text = $"{fileCount} datasets selected. Output cell size:", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Cell size X" }, cellXBox,
                    new TextBlock { Text = "Cell size Y" }, cellYBox,
                    new TextBlock { Text = "Where datasets overlap" }, overlapBox,
                    buttons,
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        // ---- contours ----------------------------------------------------------------

        private async Task GenerateContoursAsync()
        {
            var loaded = await TryGetLoadedRasterAsync("Generate contours");
            if (loaded == null) return;
            var (raster, _, _) = loaded.Value;
            var doc = _view.Document!;

            var stats = raster.Statistics;
            var options = await ShowContourDialogAsync(stats.Minimum, stats.Maximum);
            if (options == null) return;

            var levels = BuildContourLevels(options.Value.Min, options.Value.Max, options.Value.Interval);
            if (levels.Count == 0)
            {
                await MessageAsync("Generate contours", "No levels fall within the given range/interval.");
                return;
            }

            SetBusy(true, "Tracing contours…");
            var lines = await Task.Run(() => ContourGenerator.TraceLevels(raster, doc.GeoReference, levels));
            SetBusy(false);
            if (lines.Count == 0)
            {
                await MessageAsync("Generate contours", "No contour lines were produced for these levels.");
                return;
            }

            var erv = ErvDocument.Create(doc.Header.CoordinateSpace.Projection, doc.Header.CoordinateSpace.Datum);
            foreach (var line in lines)
            {
                var poly = new VectorPolyline { Attribute = line.Level.ToString("g6", CultureInfo.InvariantCulture) };
                foreach (var p in line.Points) poly.Points.Add((p.X, p.Y));
                erv.Objects.Add(poly);
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save contours as ER Mapper vector dataset",
                DefaultExtension = "erv",
                SuggestedFileName = SuggestName() + "_contours.erv",
                FileTypeChoices = ErvFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            erv.Save(path!);
            Flash($"Contours saved: {Path.GetFileName(path)}  ({lines.Count} line(s), {levels.Count} level(s))");
        }

        private static List<double> BuildContourLevels(double min, double max, double interval)
        {
            var levels = new List<double>();
            if (interval <= 0 || max <= min) return levels;

            double start = Math.Ceiling(min / interval) * interval;
            for (double v = start; v <= max + 1e-9; v += interval)
                levels.Add(v);
            return levels;
        }

        private async Task<(double Min, double Max, double Interval)?> ShowContourDialogAsync(double dataMin, double dataMax)
        {
            var tcs = new TaskCompletionSource<(double, double, double)?>();

            double defaultInterval = Math.Max(1e-6, (dataMax - dataMin) / 10.0);
            var minBox = new NumericUpDown { Value = (decimal)dataMin, FormatString = "0.###", Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var maxBox = new NumericUpDown { Value = (decimal)dataMax, FormatString = "0.###", Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var intervalBox = new NumericUpDown { Value = (decimal)defaultInterval, FormatString = "0.###", Increment = 1, Minimum = 0.000001M, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };

            var dialog = new Window
            {
                Title = "Generate contours",
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var okBtn = new Button { Content = "Generate…", MinWidth = 80 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
            okBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(((double)(minBox.Value ?? (decimal)dataMin), (double)(maxBox.Value ?? (decimal)dataMax), (double)(intervalBox.Value ?? (decimal)defaultInterval)));
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
                    new TextBlock { Text = $"Data range: {dataMin:g4} … {dataMax:g4}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Minimum level" }, minBox,
                    new TextBlock { Text = "Maximum level" }, maxBox,
                    new TextBlock { Text = "Interval" }, intervalBox,
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
            if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                if (e.Key == Key.O) { _ = AddLayerDialogAsync(); e.Handled = true; }
                return;
            }

            if (e.KeyModifiers == KeyModifiers.Control)
            {
                switch (e.Key)
                {
                    case Key.O: _ = OpenDialogAsync(); e.Handled = true; break;
                    case Key.S: _ = SaveDatasetAsAsync(); e.Handled = true; break;
                    case Key.D0: _view.ZoomToFit(); e.Handled = true; break;
                    case Key.OemPlus: _view.ZoomBy(1.25); e.Handled = true; break;
                    case Key.OemMinus: _view.ZoomBy(0.8); e.Handled = true; break;
                }
            }
        }

        private void Flash(string message) => _infoText.Text = message;

        private async Task ShowAboutAsync() => await MessageAsync("About RasterField",
            "RasterField — a cross-platform pan/zoom viewer for the ERDAS ER Mapper raster format\n" +
            "(.ers header + Band-Interleaved-by-Line data file).\n\n" +
            "Built with Avalonia and the RasterField library: robust .ers parser and writer,\n" +
            "BIL reader/writer with byte-order handling, georeferencing and palette colourisation.\n\n" +
            "Drag to pan · wheel to zoom · arrows / +/- · 0 or F to fit.");

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
            var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
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
            return hp != null ? Path.GetFileNameWithoutExtension(hp) : "raster";
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
