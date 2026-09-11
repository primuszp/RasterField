using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>The <c>WarpType</c> entry of a <c>WarpControl</c> block.</summary>
    public enum ErsWarpType
    {
        /// <summary>Orthorectification using a DEM and exterior orientation.</summary>
        Ortho,
        /// <summary>Orthorectification using additional sensor-specific parameters.</summary>
        OrthoAdvanced,
        /// <summary>A polynomial (rubber-sheet) warp from ground control points.</summary>
        Polynomial,
        /// <summary>A piecewise-linear (triangulated) warp from ground control points.</summary>
        Triangulation,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>
    /// The <c>Correction</c> sub-block of <c>WarpControl</c>: which distortion corrections are
    /// applied during geocoding. Per the guide this is "currently not used by ER Mapper" but is
    /// still part of the file format.
    /// </summary>
    public sealed class WarpCorrection
    {
        /// <summary>Correct for radial lens distortion.</summary>
        public bool RadialLens { get; set; }
        /// <summary>Correct for other (polynomial) lens distortion.</summary>
        public bool PolyLens { get; set; }
        /// <summary>Correct for atmospheric distortion.</summary>
        public bool Atmospheric { get; set; }
        /// <summary>Correct for the earth's curvature.</summary>
        public bool EarthCurvature { get; set; }

        /// <summary>Builds a <see cref="WarpCorrection"/> from a parsed <c>Correction</c> block.</summary>
        public static WarpCorrection FromBlock(ErsBlock block)
        {
            var c = new WarpCorrection();
            if (block.TryGet("RadialLens", out var r)) c.RadialLens = r.AsBoolean();
            if (block.TryGet("PolyLens", out var p)) c.PolyLens = p.AsBoolean();
            if (block.TryGet("Atmospheric", out var a)) c.Atmospheric = a.AsBoolean();
            if (block.TryGet("EarthCurvature", out var e)) c.EarthCurvature = e.AsBoolean();
            return c;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("Correction");
            w.Keyword("RadialLens", RadialLens ? "Yes" : "No");
            w.Keyword("PolyLens", PolyLens ? "Yes" : "No");
            w.Keyword("Atmospheric", Atmospheric ? "Yes" : "No");
            w.Keyword("EarthCurvature", EarthCurvature ? "Yes" : "No");
            w.EndBlock("Correction");
        }
    }

    /// <summary>
    /// The <c>GivenOrthoInfo</c> sub-block of <c>WarpControl</c>: the sensor platform's exterior
    /// orientation (position and attitude) at the moment of capture, used to orthorectify the image.
    /// </summary>
    public sealed class GivenOrthoInfo
    {
        /// <summary>Pitch about the Y axis, radians.</summary>
        public double AttitudeOmega { get; set; }
        /// <summary>Roll about the X axis, radians.</summary>
        public double AttitudePhi { get; set; }
        /// <summary>Yaw about the Z axis, radians.</summary>
        public double AttitudeKappa { get; set; }
        /// <summary>Camera exposure centre, X, in the units of <see cref="CoordinateSpace"/>.</summary>
        public double ExposureCenterX { get; set; }
        /// <summary>Camera exposure centre, Y.</summary>
        public double ExposureCenterY { get; set; }
        /// <summary>Camera exposure centre height.</summary>
        public double ExposureCenterZ { get; set; }
        /// <summary>Image scale, calculated internally by ER Mapper.</summary>
        public double Scale { get; set; }
        /// <summary>The coordinate space the exposure centre / attitude are expressed in.</summary>
        public CoordinateSpace? CoordinateSpace { get; set; }

        /// <summary>Builds a <see cref="GivenOrthoInfo"/> from a parsed <c>GivenOrthoInfo</c> block.</summary>
        public static GivenOrthoInfo FromBlock(ErsBlock block)
        {
            var g = new GivenOrthoInfo();
            if (block.TryGet("AttitudeOmega", out var om)) g.AttitudeOmega = om.AsDouble();
            if (block.TryGet("AttitudePhi", out var ph)) g.AttitudePhi = ph.AsDouble();
            if (block.TryGet("AttitudeKappa", out var ka)) g.AttitudeKappa = ka.AsDouble();
            if (block.TryGet("ExposureCenterX", out var ex)) g.ExposureCenterX = ex.AsDouble();
            if (block.TryGet("ExposureCenterY", out var ey)) g.ExposureCenterY = ey.AsDouble();
            if (block.TryGet("ExposureCenterZ", out var ez)) g.ExposureCenterZ = ez.AsDouble();
            if (block.TryGet("SCALE", out var sc)) g.Scale = sc.AsDouble();

            var cs = block.Block("CoordinateSpace");
            if (cs != null) g.CoordinateSpace = ErMapper.CoordinateSpace.FromBlock(cs);
            return g;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("GivenOrthoInfo");
            w.Number("AttitudeOmega", AttitudeOmega);
            w.Number("AttitudePhi", AttitudePhi);
            w.Number("AttitudeKappa", AttitudeKappa);
            w.Number("ExposureCenterX", ExposureCenterX);
            w.Number("ExposureCenterY", ExposureCenterY);
            w.Number("ExposureCenterZ", ExposureCenterZ);
            w.Number("SCALE", Scale);
            CoordinateSpace?.Write(w);
            w.EndBlock("GivenOrthoInfo");
        }
    }

    /// <summary>
    /// The optional <c>WarpControl</c> sub-block of <c>RasterInfo</c>: parameters for rectifying
    /// (orthorectifying / warping) the image, describing the <i>output</i> of that process rather
    /// than the image this header itself describes.
    /// </summary>
    /// <remarks>
    /// The <c>ControlPoints</c> array (ground control points, generated by ER Mapper) is not
    /// individually modelled — it is preserved verbatim through the loss-less writer along with
    /// anything else this type does not expose.
    /// </remarks>
    public sealed class WarpControl
    {
        /// <summary>The kind of geocoding to perform.</summary>
        public ErsWarpType WarpType { get; set; } = ErsWarpType.Unknown;
        /// <summary>Resampling kernel keyword, e.g. <c>"Nearest"</c> or <c>"Bilinear"</c>.</summary>
        public string? WarpSampling { get; set; }
        /// <summary>Anti-clockwise rotation to apply, in decimal degrees.</summary>
        public double Rotation { get; set; }
        /// <summary>Path of the DEM (<c>.ers</c>) used for orthorectification.</summary>
        public string? DemFile { get; set; }
        /// <summary>Band of <see cref="DemFile"/> to use.</summary>
        public int? DemBandNr { get; set; }
        /// <summary>Whether ground control points were chosen with a digitizer.</summary>
        public bool ChooseGcpsFromDigitizer { get; set; }
        /// <summary>Whether ground control points were chosen from a geocoded reference image.</summary>
        public bool ChooseGcpsFromImage { get; set; }
        /// <summary>Path of the algorithm (<c>.alg</c>) the ground control points were chosen from.</summary>
        public string? GcpsChosenFrom { get; set; }
        /// <summary>Path the rectified image will be (or was) written to.</summary>
        public string? OutputFile { get; set; }
        /// <summary>Whether a single average height is used instead of a DEM.</summary>
        public bool UseAverageHeight { get; set; }
        /// <summary>The average height used when <see cref="UseAverageHeight"/> is set.</summary>
        public double? AverageHeight { get; set; }
        /// <summary>Output cell size, X.</summary>
        public double? OutputCellSizeX { get; set; }
        /// <summary>Output cell size, Y.</summary>
        public double? OutputCellSizeY { get; set; }
        /// <summary>Whether the output image has no-data cells.</summary>
        public bool OutputHasNullCells { get; set; }
        /// <summary>The no-data value of the output image.</summary>
        public double? OutputNullCellValue { get; set; }
        /// <summary>Which distortion corrections are applied.</summary>
        public WarpCorrection? Correction { get; set; }
        /// <summary>Exterior orientation used to orthorectify the image.</summary>
        public GivenOrthoInfo? GivenOrthoInfo { get; set; }
        /// <summary>The coordinate space of the rectified output image.</summary>
        public CoordinateSpace? CoordinateSpace { get; set; }
        /// <summary>The world-coordinate extent of the rectified output image.</summary>
        public Extents? Extents { get; set; }

        /// <summary>The parsed <c>WarpControl</c> block, kept so the loss-less writer can re-emit unmodelled entries (notably <c>ControlPoints</c>).</summary>
        public ErsBlock? RawBlock { get; private set; }

        private static readonly System.Collections.Generic.HashSet<string> HandledEntries =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "WarpType", "WarpSampling", "Rotation", "DemFile", "DemBandNr",
                "ChooseGcpsFromDigitizer", "ChooseGcpsFromImage", "GcpsChosenFrom", "OutputFile",
                "UseAverageHeight", "AverageHeight", "OutputCellSizeX", "OutputCellSizeY",
                "OutputHasNullCells", "OutputNullCellValue",
            };

        private static readonly System.Collections.Generic.HashSet<string> HandledBlocks =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "Correction", "GivenOrthoInfo", "CoordinateSpace", "Extents",
            };

        /// <summary>Builds a <see cref="WarpControl"/> from a parsed <c>WarpControl</c> block.</summary>
        public static WarpControl FromBlock(ErsBlock block)
        {
            var w = new WarpControl { RawBlock = block };
            if (block.TryGet("WarpType", out var wt)) w.WarpType = wt.AsEnum(ErsWarpType.Unknown);
            if (block.TryGet("WarpSampling", out var ws)) w.WarpSampling = ws.AsKeyword();
            if (block.TryGet("Rotation", out var rot)) w.Rotation = rot.AsDouble();
            if (block.TryGet("DemFile", out var dem)) w.DemFile = dem.AsString();
            if (block.TryGet("DemBandNr", out var dbn)) w.DemBandNr = dbn.AsInt32();
            if (block.TryGet("ChooseGcpsFromDigitizer", out var cfd)) w.ChooseGcpsFromDigitizer = cfd.AsBoolean();
            if (block.TryGet("ChooseGcpsFromImage", out var cfi)) w.ChooseGcpsFromImage = cfi.AsBoolean();
            if (block.TryGet("GcpsChosenFrom", out var gcf)) w.GcpsChosenFrom = gcf.AsString();
            if (block.TryGet("OutputFile", out var of)) w.OutputFile = of.AsString();
            if (block.TryGet("UseAverageHeight", out var uah)) w.UseAverageHeight = uah.AsBoolean();
            if (block.TryGet("AverageHeight", out var ah)) w.AverageHeight = ah.AsDouble();
            if (block.TryGet("OutputCellSizeX", out var ocx)) w.OutputCellSizeX = ocx.AsDouble();
            if (block.TryGet("OutputCellSizeY", out var ocy)) w.OutputCellSizeY = ocy.AsDouble();
            if (block.TryGet("OutputHasNullCells", out var ohn)) w.OutputHasNullCells = ohn.AsBoolean();
            if (block.TryGet("OutputNullCellValue", out var onv)) w.OutputNullCellValue = onv.AsDouble();

            var correction = block.Block("Correction");
            if (correction != null) w.Correction = WarpCorrection.FromBlock(correction);

            var given = block.Block("GivenOrthoInfo");
            if (given != null) w.GivenOrthoInfo = GivenOrthoInfo.FromBlock(given);

            var cs = block.Block("CoordinateSpace");
            if (cs != null) w.CoordinateSpace = ErMapper.CoordinateSpace.FromBlock(cs);

            var extents = block.Block("Extents");
            if (extents != null) w.Extents = ErMapper.Extents.FromBlock(extents);

            return w;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("WarpControl");
            if (WarpType != ErsWarpType.Unknown) w.Keyword("WarpType", WarpType.ToString());
            if (WarpSampling != null) w.Keyword("WarpSampling", WarpSampling);
            w.Number("Rotation", Rotation);
            if (DemFile != null) w.Quoted("DemFile", DemFile);
            if (DemBandNr.HasValue) w.Number("DemBandNr", DemBandNr.Value);
            w.Keyword("ChooseGcpsFromDigitizer", ChooseGcpsFromDigitizer ? "Yes" : "No");
            w.Keyword("ChooseGcpsFromImage", ChooseGcpsFromImage ? "Yes" : "No");
            if (GcpsChosenFrom != null) w.Quoted("GcpsChosenFrom", GcpsChosenFrom);
            if (OutputFile != null) w.Quoted("OutputFile", OutputFile);
            w.Keyword("UseAverageHeight", UseAverageHeight ? "Yes" : "No");
            if (AverageHeight.HasValue) w.Number("AverageHeight", AverageHeight.Value);
            if (OutputCellSizeX.HasValue) w.Number("OutputCellSizeX", OutputCellSizeX.Value);
            if (OutputCellSizeY.HasValue) w.Number("OutputCellSizeY", OutputCellSizeY.Value);
            w.Keyword("OutputHasNullCells", OutputHasNullCells ? "Yes" : "No");
            if (OutputNullCellValue.HasValue) w.Number("OutputNullCellValue", OutputNullCellValue.Value);
            Correction?.Write(w);
            GivenOrthoInfo?.Write(w);
            CoordinateSpace?.Write(w);
            Extents?.Write(w);
            w.EmitUnhandled(RawBlock, HandledEntries, HandledBlocks);
            w.EndBlock("WarpControl");
        }
    }
}
