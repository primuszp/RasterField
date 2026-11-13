using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RasterField.Interpolation;
using RasterField.Rasters;
using RasterField.Vectors;
using static RasterField.L;

namespace RasterField;

public sealed partial class MainWindow
{
    private readonly List<NeutralDesignDraft> _designDrafts = new();
    private readonly Dictionary<NeutralDesignDraft, RasterLayer> _designDraftSources = new();
    private readonly HashSet<VectorLayer> _designOfflineLayers = new();
    private NeutralDesignDraft? _activeDesignDraft;
    private string _savedDesigns = "[]";
    private readonly StackPanel _designVariants = new() { Spacing = 6 };
    private readonly StackPanel _designReviewResults = new() { Spacing = 6 };
    private readonly Button _designContinuous = new();
    private readonly DispatcherTimer _designTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _continuousRunning, _continuousTick, _designPickingTarget;
    private (double X, double Y)? _designPointer;
    private NeutralDesignTarget? _designTarget;
    private readonly NumericUpDown _designRadius = new() { Value = 10, Minimum = 0.1m, Maximum = 10000, Width = 90 };
    private readonly TextBlock _designTargetText = new() { TextWrapping = TextWrapping.Wrap };

    private Expander BuildDesignExtras()
    {
        var body = new StackPanel { Spacing = 8 };
        Button Action(string label, Func<Task> action)
        {
            var button = new Button { Content = new TextBlock { Text = T(label), TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += async (_, _) => { if(_designBusy)return; try { await action(); } catch (Exception ex) { await MessageAsync(T("Neutral line"), ex.Message); } };
            return button;
        }
        body.Children.Add(Action("Start from coordinates…", async () =>
        {
            if (_designSession != null) return;
            var point = await AskDesignCoordinates();
            if (point.HasValue) { SetPathTool(PathTool.NeutralLine); await StartDesignAsync(point.Value.X, point.Value.Y); }
        }));
        body.Children.Add(new TextBlock { Text = T("Optional target · arrival radius (m)"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_designRadius);
        _designRadius.ValueChanged += (_, _) => { if (_designTarget != null) { _designTarget.Radius = (double)(_designRadius.Value ?? 10); ShowDesignTarget(); } };
        body.Children.Add(Action("Pick target on map", () =>
        {
            StopContinuousDesign(); SetPathTool(PathTool.NeutralLine); _designPickingTarget = true;
            _designStatus.Text = T("Click the target centre. Arrival stops at the circle, without snapping to its centre.");
            return Task.CompletedTask;
        }));
        body.Children.Add(Action("Target coordinates…", async () =>
        {
            var point = await AskDesignCoordinates(); if (point.HasValue) SetDesignTarget(point.Value.X, point.Value.Y);
        }));
        body.Children.Add(Action("Remove target", () => { _designTarget = null; ShowDesignTarget(); return Task.CompletedTask; }));
        body.Children.Add(_designTargetText);
        body.Children.Add(Action("New variant from the same start…", NewDesignVariantAsync));
        body.Children.Add(_designVariants);
        body.Children.Add(Action("Review an existing line…", ReviewExistingDesignAsync));
        body.Children.Add(_designReviewResults);
        _designContinuous.Content = T("Continuous drawing");
        _designContinuous.Click += (_, _) =>
        {
            if (_continuousRunning) StopContinuousDesign();
            else if (_designSession != null && !_designPaused && !_designBusy && DesignSourceValid() && !DesignTargetReached())
            { _continuousRunning = true; _designContinuous.Content = T("Stop continuous drawing"); _designTimer.Start(); }
        };
        _designTimer.Tick += async (_, _) => await ContinuousDesignTickAsync();
        Deactivated += (_, _) => StopContinuousDesign();
        _view.PointerExited += (_, _) => { _designPointer = null; StopContinuousDesign(); };
        _view.PointerPressed += (_, _) => StopContinuousDesign();
        return new Expander { Header = T("Design tools"), Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private async Task<(double X, double Y)?> AskDesignCoordinates()
    {
        var text = await AskTextAsync(T("Neutral line"), T("Easting, Northing (e.g. 650000 240000)"), "");
        if (text == null) return null;
        var parts = text.Split(CoordinateSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !double.IsFinite(x) || !double.IsFinite(y))
        { await MessageAsync(T("Neutral line"), T("Enter two numbers: easting and northing.")); return null; }
        return (x, y);
    }

    private void SetDesignTarget(double x, double y)
    {
        StopContinuousDesign();
        double? elevation=null;
        if(_designSession!=null)lock(_designSession)elevation=_designSession.Surface.Sample(x,y).Value;
        _designTarget = new NeutralDesignTarget { X = x, Y = y, Radius = (double)(_designRadius.Value ?? 10), Elevation = elevation };
        _designPickingTarget = false; ShowDesignTarget();
    }

    private bool DesignTargetReached() => _designTarget != null && _designSession != null && _designTarget.Reached(_designSession.End.X, _designSession.End.Y);

    private void ShowDesignTarget()
    {
        _view.SetNeutralDesignTarget(_designTarget);
        _designTargetText.Text = _designTarget == null ? T("No target") : L.F("Target: {0:0.##}, {1:0.##} · radius {2:0.##} m", _designTarget.X, _designTarget.Y, _designTarget.Radius);
        if (_designSession != null && _designTarget != null)
        {
            _designTargetText.Text += "\n" + L.F("Distance to target: {0:0.##} m", _designTarget.Distance(_designSession.End.X, _designSession.End.Y));
            if (DesignTargetReached()) { StopContinuousDesign(); _designStatus.Text = T("Arrival area reached. Review the line and finish, or continue manually."); }
            else if (_designTarget.LevelPassed(_designSession)) { StopContinuousDesign(); _designTargetText.Text += "\n" + T("Target elevation passed outside the arrival area."); }
        }
    }

    private void StopContinuousDesign()
    { _continuousRunning = false; _designTimer.Stop(); _designContinuous.Content = T("Continuous drawing"); }

    private async Task ContinuousDesignTickAsync()
    {
        if (!_continuousRunning || _continuousTick || _designBusy) return;
        if (_designSession == null || _designPaused || !DesignSourceValid() || DesignTargetReached()) { StopContinuousDesign(); return; }
        if (!_designPointer.HasValue) return;
        var p = _designPointer.Value; var end = _designSession.End;
        if (Math.Sqrt(Math.Pow(p.X - end.X, 2) + Math.Pow(p.Y - end.Y, 2)) < _designSession.Step) { StopContinuousDesign(); return; }
        _continuousTick = true;
        try
        {
            SelectDesignDirection(p.X, p.Y);
            if (_designSearch?.SampledLevelCircle != true && (_designSelected < 0 || !_designAssessments[_designSelected].IsValid)) { StopContinuousDesign(); return; }
            if (_designTarget?.Elevation is double level && _designSelected >= 0)
            {
                var candidate = _designCandidates[_designSelected];
                if (!_designTarget.Reached(candidate.X, candidate.Y) && (_designSession.GradePercent > 0 ? candidate.Elevation > level : _designSession.GradePercent < 0 && candidate.Elevation < level))
                { StopContinuousDesign(); _designStatus.Text = T("Target elevation passed outside the arrival area."); return; }
            }
            int count = _designSession.Segments.Count;
            await DispatchNeutralPointAsync(p.X, p.Y);
            if (_designSession == null || _designPendingWarning != null || _designSession.Segments.Count == count || _designTarget?.LevelPassed(_designSession) == true) StopContinuousDesign();
        }
        finally { _continuousTick = false; }
    }

    private static string DesignSourceSignature(Raster raster, RasterLayer source)
    {
        var hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(raster.Samples.AsSpan())));
        var (a, b, c, d, e, f) = source.Document.GeoReference.GeoTransform;
        return hash + "|" + raster.Width + "|" + raster.Height + "|" + raster.NoDataValue.ToString("R", CultureInfo.InvariantCulture) + "|" +
            string.Join(",", new[] { a, b, c, d, e, f }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "|" +
            source.Document.Header.CoordinateSpace.Projection + "|" + source.Document.Header.CoordinateSpace.Datum + "|" + source.Document.Header.CoordinateSpace.Units;
    }

    private void RegisterDesignDraft(string signature)
    {
        var space = _designSource!.Document.Header.CoordinateSpace;
        _activeDesignDraft = new NeutralDesignDraft
        {
            Name = L.F("Trial {0}", _designDrafts.Count + 1),
            Band = _designBand,
            SourceSignature = signature,
            Projection = space.Projection ?? "",
            Datum = space.Datum ?? "",
            Units = space.Units ?? "",
            UnitAssumption = _neutralUnitNote
        };
        _designDrafts.Add(_activeDesignDraft); _designDraftSources[_activeDesignDraft] = _designSource;
        CaptureActiveDesign(); RebuildDesignVariants();
    }

    private void CaptureActiveDesign()
    {
        if (_activeDesignDraft == null || _designSession == null) return;
        NeutralDesignDraft data; lock (_designSession) data = _designSession.CaptureDraft();
        _activeDesignDraft.Record = data.Record; _activeDesignDraft.AcceptedSegments = data.AcceptedSegments;
        _activeDesignDraft.Step = data.Step; _activeDesignDraft.Profiles = data.Profiles; _activeDesignDraft.Turns = data.Turns;
        _activeDesignDraft.Target = _designTarget == null ? null : new NeutralDesignTarget { X = _designTarget.X, Y = _designTarget.Y, Radius = _designTarget.Radius, Elevation = _designTarget.Elevation };
    }

    private bool DesignsDirty()
    { CaptureActiveDesign(); return JsonSerializer.Serialize(_designDrafts) != _savedDesigns; }

    private void ResetDesignWorkspace()
    { ClearDesign(); _designDrafts.Clear(); _designDraftSources.Clear(); _designOfflineLayers.Clear(); _designReviewResults.Children.Clear(); _view.SetNeutralReviewSegment(null); _savedDesigns = "[]"; RebuildDesignVariants(); }

    private VectorLayer CreateStoredDesignLayer(NeutralDesignDraft draft, string name)
    {
        var doc = ErvDocument.Create(draft.Projection, draft.Datum); doc.Header.CoordinateSpace.Units = draft.Units;
        var line = new VectorPolyline();
        foreach (var v in draft.Record.Vertices.Take(draft.AcceptedSegments + 1)) line.Points.Add((v[0], v[1]));
        doc.Objects.Add(line);
        var layer = _view.AddVectorLayer(doc, name); layer.DesignData = draft; layer.LineWidth = 2.5;
        return layer;
    }

    private async Task NewDesignVariantAsync()
    {
        if (_designSession == null || _designBusy || !DesignSourceValid()) return;
        var text = await AskTextAsync(T("New variant"), T("Signed grade (%)"), _designSession.GradePercent.ToString(CultureInfo.InvariantCulture));
        if (text == null) return;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var grade) || grade < -100 || grade > 100)
        { await MessageAsync(T("Neutral line"), T("Enter a grade between -100 and 100.")); return; }
        await CreateDesignVariantAsync(grade);
    }

    private async Task CreateDesignVariantAsync(decimal grade)
    {
        if (_designSession == null || _designBusy || !DesignSourceValid()) return;
        if (grade < -100 || grade > 100) throw new ArgumentOutOfRangeException(nameof(grade));
        var start = _designSession.Vertices[0]; var source = _designSource; var target = _designTarget;
        CaptureActiveDesign(); ClearDesign(keepDraft: true);
        _view.SetActiveLayer(source!); _designGrade.Value = grade; _designTarget = target;
        SetPathTool(PathTool.NeutralLine); await StartDesignAsync(start.X, start.Y);
    }

    private async Task RestoreDesignDraftAsync(NeutralDesignDraft draft)
    {
        if (_designBusy) return;
        CaptureActiveDesign(); ClearDesign(keepDraft: true);
        if (!_designDraftSources.TryGetValue(draft, out var source) || !_view.Layers.Contains(source))
        {
            source = _view.ActiveLayer;
            if (source == null || source.IsFrame) { _designStatus.Text = T("Source unavailable. Stored geometry is retained; restore its terrain source to continue."); return; }
        }
        _view.SetActiveLayer(source); _view.SetActiveBand(draft.Band);
        if (source.ActiveBand != draft.Band) { _designStatus.Text = T("Terrain changed. The stored trial is retained but cannot be resumed on different terrain."); return; }
        var document = source.Document;
        long generation = ++_designGeneration;
        _designBusy = true; UpdateDesignControls();
        try
        {
            var loaded = await NeutralSourceAsync();
            if (loaded == null || !ReferenceEquals(loaded.Value.Source, source)) return;
            var signature = await Task.Run(() => DesignSourceSignature(loaded.Value.Raster, source));
            if (generation != _designGeneration || !ReferenceEquals(_view.ActiveLayer, source) || !ReferenceEquals(document, source.Document) || source.ActiveBand != draft.Band) return;
            if (signature != draft.SourceSignature) { _designStatus.Text = T("Terrain changed. The stored trial is retained but cannot be resumed on different terrain."); return; }
            _designDraftSources[draft] = source;
            _designPaused = true;
            _designSession = NeutralDesignSession.Restore(new WorldSurfaceSampler(SurfaceSampler.BSpline(loaded.Value.Raster), source.Document.GeoReference), draft);
            _activeDesignDraft = draft; _designSource = source; _designDocument = source.Document; _designBand = draft.Band;
            _designTarget = draft.Target; _designStep.Value = (decimal)draft.Step; _designGrade.Value = (decimal)draft.Record.GradePercent;
            if (_designTarget != null) _designRadius.Value = (decimal)_designTarget.Radius;
            _designStatus.Text = T("Trial paused. Your working line is retained; resume to continue.");
            ShowDesignPreview(); ShowDesignTarget(); RebuildDesignVariants();
        }
        finally { if (generation == _designGeneration) { _designBusy = false; UpdateDesignControls(); } }
    }

    private void RebuildDesignVariants()
    {
        _designVariants.Children.Clear();
        foreach (var draft in _designDrafts)
        {
            var end = draft.Record.Vertices[draft.AcceptedSegments];
            var visible = new CheckBox { Content = draft.Name, IsChecked = draft.Visible };
            visible.IsCheckedChanged += (_, _) =>
            {
                draft.Visible = visible.IsChecked == true;
                foreach (var layer in _designOfflineLayers.Where(l => ReferenceEquals(l.DesignData, draft))) _view.SetVectorLayerVisible(layer, draft.Visible);
                ShowDraftOverlays(); ShowDesignOverlay();
            };
            var summary = new TextBlock
            {
                Text = L.F("{0:0.###}% · {1:0.##} m · deviation {2:0.###} m · warnings {3}", draft.Record.GradePercent, end[3],
                draft.Record.MaximumDeviations.Take(draft.AcceptedSegments).DefaultIfEmpty().Max(), draft.Record.Warnings.Take(draft.AcceptedSegments).Count(w => w != NeutralDesignWarning.None)),
                TextWrapping = TextWrapping.Wrap
            };
            if (draft.Target != null) summary.Text += "\n" + L.F("Distance to target: {0:0.##} m", draft.Target.Distance(end[0], end[1]));
            var select = new Button { Content = T("Open trial") }; select.Click += async (_, _) => { try { await RestoreDesignDraftAsync(draft); } catch (Exception ex) { await MessageAsync(T("Neutral line"), ex.Message); } };
            var rename = new Button { Content = T("Rename…") }; rename.Click += async (_, _) => { var name = await AskTextAsync(T("New variant"), T("Name"), draft.Name); if (!string.IsNullOrWhiteSpace(name)) { draft.Name = name.Trim(); RebuildDesignVariants(); } };
            var discard = new Button { Content = T("Discard trial") };
            discard.Click += async (_, _) =>
            {
                if (_designBusy || !await ConfirmAsync(T("Neutral line"), draft.Name + "\n" + T("Discard the current working line?"), T("Discard trial"))) return;
                if (ReferenceEquals(draft, _activeDesignDraft)) ClearDesign();
                else { _designDrafts.Remove(draft); _designDraftSources.Remove(draft); }
                foreach (var layer in _designOfflineLayers.Where(l => ReferenceEquals(l.DesignData, draft)).ToArray())
                { _view.RemoveVectorLayer(_view.VectorLayers.ToList().IndexOf(layer)); _designOfflineLayers.Remove(layer); }
                RebuildDesignVariants();
            };
            _designVariants.Children.Add(new StackPanel { Spacing = 3, Children = { visible, summary, new WrapPanel { Children = { select, rename, discard } } } });
        }
        ShowDraftOverlays();
    }

    private void ShowDraftOverlays() => _view.SetNeutralDesignVariants(_designDrafts.Where(d => d.Visible && !ReferenceEquals(d, _activeDesignDraft))
        .Select(d => d.Record.Vertices.Take(d.AcceptedSegments + 1).Select(v => (v[0], v[1])).ToArray()).ToArray());

    private async Task ReviewExistingDesignAsync()
    {
        StopContinuousDesign(); PauseDesign();
        var lines = _view.DrawOrder.OfType<VectorLayer>().SelectMany(l => l.Document.Objects.OfType<VectorPolyline>().Select(p => (Layer: l, Line: p))).ToArray();
        if (lines.Length == 0) { await MessageAsync(T("Neutral line"), T("Add a vector polyline to review.")); return; }
        var choices = new ListBox { ItemsSource = lines.Select((p, i) => $"{i + 1}: {p.Layer.Name} · {p.Line.Points.Count}").ToArray(), SelectedIndex = 0, MaxHeight = 320 };
        int index = await Dialogs.ShowAsync(this, T("Review existing line"), choices, Dialogs.Primary(T("Review")), () => choices.SelectedIndex, -1, width: 520);
        if (index < 0) return;
        var item = lines[index]; var loaded = await NeutralSourceAsync(); if (loaded == null) return;
        var space = loaded.Value.Source.Document.Header.CoordinateSpace; var vectorSpace = item.Layer.Document.Header.CoordinateSpace;
        if (space.Projection != vectorSpace.Projection || space.Datum != vectorSpace.Datum)
        { await MessageAsync(T("Neutral line"), T("The line and terrain must use the same coordinate system.")); return; }
        double? grade = item.Layer.DesignData?.Record.GradePercent ?? (item.Layer.Recipe?.Operation == "neutral-design" ? NeutralDesignRecord.FromJson(item.Layer.Recipe.Parameters["design"]).GradePercent : null);
        var surface = new WorldSurfaceSampler(SurfaceSampler.BSpline(loaded.Value.Raster), loaded.Value.Source.Document.GeoReference);
        var points = item.Line.Points.ToArray();
        long generation = ++_designGeneration;
        _designCts?.Cancel(); _designCts?.Dispose();
        var cts = _designCts = new System.Threading.CancellationTokenSource();
        _designBusy = true; UpdateDesignControls();
        IReadOnlyList<NeutralSegmentAssessment> checks;
        try
        {
            checks = await Task.Run(() => NeutralDesignSession.Review(surface, points, grade, cts.Token), cts.Token);
            if (generation != _designGeneration || cts.IsCancellationRequested || !ReferenceEquals(loaded.Value.Source, _view.ActiveLayer)) return;
        }
        catch (OperationCanceledException) { return; }
        finally { if (generation == _designGeneration) { _designBusy = false; UpdateDesignControls(); } }
        _designReviewResults.Children.Clear();
        _view.SetNeutralReviewSegment(null);
        double reference = checks[0].ReferenceGradePercent;
        _designReviewResults.Children.Add(new TextBlock { Text = L.F("Reference grade: {0:0.###}%", reference), TextWrapping = TextWrapping.Wrap });
        if (Math.Abs(reference) < 0.5 || Math.Abs(reference) > 8) _designReviewResults.Children.Add(new TextBlock { Text = T("The grade is outside the 0.5–8% guidance range."), TextWrapping = TextWrapping.Wrap });
        _designReviewResults.Children.Add(new TextBlock { Text = T("Sampled checks only; these observations are not an engineering compliance certificate."), TextWrapping = TextWrapping.Wrap });
        for (int i = 0; i < checks.Count; i++)
        {
            var check = checks[i]; var sample = check.Profile[check.Profile.Count / 2];
            var label = L.F("Segment {0}: {1} · deviation {2:0.###} m", i + 1, check.IsValid ? (check.Warnings == NeutralDesignWarning.None ? T("No warnings") : DesignWarningText(check.Warnings)) : T("Incomplete terrain"), check.MaximumDeviation);
            double segmentLength = check.End.Distance - check.Profile[0].Distance;
            if (segmentLength > 20.01) label += "\n" + T("Long step: consider 20 m or less for closer terrain following.");
            var row = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(row, label); row.Click += (_, _) =>
            {
                _view.SetProfileHover(sample);
                _view.SetNeutralReviewSegment((check.Profile[0].X, check.Profile[0].Y, check.End.X, check.End.Y));
                _view.SetViewState(sample.X, sample.Y, _view.GetViewState()?.WorldPerPixel ?? 1);
            };
            _designReviewResults.Children.Add(row);
        }
        var samples = checks.SelectMany((c, i) => i == 0 ? c.Profile : c.Profile.Skip(1)).ToArray();
        var series = new[] { new ProfileSeries(T("Terrain profile"), samples, SeriesColors[0]) };
        var chart = new ProfileChartControl { Height = 180 };
        chart.SetSeries(series, "m", "m"); chart.HoverChanged += (_, e) => _view.SetProfileHover(e.Sample);
        _designReviewResults.Children.Add(chart);
        var export = new Button { Content = "CSV…" };
        export.Click += async (_, _) => await ProfileWindow.ExportCsvAsync(this, series, "m");
        _designReviewResults.Children.Add(export);
        _designReviewResults.BringIntoView();
    }
}
