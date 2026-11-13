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

            dark["RfBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x3E));
            light["RfBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD6, 0xDB, 0xE1));

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

            // Fields (text, numeric, combo): a quiet inset well with a hairline that firms up on hover
            // and turns accent on focus, plus a soft accent halo — instead of Fluent's thick underline.
            static SolidColorBrush B(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));
            void Fields(ResourceDictionary d, uint bg, uint bgHover, uint border, uint borderHover, uint placeholder, Color accent)
            {
                foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundFocused", "ComboBoxBackground", "ComboBoxBackgroundUnfocused", "ComboBoxBackgroundFocused" })
                    d[key] = B(bg);
                d["TextControlBackgroundPointerOver"] = d["ComboBoxBackgroundPointerOver"] = d["ComboBoxBackgroundPressed"] = B(bgHover);
                d["TextControlBorderBrush"] = d["ComboBoxBorderBrush"] = B(border);
                d["TextControlBorderBrushPointerOver"] = d["ComboBoxBorderBrushPointerOver"] = B(borderHover);
                d["TextControlBorderBrushFocused"] = d["ComboBoxBorderBrushPressed"] = d["ComboBoxBackgroundBorderBrushFocused"] = new SolidColorBrush(accent);
                d["TextControlPlaceholderForeground"] = d["TextControlPlaceholderForegroundPointerOver"] = d["TextControlPlaceholderForegroundFocused"] = B(placeholder);
                d["RfFieldBorderHoverBrush"] = B(borderHover);
                d["RfFocusRing"] = new BoxShadows(new BoxShadow { Spread = 3, Color = Color.FromArgb(0x38, accent.R, accent.G, accent.B) });
            }
            Fields(dark, 0xFF171A1F, 0xFF1A1E24, 0xFF2F3540, 0xFF434B58, 0xFF5F6876, AppTheme.DarkAccentColor);
            Fields(light, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFD3D9E0, 0xFFAEB7C2, 0xFF98A1AD, AppTheme.LightAccentColor);

            // Secondary buttons: a slightly raised tile with a hairline, never Fluent's flat grey slab.
            void Buttons(ResourceDictionary d, uint bg, uint hover, uint pressed, uint border, uint borderHover)
            {
                d["ButtonBackground"] = B(bg);
                d["ButtonBackgroundPointerOver"] = B(hover);
                d["ButtonBackgroundPressed"] = B(pressed);
                d["ButtonBorderBrush"] = B(border);
                d["ButtonBorderBrushPointerOver"] = d["ButtonBorderBrushPressed"] = B(borderHover);
            }
            Buttons(dark, 0xFF252A32, 0xFF2C323B, 0xFF20242B, 0xFF333A45, 0xFF434B58);
            // Accented buttons in dark mode: a deeper teal, so white text keeps ≥ 4.5 : 1 contrast
            // (the bright dark-theme accent is right for lines and dots, too light under text).
            dark["AccentButtonBackground"] = B(0xFF23766D);
            dark["AccentButtonBackgroundPointerOver"] = B(0xFF2A847A);
            dark["AccentButtonBackgroundPressed"] = B(0xFF1D6860);
            dark["AccentButtonForeground"] = dark["AccentButtonForegroundPointerOver"] = dark["AccentButtonForegroundPressed"] = B(0xFFFFFFFF);
            Buttons(light, 0xFFFFFFFF, 0xFFF2F4F7, 0xFFE8EBEF, 0xFFD3D9E0, 0xFFAEB7C2);

            // Tinted callouts (hints, result summaries) and the stat tiles in the analysis panel.
            dark["RfCalloutBrush"] = new SolidColorBrush(AppTheme.DarkAccentColor, 0.10);
            light["RfCalloutBrush"] = new SolidColorBrush(AppTheme.LightAccentColor, 0.08);
            dark["RfWarnBrush"] = B(0xFFE0A458);
            light["RfWarnBrush"] = B(0xFFB4700F);
            dark["RfWarnCalloutBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xA4, 0x58), 0.12);
            light["RfWarnCalloutBrush"] = new SolidColorBrush(Color.FromRgb(0xB4, 0x70, 0x0F), 0.09);
            dark["RfTextPrimaryBrush"] = AppTheme.DarkTextPrimaryBrush;
            light["RfTextPrimaryBrush"] = AppTheme.LightTextPrimaryBrush;
            dark["RfTextSecondaryBrush"] = AppTheme.DarkTextSecondaryBrush;
            light["RfTextSecondaryBrush"] = AppTheme.LightTextSecondaryBrush;

            resources.ThemeDictionaries[ThemeVariant.Dark] = dark;
            resources.ThemeDictionaries[ThemeVariant.Light] = light;

            // Geometry shared by every field and button: one radius, hairline borders also when focused.
            resources["ControlCornerRadius"] = new CornerRadius(6);
            resources["TextControlBorderThemeThickness"] = new Thickness(1);
            resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
            resources["ComboBoxBorderThemeThickness"] = new Thickness(1);
            resources["ButtonBorderThemeThickness"] = new Thickness(1);

            // One control height (28 px) and matching inner padding for every field and button, so a
            // combo next to a numeric box next to a button lines up on both edges and on the text baseline.
            resources["TextControlThemeMinHeight"] = FieldHeight;
            resources["TextControlThemePadding"] = new Thickness(10, 4, 10, 4);
            resources["ComboBoxMinHeight"] = FieldHeight;
            resources["ComboBoxPadding"] = new Thickness(10, 4, 6, 4);
            resources["ButtonPadding"] = new Thickness(12, 4, 12, 4);

            // Sliders: Fluent reserves 15 px above and below the track for tick bars the app never shows.
            resources["SliderPreContentMargin"] = new GridLength(0);
            resources["SliderPostContentMargin"] = new GridLength(0);
            resources["SliderHorizontalHeight"] = 22.0;

            // Expanders ("Advanced"): a field-height header instead of Fluent's 48 px slab.
            resources["ExpanderMinHeight"] = FieldHeight + 4;
            resources["ExpanderHeaderPadding"] = new Thickness(12, 0, 8, 0);
            resources["ExpanderContentPadding"] = new Thickness(12, 10, 12, 12);
        }

        /// <summary>The height of every text field, numeric box, combo box and standard button.</summary>
        public const double FieldHeight = 28;

        /// <summary>
        /// A segmented ListBox's selected item is a raised tile rather than Fluent's accent wash: Fluent
        /// paints selection from the SystemControlHighlightListAccent* brushes, so they are redefined
        /// for this control only (selected, selected + hover, selected + pressed).
        /// </summary>
        public static void UseSegmentedSelectionBrushes(Control control) =>
            UseSelectionBrushes(control, Color.FromRgb(0x33, 0x3A, 0x45), Color.FromRgb(0x3A, 0x42, 0x4E), Colors.White, Color.FromRgb(0xF7, 0xF8, 0xFA));

        /// <summary>A list's selected row as a soft accent tint (the active-card colours) instead of Fluent's strong wash.</summary>
        public static void UseSoftSelectionBrushes(Control control) =>
            UseSelectionBrushes(control, Color.FromRgb(0x1D, 0x30, 0x31), Color.FromRgb(0x23, 0x3A, 0x3B), Color.FromRgb(0xE0, 0xF0, 0xEE), Color.FromRgb(0xD3, 0xEA, 0xE7));

        private static void UseSelectionBrushes(Control control, Color darkSelected, Color darkHover, Color lightSelected, Color lightHover)
        {
            ResourceDictionary Dict(Color selected, Color hover) => new ResourceDictionary
            {
                ["SystemControlHighlightListAccentLowBrush"] = new SolidColorBrush(selected),
                ["SystemControlHighlightListAccentMediumBrush"] = new SolidColorBrush(hover),
                ["SystemControlHighlightListAccentHighBrush"] = new SolidColorBrush(hover),
            };
            control.Resources.ThemeDictionaries[ThemeVariant.Dark] = Dict(darkSelected, darkHover);
            control.Resources.ThemeDictionaries[ThemeVariant.Light] = Dict(lightSelected, lightHover);
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

            // ---- standard buttons and fields: one height, content centred both ways ----
            styles.Add(new Style(x => x.OfType<Button>())
            {
                Setters =
                {
                    new Setter(Layoutable.MinHeightProperty, FieldHeight),
                    new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                },
            });
            styles.Add(new Style(x => x.OfType<TextBox>())
            {
                Setters = { new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center) },
            });
            styles.Add(new Style(x => x.OfType<ComboBox>())
            {
                Setters = { new Setter(ComboBox.VerticalContentAlignmentProperty, VerticalAlignment.Center) },
            });
            styles.Add(new Style(x => x.OfType<CheckBox>())
            {
                Setters =
                {
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                    new Setter(Layoutable.MinHeightProperty, FieldHeight),
                },
            });
            styles.Add(new Style(x => x.OfType<RadioButton>())
            {
                Setters =
                {
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                    new Setter(Layoutable.MinHeightProperty, FieldHeight),
                },
            });

            // ---- flat icon buttons (layer cards) ----
            styles.Add(new Style(x => x.OfType<Button>().Class("icon"))
            {
                Setters =
                {
                    new Setter(Layoutable.MinHeightProperty, 0.0),
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

            // ---- ghost buttons ("+ Add layer…"): an outline matching the cards, filled only on hover ----
            styles.Add(new Style(x => x.OfType<Button>().Class("ghost"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                    new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("RfBorderBrush")),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                    new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("RfIconBrush")),
                    new Setter(Layoutable.MinHeightProperty, 30.0),
                },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("ghost").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("RfCardHoverBrush")),
                    new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("RfBorderBrush")),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfIconHoverBrush")),
                },
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

            // ---- fields: a soft accent halo around the focused field (the border itself turns accent via the resources) ----
            styles.Add(new Style(x => x.OfType<TextBox>().Class(":focus").Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters = { new Setter(Border.BoxShadowProperty, new DynamicResourceExtension("RfFocusRing")) },
            });
            styles.Add(new Style(x => x.OfType<TextBox>().Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Setter(Animatable.TransitionsProperty, new Transitions
                    {
                        new BrushTransition { Property = Border.BorderBrushProperty, Duration = TimeSpan.FromMilliseconds(120) },
                    }),
                },
            });

            // ---- numeric inputs: one field with the digits centred and slim stacked chevrons inside ----
            // Fluent draws two 34 px ▲▼ tiles beside the box, which left e.g. 12 px for the digits of
            // an 80 px R/G/B box; here the spinner is a 16 px column the field itself owns.
            styles.Add(new Style(x => x.OfType<NumericUpDown>())
            {
                Setters =
                {
                    new Setter(NumericUpDown.TextAlignmentProperty, TextAlignment.Center),
                    new Setter(NumericUpDown.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextElement.FontFeaturesProperty, new FontFeatureCollection { FontFeature.Parse("tnum") }),
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
            // The inner text box draws nothing of its own: the spinner's border is the field.
            foreach (var state in new[] { ":focus", ":pointerover", ":disabled" })
            {
                styles.Add(new Style(x => x.OfType<NumericUpDown>().Template().OfType<TextBox>().Name("PART_TextBox").Class(state).Template().OfType<Border>().Name("PART_BorderElement"))
                {
                    Setters =
                    {
                        new Setter(Border.BackgroundProperty, Brushes.Transparent),
                        new Setter(Border.BorderBrushProperty, Brushes.Transparent),
                        new Setter(Border.BoxShadowProperty, default(BoxShadows)),
                    },
                });
            }
            styles.Add(new Style(x => x.OfType<ButtonSpinner>())
            {
                Setters = { new Setter(TemplatedControl.TemplateProperty, SpinnerTemplate()) },
            });
            styles.Add(new Style(x => x.OfType<ButtonSpinner>().Class(":pointerover"))
            {
                Setters = { new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("RfFieldBorderHoverBrush")) },
            });
            styles.Add(new Style(x => x.OfType<ButtonSpinner>().Class(":focus-within"))
            {
                Setters = { new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("RfAccentBrush")) },
            });
            styles.Add(new Style(x => x.OfType<ButtonSpinner>().Class(":focus-within").Template().OfType<Border>().Name("RfSpinnerBorder"))
            {
                Setters = { new Setter(Border.BoxShadowProperty, new DynamicResourceExtension("RfFocusRing")) },
            });
            styles.Add(new Style(x => x.OfType<RepeatButton>().Class("rf-spin"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3)),
                    new Setter(Layoutable.MinHeightProperty, 0.0),
                    new Setter(Layoutable.MinWidthProperty, 0.0),
                    new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("RfIconBrush")),
                },
            });
            styles.Add(new Style(x => x.OfType<RepeatButton>().Class("rf-spin").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("RfIconHoverBackground")),
                    new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfAccentBrush")),
                },
            });
            styles.Add(new Style(x => x.OfType<RepeatButton>().Class("rf-spin").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("RfIconPressedBackground")),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfAccentBrush")),
                },
            });
            // A chevron at its limit (e.g. ▼ at the minimum) fades out instead of turning into a grey box.
            styles.Add(new Style(x => x.OfType<RepeatButton>().Class("rf-spin").Class(":disabled").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
            styles.Add(new Style(x => x.OfType<RepeatButton>().Class("rf-spin").Class(":disabled"))
            {
                Setters = { new Setter(Visual.OpacityProperty, 0.3) },
            });

            // ---- segmented tab strip (the Analysis panel): pills instead of headers with a pipe ----
            styles.Add(new Style(x => x.OfType<TabControl>().Class("segmented").Descendant().OfType<TabItem>())
            {
                Setters =
                {
                    new Setter(TextElement.FontSizeProperty, AppTheme.FontCaption + 0.5),
                    new Setter(Layoutable.MinHeightProperty, 26.0),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 3)),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                    new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("RfIconBrush")),
                    new Setter(TextElement.FontWeightProperty, FontWeight.Medium),
                },
            });
            styles.Add(new Style(x => x.OfType<TabControl>().Class("segmented").Descendant().OfType<TabItem>().Class(":selected"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("RfCardActiveBrush")),
                    new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("RfAccentBrush")),
                    new Setter(TextElement.FontWeightProperty, FontWeight.SemiBold),
                },
            });
            styles.Add(new Style(x => x.OfType<TabControl>().Class("segmented").Descendant().OfType<TabItem>().Class(":selected").Template().OfType<Border>().Name("PART_SelectedPipe"))
            {
                Setters = { new Setter(Visual.IsVisibleProperty, false) },
            });
            styles.Add(new Style(x => x.OfType<TabControl>().Class("segmented").Descendant().OfType<TabItem>().Class(":pointerover").Template().OfType<Border>().Name("PART_LayoutRoot"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCardHoverBrush")),
                    new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("RfIconHoverBrush")),
                },
            });
            styles.Add(new Style(x => x.OfType<TabControl>().Class("segmented").Descendant().OfType<TabItem>().Class(":selected").Class(":pointerover").Template().OfType<Border>().Name("PART_LayoutRoot"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCardActiveHoverBrush")),
                    new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("RfAccentBrush")),
                },
            });

            // ---- list rows everywhere: comfortable height, rounded selection, text centred vertically ----
            styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 5)),
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                },
            });
            styles.Add(new Style(x => x.OfType<ListBoxItem>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(6)) },
            });

            // ---- segmented control (ListBox.segmented): a recessed track with a raised thumb on the choice ----
            styles.Add(new Style(x => x.OfType<ListBox>().Class("segmented"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("RfCardBrush")),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(2)),
                },
            });
            styles.Add(new Style(x => x.OfType<ListBox>().Class("segmented").Descendant().OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 4)),
                    new Setter(Layoutable.MinHeightProperty, 0.0),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                    new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(TextElement.FontSizeProperty, AppTheme.FontCaption + 0.5),
                    new Setter(TextElement.FontWeightProperty, FontWeight.Medium),
                    new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("RfIconBrush")),
                },
            });
            styles.Add(new Style(x => x.OfType<ListBox>().Class("segmented").Descendant().OfType<ListBoxItem>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(6)),
                    new Setter(Animatable.TransitionsProperty, new Transitions
                    {
                        new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(140) },
                    }),
                },
            });
            foreach (var state in new[] { ":pointerover", ":pressed" })
            {
                styles.Add(new Style(x => x.OfType<ListBox>().Class("segmented").Descendant().OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("RfIconHoverBackground")),
                        new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfIconHoverBrush")),
                    },
                });
            }
            // Fluent restyles a selected item separately when hovered, pressed or focused; cover each combination.
            foreach (var state in new[] { ":selected", ":pointerover", ":pressed", ":focus" })
            {
                styles.Add(new Style(x => x.OfType<ListBox>().Class("segmented").Descendant().OfType<ListBoxItem>().Class(":selected")
                    .Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfTextPrimaryBrush")),
                        new Setter(ContentPresenter.BoxShadowProperty, BoxShadows.Parse("0 1 2 0 #33000000")),
                    },
                });
            }
            styles.Add(new Style(x => x.OfType<ListBox>().Class("segmented").Descendant().OfType<ListBoxItem>().Class(":selected"))
            {
                Setters = { new Setter(TextElement.FontWeightProperty, FontWeight.SemiBold) },
            });

            // ---- chips: compact preset buttons ----
            styles.Add(new Style(x => x.OfType<Button>().Class("chip"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0, 2)),
                    new Setter(Layoutable.MinHeightProperty, 22.0),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(11)),
                    new Setter(TextElement.FontSizeProperty, AppTheme.FontCaption),
                    new Setter(TextElement.FontFeaturesProperty, new FontFeatureCollection { FontFeature.Parse("tnum") }),
                },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("chip").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("RfAccentBrush")),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfAccentBrush")),
                },
            });

            // ---- disclosure: a borderless "› More options" toggle ----
            styles.Add(new Style(x => x.OfType<Button>().Class("disclosure"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(2, 3, 6, 3)),
                    new Setter(Layoutable.MinHeightProperty, 24.0),
                    new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("RfIconBrush")),
                    new Setter(TextElement.FontSizeProperty, AppTheme.FontCaption + 0.5),
                },
            });
            styles.Add(new Style(x => x.OfType<Button>().Class("disclosure").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("RfAccentBrush")),
                },
            });

            // ---- callouts: a tinted, rounded note (hints, results) ----
            styles.Add(new Style(x => x.OfType<Border>().Class("callout"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCalloutBrush")),
                    new Setter(Border.CornerRadiusProperty, new CornerRadius(8)),
                    new Setter(Decorator.PaddingProperty, new Thickness(10, 8)),
                },
            });
            styles.Add(new Style(x => x.OfType<Border>().Class("callout").Class("warn"))
            {
                Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfWarnCalloutBrush")) },
            });
            // Stat tiles: a muted caption over a large tabular number.
            styles.Add(new Style(x => x.OfType<Border>().Class("stat"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RfCardBrush")),
                    new Setter(Border.CornerRadiusProperty, new CornerRadius(8)),
                    new Setter(Decorator.PaddingProperty, new Thickness(9, 6)),
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

        private static readonly Geometry ChevronUp = Geometry.Parse("M0,4 L3.5,0.5 L7,4");
        private static readonly Geometry ChevronDown = Geometry.Parse("M0,0.5 L3.5,4 L7,0.5");

        /// <summary>
        /// ButtonSpinner (inside every NumericUpDown): the field's border and background, the content,
        /// and a slim column of two stacked chevrons on the right that fills the field's height.
        /// </summary>
        private static FuncControlTemplate<ButtonSpinner> SpinnerTemplate() => new FuncControlTemplate<ButtonSpinner>((spinner, scope) =>
        {
            RepeatButton Arrow(string name, Geometry chevron)
            {
                var glyph = new Avalonia.Controls.Shapes.Path
                {
                    Data = chevron,
                    StrokeThickness = 1.4,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Width = 7,
                    Height = 4.5,
                };
                glyph.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, glyph.GetObservable(TextElement.ForegroundProperty));
                var button = new RepeatButton
                {
                    Name = name,
                    Content = glyph,
                    Focusable = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                button.Classes.Add("rf-spin");
                return button.RegisterInNameScope(scope);
            }

            var increase = Arrow("PART_IncreaseButton", ChevronUp);
            var decrease = Arrow("PART_DecreaseButton", ChevronDown);
            Grid.SetRow(decrease, 1);
            var arrows = new Grid
            {
                RowDefinitions = new RowDefinitions("*,*"),
                Width = 16,
                Margin = new Thickness(0, 3, 3, 3),
                Children = { increase, decrease },
            };
            arrows.Bind(Visual.IsVisibleProperty, spinner.GetObservable(ButtonSpinner.ShowButtonSpinnerProperty));
            Grid.SetColumn(arrows, 1);

            var content = new ContentPresenter { Name = "PART_ContentPresenter" }.RegisterInNameScope(scope);
            content.Bind(ContentPresenter.ContentProperty, spinner.GetObservable(ContentControl.ContentProperty));
            content.Bind(ContentPresenter.ContentTemplateProperty, spinner.GetObservable(ContentControl.ContentTemplateProperty));
            content.Bind(ContentPresenter.PaddingProperty, spinner.GetObservable(TemplatedControl.PaddingProperty));
            content.Bind(ContentPresenter.HorizontalContentAlignmentProperty, spinner.GetObservable(ContentControl.HorizontalContentAlignmentProperty));
            content.Bind(ContentPresenter.VerticalContentAlignmentProperty, spinner.GetObservable(ContentControl.VerticalContentAlignmentProperty));

            var border = new Border
            {
                Name = "RfSpinnerBorder",
                Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { content, arrows } },
                Transitions = new Transitions
                {
                    new BrushTransition { Property = Border.BorderBrushProperty, Duration = TimeSpan.FromMilliseconds(120) },
                },
            }.RegisterInNameScope(scope);
            border.Bind(Border.BackgroundProperty, spinner.GetObservable(TemplatedControl.BackgroundProperty));
            border.Bind(Border.BorderBrushProperty, spinner.GetObservable(TemplatedControl.BorderBrushProperty));
            border.Bind(Border.BorderThicknessProperty, spinner.GetObservable(TemplatedControl.BorderThicknessProperty));
            border.Bind(Border.CornerRadiusProperty, spinner.GetObservable(TemplatedControl.CornerRadiusProperty));
            return border;
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
