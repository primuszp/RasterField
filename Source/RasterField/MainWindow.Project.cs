using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RasterField.ErMapper;
using RasterField.Projects;
using RasterField.Rasters;
using RasterField.Rendering;
using RasterField.Vectors;
using static RasterField.L;

namespace RasterField
{
    /// <summary>
    /// Projects (.rfproj), bookmarks, vector import (GeoJSON / CSV), the unsaved-work guard, and
    /// recipes: every derived layer remembers how it was made, so it can be recomputed with new
    /// parameters and stored in a project without writing its data.
    /// </summary>
    public sealed partial class MainWindow
    {
        private static readonly char[] CoordinateSeparators = { ' ', ';', '\t', ',' };
        private string? _projectPath;
        private readonly List<ProjectBookmark> _bookmarks = new();
        private bool _closeConfirmed;

        private static readonly FilePickerFileType ProjectFileType = new("RasterField project (*.rfproj)") { Patterns = new[] { "*.rfproj" } };
        private static readonly FilePickerFileType[] ProjectFileTypes = { ProjectFileType };

        // ---- recipes ---------------------------------------------------------------------

        private sealed record RecipeResult(ErsDocument? Raster, ErvDocument? Vector, IReadOnlyList<double>? Widths,
            IReadOnlyList<string?>? Labels, string Name, string Lineage, Color VectorColor);

