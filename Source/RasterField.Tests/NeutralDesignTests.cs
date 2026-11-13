using System;
using System.Linq;
using System.Threading;
using RasterField.Interpolation;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests;

public sealed class NeutralDesignTests
{
    private static WorldSurfaceSampler Terrain(Func<double, double, double> function)
    {
        var raster = new Raster(160, 160);
        for (int y = 0; y < 160; y++)
            for (int x = 0; x < 160; x++) raster[y, x] = (float)function(x + 0.5, y + 0.5);
        return new WorldSurfaceSampler(SurfaceSampler.BSpline(raster), new RasterGeoReference(160, 160, 0, 1, 0, 0, 0, 1));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    public void Plane_candidates_have_the_analytic_length_and_signed_grade(double grade)
    {
        var surface = Terrain((x, _) => 100 + x * 0.125);
        var session = new NeutralDesignSession(surface, 80, 80, grade, 20);
        var search = session.FindCandidates();
        Assert.Equal(2, search.Candidates.Count);
        foreach (var candidate in search.Candidates)
        {
            Assert.Equal(80 + grade / 100 * 20 / 0.125, candidate.X, 5);
            Assert.Equal(20, Math.Sqrt(Math.Pow(candidate.X - 80, 2) + Math.Pow(candidate.Y - 80, 2)), 9);
            var check = session.Assess(candidate);
            Assert.True(check.IsValid);
            Assert.True(check.MaximumDeviation < 1e-5);
        }
    }

    [Fact]
    public void A_tangent_root_at_the_periodic_seam_is_not_lost_or_duplicated()
    {
        var session = new NeutralDesignSession(Terrain((x, _) => x * 0.125), 80, 80, 12.5, 20);
        var candidate = Assert.Single(session.FindCandidates().Candidates);
        Assert.Equal(100, candidate.X, 5);
        Assert.Equal(80, candidate.Y, 4);
    }

    [Fact]
    public void Level_circle_is_reported_without_hundreds_of_duplicate_directions()
    {
        var surface = Terrain((_, _) => 100);
        var session = new NeutralDesignSession(surface, 80, 80, 0, 20);
        var search = session.FindCandidates();
        Assert.True(search.SampledLevelCircle);
        Assert.Empty(search.Candidates);
        var candidate = NeutralCandidateSolver.AtHeading(surface, 80, 80, 20, 100, 0.731);
        Assert.NotNull(candidate);
        Assert.True(session.Accept(session.Assess(candidate.Value)));
        Assert.Equal(20, session.End.Distance, 9);
    }

    [Fact]
    public void Missing_interior_terrain_cannot_be_accepted_even_with_warning_override()
    {
        var surface = Terrain((x, y) => x > 87 && x < 90 && Math.Abs(y - 80) < 4 ? double.NaN : 100);
        var session = new NeutralDesignSession(surface, 80, 80, 0, 20);
        var candidate = NeutralCandidateSolver.AtHeading(surface, 80, 80, 20, 100, 0)!.Value;
        var check = session.Assess(candidate);
        Assert.False(check.IsValid);
        Assert.Contains(check.Profile, p => p.Value == null);
        Assert.False(session.Accept(check, acceptWarnings: true));
        Assert.Single(session.Vertices);
    }

    [Fact]
    public void Interior_terrain_deviation_requires_explicit_acceptance_and_survives_undo_redo()
    {
        var surface = Terrain((x, _) => x > 85 && x < 95 ? 102 : 100);
        var session = new NeutralDesignSession(surface, 80, 80, 0, 20);
        var candidate = NeutralCandidateSolver.AtHeading(surface, 80, 80, 20, 100, 0)!.Value;
        var check = session.Assess(candidate);
        Assert.True(check.IsValid);
        Assert.True(check.Warnings.HasFlag(NeutralDesignWarning.TerrainDeviation));
        Assert.InRange(check.MaximumDeviation, 1.9, 2.1);
        Assert.False(session.Accept(check));
        Assert.True(session.Accept(check, acceptWarnings: true));
        Assert.True(session.Undo());
        session.SetStep(5);
        Assert.True(session.Redo());
        Assert.Equal(20, session.End.Distance, 8);
        Assert.Same(check, Assert.Single(session.Segments));
        Assert.Equal(5, session.Step);
    }

    [Fact]
    public void Stale_and_foreign_assessments_are_rejected_and_a_new_branch_clears_redo()
    {
        var surface = Terrain((x, _) => 100 + x * 0.125);
        var session = new NeutralDesignSession(surface, 80, 80, 5, 10);
        var candidate = session.FindCandidates().Candidates[0];
        var check = session.Assess(candidate);
        var other = new NeutralDesignSession(surface, 80, 80, 5, 10);
        Assert.False(other.Accept(check));
        session.SetStep(5);
        Assert.False(session.Accept(check));
        var next = session.Assess(session.FindCandidates().Candidates[0]);
        Assert.True(session.Accept(next));
        Assert.True(session.Undo());
        Assert.True(session.CanRedo);
        Assert.True(session.Accept(session.Assess(session.FindCandidates().Candidates[1])));
        Assert.False(session.CanRedo);
        Assert.False(session.Undo() && session.Undo()); // the start itself cannot be removed
        Assert.Single(session.Vertices);
    }

    [Fact]
    public void A_crossing_and_sharp_turns_are_flagged()
    {
        var surface = Terrain((_, _) => 100);
        var session = new NeutralDesignSession(surface, 60, 60, 0, 20);
        void Move(double heading, double step)
        {
            session.SetStep(step);
            var end = session.End;
            var candidate = NeutralCandidateSolver.AtHeading(surface, end.X, end.Y, step, 100, heading)!.Value;
            Assert.True(session.Accept(session.Assess(candidate), acceptWarnings: true));
        }
        Move(0, 20);
        Move(Math.PI / 2, 20);
        Move(Math.PI, 10);
        session.SetStep(30);
        var last = session.End;
        var cross = NeutralCandidateSolver.AtHeading(surface, last.X, last.Y, 30, 100, -Math.PI / 2)!.Value;
        var check = session.Assess(cross);
        Assert.True(check.Warnings.HasFlag(NeutralDesignWarning.SelfIntersection));
        Assert.True(check.Warnings.HasFlag(NeutralDesignWarning.SharpTurn));
    }

    [Fact]
    public void Saved_record_keeps_variable_steps_and_accepted_decisions_exactly()
    {
        var session = new NeutralDesignSession(Terrain((x, _) => 100 + x * 0.125), 80, 80, 5, 7);
        Assert.True(session.Accept(session.Assess(session.FindCandidates().Candidates[0])));
        session.SetStep(3);
        Assert.True(session.Accept(session.Assess(session.FindCandidates().Candidates[0])));
        var record = NeutralDesignRecord.Capture(session);
        var restored = NeutralDesignRecord.FromJson(record.ToJson());
        Assert.Equal(record.Vertices, restored.Vertices);
        Assert.Equal(record.Warnings, restored.Warnings);
        Assert.Equal(record.SurfaceMethod, restored.SurfaceMethod);
        Assert.Equal(10, restored.Vertices[^1][3], 8);
        restored.Vertices[1][3] = -5;
        Assert.Throws<FormatException>(() => restored.ToJson());
    }

    [Fact]
    public void Search_and_checks_honor_cancellation_without_mutating_the_trial()
    {
        var session = new NeutralDesignSession(Terrain((x, _) => x * 0.125), 80, 80, 5, 10);
        var candidate = session.FindCandidates().Candidates[0];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.FindCandidates(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => session.Assess(candidate, cancellation.Token));
        Assert.Single(session.Vertices);
    }

    [Fact]
    public void Impossible_grade_and_partly_missing_circle_are_distinct_from_a_level_circle()
    {
        var surface = Terrain((x, _) => x * 0.125);
        var impossible = new NeutralDesignSession(surface, 80, 80, 20, 10).FindCandidates();
        Assert.Empty(impossible.Candidates);
        Assert.False(impossible.SampledLevelCircle);
        Assert.False(impossible.HasMissingTerrain);
        var edge = new NeutralDesignSession(surface, 4, 80, 0, 10).FindCandidates();
        Assert.True(edge.HasMissingTerrain);
        Assert.False(edge.SampledLevelCircle);
    }

    [Fact]
    public void Draft_roundtrip_preserves_redo_profiles_and_variable_steps()
    {
        var terrain = Terrain((x, _) => 100 + x * 0.125);
        var session = new NeutralDesignSession(terrain, 50, 50, 5, 10);
        session.Accept(session.Assess(session.FindCandidates().Candidates[0]), true);
        session.SetStep(7);
        session.Accept(session.Assess(session.FindCandidates().Candidates[0]), true);
        var original = session.CaptureDraft();
        session.Undo(); session.SetStep(3);
        var json = System.Text.Json.JsonSerializer.Serialize(session.CaptureDraft());
        var draft = System.Text.Json.JsonSerializer.Deserialize<NeutralDesignDraft>(json)!;
        var restored = NeutralDesignSession.Restore(terrain, draft);
        Assert.Single(restored.Segments); Assert.True(restored.CanRedo); Assert.Equal(3, restored.Step);
        Assert.True(restored.Redo());
        Assert.Equal(original.Record.Vertices, NeutralDesignRecord.Capture(restored).Vertices);
        Assert.Equal(original.Profiles, restored.CaptureDraft().Profiles);
        draft.AcceptedSegments = 99;
        Assert.Throws<FormatException>(() => NeutralDesignSession.Restore(terrain, draft));
    }

    [Fact]
    public void Start_only_draft_can_be_saved_and_resumed()
    {
        var terrain = Terrain((x, _) => x);
        var session = new NeutralDesignSession(terrain, 50, 50, 5);
        var restored = NeutralDesignSession.Restore(terrain, session.CaptureDraft());
        Assert.Single(restored.Vertices); Assert.False(restored.CanRedo);
        Assert.Throws<ArgumentException>(() => NeutralDesignSession.Restore(Terrain((x, _) => x + 1), session.CaptureDraft()));
    }

    [Fact]
    public void Existing_line_review_preserves_vertices_and_flags_grade_drift()
    {
        var terrain = Terrain((x, _) => 100 + x * 0.125);
        var points = new[] { (50.0, 50.0), (60.0, 50.0), (60.0, 60.0) };
        var checks = NeutralDesignSession.Review(terrain, points);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(c.IsValid));
        Assert.All(checks, c => Assert.True(c.Warnings.HasFlag(NeutralDesignWarning.GradeDeviation)));
        Assert.True(checks[1].Warnings.HasFlag(NeutralDesignWarning.SharpTurn));
        Assert.Equal(points.Skip(1), checks.Select(c => (c.End.X, c.End.Y)));
        var reference = NeutralDesignSession.Review(terrain, points, 12.5);
        Assert.False(reference[0].Warnings.HasFlag(NeutralDesignWarning.GradeDeviation));
    }

