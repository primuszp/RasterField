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

        /// <summary>Translates a composite-format string, then formats it (invariant numbers).</summary>
        public static string F(string englishFormat, params object?[] args) =>
            string.Format(CultureInfo.InvariantCulture, T(englishFormat), args);

        private static readonly Dictionary<string, string> Hungarian = BuildHungarian();
    }
}
