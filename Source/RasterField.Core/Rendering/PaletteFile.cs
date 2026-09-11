using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RasterField.Rendering
{
    /// <summary>
    /// Reads and writes DigiTerra <c>.pal</c> raster palette files: an optional
    /// <c>// ...</c> comment header followed by up to 256 lines of tab- or
    /// whitespace-separated <c>R G B</c> integer triples (RGB order, 0-255).
    /// </summary>
    public static class PaletteFile
    {
        /// <summary>Loads a <c>.pal</c> file from disk. The palette name is the file name without extension.</summary>
        public static Palette Load(string path)
        {
            using var reader = new StreamReader(path, DetectEncoding(), detectEncodingFromByteOrderMarks: true);
            return Read(reader, Path.GetFileNameWithoutExtension(path));
        }

        /// <summary>Reads a <c>.pal</c> palette from a stream (not disposed).</summary>
        public static Palette Read(Stream stream, string name)
        {
            using var reader = new StreamReader(stream, DetectEncoding(), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            return Read(reader, name);
        }

        /// <summary>Reads a <c>.pal</c> palette from a text reader.</summary>
        public static Palette Read(TextReader reader, string name)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var rows = new List<(int, int, int)>(Palette.Size);
            string? line;
            while ((line = reader.ReadLine()) != null && rows.Count < Palette.Size)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;

                string[] parts = trimmed.Split(new[] { '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) &&
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
                {
                    rows.Add((r, g, b));
                }
            }

            if (rows.Count == 0)
                throw new InvalidDataException($"'{name}' does not contain any RGB rows.");

            return Palette.FromRgbRows(name, rows);
        }

        /// <summary>Writes a palette to a <c>.pal</c> file in DigiTerra format.</summary>
        public static void Save(Palette palette, string path)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            var sb = new StringBuilder();
            sb.Append("// DigiTerra Raster Palette, RGB order\n");
            foreach (var c in palette.Entries)
                sb.Append(c.R).Append('\t').Append(c.G).Append('\t').Append(c.B).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static Encoding DetectEncoding() => Encoding.UTF8;
    }
}