        /// <summary>Runs a recipe on its source layer (off the UI thread).</summary>
        private async Task<RecipeResult> ComputeRecipeAsync(
            LayerRecipe recipe, CancellationToken cancellationToken = default)
        {
            if (recipe.Source is not RasterLayer src) throw new InvalidOperationException(T("The recipe's source is not a raster layer."));
            if (!_view.Layers.Contains(src)) throw new InvalidOperationException(L.F("The source layer “{0}” is no longer loaded.", src.Name));
            var doc = src.Document;
            string op = recipe.Operation;

            Raster Band()
            {
                var band = src.Raster ?? (doc.Bands.Count > 0 ? doc.Bands[Math.Min(src.ActiveBand, doc.Bands.Count - 1)] : null);
                return band ?? throw new InvalidOperationException(L.F("“{0}” is a streaming (large) dataset — clip a smaller region first.", src.Name));
            }
            var (_, gb, gc, _, ge, gf) = doc.GeoReference.GeoTransform;
            double cellX = Math.Sqrt(gb * gb + ge * ge), cellY = Math.Sqrt(gc * gc + gf * gf);

            switch (op)
            {
                case "bezier":
                {
                    var o = new BezierPatchOptions
                    {
                        Factor = recipe.Get("factor", 4),
                        Tension = recipe.Get("tension", 1.0),
                        Monotone = recipe.Get("monotone", false),
                        NoData = recipe.Get("strict", false) ? BezierNoDataMode.NoData : BezierNoDataMode.FallbackBilinear,
                    };
                    bool window = recipe.Parameters.ContainsKey("w");
                    int wx = recipe.Get("x", 0), wy = recipe.Get("y", 0), ww = recipe.Get("w", 0), wh = recipe.Get("h", 0);
                    var result = await Task.Run(() => window
                        ? doc.Subdivide(wx, wy, ww, wh, o, cancellationToken: cancellationToken)
                        : doc.Subdivide(o, cancellationToken: cancellationToken), cancellationToken);
                    string lineage = L.F("Bézier ×{0} of {1}{2} · τ {3:0.00}{4}{5}", o.Factor, src.Name,
                        window ? L.F(" (window {0}×{1} at {2},{3})", ww, wh, wx, wy) : "", o.Tension,
                        o.Monotone ? " · " + T("monotone") : "", o.NoData == BezierNoDataMode.NoData ? " · " + T("strict no-data") : "");
                    return new RecipeResult(result, null, null, null, $"{src.Name} · Bézier ×{o.Factor}", lineage, default);
                }

                case "contours":
                {
                    var o = new ContourOptions
                    {
                        Minimum = recipe.Get("min", 0.0), Maximum = recipe.Get("max", 0.0), Interval = recipe.Get("interval", 10.0),
                        IndexEvery = recipe.Get("index", 5), SmoothingIterations = recipe.Get("smooth", 0), MinimumLength = recipe.Get("minlen", 0.0),
                    };
                    int k = recipe.Get("factor", 1);
                    var raster = Band();
                    var geo = doc.GeoReference;
                    var lines = await Task.Run(() =>
                    {
                        if (k <= 1) return ContourGenerator.Trace(raster, geo, o);
                        var fine = BezierPatchInterpolator.Subdivide(raster,
                            new BezierPatchOptions { Factor = k }, cancellationToken: cancellationToken);
                        var (a, b, c, d, e2, f) = geo.GeoTransform;
                        return ContourGenerator.Trace(fine, new RasterGeoReference(fine.Width, fine.Height, a, b / k, c / k, d, e2 / k, f / k), o);
                    }, cancellationToken);
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
                        labels.Add(line.IsIndex || o.IndexEvery == 0 ? level : null);
                    }
                    string lineage = L.F("Contours of {0} · interval {1:g6}{2}{3}{4}", src.Name, o.Interval,
                        o.IndexEvery > 0 ? L.F(" · index every {0}", o.IndexEvery) : "",
                        k > 1 ? L.F(" · Bézier ×{0} surface", k) : "",
                        o.SmoothingIterations > 0 ? L.F(" · Chaikin ×{0}", o.SmoothingIterations) : "");
                    return new RecipeResult(null, erv, widths, labels, L.F("{0} · contours {1:g4}", src.Name, o.Interval), lineage, Color.FromRgb(0x8B, 0x4A, 0x1C));
                }

                case "streams":
                {
                    double threshold = recipe.Get("threshold", 100.0);
                    var raster = Band();
                    var geo = doc.GeoReference;
                    var segments = await Task.Run(() =>
                    {
                        var dir = HydrologyAnalysis.FlowDirection(raster, cellX, cellY);
                        var acc = HydrologyAnalysis.FlowAccumulation(dir);
                        return StreamNetwork.Extract(dir, acc, geo, threshold);
                    }, cancellationToken);
                    var erv = ErvDocument.Create(doc.Header.CoordinateSpace.Projection, doc.Header.CoordinateSpace.Datum);
                    var widths = new List<double>(segments.Count);
                    foreach (var seg in segments)
                    {
                        var poly = new VectorPolyline { Attribute = "order " + seg.Order.ToString(CultureInfo.InvariantCulture) };
                        foreach (var p in seg.Points) poly.Points.Add((p.X, p.Y));
                        erv.Objects.Add(poly);
                        widths.Add(0.6 + 0.6 * seg.Order);
                    }
                    int maxOrder = segments.Count > 0 ? segments.Max(s => s.Order) : 0;
                    return new RecipeResult(null, erv, widths, null, L.F("{0} · streams ≥{1:g6}", src.Name, threshold),
                        L.F("Stream network of {0} · D8 · threshold {1:g6} cells · Strahler 1–{2}", src.Name, threshold, maxOrder), Color.FromRgb(0x1E, 0x7F, 0xFF));
                }

                case "slope": case "aspect": case "hillshade": case "flowdir": case "flowacc": case "curvature":
                {
                    var raster = Band();
                    var type = Enum.TryParse<CurvatureType>(recipe.Get("type", "General"), out var t) ? t : CurvatureType.General;
                    Raster result = await Task.Run(() => op switch
                    {
                        "slope" => TerrainAnalysis.Slope(raster, cellX, cellY),
                        "aspect" => TerrainAnalysis.Aspect(raster, cellX, cellY),
                        "hillshade" => TerrainAnalysis.Hillshade(raster, cellX, cellY),
                        "curvature" => TerrainAnalysis.Curvature(raster, cellX, cellY, type),
                        "flowdir" => HydrologyAnalysis.FlowDirection(raster, cellX, cellY),
                        _ => HydrologyAnalysis.FlowAccumulation(HydrologyAnalysis.FlowDirection(raster, cellX, cellY)),
                    }, cancellationToken);
                    string label = op switch
                    {
                        "slope" => T("Slope"), "aspect" => T("Aspect"), "hillshade" => T("Hillshade"),
                        "curvature" => L.F("{0} curvature", T(type.ToString())),
                        "flowdir" => T("Flow direction"), _ => T("Flow accumulation"),
                    };
                    // The unit ends up in the legend: slope/aspect in degrees, hillshade as 0–255 illumination.
                    string? units = op switch
                    {
                        "slope" or "aspect" => "°",
                        "hillshade" => "0–255",
                        "flowacc" => T("cells"),
                        _ => null,
                    };
                    return new RecipeResult(BuildDerivedDocument(result, doc, label, units), null, null, null, $"{src.Name} · {label}", L.F("{0} of {1}", label, src.Name), default);
                }

                case "swissrelief":
                {
                    var raster = Band();
                    var image = await Task.Run(() => ReliefShader.RenderSwissStyle(raster, cellX, cellY), cancellationToken);
                    string label = T("Swiss-style relief");
                    return new RecipeResult(BuildRgbDocument(image, doc), null, null, null, $"{src.Name} · {label}", L.F("{0} of {1}", label, src.Name), default);
                }

                case "bandmath":
                {
                    string expr = recipe.Get("expr", "b1");
                    if (doc.Bands.Count == 0) throw new InvalidOperationException(L.F("“{0}” is a streaming (large) dataset — clip a smaller region first.", src.Name));
                    var bands = new Dictionary<string, Raster>();
                    for (int i = 0; i < doc.Bands.Count; i++) bands[$"b{i + 1}"] = doc.Bands[i];
                    Raster result = await Task.Run(() => RasterAlgebra.Evaluate(expr, bands), cancellationToken);
                    return new RecipeResult(BuildDerivedDocument(result, doc), null, null, null, $"{src.Name} · {expr}", L.F("Band math “{0}” on {1}", expr, src.Name), default);
                }
            }
            throw new InvalidOperationException(L.F("Unknown operation “{0}”.", op));
        }

