using System;
using System.Threading;
using RasterField.Rasters;

namespace RasterField.Interpolation
{
    /// <summary>A resampled scalar band with its matching affine georeference.</summary>
    public sealed class ResampledRaster
    {
        internal ResampledRaster(Raster raster, RasterGeoReference geoReference)
        { Raster = raster; GeoReference = geoReference; }
        /// <summary>Float output values; missing samples are NaN.</summary>
        public Raster Raster { get; }
        /// <summary>The output grid, preserving rotation and pixel orientation.</summary>
        public RasterGeoReference GeoReference { get; }
    }

    /// <summary>Builds rasters from any shared scalar surface; does not add measured information.</summary>
    public static class RasterResampler
    {
        /// <summary>Refines the grid by an integer factor, preserving its exact affine footprint.</summary>
        public static ResampledRaster Subdivide(WorldSurfaceSampler source, int factor,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
            int width = checked(source.Surface.Width * factor), height = checked(source.Surface.Height * factor);
            ValidateSize(width, height);
            var (a, b, c, d, e, f) = source.GeoReference.GeoTransform;
            var geo = new RasterGeoReference(width, height, a, b / factor, c / factor, d, e / factor, f / factor);
            // Direct pixel mapping avoids inverse-transform rounding at source cell boundaries.
            return Build(width, height, geo,
                (x, y) => source.Surface.Sample((x + 0.5) / factor, (y + 0.5) / factor), progress, cancellationToken);
        }

        /// <summary>
        /// Samples each target cell center. Both grids must use the same CRS and units.
        /// Point resampling is intended for refinement, not anti-aliased downsampling.
        /// </summary>
        public static ResampledRaster Resample(WorldSurfaceSampler source, RasterGeoReference target,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            WorldSurfaceSampler.ValidateTransform(target);
            ValidateSize(target.Width, target.Height);
            return Build(target.Width, target.Height, target, (x, y) =>
            {
                var (wx, wy) = target.CellCentreToWorld(x, y);
                return source.Sample(wx, wy);
            }, progress, cancellationToken);
        }

        private static void ValidateSize(int width, int height)
        {
            if (width < 1 || height < 1 || (long)width * height > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(width), "The output must be a nonempty raster that fits in memory.");
        }

        private static ResampledRaster Build(int width, int height, RasterGeoReference geo,
            Func<int, int, SurfaceSample> sample, IProgress<double>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var output = new Raster(width, height);
            // Sequential evaluation also supports samplers with a single-threaded window cache.
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    if ((x & 255) == 0) token.ThrowIfCancellationRequested();
                    float value = (float)(sample(x, y).Value ?? double.NaN);
                    output.Samples[y * width + x] = float.IsInfinity(value) ? float.NaN : value;
                }
                progress?.Report((y + 1.0) / height);
            }
            return new ResampledRaster(output, geo);
        }
    }
}
