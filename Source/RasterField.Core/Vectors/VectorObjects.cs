using System.Collections.Generic;

namespace RasterField.Vectors
{
    /// <summary>
    /// Base class for the object types found in an ER Mapper vector data file. Field names
    /// follow the specifiers used in the <i>ERDAS ER Mapper Customization Guide</i>,
    /// "Vector Datasets and Header Files (.erv)" chapter.
    /// </summary>
    public abstract class VectorObject
    {
        /// <summary>Free-form label/attribute text carried by the object (may be <see langword="null"/> or empty).</summary>
        public string? Attribute { get; set; }
    }

    /// <summary>A single point: <c>point(attribute, x, y, spare, r, g, b, page)</c>.</summary>
    public sealed class VectorPoint : VectorObject
    {
        /// <summary>X coordinate, in the units declared by the header's <c>CoordinateSpace</c>.</summary>
        public double X { get; set; }

        /// <summary>Y coordinate.</summary>
        public double Y { get; set; }

        /// <summary>Reserved; ER Mapper writes 0.</summary>
        public int Spare { get; set; }

        /// <summary>Red component, 0-255, or -1 (together with <see cref="G"/>/<see cref="B"/>) for the default layer colour.</summary>
        public int R { get; set; } = -1;

        /// <summary>Green component, 0-255, or -1 for the default layer colour.</summary>
        public int G { get; set; } = -1;

        /// <summary>Blue component, 0-255, or -1 for the default layer colour.</summary>
        public int B { get; set; } = -1;

        /// <summary><see langword="false"/> = image coordinates, <see langword="true"/> = page-relative.</summary>
        public bool Page { get; set; }
    }

    /// <summary>Shared fields of <see cref="VectorBox"/> and <see cref="VectorOval"/>.</summary>
    public abstract class VectorRectangleObject : VectorObject
    {
        /// <summary>Left/top X of the bounding rectangle.</summary>
        public double Ltx { get; set; }

        /// <summary>Left/top Y of the bounding rectangle.</summary>
        public double Lty { get; set; }

        /// <summary>Right/bottom X of the bounding rectangle.</summary>
        public double Rbx { get; set; }

        /// <summary>Right/bottom Y of the bounding rectangle.</summary>
        public double Rby { get; set; }

        /// <summary>Fill style, 0-19.</summary>
        public int Fill { get; set; }

        /// <summary>Line width in points.</summary>
        public double Width { get; set; } = 1;

        /// <summary>Pen (line) style, 0-23.</summary>
        public int Pen { get; set; }

        /// <summary>Red component, 0-255, or -1 for the default layer colour.</summary>
        public int R { get; set; } = -1;

        /// <summary>Green component, 0-255, or -1 for the default layer colour.</summary>
        public int G { get; set; } = -1;

        /// <summary>Blue component, 0-255, or -1 for the default layer colour.</summary>
        public int B { get; set; } = -1;

        /// <summary><see langword="false"/> = image coordinates, <see langword="true"/> = page-relative.</summary>
        public bool Page { get; set; }
    }

    /// <summary><c>box(attribute, ltx, lty, rbx, rby, fill, width, pen, r, g, b, page)</c>.</summary>
    public sealed class VectorBox : VectorRectangleObject { }

    /// <summary><c>oval(attribute, ltx, lty, rbx, rby, fill, width, pen, r, g, b, page)</c>.</summary>
    public sealed class VectorOval : VectorRectangleObject { }

    /// <summary>
    /// <c>map_box(attribute, ltx, lty, rbx, rby, fill, width, pen, r, g, b, fast_preview, page)</c>
    /// — a box used as a map-composition object; <see cref="VectorObject.Attribute"/> carries the
    /// object's map-composition parameters (see "Map composition files (.ldd)" in the guide).
    /// </summary>
    public sealed class VectorMapBox : VectorRectangleObject
    {
        /// <summary>Whether a fast (lower quality) preview is used while composing the map.</summary>
        public bool FastPreview { get; set; }
    }

    /// <summary>Shared fields of <see cref="VectorPolyline"/>, <see cref="VectorPolygon"/> and their map variants.</summary>
    public abstract class VectorPolyObject : VectorObject
    {
        /// <summary>Vertices, in the units declared by the header's <c>CoordinateSpace</c>.</summary>
        public IList<(double X, double Y)> Points { get; } = new List<(double, double)>();

        /// <summary>Fill style, 0-19.</summary>
        public int Fill { get; set; }

        /// <summary>Line width in points.</summary>
        public double Width { get; set; } = 1;

