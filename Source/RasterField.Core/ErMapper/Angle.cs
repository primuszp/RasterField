using System;
using System.Globalization;

namespace RasterField.ErMapper
{
    /// <summary>
    /// An angle expressed in the ER Mapper <c>Degrees:Minutes:Seconds</c> notation,
    /// where degrees and minutes are integers and seconds may be fractional
    /// (e.g. <c>23:45:34.6</c>). Used by <c>Rotation</c>, <c>Latitude</c> and
    /// <c>Longitude</c> entries.
    /// </summary>
    public readonly struct Angle : IEquatable<Angle>
    {
        /// <summary>The angle in decimal degrees.</summary>
        public double Degrees { get; }

        /// <summary>Creates an angle from decimal degrees.</summary>
        public Angle(double degrees) => Degrees = degrees;

        /// <summary>The angle in radians.</summary>
        public double Radians => Degrees * Math.PI / 180.0;

        /// <summary>A zero angle.</summary>
        public static readonly Angle Zero = new Angle(0.0);

        /// <summary>Parses a <c>D:M:S</c> (or plain decimal) angle string using invariant culture.</summary>
        public static Angle Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            string s = text.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);

            string[] parts = s.Split(':');
            if (parts.Length == 1)
                return new Angle(double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture));

            double deg = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture);
            double min = parts.Length > 1 ? double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture) : 0.0;
            double sec = parts.Length > 2 ? double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture) : 0.0;

            double sign = deg < 0 || (deg == 0 && s.TrimStart().StartsWith("-", StringComparison.Ordinal)) ? -1.0 : 1.0;
            double magnitude = Math.Abs(deg) + min / 60.0 + sec / 3600.0;
            return new Angle(sign * magnitude);
        }

        /// <summary>Tries to parse a <c>D:M:S</c> (or plain decimal) angle string.</summary>
        public static bool TryParse(string? text, out Angle angle)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    angle = Parse(text!);
                    return true;
                }
            }
            catch (FormatException) { }
            catch (OverflowException) { }
            angle = Zero;
            return false;
        }

        /// <summary>Formats the angle back to <c>D:M:S.sss</c>.</summary>
        public string ToDmsString()
        {
            double abs = Math.Abs(Degrees);
            int d = (int)Math.Floor(abs);
            double remMinutes = (abs - d) * 60.0;
            int m = (int)Math.Floor(remMinutes);
            double sec = (remMinutes - m) * 60.0;
            string sign = Degrees < 0 ? "-" : string.Empty;
            // Seconds always carry a decimal point, matching ER Mapper's own output (e.g. 0:0:0.0).
            string secText = sec.ToString("0.0###########", CultureInfo.InvariantCulture);
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}:{2}:{3}", sign, d, m, secText);
        }

        /// <inheritdoc />
        public bool Equals(Angle other) => Degrees.Equals(other.Degrees);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Angle a && Equals(a);

        /// <inheritdoc />
        public override int GetHashCode() => Degrees.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => ToDmsString();
    }
}
