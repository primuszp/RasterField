using System;
using System.Threading;
using RasterField.Interpolation;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests;

public sealed class SurfaceInterpolationTests
{
    private static Raster Make(int width, int height, Func<int, int, double> value)
    {
        var raster = new Raster(width, height, -9999);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) raster[y, x] = (float)value(x, y);
        return raster;
    }

    private static ISurfaceSampler Create(Raster raster, string method) => method switch
    {
        "bilinear" => SurfaceSampler.Bilinear(raster, strict: false),
        "bezier" => SurfaceSampler.Bezier(raster),
        _ => SurfaceSampler.BSpline(raster)
    };

    private static ISurfaceSampler Create(IRasterSource source, string method, CancellationToken token = default) => method switch
    {
        "bilinear" => SurfaceSampler.Bilinear(source, strict: false, tileSize: 4, cancellationToken: token),
        "bezier" => SurfaceSampler.Bezier(source, tileSize: 4, cancellationToken: token),
        _ => SurfaceSampler.BSpline(source, tileSize: 4, cancellationToken: token)
    };

    [Theory]
    [InlineData("bilinear")]
    [InlineData("bezier")]
    [InlineData("bspline")]
    public void All_methods_reproduce_a_plane_at_arbitrary_interior_positions(string method)
    {
        var raster = Make(10, 9, (x, y) => 20 + 2 * (x + 0.5) - 3 * (y + 0.5));
        var sampler = Create(raster, method);
        foreach (var (x, y) in new[] { (2.13, 3.7), (6.2, 5.6), (4.5, 4.5) })
        {
            var result = sampler.Sample(x, y);
            Assert.Equal(20 + 2 * x - 3 * y, result.Value!.Value, 10);
            Assert.Equal(SurfaceSampleFlags.None, result.Flags);
        }
    }

    [Fact]
    public void Direct_bspline_smooths_an_impulse_while_bezier_interpolates_its_center()
    {
        var raster = Make(9, 9, (x, y) => x == 4 && y == 4 ? 90 : 0);
        Assert.Equal(40, SurfaceSampler.BSpline(raster).Sample(4.5, 4.5).Value!.Value, 10);
        Assert.Equal(90, SurfaceSampler.Bezier(raster).Sample(4.5, 4.5).Value!.Value, 10);
        // The tensor cubic B-spline is C2 at the sample centers (away from missing data).
        var spline = SurfaceSampler.BSpline(raster);
        const double epsilon = 1e-5;
        double left = spline.Sample(4.5 - epsilon, 4.2).Value!.Value;
        double right = spline.Sample(4.5 + epsilon, 4.2).Value!.Value;
        Assert.Equal(left, right, 8);
    }

    [Fact]
    public void Strict_methods_reject_missing_data_and_fallback_is_explicit()
    {
        var raster = Make(8, 8, (x, y) => x == 2 && y == 2 ? -9999 : 10);
        var spline = SurfaceSampler.BSpline(raster).Sample(3.8, 3.8);
        Assert.False(spline.IsValid);
        Assert.True(spline.Flags.HasFlag(SurfaceSampleFlags.NoData));

        var fallback = SurfaceSampler.Bezier(raster).Sample(3.8, 3.8);
        Assert.Equal(10, fallback.Value!.Value);
        Assert.True(fallback.Flags.HasFlag(SurfaceSampleFlags.FallbackBilinear));
        Assert.False(SurfaceSampler.Bezier(raster, new BezierPatchOptions { NoData = BezierNoDataMode.NoData })
            .Sample(3.8, 3.8).IsValid);

        Assert.False(SurfaceSampler.Bilinear(raster).Sample(2.8, 2.8).IsValid);
        var tolerant = SurfaceSampler.Bilinear(raster, strict: false).Sample(2.8, 2.8);
        Assert.Equal(10, tolerant.Value!.Value);
        Assert.True(tolerant.Flags.HasFlag(SurfaceSampleFlags.Renormalized));
        // A zero-weight missing neighbor does not invalidate the exact valid cell center.
        Assert.Equal(10, SurfaceSampler.Bilinear(raster).Sample(3.5, 2.5).Value!.Value);
    }

    [Fact]
    public void Boundary_rules_are_explicit_and_small_bspline_inputs_do_not_extrapolate()
    {
        var raster = Make(6, 6, (x, y) => 5 + x + y);
        Assert.False(SurfaceSampler.BSpline(raster).Sample(0.2, 0.2).IsValid);
        Assert.False(SurfaceSampler.Bezier(raster, strictBoundary: true).Sample(0.2, 0.2).IsValid);
        var reflected = SurfaceSampler.Bezier(raster).Sample(0.2, 0.2);
        Assert.Equal(4.4, reflected.Value!.Value, 10);
        Assert.True(reflected.Flags.HasFlag(SurfaceSampleFlags.Extrapolated));
        Assert.False(SurfaceSampler.BSpline(new Raster(3, 3)).Sample(1.5, 1.5).IsValid);
        Assert.Equal(SurfaceSampleFlags.OutsideDomain, SurfaceSampler.Bezier(new Raster(0, 0)).Sample(0, 0).Flags);
    }

    [Theory]
    [InlineData("bilinear")]
    [InlineData("bezier")]
    [InlineData("bspline")]
    public void Invalid_positions_and_nonfinite_source_values_never_produce_a_value(string method)
    {
        var sampler = Create(Make(8, 8, (_, _) => double.PositiveInfinity), method);
        Assert.False(sampler.Sample(3.2, 3.8).IsValid);
        Assert.Equal(SurfaceSampleFlags.InvalidCoordinate, sampler.Sample(double.NaN, 4).Flags);
        Assert.Equal(SurfaceSampleFlags.InvalidCoordinate, sampler.Sample(4, double.PositiveInfinity).Flags);
        Assert.Equal(SurfaceSampleFlags.OutsideDomain, sampler.Sample(-0.1, 4).Flags);
        Assert.Equal(SurfaceSampleFlags.OutsideDomain, sampler.Sample(8.1, 4).Flags);
    }

    [Theory]
    [InlineData("bilinear")]
    [InlineData("bezier")]
    [InlineData("bspline")]
    public void Windowed_sampling_matches_loaded_data_across_tiles_holes_and_global_edges(string method)
    {
        var raster = Make(27, 24, (x, y) => x == 7 && y == 9 ? -9999 : Math.Sin(x) * 12 + x * y);
        using var source = new MemoryRasterSource(raster);
        var loaded = Create(raster, method);
        var windowed = Create(source, method);
        // More than sixteen tiles also exercises LRU eviction and subsequent rereads.
        for (int pass = 0; pass < 2; pass++)
            for (double y = 0.17; y <= 24; y += 1.3)
                for (double x = 0.13; x <= 27; x += 1.1)
                    Assert.Equal(loaded.Sample(x, y), windowed.Sample(x, y));
    }

    [Theory]
    [InlineData("bilinear")]
    [InlineData("bezier")]
    [InlineData("bspline")]
    public void Subdivision_and_direct_sampling_agree_and_preserve_rotated_non_square_footprint(string method)
    {
        var raster = Make(7, 8, (x, y) => Math.Sin(x) * 10 + y * y);
        var sampler = Create(raster, method);
        var geo = new RasterGeoReference(7, 8, 100, 3, 2, 200, 1, -6);
        var world = new WorldSurfaceSampler(sampler, geo);
        var refined = RasterResampler.Subdivide(world, 3);
        Assert.Equal(geo.PixelToWorld(7, 8), refined.GeoReference.PixelToWorld(21, 24));
        Assert.Equal(geo.PixelToWorld(0, 0), refined.GeoReference.PixelToWorld(0, 0));
        for (int y = 0; y < refined.Raster.Height; y++)
            for (int x = 0; x < refined.Raster.Width; x++)
            {
                float expected = (float)(sampler.Sample((x + 0.5) / 3, (y + 0.5) / 3).Value ?? double.NaN);
                Assert.Equal(expected, refined.Raster[y, x]);
            }
    }

    [Fact]
    public void Shared_bezier_subdivision_matches_the_optimized_legacy_path_with_all_options()
    {
        var raster = Make(8, 7, (x, y) => x == 2 && y == 3 ? -9999 : x < 4 ? y : 100 + y);
        var geo = new RasterGeoReference(8, 7, 0, 1, 0, 0, 0, -1);
        foreach (double tension in new[] { 0, 0.5, 1.0 })
            foreach (bool monotone in new[] { false, true })
                foreach (var noData in new[] { BezierNoDataMode.NoData, BezierNoDataMode.FallbackBilinear })
                {
                    var options = new BezierPatchOptions { Factor = 3, Tension = tension, Monotone = monotone, NoData = noData };
                    var expected = BezierPatchInterpolator.Subdivide(raster, options);
                    var actual = RasterResampler.Subdivide(new WorldSurfaceSampler(SurfaceSampler.Bezier(raster, options), geo), 3).Raster;
                    for (int i = 0; i < expected.Samples.Length; i++)
                        Assert.Equal(expected.IsNoData(expected.Samples[i]) ? float.NaN : expected.Samples[i], actual.Samples[i]);
                }
    }

    [Fact]
    public void Target_grid_and_profile_use_the_same_bspline_surface()
    {
        var raster = Make(10, 10, (x, y) => 7 + 2 * (x + 0.5) + 3 * (y + 0.5));
        var sampler = SurfaceSampler.BSpline(raster);
        var geo = new RasterGeoReference(10, 10, 100, 2, 1, 200, 1, -3);
        var world = new WorldSurfaceSampler(sampler, geo);
        var origin = geo.PixelToWorld(2, 2);
        var target = new RasterGeoReference(3, 4, origin.X, 1, 0.5, origin.Y, 0.5, -1.5);
        var refined = RasterResampler.Resample(world, target);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(7 + 2 * (2 + (x + 0.5) / 2) + 3 * (2 + (y + 0.5) / 2), refined.Raster[y, x], 5);

        var points = new[] { geo.PixelToWorld(2.5, 2.5), geo.PixelToWorld(6.5, 6.5) };
        var profile = RasterProfiler.SamplePolylineWorld(sampler, geo, points, 1);
        foreach (var sample in profile)
            Assert.Equal((float?)world.Sample(sample.X, sample.Y).Value, sample.Value);
    }

    [Fact]
    public void Sampler_copies_bezier_options_and_checks_world_dimensions()
    {
        var raster = Make(8, 8, (x, y) => x * x + y);
        var options = new BezierPatchOptions { Tension = 0.5 };
        var sampler = SurfaceSampler.Bezier(raster, options);
        var before = sampler.Sample(3.7, 4.2);
        options.Tension = 0;
        options.Monotone = true;
        Assert.Equal(before, sampler.Sample(3.7, 4.2));
        Assert.Throws<ArgumentException>(() => new WorldSurfaceSampler(sampler, new RasterGeoReference(4, 4, 0, 1, 0, 0, 0, -1)));
        Assert.Throws<ArgumentException>(() => new WorldSurfaceSampler(sampler, new RasterGeoReference(8, 8, 0, 1, 1, 0, 1, 1)));
    }

    [Theory]
    [InlineData("bilinear")]
    [InlineData("bezier")]
    [InlineData("bspline")]
    public void Windowed_sampling_honors_cancellation_even_for_a_cached_tile(string method)
    {
        using var source = new MemoryRasterSource(Make(8, 8, (x, y) => x + y));
        using var cancellation = new CancellationTokenSource();
        var sampler = Create(source, method, cancellation.Token);
        Assert.True(sampler.Sample(3.5, 3.5).IsValid);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => sampler.Sample(3.5, 3.5));
    }

    [Fact]
    public void Resampling_checks_cancellation_and_output_size_before_work()
    {
        var world = new WorldSurfaceSampler(SurfaceSampler.BSpline(new Raster(8, 8)), new RasterGeoReference(8, 8, 0, 1, 0, 0, 0, -1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RasterResampler.Subdivide(world, 2, cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => RasterResampler.Subdivide(world, 0));
        Assert.Throws<OverflowException>(() => RasterResampler.Subdivide(world, int.MaxValue));
    }
}
