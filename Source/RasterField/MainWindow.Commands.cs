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
            public Cmd(string header, Action? run = null, KeyGesture? gesture = null, Func<bool>? isChecked = null, bool radio = false,
                Key? toolKey = null, KeyGesture? macGesture = null)
            {
                Header = header; Run = run; Gesture = gesture; IsChecked = isChecked; Radio = radio; ToolKey = toolKey; MacGesture = macGesture;
            }

            public string Header { get; }
            public Action? Run { get; }

            /// <summary>The shortcut, declared with Ctrl (shown and matched as ⌘ on macOS — see <see cref="Shortcuts"/>).</summary>
            public KeyGesture? Gesture { get; }

            /// <summary>A macOS-specific shortcut used verbatim there, where the platform convention differs (e.g. Redo = ⇧⌘Z).</summary>
            public KeyGesture? MacGesture { get; }

            /// <summary>
            /// A plain-letter tool shortcut (I, P, M…): shown in the menus and the shortcut list,
            /// handled by <see cref="HandleToolKey"/> only outside text fields — never registered as a
            /// macOS menu key equivalent, which would fire while typing.
            /// </summary>
            public Key? ToolKey { get; }

            /// <summary>The shortcut for this platform.</summary>
            public KeyGesture? PlatformGesture => Shortcuts.ForPlatform(Gesture, MacGesture);
            public Func<bool>? IsChecked { get; }
            public bool Radio { get; }
            public List<Cmd?> Children { get; } = new List<Cmd?>(); // null = separator

            public Cmd Add(params Cmd?[] items) { Children.AddRange(items); return this; }
        }

        /// <summary>The File ▸ Open recent submenu, filled separately by <see cref="RebuildRecentMenu"/>.</summary>
        private const string RecentMenuHeader = "Open _recent";

        /// <summary>A menu header as plain text: translated, without access-key underscores.</summary>
        private static string MenuText(string header) =>
            T(header).Replace("_", string.Empty, StringComparison.Ordinal).Replace("&&", "&", StringComparison.Ordinal);

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
                new Cmd(RecentMenuHeader),
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
                new Cmd("_Redo", Redo, Ctrl(Key.Y), macGesture: new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift)),
                null,
                new Cmd("_Command palette…", () => _ = ShowCommandPaletteAsync(), Ctrl(Key.K)));
            // macOS keeps Settings… in the application menu (see App); elsewhere it ends the Edit menu.
            if (!mac) edit.Add(null, new Cmd("_Settings…", () => _ = ShowSettingsAsync(), Ctrl(Key.OemComma)));

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
                new Cmd("Show cell _grid", () => { _view.ShowGrid = !_view.ShowGrid; _view.InvalidateVisual(); RefreshMenuChecks(); }, isChecked: () => _view.ShowGrid, toolKey: Key.G),
                comparison,
                null,
                new Cmd("_Layers && analysis panel", ToggleLeftDock, new KeyGesture(Key.F9), isChecked: () => _leftDockVisible),
                new Cmd("_Properties panel", ToggleRightDock, new KeyGesture(Key.F10), isChecked: () => _rightDockVisible),
                new Cmd("_Map only", ToggleMapOnly, new KeyGesture(Key.F11), isChecked: () => !_leftDockVisible && !_rightDockVisible));
            // macOS: full screen lives in the View menu with the system's ⌃⌘F (theme and language are in Settings…).
            if (mac) view.Add(null, new Cmd("Enter _full screen", ToggleFullScreen, macGesture: new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta),
                isChecked: () => WindowState == WindowState.FullScreen));

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
                new Cmd("Generate _contours…", () => _ = GenerateContoursAsync()),
                new Cmd("Neutral line…", () => SetPathTool(PathTool.NeutralLine)));

            var tools = new Cmd("T_ools").Add(
                new Cmd("_Identify (click the map)", () => SetIdentifyToolActive(!_view.IdentifyMode), isChecked: () => _view.IdentifyMode, toolKey: Key.I),
                new Cmd("_Profile (multi-point path)", () => SetPathTool(_view.PathToolMode == PathTool.Profile ? PathTool.None : PathTool.Profile), isChecked: () => _view.PathToolMode == PathTool.Profile, toolKey: Key.P),
                new Cmd("_Measure distance / area", () => SetPathTool(_view.PathToolMode == PathTool.Measure ? PathTool.None : PathTool.Measure), isChecked: () => _view.PathToolMode == PathTool.Measure, toolKey: Key.M),
                new Cmd("_Zone (zonal statistics)", () => SetPathTool(_view.PathToolMode == PathTool.Zone ? PathTool.None : PathTool.Zone), isChecked: () => _view.PathToolMode == PathTool.Zone, toolKey: Key.Z),
                new Cmd("_Clip tool (drag a rectangle)", () => SetClipToolActive(!_view.SelectionMode), isChecked: () => _view.SelectionMode, toolKey: Key.C));

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

        private void ToggleFullScreen() =>
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

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

        private void SetLanguage(string code)
        {
            _settings.Language = code;
            _settings.Save();
            L.SetLanguage(code);
            RebuildMenus();
            (Application.Current as App)?.BuildApplicationMenu();
        }

        /// <summary>
        /// Settings… (⌘, in the macOS application menu, Ctrl+, in Edit elsewhere): the interface
        /// language and the appearance, both applied at once.
        /// </summary>
        internal async Task ShowSettingsAsync()
        {
            string startLanguage = L.Language;
            RadioButton Choice(string text, string group, bool isChecked, Action apply)
            {
                var radio = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked };
                radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) apply(); };
                return radio;
            }
            var restartHint = Dialogs.Hint(T("Menus switch at once; restart RasterField to switch every panel and dialog."));
            restartHint.IsVisible = false;
            void Language(string code)
            {
                SetLanguage(code);
                restartHint.IsVisible = L.Language != startLanguage;
            }

            var body = Dialogs.Stack(
                AppTheme.SectionLabel(T("Language")),
                Choice(T("Automatic (system)"), "lang", _settings.Language == "auto", () => Language("auto")),
                Choice("English", "lang", _settings.Language == "en", () => Language("en")),
                Choice("Magyar", "lang", _settings.Language == "hu", () => Language("hu")),
                restartHint,
                AppTheme.SectionLabel(T("Appearance")),
                Choice(T("System"), "theme", _settings.Theme == ThemeMode.System, () => SetThemeMode(ThemeMode.System)),
                Choice(T("Light"), "theme", _settings.Theme == ThemeMode.Light, () => SetThemeMode(ThemeMode.Light)),
                Choice(T("Dark"), "theme", _settings.Theme == ThemeMode.Dark, () => SetThemeMode(ThemeMode.Dark)));
            await Dialogs.InfoAsync(this, T("Settings"), body, width: 360);
            RefreshMenuChecks();
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
            if (cmd.Header == RecentMenuHeader) _recentMenu = mi;
            mi.InputGesture = cmd.PlatformGesture ?? (cmd.ToolKey is Key key ? new KeyGesture(key) : null);
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
            var mi = new NativeMenuItem(MenuText(cmd.Header));
            if (cmd.PlatformGesture is { } gesture) mi.Gesture = gesture;
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
            if (cmd.Header == RecentMenuHeader)
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
            if (Flatten(BuildMenuModel()).FirstOrDefault(c => c.ToolKey == e.Key && c.Run != null) is { } tool)
            {
                tool.Run!();
                RefreshMenuChecks();
                return true;
            }
            switch (e.Key)
            {
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
            // A focused text field keeps its own undo/redo.
            if (e.Key is Key.Z or Key.Y && FocusManager?.GetFocusedElement() is TextBox) return false;
            foreach (var cmd in Flatten(BuildMenuModel()))
            {
                if (cmd.PlatformGesture is not { } g || cmd.Run == null || !Shortcuts.Matches(g, e)) continue;
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

        /// <summary>
        /// The shortcut list, generated from the menu model (so it never goes stale and shows the
        /// platform's notation — ⌘ on macOS), plus the map's own keys and mouse gestures.
        /// </summary>
        private async Task ShowShortcutsAsync()
        {
            var rows = new List<(string, Control)>();
            void Section(string title)
            {
                var label = AppTheme.SectionLabel(title);
                label.Tag = "section";
                rows.Add((string.Empty, label));
            }
            void Row(string keys, string action) => rows.Add((keys, new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap }));

            foreach (var top in BuildMenuModel())
            {
                var withKeys = Flatten(top.Children).Where(c => c.Run != null && (c.PlatformGesture != null || c.ToolKey != null)).ToList();
                if (withKeys.Count == 0) continue;
                Section(MenuText(top.Header));
                foreach (var cmd in withKeys)
                    Row(cmd.PlatformGesture is { } g ? Shortcuts.Text(g) : Shortcuts.KeyText(cmd.ToolKey!.Value), MenuText(cmd.Header));
            }
            Section(T("Map"));
            Row("B", T("Bézier display smoothing on / off"));
            Row(Shortcuts.KeyText(Key.Tab), T("Next raster layer (map focused)"));
            Row(T("Drag · wheel · arrows"), T("Pan · zoom · pan"));
            Section(T("Path tools (profile, measure, zone)"));
            Row(T("Click"), T("Add a point"));
            Row(T("Drag"), T("Move a point · drag a segment's midpoint to insert one"));
            Row(T("Double-click") + " / " + Shortcuts.KeyText(Key.Enter), T("Finish"));
            Row(Shortcuts.KeyText(Key.Back), T("Remove the last point"));
            Row(Shortcuts.KeyText(Key.Escape), T("Clear the path, then close the tool"));

            var form = Dialogs.Form(rows.ToArray());
            foreach (var header in form.Children.OfType<TextBlock>().Where(t => Equals(t.Tag, "section")).ToList())
            {
                Grid.SetColumn(header, 0); // section titles span both columns
                Grid.SetColumnSpan(header, 2);
            }
            foreach (var child in form.Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) == 0))
            {
                child.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
                child.Foreground = AppTheme.TextSecondary;
                child.MinWidth = 90;
            }
            await Dialogs.InfoAsync(this, T("Keyboard shortcuts"), new ScrollViewer { Content = form, MaxHeight = 560 }, width: 520);
        }

    }
}