        /// <summary>Pen (line) style, 0-23.</summary>
        public int Pen { get; set; }

        /// <summary>Spline (curved-line) flag.</summary>
        public bool Curved { get; set; }

        /// <summary>Red component, 0-255, or -1 for the default layer colour.</summary>
        public int R { get; set; } = -1;

        /// <summary>Green component, 0-255, or -1 for the default layer colour.</summary>
        public int G { get; set; } = -1;

        /// <summary>Blue component, 0-255, or -1 for the default layer colour.</summary>
        public int B { get; set; } = -1;

        /// <summary><see langword="false"/> = image coordinates, <see langword="true"/> = page-relative.</summary>
        public bool Page { get; set; }
    }

    /// <summary>
    /// An open polyline: <c>poly(attribute, npoints, [pts], end, width, pen, reserved, curved, fill, r, g, b, page)</c>.
    /// </summary>
    public sealed class VectorPolyline : VectorPolyObject
    {
        /// <summary>Line-end style: 0 = plain, 1 = arrow at end, 2 = arrow at start, 3 = arrows both ends.</summary>
        public int End { get; set; }

        /// <summary>Reserved; ER Mapper writes 0.</summary>
        public int Reserved { get; set; }
    }

    /// <summary>A closed polygon: <c>polygon(attribute, npoints, [pts], fill, width, pen, curved, r, g, b, page)</c>.</summary>
    public sealed class VectorPolygon : VectorPolyObject { }

    /// <summary>
    /// <c>map_polygon(attribute, npoints, [pts], fill, width, pen, curved, r, g, b, fast_preview, page)</c>
    /// — a polygon used as a map-composition object.
    /// </summary>
    public sealed class VectorMapPolygon : VectorPolyObject
    {
        /// <summary>Whether a fast (lower quality) preview is used while composing the map.</summary>
        public bool FastPreview { get; set; }
    }

    /// <summary>Shared fields of <see cref="VectorText"/> and <see cref="VectorVariableText"/>.</summary>
    public abstract class VectorTextObject : VectorObject
    {
        /// <summary>Anchor X coordinate (bottom-left of the first line of text).</summary>
        public double X { get; set; }

        /// <summary>Anchor Y coordinate.</summary>
        public double Y { get; set; }

        /// <summary>Font name.</summary>
        public string Font { get; set; } = "Helvetica";

        /// <summary>Text style; not used at this time (ER Mapper writes 0).</summary>
        public int Style { get; set; }

        /// <summary>Size in points.</summary>
        public double Size { get; set; } = 10;

        /// <summary>Number of lines in <see cref="Lines"/> (kept as a field so an exact original count round-trips even if empty lines are present).</summary>
        public int NLines { get; set; }

        /// <summary>0 = left, 1 = centre, 2 = right.</summary>
        public int Just { get; set; }

        /// <summary>Rotation of the text, degrees clockwise (0-360).</summary>
        public double Angle { get; set; }

        /// <summary>Pen (line) style, 0-23.</summary>
        public int Pen { get; set; }

        /// <summary>Red component, 0-255, or -1 for the default layer colour.</summary>
        public int R { get; set; } = -1;

        /// <summary>Green component, 0-255, or -1 for the default layer colour.</summary>
        public int G { get; set; } = -1;

        /// <summary>Blue component, 0-255, or -1 for the default layer colour.</summary>
        public int B { get; set; } = -1;

        /// <summary><see langword="false"/> = image coordinates, <see langword="true"/> = page-relative.</summary>
        public bool Page { get; set; }

        /// <summary>The lines of text.</summary>
        public IList<string> Lines { get; } = new List<string>();
    }

    /// <summary>
    /// Fixed-position text: <c>text(attribute, x, y, font, style, size, nlines, just, angle,
    /// pen, r, g, b, page, [lines])</c>.
    /// </summary>
    public sealed class VectorText : VectorTextObject { }

    /// <summary>
    /// Text fitted into a box: <c>vtext(attribute, x, y, ltx, lty, rbx, rby, font, style, size,
    /// nlines, just, angle, pen, r, g, b, page, [lines])</c>.
    /// </summary>
    public sealed class VectorVariableText : VectorTextObject
    {
        /// <summary>Left/top X of the fitting box.</summary>
        public double Ltx { get; set; }

        /// <summary>Left/top Y of the fitting box.</summary>
        public double Lty { get; set; }

        /// <summary>Right/bottom X of the fitting box.</summary>
        public double Rbx { get; set; }

        /// <summary>Right/bottom Y of the fitting box.</summary>
        public double Rby { get; set; }
    }
}
