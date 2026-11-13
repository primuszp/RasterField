using System;
using System.Threading;
using RasterField.Rasters;

namespace RasterField.Interpolation
{
    /// <summary>Creates reusable surface samplers for loaded or windowed scalar bands.</summary>
    public static class SurfaceSampler
    {
        /// <summary>Creates bilinear sampling. Strict mode rejects any missing nonzero-weight contributor.</summary>
        public static ISurfaceSampler Bilinear(Raster source, bool strict = true) =>
            new Sampler(Loaded(source), "bilinear-v1", strict);

        /// <summary>Creates bilinear sampling with a bounded source-tile cache. The caller owns the source.</summary>
        public static ISurfaceSampler Bilinear(IRasterSource source, int band = 0, bool strict = true,
            int tileSize = 256, CancellationToken cancellationToken = default) =>
            new Sampler(Windowed(source, band, tileSize, cancellationToken), "bilinear-v1", strict, token: cancellationToken);

        /// <summary>
        /// Creates direct cubic B-spline smoothing with a strict valid 4×4 neighborhood.
        /// This is not an interpolating spline fitted through the original sample values.
        /// </summary>
        public static ISurfaceSampler BSpline(Raster source) => new Sampler(Loaded(source), "cubic-bspline-v1");

        /// <summary>Creates cubic B-spline sampling with a bounded source-tile cache; no edge extrapolation.</summary>
        public static ISurfaceSampler BSpline(IRasterSource source, int band = 0, int tileSize = 256,
            CancellationToken cancellationToken = default) =>
            new Sampler(Windowed(source, band, tileSize, cancellationToken), "cubic-bspline-v1", token: cancellationToken);

        /// <summary>
        /// Creates the existing Bézier/Catmull–Rom surface. Options are copied; Factor is ignored.
        /// By default edge reflection preserves existing behavior; strictBoundary rejects incomplete edge neighborhoods.
        /// </summary>
        public static ISurfaceSampler Bezier(Raster source, BezierPatchOptions? options = null, bool strictBoundary = false) =>
            new Sampler(Loaded(source), "bicubic-bezier-v1", strictBoundary, Copy(options));

        /// <summary>Creates Bézier sampling over a windowed source using the same global edge rules as a loaded raster.</summary>
        public static ISurfaceSampler Bezier(IRasterSource source, BezierPatchOptions? options = null,
            int band = 0, bool strictBoundary = false, int tileSize = 256, CancellationToken cancellationToken = default) =>
            new Sampler(Windowed(source, band, tileSize, cancellationToken), "bicubic-bezier-v1", strictBoundary, Copy(options), cancellationToken);

        private static ISampleGrid Loaded(Raster source) =>
            new LoadedSampleGrid(source ?? throw new ArgumentNullException(nameof(source)));

        private static ISampleGrid Windowed(IRasterSource source, int band, int tileSize, CancellationToken token)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (band < 0 || band >= source.BandCount) throw new ArgumentOutOfRangeException(nameof(band));
            if (tileSize < 2) throw new ArgumentOutOfRangeException(nameof(tileSize));
            return new WindowedSampleGrid(source, band, tileSize, token);
        }

        private static BezierPatchOptions Copy(BezierPatchOptions? options)
        {
            options ??= new BezierPatchOptions();
            var copy = new BezierPatchOptions { Factor = 1, Tension = options.Tension, Monotone = options.Monotone, NoData = options.NoData };
            copy.Validate();
            if (!Enum.IsDefined(typeof(BezierNoDataMode), copy.NoData)) throw new ArgumentOutOfRangeException(nameof(options));
            return copy;
        }

        private sealed class Sampler : ISurfaceSampler
        {
            private readonly ISampleGrid _grid;
            private readonly bool _strict;
            private readonly BezierPatchOptions? _bezier;
            private readonly CancellationToken _token;
            public Sampler(ISampleGrid grid, string methodId, bool strict = true,
                BezierPatchOptions? bezier = null, CancellationToken token = default)
            { _grid = grid; MethodId = methodId; _strict = strict; _bezier = bezier; _token = token; }
            public int Width => _grid.Width;
            public int Height => _grid.Height;
            public string MethodId { get; }
            public SurfaceSample Sample(double pixelX, double pixelY)
            {
                _token.ThrowIfCancellationRequested();
                if (_bezier != null) return BezierInterpolation.Sample(_grid, pixelX, pixelY, _bezier, _strict);
                return MethodId == "bilinear-v1"
                    ? Sampling.Bilinear(_grid, pixelX, pixelY, _strict)
                    : Sampling.BSpline(_grid, pixelX, pixelY);
            }
        }
    }
}
