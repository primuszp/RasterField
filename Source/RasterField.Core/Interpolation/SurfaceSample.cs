using System;

namespace RasterField.Interpolation
{
    /// <summary>Conditions encountered during sampling, including explicit quality reductions.</summary>
    [Flags]
    public enum SurfaceSampleFlags
    {
        /// <summary>The requested interpolation used a complete, in-bounds neighborhood.</summary>
        None = 0,
        /// <summary>The position lies outside the raster footprint.</summary>
        OutsideDomain = 1,
        /// <summary>Required source values are missing or non-finite.</summary>
        NoData = 2,
        /// <summary>The method's required neighborhood is incomplete.</summary>
        IncompleteNeighborhood = 4,
        /// <summary>The input position is not finite.</summary>
        InvalidCoordinate = 8,
        /// <summary>Bilinear weights were renormalized around missing or out-of-bounds samples.</summary>
        Renormalized = 16,
        /// <summary>The surface uses reflected/extrapolated samples at the raster boundary.</summary>
        Extrapolated = 32,
        /// <summary>An incomplete cubic neighborhood was replaced by bilinear sampling.</summary>
        FallbackBilinear = 64
    }

    /// <summary>A double-precision surface value and the conditions under which it was obtained.</summary>
    public readonly struct SurfaceSample
    {
        /// <summary>Creates a result. Non-finite values are reported as missing data.</summary>
        public SurfaceSample(double? value, SurfaceSampleFlags flags = SurfaceSampleFlags.None)
        {
            if (value.HasValue && !Sampling.Finite(value.Value))
            {
                value = null;
                flags |= SurfaceSampleFlags.NoData;
            }
            Value = value;
            Flags = flags;
        }

        /// <summary>The interpolated value; null means no usable result.</summary>
        public double? Value { get; }
        /// <summary>Diagnostics, also retained for usable fallback results.</summary>
        public SurfaceSampleFlags Flags { get; }
        /// <summary>Whether a finite value was obtained. Inspect flags for quality reductions.</summary>
        public bool IsValid => Value.HasValue;
    }

    /// <summary>
    /// A continuous scalar raster surface in corner-addressed pixel coordinates.
    /// Cell (x,y)'s center is (x+0.5,y+0.5). Implementations are independent of units and CRS.
    /// Keep the source alive and unchanged; use an instance from only one caller at a time.
    /// Values are in source-band units; scale/offset must already be applied by the source adapter.
    /// </summary>
    public interface ISurfaceSampler
    {
        /// <summary>Source raster width.</summary>
        int Width { get; }
        /// <summary>Source raster height.</summary>
        int Height { get; }
        /// <summary>Stable method/version identifier for provenance.</summary>
        string MethodId { get; }
        /// <summary>Samples without creating a refined raster.</summary>
        SurfaceSample Sample(double pixelX, double pixelY);
    }
}
