using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Animation;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
            var dark = Dict(AppTheme.DarkAccentColor, Color.FromRgb(0x3A, 0x41, 0x4C), Color.FromRgb(0xE8, 0xEC, 0xF0));
            var light = Dict(AppTheme.LightAccentColor, Color.FromRgb(0xC9, 0xCF, 0xD6), Colors.White);

            // Layer cards: resting, hovered, active (selected) and active + hovered.
            dark["RfCardBrush"] = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x30));
            dark["RfCardHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x3B));
            dark["RfCardActiveBrush"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x30, 0x31));
            dark["RfCardActiveHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x23, 0x3A, 0x3B));
            light["RfCardBrush"] = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xED));
            light["RfCardHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xDC, 0xE0, 0xE5));
            light["RfCardActiveBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xF0, 0xEE));
            light["RfCardActiveHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xD3, 0xEA, 0xE7));

            // Flat icon buttons: muted glyph at rest, a soft round wash and a stronger glyph on hover.
            dark["RfIconBrush"] = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA2));
            dark["RfIconHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF0));
            dark["RfIconHoverBackground"] = new SolidColorBrush(Colors.White, 0.09);
            dark["RfIconPressedBackground"] = new SolidColorBrush(Colors.White, 0.16);
            light["RfIconBrush"] = new SolidColorBrush(Color.FromRgb(0x62, 0x6C, 0x7A));
            light["RfIconHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x1F, 0x26, 0x30));
            light["RfIconHoverBackground"] = new SolidColorBrush(Colors.Black, 0.07);
            light["RfIconPressedBackground"] = new SolidColorBrush(Colors.Black, 0.13);

            resources.ThemeDictionaries[ThemeVariant.Dark] = dark;
            resources.ThemeDictionaries[ThemeVariant.Light] = light;
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
                    new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("RfIconBrush")),
                },
            });
            // Fluent paints hover/pressed/disabled on the template's presenter; restyle all three there.
            styles.Add(new Style(x => x.OfType<Button>().Class("icon").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(Animatable.TransitionsProperty, new Transitions
                    {
                        new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(110) },
                    }),
                },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("icon").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("RfIconHoverBackground")),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfIconHoverBrush")),
                    new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("icon").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("RfIconPressedBackground")),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfAccentBrush")),
                },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("icon").Class(":disabled").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfIconBrush")),
                },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("icon").Class(":disabled"))
            {
                Setters = { new Setter(Visual.OpacityProperty, 0.28) },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("icon").Class(":pressed"))
            {
                Setters = { new Setter(Visual.RenderTransformProperty, new ScaleTransform(0.92, 0.92)) },
            });

            // ---- layer cards: the whole card lights up under the pointer; the active one keeps its accent ----
            styles.Add(new Style(x => x.OfType<Border>().Class("layerCard"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCardBrush")),
                    new Setter(Border.BorderBrushProperty, Brushes.Transparent),
                    new Setter(Animatable.TransitionsProperty, new Transitions
                    {
                        new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120) },
                    }),
                },
            });
            styles.Add(new Style(x => x.OfType<Border>().Class("layerCard").Class(":pointerover"))
            {
                Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCardHoverBrush")) },
            });
            styles.Add(new Style(x => x.OfType<Border>().Class("layerCard").Class("active"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCardActiveBrush")),
                    new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("RfAccentBrush")),
                },
            });
            styles.Add(new Style(x => x.OfType<Border>().Class("layerCard").Class("active").Class(":pointerover"))
            {
                Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCardActiveHoverBrush")) },
            });

            // ---- macOS title-bar buttons (sidebar toggles): borderless, hover-only background ----
            styles.Add(new Style(x => x.OfType<Button>().Class("titlebar"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(Layoutable.WidthProperty, 30.0),
                    new Setter(Layoutable.HeightProperty, 28.0),
                    new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                },
            });

            // ---- numeric inputs: the number centred both ways, compact ▲▼ buttons ----
            // Fluent's spinner buttons are 34 px each, which left e.g. 12 px for the digits of an
            // 80 px R/G/B box; its inner text box also sits the digits against the top edge.
            styles.Add(new Style(x => x.OfType<NumericUpDown>())
            {
                Setters =
                {
                    new Setter(NumericUpDown.TextAlignmentProperty, TextAlignment.Center),
                    new Setter(NumericUpDown.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                },
            });
            styles.Add(new Style(x => x.OfType<NumericUpDown>().Template().OfType<TextBox>().Name("PART_TextBox"))
            {
                Setters =
                {
                    new Setter(TextBox.TextAlignmentProperty, TextAlignment.Center),
                    new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                },
            });
            // The template sets MinWidth on the buttons itself, which outranks a plain style; a selector
            // with an activator (the always-true :not(.rf-wide)) is applied at trigger priority and wins.
            styles.Add(new Style(x => x.OfType<ButtonSpinner>().Template().OfType<RepeatButton>().Not(y => y.Class("rf-wide")))
            {
                Setters =
                {
                    new Setter(Layoutable.MinWidthProperty, 20.0),
                    new Setter(Layoutable.WidthProperty, 20.0),
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
