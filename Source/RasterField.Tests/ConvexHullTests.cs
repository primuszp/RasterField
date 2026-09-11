using System.Linq;
using RasterField.Rasters;
using Xunit;

namespace RasterField.Tests
{
    public class ConvexHullTests
    {
        [Fact]
        public void Fewer_than_three_points_are_returned_as_is()
        {
            var single = ConvexHull.Compute(new[] { (0, 0) });
            Assert.Single(single);

            var two = ConvexHull.Compute(new[] { (0, 0), (5, 5) });
            Assert.Equal(2, two.Count);
        }

        [Fact]
        public void A_squares_corners_are_exactly_the_hull()
        {
            var points = new[] { (0, 0), (10, 0), (10, 10), (0, 10) };
            var hull = ConvexHull.Compute(points);

            Assert.Equal(4, hull.Count);
            foreach (var corner in points)
                Assert.Contains(corner, hull);
        }

        [Fact]
        public void An_interior_point_is_excluded_from_the_hull()
        {
            var points = new[] { (0, 0), (10, 0), (10, 10), (0, 10), (5, 5) };
            var hull = ConvexHull.Compute(points);

            Assert.Equal(4, hull.Count);
            Assert.DoesNotContain((5, 5), hull);
        }

        [Fact]
        public void A_point_on_an_edge_is_excluded_from_the_hull()
        {
            // (5,0) lies exactly on the edge between (0,0) and (10,0) — not a vertex.
            var points = new[] { (0, 0), (10, 0), (10, 10), (0, 10), (5, 0) };
            var hull = ConvexHull.Compute(points);

            Assert.Equal(4, hull.Count);
            Assert.DoesNotContain((5, 0), hull);
        }

        [Fact]
        public void Collinear_points_reduce_to_their_two_extremes()
        {
            var points = new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4) };
            var hull = ConvexHull.Compute(points);

            Assert.True(hull.Count <= 2);
            Assert.Contains((0, 0), hull);
            Assert.Contains((4, 4), hull);
        }

        [Fact]
        public void Duplicate_points_do_not_produce_duplicate_hull_vertices()
        {
            var points = new[] { (0, 0), (0, 0), (10, 0), (10, 0), (10, 10), (0, 10) };
            var hull = ConvexHull.Compute(points);

            Assert.Equal(4, hull.Count);
            Assert.Equal(hull.Count, hull.Distinct().Count());
        }

        [Fact]
        public void A_triangles_hull_matches_its_three_vertices()
        {
            var points = new[] { (0, 0), (20, 0), (10, 20) };
            var hull = ConvexHull.Compute(points);

            Assert.Equal(3, hull.Count);
            foreach (var p in points)
                Assert.Contains(p, hull);
        }
    }
}