        /// <summary>Computes a recipe and adds the result as a new derived layer.</summary>
        private async Task<object?> CreateDerivedAsync(LayerRecipe recipe, string busyText)
        {
            using var cancellation = recipe.Operation == "bezier" ? new CancellationTokenSource() : null;
            SetBusy(true, busyText, cancellation);
            try
            {
                var r = await ComputeRecipeAsync(recipe, cancellation?.Token ?? CancellationToken.None);
                SetBusy(false);
                return AddRecipeResult(recipe, r);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                Flash(T("Operation cancelled."));
                return null;
            }
            catch (Exception ex)
            {
                SetBusy(false);
                await MessageAsync(T("Operation failed"), ex.Message);
                return null;
            }
        }

        private object AddRecipeResult(LayerRecipe recipe, RecipeResult r)
        {
            if (r.Raster != null)
            {
                var layer = AddDerivedRasterLayer(r.Raster, r.Name, r.Lineage, recipe.Operation == "bezier" ? recipe.Source as RasterLayer : null);
                layer.Recipe = recipe;
                ApplyDerivedStyle(layer, recipe.Operation);
                return layer;
            }
            var v = AddDerivedVectorLayer(r.Vector!, r.Name, r.Lineage, r.VectorColor, r.Widths, r.Labels, lineWidth: 1.0);
            v.Recipe = recipe;
            Flash(L.F("{0}: {1} object(s) — new vector layer (not saved yet).", r.Name, r.Vector!.Objects.Count));
            return v;
        }

        /// <summary>
        /// Gives a terrain / hydrology product a palette and value range that suit what it measures,
        /// instead of the elevation palette that happens to be selected: grey illumination for a
        /// hillshade, a sequential ramp from 0 for slope, a cyclic wheel over 0–360° for aspect, a
        /// diverging ramp symmetric around 0 for curvature and an emphasised blue ramp for flow
        /// accumulation. The user can still change all of it in the properties panel.
        /// </summary>
        private void ApplyDerivedStyle(RasterLayer layer, string operation)
        {
            var raster = layer.Raster;
            if (raster == null || layer.ShowRgbComposite || raster.Statistics.ValidCount == 0) return;
            var stats = raster.Statistics;
            switch (operation)
            {
                case "hillshade":
                    _view.ApplyDisplaySettings(layer, BuiltInPalettes.Grayscale, 0, 255);
                    break;
                case "slope":
                    // The 99th percentile, so a few near-vertical edge cells don't wash out the rest.
                    double steep = RasterHistogram.Build(raster).Percentile(99);
                    _view.ApplyDisplaySettings(layer, BuiltInPalettes.Viridis, 0, Math.Max(steep, 0.1));
                    break;
                case "aspect":
                    _view.ApplyDisplaySettings(layer, _palettes.Get("Hue wheel (cyclic)") ?? BuiltInPalettes.Spectrum, 0, 360);
                    break;
                case "curvature":
                    var (low, high) = RasterHistogram.Build(raster).PercentileRange(2, 98);
                    double m = Math.Max(Math.Abs(low), Math.Abs(high));
                    _view.ApplyDisplaySettings(layer, BuiltInPalettes.BlueWhiteRed, -(m > 0 ? m : 1), m > 0 ? m : 1);
                    break;
                case "flowacc":
                    // Most cells drain only themselves; a low gamma lifts the few large values (the channels).
                    _view.ApplyDisplaySettings(layer, BuiltInPalettes.Precipitation, stats.Minimum, stats.Maximum, gamma: 0.35);
                    break;
            }
        }

