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
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RasterField.Rasters;
using RasterField.Rendering;
using RasterField.Vectors;

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

        private List<Cmd> BuildMenuModel()
        {
            var file = new Cmd("_File").Add(
                new Cmd("_Open .ers…", () => _ = OpenDialogAsync(), Ctrl(Key.O)),
                new Cmd("_Add layer(s)…", () => _ = AddLayerDialogAsync(), Ctrl(Key.O, shift: true)),
                new Cmd("Open _recent"), // filled by RebuildRecentMenu
                null,
                new Cmd("Save active _layer…", () => _ = SaveActiveLayerAsync()),
                new Cmd("Save _dataset as… (.ers + data)", () => _ = SaveDatasetAsAsync(), Ctrl(Key.S)),
                new Cmd("Save _header as .ers…", () => _ = SaveHeaderAsAsync()),
                new Cmd("_Export view as PNG…", () => _ = ExportPngAsync()),
                null,
                new Cmd("E_xit", Close));

            var magnification = new Cmd("_Magnification").Add(
                new Cmd("_Nearest (crisp cells)", () => SetDisplayResampling(DisplayResampling.Nearest), isChecked: () => _view.DisplayResampling == DisplayResampling.Nearest, radio: true),
                new Cmd("_Bilinear", () => SetDisplayResampling(DisplayResampling.Bilinear), isChecked: () => _view.DisplayResampling == DisplayResampling.Bilinear, radio: true),
                new Cmd("Bé_zier patch (smooth surface)", () => SetDisplayResampling(DisplayResampling.Bezier), isChecked: () => _view.DisplayResampling == DisplayResampling.Bezier, radio: true));

            var view = new Cmd("_View").Add(
                new Cmd("Zoom to _fit", () => _view.ZoomToFit(), Ctrl(Key.D0)),
                new Cmd("Zoom _in", () => _view.ZoomBy(1.25), Ctrl(Key.OemPlus)),
                new Cmd("Zoom _out", () => _view.ZoomBy(0.8), Ctrl(Key.OemMinus)),
                null,
                magnification,
                new Cmd("Show cell _grid", () => { _view.ShowGrid = !_view.ShowGrid; _view.InvalidateVisual(); RefreshMenuChecks(); }, isChecked: () => _view.ShowGrid),
                null,
                new Cmd("_Theme").Add(
                    new Cmd("_System", () => SetThemeMode(ThemeMode.System)),
                    new Cmd("_Light", () => SetThemeMode(ThemeMode.Light)),
                    new Cmd("_Dark", () => SetThemeMode(ThemeMode.Dark))));

            var layer = new Cmd("_Layer").Add(
                new Cmd("_Zoom to active layer", () => _view.ZoomToFit()),
                new Cmd("_Save active layer…", () => _ = SaveActiveLayerAsync()),
                new Cmd("_Remove active layer", () => { if (_view.ActiveLayer != null) _view.RemoveLayer(_view.ActiveLayer); }));

            var raster = new Cmd("_Raster").Add(
                new Cmd("_Statistics && histogram…", () => _ = ShowStatisticsAsync()),
                new Cmd("_Band math…", () => _ = BandMathAsync()),
                new Cmd("Fill _no-data gaps…", () => _ = FillNoDataAsync()),
                null,
                new Cmd("Clip by _extent (E/N)…", () => _ = ClipByExtentAsync()),
                new Cmd("_Mosaic rasters…", () => _ = MosaicAsync()));

            var interpolation = new Cmd("_Interpolation").Add(
                new Cmd("_Bézier-patch subdivision…", () => _ = BezierSubdivisionAsync(), Ctrl(Key.B)),
                new Cmd("Bézier _display settings…", () => _ = ShowBezierDisplaySettingsAsync()),
                null,
                magnification);

            var vector = new Cmd("Vec_tor").Add(
                new Cmd("Generate _contours…", () => _ = GenerateContoursAsync()),
                new Cmd("_Stream network…", () => _ = GenerateStreamNetworkAsync()));

            var terrain = new Cmd("Te_rrain").Add(
                new Cmd("_Slope", () => _ = ComputeTerrainAsync(TerrainProduct.Slope)),
                new Cmd("_Aspect", () => _ = ComputeTerrainAsync(TerrainProduct.Aspect)),
                new Cmd("_Hillshade", () => _ = ComputeTerrainAsync(TerrainProduct.Hillshade)),
                new Cmd("_Curvature…", () => _ = ComputeCurvatureAsync()),
                null,
                new Cmd("_Flow direction (D8)", () => _ = ComputeFlowDirectionAsync()),
                new Cmd("Flow acc_umulation", () => _ = ComputeFlowAccumulationAsync()),
                null,
                new Cmd("_Viewshed…", () => _ = ComputeViewshedAsync()),
                null,
                new Cmd("S_wiss-style relief (export)…", () => _ = ExportSwissReliefAsync()));

            var tools = new Cmd("T_ools").Add(
                new Cmd("_Identify (click the map)", () => SetIdentifyToolActive(!_view.IdentifyMode), isChecked: () => _view.IdentifyMode),
                new Cmd("_Profile tool (drag a line)", () => SetProfileToolActive(!_view.LineToolMode), isChecked: () => _view.LineToolMode),
                new Cmd("_Clip tool (drag a rectangle)", () => SetClipToolActive(!_view.SelectionMode), isChecked: () => _view.SelectionMode));

            var palette = new Cmd("_Palette").Add(
                new Cmd("_Edit current palette…", () => OpenPaletteEditor(CurrentPalette())),
                new Cmd("_New palette…", () => OpenPaletteEditor(null)));

            var help = new Cmd("_Help").Add(
                new Cmd("_Keyboard shortcuts…", () => _ = ShowShortcutsAsync()),
                new Cmd("_About…", () => _ = ShowAboutAsync()));

            return new List<Cmd> { file, view, layer, raster, interpolation, vector, terrain, tools, palette, help };
        }

        private Menu BuildMenu()
        {
            var menu = new Menu();
            foreach (var top in BuildMenuModel()) menu.Items.Add(ToMenuItem(top));
            return menu;
        }

        private MenuItem ToMenuItem(Cmd cmd)
        {
            var mi = new MenuItem { Header = cmd.Header };
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

            var app = new NativeMenuItem("RasterField") { Menu = new NativeMenu() };
            var about = new NativeMenuItem("About RasterField…");
            about.Click += (_, _) => _ = ShowAboutAsync();
            var quit = new NativeMenuItem("Quit RasterField") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
            quit.Click += (_, _) => Close();
            app.Menu.Items.Add(about);
            app.Menu.Items.Add(new NativeMenuItemSeparator());
            app.Menu.Items.Add(quit);
            root.Items.Add(app);

            foreach (var top in BuildMenuModel()) root.Items.Add(ToNativeMenuItem(top));
            return root;
        }

        private NativeMenuItem ToNativeMenuItem(Cmd cmd)
        {
            string header = cmd.Header.Replace("_", string.Empty, StringComparison.Ordinal).Replace("&&", "&", StringComparison.Ordinal);
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
                Flash("Bézier display smoothing on — zoom in past 1.5× to see the smooth surface (display only; data unchanged).");
            RefreshMenuChecks();
        }

        // ---- single-key tool shortcuts ---------------------------------------------------

        /// <summary>Plain-letter shortcuts (no modifier), ignored while typing into a text field.</summary>
        private bool HandleToolKey(KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.None) return false;
            if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown) return false;
            switch (e.Key)
            {
                case Key.I: SetIdentifyToolActive(!_view.IdentifyMode); break;
                case Key.P: SetProfileToolActive(!_view.LineToolMode); break;
                case Key.C: SetClipToolActive(!_view.SelectionMode); break;
                case Key.G: _view.ShowGrid = !_view.ShowGrid; _view.InvalidateVisual(); break;
                case Key.B: SetDisplayResampling(_view.DisplayResampling == DisplayResampling.Bezier ? DisplayResampling.Nearest : DisplayResampling.Bezier); break;
                case Key.Tab when ReferenceEquals(FocusManager?.GetFocusedElement(), _view): CycleActiveLayer(); break;
                case Key.Escape when _view.IdentifyMode: SetIdentifyToolActive(false); break;
                default: return false;
            }
            RefreshMenuChecks();
            return true;
        }

        private void CycleActiveLayer()
        {
            int n = _view.Layers.Count;
            if (n > 1) _view.SetActiveLayerIndex((_view.ActiveLayerIndex + 1) % n);
        }

        private async Task ShowShortcutsAsync() => await MessageAsync("Keyboard shortcuts",
            "Ctrl+O  open · Ctrl+Shift+O  add layer · Ctrl+S  save dataset as · Ctrl+B  Bézier subdivision\n" +
            "Ctrl+0 / F  zoom to fit · Ctrl+± / wheel  zoom · arrows  pan\n" +
            "I  identify · P  profile · C  clip · G  cell grid · B  Bézier display smoothing on/off\n" +
            "Tab  next raster layer · Esc  cancel the current tool");

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
            Flash($"{name}: new layer (in memory, not saved yet — Layer ▸ Save active layer, or the 💾 on its card).");
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
            if (_view.ActiveLayer == null) { await MessageAsync("Save layer", "There is no active raster layer."); return; }
            await SaveRasterLayerAsync(_view.ActiveLayer);
        }

        private async Task SaveRasterLayerAsync(RasterLayer layer)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save layer “{layer.Name}” (writes .ers + binary data file)",
                DefaultExtension = "ers",
                SuggestedFileName = SafeFileName(layer.Name) + ".ers",
                FileTypeChoices = ErsSaveFileTypeChoices,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                SetBusy(true, "Saving…");
                await Task.Run(() => layer.Document.Save(path!));
                SetBusy(false);
                _view.MarkSaved(layer, path!);
                _settings.AddRecentFile(Path.GetFullPath(path!));
                _settings.Save();
                RebuildRecentMenu();
                Flash($"Layer saved: {Path.GetFileName(path)} (+ data file)");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Save failed", ex.Message); }
        }

        private async Task SaveVectorLayerAsync(VectorLayer layer)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save vector layer “{layer.Name}”",
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
                Flash($"Vector layer saved: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { await MessageAsync("Save failed", ex.Message); }
        }

        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
                sb.Append(Path.GetInvalidFileNameChars().Contains(ch) || ch == '×' || char.IsWhiteSpace(ch) ? '_' : ch);
            return sb.ToString();
        }

        // ---- Bézier-patch subdivision ----------------------------------------------------

        private sealed record BezierChoice(int Factor, double Tension, bool Monotone, BezierNoDataMode NoData, bool ViewOnly);

        private async Task BezierSubdivisionAsync()
        {
            var layer = _view.ActiveLayer;
            if (layer == null) { await MessageAsync("Bézier subdivision", "Open a dataset first."); return; }

            var choice = await ShowBezierDialogAsync(layer);
            if (choice == null) return;

            var options = new BezierPatchOptions
            {
                Factor = choice.Factor, Tension = choice.Tension, Monotone = choice.Monotone, NoData = choice.NoData,
            };
            var window = choice.ViewOnly ? _view.VisibleCellWindow() : null;
            var doc = layer.Document;

            SetBusy(true, $"Bézier ×{choice.Factor} subdivision…");
            try
            {
                var result = await Task.Run(() => window is PixelRect w
                    ? doc.Subdivide(w.X, w.Y, w.Width, w.Height, options)
                    : doc.Subdivide(options));
                SetBusy(false);

                string lineage = string.Format(CultureInfo.InvariantCulture,
                    "Bézier ×{0} of {1}{2} · τ {3:0.00}{4}{5}",
                    choice.Factor, layer.Name, window is PixelRect r ? $" (window {r.Width}×{r.Height} at {r.X},{r.Y})" : "",
                    choice.Tension, choice.Monotone ? " · monotone" : "",
                    choice.NoData == BezierNoDataMode.NoData ? " · strict no-data" : "");
                AddDerivedRasterLayer(result, $"{layer.Name} · Bézier ×{choice.Factor}", lineage, layer);
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Bézier subdivision failed", ex.Message); }
        }

        private async Task<BezierChoice?> ShowBezierDialogAsync(RasterLayer layer)
        {
            var tcs = new TaskCompletionSource<BezierChoice?>();
            int w = layer.DatasetWidth, h = layer.DatasetHeight;
            double cellX = layer.Document.Header.RasterInfo.CellSizeX, cellY = layer.Document.Header.RasterInfo.CellSizeY;
            string unit = layer.Document.Header.CoordinateSpace.EffectiveUnits;
            var viewWindow = _view.VisibleCellWindow();

            var factorBox = new ComboBox { ItemsSource = new[] { "×2", "×3", "×4", "×8" }, SelectedIndex = 2, Width = 90 };
            var customFactor = new NumericUpDown { Minimum = 1, Maximum = 32, Value = 4, Increment = 1, Width = 110, FormatString = "0" };
            var customCheck = new CheckBox { Content = "custom" };
            var areaFull = new RadioButton { Content = "Whole layer", IsChecked = true, GroupName = "bzArea" };
            var areaView = new RadioButton { Content = "Current view only", GroupName = "bzArea", IsEnabled = viewWindow != null };
            var tension = new Slider { Minimum = 0, Maximum = 1, Value = 1, Width = 200, TickFrequency = 0.05, IsSnapToTickEnabled = true };
            var tensionText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 40 };
            var monotone = new CheckBox { Content = "Monotone — no overshoot (for sharp edges: embankments, quarry walls)" };
            var strict = new CheckBox { Content = "Leave no-data where the 4×4 neighbourhood is incomplete (instead of bilinear fallback)" };
            var sizeText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var warnText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Danger };
            var okBtn = new Button { Content = "Apply → new layer", MinWidth = 120 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };

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
                    ? "Too large for one in-memory layer — choose a smaller factor or “Current view only” (or clip first)."
                    : layer.IsStreaming && !view ? "Large streaming dataset: the whole raster has to be read. Consider “Current view only”." : "";
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
                Title = "Bézier-patch subdivision",
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

            TextBlock H(string t) => new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) };
            StackPanel Row(params Control[] c) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var x in c) p.Children.Add(x); return p; }
            StackPanel Captioned(string caption, Control c) { var p = new StackPanel { Spacing = 2 }; p.Children.Add(new TextBlock { Text = caption, Opacity = 0.8, FontSize = 11 }); p.Children.Add(c); return p; }

            var advanced = new Expander
            {
                Header = "Advanced",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        Row(new TextBlock { Text = "Tension τ", VerticalAlignment = VerticalAlignment.Center, Width = 70 }, tension, tensionText),
                        new TextBlock { Text = "1 = Catmull-Rom tangents (smooth, C¹) · 0 = exactly bilinear", Opacity = 0.7, FontSize = 11 },
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
                    new TextBlock { Text = $"Source: {layer.Name}  ({w}×{h}, {Math.Max(1, layer.BandCount)} band(s))", TextWrapping = TextWrapping.Wrap },
                    H("Subdivision"),
                    Row(factorBox, customCheck, customFactor),
                    sizeText,
                    H("Area"),
                    Row(areaFull, areaView),
                    advanced,
                    H("Preview (centre of the view, 16×16 cells)"),
                    previewSource == null
                        ? new TextBlock { Text = "No preview for streaming or RGB layers.", Opacity = 0.7 }
                        : Row(Captioned("original", originalImage), Captioned("Bézier", bezierImage)),
                    new TextBlock { Text = "ⓘ Interpolation smooths the surface — it does not add measured information.", Opacity = 0.75, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    warnText,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { okBtn, cancelBtn } },
                },
            };
            Update();
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async Task ShowBezierDisplaySettingsAsync()
        {
            var o = _view.DisplayBezierOptions;
            var tension = new Slider { Minimum = 0, Maximum = 1, Value = o.Tension, Width = 220, TickFrequency = 0.05, IsSnapToTickEnabled = true };
            var monotone = new CheckBox { Content = "Monotone (no overshoot)", IsChecked = o.Monotone };
            var enable = new CheckBox { Content = "Use Bézier display smoothing now", IsChecked = _view.DisplayResampling == DisplayResampling.Bezier };

            var dialog = new Window
            {
                Title = "Bézier display smoothing",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var ok = new Button { Content = "OK", MinWidth = 80 };
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
                    new TextBlock { Text = "When magnified, the active layer's visible window is drawn as a bicubic Bézier-patch surface. Display only — the data is not changed.", TextWrapping = TextWrapping.Wrap },
                    enable,
                    new TextBlock { Text = "Tension τ (1 = smooth Catmull-Rom, 0 = bilinear)" },
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
            var loaded = await TryGetLoadedRasterAsync("Generate contours");
            if (loaded == null) return;
            var (raster, _, _) = loaded.Value;
            var layer = _view.ActiveLayer!;
            var doc = layer.Document;

            var stats = raster.Statistics;
            var choice = await ShowContourDialogAsync(stats.Minimum, stats.Maximum);
            if (choice == null) return;

            SetBusy(true, choice.SourceFactor > 1 ? $"Bézier ×{choice.SourceFactor} surface + contours…" : "Tracing contours…");
            try
            {
                var lines = await Task.Run(() =>
                {
                    var geo = doc.GeoReference;
                    int k = choice.SourceFactor;
                    if (k <= 1)
                        return ContourGenerator.Trace(raster, geo, choice.Options);
                    // Same extent and origin, k× finer cells: every pixel-axis coefficient divides by k.
                    var fine = BezierPatchInterpolator.Subdivide(raster, new BezierPatchOptions { Factor = k });
                    var (a, b, c, d, e2, f) = geo.GeoTransform;
                    var fineGeo = new RasterGeoReference(fine.Width, fine.Height, a, b / k, c / k, d, e2 / k, f / k);
                    return ContourGenerator.Trace(fine, fineGeo, choice.Options);
                });
                SetBusy(false);
                if (lines.Count == 0) { await MessageAsync("Generate contours", "No contour lines were produced for these levels."); return; }

                var erv = ErvDocument.Create(doc.Header.CoordinateSpace.Projection, doc.Header.CoordinateSpace.Datum);
                var widths = new List<double>(lines.Count);
                var labels = new List<string?>(lines.Count);
                foreach (var line in lines)
                {
                    string level = line.Level.ToString("g6", CultureInfo.InvariantCulture);
                    var poly = new VectorPolyline { Attribute = level };
                    foreach (var p in line.Points) poly.Points.Add((p.X, p.Y));
                    erv.Objects.Add(poly);
                    widths.Add(line.IsIndex ? 2.2 : 1.0);
                    labels.Add(line.IsIndex || choice.Options.IndexEvery == 0 ? level : null);
                }

                var o = choice.Options;
                string lineage = string.Format(CultureInfo.InvariantCulture, "Contours of {0} · interval {1:g6}{2}{3}{4}",
                    layer.Name, o.Interval, o.IndexEvery > 0 ? $" · index every {o.IndexEvery}" : "",
                    choice.SourceFactor > 1 ? $" · Bézier ×{choice.SourceFactor} surface" : "",
                    o.SmoothingIterations > 0 ? $" · Chaikin ×{o.SmoothingIterations}" : "");
                AddDerivedVectorLayer(erv, $"{layer.Name} · contours {o.Interval:g4}", lineage,
                    Color.FromRgb(0x8B, 0x4A, 0x1C), widths, labels, lineWidth: 1.0);
                Flash($"Contours: {lines.Count} line(s) as a new vector layer (not saved yet — use 💾 on its card to write .erv).");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Contours failed", ex.Message); }
        }

        private async Task<ContourChoice?> ShowContourDialogAsync(double dataMin, double dataMax)
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
            var sourceBox = new ComboBox { ItemsSource = new[] { "Original grid", "Bézier ×2 surface", "Bézier ×4 surface" }, SelectedIndex = 0, Width = 180 };
            var smoothBox = new ComboBox { ItemsSource = new[] { "None", "Chaikin 1×", "Chaikin 2×", "Chaikin 3×" }, SelectedIndex = 0, Width = 180 };
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
                Title = "Generate contours",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var okBtn = new Button { Content = "Apply → new layer", MinWidth = 120 };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
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
                    countText.Text = "⚠ no levels fall within this range/interval";
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
                    new TextBlock { Text = string.Format(CultureInfo.InvariantCulture, "Data range: {0:g6} … {1:g6}", dataMin, dataMax) },
                    Form(("Minimum level", minBox), ("Maximum level", maxBox), ("Interval", intervalBox), ("Index contour every", indexBox)),
                    countText,
                    new Expander
                    {
                        Header = "Advanced",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = Form(("Source surface", sourceBox), ("Line smoothing", smoothBox), ("Min. line length", minLenBox)),
                    },
                    new TextBlock { Text = "Tip: a Bézier surface gives stair-free contours on coarse grids.", Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { okBtn, cancelBtn } },
                },
            };
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
            var loaded = await TryGetLoadedRasterAsync("Stream network");
            if (loaded == null) return;
            var (raster, cellSizeX, cellSizeY) = loaded.Value;
            var layer = _view.ActiveLayer!;
            var doc = layer.Document;

            long cells = raster.Statistics.ValidCount;
            var threshold = await AskNumberAsync("Stream network",
                "A cell becomes part of a stream when at least this many upstream cells drain through it (D8 flow accumulation). Smaller = denser network.",
                "Threshold (cells)", Math.Max(10, Math.Round(cells / 200.0)), 1);
            if (threshold == null) return;

            SetBusy(true, "Flow direction, accumulation and stream network…");
            try
            {
                var segments = await Task.Run(() =>
                {
                    var dir = HydrologyAnalysis.FlowDirection(raster, cellSizeX, cellSizeY);
                    var acc = HydrologyAnalysis.FlowAccumulation(dir);
                    return StreamNetwork.Extract(dir, acc, doc.GeoReference, threshold.Value);
                });
                SetBusy(false);
                if (segments.Count == 0) { await MessageAsync("Stream network", "No cell reaches this threshold — try a smaller one."); return; }

                var erv = ErvDocument.Create(doc.Header.CoordinateSpace.Projection, doc.Header.CoordinateSpace.Datum);
                var widths = new List<double>(segments.Count);
                foreach (var seg in segments)
                {
                    var poly = new VectorPolyline { Attribute = "order " + seg.Order.ToString(CultureInfo.InvariantCulture) };
                    foreach (var p in seg.Points) poly.Points.Add((p.X, p.Y));
                    erv.Objects.Add(poly);
                    widths.Add(0.6 + 0.6 * seg.Order);
                }
                int maxOrder = segments.Max(s => s.Order);
                AddDerivedVectorLayer(erv, $"{layer.Name} · streams ≥{threshold.Value:g6}",
                    string.Format(CultureInfo.InvariantCulture, "Stream network of {0} · D8 · threshold {1:g6} cells · Strahler 1–{2}", layer.Name, threshold.Value, maxOrder),
                    Color.FromRgb(0x1E, 0x7F, 0xFF), widths, null, lineWidth: 1.0);
                Flash($"Stream network: {segments.Count} segment(s), Strahler order up to {maxOrder} — new vector layer (not saved yet).");
            }
            catch (Exception ex) { SetBusy(false); await MessageAsync("Stream network failed", ex.Message); }
        }

        private async Task<double?> AskNumberAsync(string title, string explanation, string label, double value, double minimum)
        {
            var tcs = new TaskCompletionSource<double?>();
            var box = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)minimum, FormatString = "0.###", Increment = 10, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            var dialog = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            var ok = new Button { Content = "Apply → new layer", MinWidth = 120 };
            var cancel = new Button { Content = "Cancel", MinWidth = 80 };
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
            if (active) { SetClipToolActive(false); SetProfileToolActive(false); }
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
                    case RasterLayer layer when layer.IsVisible:
                        sb.AppendLine(DescribeRasterAt(layer, e.WorldX, e.WorldY));
                        break;
                    case VectorLayer v when v.IsVisible:
                        sb.AppendLine(DescribeVectorNear(v, e.WorldX, e.WorldY));
                        break;
                }
            }
            await MessageAsync("Identify", sb.ToString().TrimEnd());
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
            if (layer == null || r == null) { await MessageAsync("Statistics", "Open a dataset first."); return; }

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

            var dialog = new Window { Title = "Statistics & histogram", Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var close = new Button { Content = "Close", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
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
