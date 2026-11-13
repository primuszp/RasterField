using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using RasterField.Rasters;
using RasterField.Vectors;
using Xunit;

namespace RasterField.Tests;

[Collection("Avalonia")]
public sealed class NeutralDesignUiTests
{
    private readonly HeadlessUnitTestSession _session;
    public NeutralDesignUiTests(AvaloniaTestFixture fixture) => _session = fixture.Session;
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static Task Call(MainWindow window, string name, params object[] args) => (Task)Invoke(window, name, args)!;

    private static async Task Idle(MainWindow window)
    {
        for (int i = 0; i < 200 && Field<bool>(window, "_designBusy"); i++) await Task.Delay(10);
        Assert.False(Field<bool>(window, "_designBusy"));
    }

    private static async Task WithTerrain(Func<MainWindow, RasterView, string, Task> action, bool flat = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "neutral-design-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settings = AppSettings.SettingsPath();
        byte[]? prior = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        try
        {
            var raster = new Raster(120, 120);
            for (int y = 0; y < 120; y++)
                for (int x = 0; x < 120; x++) raster[y, x] = (float)(100 + (flat ? 0 : 0.125 * (x + 0.5)));
            string path = Path.Combine(directory, "terrain.ers");
            ErsDocument.Create(raster, 0, 120, 1, 1).Save(path);
            using var window = new MainWindow();
            var view = Field<RasterView>(window, "_view");
            view.AddLayerFromPath(path);
            Invoke(window, "SetPathTool", PathTool.NeutralLine);
            await action(window, view, directory);
        }
        finally
        {
            if (prior != null) File.WriteAllBytes(settings, prior);
            else if (File.Exists(settings)) File.Delete(settings);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public Task Interactive_click_history_profile_finish_and_project_round_trip() => _session.Dispatch(() =>
        WithTerrain(async (window, view, directory) =>
        {
            Assert.Equal(0, Field<TabControl>(window, "_neutralWorkspace").SelectedIndex);
            await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
            var session = Field<NeutralDesignSession>(window, "_designSession");
            var first = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
            await Call(window, "DispatchNeutralPointAsync", first.X, first.Y);
            Assert.Equal(2, session.Vertices.Count);
            Assert.Empty(view.DrawOrder.OfType<VectorLayer>());
            Assert.NotEmpty((System.Collections.Generic.List<string>)Invoke(window, "UnsavedLayerNames")!);
            Assert.False(Field<NumericUpDown>(window, "_designGrade").IsEnabled);

            await Call(window, "ChangeDesignHistoryAsync", false);
            Assert.Single(session.Vertices);
            // Undo history also contains work that would be lost on close.
            Assert.NotEmpty((System.Collections.Generic.List<string>)Invoke(window, "UnsavedLayerNames")!);
            await Call(window, "ChangeDesignHistoryAsync", true);
            Assert.Equal(2, session.Vertices.Count);
            Field<NumericUpDown>(window, "_designStep").Value = 7;
            await Idle(window);
            Assert.Equal(7, session.Step);
            var next = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
            await Call(window, "DispatchNeutralPointAsync", next.X, next.Y);
            Assert.Equal(3, session.Vertices.Count);
            Assert.Equal(27, session.End.Distance, 6);
            Assert.Equal(2, Field<ProfileSeries[]>(window, "_designSeries").Length);

            var vertices = session.Vertices.Select(p => (p.X, p.Y)).ToArray();
            Invoke(window, "FinishDesign");
            var layer = Assert.Single(view.DrawOrder.OfType<VectorLayer>());
            Assert.Equal("neutral-design", layer.Recipe!.Operation);
            var record = NeutralDesignRecord.FromJson(layer.Recipe.Parameters["design"]);
            Assert.Equal(3, record.Vertices.Length);
            Assert.Equal(vertices, Assert.IsType<VectorPolyline>(Assert.Single(layer.Document.Objects)).Points);

            string project = Path.Combine(directory, "design.rfproj");
            typeof(MainWindow).GetField("_projectPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, project);
            await Call(window, "SaveProjectAsync", false);
            Assert.Empty((System.Collections.Generic.List<string>)Invoke(window, "UnsavedLayerNames")!);
            using var restored = new MainWindow();
            await restored.LoadProjectAsync(project);
            var restoredLayer = Assert.Single(Field<RasterView>(restored, "_view").DrawOrder.OfType<VectorLayer>());
            Assert.Equal(vertices, Assert.IsType<VectorPolyline>(Assert.Single(restoredLayer.Document.Objects)).Points);
            Assert.Equal(record.ToJson(), NeutralDesignRecord.FromJson(restoredLayer.Recipe!.Parameters["design"]).ToJson());
        }), CancellationToken.None);

    [Fact]
    public Task Tool_switch_pause_and_resume_keep_the_working_line_and_refresh_stale_candidates() => _session.Dispatch(() =>
        WithTerrain(async (window, view, _) =>
        {
            await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
            var first = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
            await Call(window, "DispatchNeutralPointAsync", first.X, first.Y);
            var session = Field<NeutralDesignSession>(window, "_designSession");
            Invoke(window, "SetPathTool", PathTool.Measure);
            Assert.True(Field<bool>(window, "_designPaused"));
            Assert.False(view.NeutralDesignActive);
            Assert.Equal(2, session.Vertices.Count);
            Field<NumericUpDown>(window, "_designStep").Value = 6;
            Assert.Equal(6, session.Step);
            Invoke(window, "SetPathTool", PathTool.NeutralLine);
            await Call(window, "RefreshDesignCandidatesAsync");
            var next = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
            await Call(window, "DispatchNeutralPointAsync", next.X, next.Y);
            Assert.Equal(26, session.End.Distance, 6);
        }), CancellationToken.None);

    [Fact]
    public Task Flat_terrain_checks_the_clicked_direction_and_keeps_warning_acceptance_explicit() => _session.Dispatch(() =>
        WithTerrain(async (window, view, _) =>
        {
            Field<NumericUpDown>(window, "_designGrade").Value = 0;
            await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
            Assert.True(Field<NeutralCandidateSearch>(window, "_designSearch").SampledLevelCircle);
            await Call(window, "DispatchNeutralPointAsync", 80.0, 60.0);
            var session = Field<NeutralDesignSession>(window, "_designSession");
            Assert.Equal(70, session.End.X, 6);
            await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
            Assert.Single(session.Segments);
            Assert.True(Field<Button>(window, "_designAcceptWarning").IsVisible);
            await Call(window, "AcceptDesignWarningAsync");
            Assert.Equal(2, session.Segments.Count);
            Assert.True(session.Segments[^1].Warnings.HasFlag(NeutralDesignWarning.ReverseTurn));
            Assert.Empty(view.DrawOrder.OfType<VectorLayer>());
        }, flat: true), CancellationToken.None);

    [Fact]
    public Task Switching_sources_pauses_and_prevents_accepting_old_candidates() => _session.Dispatch(() =>
        WithTerrain(async (window, view, directory) =>
        {
            await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
            var session = Field<NeutralDesignSession>(window, "_designSession");
            var candidate = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
            view.AddLayerFromPath(Path.Combine(directory, "terrain.ers"));
            Assert.True(Field<bool>(window, "_designPaused"));
            await Call(window, "DispatchNeutralPointAsync", candidate.X, candidate.Y);
            Assert.Single(session.Vertices);
            Assert.False(Field<Button>(window, "_designFinish").IsEnabled);
        }), CancellationToken.None);

    [Fact]
    public Task Draft_project_preserves_redo_target_and_can_resume() => _session.Dispatch(() => WithTerrain(async (window, view, directory) =>
    {
        await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
        var first = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
        await Call(window, "DispatchNeutralPointAsync", first.X, first.Y);
        Invoke(window, "SetDesignTarget", first.X + 10, first.Y);
        await Call(window, "ChangeDesignHistoryAsync", false);
        string project = Path.Combine(directory, "draft.rfproj");
        typeof(MainWindow).GetField("_projectPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, project);
        await Call(window, "SaveProjectAsync", false);
        Assert.False((bool)Invoke(window, "DesignsDirty")!);
        using var restored = new MainWindow(); await restored.LoadProjectAsync(project);
        var draft = Assert.Single(Field<System.Collections.Generic.List<NeutralDesignDraft>>(restored, "_designDrafts"));
        Assert.NotNull(draft.Target); Assert.Equal(0, draft.AcceptedSegments);
        await Call(restored, "RestoreDesignDraftAsync", draft);
        Assert.True(Field<bool>(restored, "_designPaused"));
        var session = Field<NeutralDesignSession>(restored, "_designSession");
        Assert.True(session.CanRedo);
        await Call(restored, "ChangeDesignHistoryAsync", true);
        Assert.Equal(first.X, session.End.X); Assert.Equal(first.Y, session.End.Y);
        Assert.True((bool)Invoke(restored, "DesignsDirty")!);
    }), CancellationToken.None);

    [Fact]
    public Task Continuous_drawing_stops_at_target_and_warning_without_auto_acceptance() => _session.Dispatch(() => WithTerrain(async (window, view, _) =>
    {
        await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
        var candidate = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
        Invoke(window, "SetDesignTarget", candidate.X, candidate.Y);
        typeof(MainWindow).GetField("_designPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, (candidate.X + 10, candidate.Y + 25));
        typeof(MainWindow).GetField("_continuousRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        await Call(window, "ContinuousDesignTickAsync");
        Assert.Single(Field<NeutralDesignSession>(window, "_designSession").Segments);
        Assert.False(Field<bool>(window, "_continuousRunning"));
        Assert.True((bool)Invoke(window, "DesignTargetReached")!);
        Invoke(window, "SetPathTool", PathTool.Measure);
        Assert.False(Field<bool>(window, "_continuousRunning"));
    }), CancellationToken.None);

    [Fact]
    public Task Stored_geometry_loads_without_terrain_and_drafts_remain_in_project() => _session.Dispatch(() => WithTerrain(async (window, view, directory) =>
    {
        await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
        var candidate = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
        await Call(window, "DispatchNeutralPointAsync", candidate.X, candidate.Y);
        Invoke(window, "CaptureActiveDesign");
        var draft = Field<NeutralDesignDraft>(window, "_activeDesignDraft");
        var project = new RasterField.Projects.ProjectDocument();
        project.NeutralDesigns.Add(draft);
        project.Layers.Add(new RasterField.Projects.ProjectLayer { Kind = "vector", Name = "stored", DesignData = draft });
        string path = Path.Combine(directory, "offline.rfproj"); project.Save(path);
        using var restored = new MainWindow(); await restored.LoadProjectAsync(path);
        var restoredView = Field<RasterView>(restored, "_view");
        Assert.False(restoredView.HasRasterLayers);
        Assert.Equal(2, restoredView.DrawOrder.OfType<VectorLayer>().Count());
        Assert.All(restoredView.DrawOrder.OfType<VectorLayer>(), v => Assert.Equal((candidate.X, candidate.Y), Assert.IsType<VectorPolyline>(Assert.Single(v.Document.Objects)).Points[^1]));
        Assert.Single(Field<System.Collections.Generic.List<NeutralDesignDraft>>(restored, "_designDrafts"));
        await Call(restored, "SaveProjectAsync", false);
        var saved = RasterField.Projects.ProjectDocument.Load(path);
        Assert.Single(saved.NeutralDesigns); Assert.Single(saved.Layers);
        restoredView.AddLayerFromPath(Path.Combine(directory, "terrain.ers"));
        await Call(restored, "RestoreDesignDraftAsync", Field<System.Collections.Generic.List<NeutralDesignDraft>>(restored, "_designDrafts")[0]);
        Assert.Equal(2, Field<NeutralDesignSession>(restored, "_designSession").Vertices.Count);
    }), CancellationToken.None);

    [Fact]
    public Task Variants_keep_independent_geometry_grade_and_target_and_detect_changed_terrain() => _session.Dispatch(() => WithTerrain(async (window, view, directory) =>
    {
        await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
        var first = Field<NeutralCandidate[]>(window, "_designCandidates")[0];
        await Call(window, "DispatchNeutralPointAsync", first.X, first.Y);
        Invoke(window, "SetDesignTarget", first.X + 20, first.Y);
        await Call(window, "CreateDesignVariantAsync", 3m);
        var drafts = Field<System.Collections.Generic.List<NeutralDesignDraft>>(window, "_designDrafts");
        Assert.Equal(2, drafts.Count); Assert.Equal(5, drafts[0].Record.GradePercent); Assert.Equal(3, drafts[1].Record.GradePercent);
        Assert.Equal(drafts[0].Record.Vertices[0], drafts[1].Record.Vertices[0]);
        Field<NumericUpDown>(window, "_designRadius").Value = 4;
        Invoke(window, "CaptureActiveDesign");
        Assert.Equal(10, drafts[0].Target!.Radius); Assert.Equal(4, drafts[1].Target!.Radius);
        await Call(window, "RestoreDesignDraftAsync", drafts[0]);
        Assert.Equal(2, Field<NeutralDesignSession>(window, "_designSession").Vertices.Count);
        Assert.Equal(5, Field<NeutralDesignSession>(window, "_designSession").GradePercent);
        view.ActiveLayer!.Document.Band![0, 0] += 1; // Even a distant terrain edit invalidates the source signature.
        await Call(window, "RestoreDesignDraftAsync", drafts[1]);
        Assert.Null(Field<NeutralDesignSession?>(window, "_designSession"));
        Assert.Equal(2, drafts.Count);
    }), CancellationToken.None);

    [Fact]
    public Task Continuous_mode_stops_before_a_reverse_turn_and_does_not_accept_the_warning() => _session.Dispatch(() => WithTerrain(async (window, view, _) =>
    {
        Field<NumericUpDown>(window, "_designGrade").Value = 0;
        await Call(window, "DispatchNeutralPointAsync", 50.0, 60.0);
        await Call(window, "DispatchNeutralPointAsync", 80.0, 60.0);
        typeof(MainWindow).GetField("_designPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, (30.0, 60.0));
        typeof(MainWindow).GetField("_continuousRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        await Call(window, "ContinuousDesignTickAsync");
        Assert.False(Field<bool>(window, "_continuousRunning"));
        Assert.Single(Field<NeutralDesignSession>(window, "_designSession").Segments);
        Assert.True(Field<Button>(window, "_designAcceptWarning").IsVisible);
    }, flat: true), CancellationToken.None);
}
