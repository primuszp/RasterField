using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RasterField.Tests
{
    /// <summary>
    /// The app is bilingual: every string the code translates (T, L.T, L.F and menu headers) must
    /// have a Hungarian entry, or the Hungarian interface silently falls back to English.
    /// </summary>
    public class TranslationCoverageTests
    {
        private static readonly Regex Literal = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        private static readonly Regex Translated = new(@"\b(?:T|L\.T|L\.F)\(\s*""((?:[^""\\]|\\.)*)""\s*(?:\+\s*""((?:[^""\\]|\\.)*)""\s*)*", RegexOptions.Compiled);
        private static readonly Regex MenuHeader = new(@"(?:new Cmd|\bType)\(\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        private static string AppSourceDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RasterField.slnx"))) dir = dir.Parent;
            return dir == null ? throw new InvalidOperationException("Repository root not found.") : Path.Combine(dir.FullName, "Source", "RasterField");
        }

        private static string Unescape(string s) => Regex.Unescape(s);

        [Fact]
        public void Every_translated_string_has_a_hungarian_entry()
        {
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            int checkedKeys = 0;
            foreach (string file in Directory.EnumerateFiles(AppSourceDirectory(), "*.cs").Where(f => !Path.GetFileName(f).StartsWith("Localization", StringComparison.Ordinal)))
            {
                string code = File.ReadAllText(file);
                foreach (Match m in Translated.Matches(code))
                {
                    // A key may be split over concatenated literals: "first line\n" + "second line".
                    string key = string.Concat(Literal.Matches(m.Value).Select(l => Unescape(l.Groups[1].Value)));
                    checkedKeys++;
                    if (!L.HasHungarian(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
                }
                foreach (Match m in MenuHeader.Matches(code))
                {
                    string key = Unescape(m.Groups[1].Value);
                    checkedKeys++;
                    if (!L.HasHungarian(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
            Assert.True(checkedKeys > 300, $"Only {checkedKeys} translated strings found — the source scan is broken.");
            Assert.True(missing.Count == 0, "Missing Hungarian translations:\n" + string.Join("\n", missing));
        }
    }
}
