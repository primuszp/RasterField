using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RasterField.Gdal;
using RasterField.Rasters;
using RasterField.Rendering;
using RasterField.Vectors;
using static RasterField.L;

namespace RasterField
{
    public sealed partial class MainWindow
    {
        // ---- menu model -------------------------------------------------------------
        //
        // One declarative tree drives both the in-window menu (Windows/Linux) and the macOS system
        // menu bar, so the two can never drift apart. Toggle/radio items read their state from a
        // delegate and are refreshed together by RefreshMenuChecks().

        private sealed class Cmd
        {
            public Cmd(string header, Action? run = null, KeyGesture? gesture = null, Func<bool>? isChecked = null, bool radio = false)
            {
                Header = header; Run = run; Gesture = gesture; IsChecked = isChecked; Radio = radio;
            }

            public string Header { get; }
            public Action? Run { get; }
            public KeyGesture? Gesture { get; }
            public Func<bool>? IsChecked { get; }
            public bool Radio { get; }
            public List<Cmd?> Children { get; } = new List<Cmd?>(); // null = separator

            public Cmd Add(params Cmd?[] items) { Children.AddRange(items); return this; }
        }

        private readonly List<(MenuItem Item, Func<bool> IsChecked)> _menuChecks = new();
        private readonly List<(NativeMenuItem Item, Func<bool> IsChecked)> _nativeMenuChecks = new();

        private static KeyGesture Ctrl(Key key, bool shift = false) =>
            new KeyGesture(key, KeyModifiers.Control | (shift ? KeyModifiers.Shift : KeyModifiers.None));

