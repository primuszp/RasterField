using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RasterField.Vectors
{
    /// <summary>
    /// Writes ER Mapper vector objects back to the ASCII data-file syntax described in the
    /// "Vector Datasets and Header Files (.erv)" chapter. Real numbers are always written in
    /// fixed-point form — the format forbids exponent notation.
    /// </summary>
    public static class VectorDataWriter
    {
        /// <summary>Writes every object, one per line, terminated with <c>.</c>.</summary>
        public static void Write(TextWriter writer, IEnumerable<VectorObject> objects)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (objects == null) throw new ArgumentNullException(nameof(objects));
            foreach (var obj in objects)
                writer.Write(Format(obj));
        }

        /// <summary>Formats a single object as it would appear in a data file, including the trailing <c>.</c> and newline.</summary>
        public static string Format(VectorObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var sb = new StringBuilder();

            switch (obj)
            {
                case VectorMapBox mb:
                    sb.Append("map_box(").Append(Attr(mb.Attribute)).Append(',')
                      .Append(RectFields(mb)).Append(',').Append(Bit(mb.FastPreview)).Append(',').Append(Bit(mb.Page));
                    break;
                case VectorBox b:
                    sb.Append("box(").Append(Attr(b.Attribute)).Append(',').Append(RectFields(b)).Append(',').Append(Bit(b.Page));
                    break;
                case VectorOval o:
                    sb.Append("oval(").Append(Attr(o.Attribute)).Append(',').Append(RectFields(o)).Append(',').Append(Bit(o.Page));
                    break;
                case VectorMapPolygon mp:
                    sb.Append("map_polygon(").Append(Attr(mp.Attribute)).Append(',').Append(mp.Points.Count).Append(",[")
                      .Append(Pts(mp.Points)).Append("],").Append(PolyFields(mp)).Append(',').Append(Bit(mp.FastPreview)).Append(',').Append(Bit(mp.Page));
                    break;
                case VectorPolygon pg:
                    sb.Append("polygon(").Append(Attr(pg.Attribute)).Append(',').Append(pg.Points.Count).Append(",[")
                      .Append(Pts(pg.Points)).Append("],").Append(PolyFields(pg)).Append(',').Append(Bit(pg.Page));
                    break;
                case VectorPolyline pl:
                    sb.Append("poly(").Append(Attr(pl.Attribute)).Append(',').Append(pl.Points.Count).Append(",[")
                      .Append(Pts(pl.Points)).Append("],").Append(pl.End).Append(',').Append(N(pl.Width)).Append(',').Append(pl.Pen)
                      .Append(',').Append(pl.Reserved).Append(',').Append(Bit(pl.Curved)).Append(',').Append(pl.Fill)
                      .Append(',').Append(pl.R).Append(',').Append(pl.G).Append(',').Append(pl.B).Append(',').Append(Bit(pl.Page));
                    break;
                case VectorVariableText vt:
                    sb.Append("vtext(").Append(Attr(vt.Attribute)).Append(',').Append(N(vt.X)).Append(',').Append(N(vt.Y))
                      .Append(',').Append(N(vt.Ltx)).Append(',').Append(N(vt.Lty)).Append(',').Append(N(vt.Rbx)).Append(',').Append(N(vt.Rby))
                      .Append(',').Append(TextFields(vt));
                    break;
                case VectorText t:
                    sb.Append("text(").Append(Attr(t.Attribute)).Append(',').Append(N(t.X)).Append(',').Append(N(t.Y))
                      .Append(',').Append(TextFields(t));
                    break;
                case VectorPoint p:
                    sb.Append("point(").Append(Attr(p.Attribute)).Append(',').Append(N(p.X)).Append(',').Append(N(p.Y))
                      .Append(',').Append(p.Spare).Append(',').Append(p.R).Append(',').Append(p.G).Append(',').Append(p.B)
                      .Append(',').Append(Bit(p.Page));
                    break;
                default:
                    throw new NotSupportedException($"Unknown vector object type '{obj.GetType().Name}'.");
            }

            sb.Append(").\n");
            return sb.ToString();
        }

        private static string RectFields(VectorRectangleObject r) =>
            $"{N(r.Ltx)},{N(r.Lty)},{N(r.Rbx)},{N(r.Rby)},{r.Fill},{N(r.Width)},{r.Pen},{r.R},{r.G},{r.B}";

        private static string PolyFields(VectorPolyObject p) =>
            $"{p.Fill},{N(p.Width)},{p.Pen},{Bit(p.Curved)},{p.R},{p.G},{p.B}";

        private static string TextFields(VectorTextObject t) =>
            $"{t.Font},{t.Style},{N(t.Size)},{(t.NLines != 0 ? t.NLines : t.Lines.Count)},{t.Just},{N(t.Angle)},{t.Pen},{t.R},{t.G},{t.B},{Bit(t.Page)},[{Lines(t.Lines)}]";

        private static string Pts(IEnumerable<(double X, double Y)> points)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var (x, y) in points)
            {
                if (!first) sb.Append(',');
                sb.Append(N(x)).Append(',').Append(N(y));
                first = false;
            }
            return sb.ToString();
        }

        private static string Lines(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var line in lines)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(Escape(line)).Append('"');
                first = false;
            }
            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\"", "\\\"").Replace("\n", "\\n");

        private static string Attr(string? attribute) =>
            string.IsNullOrEmpty(attribute) ? string.Empty : "\"" + Escape(attribute!) + "\"";

        private static string Bit(bool b) => b ? "1" : "0";

        /// <summary>Formats a real number in fixed-point notation — the format never allows exponent form.</summary>
        private static string N(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);
    }
}
