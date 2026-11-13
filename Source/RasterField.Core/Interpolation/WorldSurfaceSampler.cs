using System;
using RasterField.Rasters;

namespace RasterField.Interpolation
{
    /// <summary>Maps world positions to an existing pixel-coordinate surface. No CRS reprojection is performed.</summary>
    public sealed class WorldSurfaceSampler
    {
        /// <summary>Creates an adapter with matching source dimensions and an invertible finite transform.</summary>
        public WorldSurfaceSampler(ISurfaceSampler surface, RasterGeoReference geoReference)
        {
            Surface = surface ?? throw new ArgumentNullException(nameof(surface));
            GeoReference = geoReference ?? throw new ArgumentNullException(nameof(geoReference));
            ValidateTransform(geoReference);
            if (surface.Width != geoReference.Width || surface.Height != geoReference.Height)
                throw new ArgumentException("Surface and georeference dimensions must match.", nameof(geoReference));
        }

        /// <summary>The underlying pixel-coordinate surface.</summary>
        public ISurfaceSampler Surface { get; }
        /// <summary>The source affine transform.</summary>
        public RasterGeoReference GeoReference { get; }
        /// <summary>Returns a sample at a world position in the source coordinate system.</summary>
        public SurfaceSample Sample(double worldX, double worldY)
        {
            if (!Sampling.Finite(worldX) || !Sampling.Finite(worldY))
                return new SurfaceSample(null, SurfaceSampleFlags.InvalidCoordinate);
            var (x, y) = GeoReference.WorldToPixel(worldX, worldY);
            return Surface.Sample(x, y);
        }

        internal static void ValidateTransform(RasterGeoReference geo)
        {
            var (a, b, c, d, e, f) = geo.GeoTransform;
            if (!geo.IsInvertible || !Sampling.Finite(a) || !Sampling.Finite(b) || !Sampling.Finite(c) ||
                !Sampling.Finite(d) || !Sampling.Finite(e) || !Sampling.Finite(f))
                throw new ArgumentException("A finite, invertible georeference is required.", nameof(geo));
        }
    }
}
