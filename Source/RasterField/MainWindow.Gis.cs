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
    /// The mini-GIS layer of the main window (see <c>docs/UI-UX-TERV.md</c>): derived in-memory layers, Bézier-patch subdivision, contour / stream-network derivation,
    /// the identify tool and the statistics view.
    /// </summary>
    public sealed partial class MainWindow
    {
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
            var path = await Dialogs.SavePathAsync(this, L.F("Save raster layer “{0}”", layer.Name), SafeFileName(layer.Name) + ".ers", FileTypes.SaveRaster);
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
            var path = await Dialogs.SavePathAsync(this, L.F("Save vector layer “{0}”", layer.Name), SafeFileName(layer.Name) + ".erv", FileTypes.Only(FileTypes.ErvHeader));
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

            var start = Dialogs.Primary(T("Start comparison"));
            var pair = new LayerPairPicker(layers, _view.ActiveLayer, start);
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
            var body = Dialogs.Stack(
                Dialogs.Hint(mode == RasterComparisonMode.Swipe
                    ? T("The first layer is shown left of the draggable divider; the second is shown on the right.")
                    : T("The display alternates between the first and second layer.")),
                Dialogs.Captioned(T("First layer"), pair.First),
                Dialogs.Captioned(T("Second layer"), pair.Second),
                pair.Warning,
                Dialogs.Captioned(mode == RasterComparisonMode.Swipe ? T("Initial divider position (%)") : T("Blink interval (milliseconds)"), value));
            var choice = await Dialogs.ShowAsync<(int First, int Second, int Value)?>(this,
                mode == RasterComparisonMode.Swipe ? T("Swipe comparison") : T("Blink comparison"), body, start,
                () => (pair.First.SelectedIndex, pair.Second.SelectedIndex, (int)(value.Value ?? 0)), null, width: 420);
            if (choice == null) return;

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

            bool zoneAvailable = _view.IsPathFinished && _view.CurrentPath.Count >= 3;
            var run = Dialogs.Primary(T("Compare → new ΔZ layer"));
            var pair = new LayerPairPicker(layers, _view.ActiveLayer, run);
            var thresholdBox = new NumericUpDown
            {
                Value = 0,
                Minimum = 0,
                FormatString = "0.###",
                Increment = 0.1m,
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var zoneBox = new CheckBox
            {
                Content = T("Use the finished map zone as the analysis mask"),
                IsChecked = zoneAvailable,
                IsEnabled = zoneAvailable,
            };
            var streamingHint = Dialogs.Hint(T("Large streamed layers produce statistics and volumes without creating an in-memory ΔZ layer."));
            void UpdateComparisonMode()
            {
                bool streaming = pair.First.SelectedIndex >= 0 && pair.Second.SelectedIndex >= 0 &&
                    (layers[pair.First.SelectedIndex].Raster == null || layers[pair.Second.SelectedIndex].Raster == null);
                run.Content = streaming ? T("Compare → statistics") : T("Compare → new ΔZ layer");
                streamingHint.IsVisible = streaming;
            }
            pair.First.SelectionChanged += (_, _) => UpdateComparisonMode();
            pair.Second.SelectionChanged += (_, _) => UpdateComparisonMode();
            UpdateComparisonMode();
            var body = Dialogs.Stack(
                Dialogs.Hint(T("ΔZ is calculated as second raster minus first raster. The grids must match exactly.")),
                Dialogs.Captioned(T("First (baseline)"), pair.First),
                Dialogs.Captioned(T("Second (newer)"), pair.Second),
                pair.Warning,
                Dialogs.Captioned(T("Absolute change threshold"), thresholdBox),
                zoneBox,
                streamingHint);
            var choice = await Dialogs.ShowAsync<(int First, int Second, double Threshold, bool Zone)?>(this, T("Compare rasters — ΔZ and volume"), body, run,
                () => (pair.First.SelectedIndex, pair.Second.SelectedIndex, (double)(thresholdBox.Value ?? 0), zoneBox.IsChecked == true), null, width: 460);
            if (choice == null) return;

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

                string unit = L.UnitName(first.Document.Header.CoordinateSpace.EffectiveUnits);
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
            int w = layer.DatasetWidth, h = layer.DatasetHeight;
            double cellX = layer.Document.Header.RasterInfo.CellSizeX, cellY = layer.Document.Header.RasterInfo.CellSizeY;
            string unit = layer.Document.Header.CoordinateSpace.EffectiveUnits;
            var viewWindow = _view.VisibleCellWindow();

            var factorBox = new ComboBox { ItemsSource = new[] { "×2", "×3", "×4", "×8" }, SelectedIndex = 2, Width = 90 };
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
            var okBtn = Dialogs.Primary(initial != null ? T("Recompute") : T("Apply → new layer"));

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
                tensionText.Text = L.Num(tension.Value, "0.00");
                bool view = areaView.IsChecked == true && viewWindow != null;
                int sw = view ? viewWindow!.Value.Width : w, sh = view ? viewWindow!.Value.Height : h;
                long cells = (long)sw * k * sh * k;
                int bands = Math.Max(1, layer.BandCount);
                double mb = cells * 4.0 * bands / (1024 * 1024);
                sizeText.Text = L.F("{0:0.###} × {1:0.###} {2} cells → {3:0.###} × {4:0.###} {2}  ·  {5}×{6} → {7:N0}×{8:N0}  ·  ≈ {9:0.#} MB in memory ({10} band(s))",
                    cellX, cellY, L.UnitName(unit), cellX / k, cellY / k, sw, sh, (long)sw * k, (long)sh * k, mb, bands);

                bool tooBig = cells > int.MaxValue || mb > 2048;
                warnText.Text = tooBig
                    ? T("Too large for one in-memory layer — choose a smaller factor or “Current view only” (or clip first).")
                    : layer.IsStreaming && !view ? T("Large streaming dataset: the whole raster has to be read. Consider “Current view only”.") : "";
                okBtn.IsEnabled = !tooBig;
                warnText.IsVisible = !string.IsNullOrEmpty(warnText.Text);

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

            var advanced = new Expander
            {
                Header = T("Advanced"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        Dialogs.Row(new TextBlock { Text = T("Tension τ"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 70 }, tension, tensionText),
                        Dialogs.Hint(T("1 = Catmull-Rom tangents (smooth, C¹) · 0 = exactly bilinear")),
                        monotone,
                        strict,
                    },
                },
            };

            sizeText.FontSize = AppTheme.FontCaption;
            sizeText.Foreground = AppTheme.TextSecondary;
            var body = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Dialogs.Paragraph(L.F("Source: {0}  ({1}×{2}, {3} band(s))", layer.Name, w, h, Math.Max(1, layer.BandCount))),
                    AppTheme.SectionLabel(T("Subdivision")),
                    Dialogs.Row(factorBox, customCheck, customFactor),
                    sizeText,
                    AppTheme.SectionLabel(T("Area")),
                    Dialogs.Row(areaFull, areaView),
                    advanced,
                    AppTheme.SectionLabel(T("Preview (centre of the view, 16×16 cells)")),
                    previewSource == null
                        ? Dialogs.Hint(T("No preview for streaming or RGB layers."))
                        : Dialogs.Row(Dialogs.Captioned(T("Original"), originalImage), Dialogs.Captioned(T("Bézier"), bezierImage)),
                    Dialogs.Hint(T("ⓘ Interpolation smooths the surface — it does not add measured information.")),
                    warnText,
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
            }
            Update();
            return await Dialogs.ShowAsync<BezierChoice?>(this, T("Bézier-patch subdivision"), body, okBtn,
                () => new BezierChoice(Factor(), tension.Value, monotone.IsChecked == true,
                    strict.IsChecked == true ? BezierNoDataMode.NoData : BezierNoDataMode.FallbackBilinear,
                    areaView.IsChecked == true && viewWindow != null),
                null, width: 540);
        }

        private async Task ShowBezierDisplaySettingsAsync()
        {
            var o = _view.DisplayBezierOptions;
            var tension = new Slider { Minimum = 0, Maximum = 1, Value = o.Tension, Width = 220, TickFrequency = 0.05, IsSnapToTickEnabled = true };
            var monotone = new CheckBox { Content = T("Monotone (no overshoot)"), IsChecked = o.Monotone };
            var enable = new CheckBox { Content = T("Use Bézier display smoothing now"), IsChecked = _view.DisplayResampling == DisplayResampling.Bezier };

            var body = Dialogs.Stack(
                Dialogs.Paragraph(T("When magnified, the active layer's visible window is drawn as a bicubic Bézier-patch surface. Display only — the data is not changed.")),
                enable,
                Dialogs.Captioned(T("Tension τ (1 = smooth Catmull-Rom, 0 = bilinear)"), tension),
                monotone);
            bool apply = await Dialogs.ShowAsync(this, T("Bézier display smoothing"), body, Dialogs.Primary(T("OK")), () => true, false, width: 420);
            if (!apply) return;
            o.Tension = tension.Value;
            o.Monotone = monotone.IsChecked == true;
            SetDisplayResampling(enable.IsChecked == true ? DisplayResampling.Bezier : (_view.DisplayResampling == DisplayResampling.Bezier ? DisplayResampling.Nearest : _view.DisplayResampling));
            if (_view.ActiveLayer != null) _view.ActiveLayer.SmoothKey = null; // force a rebuild with the new options
            _view.ZoomBy(1.0);
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
            double niceInterval = NiceInterval((dataMax - dataMin) / 12.0);
            NumericUpDown Num(double v, double min = double.MinValue) => new NumericUpDown
            {
                Value = (decimal)v, FormatString = "0.###", Increment = (decimal)Math.Max(niceInterval, 1e-6),
                Minimum = min == double.MinValue ? decimal.MinValue : (decimal)min, Width = 140, HorizontalAlignment = HorizontalAlignment.Left,
            };
            var minBox = Num(Math.Floor(dataMin / niceInterval) * niceInterval);
            var maxBox = Num(Math.Ceiling(dataMax / niceInterval) * niceInterval);
            var intervalBox = Num(niceInterval, 1e-6);
            var indexBox = new NumericUpDown { Value = 5, Minimum = 0, Maximum = 100, Increment = 1, FormatString = "0", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            var sourceBox = new ComboBox { ItemsSource = new[] { T("Original grid"), T("Bézier ×2 surface"), T("Bézier ×4 surface") }, SelectedIndex = 0, MinWidth = 180 };
            var smoothBox = new ComboBox { ItemsSource = new[] { T("None"), T("Chaikin 1×"), T("Chaikin 2×"), T("Chaikin 3×") }, SelectedIndex = 0, MinWidth = 180 };
            var minLenBox = new NumericUpDown { Value = 0, Minimum = 0, Increment = 10, FormatString = "0.##", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            var countText = Dialogs.Hint("");
            var okBtn = Dialogs.Primary(initial != null ? T("Recompute") : T("Apply → new layer"));

            ContourOptions Options() => new ContourOptions
            {
                Minimum = (double)(minBox.Value ?? 0),
                Maximum = (double)(maxBox.Value ?? 0),
                Interval = (double)(intervalBox.Value ?? (decimal)niceInterval),
                IndexEvery = (int)(indexBox.Value ?? 0),
                SmoothingIterations = smoothBox.SelectedIndex,
                MinimumLength = (double)(minLenBox.Value ?? 0),
            };
            void UpdateCount()
            {
                var o = Options();
                int n = o.Interval > 0 ? ContourGenerator.BuildLevels(o.Minimum, o.Maximum, o.Interval).Count : 0;
                countText.Text = n > 0 ? L.F("{0} level(s)", n) : T("⚠ no levels fall within this range/interval");
                countText.Foreground = n > 0 ? AppTheme.TextSecondary : AppTheme.Danger;
                okBtn.IsEnabled = n > 0;
            }
            minBox.ValueChanged += (_, _) => UpdateCount();
            maxBox.ValueChanged += (_, _) => UpdateCount();
            intervalBox.ValueChanged += (_, _) => UpdateCount();

            if (initial != null)
            {
                var o0 = initial.Options;
                minBox.Value = (decimal)o0.Minimum; maxBox.Value = (decimal)o0.Maximum; intervalBox.Value = (decimal)o0.Interval;
                indexBox.Value = o0.IndexEvery; smoothBox.SelectedIndex = Math.Clamp(o0.SmoothingIterations, 0, 3);
                minLenBox.Value = (decimal)o0.MinimumLength;
                sourceBox.SelectedIndex = initial.SourceFactor switch { 2 => 1, 4 => 2, _ => 0 };
            }
            UpdateCount();

            var body = Dialogs.Stack(
                Dialogs.Paragraph(L.F("Data range: {0:g6} … {1:g6}", dataMin, dataMax)),
                Dialogs.Form((T("Minimum level"), minBox), (T("Maximum level"), maxBox), (T("Interval"), intervalBox), (T("Index contour every"), indexBox)),
                countText,
                new Expander
                {
                    Header = T("Advanced"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = Dialogs.Form((T("Source surface"), sourceBox), (T("Line smoothing"), smoothBox), (T("Min. line length"), minLenBox)),
                },
                Dialogs.Hint(T("Tip: a Bézier surface gives stair-free contours on coarse grids.")));
            return await Dialogs.ShowAsync<ContourChoice?>(this, T("Generate contours"), body, okBtn,
                () => new ContourChoice(Options(), sourceBox.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 }), null, width: 460);
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
            var box = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)minimum, FormatString = "0.###", Increment = 10, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            var body = Dialogs.Stack(Dialogs.Hint(explanation), Dialogs.Captioned(label, box));
            return await Dialogs.ShowAsync<double?>(this, title, body, Dialogs.Primary(T("Apply → new layer")),
                () => (double)(box.Value ?? (decimal)value), null, width: 400);
        }

        // ---- identify --------------------------------------------------------------------

        private void SetIdentifyToolActive(bool active)
        {
            if (active) { SetClipToolActive(false); SetPathTool(PathTool.None); _analysisTabs.SelectedIndex = 0; }
            _view.IdentifyMode = active;
            Flash(active ? T("Identify: click the map to list every visible layer's value there (Esc to stop).") : "");
            RefreshMenuChecks();
        }

        private async void OnIdentifyRequested(object? sender, RasterReadoutEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.F("E {0:0.###}   N {1:0.###}", e.WorldX, e.WorldY));
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
            if (!geo.IsInvertible) return name + ": " + T("(no georeference)");
            var (col, row) = geo.WorldToPixel(wx, wy);
            int c = (int)Math.Floor(col), r = (int)Math.Floor(row);
            if (c < 0 || r < 0 || c >= layer.DatasetWidth || r >= layer.DatasetHeight) return name + ": " + T("outside the raster");

            var parts = new List<string>();
            if (layer.Document.Bands.Count > 0)
            {
                for (int b = 0; b < layer.Document.Bands.Count && b < 8; b++)
                {
                    float? v = layer.Document.Bands[b].GetValueOrNull(r, c);
                    string prefix = layer.Document.Bands.Count > 1 ? $"b{b + 1} " : "";
                    parts.Add(prefix + (v.HasValue ? L.Num(v.Value) : T("no-data")));
                }
                var band = layer.Document.Bands[Math.Min(layer.ActiveBand, layer.Document.Bands.Count - 1)];
                float? bil = RasterProfiler.BilinearSample(band, col, row);
                float? bez = BezierPatchInterpolator.Sample(band, col, row, new BezierPatchOptions());
                if (bil.HasValue) parts.Add(L.F("bilinear {0}", L.Num(bil.Value)));
                if (bez.HasValue) parts.Add(L.F("Bézier {0} (interpolated)", L.Num(bez.Value)));
            }
            else if (layer.Source != null)
            {
                try
                {
                    var single = layer.Source.ReadWindow(c, r, 1, 1, band: layer.ActiveBand);
                    float? v = single.Width == 1 ? single.GetValueOrNull(0, 0) : null;
                    parts.Add(v.HasValue ? L.Num(v.Value) : T("no-data"));
                }
                catch (IOException ex) { parts.Add(L.F("read error: {0}", ex.Message)); }
            }
            return L.F("{0}  [cell {1}, {2}]: ", name, c, r) + string.Join(" · ", parts);
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
                ? name + ": " + T("(empty)")
                : L.F("{0}: nearest “{1}” at {2:0.##}", name, attribute, Math.Sqrt(best));
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
            string N(double v) => v.ToString("g6", CultureInfo.CurrentCulture);
            var rows = new List<(string, Control)>
            {
                (T("Dimensions"), Value(L.F("{0} × {1} cells", layer.DatasetWidth.ToString("N0", CultureInfo.CurrentCulture), layer.DatasetHeight.ToString("N0", CultureInfo.CurrentCulture)))),
                (T("Bands"), Value(layer.BandCount.ToString(CultureInfo.CurrentCulture))),
                (T("Cell type"), Value(layer.Document.Header.RasterInfo.CellType.ToString())),
                (T("Minimum"), Value(N(stats.Minimum))),
                (T("Maximum"), Value(N(stats.Maximum))),
                (T("Mean"), Value(N(stats.Mean))),
                (T("Std. deviation (σ)"), Value(N(stats.StandardDeviation))),
                (T("Valid cells"), Value(stats.ValidCount.ToString("N0", CultureInfo.CurrentCulture))),
                (T("No-data cells"), Value(gapsInHull >= 0
                    ? L.F("{0} ({1} inside the data footprint)", noData.ToString("N0", CultureInfo.CurrentCulture), gapsInHull.ToString("N0", CultureInfo.CurrentCulture))
                    : noData.ToString("N0", CultureInfo.CurrentCulture))),
            };
            if (layer.Lineage != null) rows.Add((T("Made from:"), Value(layer.Lineage)));
            var body = Dialogs.Stack(
                new TextBlock { Text = layer.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                layer.IsStreaming ? Dialogs.Hint(T("Statistics of the streamed window — approximate.")) : new Control { IsVisible = false },
                Dialogs.Form(rows.ToArray()),
                chart);
            await Dialogs.InfoAsync(this, T("Statistics & histogram"), body, width: 480);

            static SelectableTextBlock Value(string text) => new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") },
            };
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
                var label = new FormattedText(T("log scale"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, AppTheme.TextPrimary);
                context.DrawText(label, new Point(w - label.Width - 4, 2));
            }
        }

        /// <summary>
        /// Two raster-layer combo boxes for "compare A with B" dialogs: sensible defaults (the
        /// active layer second, another first) and the dialog's primary button disabled, with a
        /// short warning, while both name the same layer.
        /// </summary>
        private sealed class LayerPairPicker
        {
            public LayerPairPicker(IReadOnlyList<RasterLayer> layers, RasterLayer? active, Button primary)
            {
                var names = layers.Select(l => l.Name).ToList();
                int second = active != null ? IndexOf(layers, active) : -1;
                if (second < 0) second = layers.Count - 1;
                int first = second == 0 ? 1 : 0;
                First = new ComboBox { ItemsSource = names, SelectedIndex = first, HorizontalAlignment = HorizontalAlignment.Stretch };
                Second = new ComboBox { ItemsSource = names, SelectedIndex = second, HorizontalAlignment = HorizontalAlignment.Stretch };
                Warning = Dialogs.Hint(T("Choose two different raster layers."));
                Warning.Foreground = AppTheme.Danger;
                void Validate()
                {
                    bool same = First.SelectedIndex == Second.SelectedIndex;
                    Warning.IsVisible = same;
                    primary.IsEnabled = !same;
                }
                First.SelectionChanged += (_, _) => Validate();
                Second.SelectionChanged += (_, _) => Validate();
                Validate();
            }

            public ComboBox First { get; }
            public ComboBox Second { get; }
            public TextBlock Warning { get; }

            private static int IndexOf(IReadOnlyList<RasterLayer> layers, RasterLayer layer)
            {
                for (int i = 0; i < layers.Count; i++) if (ReferenceEquals(layers[i], layer)) return i;
                return -1;
            }
        }
    }
}
