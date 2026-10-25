using System;
using System.Collections.Generic;
using System.Globalization;

namespace RasterField
{
    /// <summary>
    /// UI language (English / Hungarian). Every user-visible string goes through <see cref="T"/>
    /// with its English text as the key; Hungarian translations live in <see cref="Hungarian"/>.
    /// A missing translation falls back to English, so an untranslated string is never blank.
    /// </summary>
    public static partial class L
    {
        /// <summary>Supported languages: <c>en</c>, <c>hu</c>.</summary>
        public static string Language { get; private set; } = "en";

        /// <summary>
        /// Applies a language code; <c>auto</c> (or null) picks Hungarian when the OS UI culture is
        /// Hungarian, English otherwise.
        /// </summary>
        public static void SetLanguage(string? code)
        {
            code = (code ?? "auto").ToLowerInvariant();
            if (code == "auto") code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hu" ? "hu" : "en";
            Language = code == "hu" ? "hu" : "en";
        }

        /// <summary>Translates an English UI string.</summary>
        public static string T(string english)
        {
            if (Language == "hu" && Hungarian.TryGetValue(english, out var hu)) return hu;
            return english;
        }

        /// <summary>
        /// Translates a composite-format string, then formats it with the user's own number format
        /// (the system region — decimal comma on a Hungarian Mac), as native apps do. For display
        /// only: anything written to a file formats its numbers invariantly itself.
        /// </summary>
        public static string F(string englishFormat, params object?[] args) =>
            string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

        /// <summary>A number for display in the user's number format (<paramref name="format"/> as for <see cref="double.ToString(string)"/>).</summary>
        public static string Num(double value, string format = "g6") => value.ToString(format, CultureInfo.CurrentCulture);

        /// <summary>
        /// A coordinate/value unit as shown to the user. ER Mapper's keywords (<c>natural</c>,
        /// <c>METERS</c>, <c>FEET</c>, <c>DEGREES</c>…) become the usual short forms; "natural"
        /// (no declared unit) reads as "map units" in the interface language.
        /// </summary>
        public static string UnitName(string? unit) => (unit ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "" or "NATURAL" => T("map units"),
            "METERS" or "METRES" or "METER" or "METRE" or "M" => "m",
            "KILOMETERS" or "KILOMETRES" or "KM" => "km",
            "FEET" or "FOOT" or "FT" => "ft",
            "US_SURVEY_FEET" or "US SURVEY FEET" => "US ft",
            "DEGREES" or "DEGREE" or "DEG" => "°",
            _ => unit!.Trim(),
        };

        /// <summary>Whether a Hungarian translation exists for <paramref name="english"/> (for tests).</summary>
        internal static bool HasHungarian(string english) => Hungarian.ContainsKey(english);

        private static readonly Dictionary<string, string> Hungarian = BuildHungarian();
    }
}
