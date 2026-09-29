using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RasterField.Vectors
{
    /// <summary>
    /// Converts between GeoJSON (RFC 7946) and the ER Mapper vector object model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading: <c>Point</c>/<c>MultiPoint</c> → <see cref="VectorPoint"/>, <c>LineString</c>/<c>MultiLineString</c>
    /// → <see cref="VectorPolyline"/>, <c>Polygon</c>/<c>MultiPolygon</c> → one <see cref="VectorPolygon"/> per
    /// ring (the ER Mapper format has no holes, so inner rings become separate polygons whose attribute
    /// is suffixed with <c>" (hole)"</c>), <c>GeometryCollection</c> recursively. A feature's attribute is
    /// taken from its <c>attribute</c>, <c>name</c>, <c>label</c>, <c>level</c> or <c>id</c> property (the
    /// first present), otherwise from all properties as <c>key=value; …</c>.
    /// </para>
    /// <para>
    /// Coordinates are copied verbatim. RFC 7946 prescribes WGS 84 longitude/latitude, but GeoJSON in
    /// a projected system (EOV, UTM, …) is common in practice; no reprojection is done either way.
    /// </para>
    /// </remarks>
    public static class GeoJsonFormat
    {
        private static readonly string[] AttributeKeys = { "attribute", "name", "label", "level", "id" };

        /// <summary>Parses GeoJSON text (a FeatureCollection, a Feature or a bare geometry).</summary>
        public static IReadOnlyList<VectorObject> Read(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var result = new List<VectorObject>();
            ReadNode(doc.RootElement, null, result);
            return result;
        }

        /// <summary>Reads a GeoJSON file.</summary>
        public static IReadOnlyList<VectorObject> ReadFile(string path) => Read(File.ReadAllText(path));

        private static void ReadNode(JsonElement node, string? attribute, List<VectorObject> output)
        {
            if (node.ValueKind != JsonValueKind.Object) throw new FormatException("A GeoJSON object was expected.");
            string type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";

            switch (type)
            {
                case "FeatureCollection":
                    if (node.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
                        foreach (var f in features.EnumerateArray()) ReadNode(f, null, output);
                    break;

                case "Feature":
                    string? attr = FeatureAttribute(node);
                    if (node.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object)
                        ReadNode(geometry, attr, output);
                    break;

                case "GeometryCollection":
                    if (node.TryGetProperty("geometries", out var geoms) && geoms.ValueKind == JsonValueKind.Array)
                        foreach (var g in geoms.EnumerateArray()) ReadNode(g, attribute, output);
                    break;

                case "Point":
                {
                    var (x, y) = Position(Coordinates(node));
                    output.Add(new VectorPoint { X = x, Y = y, Attribute = attribute });
                    break;
                }
                case "MultiPoint":
                    foreach (var p in Coordinates(node).EnumerateArray())
                    {
                        var (x, y) = Position(p);
                        output.Add(new VectorPoint { X = x, Y = y, Attribute = attribute });
                    }
                    break;

                case "LineString":
                    output.Add(Line(Coordinates(node), attribute));
                    break;
                case "MultiLineString":
                    foreach (var line in Coordinates(node).EnumerateArray()) output.Add(Line(line, attribute));
                    break;

                case "Polygon":
                    AddPolygon(Coordinates(node), attribute, output);
                    break;
                case "MultiPolygon":
                    foreach (var poly in Coordinates(node).EnumerateArray()) AddPolygon(poly, attribute, output);
                    break;

                default:
                    throw new FormatException($"Unsupported GeoJSON type \"{type}\".");
            }
        }

        private static JsonElement Coordinates(JsonElement geometry) =>
            geometry.TryGetProperty("coordinates", out var c) && c.ValueKind == JsonValueKind.Array
                ? c
                : throw new FormatException("A geometry without a \"coordinates\" array.");

        private static (double X, double Y) Position(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) throw new FormatException("A position needs at least two numbers.");
            return (p[0].GetDouble(), p[1].GetDouble());
        }

        private static VectorPolyline Line(JsonElement coords, string? attribute)
        {
            var line = new VectorPolyline { Attribute = attribute };
            foreach (var p in coords.EnumerateArray()) line.Points.Add(Position(p));
            return line;
        }

        private static void AddPolygon(JsonElement rings, string? attribute, List<VectorObject> output)
        {
            int index = 0;
            foreach (var ring in rings.EnumerateArray())
            {
                var poly = new VectorPolygon { Attribute = index == 0 ? attribute : (attribute ?? "") + " (hole)" };
                foreach (var p in ring.EnumerateArray()) poly.Points.Add(Position(p));
                // GeoJSON rings repeat the first position at the end; ER Mapper polygons are implicitly closed.
                int n = poly.Points.Count;
                if (n > 1 && poly.Points[0] == poly.Points[n - 1]) poly.Points.RemoveAt(n - 1);
                output.Add(poly);
                index++;
            }
        }

        private static string? FeatureAttribute(JsonElement feature)
        {
            if (feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in AttributeKeys)
                    foreach (var p in props.EnumerateObject())
                        if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
                            return ValueText(p.Value);

                var parts = props.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null)
                    .Select(p => p.Name + "=" + ValueText(p.Value)).ToList();
                if (parts.Count > 0) return string.Join("; ", parts);
            }
            if (feature.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null) return ValueText(id);
            return null;
        }

        private static string ValueText(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();

        // ---- writing -------------------------------------------------------------------

        /// <summary>
        /// Writes <paramref name="objects"/> as a GeoJSON FeatureCollection. Each feature carries the
        /// object's attribute as <c>properties.attribute</c> (and, for text, <c>properties.text</c>).
        /// Boxes become rectangles and ovals 36-gon polygons; page-relative objects are skipped.
        /// </summary>
        public static string Write(IEnumerable<VectorObject> objects)
        {
            if (objects == null) throw new ArgumentNullException(nameof(objects));
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("type", "FeatureCollection");
                w.WriteStartArray("features");
                foreach (var obj in objects) WriteFeature(w, obj);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>Writes a GeoJSON file.</summary>
        public static void WriteFile(string path, IEnumerable<VectorObject> objects) =>
            File.WriteAllText(path, Write(objects), new UTF8Encoding(false));

        private static void WriteFeature(Utf8JsonWriter w, VectorObject obj)
        {
            switch (obj)
            {
                case VectorPoint p when !p.Page:
                    Begin(w, obj, "Point");
                    WritePosition(w, p.X, p.Y);
                    End(w, obj);
                    break;
                case VectorPolyline line when !line.Page && line.Points.Count >= 2:
                    Begin(w, obj, "LineString");
                    w.WriteStartArray();
                    foreach (var (x, y) in line.Points) WritePosition(w, x, y);
                    w.WriteEndArray();
                    End(w, obj);
                    break;
                case VectorPolyObject poly when !poly.Page && poly.Points.Count >= 3:
                    WriteRing(w, obj, poly.Points.ToList());
                    break;
                case VectorOval oval when !oval.Page:
                {
                    double cx = (oval.Ltx + oval.Rbx) / 2, cy = (oval.Lty + oval.Rby) / 2;
                    double rx = Math.Abs(oval.Rbx - oval.Ltx) / 2, ry = Math.Abs(oval.Rby - oval.Lty) / 2;
                    var ring = Enumerable.Range(0, 36).Select(i => (cx + rx * Math.Cos(i * Math.PI / 18), cy + ry * Math.Sin(i * Math.PI / 18))).ToList();
                    WriteRing(w, obj, ring);
                    break;
                }
                case VectorRectangleObject box when !box.Page:
                    WriteRing(w, obj, new[] { (box.Ltx, box.Lty), (box.Rbx, box.Lty), (box.Rbx, box.Rby), (box.Ltx, box.Rby) });
                    break;
                case VectorTextObject text when !text.Page:
                    Begin(w, obj, "Point");
                    WritePosition(w, text.X, text.Y);
                    End(w, obj, string.Join("\n", text.Lines));
                    break;
            }
        }

        private static void WriteRing(Utf8JsonWriter w, VectorObject obj, IReadOnlyList<(double X, double Y)> points)
        {
            Begin(w, obj, "Polygon");
            w.WriteStartArray();
            w.WriteStartArray();
            foreach (var (x, y) in points) WritePosition(w, x, y);
            if (points[0] != points[points.Count - 1]) WritePosition(w, points[0].X, points[0].Y);
            w.WriteEndArray();
            w.WriteEndArray();
            End(w, obj);
        }

        private static void Begin(Utf8JsonWriter w, VectorObject obj, string geometryType)
        {
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteStartObject("geometry");
            w.WriteString("type", geometryType);
            w.WritePropertyName("coordinates");
        }

        private static void End(Utf8JsonWriter w, VectorObject obj, string? text = null)
        {
            w.WriteEndObject(); // geometry
            w.WriteStartObject("properties");
            if (obj.Attribute != null) w.WriteString("attribute", obj.Attribute);
            if (text != null) w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndObject(); // feature
        }

        private static void WritePosition(Utf8JsonWriter w, double x, double y)
        {
            w.WriteStartArray();
            w.WriteNumberValue(x);
            w.WriteNumberValue(y);
            w.WriteEndArray();
        }
    }

    /// <summary>
    /// Plain-text point lists: one point per row with X, Y and optional value/label columns.
    /// </summary>
    /// <remarks>
    /// The separator (<c>,</c> <c>;</c> tab) is detected from the first line; with <c>;</c> or tab a
    /// decimal comma is accepted too. A header row is recognised when its X/Y columns aren't numbers;
    /// the X/Y columns are then found by name (<c>x</c>, <c>e</c>, <c>easting</c>, <c>eov_y</c>, <c>lon</c>… /
    /// <c>y</c>, <c>n</c>, <c>northing</c>, <c>eov_x</c>, <c>lat</c>…), and every other column is joined into
    /// the point's attribute as <c>name=value; …</c>. Without a header the first two columns are X, Y and the
    /// rest the attribute.
    /// </remarks>
    public static class CsvPointFormat
    {
        private static readonly string[] XNames = { "x", "e", "east", "easting", "eov_y", "lon", "long", "longitude" };
        private static readonly string[] YNames = { "y", "n", "north", "northing", "eov_x", "lat", "latitude" };

        /// <summary>Parses CSV text into points.</summary>
        public static IReadOnlyList<VectorPoint> Read(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#", StringComparison.Ordinal)).ToList();
            var result = new List<VectorPoint>();
            if (lines.Count == 0) return result;

            char sep = lines[0].IndexOf('\t') >= 0 ? '\t' : lines[0].IndexOf(';') >= 0 ? ';' : ',';
            bool decimalComma = sep != ',';

            string[] first = Split(lines[0], sep);
            int xCol = 0, yCol = 1, start = 0;
            string[]? header = null;
            if (first.Length < 2 || !TryNumber(first[0], decimalComma, out _) || !TryNumber(first[1], decimalComma, out _))
            {
                header = first.Select(h => h.Trim().Trim('"')).ToArray();
                xCol = IndexOf(header, XNames);
                yCol = IndexOf(header, YNames);
                if (xCol < 0 || yCol < 0) { xCol = 0; yCol = 1; }
                start = 1;
            }

            for (int i = start; i < lines.Count; i++)
            {
                string[] cells = Split(lines[i], sep);
                if (cells.Length <= Math.Max(xCol, yCol)) throw new FormatException($"Line {i + 1}: expected at least {Math.Max(xCol, yCol) + 1} columns.");
                if (!TryNumber(cells[xCol], decimalComma, out double x) || !TryNumber(cells[yCol], decimalComma, out double y))
                    throw new FormatException($"Line {i + 1}: \"{cells[xCol]}\" / \"{cells[yCol]}\" are not numbers.");

                var rest = new List<string>();
                for (int c = 0; c < cells.Length; c++)
                {
                    if (c == xCol || c == yCol) continue;
                    string v = cells[c].Trim().Trim('"');
                    if (v.Length == 0) continue;
                    rest.Add(header != null && c < header.Length && header.Length > 3 ? header[c] + "=" + v : v);
                }
                result.Add(new VectorPoint { X = x, Y = y, Attribute = rest.Count == 0 ? null : string.Join("; ", rest) });
            }
            return result;
        }

        /// <summary>Writes points as <c>x,y,attribute</c> with a header row (invariant culture).</summary>
        public static string Write(IEnumerable<VectorPoint> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            var sb = new StringBuilder("x,y,attribute\n");
            foreach (var p in points)
            {
                string attr = p.Attribute ?? "";
                if (attr.IndexOfAny(new[] { ',', '"', '\n' }) >= 0) attr = "\"" + attr.Replace("\"", "\"\"") + "\"";
                sb.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(attr).Append('\n');
            }
            return sb.ToString();
        }

        private static int IndexOf(string[] header, string[] names)
        {
            for (int i = 0; i < header.Length; i++)
                if (names.Contains(header[i].ToLowerInvariant())) return i;
            return -1;
        }

        private static bool TryNumber(string s, bool decimalComma, out double value)
        {
            s = s.Trim().Trim('"');
            if (decimalComma) s = s.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Splits one line, honouring double-quoted fields.</summary>
        private static string[] Split(string line, char sep)
        {
            var cells = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (ch == sep && !quoted) { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            cells.Add(cur.ToString());
            return cells.ToArray();
        }
    }
}
