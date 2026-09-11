using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class CurvatureTests
    {
        private static Raster FlatPlane(int w, int h, float elevation)
        {
            var r = new Raster(w, h);
            for (int i = 0; i < r.Samples.Length; i++) r.Samples[i] = elevation;
            return r;
        }

        /// <summary>An isotropic paraboloid dome, z = peak - a*(x^2+y^2), sampled on a unit grid centred at (cx,cy).</summary>
        private static Raster Dome(int size, double cx, double cy, double a, double peak = 1000)
        {
            var r = new Raster(size, size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    r[y, x] = (float)(peak - a * (dx * dx + dy * dy));
                }
            return r;
        }

        [Fact]
        public void A_flat_plane_has_zero_curvature_everywhere()
        {
            var elevation = FlatPlane(10, 10, 500f);
            var general = TerrainAnalysis.Curvature(elevation, 1, 1, CurvatureType.General);
            var profile = TerrainAnalysis.Curvature(elevation, 1, 1, CurvatureType.Profile);
            var plan = TerrainAnalysis.Curvature(elevation, 1, 1, CurvatureType.Plan);

            for (int y = 1; y < 9; y++)
                for (int x = 1; x < 9; x++)
                {
                    Assert.Equal(0.0, general[y, x], 5);
                    Assert.Equal(0.0, profile[y, x], 5);
                    Assert.Equal(0.0, plan[y, x], 5);
                }
        }

        [Fact]
        public void A_dome_is_convex_a_bowl_is_concave()
        {
            const int size = 21;
            var dome = Dome(size, size / 2.0, size / 2.0, a: 0.5);   // peak - a*r^2: convex hill
            var bowl = Dome(size, size / 2.0, size / 2.0, a: -0.5);  // peak + a*r^2: concave valley

            var domeCurv = TerrainAnalysis.Curvature(dome, 1, 1);
            var bowlCurv = TerrainAnalysis.Curvature(bowl, 1, 1);

            Assert.True(domeCurv[size / 2, size / 2] > 0);
            Assert.True(bowlCurv[size / 2, size / 2] < 0);
        }

        [Fact]
        public void General_curvature_of_an_exact_paraboloid_matches_the_analytic_value_everywhere()
        {
            // z = peak - a*(x^2+y^2): f_xx = f_yy = -2a everywhere (constant), independent of position.
            // General curvature = -2*(D+E) = -2*(f_xx/2 + f_yy/2) = -(f_xx+f_yy) = 4a.
            const int size = 21;
            const double a = 0.7;
            var dome = Dome(size, size / 2.0, size / 2.0, a);
            var curv = TerrainAnalysis.Curvature(dome, 1, 1, CurvatureType.General);

            for (int y = 2; y < size - 2; y++)
                for (int x = 2; x < size - 2; x++)
                    Assert.Equal(4 * a, curv[y, x], 3);
        }

        [Fact]
        public void Profile_and_plan_curvature_are_constant_across_an_isotropic_paraboloid()
        {
            // For an isotropic Hessian (D=E, F=0), the simplified Zevenbergen-Thorne profile/plan
            // formulas collapse to the same constant value everywhere on the surface, not just at
            // the apex - both should equal 2a here (see the type's remarks for the derivation).
            const int size = 25;
            const double a = 0.4;
            var dome = Dome(size, size / 2.0, size / 2.0, a);

            var profile = TerrainAnalysis.Curvature(dome, 1, 1, CurvatureType.Profile);
            var plan = TerrainAnalysis.Curvature(dome, 1, 1, CurvatureType.Plan);

            // A selection of points at varying distance/direction from the centre, avoiding the
            // exact apex (where the fallback path is taken) and the edge (window clamping).
            (int X, int Y)[] points = { (14, 14), (5, 12), (12, 20), (18, 6), (7, 7) };
            foreach (var (x, y) in points)
            {
                Assert.Equal(2 * a, profile[y, x], 2);
                Assert.Equal(2 * a, plan[y, x], 2);
            }
        }

        [Fact]
        public void At_the_apex_profile_and_plan_equal_half_the_general_curvature()
        {
            // At an isotropic extremum the gradient vanishes, so the directional profile/plan
            // formulas fall back to the average over every approach direction: -(D+E), i.e.
            // exactly half of General's -2(D+E). This also happens to be the true (not just
            // averaged) value here, since the dome is isotropic at every point, not only its apex.
            const int size = 21;
            const double a = 0.5;
            const int cc = 10; // an exact grid point, so the gradient is exactly zero there
            var dome = Dome(size, cc, cc, a);

            var general = TerrainAnalysis.Curvature(dome, 1, 1, CurvatureType.General);
            var profile = TerrainAnalysis.Curvature(dome, 1, 1, CurvatureType.Profile);
            var plan = TerrainAnalysis.Curvature(dome, 1, 1, CurvatureType.Plan);

            Assert.Equal(general[cc, cc] / 2.0, profile[cc, cc], 4);
            Assert.Equal(general[cc, cc] / 2.0, plan[cc, cc], 4);
        }

        [Fact]
        public void A_neighbourhood_touching_no_data_propagates_no_data()
        {
            var elevation = FlatPlane(6, 6, 10f);
            elevation[3, 3] = float.NaN;
            var curv = TerrainAnalysis.Curvature(elevation, 1, 1);

            Assert.True(float.IsNaN(curv[2, 2]));
            Assert.False(float.IsNaN(curv[0, 0]));
        }

        [Fact]
        public void Output_has_the_same_dimensions_as_the_input()
        {
            var elevation = FlatPlane(7, 4, 1f);
            var curv = TerrainAnalysis.Curvature(elevation, 1, 1);
            Assert.Equal((7, 4), (curv.Width, curv.Height));
        }
    }
}