        /// <summary>
        /// The top-level menus, grouped by what the user is working on rather than by algorithm:
        /// <c>File · Edit · View · Layer · Raster · Analysis · Tools · (Window) · Help</c>.
        /// On macOS the app menu (About, Hide, Quit) comes from <see cref="App"/> instead
        /// of File ▸ Exit / Help ▸ About, and a Window menu is added, as the platform expects.
        /// </summary>
        private List<Cmd> BuildMenuModel()
        {
            bool mac = OperatingSystem.IsMacOS();

            var export = new Cmd("_Export").Add(
                new Cmd("_View as PNG…", () => _ = ExportPngAsync()),
                new Cmd("Inspection report as _PDF…", () => _ = ExportInspectionReportAsync()));

            var file = new Cmd("_File").Add(
                new Cmd("_New project", () => _ = NewProjectAsync()),
                new Cmd("Open _project…", () => _ = OpenProjectDialogAsync()),
                new Cmd("_Open raster…", () => _ = OpenDialogAsync(), Ctrl(Key.O)),
                new Cmd("_Add layer(s)…", () => _ = AddLayerDialogAsync(), Ctrl(Key.O, shift: true)),
                new Cmd("Open _recent"), // filled by RebuildRecentMenu
                null,
                new Cmd("_Save project", () => _ = SaveProjectAsync(false), Ctrl(Key.S)),
                new Cmd("Save project _as…", () => _ = SaveProjectAsync(true)),
                null,
                new Cmd("Save active _layer…", () => _ = SaveActiveLayerAsync()),
                new Cmd("Save _dataset as…", () => _ = SaveDatasetAsAsync(), Ctrl(Key.S, shift: true)),
                new Cmd("Save _header as .ers…", () => _ = SaveHeaderAsAsync()),
                null,
                export);
            if (!mac) file.Add(null, new Cmd("E_xit", Close));

            var edit = new Cmd("_Edit").Add(
                new Cmd("_Undo", Undo, Ctrl(Key.Z)),
                new Cmd("_Redo", Redo, Ctrl(Key.Y)),
                null,
                new Cmd("_Command palette…", () => _ = ShowCommandPaletteAsync(), Ctrl(Key.K)));

            var magnification = new Cmd("_Magnification").Add(
                new Cmd("_Nearest (crisp cells)", () => SetDisplayResampling(DisplayResampling.Nearest), isChecked: () => _view.DisplayResampling == DisplayResampling.Nearest, radio: true),
                new Cmd("_Bilinear", () => SetDisplayResampling(DisplayResampling.Bilinear), isChecked: () => _view.DisplayResampling == DisplayResampling.Bilinear, radio: true),
                new Cmd("Bé_zier patch (smooth surface)", () => SetDisplayResampling(DisplayResampling.Bezier), isChecked: () => _view.DisplayResampling == DisplayResampling.Bezier, radio: true),
                null,
                new Cmd("Bézier _display settings…", () => _ = ShowBezierDisplaySettingsAsync()));

            var bookmarks = new Cmd("_Bookmarks").Add(new Cmd("_Add bookmark…", () => _ = AddBookmarkAsync(), Ctrl(Key.D, shift: true)));
            for (int b = 0; b < _bookmarks.Count && b < 9; b++)
            {
                var bm = _bookmarks[b];
                bookmarks.Add(new Cmd(string.Format(CultureInfo.InvariantCulture, "{0}  {1}", b + 1, bm.Name), () => GoToBookmark(bm), Ctrl(Key.D1 + b)));
            }
            if (_bookmarks.Count > 0) bookmarks.Add(null, new Cmd("_Clear bookmarks", () => { _bookmarks.Clear(); RebuildMenus(); }));

            var comparison = new Cmd("_Comparison").Add(
                new Cmd("_Swipe…", () => _ = ConfigureVisualComparisonAsync(RasterComparisonMode.Swipe),
                    isChecked: () => _view.ComparisonMode == RasterComparisonMode.Swipe),
                new Cmd("_Blink…", () => _ = ConfigureVisualComparisonAsync(RasterComparisonMode.Blink),
                    isChecked: () => _view.ComparisonMode == RasterComparisonMode.Blink),
                null,
                new Cmd("_Stop comparison", () => { _view.StopComparison(); RefreshMenuChecks(); }));

            var view = new Cmd("_View").Add(
                new Cmd("Zoom to _fit", () => _view.ZoomToFit(), Ctrl(Key.D0)),
                new Cmd("Zoom _in", () => _view.ZoomBy(1.25), Ctrl(Key.OemPlus)),
                new Cmd("Zoom _out", () => _view.ZoomBy(0.8), Ctrl(Key.OemMinus)),
                new Cmd("_Go to coordinate…", () => _ = GoToCoordinateAsync()),
                bookmarks,
                null,
                magnification,
                new Cmd("Show cell _grid", () => { _view.ShowGrid = !_view.ShowGrid; _view.InvalidateVisual(); RefreshMenuChecks(); }, isChecked: () => _view.ShowGrid),
                comparison,
                null,
                new Cmd("_Layers && analysis panel", ToggleLeftDock, new KeyGesture(Key.F9), isChecked: () => _leftDockVisible),
                new Cmd("_Properties panel", ToggleRightDock, new KeyGesture(Key.F10), isChecked: () => _rightDockVisible),
                new Cmd("_Map only", ToggleMapOnly, new KeyGesture(Key.F11), isChecked: () => !_leftDockVisible && !_rightDockVisible),
                null,
                new Cmd("_Theme").Add(
                    new Cmd("_System", () => SetThemeMode(ThemeMode.System), isChecked: () => _settings.Theme == ThemeMode.System, radio: true),
                    new Cmd("_Light", () => SetThemeMode(ThemeMode.Light), isChecked: () => _settings.Theme == ThemeMode.Light, radio: true),
                    new Cmd("_Dark", () => SetThemeMode(ThemeMode.Dark), isChecked: () => _settings.Theme == ThemeMode.Dark, radio: true)),
                new Cmd("_Language").Add(
                    new Cmd("_Automatic (system)", () => SetLanguage("auto"), isChecked: () => _settings.Language == "auto", radio: true),
                    new Cmd("_English", () => SetLanguage("en"), isChecked: () => _settings.Language == "en", radio: true),
                    new Cmd("_Magyar", () => SetLanguage("hu"), isChecked: () => _settings.Language == "hu", radio: true)));

            var layer = new Cmd("_Layer").Add(
                new Cmd("_Add layer(s)…", () => _ = AddLayerDialogAsync()),
                null,
                new Cmd("_Zoom to active layer", () => _view.ZoomToFit()),
                new Cmd("_Next raster layer", CycleActiveLayer),
                null,
                new Cmd("Parameters / _recompute…", () => { if (_view.ActiveLayer != null) _ = EditRecipeAsync(_view.ActiveLayer); }),
                new Cmd("_Save active layer…", () => _ = SaveActiveLayerAsync()),
                new Cmd("Re_move active layer", () => { if (_view.ActiveLayer is { IsFrame: false } a) _view.RemoveLayer(a); }),
                null,
                new Cmd("_Palette").Add(
                    new Cmd("_Edit current palette…", () => OpenPaletteEditor(CurrentPalette())),
                    new Cmd("_New palette…", () => OpenPaletteEditor(null))));

            // Raster = operations that produce a modified copy of the data (processing).
            var raster = new Cmd("_Raster").Add(
                new Cmd("_Band math…", () => _ = BandMathAsync()),
                new Cmd("Fill _no-data gaps…", () => _ = FillNoDataAsync()),
                new Cmd("_Bézier-patch subdivision…", () => _ = BezierSubdivisionAsync(), Ctrl(Key.B)),
                new Cmd("_Filter (convolution)…", () => _ = FilterAsync()),
                null,
                new Cmd("Clip by _extent (E/N)…", () => _ = ClipByExtentAsync()),
                new Cmd("_Mosaic rasters…", () => _ = MosaicAsync()));

            // Analysis = operations that measure or derive new information from the data.
            var analysis = new Cmd("_Analysis").Add(
                new Cmd("_Statistics && histogram…", () => _ = ShowStatisticsAsync()),
                new Cmd("_Zonal statistics by polygon layer…", () => _ = ZonalByLayerAsync(null)),
                new Cmd("_Compare / ΔZ && volume…", () => _ = CompareRastersAsync()),
                null,
                new Cmd("_Terrain").Add(
                    new Cmd("_Slope", () => _ = ComputeTerrainAsync(TerrainProduct.Slope)),
                    new Cmd("_Aspect", () => _ = ComputeTerrainAsync(TerrainProduct.Aspect)),
                    new Cmd("_Hillshade", () => _ = ComputeTerrainAsync(TerrainProduct.Hillshade)),
                    new Cmd("S_wiss-style relief", () => _ = ComputeSwissReliefAsync()),
                    new Cmd("_Curvature…", () => _ = ComputeCurvatureAsync()),
                    null,
                    new Cmd("_Viewshed…", () => _ = ComputeViewshedAsync())),
                new Cmd("_Hydrology").Add(
                    new Cmd("_Flow direction (D8)", () => _ = ComputeFlowDirectionAsync()),
                    new Cmd("Flow acc_umulation", () => _ = ComputeFlowAccumulationAsync()),
                    null,
                    new Cmd("_Stream network…", () => _ = GenerateStreamNetworkAsync())),
                new Cmd("Generate _contours…", () => _ = GenerateContoursAsync()));

            var tools = new Cmd("T_ools").Add(
                new Cmd("_Identify (click the map)", () => SetIdentifyToolActive(!_view.IdentifyMode), isChecked: () => _view.IdentifyMode),
                new Cmd("_Profile (multi-point path)", () => SetPathTool(_view.PathToolMode == PathTool.Profile ? PathTool.None : PathTool.Profile), isChecked: () => _view.PathToolMode == PathTool.Profile),
                new Cmd("_Measure distance / area", () => SetPathTool(_view.PathToolMode == PathTool.Measure ? PathTool.None : PathTool.Measure), isChecked: () => _view.PathToolMode == PathTool.Measure),
                new Cmd("_Zone (zonal statistics)", () => SetPathTool(_view.PathToolMode == PathTool.Zone ? PathTool.None : PathTool.Zone), isChecked: () => _view.PathToolMode == PathTool.Zone),
                new Cmd("_Clip tool (drag a rectangle)", () => SetClipToolActive(!_view.SelectionMode), isChecked: () => _view.SelectionMode));

            var help = new Cmd("_Help").Add(new Cmd("_Keyboard shortcuts…", () => _ = ShowShortcutsAsync()));
            if (!mac) help.Add(null, new Cmd("_About…", () => _ = ShowAboutAsync()));

            var menus = new List<Cmd> { file, edit, view, layer, raster, analysis, tools };
            if (mac)
            {
                menus.Add(new Cmd("_Window").Add(
                    new Cmd("_Minimize", () => WindowState = WindowState.Minimized, Ctrl(Key.M)),
                    new Cmd("Zoom _window", ToggleMaximized)));
            }
            menus.Add(help);
            return menus;
        }

