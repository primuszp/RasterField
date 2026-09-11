using System;
using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>The <c>SensorType</c> entry of a <c>SensorInfo</c> block.</summary>
    public enum ErsSensorType
    {
        /// <summary>A calibrated metric (photogrammetric) camera.</summary>
        MetricCamera,
        /// <summary>An uncalibrated, non-metric camera.</summary>
        NonMetricCamera,
        /// <summary>A push-broom / line-array scanner.</summary>
        LineArray,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>The <c>PlatformType</c> entry of a <c>SensorInfo</c> block.</summary>
    public enum ErsPlatformType
    {
        /// <summary>Carried by an aircraft.</summary>
        Aerial,
        /// <summary>Ground-based.</summary>
        Terrestrial,
        /// <summary>Carried by a satellite.</summary>
        Satellite,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>
    /// One <c>FiducialPointXxx</c> sub-block of <see cref="FiducialInfo"/>: the position of a
    /// single fiducial mark, in image cell coordinates and relative to the principal point.
    /// </summary>
    public sealed class FiducialPoint
    {
        /// <summary>Whether the point is used when calculating RMS errors.</summary>
        public bool IsOn { get; set; } = true;

        /// <summary>Whether the point's position is locked against further adjustment.</summary>
        public bool IsLocked { get; set; }

        /// <summary>Cell (pixel) X position of the fiducial mark.</summary>
        public double CellX { get; set; }

        /// <summary>Cell (pixel) Y position of the fiducial mark.</summary>
        public double CellY { get; set; }

        /// <summary>Position relative to the principal point, X (mm).</summary>
        public double OffsetX { get; set; }

        /// <summary>Position relative to the principal point, Y (mm).</summary>
        public double OffsetY { get; set; }

        /// <summary>Builds a <see cref="FiducialPoint"/> from a parsed <c>FiducialPointXxx</c> block.</summary>
        public static FiducialPoint FromBlock(ErsBlock block)
        {
            var p = new FiducialPoint();
            if (block.TryGet("IsOn", out var on)) p.IsOn = on.AsBoolean();
            if (block.TryGet("IsLocked", out var locked)) p.IsLocked = locked.AsBoolean();
            if (block.TryGet("CellX", out var cx)) p.CellX = cx.AsDouble();
            if (block.TryGet("CellY", out var cy)) p.CellY = cy.AsDouble();
            if (block.TryGet("OffsetX", out var ox)) p.OffsetX = ox.AsDouble();
            if (block.TryGet("OffsetY", out var oy)) p.OffsetY = oy.AsDouble();
            return p;
        }

        internal void Write(ErsHeaderWriter w, string blockName)
        {
            w.BeginBlock(blockName);
            w.Keyword("IsOn", IsOn ? "Yes" : "No");
            w.Keyword("IsLocked", IsLocked ? "Yes" : "No");
            w.Number("CellX", CellX);
            w.Number("CellY", CellY);
            w.Number("OffsetX", OffsetX);
            w.Number("OffsetY", OffsetY);
            w.EndBlock(blockName);
        }
    }

    /// <summary>
    /// The <c>FiducialInfo</c> sub-block of <see cref="SensorInfo"/>: the camera's fiducial
    /// marks, used together with the frame's measured fiducials to solve interior orientation.
    /// </summary>
    public sealed class FiducialInfo
    {
        /// <summary>Lens-distortion offset of the principal point from the lens centre, X (mm).</summary>
        public double PrincipalPointOffsetX { get; set; }

        /// <summary>Lens-distortion offset of the principal point from the lens centre, Y (mm).</summary>
        public double PrincipalPointOffsetY { get; set; }

        /// <summary>The top-left fiducial mark.</summary>
        public FiducialPoint? TopLeft { get; set; }

        /// <summary>The top-right fiducial mark.</summary>
        public FiducialPoint? TopRight { get; set; }

        /// <summary>The bottom-left fiducial mark.</summary>
        public FiducialPoint? BottomLeft { get; set; }

        /// <summary>The bottom-right fiducial mark.</summary>
        public FiducialPoint? BottomRight { get; set; }

        /// <summary>The top-centre fiducial mark.</summary>
        public FiducialPoint? MiddleTop { get; set; }

        /// <summary>The bottom-centre fiducial mark.</summary>
        public FiducialPoint? MiddleBottom { get; set; }

        /// <summary>The left-centre fiducial mark.</summary>
        public FiducialPoint? MiddleLeft { get; set; }

        /// <summary>The right-centre fiducial mark.</summary>
        public FiducialPoint? MiddleRight { get; set; }

        /// <summary>Builds a <see cref="FiducialInfo"/> from a parsed <c>FiducialInfo</c> block.</summary>
        public static FiducialInfo FromBlock(ErsBlock block)
        {
            var f = new FiducialInfo();
            if (block.TryGet("PrinciplePointOffsetX", out var px)) f.PrincipalPointOffsetX = px.AsDouble();
            if (block.TryGet("PrinciplePointOffsetY", out var py)) f.PrincipalPointOffsetY = py.AsDouble();

            FiducialPoint? Read(string name)
            {
                var b = block.Block(name);
                return b != null ? FiducialPoint.FromBlock(b) : null;
            }

            f.TopLeft = Read("FiducialPointTopLeft");
            f.TopRight = Read("FiducialPointTopRight");
            f.BottomLeft = Read("FiducialPointBottomLeft");
            f.BottomRight = Read("FiducialPointBottomRight");
            f.MiddleTop = Read("FiducialPointMiddleTop");
            f.MiddleBottom = Read("FiducialPointMiddleBottom");
            f.MiddleLeft = Read("FiducialPointMiddleLeft");
            f.MiddleRight = Read("FiducialPointMiddleRight");
            return f;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("FiducialInfo");
            w.Number("PrinciplePointOffsetX", PrincipalPointOffsetX);
            w.Number("PrinciplePointOffsetY", PrincipalPointOffsetY);
            TopLeft?.Write(w, "FiducialPointTopLeft");
            TopRight?.Write(w, "FiducialPointTopRight");
            BottomLeft?.Write(w, "FiducialPointBottomLeft");
            BottomRight?.Write(w, "FiducialPointBottomRight");
            MiddleTop?.Write(w, "FiducialPointMiddleTop");
            MiddleBottom?.Write(w, "FiducialPointMiddleBottom");
            MiddleLeft?.Write(w, "FiducialPointMiddleLeft");
            MiddleRight?.Write(w, "FiducialPointMiddleRight");
            w.EndBlock("FiducialInfo");
        }
    }

    /// <summary>
    /// The optional <c>SensorInfo</c> sub-block of <c>RasterInfo</c>: calibration details of the
    /// camera (or other sensor) used to capture the image, required for orthorectification.
    /// </summary>
    public sealed class SensorInfo
    {
        /// <summary>Name of the camera manufacturer.</summary>
        public string? CameraManufacturer { get; set; }

        /// <summary>Camera model.</summary>
        public string? CameraModel { get; set; }

        /// <summary>Serial number of the lens.</summary>
        public string? LensSerialNr { get; set; }

        /// <summary>Date of the camera calibration report this data was obtained from.</summary>
        public DateTime? CalibrationDate { get; set; }

        /// <summary>The kind of sensor.</summary>
        public ErsSensorType SensorType { get; set; } = ErsSensorType.Unknown;

        /// <summary>The kind of platform the sensor was mounted on.</summary>
        public ErsPlatformType PlatformType { get; set; } = ErsPlatformType.Unknown;

        /// <summary>Fiducial mark positions, when present.</summary>
        public FiducialInfo? FiducialInfo { get; set; }

        /// <summary>Focal length of the lens.</summary>
        public double? FocalLength { get; set; }

        /// <summary>X image-cell position of the principal point.</summary>
        public double? PrinciplePointX { get; set; }

        /// <summary>Y image-cell position of the principal point.</summary>
        public double? PrinciplePointY { get; set; }

        /// <summary>Builds a <see cref="SensorInfo"/> from a parsed <c>SensorInfo</c> block.</summary>
        public static SensorInfo FromBlock(ErsBlock block)
        {
            var s = new SensorInfo();
            if (block.TryGet("CameraManufacturer", out var cm)) s.CameraManufacturer = cm.AsString();
            if (block.TryGet("CameraModel", out var mo)) s.CameraModel = mo.AsString();
            if (block.TryGet("LensSerialNr", out var ln)) s.LensSerialNr = ln.AsString();
            if (block.TryGet("CalibrationDate", out var cd)) s.CalibrationDate = cd.AsDateTimeUtc();
            if (block.TryGet("SensorType", out var st)) s.SensorType = st.AsEnum(ErsSensorType.Unknown);
            if (block.TryGet("PlatformType", out var pt)) s.PlatformType = pt.AsEnum(ErsPlatformType.Unknown);
            if (block.TryGet("FocalLength", out var fl)) s.FocalLength = fl.AsDouble();
            if (block.TryGet("PrinciplePointX", out var ppx)) s.PrinciplePointX = ppx.AsDouble();
            if (block.TryGet("PrinciplePointY", out var ppy)) s.PrinciplePointY = ppy.AsDouble();

            var fid = block.Block("FiducialInfo");
            if (fid != null) s.FiducialInfo = FiducialInfo.FromBlock(fid);
            return s;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("SensorInfo");
            if (CameraManufacturer != null) w.Quoted("CameraManufacturer", CameraManufacturer);
            if (CameraModel != null) w.Quoted("CameraModel", CameraModel);
            if (LensSerialNr != null) w.Quoted("LensSerialNr", LensSerialNr);
            if (CalibrationDate.HasValue) w.Raw("CalibrationDate", FormatGmt(CalibrationDate.Value));
            if (SensorType != ErsSensorType.Unknown) w.Keyword("SensorType", SensorType.ToString());
            if (PlatformType != ErsPlatformType.Unknown) w.Keyword("PlatformType", PlatformType.ToString());
            FiducialInfo?.Write(w);
            if (FocalLength.HasValue) w.Number("FocalLength", FocalLength.Value);
            if (PrinciplePointX.HasValue) w.Number("PrinciplePointX", PrinciplePointX.Value);
            if (PrinciplePointY.HasValue) w.Number("PrinciplePointY", PrinciplePointY.Value);
            w.EndBlock("SensorInfo");
        }

        private static string FormatGmt(DateTime utc) =>
            utc.ToString("ddd MMM d HH:mm:ss 'GMT' yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}