        /// <summary>Opens the operation's dialog pre-filled with the layer's recipe and recomputes the layer in place.</summary>
        private async Task EditRecipeAsync(object layer)
        {
            var recipe = layer switch { RasterLayer r => r.Recipe, VectorLayer v => v.Recipe, _ => null };
            if (recipe == null) { await MessageAsync(T("Parameters"), T("This layer has no recipe (it was opened from a file, or made by clip / mosaic / viewshed / fill).")); return; }
            if (recipe.Source is not RasterLayer src || !_view.Layers.Contains(src))
            {
                await MessageAsync(T("Parameters"), T("The layer this one was derived from is no longer loaded."));
                return;
            }

            LayerRecipe? updated = recipe.Operation switch
            {
                "bezier" => await AskBezierRecipeAsync(src, recipe),
                "contours" => await AskContourRecipeAsync(src, recipe),
                "streams" => await AskStreamRecipeAsync(src, recipe),
                "curvature" => await ShowCurvatureDialogAsync() is CurvatureType t
                    ? new LayerRecipe("curvature", src, new Dictionary<string, string> { ["type"] = t.ToString() }) : null,
                "bandmath" => await ShowBandMathDialogAsync(Enumerable.Range(1, Math.Max(1, src.Document.Bands.Count)).Select(i => $"b{i}").ToList(), recipe.Get("expr", "")) is string e && e.Length > 0
                    ? new LayerRecipe("bandmath", src, new Dictionary<string, string> { ["expr"] = e }) : null,
                _ => recipe, // no parameters: recompute from the (possibly changed) source
            };
            if (updated == null) return;

            using var cancellation = updated.Operation == "bezier" ? new CancellationTokenSource() : null;
            SetBusy(true, T("Recomputing…"), cancellation);
            try
            {
                var r = await ComputeRecipeAsync(updated, cancellation?.Token ?? CancellationToken.None);
                SetBusy(false);
                if (layer is RasterLayer rl && r.Raster != null) _view.ReplaceLayerDocument(rl, r.Raster, r.Lineage, updated);
                else if (layer is VectorLayer vl && r.Vector != null) _view.ReplaceVectorDocument(vl, r.Vector, r.Widths, r.Labels, r.Lineage, updated);
                Flash(L.F("Recomputed: {0}", r.Lineage));
            }
            catch (OperationCanceledException) { SetBusy(false); Flash(T("Operation cancelled.")); }
            catch (Exception ex) { SetBusy(false); await MessageAsync(T("Operation failed"), ex.Message); }
        }

        private async Task<LayerRecipe?> AskBezierRecipeAsync(RasterLayer src, LayerRecipe? initial)
        {
            BezierChoice? seed = initial == null ? null : new BezierChoice(initial.Get("factor", 4), initial.Get("tension", 1.0), initial.Get("monotone", false),
                initial.Get("strict", false) ? BezierNoDataMode.NoData : BezierNoDataMode.FallbackBilinear, initial.Parameters.ContainsKey("w"));
            var choice = await ShowBezierDialogAsync(src, seed);
            if (choice == null) return null;
            var p = new Dictionary<string, string>
            {
                ["factor"] = choice.Factor.ToString(CultureInfo.InvariantCulture),
                ["tension"] = LayerRecipe.Num(choice.Tension),
                ["monotone"] = choice.Monotone.ToString(),
                ["strict"] = (choice.NoData == BezierNoDataMode.NoData).ToString(),
            };
            // Recomputing a window recipe keeps its original window; a new one uses the current view.
            PixelRect? window = !choice.ViewOnly ? null
                : initial != null && initial.Parameters.ContainsKey("w")
                    ? new PixelRect(initial.Get("x", 0), initial.Get("y", 0), initial.Get("w", 1), initial.Get("h", 1))
                    : _view.VisibleCellWindow();
            if (window is PixelRect w)
            {
                p["x"] = w.X.ToString(CultureInfo.InvariantCulture); p["y"] = w.Y.ToString(CultureInfo.InvariantCulture);
                p["w"] = w.Width.ToString(CultureInfo.InvariantCulture); p["h"] = w.Height.ToString(CultureInfo.InvariantCulture);
            }
            return new LayerRecipe("bezier", src, p);
        }

