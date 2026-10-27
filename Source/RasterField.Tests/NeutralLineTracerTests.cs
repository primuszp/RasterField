using System;
using System.Linq;
using System.Threading;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests;

public class NeutralLineTracerTests
{
    private static (Raster Raster, RasterGeoReference Geo) Terrain(Func<double, double, double> height, bool rotated = false)
    {
        var geo = rotated ? new RasterGeoReference(100, 100, 0, 1.6, 0.6, 0, 1.2, -0.8)
            : new RasterGeoReference(100, 100, 0, 1, 0, 100, 0, -1);
        var raster = new Raster(100, 100);
        for (int r = 0; r < 100; r++)
        for (int c = 0; c < 100; c++)
        { var p = geo.CellCentreToWorld(c, r); raster[r,c] = (float)height(p.X, p.Y); }
        return (raster, geo);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(-5, false)]
    [InlineData(0, false)]
    [InlineData(5, true)]
    public void Plane_branches_have_analytic_endpoints_and_correct_horizontal_chainage(double percent, bool rotated)
    {
        var (raster, geo) = Terrain((x, _) => 0.125*x, rotated);
        var start = geo.PixelToWorld(50, 50);
        var branches = NeutralLineTracer.Trace(raster, geo, start.X, start.Y,
            new NeutralLineOptions { GradePercent = percent, MaximumLength = 10 });
        Assert.Equal(2, branches.Count);
        for (int i = 0; i < 2; i++)
        {
            var branch = branches[i];
            Assert.Equal(NeutralLineStop.MaximumLength, branch.Stop);
            Assert.Equal(10, branch.Length, 7);
            var last = branch.Samples[^1];
            Assert.InRange(Math.Abs(last.X-start.X-10*percent/12.5), 0, 0.0002);
            double dy = (i == 0 ? 1 : -1)*10*Math.Sqrt(1-Math.Pow(percent/12.5,2));
            Assert.InRange(Math.Abs(last.Y-start.Y-dy), 0, 0.0002);
            Assert.InRange(branch.MaximumHeightError, 0, 0.00001);
            Assert.InRange(branch.MaximumGradeErrorPercent, 0, 0.001);
        }
    }

    [Fact]
    public void Too_gentle_and_flat_terrain_report_no_path()
    {
        var (raster, geo) = Terrain((x, _) => 0.01*x);
        Assert.All(NeutralLineTracer.Trace(raster, geo, 50, 50, new NeutralLineOptions()),
            b => { Assert.Single(b.Samples); Assert.Equal(NeutralLineStop.InsufficientSlope, b.Stop); });
        var flat = new Raster(100,100);
        Assert.All(NeutralLineTracer.Trace(flat, geo, 50, 50, new NeutralLineOptions { GradePercent = 0 }),
            b => Assert.Equal(NeutralLineStop.FlatSurface, b.Stop));
    }

    [Fact]
    public void Nodata_barrier_is_never_crossed()
    {
        var (raster, geo) = Terrain((x, _) => 0.125*x);
        for (int c = 0; c < 100; c++) raster[40,c] = float.NaN;
        var branch = NeutralLineTracer.Trace(raster, geo, 50, 50,
            new NeutralLineOptions { MaximumLength = 30 })[0];
        Assert.Equal(NeutralLineStop.BoundaryOrNoData, branch.Stop);
        Assert.All(branch.Samples, p => Assert.True(p.Y <= 58.5));
    }

    [Fact]
    public void Curved_surface_respects_whole_segment_tolerances_and_dense_independent_profile()
    {
        var (raster, geo) = Terrain((x, y) => 0.1*x+0.001*x*y);
        var options = new NeutralLineOptions { MaximumLength = 25, GradeTolerancePercent = 0.05, HeightTolerance = 0.002 };
        foreach (var branch in NeutralLineTracer.Trace(raster, geo, 50, 50, options))
        {
            Assert.Equal(NeutralLineStop.MaximumLength, branch.Stop);
            Assert.True(branch.MaximumHeightError <= options.HeightTolerance);
            Assert.True(branch.MaximumGradeErrorPercent <= options.GradeTolerancePercent);
            var profile = RasterProfiler.SamplePolylineWorld(raster, geo, branch.Samples.Select(p => (p.X,p.Y)).ToArray(), 0.01);
            Assert.All(profile, p => Assert.InRange(Math.Abs(p.Value!.Value-(7.5+0.05*p.Distance)), 0, options.HeightTolerance+0.00001));
        }
    }

