using System;
using System.Collections.Generic;
using System.Linq;

namespace RasterField.Rendering
{
    /// <summary>
    /// A 256-entry colour lookup table used to colourise a normalised raster value
    /// (<c>0.0</c> = low end, <c>1.0</c> = high end). Palettes can be built from evenly
    /// spaced colours, from positioned gradient stops, or loaded from DigiTerra
    /// <c>.pal</c> files / 16&#215;256 image strips.
    /// </summary>
    public sealed class Palette
    {
        /// <summary>Number of entries in every palette.</summary>
        public const int Size = 256;

        private readonly ColorRgba[] _entries;

        /// <summary>Wraps exactly 256 colour entries (index 0 = low value, 255 = high value).</summary>
        public Palette(string name, ColorRgba[] entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (entries.Length != Size) throw new ArgumentException($"A palette needs exactly {Size} entries.", nameof(entries));
            Name = name ?? "palette";
            _entries = entries;
        }

        /// <summary>Human-readable palette name (often the source file name).</summary>
        public string Name { get; }

        /// <summary>The raw 256 entries.</summary>
        public IReadOnlyList<ColorRgba> Entries => _entries;

        /// <summary>The entry at <paramref name="index"/> (0-255, clamped).</summary>
        public ColorRgba this[int index] => _entries[index < 0 ? 0 : index > 255 ? 255 : index];

        /// <summary>
        /// Colour for a normalised position <paramref name="t"/> in [0, 1], linearly
        /// interpolating between the two nearest entries. Values outside the range are clamped.
        /// </summary>
        public ColorRgba Sample(double t)
        {
            if (double.IsNaN(t)) return ColorRgba.Transparent;
            if (t <= 0) return _entries[0];
            if (t >= 1) return _entries[255];

            double scaled = t * 255.0;
            int i = (int)scaled;
            double frac = scaled - i;
            return frac <= 0 ? _entries[i] : ColorRgba.Lerp(_entries[i], _entries[i + 1], frac);
        }

        /// <summary>A copy of this palette with the entry order reversed.</summary>
        public Palette Reversed()
        {
            var e = new ColorRgba[Size];
            for (int i = 0; i < Size; i++) e[i] = _entries[Size - 1 - i];
            return new Palette(Name + " (reversed)", e);
        }

        /// <summary>Packs the palette into an <c>int[256]</c> of <c>0xAARRGGBB</c> values for fast blitting.</summary>
        public int[] ToArgbLut()
        {
            var lut = new int[Size];
            for (int i = 0; i < Size; i++) lut[i] = _entries[i].ToArgb();
            return lut;
        }

        /// <summary>Builds a palette by spreading the given colours evenly across the 256 entries.</summary>
        public static Palette FromColors(string name, IReadOnlyList<ColorRgba> colors)
        {
            if (colors == null || colors.Count == 0) throw new ArgumentException("At least one colour is required.", nameof(colors));
            if (colors.Count == 1) return Uniform(name, colors[0]);

            var stops = new (double pos, ColorRgba color)[colors.Count];
            for (int i = 0; i < colors.Count; i++)
                stops[i] = (i / (double)(colors.Count - 1), colors[i]);
            return FromStops(name, stops);
        }

        /// <summary>Builds a palette by linearly interpolating between positioned gradient stops (positions in [0, 1]).</summary>
        public static Palette FromStops(string name, params (double Position, ColorRgba Color)[] stops)
        {
            if (stops == null || stops.Length == 0) throw new ArgumentException("At least one stop is required.", nameof(stops));

            var ordered = stops.OrderBy(s => s.Position).ToArray();
            var entries = new ColorRgba[Size];

            for (int i = 0; i < Size; i++)
            {
                double t = i / 255.0;

                if (t <= ordered[0].Position) { entries[i] = ordered[0].Color; continue; }
                if (t >= ordered[ordered.Length - 1].Position) { entries[i] = ordered[ordered.Length - 1].Color; continue; }

                for (int k = 1; k < ordered.Length; k++)
                {
                    if (t <= ordered[k].Position)
                    {
                        var (p0, c0) = ordered[k - 1];
                        var (p1, c1) = ordered[k];
                        double local = p1 > p0 ? (t - p0) / (p1 - p0) : 0.0;
                        entries[i] = ColorRgba.Lerp(c0, c1, local);
                        break;
                    }
                }
            }

            return new Palette(name, entries);
        }

        /// <summary>Builds a palette from an ordered list of RGB triples (0-255), one per entry, padding/truncating to 256.</summary>
        public static Palette FromRgbRows(string name, IReadOnlyList<(int R, int G, int B)> rows)
        {
            if (rows == null || rows.Count == 0) throw new ArgumentException("At least one row is required.", nameof(rows));

            var entries = new ColorRgba[Size];
            for (int i = 0; i < Size; i++)
            {
                var (r, g, b) = rows[Math.Min(i, rows.Count - 1)];
                entries[i] = new ColorRgba(Clamp(r), Clamp(g), Clamp(b));
            }
            return new Palette(name, entries);
        }

        /// <summary>
        /// Builds a 256-entry palette from a decoded colour-strip image in <c>BGRA8888</c>
        /// byte order. The longer axis is resampled to 256 entries (index 0 = top / left end),
        /// sampling the centre of the strip. Use this to load 16&#215;256 / 256&#215;16 PNG
        /// palette previews without pulling an image library into this assembly.
        /// </summary>
        public static Palette FromImage(string name, byte[] bgra, int width, int height, int stride)
        {
            if (bgra == null) throw new ArgumentNullException(nameof(bgra));
            if (width <= 0 || height <= 0) throw new ArgumentException("Invalid image size.");

            bool vertical = height >= width;
            int longLength = vertical ? height : width;
            int crossMid = (vertical ? width : height) / 2;

            var entries = new ColorRgba[Size];
            for (int i = 0; i < Size; i++)
            {
                int pos = longLength == 1 ? 0 : (int)Math.Round(i / 255.0 * (longLength - 1));
                int px = vertical ? crossMid : pos;
                int py = vertical ? pos : crossMid;
                int o = py * stride + px * 4;
                entries[i] = new ColorRgba(bgra[o + 2], bgra[o + 1], bgra[o], bgra[o + 3]);
            }
            return new Palette(name, entries);
        }

        /// <summary>
        /// Approximates this palette as a small set of positioned gradient stops (evenly spaced,
        /// always including both ends), suitable as a starting point for an interactive editor.
        /// Baking the result with <see cref="FromStops"/> reproduces this palette closely but not
        /// necessarily exactly, since a 256-entry table does not retain its original stop positions.
        /// </summary>
        public IReadOnlyList<(double Position, ColorRgba Color)> ExtractStops(int count = 8)
        {
            count = Math.Max(2, count);
            var stops = new (double, ColorRgba)[count];
            for (int i = 0; i < count; i++)
            {
                double t = i / (double)(count - 1);
                stops[i] = (t, Sample(t));
            }
            return stops;
        }

        /// <summary>A palette where every entry is the same colour.</summary>
        public static Palette Uniform(string name, ColorRgba color)
        {
            var e = new ColorRgba[Size];
            for (int i = 0; i < Size; i++) e[i] = color;
            return new Palette(name, e);
        }

        private static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }
}
