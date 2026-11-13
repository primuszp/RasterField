using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RasterField.Interpolation;
using RasterField.Rasters;
using RasterField.Vectors;
using static RasterField.L;

namespace RasterField
{
    public sealed partial class MainWindow
    {
        private readonly TabControl _neutralWorkspace = new();
        private readonly NumericUpDown _designGrade = new() { Value = 5, Minimum = -100, Maximum = 100, Increment = 0.1m, FormatString = "0.##", Width = 100 };
        private readonly NumericUpDown _designStep = new() { Value = 20, Minimum = 0.1m, Maximum = 10000, Increment = 1, FormatString = "0.##", Width = 100 };
        private readonly TextBlock _designSourceText = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextSecondary };
        private readonly TextBlock _designStatus = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _designPreviewText = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _designStats = new() { TextWrapping = TextWrapping.Wrap };
        private readonly Button _designStart = new();
        private readonly Button _designPause = new();
        private readonly Button _designUndo = new();
        private readonly Button _designRedo = new();
        private readonly Button _designFinish = new();
        private readonly Button _designAcceptWarning = new();
        private readonly Button _designSmallerStep = new();
        private readonly Button _designDiscard = new();
        private readonly ProgressBar _designProgress = new() { Height = 3, IsIndeterminate = true, IsVisible = false };
        private readonly ProfileChartControl _designChart = new() { Height = 180 };
        private readonly StackPanel _designSetup = new() { Spacing = 6 };
        private readonly StackPanel _designFooter = new() { Spacing = 6, Margin = new Thickness(10) };
        private readonly Button _designCsv = new() { Content = "CSV…" };
        private NeutralDesignSession? _designSession;
        private RasterLayer? _designSource;
        private ErsDocument? _designDocument;
        private int _designBand;
        private CancellationTokenSource? _designCts;
        private long _designGeneration;
        private bool _designBusy, _designPaused, _designPickingStart;
        private NeutralCandidateSearch? _designSearch;
        private NeutralSegmentAssessment[] _designAssessments = Array.Empty<NeutralSegmentAssessment>();
        private NeutralCandidate[] _designCandidates = Array.Empty<NeutralCandidate>();
        private int _designSelected = -1;
        private NeutralSegmentAssessment? _designPendingWarning;
        private ProfileSeries[] _designSeries = Array.Empty<ProfileSeries>();
        private bool InteractiveNeutral => _neutralWorkspace.SelectedIndex == 0;
        private bool HasUnfinishedDesign => DesignsDirty();

