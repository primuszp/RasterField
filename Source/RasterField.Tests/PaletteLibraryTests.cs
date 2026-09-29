using System.Collections.Generic;
using System.IO;
using RasterField.Rendering;
using Xunit;

namespace RasterField.Tests
{
    public class PaletteLibraryTests
    {
        [Fact]
        public void Add_then_Get_finds_the_palette_case_insensitively()
        {
            var lib = new PaletteLibrary();
            var palette = Palette.FromStops("MyPalette", new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(255, 255, 255)) });

            lib.Add(palette);

            Assert.Equal(1, lib.Count);
            Assert.Same(palette, lib.Get("mypalette"));
            Assert.Contains("MyPalette", lib.Names);
        }

        [Fact]
        public void Add_replaces_an_existing_palette_in_place_by_name()
        {
            var lib = new PaletteLibrary();
            lib.Add(Palette.FromStops("P", new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(1, 1, 1)) }));
            var replacement = Palette.FromStops("P", new[] { (0.0, new ColorRgba(9, 9, 9)), (1.0, new ColorRgba(8, 8, 8)) });

            lib.Add(replacement);

            Assert.Equal(1, lib.Count);
            Assert.Same(replacement, lib.Get("P"));
        }

        [Fact]
        public void Get_returns_null_for_an_unknown_name()
        {
            var lib = new PaletteLibrary();
            Assert.Null(lib.Get("nosuch"));
            Assert.Null(lib.Get(null));
        }

        [Fact]
        public void GetOrDefault_falls_back_to_the_first_entry_then_the_built_in_default()
        {
            var empty = new PaletteLibrary();
            Assert.Same(BuiltInPalettes.Default, empty.GetOrDefault("nosuch"));

            var lib = new PaletteLibrary();
            var first = Palette.FromStops("First", new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(1, 1, 1)) });
            lib.Add(first);

            Assert.Same(first, lib.GetOrDefault("nosuch"));
            Assert.Same(first, lib.GetOrDefault(null));
        }

        [Fact]
        public void AddBuiltIns_adds_every_built_in_palette()
        {
            var lib = new PaletteLibrary().AddBuiltIns();
            Assert.Equal(BuiltInPalettes.All.Count, lib.Count);
            Assert.Contains("Elevation", lib.Names);
        }

        [Fact]
        public void AddPalFiles_ignores_a_missing_directory()
        {
            var lib = new PaletteLibrary();
            var result = lib.AddPalFiles(Path.Combine(Path.GetTempPath(), "gevi_no_such_dir_" + System.Guid.NewGuid().ToString("N")));

            Assert.Same(lib, result); // fluent return
            Assert.Equal(0, lib.Count);
        }

        [Fact]
        public void AddPalFiles_loads_pal_files_and_reports_unparsable_ones()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_paldir_" + System.Guid.NewGuid().ToString("N")));
            try
            {
                var good = Palette.FromStops("Good", new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(255, 255, 255)) });
                PaletteFile.Save(good, Path.Combine(dir.FullName, "good.pal"));
                File.WriteAllText(Path.Combine(dir.FullName, "bad.pal"), "not a palette file at all\n### garbage ###");

                var errors = new List<string>();
                var lib = new PaletteLibrary();
                lib.AddPalFiles(dir.FullName, (file, _) => errors.Add(Path.GetFileName(file)));

                Assert.Equal(1, lib.Count);
                Assert.Contains("good", lib.Names); // .pal has no name field; the file name (without extension) is used
                Assert.Contains("bad.pal", errors);
            }
            finally { dir.Delete(recursive: true); }
        }

        [Fact]
        public void CreateDefault_includes_built_ins_and_a_palette_directorys_files()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gevi_paldir2_" + System.Guid.NewGuid().ToString("N")));
            try
            {
                var custom = Palette.FromStops("Custom", new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(2, 2, 2)) });
                PaletteFile.Save(custom, Path.Combine(dir.FullName, "custom.pal"));

                var lib = PaletteLibrary.CreateDefault(dir.FullName);

                Assert.Contains("Elevation", lib.Names);
                Assert.Contains("custom", lib.Names); // .pal has no name field; the file name (without extension) is used
            }
            finally { dir.Delete(recursive: true); }
        }

        [Fact]
        public void Alias_resolves_to_the_renamed_palette_but_never_shadows_a_real_name()
        {
            var lib = new PaletteLibrary();
            var renamed = Palette.FromStops("Terrain (yellow-green)", new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(9, 9, 9)) });
            var real = Palette.FromStops("old", new[] { (0.0, new ColorRgba(1, 1, 1)), (1.0, new ColorRgba(2, 2, 2)) });
            lib.Add(renamed);
            lib.Add(real);
            lib.AddAlias("felszin1", "Terrain (yellow-green)");
            lib.AddAlias("old", "Terrain (yellow-green)");

            Assert.Same(renamed, lib.Get("FELSZIN1"));
            Assert.Equal("Terrain (yellow-green)", lib.Resolve("felszin1"));
            Assert.Same(real, lib.Get("old"));
            Assert.Null(lib.Resolve("nosuch"));
            Assert.Null(lib.Resolve(null));
        }

        [Fact]
        public void Lookup_ignores_unicode_normalisation_differences()
        {
            var lib = new PaletteLibrary();
            lib.Add(Palette.FromStops("Grayscale", new[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(255, 255, 255)) }));
            lib.AddAlias("szürkeskála", "Grayscale");

            // A decomposed (NFD) spelling, as macOS file systems often store it.
            string decomposed = "szürkeskála".Normalize(System.Text.NormalizationForm.FormD);
            Assert.Equal("Grayscale", lib.Resolve(decomposed));
        }

        [Fact]
        public void Former_built_in_name_still_resolves()
        {
            var lib = new PaletteLibrary().AddBuiltIns();
            Assert.Equal("Land & Sea", lib.Resolve("Land &amp; Sea"));
        }
    }
}
