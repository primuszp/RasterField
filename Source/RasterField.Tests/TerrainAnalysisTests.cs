using System;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class TerrainAnalysisTests
    {
        private static Raster FlatPlane(int w, int h, float elevation)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = elevation;
            return r;
        }

        /// <summary>An east-rising ramp: elevation = k * column (independent of row) — Horn's method is exact for a planar surface.</summary>
        private static Raster EastRisingRamp(int w, int h, double k)
        {
            var r = new Raster(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y, x] = (float)(k * x);
            return r;
        }

        [Fact]
        public void Slope_of_a_flat_plane_is_zero_everywhere()
        {
            var elevation = FlatPlane(10, 10, 100f);
            var slope = TerrainAnalysis.Slope(elevation, 1, 1);

            for (int y = 1; y < 9; y++)
                for (int x = 1; x < 9; x++)
                    Assert.Equal(0.0, slope[y, x], 5);
        }

        [Fact]
        public void Aspect_of_a_flat_plane_reports_minus_one()
        {
            var elevation = FlatPlane(10, 10, 100f);
            var aspect = TerrainAnalysis.Aspect(elevation, 1, 1);

            for (int y = 1; y < 9; y++)
                for (int x = 1; x < 9; x++)
                    Assert.Equal(-1f, aspect[y, x]);
        }

        [Fact]
        public void Slope_of_a_planar_ramp_matches_the_analytic_gradient()
        {
            const double k = 2.0; // 2 units of rise per unit of run
            var elevation = EastRisingRamp(10, 10, k);
            var slope = TerrainAnalysis.Slope(elevation, 1, 1, SlopeUnits.Degrees);

            double expectedDegrees = Math.Atan(k) * 180.0 / Math.PI;
            // Interior cell, away from edge-replication effects.
            Assert.Equal(expectedDegrees, slope[5, 5], 4);
        }

        [Fact]
        public void Slope_in_percent_matches_rise_over_run_times_100()
        {
            const double k = 0.5;
            var elevation = EastRisingRamp(10, 10, k);
            var slope = TerrainAnalysis.Slope(elevation, 1, 1, SlopeUnits.Percent);

            Assert.Equal(k * 100.0, slope[5, 5], 4);
        }

        [Fact]
        public void Aspect_of_an_east_rising_ramp_faces_west()
        {
            // Elevation increases eastward -> steepest descent (aspect) points west (270 degrees).
            var elevation = EastRisingRamp(10, 10, 3.0);
            var aspect = TerrainAnalysis.Aspect(elevation, 1, 1);

            Assert.Equal(270.0, aspect[5, 5], 2);
        }

        [Fact]
        public void Hillshade_of_a_flat_plane_matches_the_pure_altitude_term()
        {
            var elevation = FlatPlane(10, 10, 50f);
            var hillshade = TerrainAnalysis.Hillshade(elevation, 1, 1, azimuthDegrees: 315, altitudeDegrees: 45);

            // Flat surface: shade = cos(zenith)*cos(0) + sin(zenith)*sin(0)*cos(...) = cos(zenith) = sin(altitude).
            double expected = Math.Sin(45.0 * Math.PI / 180.0) * 255.0;
            Assert.Equal(expected, hillshade[5, 5], 2);
        }

        [Fact]
        public void Hillshade_stays_within_the_0_to_255_range()
        {
            var elevation = EastRisingRamp(20, 20, 5.0);
            var hillshade = TerrainAnalysis.Hillshade(elevation, 1, 1);

            for (int y = 1; y < 19; y++)
                for (int x = 1; x < 19; x++)
                {
                    Assert.True(hillshade[y, x] >= 0);
                    Assert.True(hillshade[y, x] <= 255);
                }
        }

        [Fact]
        public void A_neighbourhood_touching_no_data_propagates_no_data()
        {
            var elevation = FlatPlane(5, 5, 10f);
            elevation[2, 2] = float.NaN;
            var slope = TerrainAnalysis.Slope(elevation, 1, 1);

            // Every cell whose 3x3 window includes (2,2) becomes no-data.
            Assert.True(float.IsNaN(slope[1, 1]));
            Assert.True(float.IsNaN(slope[3, 3]));
            // A cell far away is unaffected.
            Assert.False(float.IsNaN(slope[0, 0]));
        }

        [Fact]
        public void Output_has_the_same_dimensions_as_the_input()
        {
            var elevation = EastRisingRamp(7, 4, 1.0);
            var slope = TerrainAnalysis.Slope(elevation, 1, 1);
            var aspect = TerrainAnalysis.Aspect(elevation, 1, 1);
            var hillshade = TerrainAnalysis.Hillshade(elevation, 1, 1);

            Assert.Equal((7, 4), (slope.Width, slope.Height));
            Assert.Equal((7, 4), (aspect.Width, aspect.Height));
            Assert.Equal((7, 4), (hillshade.Width, hillshade.Height));
        }
    }
}
