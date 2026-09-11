using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RasterField.Vectors
{
    /// <summary>
    /// Parses an ER Mapper vector data file: an ASCII list of object specifications of the form
    /// <c>name(field, field, ...).</c>, one after another. Per the format's own rules the parser
    /// is <b>not</b> line-based — <c>point</c>/<c>box</c>/<c>oval</c>/<c>map_box</c> objects are
    /// always single-line, but <c>poly</c>/<c>polygon</c>/<c>map_polygon</c>/<c>text</c>/<c>vtext</c>
    /// may legitimately span several physical lines (ER Mapper itself wraps long coordinate
    /// arrays), and a quoted attribute string may contain literal newlines. The parser instead
    /// scans the whole file as one character stream, tracking quote state so that commas,
    /// brackets and periods inside quoted strings are never mistaken for syntax.
    /// </summary>
    public static class VectorDataReader
    {
        /// <summary>Reads every object from a text reader (consumes it to the end).</summary>
        public static List<VectorObject> ReadObjects(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return Parse(reader.ReadToEnd());
        }

        /// <summary>Parses every object from an in-memory string.</summary>
        public static List<VectorObject> Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var result = new List<VectorObject>();
            int i = 0, n = text.Length;

            while (true)
            {
                SkipWhitespace(text, ref i);
                if (i >= n) break;

                int nameStart = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                if (i == nameStart)
                    throw new FormatException($"Unexpected character '{Describe(text, i)}' at position {i} in vector data (expected an object name).");
                string name = text.Substring(nameStart, i - nameStart);

                SkipWhitespace(text, ref i);
                if (i >= n || text[i] != '(')
                    throw new FormatException($"Expected '(' after '{name}' at position {i} in vector data.");
                i++; // consume '('

                int contentStart = i;
                bool inQuotes = false;
                int depth = 1;
                while (i < n)
                {
                    char c = text[i];
                    if (c == '"' && (i == 0 || text[i - 1] != '\\')) inQuotes = !inQuotes;
                    else if (!inQuotes)
                    {
                        if (c == '(') depth++;
                        else if (c == ')') { depth--; if (depth == 0) break; }
                    }
                    i++;
                }
                if (depth != 0)
                    throw new FormatException($"Unterminated '{name}(' starting near position {nameStart} in vector data.");
                string content = text.Substring(contentStart, i - contentStart);
                i++; // consume the matching ')'

                SkipWhitespace(text, ref i);
                if (i >= n || text[i] != '.')
                    throw new FormatException($"Expected '.' to end the '{name}(...)' object near position {i} in vector data.");
                i++; // consume '.'

                var args = SplitTopLevel(content);
                result.Add(BuildObject(name, args));
            }

            return result;
        }

        private static string Describe(string text, int i) => i < text.Length ? text[i].ToString() : "<end of file>";

        private static void SkipWhitespace(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }

        /// <summary>Splits on commas at bracket depth 0, outside quotes (so array/string commas are not top-level separators).</summary>
        private static List<string> SplitTopLevel(string content)
        {
            var args = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            int depth = 0;

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '"' && (i == 0 || content[i - 1] != '\\')) inQuotes = !inQuotes;

                if (!inQuotes)
                {
                    if (c == '[') depth++;
                    else if (c == ']') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        args.Add(sb.ToString().Trim());
                        sb.Clear();
                        continue;
                    }
                }
                sb.Append(c);
            }
            args.Add(sb.ToString().Trim());
            return args;
        }

        private static string Unquote(string token)
        {
            token = token.Trim();
            if (token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"')
                return token.Substring(1, token.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n");
            return token;
        }

        private static double D(IReadOnlyList<string> a, int i, string objName) =>
            double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : throw new FormatException($"'{objName}': field {i} ('{a[i]}') is not a number.");

        private static int I(IReadOnlyList<string> a, int i, string objName) =>
            int.TryParse(a[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : throw new FormatException($"'{objName}': field {i} ('{a[i]}') is not an integer.");

        private static bool Bit(IReadOnlyList<string> a, int i, string objName) => I(a, i, objName) != 0;

        private static void Require(IReadOnlyList<string> a, int count, string objName)
        {
            if (a.Count != count)
                throw new FormatException($"'{objName}' expects {count} fields but found {a.Count}.");
        }

        private static IList<(double, double)> ParsePoints(string arrayToken, string objName)
        {
            string inner = StripBrackets(arrayToken, objName);
            var nums = SplitTopLevel(inner);
            if (nums.Count % 2 != 0)
                throw new FormatException($"'{objName}': the point array has an odd number of values.");

            var pts = new List<(double, double)>(nums.Count / 2);
            for (int i = 0; i < nums.Count; i += 2)
                pts.Add((
                    double.Parse(nums[i], NumberStyles.Float, CultureInfo.InvariantCulture),
                    double.Parse(nums[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture)));
            return pts;
        }

        private static IList<string> ParseLines(string arrayToken, string objName)
        {
            string inner = StripBrackets(arrayToken, objName);
            if (inner.Length == 0) return new List<string>();
            var tokens = SplitTopLevel(inner);
            var lines = new List<string>(tokens.Count);
            foreach (var t in tokens) lines.Add(Unquote(t));
            return lines;
        }

        private static string StripBrackets(string token, string objName)
        {
            token = token.Trim();
            if (token.Length < 2 || token[0] != '[' || token[token.Length - 1] != ']')
                throw new FormatException($"'{objName}': expected a '[...]' array, found '{token}'.");
            return token.Substring(1, token.Length - 2);
        }

        private static VectorObject BuildObject(string name, List<string> a)
        {
            switch (name.ToLowerInvariant())
            {
                case "point":
                {
                    Require(a, 8, name);
                    return new VectorPoint
                    {
                        Attribute = Unquote(a[0]), X = D(a, 1, name), Y = D(a, 2, name), Spare = I(a, 3, name),
                        R = I(a, 4, name), G = I(a, 5, name), B = I(a, 6, name), Page = Bit(a, 7, name),
                    };
                }
                case "box":
                {
                    Require(a, 12, name);
                    return FillRect(new VectorBox(), a, name);
                }
                case "oval":
                {
                    Require(a, 12, name);
                    return FillRect(new VectorOval(), a, name);
                }
                case "map_box":
                {
                    Require(a, 13, name);
                    var box = new VectorMapBox
                    {
                        Attribute = Unquote(a[0]), Ltx = D(a, 1, name), Lty = D(a, 2, name),
                        Rbx = D(a, 3, name), Rby = D(a, 4, name), Fill = I(a, 5, name), Width = D(a, 6, name),
                        Pen = I(a, 7, name), R = I(a, 8, name), G = I(a, 9, name), B = I(a, 10, name),
                        FastPreview = Bit(a, 11, name), Page = Bit(a, 12, name),
                    };
                    return box;
                }
                case "poly":
                {
                    Require(a, 13, name);
                    var pts = ParsePoints(a[2], name);
                    var pl = new VectorPolyline
                    {
                        Attribute = Unquote(a[0]),
                        End = I(a, 3, name), Width = D(a, 4, name), Pen = I(a, 5, name), Reserved = I(a, 6, name),
                        Curved = Bit(a, 7, name), Fill = I(a, 8, name),
                        R = I(a, 9, name), G = I(a, 10, name), B = I(a, 11, name), Page = Bit(a, 12, name),
                    };
                    foreach (var p in pts) pl.Points.Add(p);
                    return pl;
                }
                case "polygon":
                {
                    Require(a, 11, name);
                    var pts = ParsePoints(a[2], name);
                    var pg = new VectorPolygon
                    {
                        Attribute = Unquote(a[0]),
                        Fill = I(a, 3, name), Width = D(a, 4, name), Pen = I(a, 5, name), Curved = Bit(a, 6, name),
                        R = I(a, 7, name), G = I(a, 8, name), B = I(a, 9, name), Page = Bit(a, 10, name),
                    };
                    foreach (var p in pts) pg.Points.Add(p);
                    return pg;
                }
                case "map_polygon":
                {
                    Require(a, 12, name);
                    var pts = ParsePoints(a[2], name);
                    var mp = new VectorMapPolygon
                    {
                        Attribute = Unquote(a[0]),
                        Fill = I(a, 3, name), Width = D(a, 4, name), Pen = I(a, 5, name), Curved = Bit(a, 6, name),
                        R = I(a, 7, name), G = I(a, 8, name), B = I(a, 9, name),
                        FastPreview = Bit(a, 10, name), Page = Bit(a, 11, name),
                    };
                    foreach (var p in pts) mp.Points.Add(p);
                    return mp;
                }
                case "text":
                {
                    Require(a, 15, name);
                    var lines = ParseLines(a[14], name);
                    var t = new VectorText
                    {
                        Attribute = Unquote(a[0]), X = D(a, 1, name), Y = D(a, 2, name), Font = Unquote(a[3]),
                        Style = I(a, 4, name), Size = D(a, 5, name), NLines = I(a, 6, name), Just = I(a, 7, name),
                        Angle = D(a, 8, name), Pen = I(a, 9, name),
                        R = I(a, 10, name), G = I(a, 11, name), B = I(a, 12, name), Page = Bit(a, 13, name),
                    };
                    foreach (var l in lines) t.Lines.Add(l);
                    return t;
                }
                case "vtext":
                {
                    Require(a, 19, name);
                    var lines = ParseLines(a[18], name);
                    var vt = new VectorVariableText
                    {
                        Attribute = Unquote(a[0]), X = D(a, 1, name), Y = D(a, 2, name),
                        Ltx = D(a, 3, name), Lty = D(a, 4, name), Rbx = D(a, 5, name), Rby = D(a, 6, name),
                        Font = Unquote(a[7]), Style = I(a, 8, name), Size = D(a, 9, name), NLines = I(a, 10, name),
                        Just = I(a, 11, name), Angle = D(a, 12, name), Pen = I(a, 13, name),
                        R = I(a, 14, name), G = I(a, 15, name), B = I(a, 16, name), Page = Bit(a, 17, name),
                    };
                    foreach (var l in lines) vt.Lines.Add(l);
                    return vt;
                }
                default:
                    throw new FormatException($"Unrecognised vector object type '{name}'.");
            }
        }

        private static T FillRect<T>(T box, List<string> a, string name) where T : VectorRectangleObject
        {
            box.Attribute = Unquote(a[0]);
            box.Ltx = D(a, 1, name); box.Lty = D(a, 2, name); box.Rbx = D(a, 3, name); box.Rby = D(a, 4, name);
            box.Fill = I(a, 5, name); box.Width = D(a, 6, name); box.Pen = I(a, 7, name);
            box.R = I(a, 8, name); box.G = I(a, 9, name); box.B = I(a, 10, name); box.Page = Bit(a, 11, name);
            return box;
        }
    }
}