    [Fact]
    public void Maximum_slope_merges_branches_and_float_nodata_sentinels_are_respected()
    {
        var (raster, geo) = Terrain((x, _) => 0.125*x);
        var branches = NeutralLineTracer.Trace(raster, geo, 50, 50,
            new NeutralLineOptions { GradePercent = 12.5, MaximumLength = 10 });
        Assert.All(branches, b => Assert.Equal(NeutralLineStop.MaximumLength, b.Stop));
        Assert.Equal(branches[0].Samples[^1].X, branches[1].Samples[^1].X, 9);
        Assert.Equal(branches[0].Samples[^1].Y, branches[1].Samples[^1].Y, 9);
        var nodata = new Raster(100, 100, noDataValue: -9999.123);
        Array.Fill(nodata.Samples, (float)-9999.123);
        Assert.All(NeutralLineTracer.Trace(nodata, geo, 50, 50, new NeutralLineOptions()), b => Assert.Empty(b.Samples));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Circle_search_continues_both_branches_through_a_wavy_patch_transition(int sign)
    {
        // Legacy local-only correction stopped at 0.205 m / 0.319 m here.
        var (raster, geo) = Terrain((x, y) => sign *
            (0.1*x + 0.375*Math.Sin(y/6 + 4.6) + 0.1*Math.Sin(0.9*x + 2.8)));
        var options = new NeutralLineOptions { GradePercent = sign*5, MaximumLength = 1 };
        var branches = NeutralLineTracer.Trace(raster, geo, 50.2, 50.3, options);
        foreach (var branch in branches)
        {
            Assert.Equal(NeutralLineStop.MaximumLength, branch.Stop);
            Assert.Equal(1, branch.Length, 8);
            VerifyDenseProfile(raster, geo, branch, options);
        }
        Assert.NotEqual(branches[0].Samples[^1].Y, branches[1].Samples[^1].Y);
    }

    [Fact]
    public void Validated_step_growth_reaches_length_within_a_small_step_budget()
    {
        var (raster, geo) = Terrain((x, _) => 0.125*x);
        var options = new NeutralLineOptions { MaximumLength = 10, MaximumSteps = 40 };
        foreach (var branch in NeutralLineTracer.Trace(raster, geo, 50, 50, options))
        {
            // The legacy quarter-cell steps exhausted this budget at 7.071 m.
            Assert.Equal(NeutralLineStop.MaximumLength, branch.Stop);
            Assert.Equal(10, branch.Length, 8);
            VerifyDenseProfile(raster, geo, branch, options);
        }
    }

    [Fact]
    public void Contour_following_handles_periodic_angles_and_keeps_the_initial_sides_distinct()
    {
        var (raster, geo) = Terrain((x, y) => 0.01*((x-50)*(x-50)+(y-50)*(y-50)));
        var options = new NeutralLineOptions { GradePercent = 0, MaximumLength = 10, GradeTolerancePercent = 0.05 };
        var branches = NeutralLineTracer.Trace(raster, geo, 70.2, 50.3, options);
        foreach (var branch in branches)
        {
            Assert.Equal(NeutralLineStop.MaximumLength, branch.Stop);
            VerifyDenseProfile(raster, geo, branch, options);
        }
        Assert.True(branches[0].Samples[^1].Y > 50.3);
        Assert.True(branches[1].Samples[^1].Y < 50.3);
    }

    private static void VerifyDenseProfile(Raster raster, RasterGeoReference geo, NeutralLineBranch branch, NeutralLineOptions options)
    {
        Assert.True(branch.MaximumHeightError <= options.HeightTolerance);
        Assert.True(branch.MaximumGradeErrorPercent <= options.GradeTolerancePercent);
        var profile = RasterProfiler.SamplePolylineWorld(raster, geo,
            branch.Samples.Select(p => (p.X, p.Y)).ToArray(), 0.005);
        var start = geo.WorldToPixel(branch.Samples[0].X, branch.Samples[0].Y);
        double z0 = RasterProfiler.BilinearSample(raster, start.Column, start.Row)!.Value;
        for (int i = 1; i < profile.Count; i++)
        {
            var a = profile[i-1]; var b = profile[i];
            Assert.NotNull(b.Value);
            Assert.InRange(Math.Abs(b.Value.Value-z0-options.GradePercent/100*b.Distance), 0, options.HeightTolerance+0.00001);
            if (b.Distance-a.Distance > 0.0001)
            {
                double grade = 100*(b.Value.Value-a.Value!.Value)/(b.Distance-a.Distance);
                // Independent float samples have limited vertical precision.
                Assert.InRange(Math.Abs(grade-options.GradePercent), 0, options.GradeTolerancePercent+0.02);
            }
        }
    }

    [Fact]
    public void Invalid_start_cancellation_and_step_budget_are_explicit()
    {
        var (raster, geo) = Terrain((x, _) => 0.125*x);
        Assert.All(NeutralLineTracer.Trace(raster, geo, -1, 0, new NeutralLineOptions()), b => Assert.Empty(b.Samples));
        Assert.Throws<OperationCanceledException>(() => NeutralLineTracer.Trace(raster, geo, 50,50, new NeutralLineOptions(), new CancellationToken(true)));
        Assert.All(NeutralLineTracer.Trace(raster, geo, 50,50, new NeutralLineOptions { MaximumSteps = 2 }), b => Assert.Equal(NeutralLineStop.StepLimit,b.Stop));
        Assert.Throws<ArgumentOutOfRangeException>(() => NeutralLineTracer.Trace(raster, geo, 50,50, new NeutralLineOptions { GradePercent = double.NaN }));
    }
}
