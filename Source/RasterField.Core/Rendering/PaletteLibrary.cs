using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
        private readonly Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// Registers a former or alternative name for a palette, so settings and projects saved
        /// before a palette was renamed still find it. Aliases never shadow a real palette name.
        /// </summary>
        public void AddAlias(string alias, string name)
        {
            if (alias == null) throw new ArgumentNullException(nameof(alias));
            if (name == null) throw new ArgumentNullException(nameof(name));
            _aliases[Normalize(alias)] = name;
        }

        /// <summary>
        /// The current name of the palette called <paramref name="name"/> — directly, or through an
        /// alias — or <see langword="null"/> when there is none. Case-insensitive and tolerant of
        /// Unicode normalisation differences (macOS file names are often decomposed, NFD).
        /// </summary>
        public string? Resolve(string? name) => Find(name)?.Name;

        /// <summary>Gets a palette by name or alias, or <see langword="null"/>.</summary>
        public Palette? Get(string? name) => Find(name);

        /// <summary>Gets a palette by name or alias, falling back to the first entry (or the built-in default).</summary>
        public Palette GetOrDefault(string? name) =>
            Find(name) ?? (_order.Count > 0 ? _order[0] : BuiltInPalettes.Default);

        private Palette? Find(string? name)
        {
            if (name == null) return null;
            if (_byName.TryGetValue(name, out var p)) return p;
            string normalized = Normalize(name);
            if (_byName.TryGetValue(normalized, out p)) return p;
            return _aliases.TryGetValue(normalized, out var target) && _byName.TryGetValue(target, out p) ? p : null;
        }

        private static string Normalize(string name) => name.Normalize(NormalizationForm.FormC);

        /// <summary>Adds every <see cref="BuiltInPalettes"/> entry (and its former names as aliases).</summary>
        public PaletteLibrary AddBuiltIns()
        {
            foreach (var p in BuiltInPalettes.All.Values) Add(p);
            foreach (var legacy in BuiltInPalettes.LegacyNames) AddAlias(legacy.Key, legacy.Value);
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