        private void ToggleMaximized() =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        /// <summary>Rebuilds both menus (after a language change or a bookmark edit).</summary>
        private void RebuildMenus()
        {
            _menuChecks.Clear();
            _nativeMenuChecks.Clear();
            if (OperatingSystem.IsMacOS())
            {
                NativeMenu.SetMenu(this, BuildNativeMenu());
            }
            else if (Content is DockPanel root && _menuHost != null)
            {
                int index = root.Children.IndexOf(_menuHost);
                var menu = BuildMenu();
                DockPanel.SetDock(menu, Dock.Top);
                root.Children[index] = menu;
                _menuHost = menu;
            }
            RebuildRecentMenu();
        }

        private void Undo()
        {
            if (!_view.CanUndo) { Flash(T("Nothing to undo.")); return; }
            string? label = _view.UndoLabel;
            _view.Undo();
            Flash(L.F("Undone: {0}", T(label ?? "")));
        }

        private void Redo()
        {
            if (!_view.CanRedo) { Flash(T("Nothing to redo.")); return; }
            string? label = _view.RedoLabel;
            _view.Redo();
            Flash(L.F("Redone: {0}", T(label ?? "")));
        }

        private async void SetLanguage(string code)
        {
            _settings.Language = code;
            _settings.Save();
            L.SetLanguage(code);
            RebuildMenus();
            await MessageAsync(T("Language"), T("The menus switch immediately; restart RasterField to switch every panel and dialog."));
        }