        private TabItem BuildNeutralLineTab()
        {
            // Initialize legacy recipe controls for compatibility, without exposing the old workflow.
            BuildAutomaticNeutralLineTab();
            var body = new StackPanel { Spacing = 10, Margin = new Thickness(10) };
            Grid Row(string label, Control control)
            {
                Grid.SetColumn(control, 1);
                return new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, control }
                };
            }
            body.Children.Add(_designSourceText);
            _designSetup.Children.Add(Row(T("Signed grade (%)"), _designGrade));
            _designSetup.Children.Add(new TextBlock
            {
                Text = T("Positive grade rises; negative grade falls. The grade stays fixed during a trial."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = AppTheme.TextSecondary,
                FontSize = AppTheme.FontCaption
            });
            body.Children.Add(_designSetup);
            body.Children.Add(Row(T("Step length (m)"), _designStep));
            _designStart.Content = T("Pick the start point on the map");
            _designStart.Classes.Add("accent");
            _designStart.Click += (_, _) =>
            {
                _designPickingStart = true;
                SetPathTool(PathTool.NeutralLine);
                _designStatus.Text = T("Click a start point with a valid terrain neighborhood.");
                UpdateDesignControls();
            };
            body.Children.Add(_designStart);
            body.Children.Add(_designProgress);
            var callout = new Border { Child = _designStatus, Padding = new Thickness(10) };
            callout.Classes.Add("callout");
            body.Children.Add(callout);
            body.Children.Add(_designStats);
            body.Children.Add(_designPreviewText);
            _designAcceptWarning.Content = T("Accept this segment with warnings");
            _designAcceptWarning.Click += async (_, _) => await AcceptDesignWarningAsync();
            _designSmallerStep.Content = T("Recalculate with half the step");
            _designSmallerStep.Click += (_, _) => _designStep.Value = Math.Max(0.1m, (_designStep.Value ?? 20) / 2);
            body.Children.Add(_designAcceptWarning);
            body.Children.Add(_designSmallerStep);
            _designChart.HoverChanged += (_, e) => _view.SetProfileHover(e.Sample);
            body.Children.Add(_designChart);
            _designCsv.Click += async (_, _) => { if (_designSeries.Length > 0) await ProfileWindow.ExportCsvAsync(this, _designSeries, "m"); };
            body.Children.Add(_designCsv);
            body.Children.Add(BuildDesignExtras());

            _designPause.Click += async (_, _) =>
            {
                if (_designPaused) { SetPathTool(PathTool.NeutralLine); await RefreshDesignCandidatesAsync(); }
                else { PauseDesign(); SetPathTool(PathTool.None); }
            };
            _designUndo.Content = T("Undo");
            _designRedo.Content = T("Redo");
            _designUndo.Click += async (_, _) => await ChangeDesignHistoryAsync(false);
            _designRedo.Click += async (_, _) => await ChangeDesignHistoryAsync(true);
            _designFinish.Content = T("Finish → new vector layer");
            _designFinish.Classes.Add("accent");
            _designFinish.Click += async (_, _) =>
            {
                if (_designSession == null || _designBusy) return;
                PauseDesign();
                string summary = L.F("{0} segments · {1:0.##} m · {2} accepted warnings", _designSession.Segments.Count, _designSession.End.Distance,
                    _designSession.Segments.Count(s => s.Warnings != NeutralDesignWarning.None));
                summary += "\n" + _designTargetText.Text + "\n" + T("Create a vector layer with these exact accepted vertices?");
                if (await ConfirmAsync(T("Review working line"), summary, T("Finish → new vector layer"))) FinishDesign();
            };
            _designDiscard.Content = T("Discard trial");
            _designDiscard.Click += async (_, _) =>
            {
                if (_designSession != null && _designSession.Segments.Count > 0 &&
                    !await ConfirmAsync(T("Neutral line"), T("Discard the current working line?"), T("Discard trial"))) return;
                ClearDesign();
                SetPathTool(PathTool.None);
            };
            _designFooter.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _designUndo, _designRedo, _designPause } });
            _designFooter.Children.Add(_designFinish);
            _designFooter.Children.Add(_designContinuous);
            body.Children.Add(_designDiscard);
            ToolTip.SetTip(_designFooter, T("Click: accept · ←/→: direction · Ctrl+Z/Y: undo/redo · Esc: pause"));
            var panel = new DockPanel();
            DockPanel.SetDock(_designFooter, Dock.Bottom);
            panel.Children.Add(_designFooter);
            panel.Children.Add(new ScrollViewer { Content = body });
            _neutralWorkspace.ItemsSource = new[] { new TabItem { Header = T("Interactive"), Content = panel } };
            _neutralWorkspace.SelectedIndex = 0;
            _neutralWorkspace.SelectionChanged += (_, e) =>
            {
                if (!ReferenceEquals(e.Source, _neutralWorkspace)) return;
                if (!InteractiveNeutral) { PauseDesign(); _designPickingStart = false; }
                SetPathTool(PathTool.None);
                UpdateDesignControls();
            };
            _designStep.ValueChanged += async (_, _) =>
            {
                if (_designSession == null || _designBusy || _designStep.Value == null) return;
                StopContinuousDesign();
                lock (_designSession) _designSession.SetStep((double)_designStep.Value.Value);
                _designPendingWarning = null;
                await RefreshDesignCandidatesAsync();
            };
            _view.NeutralDesignPointerMoved += (_, e) => { _designPointer = (e.WorldX, e.WorldY); SelectDesignDirection(e.WorldX, e.WorldY); };
            _view.NeutralDesignKey += async (_, e) =>
            {
                if (!InteractiveNeutral) return;
                bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
                if (command && e.Key is Key.Z or Key.Y)
                { e.Handled = true; await ChangeDesignHistoryAsync(e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)); }
                else if (e.Key == Key.Escape) { e.Handled = true; PauseDesign(); SetPathTool(PathTool.None); }
                else if (e.Key is Key.Left or Key.Right && !_designBusy && _designCandidates.Length > 0)
                {
                    e.Handled = true;
                    _designSelected = (_designSelected + (e.Key == Key.Right ? 1 : -1) + _designCandidates.Length) % _designCandidates.Length;
                    _designPendingWarning = null;
                    ShowDesignPreview();
                }
                else if (e.Key == Key.Enter) { e.Handled = true; await AcceptSelectedDesignAsync(); }
                else if (e.Key == Key.Back) { e.Handled = true; await ChangeDesignHistoryAsync(false); }
            };
            _view.LayersChanged += (_, _) =>
            {
                if (_designSession != null && !DesignSourceValid())
                { PauseDesign(); _designStatus.Text = T("Source layer changed. Restore the original layer and band to resume, or discard the trial."); }
                UpdateDesignControls();
            };
            _designStatus.Text = T("Set the grade and step, then choose a start point. Each click accepts one terrain-guided segment.");
            UpdateDesignControls();
            return new TabItem { Header = T("Neutral line"), Content = _neutralWorkspace };
        }

        private bool DesignSourceValid() => _designSource != null && _view.Layers.Contains(_designSource) &&
            ReferenceEquals(_view.ActiveLayer, _designSource) && ReferenceEquals(_designSource.Document, _designDocument) &&
            _designSource.ActiveBand == _designBand;

        private void OnDesignToolChanging(PathTool tool)
        {
            if (!InteractiveNeutral || tool != PathTool.NeutralLine)
            {
                if (!_designPaused && (_designSession != null || _designPickingStart || _designBusy)) PauseDesign();
                _view.NeutralDesignActive = false; _designPickingStart = false;
            }
            else
            {
                if (_designSession != null && !DesignSourceValid()) { PauseDesign(); return; }
                bool resume = _designPaused && _designSession != null;
                if (_designSession == null) _designPickingStart = true;
                _designPaused = false;
                _view.NeutralDesignActive = true;
                _view.NeutralGuideActive = false;
                if (resume)
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                    {
                        if (InteractiveNeutral && !_designPaused && !_designBusy && _view.PathToolMode == PathTool.NeutralLine)
                            await RefreshDesignCandidatesAsync();
                    });
            }
        }

        private async Task DispatchNeutralPointAsync(double x, double y)
        {
            if (!InteractiveNeutral) { await HandleNeutralPointAsync(x, y); return; }
            if (_designBusy || _designPaused) return;
            if (_designPickingTarget) { SetDesignTarget(x, y); return; }
            if (_designSession == null && _designPickingStart) { await StartDesignAsync(x, y); return; }
            if (_designSession == null || !DesignSourceValid()) return;
            if (_designSearch?.SampledLevelCircle == true)
            {
                var session = _designSession;
                double heading = Math.Atan2(y - session.End.Y, x - session.End.X);
                var candidate = NeutralCandidateSolver.AtHeading(session.Surface, session.End.X, session.End.Y, session.Step, session.NextElevation, heading);
                if (!candidate.HasValue) return;
                // A sampled level circle is not assumed valid between discovery directions.
                var assessment = session.Assess(candidate.Value);
                _designCandidates = new[] { candidate.Value };
                _designAssessments = new[] { assessment };
                _designSelected = 0;
            }
            else SelectDesignDirection(x, y);
            await AcceptSelectedDesignAsync();
        }

        private async Task StartDesignAsync(double x, double y)
        {
            if (_designSession != null || _designBusy) return;
            long generation = ++_designGeneration;
            _designBusy = true;
            UpdateDesignControls();
            try
            {
                var loaded = await NeutralSourceAsync();
                if (loaded == null || generation != _designGeneration || !InteractiveNeutral) return;
                var source = loaded.Value.Source;
                var document = source.Document;
                int band = source.ActiveBand;
                string signature = await Task.Run(() => DesignSourceSignature(loaded.Value.Raster, source));
                if (generation != _designGeneration || !ReferenceEquals(_view.ActiveLayer, source) || !ReferenceEquals(source.Document, document) || source.ActiveBand != band) return;
                var surface = new WorldSurfaceSampler(SurfaceSampler.BSpline(loaded.Value.Raster), source.Document.GeoReference);
                _designSession = new NeutralDesignSession(surface, x, y, (double)(_designGrade.Value ?? 5), (double)(_designStep.Value ?? 20));
                _designSource = source; _designDocument = source.Document; _designBand = source.ActiveBand;
                RegisterDesignDraft(signature);
                if (_designTarget != null) _designTarget.Elevation = surface.Sample(_designTarget.X, _designTarget.Y).Value;
                _designPickingStart = false; _designPaused = false;
                _view.NeutralDesignActive = true;
                _view.ClearPath();
                _designBusy = false;
                await RefreshDesignCandidatesAsync();
            }
            catch (Exception ex) { if (generation == _designGeneration) _designStatus.Text = T("Cannot start the trial: ") + ex.Message; }
            finally { if (generation == _designGeneration) { _designBusy = false; UpdateDesignControls(); } }
        }

        private async Task RefreshDesignCandidatesAsync()
        {
            var session = _designSession;
            if (session == null || _designPaused || !DesignSourceValid()) { UpdateDesignControls(); return; }
            _designCts?.Cancel();
            _designCts?.Dispose();
            var cts = _designCts = new CancellationTokenSource();
            var token = cts.Token;
            long generation = ++_designGeneration;
            _designBusy = true;
            _designPendingWarning = null;
            _designSearch = null;
            _designCandidates = Array.Empty<NeutralCandidate>(); _designAssessments = Array.Empty<NeutralSegmentAssessment>();
            _designSelected = -1;
            _designStatus.Text = T("Finding the next terrain-guided directions…");
            UpdateDesignControls();
            ShowDesignOverlay();
            try
            {
                var computed = await Task.Run(() =>
                {
                    lock (session)
                    {
                        token.ThrowIfCancellationRequested();
                        var search = session.FindCandidates(token);
                        var checks = search.Candidates.Select(c => session.Assess(c, token)).ToArray();
                        return (Search: search, Checks: checks);
                    }
                }, token);
                if (generation != _designGeneration || token.IsCancellationRequested || !ReferenceEquals(session, _designSession) || !DesignSourceValid()) return;
                _designSearch = computed.Search;
                _designCandidates = computed.Search.Candidates.ToArray();
                _designAssessments = computed.Checks;
                _designSelected = _designCandidates.Length > 0 ? 0 : -1;
                _designStatus.Text = computed.Search.SampledLevelCircle
                    ? T("The sampled circle is level. Click a direction; that exact segment will be checked before acceptance.")
                    : _designCandidates.Length == 0
                        ? T("No next point found with these settings. Change the step length or undo a segment.")
                        : L.F("{0} directions found. Point toward a direction, then click to accept.", _designCandidates.Length);
                if (_neutralUnitNote != null) _designStatus.Text += "\n" + _neutralUnitNote;
                ShowDesignPreview();
                CaptureActiveDesign(); RebuildDesignVariants(); ShowDesignTarget();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (generation == _designGeneration) _designStatus.Text = ex.Message; }
            finally { if (generation == _designGeneration) { _designBusy = false; UpdateDesignControls(); } }
        }

        private void SelectDesignDirection(double x, double y)
        {
            if (_designBusy || _designPaused || _designSession == null || _designCandidates.Length == 0 || _designSearch?.SampledLevelCircle == true) return;
            var end = _designSession.End;
            if (Math.Abs(x - end.X) + Math.Abs(y - end.Y) < _designSession.Step * 0.05) return;
            double heading = Math.Atan2(y - end.Y, x - end.X);
            int best = 0;
            for (int i = 1; i < _designCandidates.Length; i++)
                if (NeutralCandidateSolver.AngleDistance(heading, _designCandidates[i].Heading) < NeutralCandidateSolver.AngleDistance(heading, _designCandidates[best].Heading)) best = i;
            if (_designSelected >= 0 && best != _designSelected &&
                NeutralCandidateSolver.AngleDistance(heading, _designCandidates[_designSelected].Heading) <
                NeutralCandidateSolver.AngleDistance(heading, _designCandidates[best].Heading) + 3 * Math.PI / 180) return;
            if (_designSelected == best) return;
            _designSelected = best;
            _designPendingWarning = null;
            ShowDesignPreview();
        }

        private async Task AcceptSelectedDesignAsync()
        {
            if (_designBusy || _designPaused || !DesignSourceValid() || _designSession == null || _designSelected < 0 || _designSelected >= _designAssessments.Length) return;
            var assessment = _designAssessments[_designSelected];
            if (!assessment.IsValid) { ShowDesignPreview(); return; }
            if (assessment.Warnings != NeutralDesignWarning.None)
            {
                StopContinuousDesign();
                _designPendingWarning = assessment;
                ShowDesignPreview();
                UpdateDesignControls();
                return;
            }
            bool accepted;
            lock (_designSession) accepted = _designSession.Accept(assessment);
            if (accepted) await RefreshDesignCandidatesAsync();
        }

        private async Task AcceptDesignWarningAsync()
        {
            if (_designBusy || _designPaused || !DesignSourceValid() || _designSession == null || _designPendingWarning == null) return;
            bool accepted;
            lock (_designSession) accepted = _designSession.Accept(_designPendingWarning, acceptWarnings: true);
            _designPendingWarning = null;
            if (accepted) await RefreshDesignCandidatesAsync();
        }

        private async Task ChangeDesignHistoryAsync(bool redo)
        {
            StopContinuousDesign();
            if (_designBusy || _designSession == null || !DesignSourceValid()) return;
            lock (_designSession) { if (redo) _designSession.Redo(); else _designSession.Undo(); }
            _designPendingWarning = null;
            ShowDesignPreview();
            await RefreshDesignCandidatesAsync();
        }

        private void PauseDesign()
        {
            StopContinuousDesign(); _designPickingTarget = false;
            _designCts?.Cancel(); _designGeneration++;
            _designBusy = false; _designPaused = true;
            _view.NeutralDesignActive = false;
            _designPickingStart = false;
            _designPendingWarning = null;
            if (_designSession != null) _designStatus.Text = T("Trial paused. Your working line is retained; resume to continue.");
            UpdateDesignControls();
            if (_designSession != null) ShowDesignPreview();
            else ShowDesignOverlay();
        }

        private void ClearDesign(bool keepDraft = false)
        {
            PauseDesign();
            if (!keepDraft && _activeDesignDraft != null) { _designDrafts.Remove(_activeDesignDraft); _designDraftSources.Remove(_activeDesignDraft); }
            _activeDesignDraft = null; _designTarget = null; ShowDesignTarget();
            _designSession = null; _designSource = null; _designDocument = null;
            _designSearch = null; _designCandidates = Array.Empty<NeutralCandidate>(); _designAssessments = Array.Empty<NeutralSegmentAssessment>();
            _designSelected = -1; _designPaused = false;
            _designSeries = Array.Empty<ProfileSeries>();
            _designChart.SetSeries(_designSeries, "m", "m");
            _designStats.Text = ""; _designPreviewText.Text = "";
            _designStatus.Text = T("Set the grade and step, then choose a start point. Each click accepts one terrain-guided segment.");
            _view.SetProfileHover(null);
            ShowDesignOverlay(); UpdateDesignControls();
            RebuildDesignVariants();
        }

        private void FinishDesign()
        {
            if (_designBusy || _designSession == null || _designSession.Segments.Count == 0 || !DesignSourceValid()) return;
            NeutralDesignRecord record;
            lock (_designSession) record = NeutralDesignRecord.Capture(_designSession);
            var source = _designSource!;
            var recipe = new LayerRecipe("neutral-design", source, new Dictionary<string, string>
            { ["design"] = record.ToJson(), ["band"] = LayerRecipe.Num(_designBand) });
            var result = NeutralDesignRecipeResult(source, recipe);
            CaptureActiveDesign();
            var stored = _activeDesignDraft;
            ClearDesign();
            SetPathTool(PathTool.None);
            var layer = (VectorLayer)AddRecipeResult(recipe, result);
            layer.LineWidth = 2.5;
            layer.DesignData = stored;
            _designStatus.Text = T("Working line saved as a vector layer. Save the project or export the layer to keep it on disk.");
        }

        private static RecipeResult NeutralDesignRecipeResult(RasterLayer source, LayerRecipe recipe)
        {
            var record = NeutralDesignRecord.FromJson(recipe.Parameters["design"]);
            var space = source.Document.Header.CoordinateSpace;
            var doc = ErvDocument.Create(space.Projection, space.Datum);
            doc.Header.CoordinateSpace.Units = space.Units;
            doc.Header.CoordinateSpace.CoordinateType = space.CoordinateType;
            var line = new VectorPolyline { Attribute = L.F("Interactive neutral line · {0:0.###}%", record.GradePercent) };
            foreach (var p in record.Vertices) line.Points.Add((p[0], p[1]));
            doc.Objects.Add(line);
            return new RecipeResult(null, doc, null, null, L.F("{0} · designed neutral line", source.Name),
                L.F("{0} segments · {1:0.##} m · {2} accepted warnings", record.Warnings.Length, record.Vertices[^1][3], record.Warnings.Count(w => w != NeutralDesignWarning.None)), SeriesColors[0]);
        }

        private void ShowDesignOverlay() => _view.SetNeutralDesignOverlay(_activeDesignDraft?.Visible == false ? Array.Empty<NeutralDesignVertex>() : _designSession?.Vertices.ToArray() ?? Array.Empty<NeutralDesignVertex>(),
            _designCandidates, _designSelected, _designSession?.Step ?? 0);

        private void ShowDesignPreview()
        {
            var session = _designSession;
            if (session == null) return;
            var preview = !_designPaused && _designSelected >= 0 && _designSelected < _designAssessments.Length ? _designAssessments[_designSelected] : null;
            _designStats.Text = L.F("{0} segments · {1:0.##} m · {2} accepted warnings", session.Segments.Count, session.End.Distance,
                session.Segments.Count(s => s.Warnings != NeutralDesignWarning.None));
            if (_designTarget != null) _designStats.Text += "\n" + L.F("Distance to target: {0:0.##} m", _designTarget.Distance(session.End.X, session.End.Y));
            _designPreviewText.Text = preview == null ? "" : !preview.IsValid
                ? T("This segment cannot be accepted: its terrain is incomplete or the checking limit was reached.")
                : L.F("Next segment: {0:0.##} m · turn {1:0.#}° · sampled terrain deviation {2:0.###} m", session.Step, preview.TurnDegrees, preview.MaximumDeviation);
            if (preview?.Warnings != NeutralDesignWarning.None && preview != null)
                _designPreviewText.Text += "\n" + DesignWarningText(preview.Warnings);
            if (Math.Abs(session.GradePercent) < 0.5 || Math.Abs(session.GradePercent) > 8)
                _designPreviewText.Text += "\n" + T("The grade is outside the 0.5–8% guidance range.");
            if (session.Step > 20.01) _designPreviewText.Text += "\n" + T("Long step: consider 20 m or less for closer terrain following.");
            var samples = session.Profile().ToList();
            if (preview != null) samples.AddRange(preview.Profile.Skip(1));
            double z0 = session.Vertices[0].Elevation;
            _designSeries = new[]
            {
                new ProfileSeries(T("Terrain profile"), samples, SeriesColors[0]),
                new ProfileSeries(L.F("Target · {0:0.###}%", session.GradePercent), samples.Select(p =>
                    new ProfileSample(p.Distance, p.X, p.Y, (float)(z0 + p.Distance * session.GradePercent / 100))).ToArray(), Colors.OrangeRed, dashed: true)
            };
            _designChart.SetSeries(_designSeries, "m", "m");
            ShowDesignOverlay(); UpdateDesignControls();
        }

        private static string DesignWarningText(NeutralDesignWarning warnings)
        {
            var messages = new List<string>();
            if (warnings.HasFlag(NeutralDesignWarning.TerrainDeviation)) messages.Add(T("The sampled terrain deviation exceeds 0.50 m. Try a shorter step."));
            if (warnings.HasFlag(NeutralDesignWarning.SharpTurn)) messages.Add(T("Sharp direction change."));
            if (warnings.HasFlag(NeutralDesignWarning.ReverseTurn)) messages.Add(T("The segment turns back."));
            if (warnings.HasFlag(NeutralDesignWarning.Zigzag)) messages.Add(T("Alternating sharp turns form a zigzag."));
            if (warnings.HasFlag(NeutralDesignWarning.SelfIntersection)) messages.Add(T("The segment intersects the existing working line."));
            if (warnings.HasFlag(NeutralDesignWarning.GradeDeviation)) messages.Add(T("Segment grade differs from the reference by more than 0.25 percentage points."));
            return string.Join("\n", messages);
        }

        private void UpdateDesignControls()
        {
            bool exists = _designSession != null, valid = !exists || DesignSourceValid();
            _designSetup.IsVisible = !exists;
            _designFooter.IsVisible = exists;
            _designChart.IsVisible = exists;
            _designCsv.IsVisible = exists;
            _designContinuous.IsVisible = exists;
            _designContinuous.IsEnabled = exists && valid && !_designPaused;
            _designGrade.IsEnabled = !exists && !_designBusy;
            _designStep.IsEnabled = !_designBusy && valid;
            _designStart.IsVisible = !exists;
            _designStart.IsEnabled = !_designBusy;
            _designPause.IsVisible = exists;
            _designPause.Content = T(_designPaused ? "Resume" : "Pause");
            _designPause.IsEnabled = valid;
            _designUndo.IsEnabled = exists && !_designBusy && valid && _designSession!.Segments.Count > 0;
            _designRedo.IsEnabled = exists && !_designBusy && valid && _designSession!.CanRedo;
            _designFinish.IsEnabled = _designUndo.IsEnabled;
            _designDiscard.IsVisible = exists || _designPickingStart;
            _designProgress.IsVisible = _designBusy;
            _designAcceptWarning.IsVisible = _designPendingWarning != null && !_designPaused && !_designBusy;
            _designSmallerStep.IsVisible = exists && !_designBusy && valid;
            _designSourceText.Text = L.F("Terrain: {0} · band {1} · cubic B-spline", _designSource?.Name ?? _view.ActiveLayer?.Name ?? "—", (exists ? _designBand : _view.ActiveLayer?.ActiveBand ?? 0) + 1);
        }
    }
}
