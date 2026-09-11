using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RasterField.ErMapper.Text
{
    /// <summary>
    /// A single value taken from an <c>key = value</c> entry in an ER Mapper ASCII file.
    /// The raw text is preserved; typed accessors interpret it on demand following the
    /// conventions described in the <i>ERDAS ER Mapper Customization Guide</i>
    /// ("File Syntax" chapter): quoted strings, integers, real numbers (optionally in
    /// exponent form), keywords (unquoted, case-insensitive), angles (<c>D:M:S</c>),
    /// GMT dates and brace-delimited arrays.
    /// </summary>
    public readonly struct ErsValue
    {
        /// <summary>The value exactly as it appeared in the file, with surrounding whitespace trimmed.</summary>
        public string Raw { get; }

        /// <summary>For an array value (<c>{ ... }</c>) the individual whitespace separated items; otherwise empty.</summary>
        public IReadOnlyList<string> ArrayItems { get; }

        /// <summary><see langword="true"/> when the entry used the <c>{ ... }</c> array syntax.</summary>
        public bool IsArray { get; }

        internal ErsValue(string raw)
        {
            Raw = raw ?? string.Empty;
            ArrayItems = Array.Empty<string>();
            IsArray = false;
        }

        internal ErsValue(IReadOnlyList<string> items)
        {
            ArrayItems = items ?? Array.Empty<string>();
            IsArray = true;
            Raw = "{ " + string.Join(" ", ArrayItems) + " }";
        }

        /// <summary><see langword="true"/> when no value was present.</summary>
        public bool IsEmpty => !IsArray && string.IsNullOrEmpty(Raw);

        /// <summary>Returns the value as text, stripping a single pair of surrounding double quotes and unescaping <c>\"</c>.</summary>
        public string AsString()
        {
            string s = Raw;
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2).Replace("\\\"", "\"");
            return s;
        }

        /// <summary>Returns the value as a keyword: unquoted, trimmed, case preserved.</summary>
        public string AsKeyword() => AsString().Trim();

        /// <summary>Parses the value as a 32-bit integer using invariant culture.</summary>
        public int AsInt32() =>
            int.Parse(CleanNumeric(), NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        /// <summary>Parses the value as a 64-bit integer using invariant culture.</summary>
        public long AsInt64() =>
            long.Parse(CleanNumeric(), NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        /// <summary>Parses the value as a double, accepting plain and exponent (<c>1.313E+02</c>) forms.</summary>
        public double AsDouble() =>
            double.Parse(CleanNumeric(), NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>Parses the value as a <see cref="float"/>.</summary>
        public float AsSingle() =>
            float.Parse(CleanNumeric(), NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>Interprets <c>Yes/No</c>, <c>True/False</c> or <c>1/0</c> (case-insensitive) as a boolean.</summary>
        public bool AsBoolean()
        {
            string s = AsKeyword();
            if (s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s == "1") return true;
            if (s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s == "0") return false;
            throw new FormatException($"'{Raw}' is not a recognised boolean value.");
        }

        /// <summary>Parses an <c>D:M:S</c> angle entry (see <see cref="Angle"/>).</summary>
        public Angle AsAngle() => Angle.Parse(Raw);

        /// <summary>Maps the value (as a keyword) onto an enum member, ignoring case. Returns <paramref name="fallback"/> when it does not match.</summary>
        public TEnum AsEnum<TEnum>(TEnum fallback) where TEnum : struct
            => Enum.TryParse(AsKeyword(), ignoreCase: true, out TEnum parsed) ? parsed : fallback;

        /// <summary>Best-effort parse of a GMT date entry such as <c>Sun Dec 7 03:20:25 GMT 1989</c>.</summary>
        public DateTime? AsDateTimeUtc()
        {
            string s = Raw.Replace(" GMT ", " ");
            string[] formats =
            {
                "ddd MMM d HH:mm:ss yyyy",
                "ddd MMM dd HH:mm:ss yyyy",
                "ddd MMM  d HH:mm:ss yyyy",
            };
            return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt)
                ? dt
                : (DateTime?)null;
        }

        /// <summary>Array items parsed as doubles.</summary>
        public double[] AsDoubleArray() =>
            ArrayItems.Select(i => double.Parse(i, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

        private string CleanNumeric()
        {
            string s = AsString().Trim();
            // Some writers append a stray trailing unit or token after whitespace.
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        /// <inheritdoc />
        public override string ToString() => Raw;
    }
}
