using System;
using System.Linq;
using System.Threading;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests;

public class NeutralRouteTests
{
    private static (Raster Raster, RasterGeoReference Geo) Terrain(Func<double,double,double> height)
    {
        var raster = new Raster(100,100);
        var geo = new RasterGeoReference(100,100,0,1,0,100,0,-1);
        for (int r = 0; r < 100; r++)
        for (int c = 0; c < 100; c++)
        { var p = geo.CellCentreToWorld(c,r); raster[r,c] = (float)height(p.X,p.Y); }
        return (raster,geo);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Exact_endpoints_are_connected_by_a_checked_switchback_on_a_plane(int sign)
    {
        var (raster,geo) = Terrain((x,_) => sign*0.125*x);
        var options = new NeutralRouteOptions { GradePercent = sign*5, MaximumLength = 20 };
        var result = NeutralLineTracer.FindRoute(raster,geo,40,50,44,50,options);
        Assert.True(result.Found, result.Status.ToString());
        Assert.Equal(10,result.RequiredLength!.Value,8);
        Assert.Equal(10,result.Length,7);
        Verify(raster,geo,result,options,40,50,44,50);
        Assert.Contains(result.Samples,p => Math.Abs(p.Y-50)>1);
    }

    [Fact]
    public void Straight_feasible_connection_is_accepted_without_a_detour()
    {
        var (raster,geo) = Terrain((x,_) => 0.125*x);
        double endY = 50+10*Math.Sqrt(1-0.4*0.4);
        var options = new NeutralRouteOptions { MaximumLength = 20 };
        var result = NeutralLineTracer.FindRoute(raster,geo,40,50,44,endY,options);
        Assert.True(result.Found);
        Assert.Equal(10,result.Length,8);
        Verify(raster,geo,result,options,40,50,44,endY);
    }

    [Fact]
    public void Flat_zero_grade_connection_can_avoid_a_nodata_barrier()
    {
        var (raster,geo) = Terrain((_,_) => 100);
        for(int r=44;r<=55;r++) raster[r,50] = float.NaN;
        var options = new NeutralRouteOptions { GradePercent = 0, MaximumLength = 40 };
        var result = NeutralLineTracer.FindRoute(raster,geo,40,50,60,50,options);
        Assert.True(result.Found,result.Status.ToString());
        Assert.Null(result.RequiredLength);
        Verify(raster,geo,result,options,40,50,60,50);
        Assert.True(result.Length>20);
    }

    [Fact]
    public void Elevation_incompatibility_length_limit_and_invalid_endpoints_are_explicit()
    {
        var (raster,geo) = Terrain((x,_) => 0.125*x);
        Assert.Equal(NeutralRouteStatus.IncompatibleGrade,NeutralLineTracer.FindRoute(raster,geo,40,50,44,50,new NeutralRouteOptions { GradePercent=-5 }).Status);
        Assert.Equal(NeutralRouteStatus.IncompatibleGrade,NeutralLineTracer.FindRoute(raster,geo,40,50,44,50,new NeutralRouteOptions { GradePercent=0 }).Status);
        Assert.Equal(NeutralRouteStatus.IncompatibleGrade,NeutralLineTracer.FindRoute(raster,geo,40,50,44,50,new NeutralRouteOptions { GradePercent=20 }).Status);
        Assert.Equal(NeutralRouteStatus.LengthLimit,NeutralLineTracer.FindRoute(raster,geo,40,50,44,50,new NeutralRouteOptions { MaximumLength=3 }).Status);
        Assert.Equal(NeutralRouteStatus.LengthLimit,NeutralLineTracer.FindRoute(raster,geo,40,50,44,50,new NeutralRouteOptions { MaximumLength=8 }).Status);
        Assert.Equal(NeutralRouteStatus.CoincidentEndpoints,NeutralLineTracer.FindRoute(raster,geo,40,50,40,50,new NeutralRouteOptions()).Status);
        Assert.Equal(NeutralRouteStatus.EndpointNoData,NeutralLineTracer.FindRoute(raster,geo,-1,50,44,50,new NeutralRouteOptions()).Status);
    }

    [Fact]
    public void Search_limits_return_no_partial_line_and_cancellation_throws()
    {
        var (raster,geo) = Terrain((x,y) => 0.1*x+0.001*x*y);
        var options = new NeutralRouteOptions { MaximumLength=100, MaximumStates=1, HeightTolerance=0.0001, GradeTolerancePercent=0.05 };
        var result = NeutralLineTracer.FindRoute(raster,geo,30,50,35,50,options);
        Assert.False(result.Found);
        Assert.Empty(result.Samples);
        Assert.Equal(NeutralRouteStatus.SearchLimit,result.Status);
        Assert.Throws<OperationCanceledException>(() => NeutralLineTracer.FindRoute(raster,geo,30,50,35,50,options,new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NeutralLineTracer.FindRoute(raster,geo,30,50,35,50,new NeutralRouteOptions { MaximumStates=0 }));
    }

    [Fact]
    public void A_curved_trace_endpoint_can_be_reached_by_multiple_search_steps()
    {
        var (raster,geo) = Terrain((x,y) => 0.1*x+0.001*x*y);
        var trace = NeutralLineTracer.Trace(raster,geo,40.2,50.3,new NeutralLineOptions { MaximumLength=8, HeightTolerance=0.0001, GradeTolerancePercent=0.02 })[0];
        Assert.Equal(NeutralLineStop.MaximumLength,trace.Stop);
        var end = trace.Samples[^1];
        var options = new NeutralRouteOptions { MaximumLength=10, HeightTolerance=0.0001, GradeTolerancePercent=0.02 };
        var result = NeutralLineTracer.FindRoute(raster,geo,40.2,50.3,end.X,end.Y,options);
        Assert.True(result.Found,result.Status.ToString());
        Assert.True(result.ExpandedStates > 1);
        Verify(raster,geo,result,options,40.2,50.3,end.X,end.Y);
    }

    [Fact]
    public void Rotated_non_square_metric_cells_preserve_world_endpoints()
    {
        var geo = new RasterGeoReference(100,100,650000,1.6,0.6,200000,1.2,-0.8);
        var raster = new Raster(100,100);
        for (int row=0;row<100;row++)
        for (int col=0;col<100;col++)
        { var p=geo.CellCentreToWorld(col,row); raster[row,col]=(float)(100+0.125*(p.X-650000)); }
        var start = geo.PixelToWorld(50,50);
        var options = new NeutralRouteOptions { MaximumLength=20 };
        var result = NeutralLineTracer.FindRoute(raster,geo,start.X,start.Y,start.X+4,start.Y,options);
        Assert.True(result.Found,result.Status.ToString());
        Verify(raster,geo,result,options,start.X,start.Y,start.X+4,start.Y);
    }

    private static void Verify(Raster raster,RasterGeoReference geo,NeutralRouteResult result,NeutralRouteOptions options,double ax,double ay,double bx,double by)
    {
        Assert.Equal(ax,result.Samples[0].X); Assert.Equal(ay,result.Samples[0].Y);
        Assert.Equal(bx,result.Samples[^1].X,9); Assert.Equal(by,result.Samples[^1].Y,9);
        Assert.InRange(result.MaximumHeightError,0,options.HeightTolerance);
        Assert.InRange(result.MaximumGradeErrorPercent,0,options.GradeTolerancePercent);
        Assert.True(result.Length<=options.MaximumLength+1e-8);
        var samples = RasterProfiler.SamplePolylineWorld(raster,geo,result.Samples.Select(p=>(p.X,p.Y)).ToArray(),0.005);
        foreach(var sample in samples)
        {
            Assert.NotNull(sample.Value);
            Assert.InRange(Math.Abs(sample.Value.Value-result.StartElevation-options.GradePercent/100*sample.Distance),0,options.HeightTolerance+0.00002);
        }
    }
}