        private async Task<LayerRecipe?> AskContourRecipeAsync(RasterLayer src, LayerRecipe? initial)
        {
            var raster = src.Raster;
            if (raster == null) { await MessageAsync(T("Generate contours"), L.F("“{0}” is a streaming (large) dataset — clip a smaller region first.", src.Name)); return null; }
            ContourChoice? seed = initial == null ? null : new ContourChoice(new ContourOptions
            {
                Minimum = initial.Get("min", 0.0), Maximum = initial.Get("max", 0.0), Interval = initial.Get("interval", 10.0),
                IndexEvery = initial.Get("index", 5), SmoothingIterations = initial.Get("smooth", 0), MinimumLength = initial.Get("minlen", 0.0),
            }, initial.Get("factor", 1));
            var choice = await ShowContourDialogAsync(raster.Statistics.Minimum, raster.Statistics.Maximum, seed);
            if (choice == null) return null;
            var o = choice.Options;
            return new LayerRecipe("contours", src, new Dictionary<string, string>
            {
                ["min"] = LayerRecipe.Num(o.Minimum), ["max"] = LayerRecipe.Num(o.Maximum), ["interval"] = LayerRecipe.Num(o.Interval),
                ["index"] = o.IndexEvery.ToString(CultureInfo.InvariantCulture), ["smooth"] = o.SmoothingIterations.ToString(CultureInfo.InvariantCulture),
                ["minlen"] = LayerRecipe.Num(o.MinimumLength), ["factor"] = choice.SourceFactor.ToString(CultureInfo.InvariantCulture),
            });
        }

        private async Task<LayerRecipe?> AskStreamRecipeAsync(RasterLayer src, LayerRecipe? initial)
        {
            long cells = src.Raster?.Statistics.ValidCount ?? 0;
            var threshold = await AskNumberAsync(T("Stream network"),
                T("A cell becomes part of a stream when at least this many upstream cells drain through it (D8 flow accumulation). Smaller = denser network."),
                T("Threshold (cells)"), initial?.Get("threshold", 100.0) ?? Math.Max(10, Math.Round(cells / 200.0)), 1);
            return threshold == null ? null
                : new LayerRecipe("streams", src, new Dictionary<string, string> { ["threshold"] = LayerRecipe.Num(threshold.Value) });
        }

        // ---- vector import -------------------------------------------------------------------

        private static readonly Color[] ImportColors =
        {
            Color.FromRgb(255, 105, 180), Color.FromRgb(0, 200, 255), Color.FromRgb(255, 200, 0),
            Color.FromRgb(120, 220, 90), Color.FromRgb(255, 120, 60),
        };

        private static readonly string[] VectorExtensions = { ".erv", ".geojson", ".json", ".csv", ".txt" };
        private static readonly string[] RasterExtensions = { ".ers", ".tif", ".tiff" };

