using System;
using System.Collections.Generic;
using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>
    /// Read-only typed view of a <c>RegionInfo</c> sub-block of <c>RasterInfo</c>: a named
    /// region (usually a training polygon) and its outline in image cell coordinates. The
    /// generated <c>Stats</c> sub-block is left as <see cref="Raw"/> for callers that need it.
    /// </summary>
    /// <remarks>
    /// This is a convenience view only — the loss-less writer re-emits the original
    /// <see cref="Raw"/> block verbatim, so nothing here needs to be written back.
    /// </remarks>
    public sealed class RegionInfo
    {
        /// <summary>The parsed block this view was built from.</summary>
        public ErsBlock Raw { get; }

        private RegionInfo(ErsBlock raw) => Raw = raw;

        /// <summary>Region geometry type; ER Mapper uses <c>"Polygon"</c>.</summary>
        public string? Type { get; private set; }

        /// <summary>Region name shown on ER Mapper menus.</summary>
        public string? RegionName { get; private set; }

        /// <summary>Region number, when present.</summary>
        public int? Number { get; private set; }

        /// <summary>Colour index / RGB string, when present.</summary>
        public string? Colour { get; private set; }

        /// <summary>Outline vertices in image cell coordinates (x, y pairs from <c>SubRegion</c>).</summary>
        public IReadOnlyList<(double X, double Y)> SubRegion { get; private set; } = Array.Empty<(double, double)>();

        /// <summary>The generated <c>Stats</c> sub-block, if any.</summary>
        public ErsBlock? Stats => Raw.Block("Stats");

        /// <summary>Builds a <see cref="RegionInfo"/> from a parsed <c>RegionInfo</c> block.</summary>
        public static RegionInfo FromBlock(ErsBlock block)
        {
            var r = new RegionInfo(block ?? throw new ArgumentNullException(nameof(block)));
            if (block.TryGet("Type", out var t)) r.Type = t.AsString();
            if (block.TryGet("RegionName", out var n)) r.RegionName = n.AsString();
            if (block.TryGet("RegionNumber", out var num) || block.TryGet("Number", out num)) r.Number = SafeInt(num);
            if (block.TryGet("RegionColour", out var c) || block.TryGet("Colour", out c) || block.TryGet("Color", out c))
                r.Colour = c.AsString();

            if (block.TryGet("SubRegion", out var sub) && sub.IsArray)
            {
                double[] flat = sub.AsDoubleArray();
                var pts = new List<(double, double)>(flat.Length / 2);
                for (int i = 0; i + 1 < flat.Length; i += 2)
                    pts.Add((flat[i], flat[i + 1]));
                r.SubRegion = pts;
            }
            return r;
        }

        private static int? SafeInt(ErsValue v)
        {
            try { return v.AsInt32(); } catch (FormatException) { return null; } catch (OverflowException) { return null; }
        }
    }

    /// <summary>
    /// Read-only typed view of an <c>FFTInfo</c> sub-block of <c>DatasetHeader</c>: the state
    /// needed to reconstruct the original image after an inverse Fourier transform. The nested
    /// <c>CoordinateSpace</c> / <c>RasterInfo</c> describing the original image are exposed via
    /// <see cref="Raw"/>.
    /// </summary>
    public sealed class FftInfo
    {
        /// <summary>The parsed block this view was built from.</summary>
        public ErsBlock Raw { get; }

        private FftInfo(ErsBlock raw) => Raw = raw;

        /// <summary>Transform kind, e.g. <c>"FFT"</c>.</summary>
        public string? ForwardTransform { get; private set; }

        /// <summary>Spectrum extent, e.g. <c>"Full"</c>.</summary>
        public string? SpectrumAmount { get; private set; }

        /// <summary>Path of the spatial-domain dataset the transform came from.</summary>
        public string? SpatialDataset { get; private set; }

        /// <summary>Padding added in X before the transform.</summary>
        public int? PadCellX { get; private set; }

        /// <summary>Padding added in Y before the transform.</summary>
        public int? PadCellY { get; private set; }

        /// <summary>Builds an <see cref="FftInfo"/> from a parsed <c>FFTInfo</c> block.</summary>
        public static FftInfo FromBlock(ErsBlock block)
        {
            var f = new FftInfo(block ?? throw new ArgumentNullException(nameof(block)));
            if (block.TryGet("ForwardTransform", out var ft)) f.ForwardTransform = ft.AsKeyword();
            if (block.TryGet("SpectrumAmount", out var sa)) f.SpectrumAmount = sa.AsKeyword();
            if (block.TryGet("SpatialDataset", out var sd)) f.SpatialDataset = sd.AsString();
            if (block.TryGet("PadCellX", out var px)) f.PadCellX = TryInt(px);
            if (block.TryGet("PadCellY", out var py)) f.PadCellY = TryInt(py);
            return f;
        }

        private static int? TryInt(ErsValue v)
        {
            try { return v.AsInt32(); } catch (FormatException) { return null; } catch (OverflowException) { return null; }
        }
    }
}
