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
        public static IBrush Danger { get; } = new SolidColorBrush(Color.FromRgb(0xDC, 0x5C, 0x5C));

        private static readonly IBrush DarkWindowBackground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));
        private static readonly IBrush LightWindowBackground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5));

        private static readonly IBrush DarkPanelBackground = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x29));
        private static readonly IBrush LightPanelBackground = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFC));

        private static readonly IBrush DarkBarBackground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
        private static readonly IBrush LightBarBackground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEB));

        private static readonly IBrush DarkBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40));
        private static readonly IBrush LightBorder = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8));

        private static readonly IBrush DarkTextPrimary = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
        private static readonly IBrush LightTextPrimary = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

        private static readonly IBrush DarkTextSecondary = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA6));
        private static readonly IBrush LightTextSecondary = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73));

        private static readonly IBrush DarkScrim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        private static readonly IBrush LightScrim = new SolidColorBrush(Color.FromArgb(90, 20, 20, 24));

        private static readonly IBrush DarkCanvasBackground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
        private static readonly IBrush LightCanvasBackground = new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDE));

        private static readonly IBrush DarkActiveHighlight = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        private static readonly IBrush LightActiveHighlight = new SolidColorBrush(Color.FromRgb(0xDC, 0xE8, 0xF7));

        private static readonly IBrush DarkAccent = new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xFF));
        private static readonly IBrush LightAccent = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xE0));
    }

    /// <summary>The app's theme preference — "System" follows the OS light/dark setting live.</summary>
    public enum ThemeMode { System, Light, Dark }
}
