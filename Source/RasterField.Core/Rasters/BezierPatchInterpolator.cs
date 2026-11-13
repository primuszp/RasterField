using System;
using System.Threading;

namespace RasterField.Rasters
{
    /// <summary>What <see cref="BezierPatchInterpolator"/> does where a patch's 4&#215;4 neighbourhood touches no-data.</summary>
    public enum BezierNoDataMode
    {
        /// <summary>Fall back to no-data-tolerant bilinear interpolation for that patch (default).</summary>
        FallbackBilinear,

        /// <summary>Leave the output no-data wherever the patch's neighbourhood is incomplete.</summary>
        NoData,
    }

    /// <summary>Parameters for <see cref="BezierPatchInterpolator"/>.</summary>
    public sealed class BezierPatchOptions
    {
        /// <summary>Subdivision factor <c>k</c>: every source cell becomes <c>k &#215; k</c> output cells. 1 = identity.</summary>
        public int Factor { get; set; } = 4;

        /// <summary>
        /// Tangent tension <c>τ</c> in [0, 1]: 1 = Catmull-Rom tangents (smooth, C¹ across patches);
        /// 0 = the patch reproduces plain bilinear interpolation exactly. In between blends the two.
        /// </summary>
        public double Tension { get; set; } = 1.0;

        /// <summary>
        /// Suppresses overshoot: tangents are limited (Fritsch–Carlson / Hyman style) so every patch
        /// edge is monotone between its corners, the twist is dropped, and the value is clamped to the
        /// patch's corner range. Useful on sharp breaks (embankments, quarry walls).
        /// </summary>
        public bool Monotone { get; set; }

        /// <summary>Behaviour next to no-data cells.</summary>
        public BezierNoDataMode NoData { get; set; } = BezierNoDataMode.FallbackBilinear;

        internal void Validate()
        {
            if (Factor < 1) throw new ArgumentOutOfRangeException(nameof(Factor), "The subdivision factor must be at least 1.");
            if (double.IsNaN(Tension) || Tension < 0 || Tension > 1)
                throw new ArgumentOutOfRangeException(nameof(Tension), "Tension must be between 0 and 1.");
        }
    }

    /// <summary>Compatibility entry points for the shared Bézier interpolation implementation.</summary>
    public static class BezierPatchInterpolator
    {
        /// <summary>Creates a finer raster over the same extent.</summary>
        public static Raster Subdivide(Raster source, BezierPatchOptions options,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Interpolation.BezierInterpolation.Subdivide(source, options, progress, cancellationToken);

        /// <summary>Subdivides a source window and removes its context margin.</summary>
        public static Raster SubdivideWindow(Raster sourceWindow, int marginCells, BezierPatchOptions options) =>
            Interpolation.BezierInterpolation.SubdivideWindow(sourceWindow, marginCells, options);

        /// <summary>Samples at a corner-addressed pixel position; the subdivision factor is ignored.</summary>
        public static float? Sample(Raster source, double pixelX, double pixelY, BezierPatchOptions options)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();
            return (float?)Interpolation.BezierInterpolation.Sample(new Interpolation.LoadedSampleGrid(source),
                pixelX, pixelY, options, false).Value;
        }
    }
}
