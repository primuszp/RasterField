using System;
using System.Collections.Generic;
using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>
    /// The <c>CellInfo</c> sub-block: physical cell size. Both dimensions default to 1.
    /// </summary>
    public sealed class CellInfo
    {
        /// <summary>X dimension of each cell, in the units of the coordinate space (default 1).</summary>
        public double XDimension { get; set; } = 1.0;

        /// <summary>Y dimension of each cell, in the units of the coordinate space (default 1).</summary>
        public double YDimension { get; set; } = 1.0;

        /// <summary>Builds a <see cref="CellInfo"/> from a parsed <c>CellInfo</c> block.</summary>
        public static CellInfo FromBlock(ErsBlock block)
        {
            var ci = new CellInfo();
            if (block.TryGet("Xdimension", out var x)) ci.XDimension = x.AsDouble();
            if (block.TryGet("Ydimension", out var y)) ci.YDimension = y.AsDouble();
            return ci;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("CellInfo");
            w.Number("Xdimension", XDimension);
            w.Number("Ydimension", YDimension);
            w.EndBlock("CellInfo");
        }
    }

    /// <summary>The optional <c>BandId</c> sub-block: what a band represents.</summary>
    public sealed class BandInfo
    {
        /// <summary>Value description shown on the band button in ER Mapper.</summary>
        public string? Value { get; set; }

        /// <summary>Units for the band description, e.g. <c>"um"</c>.</summary>
        public string? Units { get; set; }

        /// <summary>Spectral width of the band, or <see langword="null"/> when unspecified.</summary>
        public double? Width { get; set; }

        /// <summary>Builds a <see cref="BandInfo"/> from a parsed <c>BandId</c> block.</summary>
        public static BandInfo FromBlock(ErsBlock block)
        {
            var b = new BandInfo();
            if (block.TryGet("Value", out var v)) b.Value = v.AsString();
            if (block.TryGet("Units", out var u)) b.Units = u.AsString();
            if (block.TryGet("Width", out var w)) b.Width = w.AsDouble();
            return b;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("BandId");
            if (Value != null) w.Quoted("Value", Value);
            if (Width.HasValue) w.Number("Width", Width.Value);
            if (Units != null) w.Quoted("Units", Units);
            w.EndBlock("BandId");
        }
    }

    /// <summary>
    /// The compulsory <c>RasterInfo</c> block: cell type, image size, cell size,
    /// registration and per-band metadata.
    /// </summary>
    public sealed class RasterInfo
    {
        /// <summary>Numeric type of a cell.</summary>
        public ErsCellType CellType { get; set; } = ErsCellType.Unknown;

        /// <summary>Value that marks a cell as "no data", or <see langword="null"/> when not declared.</summary>
        public double? NullCellValue { get; set; }

        /// <summary>Physical cell size, or <see langword="null"/> when the <c>CellInfo</c> block is absent (implies 1 x 1).</summary>
        public CellInfo? CellInfo { get; set; }

        /// <summary>Number of image lines (rows).</summary>
        public int NrOfLines { get; set; }

        /// <summary>Number of cells per line (columns).</summary>
        public int NrOfCellsPerLine { get; set; }

        /// <summary>Number of bands. Defaults to 1.</summary>
        public int NrOfBands { get; set; } = 1;

        /// <summary>Image column of the registration cell (may be fractional; default 0 = left edge).</summary>
        public double RegistrationCellX { get; set; }

        /// <summary>Image row of the registration cell (may be fractional; default 0 = top edge).</summary>
        public double RegistrationCellY { get; set; }

        /// <summary>World location of the registration cell (required for non-RAW images).</summary>
        public RegistrationCoord? RegistrationCoord { get; set; }

        /// <summary>Per-band descriptors, in file order.</summary>
        public IList<BandInfo> Bands { get; } = new List<BandInfo>();

        /// <summary>Typed read-only views of any <c>RegionInfo</c> sub-blocks (training regions).</summary>
        public IReadOnlyList<RegionInfo> Regions { get; private set; } = Array.Empty<RegionInfo>();

        /// <summary>Camera/sensor calibration details, when present (required for orthorectification).</summary>
        public SensorInfo? SensorInfo { get; set; }

        /// <summary>Rectification (orthorectification / warp) parameters, when present.</summary>
        public WarpControl? WarpControl { get; set; }

        /// <summary>The parsed <c>RasterInfo</c> block, kept so the loss-less writer can re-emit unmodelled sub-blocks (e.g. <c>RegionInfo.Stats</c>, <c>WarpControl.ControlPoints</c>).</summary>
        public ErsBlock? RawBlock { get; private set; }

        /// <summary>Effective X cell size (falls back to 1 when <see cref="CellInfo"/> is absent).</summary>
        public double CellSizeX => CellInfo?.XDimension ?? 1.0;

        /// <summary>Effective Y cell size (falls back to 1 when <see cref="CellInfo"/> is absent).</summary>
        public double CellSizeY => CellInfo?.YDimension ?? 1.0;

        internal static readonly ISet<string> HandledEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CellType", "NullCellValue", "NrOfLines", "NrOfCellsPerLine", "NrOfBands",
            "RegistrationCellX", "RegistrationCellY",
        };

        internal static readonly ISet<string> HandledBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CellInfo", "RegistrationCoord", "BandId", "SensorInfo", "WarpControl",
        };

        /// <summary>Builds a <see cref="RasterInfo"/> from a parsed <c>RasterInfo</c> block.</summary>
        public static RasterInfo FromBlock(ErsBlock block)
        {
            var ri = new RasterInfo { RawBlock = block };
            if (block.TryGet("CellType", out var ctv)) ri.CellType = ctv.AsEnum(ErsCellType.Unknown);
            if (block.TryGet("NullCellValue", out var ncv)) ri.NullCellValue = ncv.AsDouble();
            if (block.TryGet("NrOfLines", out var nl)) ri.NrOfLines = nl.AsInt32();
            if (block.TryGet("NrOfCellsPerLine", out var ncl)) ri.NrOfCellsPerLine = ncl.AsInt32();
            if (block.TryGet("NrOfBands", out var nb)) ri.NrOfBands = nb.AsInt32();
            if (block.TryGet("RegistrationCellX", out var rcx)) ri.RegistrationCellX = rcx.AsDouble();
            if (block.TryGet("RegistrationCellY", out var rcy)) ri.RegistrationCellY = rcy.AsDouble();

            var cellInfo = block.Block("CellInfo");
            if (cellInfo != null) ri.CellInfo = CellInfo.FromBlock(cellInfo);

            var regCoord = block.Block("RegistrationCoord");
            if (regCoord != null) ri.RegistrationCoord = RegistrationCoord.FromBlock(regCoord);

            foreach (var bandBlock in block.Blocks("BandId"))
                ri.Bands.Add(BandInfo.FromBlock(bandBlock));

            var regions = new List<RegionInfo>();
            foreach (var regionBlock in block.Blocks("RegionInfo"))
                regions.Add(RegionInfo.FromBlock(regionBlock));
            if (regions.Count > 0) ri.Regions = regions;

            var sensorBlock = block.Block("SensorInfo");
            if (sensorBlock != null) ri.SensorInfo = SensorInfo.FromBlock(sensorBlock);

            var warpBlock = block.Block("WarpControl");
            if (warpBlock != null) ri.WarpControl = WarpControl.FromBlock(warpBlock);

            return ri;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("RasterInfo");
            if (CellType != ErsCellType.Unknown) w.Keyword("CellType", CellType.ToString());
            if (NullCellValue.HasValue) w.Number("NullCellValue", NullCellValue.Value);
            CellInfo?.Write(w);
            w.Number("NrOfLines", NrOfLines);
            w.Number("NrOfCellsPerLine", NrOfCellsPerLine);
            if (RegistrationCoord != null) RegistrationCoord.Write(w);
            if (RegistrationCellX != 0) w.Number("RegistrationCellX", RegistrationCellX);
            if (RegistrationCellY != 0) w.Number("RegistrationCellY", RegistrationCellY);
            w.Number("NrOfBands", NrOfBands);
            foreach (var band in Bands) band.Write(w);
            SensorInfo?.Write(w);
            WarpControl?.Write(w);

            // Re-emit anything the model does not cover (RegionInfo.Stats, WarpControl.ControlPoints, …).
            w.EmitUnhandled(RawBlock, HandledEntries, HandledBlocks);
            w.EndBlock("RasterInfo");
        }
    }
}