        private static bool IsVectorFile(string path) => VectorExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        private static bool IsRasterFile(string path) => RasterExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        private static bool IsGeoTiff(string path) => Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                                                      Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase);

        /// <summary>Adds an .erv, GeoJSON or CSV-points file as a vector layer.</summary>
        private VectorLayer AddVectorFromFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".erv") return _view.AddVectorLayerFromPath(path);

            IReadOnlyList<VectorObject> objects = ext is ".geojson" or ".json"
                ? GeoJsonFormat.ReadFile(path)
                : CsvPointFormat.Read(File.ReadAllText(path));
            if (objects.Count == 0) throw new InvalidDataException(T("The file contains no features."));

            // Imported coordinates are assumed to be in the map's coordinate system (no reprojection).
            var cs = _view.Document?.Header.CoordinateSpace;
            var erv = ErvDocument.Create(cs?.Projection is { } p && p != "RAW" ? p : null, cs?.Datum is { } d && d != "RAW" ? d : null);
            foreach (var o in objects) erv.Objects.Add(o);
            var layer = _view.AddVectorLayer(erv, Path.GetFileNameWithoutExtension(path));
            layer.SourcePath = Path.GetFullPath(path);
            layer.Color = ImportColors[_view.VectorLayers.Count % ImportColors.Length];
            _view.InvalidateVisual();
            return layer;
        }

        // ---- projects ------------------------------------------------------------------------

        private void UpdateTitle()
        {
            string name = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : T("(untitled project)");
            Title = $"RasterField — {name}";
            UpdateTitleBarText();
        }

        private List<string> UnsavedLayerNames() =>
            _view.DrawOrder.Select(o => o switch
            {
                RasterLayer { IsFrame: false, IsUnsaved: true } r => r.Name,
                VectorLayer { IsUnsaved: true } v => v.Name,
                _ => null,
            }).Where(n => n != null).Select(n => n!).ToList();

        private async Task<bool> ConfirmDiscardAsync(string action)
        {
            var unsaved = UnsavedLayerNames();
            if (unsaved.Count == 0) return true;
            return await ConfirmAsync(action,
                L.F("{0} layer(s) exist only in memory and will be lost:\n• {1}", unsaved.Count, string.Join("\n• ", unsaved.Take(12))),
                T("Continue anyway"));
        }

        private async Task NewProjectAsync()
        {
            if (!await ConfirmDiscardAsync(T("New project"))) return;
            _view.ClearAll();
            _bookmarks.Clear();
            _projectPath = null;
            SetPathTool(PathTool.None);
            RebuildMenus();
            UpdateTitle();
        }

        private async Task OpenProjectDialogAsync()
        {
            if (!await ConfirmDiscardAsync(T("Open project"))) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Open RasterField project"),
                AllowMultiple = false,
                FileTypeFilter = new[] { ProjectFileType, FilePickerFileTypes.All },
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path)) await LoadProjectAsync(path!);
        }

        /// <summary>Opens a project file (also used for drag-and-drop and the command line).</summary>
        public async Task LoadProjectAsync(string path)
        {
            ProjectDocument project;
            try { project = ProjectDocument.Load(path); }
            catch (Exception ex) { await MessageAsync(T("Could not open project"), ex.Message); return; }

            SetBusy(true, T("Opening project…"));
            _view.ClearAll();
            _bookmarks.Clear();
            var problems = new List<string>();
            var byId = new Dictionary<string, object>();
            try
            {
                foreach (var pl in project.Layers)
                {
                    try
                    {
                        object? layer = null;
                        if (pl.Path != null)
                        {
                            if (!File.Exists(pl.Path)) { problems.Add(L.F("{0}: file not found ({1})", pl.Name, pl.Path)); continue; }
                            layer = pl.Kind == "vector" ? AddVectorFromFile(pl.Path) : _view.AddLayerFromPath(pl.Path, CurrentPalette());
                        }
                        else if (pl.Recipe != null)
                        {
                            if (pl.Recipe.SourceLayerId == null || !byId.TryGetValue(pl.Recipe.SourceLayerId, out var source))
                            {
                                problems.Add(L.F("{0}: its source layer is missing, cannot recompute", pl.Name));
                                continue;
                            }
                            var recipe = new LayerRecipe(pl.Recipe.Operation, source, pl.Recipe.Parameters);
                            layer = AddRecipeResult(recipe, await ComputeRecipeAsync(recipe));
                        }
                        if (layer == null) continue;
                        byId[pl.Id] = layer;
                        ApplyProjectLayer(layer, pl);
                    }
                    catch (Exception ex) { problems.Add($"{pl.Name}: {ex.Message}"); }
                }

                if (project.ActiveLayerId != null && byId.TryGetValue(project.ActiveLayerId, out var active) && active is RasterLayer ar)
                    _view.SetActiveLayer(ar);
                if (project.View is { } v) _view.SetViewState(v.CenterX, v.CenterY, v.WorldPerPixel);
                _bookmarks.AddRange(project.Bookmarks);
            }
            finally
            {
                SetBusy(false);
                _view.ClearHistory();
            }

            _projectPath = Path.GetFullPath(path);
            _settings.LastProjectPath = _projectPath;
            _settings.AddRecentFile(_projectPath);
            _settings.Save();
            RebuildMenus();
            UpdateTitle();
            QueueRebuildLayersPanel();
            if (problems.Count > 0)
                await MessageAsync(T("Project opened with problems"), string.Join("\n", problems));
            else
                Flash(L.F("Project opened: {0} ({1} layer(s))", Path.GetFileName(path), byId.Count));
        }

        private void ApplyProjectLayer(object layer, ProjectLayer pl)
        {
            switch (layer)
            {
                case RasterLayer r:
                    r.Name = pl.Name.Length > 0 ? pl.Name : r.Name;
                    if (pl.Lineage != null) r.Lineage = pl.Lineage;
                    _view.SetActiveLayer(r);
                    if (pl.ActiveBand is int band) _view.SetActiveBand(band);
                    if (pl.RgbComposite is bool rgb) _view.ShowRgbComposite = rgb;
                    if (pl.Palette != null)
                    {
                        var palette = _palettes.GetOrDefault(pl.Palette);
                        _view.SetPalette(pl.PaletteReversed ? palette.Reversed() : palette);
                    }
                    if (pl.RenderMode != null && Enum.TryParse<PaletteRenderMode>(pl.RenderMode, out var mode)) _view.SetRenderMode(mode, pl.ClassCount ?? 8);
                    if (pl.Minimum is double min && pl.Maximum is double max) _view.SetValueRange(min, max);
                    if (pl.Gamma is double gamma) _view.SetGamma(gamma);
                    _view.SetLayerOpacity(r, pl.Opacity);
                    if (pl.BlendMode != null && Enum.TryParse<LayerBlendMode>(pl.BlendMode, out var blend)) _view.SetLayerBlendMode(r, blend);
                    _view.SetLayerVisible(r, pl.Visible);
                    break;
                case VectorLayer v:
                    v.Name = pl.Name.Length > 0 ? pl.Name : v.Name;
                    if (pl.Lineage != null) v.Lineage = pl.Lineage;
                    if (pl.Color != null) { try { _view.SetVectorLayerColor(v, Color.Parse(pl.Color)); } catch (FormatException) { } }
                    if (pl.LineWidth is double w) _view.SetVectorLayerLineWidth(v, w);
                    _view.SetVectorLayerVisible(v, pl.Visible);
                    break;
            }
        }

        private async Task SaveProjectAsync(bool saveAs)
        {
            string? path = saveAs ? null : _projectPath;
            if (path == null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = T("Save RasterField project"),
                    DefaultExtension = "rfproj",
                    SuggestedFileName = (_projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : SuggestName()) + ".rfproj",
                    FileTypeChoices = ProjectFileTypes,
                });
                path = file?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) return;
            }

            var ids = new Dictionary<object, string>();
            var project = new ProjectDocument();
            var skipped = new List<string>();
            foreach (var item in _view.DrawOrder)
            {
                if (item is RasterLayer { IsFrame: true }) continue;
                var pl = new ProjectLayer();
                ids[item] = pl.Id;
                switch (item)
                {
                    case RasterLayer r:
                        pl.Kind = "raster"; pl.Name = r.Name; pl.Visible = r.IsVisible; pl.Opacity = r.Opacity; pl.Lineage = r.Lineage;
                        pl.BlendMode = r.BlendMode == LayerBlendMode.Normal ? null : r.BlendMode.ToString();
                        pl.Path = r.IsUnsaved ? null : r.Document.HeaderPath;
                        if (r.Colorizer is { } c)
                        {
                            string pn = c.Palette.Name;
                            pl.PaletteReversed = pn.EndsWith(" (reversed)", StringComparison.Ordinal);
                            pl.Palette = pl.PaletteReversed ? pn.Substring(0, pn.Length - " (reversed)".Length) : pn;
                            pl.Minimum = c.Minimum; pl.Maximum = c.Maximum; pl.Gamma = c.Gamma;
                            pl.RenderMode = c.Mode.ToString(); pl.ClassCount = c.ClassCount;
                        }
                        pl.ActiveBand = r.ActiveBand; pl.RgbComposite = r.ShowRgbComposite;
                        pl.Recipe = r.IsUnsaved ? ToProjectRecipe(r.Recipe, ids) : null;
                        break;
                    case VectorLayer v:
                        pl.Kind = "vector"; pl.Name = v.Name; pl.Visible = v.IsVisible; pl.Lineage = v.Lineage;
                        pl.Color = FormatHex(v.Color); pl.LineWidth = v.LineWidth;
                        pl.Path = v.IsUnsaved ? null : v.Document.HeaderPath ?? v.SourcePath;
                        pl.Recipe = v.IsUnsaved ? ToProjectRecipe(v.Recipe, ids) : null;
                        break;
                }
                if (pl.Path == null && pl.Recipe == null) { skipped.Add(pl.Name); ids.Remove(item); continue; }
                project.Layers.Add(pl);
            }
            if (_view.ActiveLayer is { IsFrame: false } a && ids.TryGetValue(a, out var activeId)) project.ActiveLayerId = activeId;
            if (_view.GetViewState() is { } vs) project.View = new ProjectView { CenterX = vs.X, CenterY = vs.Y, WorldPerPixel = vs.WorldPerPixel };
            project.Bookmarks.AddRange(_bookmarks);

            if (skipped.Count > 0 && !await ConfirmAsync(T("Save project"),
                    L.F("These layers exist only in memory and have no recipe, so they can't be stored in the project (save them as files first):\n• {0}", string.Join("\n• ", skipped)),
                    T("Save without them")))
                return;

            try
            {
                project.Save(path!);
                _projectPath = Path.GetFullPath(path!);
                _settings.LastProjectPath = _projectPath;
                _settings.AddRecentFile(_projectPath);
                _settings.Save();
                RebuildRecentMenu();
                UpdateTitle();
                Flash(L.F("Project saved: {0} — derived layers are stored as recipes and recomputed on open.", Path.GetFileName(path)));
            }
            catch (Exception ex) { await MessageAsync(T("Save failed"), ex.Message); }
        }

        private static ProjectRecipe? ToProjectRecipe(LayerRecipe? recipe, Dictionary<object, string> ids) =>
            recipe != null && ids.TryGetValue(recipe.Source, out var sourceId)
                ? new ProjectRecipe { Operation = recipe.Operation, SourceLayerId = sourceId, Parameters = new Dictionary<string, string>(recipe.Parameters) }
                : null;

        // ---- bookmarks / go to ------------------------------------------------------------------

        private async Task AddBookmarkAsync()
        {
            if (_view.GetViewState() is not { } vs) return;
            string? name = await AskTextAsync(T("Add bookmark"), T("Name"), L.F("View {0}", _bookmarks.Count + 1));
            if (string.IsNullOrWhiteSpace(name)) return;
            _bookmarks.Add(new ProjectBookmark { Name = name!.Trim(), View = new ProjectView { CenterX = vs.X, CenterY = vs.Y, WorldPerPixel = vs.WorldPerPixel } });
            RebuildMenus();
            Flash(L.F("Bookmark “{0}” — Ctrl+{1}", name, Math.Min(9, _bookmarks.Count)));
        }

        private void GoToBookmark(ProjectBookmark bookmark) =>
            _view.SetViewState(bookmark.View.CenterX, bookmark.View.CenterY, bookmark.View.WorldPerPixel);

        private async Task GoToCoordinateAsync()
        {
            if (_view.GetViewState() is not { } vs) return;
            string? text = await AskTextAsync(T("Go to coordinate"), T("Easting, Northing (e.g. 650000 240000)"),
                string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##}", vs.X, vs.Y));
            if (text == null) return;
            var parts = text.Split(CoordinateSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                _view.SetViewState(x, y, vs.WorldPerPixel);
            else
                await MessageAsync(T("Go to coordinate"), T("Enter two numbers: easting and northing."));
        }

        // ---- small dialogs -------------------------------------------------------------------------

        private async Task<string?> AskTextAsync(string title, string label, string value)
        {
            var tcs = new TaskCompletionSource<string?>();
            var box = new TextBox { Text = value };
            var dialog = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            var ok = new Button { Content = T("OK"), MinWidth = 80 };
            var cancel = new Button { Content = T("Cancel"), MinWidth = 80 };
            ok.Click += (_, _) => { tcs.TrySetResult(box.Text); dialog.Close(); };
            cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { tcs.TrySetResult(box.Text); dialog.Close(); } };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 8,
                Children = { new TextBlock { Text = label }, box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } },
            };
            dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            var tcs = new TaskCompletionSource<bool>();
            var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            var ok = new Button { Content = confirmText, MinWidth = 100 };
            var cancel = new Button { Content = T("Cancel"), MinWidth = 80 };
            ok.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 12,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(false);
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        /// <summary>Warns before closing while derived layers exist only in memory.</summary>
        private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_closeConfirmed || UnsavedLayerNames().Count == 0)
            {
                RememberDockWidths();
                SaveWindowSettings();
                return;
            }
            e.Cancel = true;
            if (await ConfirmDiscardAsync(T("Close RasterField")))
            {
                _closeConfirmed = true;
                Close();
            }
        }
    }
}
