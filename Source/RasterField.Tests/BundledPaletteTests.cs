using System;
using System.IO;
using System.Linq;
using RasterField.Rendering;
using Xunit;

namespace RasterField.Tests
{
    /// <summary>The palettes shipped in the app's <c>palette/</c> folder: distinct content, English + Hungarian names.</summary>
    public class BundledPaletteTests
    {
        private static string BundledDirectory() =>
            PaletteStorage.BundledPaletteDirectory() ?? throw new InvalidOperationException("Bundled palette folder not found.");

        private static string[] BundledNames() =>
            Directory.EnumerateFiles(BundledDirectory())
                .Where(f => new[] { ".pal", ".png", ".bmp" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(Path.GetFileNameWithoutExtension)
                .Select(n => n!)
                .ToArray();

        [Fact]
        public void Every_built_in_and_bundled_palette_has_a_hungarian_name()
        {
            var names = BuiltInPalettes.All.Keys.Concat(BundledNames()).ToList();
            Assert.NotEmpty(names);
            Assert.All(names, n => Assert.True(L.HasHungarian(n), $"No Hungarian name for palette '{n}'."));
        }

        [Fact]
        public void Bundled_pal_files_differ_from_each_other_and_from_the_built_ins()
        {
            var palettes = BuiltInPalettes.All.Values
                .Concat(Directory.EnumerateFiles(BundledDirectory(), "*.pal").Select(PaletteFile.Load))
                .ToList();

            for (int i = 0; i < palettes.Count; i++)
                for (int j = i + 1; j < palettes.Count; j++)
                {
                    double diff = MeanAbsoluteDifference(palettes[i], palettes[j]);
                    Assert.True(diff > 2.0, $"'{palettes[i].Name}' and '{palettes[j].Name}' are (near-)duplicates (mean Δ {diff:0.##}).");
                }
        }

        [Fact]
        public void Every_former_bundled_name_resolves_to_an_existing_palette()
        {
            var lib = PaletteLibrary.CreateDefault(BundledDirectory());
            // Image strips are decoded by the app (Avalonia); stand-ins with the same names suffice here.
            foreach (string name in BundledNames())
                if (lib.Get(name) == null)
                    lib.Add(Palette.FromStops(name, new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(255, 255, 255)) }));
            foreach (var legacy in PaletteStorage.BundledLegacyNames) lib.AddAlias(legacy.Key, legacy.Value);

            Assert.All(PaletteStorage.BundledLegacyNames, legacy =>
                Assert.Equal(legacy.Value, lib.Resolve(legacy.Key)));
        }

        private static double MeanAbsoluteDifference(Palette a, Palette b)
        {
            double sum = 0;
            for (int i = 0; i < Palette.Size; i++)
            {
                var x = a.Entries[i];
                var y = b.Entries[i];
                sum += Math.Abs(x.R - y.R) + Math.Abs(x.G - y.G) + Math.Abs(x.B - y.B);
            }
            return sum / (Palette.Size * 3);
        }
    }
}
