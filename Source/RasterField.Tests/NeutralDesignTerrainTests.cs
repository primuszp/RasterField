using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using RasterField.Interpolation;
using RasterField.Rasters;
using Xunit;
using Xunit.Abstractions;

namespace RasterField.Tests;

public sealed class NeutralDesignTerrainTests(ITestOutputHelper output)
{
    [Fact]
    public void Repository_terrain_supports_checked_candidates_and_reports_search_latency()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "P_00_01.ers"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var document = ErsDocument.Load(Path.Combine(directory.FullName, "P_00_01.ers"));
        var spline = new WorldSurfaceSampler(SurfaceSampler.BSpline(document.Band!), document.GeoReference);
        var bilinear = new WorldSurfaceSampler(SurfaceSampler.Bilinear(document.Band!), document.GeoReference);
        var times = new System.Collections.Generic.List<double>();
        double difference = 0; int valid = 0, checkedCandidates = 0;
        foreach (int row in new[] { 90, 180, 270, 360 })
            foreach (int column in new[] { 120, 240, 360, 480 })
            {
                var (x, y) = document.GeoReference.CellCentreToWorld(column, row);
                var s = spline.Sample(x, y); var b = bilinear.Sample(x, y);
                if (!s.IsValid || !b.IsValid) continue;
                valid++; difference = Math.Max(difference, Math.Abs(s.Value!.Value - b.Value!.Value));
                var session = new NeutralDesignSession(spline, x, y, 0, 20);
                var watch = Stopwatch.StartNew();
                var search = session.FindCandidates();
                foreach (var candidate in search.Candidates)
                {
                    var check = session.Assess(candidate);
                    Assert.True(check.IsValid);
                    Assert.InRange(Math.Abs(check.End.Elevation - session.NextElevation), 0, 0.000002);
                    checkedCandidates++;
                }
                watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds);
            }
        Assert.True(valid > 0); Assert.True(checkedCandidates > 0);
        output.WriteLine($"P_00_01: {valid} starts, {checkedCandidates} checked candidates; search+assessment mean {times.Average():F2} ms, max {times.Max():F2} ms. Max B-spline/bilinear difference {difference:F3} m.");
    }
}
