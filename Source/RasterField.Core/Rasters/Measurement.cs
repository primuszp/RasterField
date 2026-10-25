using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>Planar measurements of paths and polygons in world units, plus the terrain-following (3-D) length.</summary>
    public static class Measurement
    {
        /// <summary>Length of the polyline through <paramref name="points"/>.</summary>
        public static double Length(IReadOnlyList<(double X, double Y)> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            double len = 0;
            for (int i = 1; i < points.Count; i++)
                len += Math.Sqrt(Sq(points[i].X - points[i - 1].X) + Sq(points[i].Y - points[i - 1].Y));
            return len;
        }

        /// <summary>Perimeter of the (implicitly closed) polygon.</summary>
        public static double Perimeter(IReadOnlyList<(double X, double Y)> polygon)
        {
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));
            if (polygon.Count < 2) return 0;
            var last = polygon[polygon.Count - 1];
            return Length(polygon) + Math.Sqrt(Sq(polygon[0].X - last.X) + Sq(polygon[0].Y - last.Y));
        }

        /// <summary>Area of the (implicitly closed, simple) polygon — shoelace formula, always positive.</summary>
        public static double Area(IReadOnlyList<(double X, double Y)> polygon)
        {
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));
            if (polygon.Count < 3) return 0;
            double twice = 0;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                twice += (polygon[j].X * polygon[i].Y) - (polygon[i].X * polygon[j].Y);
            return Math.Abs(twice) / 2;
        }

        /// <summary>
        /// Terrain-following length of a path over an elevation raster: the path is sampled every
        /// <paramref name="spacing"/> world units (bilinear) and each step contributes
        /// √(horizontal² + Δz²). Steps touching no-data count only their horizontal length.
        /// </summary>
        public static double SurfaceLength(Raster elevation, RasterGeoReference geoReference, IReadOnlyList<(double X, double Y)> points, double spacing)
        {
            var samples = RasterProfiler.SamplePolylineWorld(elevation, geoReference, points, spacing);
            double len = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                double d = samples[i].Distance - samples[i - 1].Distance;
                float? z0 = samples[i - 1].Value, z1 = samples[i].Value;
                len += z0.HasValue && z1.HasValue ? Math.Sqrt(d * d + Sq(z1.Value - z0.Value)) : d;
            }
            return len;
        }

        private static double Sq(double v) => v * v;
    }
}
