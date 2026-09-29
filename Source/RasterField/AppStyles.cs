using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace RasterField
{
    /// <summary>
    /// The app's visual layer on top of FluentTheme: compact density, one type scale, the
    /// slate + teal palette fed into Fluent's accent/region colours, and restyled sliders, tabs,
    /// icon buttons and menus. Everything lives in code (the app has no XAML).
    /// </summary>
    public static class AppStyles
    {
        /// <summary>FluentTheme configured with the app palette and compact density.</summary>
        public static FluentTheme CreateFluentTheme()
        {
            var theme = new FluentTheme { DensityStyle = DensityStyle.Compact };
            theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources
            {
                Accent = AppTheme.DarkAccentColor,
                RegionColor = AppTheme.DarkWindowColor,
            };
            theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources
            {
                Accent = AppTheme.LightAccentColor,
                RegionColor = AppTheme.LightWindowColor,
            };
            return theme;
        }

        /// <summary>Theme-dependent brushes the custom templates bind to.</summary>
        public static void AddResources(IResourceDictionary resources)
        {
            ResourceDictionary Dict(Color accent, Color track, Color ring) => new ResourceDictionary
            {
                ["RfAccentBrush"] = new SolidColorBrush(accent),
                ["RfTrackBrush"] = new SolidColorBrush(track),
                ["RfThumbRingBrush"] = new SolidColorBrush(ring),
            };
            resources.ThemeDictionaries[ThemeVariant.Dark] = Dict(AppTheme.DarkAccentColor, Color.FromRgb(0x3A, 0x41, 0x4C), Color.FromRgb(0xE8, 0xEC, 0xF0));
            resources.ThemeDictionaries[ThemeVariant.Light] = Dict(AppTheme.LightAccentColor, Color.FromRgb(0xC9, 0xCF, 0xD6), Colors.White);
        }

        /// <summary>The app's styles; add after the FluentTheme.</summary>
        public static Styles Create()
        {
            var styles = new Styles();

            // ---- type scale: one body size everywhere (Fluent defaults to 14, tab headers to 24) ----
            styles.Add(new Style(x => x.OfType<Window>())
            {
                Setters =
                {
                    new Setter(TextElement.FontSizeProperty, AppTheme.FontBody),
                },
            });
            styles.Add(new Style(x => x.OfType<MenuItem>()) { Setters = { new Setter(TextElement.FontSizeProperty, AppTheme.FontBody) } });
            styles.Add(new Style(x => x.OfType<Menu>()) { Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 2)) } });
            styles.Add(new Style(x => x.OfType<ToolTip>()) { Setters = { new Setter(TextElement.FontSizeProperty, AppTheme.FontCaption) } });

            // ---- tabs: small, calm headers; the selected one is semibold with the accent pipe ----
            styles.Add(new Style(x => x.OfType<TabItem>())
            {
                Setters =
                {
                    new Setter(TextElement.FontSizeProperty, AppTheme.FontBody),
                    new Setter(Layoutable.MinHeightProperty, 30.0),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 2)),
                    new Setter(TextElement.FontWeightProperty, FontWeight.Normal),
                },
            });
            styles.Add(new Style(x => x.OfType<TabItem>().Class(":selected"))
            {
                Setters = { new Setter(TextElement.FontWeightProperty, FontWeight.SemiBold) },
            });

            // ---- slider: 3 px track, a small ringed thumb that grows slightly on hover ----
            styles.Add(new Style(x => x.OfType<Slider>())
            {
                Setters = { new Setter(Layoutable.MinHeightProperty, 22.0) },
            });
            styles.Add(new Style(x => x.OfType<Slider>().Template().OfType<Track>().Name("PART_Track"))
            {
                Setters = { new Setter(Layoutable.HeightProperty, 18.0) },
            });
            styles.Add(new Style(x => x.OfType<Slider>().Template().OfType<RepeatButton>().Name("PART_DecreaseButton"))
            {
                Setters = { new Setter(TemplatedControl.TemplateProperty, TrackPart("RfAccentBrush")) },
            });
            styles.Add(new Style(x => x.OfType<Slider>().Template().OfType<RepeatButton>().Name("PART_IncreaseButton"))
            {
                Setters = { new Setter(TemplatedControl.TemplateProperty, TrackPart("RfTrackBrush")) },
            });
            styles.Add(new Style(x => x.OfType<Slider>().Template().OfType<Thumb>().Name("thumb"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.TemplateProperty, ThumbTemplate()),
                    new Setter(Layoutable.WidthProperty, 14.0),
                    new Setter(Visual.RenderTransformProperty, new ScaleTransform(1, 1)),
                },
            });
            // The Track stretches the thumb to its full height, so the dot is a fixed-size child;
            // hover/drag enlarge it with a transform instead of relayout.
            styles.Add(new Style(x => x.OfType<Slider>().Class(":pointerover").Template().OfType<Thumb>().Name("thumb"))
            {
                Setters = { new Setter(Visual.RenderTransformProperty, new ScaleTransform(1.2, 1.2)) },
            });
            styles.Add(new Style(x => x.OfType<Slider>().Class(":pressed").Template().OfType<Thumb>().Name("thumb"))
            {
                Setters = { new Setter(Visual.RenderTransformProperty, new ScaleTransform(1.3, 1.3)) },
            });
            styles.Add(new Style(x => x.OfType<Slider>().Class(":disabled"))
            {
                Setters = { new Setter(Visual.OpacityProperty, 0.45) },
            });

            // ---- flat icon buttons (layer cards) ----
            styles.Add(new Style(x => x.OfType<Button>().Class("icon"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(TextElement.FontSizeProperty, 11.0),
                    new Setter(Layoutable.WidthProperty, 22.0),
                    new Setter(Layoutable.HeightProperty, 22.0),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                },
            });

            // ---- quieter GridSplitters ----
            styles.Add(new Style(x => x.OfType<GridSplitter>())
            {
                Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent) },
            });

            return styles;
        }

        private static FuncControlTemplate<RepeatButton> TrackPart(string brushKey) => new FuncControlTemplate<RepeatButton>((_, _) =>
        {
            var line = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center };
            line.Bind(Border.BackgroundProperty, line.GetResourceObservable(brushKey));
            return new Grid { Background = Brushes.Transparent, Children = { line } }; // full-height hit area
        });

        private static FuncControlTemplate<Thumb> ThumbTemplate() => new FuncControlTemplate<Thumb>((_, _) =>
        {
            // A 12 px accent dot with a light ring and a soft shadow, centred in the thumb's hit area.
            var dot = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BoxShadow = BoxShadows.Parse("0 1 2 0 #50000000"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.Bind(Border.BackgroundProperty, dot.GetResourceObservable("RfAccentBrush"));
            dot.Bind(Border.BorderBrushProperty, dot.GetResourceObservable("RfThumbRingBrush"));
            return new Grid { Background = Brushes.Transparent, Children = { dot } };
        });
    }
}
