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

        internal static readonly IBrush DarkTextPrimaryBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEA));
        internal static readonly IBrush LightTextPrimaryBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x26, 0x30));
        private static IBrush DarkTextPrimary => DarkTextPrimaryBrush;
        private static IBrush LightTextPrimary => LightTextPrimaryBrush;

        internal static readonly IBrush DarkTextSecondaryBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA2));
        internal static readonly IBrush LightTextSecondaryBrush = new SolidColorBrush(Color.FromRgb(0x62, 0x6C, 0x7A));
        private static IBrush DarkTextSecondary => DarkTextSecondaryBrush;
        private static IBrush LightTextSecondary => LightTextSecondaryBrush;

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

        /// <summary>
        /// Fixed-width text (ERS headers, matrices, tables): the platform's own monospaced face —
        /// Menlo on macOS, Consolas on Windows, DejaVu Sans Mono on Linux — rather than the generic
        /// "monospace", which macOS does not resolve and silently replaces with a proportional font.
        /// </summary>
        public static FontFamily MonospaceFont { get; } = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, Liberation Mono, monospace");

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

        /// <summary>Stroked line icons on a 14 × 14 grid (drawn, so they sit exactly centred, unlike font fallback glyphs).</summary>
        public static class Icons
        {
            public static readonly Geometry Rising = Geometry.Parse("M2.5,11.5 L11.5,2.5 M5.5,2.5 H11.5 V8.5");
            public static readonly Geometry Falling = Geometry.Parse("M2.5,2.5 L11.5,11.5 M11.5,5.5 V11.5 H5.5");
            public static readonly Geometry ChevronRight = Geometry.Parse("M5,2.5 L9.5,7 L5,11.5");
            public static readonly Geometry Target = Geometry.Parse("M7,0.5 V3.5 M7,10.5 V13.5 M0.5,7 H3.5 M10.5,7 H13.5 M7,2.8 A4.2,4.2 0 1 1 6.99,2.8");
            public static readonly Geometry Close = Geometry.Parse("M3,3 L11,11 M11,3 L3,11");
            public static readonly Geometry Ruler = Geometry.Parse("M1.5,9.5 L9.5,1.5 L12.5,4.5 L4.5,12.5 Z M4,7 L5.5,8.5 M6,5 L7.5,6.5 M8,3 L9.5,4.5");
            public static readonly Geometry Polygon = Geometry.Parse("M2,4.5 L7,1.5 L12.5,5 L10.5,12 L3,11 Z");
            public static readonly Geometry Profile = Geometry.Parse("M1,11 L4.5,6 L7,8.5 L10,3 L13,7 M1,13 H13");
            public static readonly Geometry Layers = Geometry.Parse("M7,1.5 L12.5,4.5 L7,7.5 L1.5,4.5 Z M1.5,7.5 L7,10.5 L12.5,7.5 M1.5,10 L7,13 L12.5,10");
        }

        /// <summary>A stroked icon that takes the surrounding text colour.</summary>
        public static Avalonia.Controls.Shapes.Path Icon(Geometry data, double size = 14, double stroke = 1.5)
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = data,
                Width = 14,
                Height = 14,
                Stretch = Stretch.None,
                StrokeThickness = stroke,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            if (size != 14) path.RenderTransform = new ScaleTransform(size / 14, size / 14);
            path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, path.GetObservable(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
            return path;
        }

        /// <summary>An icon and a label side by side, both centred on the same line.</summary>
        public static Avalonia.Controls.StackPanel IconLabel(Geometry icon, string text, FontWeight? weight = null) => new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                Icon(icon),
                new Avalonia.Controls.TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontWeight = weight ?? FontWeight.Normal },
            },
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
