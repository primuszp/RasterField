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
    /// <summary>
    /// The mini-GIS layer of the main window (see <c>docs/UI-UX-TERV.md</c>): the menu model,
    /// derived in-memory layers, Bézier-patch subdivision, contour / stream-network derivation,
    /// the identify tool and the statistics view.
    /// </summary>
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

        // ---- derived (in-memory) layers ---------------------------------------------------

        /// <summary>
        /// Adds a computed raster as a new, not-yet-saved layer on top of the stack (plan principle
        /// C3: every analysis result is a layer; saving is a separate, optional step).
        /// </summary>
        private RasterLayer AddDerivedRasterLayer(ErsDocument document, string name, string lineage, RasterLayer? inheritDisplayFrom = null)
        {
            var layer = _view.AddLayer(document, name, CurrentPalette());
            if (inheritDisplayFrom != null) _view.CopyDisplaySettings(inheritDisplayFrom, layer);
            _view.MarkDerived(layer, lineage);
            Flash(L.F("{0}: new layer (in memory, not saved yet — Layer ▸ Save active layer, or the ⤓ on its card).", name));
            return layer;
        }

        private VectorLayer AddDerivedVectorLayer(ErvDocument document, string name, string lineage, Color color,
            IReadOnlyList<double>? widthFactors = null, IReadOnlyList<string?>? labels = null, double lineWidth = 1.2)
        {
            var layer = _view.AddVectorLayer(document, name);
            layer.Color = color;
            layer.LineWidth = lineWidth;
            layer.SetObjectStyles(widthFactors, labels);
            _view.MarkDerived(layer, lineage);
            return layer;
        }

        private async Task SaveActiveLayerAsync()
        {
            if (_view.ActiveLayer == null) { await MessageAsync(T("Save layer"), T("There is no active raster layer.")); return; }
            await SaveRasterLayerAsync(_view.ActiveLayer);
        }

        private async Task SaveRasterLayerAsync(RasterLayer layer)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.F("Save raster layer “{0}”", layer.Name),
                DefaultExtension = "ers",
                SuggestedFileName = SafeFileName(layer.Name) + ".ers",
                FileTypeChoices = RasterSaveFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                SetBusy(true, T("Saving…"));
                if (IsGeoTiff(path!)) await Task.Run(() => GeoTiffDataset.Save(layer.Document, path!));
                else await Task.Run(() => layer.Document.Save(path!));
                SetBusy(false);
                _view.MarkSaved(layer, path!);
                _settings.AddRecentFile(Path.GetFullPath(path!));
                _settings.Save();
                RebuildRecentMenu();
                Flash(IsGeoTiff(path!)
                    ? L.F("GeoTIFF written: {0}", Path.GetFileName(path))
                    : L.F("Layer saved: {0} (+ data file)", Path.GetFileName(path)));
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Save failed"), ex.Message); }
        }

        private async Task SaveVectorLayerAsync(VectorLayer layer)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.F("Save vector layer “{0}”", layer.Name),
                DefaultExtension = "erv",
                SuggestedFileName = SafeFileName(layer.Name) + ".erv",
                FileTypeChoices = ErvFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                layer.Document.Save(path!);
                _view.MarkSaved(layer, path!);
                Flash(L.F("Vector layer saved: {0}", Path.GetFileName(path)));
            }
            catch (Exception ex) { await MessageAsync(T("Save failed"), ex.Message); }
        }

        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
                sb.Append(Path.GetInvalidFileNameChars().Contains(ch) || ch == '×' || char.IsWhiteSpace(ch) ? '_' : ch);
            return sb.ToString();
        }

        // ---- raster comparison ----------------------------------------------------------

        private async Task ConfigureVisualComparisonAsync(RasterComparisonMode mode)
        {
            var layers = _view.Layers.Where(l => !l.IsFrame && l.Bitmap != null).ToList();
            if (layers.Count < 2)
            {
                await MessageAsync(T("Visual comparison"), T("Add at least two raster layers first."));
                return;
            }

            var names = layers.Select(l => l.Name).ToList();
            int secondDefault = layers.IndexOf(_view.ActiveLayer!);
            if (secondDefault < 0) secondDefault = layers.Count - 1;
            int firstDefault = secondDefault == 0 ? 1 : 0;
            var firstBox = new ComboBox { ItemsSource = names, SelectedIndex = firstDefault, HorizontalAlignment = HorizontalAlignment.Stretch };
            var secondBox = new ComboBox { ItemsSource = names, SelectedIndex = secondDefault, HorizontalAlignment = HorizontalAlignment.Stretch };
            var value = new NumericUpDown
            {
                Minimum = mode == RasterComparisonMode.Swipe ? 2 : 100,
                Maximum = mode == RasterComparisonMode.Swipe ? 98 : 5000,
                Value = mode == RasterComparisonMode.Swipe ? (decimal)(_view.SwipePosition * 100) : 700,
                Increment = mode == RasterComparisonMode.Swipe ? 1 : 100,
                FormatString = "0",
                Width = 150,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var tcs = new TaskCompletionSource<(int First, int Second, int Value)?>();
            var dialog = new Window
            {
                Title = mode == RasterComparisonMode.Swipe ? T("Swipe comparison") : T("Blink comparison"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var start = new Button { Content = T("Start comparison"), MinWidth = 120 };
            var cancel = new Button { Content = T("Cancel"), MinWidth = 80 };
            start.Click += (_, _) =>
            {
                tcs.TrySetResult((firstBox.SelectedIndex, secondBox.SelectedIndex, (int)(value.Value ?? 0)));
                dialog.Close();
            };
            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = mode == RasterComparisonMode.Swipe
                        ? T("The first layer is shown left of the draggable divider; the second is shown on the right.")
                        : T("The display alternates between the first and second layer."), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
                    new TextBlock { Text = T("First layer") }, firstBox,
                    new TextBlock { Text = T("Second layer") }, secondBox,
                    new TextBlock { Text = mode == RasterComparisonMode.Swipe ? T("Initial divider position (%)") : T("Blink interval (milliseconds)") }, value,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { start, cancel } },
                },
            };
            await dialog.ShowDialog(this);
            var choice = await tcs.Task;
            if (choice == null) return;
            if (choice.Value.First == choice.Value.Second)
            {
                await MessageAsync(T("Visual comparison"), T("Choose two different raster layers."));
                return;
            }

            RasterLayer first = layers[choice.Value.First];
            RasterLayer second = layers[choice.Value.Second];
            _view.SetActiveLayer(first);
            _view.StartComparison(mode, first, second,
                swipePosition: mode == RasterComparisonMode.Swipe ? choice.Value.Value / 100.0 : 0.5,
                blinkIntervalMilliseconds: mode == RasterComparisonMode.Blink ? choice.Value.Value : 700);
            RefreshMenuChecks();
            Flash(mode == RasterComparisonMode.Swipe
                ? T("Swipe active — drag the vertical divider; Comparison ▸ Stop comparison restores the layer stack.")
                : T("Blink active — Comparison ▸ Stop comparison restores the layer stack."));
        }

        private async Task CompareRastersAsync()
        {
            var layers = _view.Layers.Where(l => !l.IsFrame).ToList();
            if (layers.Count < 2)
            {
                await MessageAsync(T("Compare rasters"), T("Add at least two raster layers first."));
                return;
            }

            var names = layers.Select(l => l.Name).ToList();
            int secondDefault = layers.IndexOf(_view.ActiveLayer!);
            if (secondDefault < 0) secondDefault = layers.Count - 1;
            int firstDefault = secondDefault == 0 ? 1 : 0;
            bool zoneAvailable = _view.IsPathFinished && _view.CurrentPath.Count >= 3;

            var firstBox = new ComboBox { ItemsSource = names, SelectedIndex = firstDefault, HorizontalAlignment = HorizontalAlignment.Stretch };
            var secondBox = new ComboBox { ItemsSource = names, SelectedIndex = secondDefault, HorizontalAlignment = HorizontalAlignment.Stretch };
            var thresholdBox = new NumericUpDown
            {
                Value = 0,
                Minimum = 0,
                FormatString = "0.###",
                Increment = 0.1m,
                Width = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var zoneBox = new CheckBox
            {
                Content = T("Use the finished map zone as the analysis mask"),
                IsChecked = zoneAvailable,
                IsEnabled = zoneAvailable,
            };
            var tcs = new TaskCompletionSource<(int First, int Second, double Threshold, bool Zone)?>();
            var dialog = new Window
            {
                Title = T("Compare rasters — ΔZ and volume"),
                Width = 450,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var run = new Button { Content = T("Compare → new ΔZ layer"), MinWidth = 150 };
            var cancel = new Button { Content = T("Cancel"), MinWidth = 80 };
            var streamingHint = new TextBlock
            {
                Text = T("Large streamed layers produce statistics and volumes without creating an in-memory ΔZ layer."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = AppTheme.TextSecondary,
                IsVisible = false,
            };
            void UpdateComparisonMode()
            {
                bool streaming = firstBox.SelectedIndex >= 0 && secondBox.SelectedIndex >= 0 &&
                    (layers[firstBox.SelectedIndex].Raster == null || layers[secondBox.SelectedIndex].Raster == null);
                run.Content = streaming ? T("Compare → statistics") : T("Compare → new ΔZ layer");
                streamingHint.IsVisible = streaming;
            }
            firstBox.SelectionChanged += (_, _) => UpdateComparisonMode();
            secondBox.SelectionChanged += (_, _) => UpdateComparisonMode();
            UpdateComparisonMode();
            run.Click += (_, _) =>
            {
                tcs.TrySetResult((firstBox.SelectedIndex, secondBox.SelectedIndex,
                    (double)(thresholdBox.Value ?? 0), zoneBox.IsChecked == true));
                dialog.Close();
            };
            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = T("ΔZ is calculated as second raster minus first raster. The grids must match exactly."), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
                    new TextBlock { Text = T("First (baseline)") }, firstBox,
                    new TextBlock { Text = T("Second (newer)") }, secondBox,
                    new TextBlock { Text = T("Absolute change threshold") }, thresholdBox,
                    zoneBox,
                    streamingHint,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { run, cancel } },
                },
            };
            await dialog.ShowDialog(this);
            var choice = await tcs.Task;
            if (choice == null) return;
            if (choice.Value.First == choice.Value.Second)
            {
                await MessageAsync(T("Compare rasters"), T("Choose two different raster layers."));
                return;
            }

            RasterLayer first = layers[choice.Value.First];
            RasterLayer second = layers[choice.Value.Second];
            RasterGridCompatibility compatibility = RasterGridCompatibility.Check(first.Document, second.Document);
            if (!compatibility.IsCompatible)
            {
                await MessageAsync(T("Rasters are not aligned"), compatibility.Message);
                return;
            }

            IReadOnlyList<(double X, double Y)>? polygon = choice.Value.Zone
                ? _view.CurrentPath.ToList()
                : null;
            using var cancellation = new CancellationTokenSource();
            try
            {
                SetBusy(true, T("Computing ΔZ and volumes…"), cancellation);
                RasterChangeResult? materialized = null;
                RasterChangeSummary result;
                if (first.Raster != null && second.Raster != null)
                {
                    materialized = await Task.Run(() => RasterChangeAnalysis.Compute(
                        first.Raster, second.Raster, first.Document.GeoReference,
                        choice.Value.Threshold, polygon, cancellation.Token), cancellation.Token);
                    result = materialized;
                }
                else
                {
                    result = await Task.Run(() => ComputeStreamingChangeSummary(
                        first, second, choice.Value.Threshold, polygon, cancellation.Token), cancellation.Token);
                }
                SetBusy(false);

                string unit = first.Document.Header.CoordinateSpace.EffectiveUnits;
                if (string.IsNullOrWhiteSpace(unit)) unit = T("map unit");
                string scope = polygon == null ? T("Entire aligned grid") : T("Finished map zone");
                if (materialized != null)
                {
                    string layerName = $"{second.Name} − {first.Name} (ΔZ)";
                    string lineage = string.Format(CultureInfo.CurrentCulture,
                        "ΔZ: {0} − {1} · {2} · |ΔZ| > {3:g6}: {4:N2} {9}² ({5:N0} cells) · cut {6:N2} {9}³ · fill {7:N2} {9}³ · net {8:N2} {9}³",
                        second.Name, first.Name, scope, choice.Value.Threshold, result.ThresholdArea,
                        result.ThresholdCellCount, result.CutVolume, result.FillVolume, result.NetVolume, unit);
                    AddDerivedRasterLayer(BuildDerivedDocument(materialized.Difference, first.Document), layerName, lineage);
                    _view.SetPalette(BuiltInPalettes.BlueWhiteRed);
                    double extent = Math.Max(Math.Abs(result.Minimum), Math.Abs(result.Maximum));
                    if (extent > 0 && !double.IsNaN(extent)) _view.SetValueRange(-extent, extent);
                }

                string streamedNote = materialized == null
                    ? T("The large rasters were analysed tile by tile; no full in-memory ΔZ layer was created.") + "\n\n"
                    : string.Empty;
                await MessageAsync(T("Raster comparison complete"), streamedNote + string.Format(CultureInfo.CurrentCulture,
                    "{0}\n\nΔZ min / max: {1:N3} / {2:N3} {9}\nMean: {3:N3} {9}   σ: {4:N3} {9}\n|ΔZ| > {5:N3}: {6:N2} {9}² ({7:N0} cells)\n\nCut: {8:N2} {9}³\nFill: {10:N2} {9}³\nNet (fill − cut): {11:N2} {9}³",
                    scope, result.Minimum, result.Maximum, result.Mean, result.StandardDeviation,
                    choice.Value.Threshold, result.ThresholdArea, result.ThresholdCellCount,
                    result.CutVolume, unit, result.FillVolume, result.NetVolume));
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                Flash(T("Raster comparison cancelled."));
            }
            catch (Exception ex)
            {
                SetBusy(false);
                await MessageAsync(T("Raster comparison failed"), ex.Message);
            }
        }

        private static RasterChangeSummary ComputeStreamingChangeSummary(
            RasterLayer first, RasterLayer second, double threshold,
            IReadOnlyList<(double X, double Y)>? polygon,
            CancellationToken cancellationToken)
        {
            IRasterSource? ownedFirst = null, ownedSecond = null;
            try
            {
                IRasterSource firstSource;
                if (first.Raster != null)
                    firstSource = ownedFirst = new MemoryRasterSource(first.Document.Bands);
                else if (first.SourceFactory != null)
                    firstSource = ownedFirst = first.SourceFactory();
                else
                    firstSource = first.Source!;

                IRasterSource secondSource;
                if (second.Raster != null)
                    secondSource = ownedSecond = new MemoryRasterSource(second.Document.Bands);
                else if (second.SourceFactory != null)
                    secondSource = ownedSecond = second.SourceFactory();
                else
                    secondSource = second.Source!;

                return RasterChangeAnalysis.ComputeSummary(firstSource, secondSource,
                    first.Document.GeoReference, threshold, polygon, first.ActiveBand, second.ActiveBand,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                ownedFirst?.Dispose();
                ownedSecond?.Dispose();
            }
        }

        // ---- Bézier-patch subdivision ----------------------------------------------------

        private sealed record BezierChoice(int Factor, double Tension, bool Monotone, BezierNoDataMode NoData, bool ViewOnly);

        private async Task BezierSubdivisionAsync()
        {
            var layer = _view.ActiveLayer;
            if (layer == null || layer.IsFrame) { await MessageAsync(T("Bézier subdivision"), T("Open a raster dataset first.")); return; }
            var recipe = await AskBezierRecipeAsync(layer, null);
            if (recipe != null) await CreateDerivedAsync(recipe, L.F("Bézier ×{0} subdivision…", recipe.Get("factor", 4)));
        }

        private async Task<BezierChoice?> ShowBezierDialogAsync(RasterLayer layer, BezierChoice? initial = null)
        {
            var tcs = new TaskCompletionSource<BezierChoice?>();
            int w = layer.DatasetWidth, h = layer.DatasetHeight;
            double cellX = layer.Document.Header.RasterInfo.CellSizeX, cellY = layer.Document.Header.RasterInfo.CellSizeY;
            string unit = layer.Document.Header.CoordinateSpace.EffectiveUnits;
            var viewWindow = _view.VisibleCellWindow();

            var factorBox = new ComboBox { ItemsSource = new[] { T("×2"), T("×3"), T("×4"), T("×8") }, SelectedIndex = 2, Width = 90 };
            var customFactor = new NumericUpDown { Minimum = 1, Maximum = 32, Value = 4, Increment = 1, Width = 110, FormatString = "0" };
            var customCheck = new CheckBox { Content = T("custom") };
            var areaFull = new RadioButton { Content = T("Whole layer"), IsChecked = true, GroupName = "bzArea" };
            var areaView = new RadioButton { Content = T("Current view only"), GroupName = "bzArea", IsEnabled = viewWindow != null };
            var tension = new Slider { Minimum = 0, Maximum = 1, Value = 1, Width = 200, TickFrequency = 0.05, IsSnapToTickEnabled = true };
            var tensionText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 40 };
            var monotone = new CheckBox { Content = T("Monotone — no overshoot (for sharp edges: embankments, quarry walls)") };
            var strict = new CheckBox { Content = T("Leave no-data where the 4×4 neighbourhood is incomplete (instead of bilinear fallback)") };
            var sizeText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var warnText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Danger };
            var okBtn = new Button { Content = T("Apply → new layer"), MinWidth = 120 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };

            var originalImage = new Image { Width = 200, Height = 200, Stretch = Stretch.Fill };
            var bezierImage = new Image { Width = 200, Height = 200, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapInterpolationMode(originalImage, BitmapInterpolationMode.None);
            RenderOptions.SetBitmapInterpolationMode(bezierImage, BitmapInterpolationMode.None);

            // Preview: a 16×16-cell patch at the centre of the view, rendered with the layer's own colours.
            Raster? previewSource = null;
            if (layer.Raster != null && !layer.ShowRgbComposite && layer.Colorizer != null)
            {
                var centre = viewWindow ?? new PixelRect(0, 0, w, h);
                int size = Math.Min(16, Math.Min(w, h));
                int px = Math.Clamp(centre.X + centre.Width / 2 - size / 2, 0, Math.Max(0, w - size));
                int py = Math.Clamp(centre.Y + centre.Height / 2 - size / 2, 0, Math.Max(0, h - size));
                previewSource = RasterClipper.Crop(layer.Raster, px, py, size, size);
                originalImage.Source = RasterView.ToBitmap(RasterImageRenderer.Render(previewSource, layer.Colorizer));
            }

            int Factor() => customCheck.IsChecked == true ? (int)(customFactor.Value ?? 4) : factorBox.SelectedIndex switch { 0 => 2, 1 => 3, 3 => 8, _ => 4 };

            void Update()
            {
                int k = Factor();
                tensionText.Text = tension.Value.ToString("0.00", CultureInfo.InvariantCulture);
                bool view = areaView.IsChecked == true && viewWindow != null;
                int sw = view ? viewWindow!.Value.Width : w, sh = view ? viewWindow!.Value.Height : h;
                long cells = (long)sw * k * sh * k;
                int bands = Math.Max(1, layer.BandCount);
                double mb = cells * 4.0 * bands / (1024 * 1024);
                sizeText.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0:0.###} × {1:0.###} {2} cells → {3:0.###} × {4:0.###} {2}  ·  {5}×{6} → {7:N0}×{8:N0}  ·  ≈ {9:0.#} MB in memory ({10} band{11})",
                    cellX, cellY, unit, cellX / k, cellY / k, sw, sh, (long)sw * k, (long)sh * k, mb, bands, bands == 1 ? "" : "s");

                bool tooBig = cells > int.MaxValue || mb > 2048;
                warnText.Text = tooBig
                    ? T("Too large for one in-memory layer — choose a smaller factor or “Current view only” (or clip first).")
                    : layer.IsStreaming && !view ? T("Large streaming dataset: the whole raster has to be read. Consider “Current view only”.") : "";
                okBtn.IsEnabled = !tooBig;

                if (previewSource != null && layer.Colorizer != null)
                {
                    var fine = BezierPatchInterpolator.Subdivide(previewSource, new BezierPatchOptions
                    {
                        Factor = Math.Min(k, 8), Tension = tension.Value, Monotone = monotone.IsChecked == true,
                        NoData = strict.IsChecked == true ? BezierNoDataMode.NoData : BezierNoDataMode.FallbackBilinear,
                    });
                    bezierImage.Source = RasterView.ToBitmap(RasterImageRenderer.Render(fine, layer.Colorizer));
                }
            }

            factorBox.SelectionChanged += (_, _) => Update();
            customFactor.ValueChanged += (_, _) => Update();
            customCheck.IsCheckedChanged += (_, _) => { factorBox.IsEnabled = customCheck.IsChecked != true; Update(); };
            areaView.IsCheckedChanged += (_, _) => Update();
            tension.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Update(); };
            monotone.IsCheckedChanged += (_, _) => Update();
            strict.IsCheckedChanged += (_, _) => Update();

            var dialog = new Window
            {
                Title = T("Bézier-patch subdivision"),
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            okBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(new BezierChoice(Factor(), tension.Value, monotone.IsChecked == true,
                    strict.IsChecked == true ? BezierNoDataMode.NoData : BezierNoDataMode.FallbackBilinear,
                    areaView.IsChecked == true && viewWindow != null));
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            TextBlock H(string t) => AppTheme.SectionLabel(t);
            StackPanel Row(params Control[] c) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var x in c) p.Children.Add(x); return p; }
            StackPanel Captioned(string caption, Control c) { var p = new StackPanel { Spacing = 2 }; p.Children.Add(new TextBlock { Text = caption, Opacity = 0.8, FontSize = 11 }); p.Children.Add(c); return p; }

            var advanced = new Expander
            {
                Header = T("Advanced"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        Row(new TextBlock { Text = T("Tension τ"), VerticalAlignment = VerticalAlignment.Center, Width = 70 }, tension, tensionText),
                        new TextBlock { Text = T("1 = Catmull-Rom tangents (smooth, C¹) · 0 = exactly bilinear"), Opacity = 0.7, FontSize = 11 },
                        monotone,
                        strict,
                    },
                },
            };

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = L.F("Source: {0}  ({1}×{2}, {3} band(s))", layer.Name, w, h, Math.Max(1, layer.BandCount)), TextWrapping = TextWrapping.Wrap },
                    H("Subdivision"),
                    Row(factorBox, customCheck, customFactor),
                    sizeText,
                    H("Area"),
                    Row(areaFull, areaView),
                    advanced,
                    H(T("Preview (centre of the view, 16×16 cells)")),
                    previewSource == null
                        ? new TextBlock { Text = T("No preview for streaming or RGB layers."), Opacity = 0.7 }
                        : Row(Captioned("original", originalImage), Captioned("Bézier", bezierImage)),
                    new TextBlock { Text = T("ⓘ Interpolation smooths the surface — it does not add measured information."), Opacity = 0.75, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    warnText,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { okBtn, cancelBtn } },
                },
            };
            if (initial != null)
            {
                int idx = initial.Factor switch { 2 => 0, 3 => 1, 4 => 2, 8 => 3, _ => -1 };
                if (idx >= 0) factorBox.SelectedIndex = idx;
                else { customCheck.IsChecked = true; customFactor.Value = initial.Factor; }
                tension.Value = initial.Tension;
                monotone.IsChecked = initial.Monotone;
                strict.IsChecked = initial.NoData == BezierNoDataMode.NoData;
                if (initial.ViewOnly) { areaView.IsEnabled = true; areaView.IsChecked = true; }
                okBtn.Content = T("Recompute");
            }
            Update();
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async Task ShowBezierDisplaySettingsAsync()
        {
            var o = _view.DisplayBezierOptions;
            var tension = new Slider { Minimum = 0, Maximum = 1, Value = o.Tension, Width = 220, TickFrequency = 0.05, IsSnapToTickEnabled = true };
            var monotone = new CheckBox { Content = T("Monotone (no overshoot)"), IsChecked = o.Monotone };
            var enable = new CheckBox { Content = T("Use Bézier display smoothing now"), IsChecked = _view.DisplayResampling == DisplayResampling.Bezier };

            var dialog = new Window
            {
                Title = T("Bézier display smoothing"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var ok = new Button { Content = T("OK"), MinWidth = 80 };
            ok.Click += (_, _) =>
            {
                o.Tension = tension.Value;
                o.Monotone = monotone.IsChecked == true;
                SetDisplayResampling(enable.IsChecked == true ? DisplayResampling.Bezier : (_view.DisplayResampling == DisplayResampling.Bezier ? DisplayResampling.Nearest : _view.DisplayResampling));
                if (_view.ActiveLayer != null) _view.ActiveLayer.SmoothKey = null; // force a rebuild with the new options
                _view.ZoomBy(1.0);
                dialog.Close();
            };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = T("When magnified, the active layer's visible window is drawn as a bicubic Bézier-patch surface. Display only — the data is not changed."), TextWrapping = TextWrapping.Wrap },
                    enable,
                    new TextBlock { Text = T("Tension τ (1 = smooth Catmull-Rom, 0 = bilinear)") },
                    tension,
                    monotone,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
                },
            };
            await dialog.ShowDialog(this);
        }

        // ---- contours ------------------------------------------------------------------

        private sealed record ContourChoice(ContourOptions Options, int SourceFactor);

        private async Task GenerateContoursAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Generate contours"));
            if (loaded == null) return;
            var recipe = await AskContourRecipeAsync(_view.ActiveLayer!, null);
            if (recipe != null)
                await CreateDerivedAsync(recipe, recipe.Get("factor", 1) > 1 ? L.F("Bézier ×{0} surface + contours…", recipe.Get("factor", 1)) : T("Tracing contours…"));
        }

        private async Task<ContourChoice?> ShowContourDialogAsync(double dataMin, double dataMax, ContourChoice? initial = null)
        {
            var tcs = new TaskCompletionSource<ContourChoice?>();

            double niceInterval = NiceInterval((dataMax - dataMin) / 12.0);
            NumericUpDown Num(double v, double min = double.MinValue) => new NumericUpDown
            {
                Value = (decimal)v, FormatString = "0.###", Increment = (decimal)Math.Max(niceInterval, 1e-6),
                Minimum = min == double.MinValue ? decimal.MinValue : (decimal)min, Width = 130, HorizontalAlignment = HorizontalAlignment.Left,
            };
            var minBox = Num(Math.Floor(dataMin / niceInterval) * niceInterval);
            var maxBox = Num(Math.Ceiling(dataMax / niceInterval) * niceInterval);
            var intervalBox = Num(niceInterval, 1e-6);
            var indexBox = new NumericUpDown { Value = 5, Minimum = 0, Maximum = 100, Increment = 1, FormatString = "0", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var sourceBox = new ComboBox { ItemsSource = new[] { T("Original grid"), T("Bézier ×2 surface"), T("Bézier ×4 surface") }, SelectedIndex = 0, Width = 180 };
            var smoothBox = new ComboBox { ItemsSource = new[] { T("None"), T("Chaikin 1×"), T("Chaikin 2×"), T("Chaikin 3×") }, SelectedIndex = 0, Width = 180 };
            var minLenBox = new NumericUpDown { Value = 0, Minimum = 0, Increment = 10, FormatString = "0.##", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            var countText = new TextBlock { Opacity = 0.8 };

            void UpdateCount()
            {
                double i = (double)(intervalBox.Value ?? 0), lo = (double)(minBox.Value ?? 0), hi = (double)(maxBox.Value ?? 0);
                int n = ContourGenerator.BuildLevels(lo, hi, i).Count;
                countText.Text = n > 0 ? $"{n} level(s)" : "no levels in this range";
            }
            minBox.ValueChanged += (_, _) => UpdateCount();
            maxBox.ValueChanged += (_, _) => UpdateCount();
            intervalBox.ValueChanged += (_, _) => UpdateCount();

            var dialog = new Window
            {
                Title = T("Generate contours"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var okBtn = new Button { Content = T("Apply → new layer"), MinWidth = 120 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };
            okBtn.Click += (_, _) =>
            {
                var options = new ContourOptions
                {
                    Minimum = (double)(minBox.Value ?? 0),
                    Maximum = (double)(maxBox.Value ?? 0),
                    Interval = (double)(intervalBox.Value ?? (decimal)niceInterval),
                    IndexEvery = (int)(indexBox.Value ?? 0),
                    SmoothingIterations = smoothBox.SelectedIndex,
                    MinimumLength = (double)(minLenBox.Value ?? 0),
                };
                if (!(options.Interval > 0) || ContourGenerator.BuildLevels(options.Minimum, options.Maximum, options.Interval).Count == 0)
                {
                    countText.Text = T("⚠ no levels fall within this range/interval");
                    return;
                }
                tcs.TrySetResult(new ContourChoice(options, sourceBox.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 }));
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            Grid Form(params (string Label, Control Field)[] rows)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 6 };
                for (int r = 0; r < rows.Length; r++)
                {
                    g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var label = new TextBlock { Text = rows[r].Label, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetRow(label, r); Grid.SetRow(rows[r].Field, r); Grid.SetColumn(rows[r].Field, 1);
                    g.Children.Add(label); g.Children.Add(rows[r].Field);
                }
                return g;
            }

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = L.F("Data range: {0:g6} … {1:g6}", dataMin, dataMax) },
                    Form((T("Minimum level"), minBox), (T("Maximum level"), maxBox), (T("Interval"), intervalBox), (T("Index contour every"), indexBox)),
                    countText,
                    new Expander
                    {
                        Header = T("Advanced"),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = Form((T("Source surface"), sourceBox), (T("Line smoothing"), smoothBox), (T("Min. line length"), minLenBox)),
                    },
                    new TextBlock { Text = T("Tip: a Bézier surface gives stair-free contours on coarse grids."), Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { okBtn, cancelBtn } },
                },
            };
            if (initial != null)
            {
                var o0 = initial.Options;
                minBox.Value = (decimal)o0.Minimum; maxBox.Value = (decimal)o0.Maximum; intervalBox.Value = (decimal)o0.Interval;
                indexBox.Value = o0.IndexEvery; smoothBox.SelectedIndex = Math.Clamp(o0.SmoothingIterations, 0, 3);
                minLenBox.Value = (decimal)o0.MinimumLength;
                sourceBox.SelectedIndex = initial.SourceFactor switch { 2 => 1, 4 => 2, _ => 0 };
                okBtn.Content = T("Recompute");
            }
            UpdateCount();
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        /// <summary>Rounds a raw step to 1, 2, 2.5 or 5 × 10ⁿ — a contour interval people would pick.</summary>
        private static double NiceInterval(double raw)
        {
            if (!(raw > 0) || double.IsInfinity(raw)) return 1;
            double exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / exp;
            double nice = f < 1.5 ? 1 : f < 2.25 ? 2 : f < 3.5 ? 2.5 : f < 7.5 ? 5 : 10;
            return nice * exp;
        }

        // ---- stream network -------------------------------------------------------------

        private async Task GenerateStreamNetworkAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Stream network"));
            if (loaded == null) return;
            var recipe = await AskStreamRecipeAsync(_view.ActiveLayer!, null);
            if (recipe != null) await CreateDerivedAsync(recipe, T("Flow direction, accumulation and stream network…"));
        }

        private async Task<double?> AskNumberAsync(string title, string explanation, string label, double value, double minimum)
        {
            var tcs = new TaskCompletionSource<double?>();
            var box = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)minimum, FormatString = "0.###", Increment = 10, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            var dialog = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            var ok = new Button { Content = T("Apply → new layer"), MinWidth = 120 };
            var cancel = new Button { Content = T("Cancel"), MinWidth = 80 };
            ok.Click += (_, _) => { tcs.TrySetResult((double)(box.Value ?? (decimal)value)); dialog.Close(); };
            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
                    new TextBlock { Text = label }, box,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        // ---- identify --------------------------------------------------------------------

        private void SetIdentifyToolActive(bool active)
        {
            if (active) { SetClipToolActive(false); SetPathTool(PathTool.None); _analysisTabs.SelectedIndex = 0; }
            _view.IdentifyMode = active;
            Flash(active ? "Identify: click the map to list every visible layer's value there (Esc to stop)." : "");
            RefreshMenuChecks();
        }

        private async void OnIdentifyRequested(object? sender, RasterReadoutEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "E {0:0.###}   N {1:0.###}", e.WorldX, e.WorldY));
            sb.AppendLine();

            foreach (var item in _view.DrawOrder.Reverse())
            {
                switch (item)
                {
                    case RasterLayer layer when layer.IsVisible && !layer.IsFrame:
                        sb.AppendLine(DescribeRasterAt(layer, e.WorldX, e.WorldY));
                        break;
                    case VectorLayer v when v.IsVisible:
                        sb.AppendLine(DescribeVectorNear(v, e.WorldX, e.WorldY));
                        break;
                }
            }
            _identifyText.Text = sb.ToString().TrimEnd();
            _analysisTabs.SelectedIndex = 0;
            if (!_leftDockVisible) { _leftDockVisible = true; ApplyDockVisibility(); }
            await Task.CompletedTask;
        }

        private static string DescribeRasterAt(RasterLayer layer, double wx, double wy)
        {
            var geo = layer.Document.GeoReference;
            string name = "▣ " + layer.Name + (layer.IsUnsaved ? " ●" : "");
            if (!geo.IsInvertible) return name + ": (no georeference)";
            var (col, row) = geo.WorldToPixel(wx, wy);
            int c = (int)Math.Floor(col), r = (int)Math.Floor(row);
            if (c < 0 || r < 0 || c >= layer.DatasetWidth || r >= layer.DatasetHeight) return name + ": outside";

            var parts = new List<string>();
            if (layer.Document.Bands.Count > 0)
            {
                for (int b = 0; b < layer.Document.Bands.Count && b < 8; b++)
                {
                    float? v = layer.Document.Bands[b].GetValueOrNull(r, c);
                    string prefix = layer.Document.Bands.Count > 1 ? $"b{b + 1} " : "";
                    parts.Add(prefix + (v.HasValue ? v.Value.ToString("g6", CultureInfo.InvariantCulture) : "no-data"));
                }
                var band = layer.Document.Bands[Math.Min(layer.ActiveBand, layer.Document.Bands.Count - 1)];
                float? bil = RasterProfiler.BilinearSample(band, col, row);
                float? bez = BezierPatchInterpolator.Sample(band, col, row, new BezierPatchOptions());
                if (bil.HasValue) parts.Add("bilinear " + bil.Value.ToString("g6", CultureInfo.InvariantCulture));
                if (bez.HasValue) parts.Add("Bézier " + bez.Value.ToString("g6", CultureInfo.InvariantCulture) + " (interpolated)");
            }
            else if (layer.Source != null)
            {
                try
                {
                    var single = layer.Source.ReadWindow(c, r, 1, 1, band: layer.ActiveBand);
                    float? v = single.Width == 1 ? single.GetValueOrNull(0, 0) : null;
                    parts.Add(v.HasValue ? v.Value.ToString("g6", CultureInfo.InvariantCulture) : "no-data");
                }
                catch (IOException ex) { parts.Add("read error: " + ex.Message); }
            }
            return $"{name}  [cell {c}, {r}]: " + string.Join(" · ", parts);
        }

        private static string DescribeVectorNear(VectorLayer layer, double wx, double wy)
        {
            // Nearest poly vertex / point within the layer (a quick, dependable proxy for "what's here").
            double best = double.MaxValue;
            string? attribute = null;
            foreach (var obj in layer.Document.Objects)
            {
                IEnumerable<(double X, double Y)> pts = obj switch
                {
                    VectorPoint p => new[] { (p.X, p.Y) },
                    VectorPolyObject poly => poly.Points,
                    VectorTextObject t => new[] { (t.X, t.Y) },
                    _ => Array.Empty<(double, double)>(),
                };
                foreach (var (x, y) in pts)
                {
                    double d = (x - wx) * (x - wx) + (y - wy) * (y - wy);
                    if (d < best) { best = d; attribute = obj.Attribute; }
                }
            }
            string name = "▤ " + layer.Name + (layer.IsUnsaved ? " ●" : "");
            return attribute == null
                ? name + ": (empty)"
                : string.Format(CultureInfo.InvariantCulture, "{0}: nearest “{1}” at {2:0.##}", name, attribute, Math.Sqrt(best));
        }

        // ---- statistics & histogram ------------------------------------------------------

        private async Task ShowStatisticsAsync()
        {
            var layer = _view.ActiveLayer;
            var r = layer?.ActiveRaster;
            if (layer == null || r == null) { await MessageAsync(T("Statistics"), T("Open a dataset first.")); return; }

            var stats = r.Statistics;
            long total = (long)r.Width * r.Height;
            long noData = total - stats.ValidCount;
            long gapsInHull = layer.Raster != null ? NoDataFiller.CountNoDataWithinHull(layer.Raster) : -1;

            const int binCount = 64;
            var bins = new long[binCount];
            double range = stats.Maximum - stats.Minimum;
            foreach (float v in r.Samples)
            {
                if (r.IsNoData(v)) continue;
                int bin = range > 0 ? (int)((v - stats.Minimum) / range * (binCount - 1)) : 0;
                bins[Math.Clamp(bin, 0, binCount - 1)]++;
            }

            var chart = new HistogramControl(bins, layer.Colorizer) { Height = 140, Margin = new Thickness(0, 6) };
            var info = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = string.Format(CultureInfo.InvariantCulture,
                    "{0}{1}\n{2}×{3} cells · {4} band(s) · {5}\n\nmin {6:g6} · max {7:g6}\nmean {8:g6} · σ {9:g6}\nvalid {10:N0} · no-data {11:N0}{12}{13}",
                    layer.Name, layer.IsStreaming ? " (statistics of the streamed window — approximate)" : "",
                    layer.DatasetWidth, layer.DatasetHeight, layer.BandCount, layer.Document.Header.RasterInfo.CellType,
                    stats.Minimum, stats.Maximum, stats.Mean, stats.StandardDeviation, stats.ValidCount, noData,
                    gapsInHull >= 0 ? $" ({gapsInHull:N0} inside the data footprint)" : "",
                    layer.Lineage != null ? "\n\nMade from: " + layer.Lineage : ""),
            };

            var dialog = new Window { Title = T("Statistics & histogram"), Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var close = new Button { Content = T("Close"), MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 6, Children = { info, chart, close } };
            await dialog.ShowDialog(this);
        }

        /// <summary>A small bar histogram, bars tinted with the layer's own palette and the current stretch marked.</summary>
        private sealed class HistogramControl : Control
        {
            private readonly long[] _bins;
            private readonly RasterColorizer? _colorizer;

            public HistogramControl(long[] bins, RasterColorizer? colorizer) { _bins = bins; _colorizer = colorizer; }

            public override void Render(DrawingContext context)
            {
                double w = Bounds.Width, h = Bounds.Height;
                context.FillRectangle(AppTheme.BarBackground, new Rect(0, 0, w, h));
                long max = _bins.Length == 0 ? 0 : _bins.Max();
                if (max == 0) return;
                double bw = w / _bins.Length;
                for (int i = 0; i < _bins.Length; i++)
                {
                    double bh = Math.Log(1 + _bins[i]) / Math.Log(1 + max) * (h - 4);
                    IBrush brush = AppTheme.Accent;
                    if (_colorizer != null)
                    {
                        var c = _colorizer.Palette.Sample((i + 0.5) / _bins.Length);
                        brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                    }
                    context.FillRectangle(brush, new Rect(i * bw, h - bh, Math.Max(1, bw - 1), bh));
                }
                var label = new FormattedText("log scale", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, AppTheme.TextPrimary);
                context.DrawText(label, new Point(w - label.Width - 4, 2));
            }
        }
    }
}
