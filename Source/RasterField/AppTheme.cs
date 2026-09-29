using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace RasterField
{
    /// <summary>
    /// A small, curated set of semantic colours for the app's chrome — window/panel backgrounds,
    /// bars, borders, text, the raster canvas backdrop — with a distinct light and dark palette.
    /// The one central place the app's look lives, instead of colours hardcoded ad hoc at each
    /// call site (which is how a UI silently breaks the moment a user's system is in a different
    /// theme than whoever wrote the hardcoded value assumed). Read <see cref="IsDark"/> or any of
    /// the brush properties fresh wherever you need them — they always reflect the current
    /// <see cref="Application.ActualThemeVariant"/>.
    /// </summary>
    public static class AppTheme
    {
        /// <summary><see langword="true"/> when the effective theme (system-followed or user-forced) is dark.</summary>
        public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        /// <summary>The window's own backdrop, behind every panel.</summary>
        public static IBrush WindowBackground => IsDark ? DarkWindowBackground : LightWindowBackground;

        /// <summary>A raised panel's background (the side panel, dialogs' content areas).</summary>
        public static IBrush PanelBackground => IsDark ? DarkPanelBackground : LightPanelBackground;

        /// <summary>A slightly-off-window bar's background (status bar, floating toolbars).</summary>
        public static IBrush BarBackground => IsDark ? DarkBarBackground : LightBarBackground;

        /// <summary>Hairline borders and dividers.</summary>
        public static IBrush Border => IsDark ? DarkBorder : LightBorder;

        /// <summary>Primary (high-emphasis) text.</summary>
        public static IBrush TextPrimary => IsDark ? DarkTextPrimary : LightTextPrimary;

        /// <summary>Secondary (de-emphasised, e.g. captions/hints) text.</summary>
        public static IBrush TextSecondary => IsDark ? DarkTextSecondary : LightTextSecondary;

        /// <summary>A dimming scrim behind a modal-ish overlay (the busy indicator).</summary>
        public static IBrush OverlayScrim => IsDark ? DarkScrim : LightScrim;

        /// <summary>The raster canvas's own backdrop, showing through where no data is loaded/no-data cells are transparent.</summary>
        public static IBrush CanvasBackground => IsDark ? DarkCanvasBackground : LightCanvasBackground;

        /// <summary>Highlight for "this is the selected/active thing" rows (e.g. the active layer in the Layers panel).</summary>
        public static IBrush ActiveHighlight => IsDark ? DarkActiveHighlight : LightActiveHighlight;

        /// <summary>The app's accent colour — used sparingly, for "this is the one that's selected/active" borders and similar emphasis.</summary>
        public static IBrush Accent => IsDark ? DarkAccent : LightAccent;

        /// <summary>A fixed, moderately-saturated red for destructive actions (e.g. "remove"), legible on either theme.</summary>
        public static IBrush Danger { get; } = new SolidColorBrush(Color.FromRgb(0xD9, 0x6C, 0x6C));

        // One calm palette: cool slate neutrals plus a single muted teal accent, which sits well
        // next to the earth/green/blue colours of elevation palettes without competing with them.
        internal static readonly Color DarkAccentColor = Color.FromRgb(0x4F, 0xB3, 0xA9);
        internal static readonly Color LightAccentColor = Color.FromRgb(0x1D, 0x86, 0x7C);
        internal static readonly Color DarkWindowColor = Color.FromRgb(0x15, 0x18, 0x1D);
        internal static readonly Color LightWindowColor = Color.FromRgb(0xEC, 0xEF, 0xF2);

        private static readonly IBrush DarkWindowBackground = new SolidColorBrush(DarkWindowColor);
        private static readonly IBrush LightWindowBackground = new SolidColorBrush(LightWindowColor);

        private static readonly IBrush DarkPanelBackground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x25));
        private static readonly IBrush LightPanelBackground = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));

        private static readonly IBrush DarkBarBackground = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x30));
        private static readonly IBrush LightBarBackground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xED));

        private static readonly IBrush DarkBorder = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x3E));
        private static readonly IBrush LightBorder = new SolidColorBrush(Color.FromRgb(0xD6, 0xDB, 0xE1));

        private static readonly IBrush DarkTextPrimary = new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEA));
        private static readonly IBrush LightTextPrimary = new SolidColorBrush(Color.FromRgb(0x1F, 0x26, 0x30));

        private static readonly IBrush DarkTextSecondary = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA2));
        private static readonly IBrush LightTextSecondary = new SolidColorBrush(Color.FromRgb(0x62, 0x6C, 0x7A));

        private static readonly IBrush DarkScrim = new SolidColorBrush(Color.FromArgb(150, 0x0B, 0x0D, 0x10));
        private static readonly IBrush LightScrim = new SolidColorBrush(Color.FromArgb(90, 0x1F, 0x26, 0x30));

        private static readonly IBrush DarkCanvasBackground = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x18));
        private static readonly IBrush LightCanvasBackground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE1, 0xE6));

        private static readonly IBrush DarkActiveHighlight = new SolidColorBrush(Color.FromRgb(0x1D, 0x30, 0x31));
        private static readonly IBrush LightActiveHighlight = new SolidColorBrush(Color.FromRgb(0xE0, 0xF0, 0xEE));

        private static readonly IBrush DarkAccent = new SolidColorBrush(DarkAccentColor);
        private static readonly IBrush LightAccent = new SolidColorBrush(LightAccentColor);

        // ---- typography ------------------------------------------------------------------
        // A small, fixed scale: body 12.5, captions 11, section labels 10.5 (upper-case, tracked).

        /// <summary>Body text size (controls, menus, cards).</summary>
        public const double FontBody = 12.5;

        /// <summary>Captions, hints, secondary values.</summary>
        public const double FontCaption = 11;

        /// <summary>Upper-case section labels in the side panels.</summary>
        public const double FontSection = 10.5;

        /// <summary>A tracked, upper-case, muted section label.</summary>
        public static Avalonia.Controls.TextBlock SectionLabel(string text) => new Avalonia.Controls.TextBlock
        {
            Text = text.ToUpper(System.Globalization.CultureInfo.CurrentCulture),
            FontSize = FontSection,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.8,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 14, 0, 5),
        };

        /// <summary>A small caption above a control.</summary>
        public static Avalonia.Controls.TextBlock Caption(string text) => new Avalonia.Controls.TextBlock
        {
            Text = text,
            FontSize = FontCaption,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 4, 0, 2),
        };
    }

    /// <summary>The app's theme preference — "System" follows the OS light/dark setting live.</summary>
    public enum ThemeMode { System, Light, Dark }
}