    [Fact]
    public void Arrival_area_and_level_passage_do_not_snap_geometry()
    {
        var surface = Terrain((x, _) => 100 + x * 0.125);
        var session = new NeutralDesignSession(surface, 50, 50, 5, 10);
        session.Accept(session.Assess(session.FindCandidates().Candidates[0]));
        var end = session.End;
        var target = new NeutralDesignTarget { X = end.X + 1, Y = end.Y, Radius = 2, Elevation = end.Elevation - 0.1 };
        Assert.True(target.Reached(end.X, end.Y)); Assert.False(target.LevelPassed(session));
        target.Radius = 0.5;
        Assert.False(target.Reached(end.X, end.Y)); Assert.True(target.LevelPassed(session));
        Assert.Equal(end.X, session.End.X);
        target.Radius = double.NaN; Assert.Throws<FormatException>(() => target.Validate());
    }

    [Fact]
    public void Existing_line_review_reports_interior_nodata_and_corrupt_profiles_are_rejected()
    {
        var terrain = Terrain((x, _) => x > 58 && x < 62 ? double.NaN : 100);
        var review = NeutralDesignSession.Review(terrain, new[] { (50.0, 50.0), (70.0, 50.0) }, 0);
        Assert.False(review[0].IsValid);
        Assert.Contains(review[0].Profile, p => !p.Value.HasValue);
        var valid = Terrain((x, _) => 100 + x * 0.125);
        var session = new NeutralDesignSession(valid, 50, 50, 5, 10);
        session.Accept(session.Assess(session.FindCandidates().Candidates[0]));
        var draft = session.CaptureDraft(); draft.Profiles[0][1][3] = null;
        Assert.Throws<FormatException>(() => draft.Validate());
        Assert.Empty(RasterField.Projects.ProjectDocument.FromJson("""{"version":1,"layers":[]}""").NeutralDesigns);
        Assert.Throws<FormatException>(() => RasterField.Projects.ProjectDocument.FromJson("""{"version":99,"layers":[]}"""));
    }
}
