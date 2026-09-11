using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RasterField.Rendering
{
    /// <summary>
    /// A named, ordered collection of palettes for binding to a UI selector. Populate it from
    /// the built-ins and/or from a folder of DigiTerra <c>.pal</c> files. Image colour-strip
    /// palettes (PNG/BMP) are decoded by the host and added with <see cref="Add"/> +
    /// <see cref="Palette.FromImage"/>, keeping this assembly free of any image dependency.
    /// </summary>
    public sealed class PaletteLibrary
    {
        private readonly Dictionary<string, Palette> _byName = new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Palette> _order = new List<Palette>();

        /// <summary>Palettes in insertion order.</summary>
        public IReadOnlyList<Palette> Palettes => _order;

        /// <summary>Palette names in insertion order.</summary>
        public IEnumerable<string> Names => _order.Select(p => p.Name);

        /// <summary>Number of palettes held.</summary>
        public int Count => _order.Count;

        /// <summary>Adds or replaces a palette by name (case-insensitive), keeping its position when replaced.</summary>
        public void Add(Palette palette)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            if (_byName.TryGetValue(palette.Name, out var existing))
            {
                int idx = _order.IndexOf(existing);
                _order[idx] = palette;
            }
            else
            {
                _order.Add(palette);
            }
            _byName[palette.Name] = palette;
        }

        /// <summary>Gets a palette by name, or <see langword="null"/>.</summary>
        public Palette? Get(string? name) => name != null && _byName.TryGetValue(name, out var p) ? p : null;

        /// <summary>Gets a palette by name, falling back to the first entry (or the built-in default).</summary>
        public Palette GetOrDefault(string? name) =>
            (name != null && _byName.TryGetValue(name, out var p)) ? p :
            _order.Count > 0 ? _order[0] : BuiltInPalettes.Default;

        /// <summary>Adds every <see cref="BuiltInPalettes"/> entry.</summary>
        public PaletteLibrary AddBuiltIns()
        {
            foreach (var p in BuiltInPalettes.All.Values) Add(p);
            return this;
        }

        /// <summary>
        /// Adds every <c>.pal</c> file found directly in <paramref name="directory"/>.
        /// Missing directories are ignored; unparsable files are reported via <paramref name="onError"/>.
        /// </summary>
        public PaletteLibrary AddPalFiles(string directory, Action<string, Exception>? onError = null)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return this;

            foreach (var file in Directory.EnumerateFiles(directory, "*.pal").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try { Add(PaletteFile.Load(file)); }
                catch (Exception ex) { onError?.Invoke(file, ex); }
            }
            return this;
        }

        /// <summary>Creates a library with the built-ins plus any <c>.pal</c> files in <paramref name="paletteDirectory"/>.</summary>
        public static PaletteLibrary CreateDefault(string? paletteDirectory = null)
        {
            var lib = new PaletteLibrary().AddBuiltIns();
            if (paletteDirectory != null) lib.AddPalFiles(paletteDirectory);
            return lib;
        }
    }
}