        /// <summary>The horizontal in-window menu strip (Windows/Linux).</summary>
        private Menu BuildMenu()
        {
            var menu = new Menu();
            foreach (var top in BuildMenuModel()) menu.Items.Add(ToMenuItem(top));
            return menu;
        }

        private MenuItem ToMenuItem(Cmd cmd)
        {
            var mi = new MenuItem { Header = T(cmd.Header) };
            if (cmd.Header == "Open _recent") _recentMenu = mi;
            if (cmd.Gesture != null) mi.InputGesture = cmd.Gesture;
            if (cmd.IsChecked != null)
            {
                mi.ToggleType = cmd.Radio ? MenuItemToggleType.Radio : MenuItemToggleType.CheckBox;
                mi.IsChecked = cmd.IsChecked();
                _menuChecks.Add((mi, cmd.IsChecked));
            }
            if (cmd.Run != null)
            {
                var run = cmd.Run;
                // Toggle items flip their own IsChecked on click; re-sync from the real state after running.
                mi.Click += (_, _) => { run(); RefreshMenuChecks(); };
            }
            foreach (var child in cmd.Children)
                mi.Items.Add(child == null ? new Separator() : ToMenuItem(child));
            return mi;
        }

        /// <summary>
        /// The same menu model, built for macOS's system menu bar — shortcuts use Cmd
        /// (<see cref="KeyModifiers.Meta"/>), the platform convention, rather than Ctrl.
        /// </summary>
        private NativeMenu BuildNativeMenu()
        {
            var root = new NativeMenu();
            foreach (var top in BuildMenuModel()) root.Items.Add(ToNativeMenuItem(top));
            return root;
        }

        private NativeMenuItem ToNativeMenuItem(Cmd cmd)
        {
            string header = T(cmd.Header).Replace("_", string.Empty, StringComparison.Ordinal).Replace("&&", "&", StringComparison.Ordinal);
            var mi = new NativeMenuItem(header);
            if (cmd.Gesture != null)
            {
                var mods = cmd.Gesture.KeyModifiers;
                if (mods.HasFlag(KeyModifiers.Control)) mods = (mods & ~KeyModifiers.Control) | KeyModifiers.Meta;
                mi.Gesture = new KeyGesture(cmd.Gesture.Key, mods);
            }
            if (cmd.IsChecked != null)
            {
                mi.ToggleType = cmd.Radio ? NativeMenuItemToggleType.Radio : NativeMenuItemToggleType.CheckBox;
                mi.IsChecked = cmd.IsChecked();
                _nativeMenuChecks.Add((mi, cmd.IsChecked));
            }
            if (cmd.Run != null)
            {
                var run = cmd.Run;
                mi.Click += (_, _) => { run(); RefreshMenuChecks(); };
            }
            if (cmd.Header == "Open _recent")
            {
                mi.Menu = new NativeMenu();
                _nativeRecentMenu = mi.Menu;
            }
            else if (cmd.Children.Count > 0)
            {
                mi.Menu = new NativeMenu();
                foreach (var child in cmd.Children)
                    mi.Menu.Items.Add(child == null ? new NativeMenuItemSeparator() : ToNativeMenuItem(child));
            }
            return mi;
        }

        /// <summary>Re-reads every toggle/radio menu item's state from the view.</summary>
        private void RefreshMenuChecks()
        {
            foreach (var (item, isChecked) in _menuChecks) item.IsChecked = isChecked();
            foreach (var (item, isChecked) in _nativeMenuChecks) item.IsChecked = isChecked();
        }

        private void SetDisplayResampling(DisplayResampling mode)
        {
            _view.DisplayResampling = mode;
            if (mode == DisplayResampling.Bezier)
                Flash(T("Bézier display smoothing on — zoom in past 1.5× to see the smooth surface (display only; data unchanged)."));
            RefreshMenuChecks();
        }

        // ---- single-key tool shortcuts ---------------------------------------------------

