using System;
using System.Globalization;

namespace RasterField.Rendering
{
    /// <summary>
    /// A 32-bit straight-alpha RGBA colour. Framework independent so palette maths stays
    /// usable from the portable (netstandard) build; the Windows build converts to
    /// <see cref="System.Drawing.Color"/> where needed.
    /// </summary>
    public readonly struct ColorRgba : IEquatable<ColorRgba>
    {
        /// <summary>Red channel (0-255).</summary>
        public byte R { get; }

        /// <summary>Green channel (0-255).</summary>
        public byte G { get; }

        /// <summary>Blue channel (0-255).</summary>
        public byte B { get; }

        /// <summary>Alpha channel (0 = transparent, 255 = opaque).</summary>
        public byte A { get; }

        /// <summary>Creates a colour from four channels; alpha defaults to opaque.</summary>
        public ColorRgba(byte r, byte g, byte b, byte a = 255)
        {
            R = r; G = g; B = b; A = a;
        }

        /// <summary>A fully transparent colour.</summary>
        public static readonly ColorRgba Transparent = new ColorRgba(0, 0, 0, 0);

        /// <summary>Opaque black.</summary>
        public static readonly ColorRgba Black = new ColorRgba(0, 0, 0);

        /// <summary>Opaque white.</summary>
        public static readonly ColorRgba White = new ColorRgba(255, 255, 255);

        /// <summary>Packs the colour into <c>0xAARRGGBB</c> as expected by GDI+ 32bppArgb bitmaps.</summary>
        public int ToArgb() => (A << 24) | (R << 16) | (G << 8) | B;

        /// <summary>Unpacks a colour from <c>0xAARRGGBB</c>.</summary>
        public static ColorRgba FromArgb(int argb) =>
            new ColorRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

        /// <summary>Linear interpolation between two colours; <paramref name="t"/> is clamped to [0, 1].</summary>
        public static ColorRgba Lerp(ColorRgba a, ColorRgba b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            double u = 1.0 - t;
            return new ColorRgba(
                (byte)(u * a.R + t * b.R + 0.5),
                (byte)(u * a.G + t * b.G + 0.5),
                (byte)(u * a.B + t * b.B + 0.5),
                (byte)(u * a.A + t * b.A + 0.5));
        }

        /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (leading <c>#</c> optional).</summary>
        public static ColorRgba ParseHex(string hex)
        {
            if (hex == null) throw new ArgumentNullException(nameof(hex));
            string s = hex.Trim().TrimStart('#');
            switch (s.Length)
            {
                case 3:
                    return new ColorRgba(
                        (byte)(Nyb(s[0]) * 17), (byte)(Nyb(s[1]) * 17), (byte)(Nyb(s[2]) * 17));
                case 6:
                    return new ColorRgba(Hex(s, 0), Hex(s, 2), Hex(s, 4));
                case 8:
                    return new ColorRgba(Hex(s, 0), Hex(s, 2), Hex(s, 4), Hex(s, 6));
                default:
                    throw new FormatException($"'{hex}' is not a valid hex colour.");
            }
        }

        private static byte Hex(string s, int i) => byte.Parse(s.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        private static int Nyb(char c) => Convert.ToInt32(c.ToString(), 16);

        /// <inheritdoc />
        public bool Equals(ColorRgba other) => R == other.R && G == other.G && B == other.B && A == other.A;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ColorRgba c && Equals(c);

        /// <inheritdoc />
        public override int GetHashCode() => ToArgb();

        /// <inheritdoc />
        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{(A == 255 ? string.Empty : A.ToString("X2"))}";
    }
}