        /// <summary>Plain-letter shortcuts (no modifier), ignored while typing into a text field.</summary>
        private bool HandleToolKey(KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.None) return false;
            if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox) return false;
            bool pathActive = _view.PathToolMode != PathTool.None;
            switch (e.Key)
            {
                case Key.I: SetIdentifyToolActive(!_view.IdentifyMode); break;
                case Key.P: SetPathTool(_view.PathToolMode == PathTool.Profile ? PathTool.None : PathTool.Profile); break;
                case Key.M: SetPathTool(_view.PathToolMode == PathTool.Measure ? PathTool.None : PathTool.Measure); break;
                case Key.Z: SetPathTool(_view.PathToolMode == PathTool.Zone ? PathTool.None : PathTool.Zone); break;
                case Key.C: SetClipToolActive(!_view.SelectionMode); break;
                case Key.G: _view.ShowGrid = !_view.ShowGrid; _view.InvalidateVisual(); break;
                case Key.B: SetDisplayResampling(_view.DisplayResampling == DisplayResampling.Bezier ? DisplayResampling.Nearest : DisplayResampling.Bezier); break;
                case Key.Tab when ReferenceEquals(FocusManager?.GetFocusedElement(), _view): CycleActiveLayer(); break;
                case Key.Back when pathActive: _view.RemoveLastPathVertex(); break;
                case Key.Enter when pathActive: _view.FinishPath(); break;
                case Key.Escape when pathActive && _view.CurrentPath.Count > 0: _view.ClearPath(); break;
                case Key.Escape when pathActive: SetPathTool(PathTool.None); break;
                case Key.Escape when _view.IdentifyMode: SetIdentifyToolActive(false); break;
                default: return false;
            }
            RefreshMenuChecks();
            return true;
        }

        /// <summary>Runs the menu command whose shortcut matches (menu gestures are display-only in Avalonia).</summary>
        private bool HandleMenuGesture(KeyEventArgs e)
        {
            if (e.KeyModifiers == KeyModifiers.None && e.Key is not (Key.F9 or Key.F10 or Key.F11)) return false;
            if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.Z or Key.Y && FocusManager?.GetFocusedElement() is TextBox) return false;
            foreach (var cmd in Flatten(BuildMenuModel()))
            {
                var g = cmd.Gesture;
                if (g == null || cmd.Run == null) continue;
                bool match = g.Key == e.Key && g.KeyModifiers == e.KeyModifiers
                    || (e.Key == Key.Add && g.Key == Key.OemPlus && g.KeyModifiers == e.KeyModifiers)
                    || (e.Key == Key.Subtract && g.Key == Key.OemMinus && g.KeyModifiers == e.KeyModifiers)
                    || (OperatingSystem.IsMacOS() && g.KeyModifiers.HasFlag(KeyModifiers.Control) && g.Key == e.Key
                        && e.KeyModifiers == ((g.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta));
                if (!match) continue;
                cmd.Run();
                RefreshMenuChecks();
                return true;
            }
            return false;
        }

        private static IEnumerable<Cmd> Flatten(IEnumerable<Cmd?> cmds)
        {
            foreach (var c in cmds)
            {
                if (c == null) continue;
                yield return c;
                foreach (var child in Flatten(c.Children)) yield return child;
            }
        }

        private void CycleActiveLayer()
        {
            var real = _view.Layers.Where(l => !l.IsFrame).ToList();
            if (real.Count < 2) return;
            int i = real.IndexOf(_view.ActiveLayer!);
            _view.SetActiveLayer(real[(i + 1) % real.Count]);
        }

        private async Task ShowShortcutsAsync() => await MessageAsync(T("Keyboard shortcuts"), T(
            "Ctrl+S  save project · Ctrl+O  open · Ctrl+Shift+O  add layer · Ctrl+Shift+S  save dataset as\n" +
            "Ctrl+Z / Ctrl+Y  undo / redo · Ctrl+K  command palette · Ctrl+B  Bézier subdivision\n" +
            "Ctrl+Shift+D  add bookmark · Ctrl+1…9  go to bookmark · F9 / F10 / F11  panels / map only\n" +
            "Ctrl+0 / F  zoom to fit · Ctrl+± / wheel  zoom · arrows  pan\n" +
            "I  identify · P  profile · M  measure · Z  zone · C  clip · G  cell grid · B  Bézier display smoothing\n" +
            "Path tools: click adds a point, drag moves it, dragging a segment midpoint inserts one, double-click / Enter finishes, Backspace removes the last point, Esc clears\n" +
            "Tab (on the map)  next raster layer"));

    }
}
