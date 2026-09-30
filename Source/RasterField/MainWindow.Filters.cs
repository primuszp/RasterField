using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RasterField.Rasters;
using RasterField.Rendering;
using static RasterField.L;

namespace RasterField
{
    /// <summary>
    /// Raster ▸ Filter (convolution): sliding-window filters — emboss, smoothing, sharpening,
    /// edge detection, local relief and a user-typed matrix — each producing a derived layer
    /// with a recipe, so it can be recomputed with other parameters and is kept in projects.
    /// </summary>
    public sealed partial class MainWindow
    {
        private enum FilterKind { Emboss, Gaussian, Mean, Sharpen, Sobel, Laplace, LocalRelief, Custom }

        private static readonly FilterKind[] FilterKinds = Enum.GetValues<FilterKind>();

        /// <summary>Compass light directions for the emboss filter (azimuth, English key).</summary>
        private static readonly (double Azimuth, string Name)[] FilterDirections =
        {
            (0, "N"), (45, "NE"), (90, "E"), (135, "SE"), (180, "S"), (225, "SW"), (270, "W"), (315, "NW"),
        };

        private static readonly int[] FilterSizes = { 3, 5, 7 };

        /// <summary>One filter's parameters — kept in a layer recipe as plain strings.</summary>
        private sealed record FilterSpec(FilterKind Kind, int Size, double Azimuth, double Radius,
            string KernelText, double Divisor, double Offset, bool Strict)
        {
            public static FilterSpec Default { get; } = new(FilterKind.Emboss, 3, 315, 10, "", 1, 0, false);

            private ConvolutionNoData NoData => Strict ? ConvolutionNoData.Propagate : ConvolutionNoData.FillFromCentre;

            /// <summary>Smoothing / sharpening / a normalised custom matrix keep the data's values and unit.</summary>
            public bool KeepsValues => Kind switch
            {
                FilterKind.Gaussian or FilterKind.Mean or FilterKind.Sharpen => true,
                FilterKind.Custom => TryKernel() is { } k && Math.Abs(k.Sum / k.Divisor - 1) < 1e-6 && Offset == 0,
                _ => false,
            };

            public ConvolutionKernel? TryKernel()
            {
                try
                {
                    return Kind switch
                    {
                        FilterKind.Emboss => ConvolutionKernel.Emboss(Azimuth, Size),
                        FilterKind.Mean => ConvolutionKernel.Mean(Size),
                        FilterKind.Sharpen => ConvolutionKernel.Sharpen(Size),
                        FilterKind.Laplace => ConvolutionKernel.Laplace(diagonals: true),
                        FilterKind.Custom => ConvolutionKernel.Parse(KernelText, Divisor, Offset),
                        _ => null,
                    };
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
            }

            public Raster Apply(Raster source, CancellationToken cancellationToken = default) => Kind switch
            {
                FilterKind.Gaussian => ConvolutionFilter.Gaussian(source, Radius / 3.0, cancellationToken),
                FilterKind.Sobel => ConvolutionFilter.SobelMagnitude(source, NoData, cancellationToken),
                FilterKind.LocalRelief => ConvolutionFilter.LocalRelief(source, Radius, cancellationToken),
                FilterKind.Custom => ConvolutionFilter.Convolve(source, ConvolutionKernel.Parse(KernelText, Divisor, Offset), NoData, cancellationToken),
                _ => ConvolutionFilter.Convolve(source, TryKernel()!, NoData, cancellationToken),
            };

            public string Label() => Kind switch
            {
                FilterKind.Emboss => L.F("Emboss {0} {1}×{1}", T(DirectionName(Azimuth)), Size),
                FilterKind.Gaussian => L.F("Gaussian smoothing r {0:g4}", Radius),
                FilterKind.Mean => L.F("Mean smoothing {0}×{0}", Size),
                FilterKind.Sharpen => L.F("Sharpen {0}×{0}", Size),
                FilterKind.Sobel => T("Sobel edges"),
                FilterKind.Laplace => T("Laplace"),
                FilterKind.LocalRelief => L.F("Local relief r {0:g4}", Radius),
                _ => L.F("Custom matrix {0}×{0}", TryKernel()?.Size ?? 0),
            };

            public Dictionary<string, string> ToParameters() => new()
            {
                ["kind"] = Kind.ToString(),
                ["size"] = Size.ToString(CultureInfo.InvariantCulture),
                ["azimuth"] = LayerRecipe.Num(Azimuth),
                ["radius"] = LayerRecipe.Num(Radius),
                ["kernel"] = KernelText,
                ["divisor"] = LayerRecipe.Num(Divisor),
                ["offset"] = LayerRecipe.Num(Offset),
                ["strict"] = Strict.ToString(),
            };

            public static FilterSpec FromRecipe(LayerRecipe recipe) => new(
                Enum.TryParse<FilterKind>(recipe.Get("kind", "Emboss"), out var kind) ? kind : FilterKind.Emboss,
                recipe.Get("size", 3), recipe.Get("azimuth", 315.0), recipe.Get("radius", 10.0),
                recipe.Get("kernel", ""), recipe.Get("divisor", 1.0), recipe.Get("offset", 0.0), recipe.Get("strict", false));
        }

        private static string DirectionName(double azimuth)
        {
            foreach (var (a, name) in FilterDirections)
                if (Math.Abs(a - azimuth) < 1e-6) return name;
            return azimuth.ToString("0.#", CultureInfo.InvariantCulture) + "°";
        }

        private static string FilterKindName(FilterKind kind) => kind switch
        {
            FilterKind.Emboss => T("Emboss (relief effect)"),
            FilterKind.Gaussian => T("Smoothing — Gaussian"),
            FilterKind.Mean => T("Smoothing — mean"),
            FilterKind.Sharpen => T("Sharpen"),
            FilterKind.Sobel => T("Edges — Sobel magnitude"),
            FilterKind.Laplace => T("Edges — Laplace"),
            FilterKind.LocalRelief => T("Local relief (high-pass)"),
            _ => T("Custom matrix"),
        };

        private static string FilterKindHelp(FilterKind kind) => kind switch
        {
            FilterKind.Emboss => T("Directional difference towards the light: slopes facing the light turn bright, the others dark — a quick relief effect on any raster (DEM, orthophoto, single band). Result: rise per cell."),
            FilterKind.Gaussian => T("Weighted average of the neighbourhood (σ = radius / 3): removes noise, keeps the values and unit. Gaps are left out, not smeared."),
            FilterKind.Mean => T("Plain average of the window: stronger, blockier smoothing than Gaussian."),
            FilterKind.Sharpen => T("Adds the difference from the local mean back to each cell: crisper edges, more noise."),
            FilterKind.Sobel => T("Gradient strength (rise per cell) from the Sobel operator: bright on edges, banks and break lines, 0 on flat or evenly sloping ground."),
            FilterKind.Laplace => T("Second derivative: positive on convex spots (ridges, bank tops), negative in concave ones (ditches, pits), 0 on planes."),
            FilterKind.LocalRelief => T("The surface minus its smoothed trend over the radius: removes the regional slope and keeps smaller features (ditches, banks, field boundaries, old channels). The most telling view of flat LiDAR terrain."),
            _ => T("Type an odd, square matrix: one row per line (or separated by ;), values by spaces; decimal comma allowed. Output = Σ weight·value / divisor + offset."),
        };

        private async Task FilterAsync()
        {
            var loaded = await TryGetLoadedRasterAsync(T("Filter"));
            if (loaded == null) return;
            var recipe = await ShowFilterDialogAsync(_view.ActiveLayer!, null);
            if (recipe != null) await CreateDerivedAsync(recipe, T("Filtering…"));
        }

        /// <summary>
        /// The derived layer's display for a filter result: the source's own palette and stretch
        /// when the values keep their meaning (smoothing, sharpening), otherwise grey — symmetric
        /// around 0 for signed results (emboss, Laplace, local relief), 0 upwards for Sobel.
        /// </summary>
        private static (Palette Palette, double Min, double Max)? FilterDisplay(FilterSpec spec, Raster result)
        {
            if (spec.KeepsValues || result.Statistics.ValidCount == 0) return null;
            var histogram = RasterHistogram.Build(result);
            if (spec.Kind == FilterKind.Sobel)
                return (BuiltInPalettes.Grayscale, 0, Math.Max(histogram.Percentile(99), 1e-6));
            var (low, high) = histogram.PercentileRange(2, 98);
            double m = Math.Max(Math.Abs(low), Math.Abs(high));
            if (!(m > 0)) m = 1;
            return (BuiltInPalettes.Grayscale, -m, m);
        }

        private async Task<LayerRecipe?> ShowFilterDialogAsync(RasterLayer layer, LayerRecipe? initial)
        {
            var tcs = new TaskCompletionSource<LayerRecipe?>();
            var seed = initial != null ? FilterSpec.FromRecipe(initial) : FilterSpec.Default;

            var kindBox = new ComboBox { ItemsSource = Array.ConvertAll(FilterKinds, FilterKindName), SelectedIndex = Array.IndexOf(FilterKinds, seed.Kind), HorizontalAlignment = HorizontalAlignment.Stretch };
            var helpText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.FontCaption, Foreground = AppTheme.TextSecondary, MinHeight = 44 };
            var sizeBox = new ComboBox { ItemsSource = Array.ConvertAll(FilterSizes, s => $"{s}×{s}"), SelectedIndex = Math.Max(0, Array.IndexOf(FilterSizes, seed.Size)), Width = 90 };
            int dirIndex = Array.FindIndex(FilterDirections, d => Math.Abs(d.Azimuth - seed.Azimuth) < 1e-6);
            var directionBox = new ComboBox { ItemsSource = Array.ConvertAll(FilterDirections, d => T(d.Name)), SelectedIndex = dirIndex < 0 ? 7 : dirIndex, Width = 90 };
            var radiusBox = new NumericUpDown { Minimum = 1, Maximum = 200, Value = (decimal)seed.Radius, Increment = 1, FormatString = "0", Width = 110 };
            var kernelBox = new TextBox
            {
                Text = seed.KernelText.Replace("; ", "\n", StringComparison.Ordinal), AcceptsReturn = true, Height = 110,
                FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.NoWrap,
            };
            var divisorBox = new NumericUpDown { Value = (decimal)seed.Divisor, Increment = 1, FormatString = "0.####", Width = 130 };
            var offsetBox = new NumericUpDown { Value = (decimal)seed.Offset, Increment = 1, FormatString = "0.####", Width = 130 };
            var strictBox = new CheckBox { Content = T("No-data wherever the window touches a gap (otherwise the centre value fills it)"), IsChecked = seed.Strict };
            var errorText = new TextBlock { Foreground = AppTheme.Danger, FontSize = AppTheme.FontCaption, TextWrapping = TextWrapping.Wrap };
            var okBtn = new Button { Content = T("Apply → new layer"), MinWidth = 80 };
            var cancelBtn = new Button { Content = T("Cancel"), MinWidth = 80 };

            StackPanel Captioned(string caption, Control c) => new StackPanel { Spacing = 2, Children = { new TextBlock { Text = caption, Opacity = 0.8, FontSize = 11 }, c } };
            var sizeRow = Captioned(T("Window"), sizeBox);
            var directionRow = Captioned(T("Light from"), directionBox);
            var radiusRow = Captioned(T("Radius (cells)"), radiusBox);
            var customPanel = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Captioned(T("Matrix"), kernelBox),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Captioned(T("Divisor"), divisorBox), Captioned(T("Offset"), offsetBox) } },
                },
            };

            // Preview: a patch at the centre of the view, at full resolution (the filter's true scale).
            var originalImage = new Image { Width = 210, Height = 210, Stretch = Stretch.Uniform };
            var filteredImage = new Image { Width = 210, Height = 210, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(originalImage, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            RenderOptions.SetBitmapInterpolationMode(filteredImage, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            Raster? patch = null;
            if (layer.Raster != null && layer.Colorizer != null && !layer.ShowRgbComposite)
            {
                int w = layer.DatasetWidth, h = layer.DatasetHeight;
                var view = _view.VisibleCellWindow() ?? new PixelRect(0, 0, w, h);
                int pw = Math.Min(160, w), ph = Math.Min(160, h);
                int px = Math.Clamp(view.X + view.Width / 2 - pw / 2, 0, Math.Max(0, w - pw));
                int py = Math.Clamp(view.Y + view.Height / 2 - ph / 2, 0, Math.Max(0, h - ph));
                patch = RasterClipper.Crop(layer.Raster, px, py, pw, ph);
                originalImage.Source = RasterView.ToBitmap(RasterImageRenderer.Render(patch, layer.Colorizer));
            }

            FilterSpec Current()
            {
                var kind = FilterKinds[Math.Max(0, kindBox.SelectedIndex)];
                return new FilterSpec(kind, FilterSizes[Math.Max(0, sizeBox.SelectedIndex)],
                    FilterDirections[Math.Max(0, directionBox.SelectedIndex)].Azimuth, (double)(radiusBox.Value ?? 10),
                    string.Join("; ", (kernelBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    (double)(divisorBox.Value ?? 1), (double)(offsetBox.Value ?? 0), strictBox.IsChecked == true);
            }

            FilterKind previousKind = seed.Kind;
            void Update()
            {
                var spec = Current();
                // Switching to "Custom" starts from the matrix of the filter shown before, ready to edit.
                if (spec.Kind == FilterKind.Custom && previousKind != FilterKind.Custom && string.IsNullOrWhiteSpace(kernelBox.Text)
                    && (spec with { Kind = previousKind }).TryKernel() is { } template)
                {
                    kernelBox.Text = template.Format("\n");
                    divisorBox.Value = (decimal)template.Divisor;
                    offsetBox.Value = (decimal)template.Offset;
                    spec = Current();
                }
                previousKind = spec.Kind;

                helpText.Text = FilterKindHelp(spec.Kind);
                sizeRow.IsVisible = spec.Kind is FilterKind.Emboss or FilterKind.Mean or FilterKind.Sharpen;
                directionRow.IsVisible = spec.Kind == FilterKind.Emboss;
                radiusRow.IsVisible = spec.Kind is FilterKind.Gaussian or FilterKind.LocalRelief;
                customPanel.IsVisible = spec.Kind == FilterKind.Custom;
                strictBox.IsVisible = spec.Kind is not (FilterKind.Gaussian or FilterKind.LocalRelief);

                string? error = null;
                if (spec.Kind == FilterKind.Custom)
                {
                    try { ConvolutionKernel.Parse(spec.KernelText, spec.Divisor, spec.Offset); }
                    catch (Exception ex) when (ex is FormatException or ArgumentException) { error = ex.Message; }
                }
                errorText.Text = error ?? "";
                okBtn.IsEnabled = error == null;

                if (patch != null && error == null)
                {
                    var result = spec.Apply(patch);
                    var display = FilterDisplay(spec, result);
                    var colorizer = display is { } d
                        ? new RasterColorizer(d.Palette, d.Min, d.Max) { NoDataColor = ColorRgba.Transparent }
                        : layer.Colorizer!;
                    filteredImage.Source = RasterView.ToBitmap(RasterImageRenderer.Render(result, colorizer));
                }
            }

            kindBox.SelectionChanged += (_, _) => Update();
            sizeBox.SelectionChanged += (_, _) => Update();
            directionBox.SelectionChanged += (_, _) => Update();
            radiusBox.ValueChanged += (_, _) => Update();
            kernelBox.TextChanged += (_, _) => Update();
            divisorBox.ValueChanged += (_, _) => Update();
            offsetBox.ValueChanged += (_, _) => Update();
            strictBox.IsCheckedChanged += (_, _) => Update();

            var dialog = new Window
            {
                Title = T("Filter (convolution)") + " — " + layer.Name,
                Width = 500,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            okBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(new LayerRecipe("filter", layer, Current().ToParameters()));
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            var previews = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children = { Captioned(T("Original"), originalImage), Captioned(T("Filtered"), filteredImage) },
            };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    kindBox,
                    helpText,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { sizeRow, directionRow, radiusRow } },
                    customPanel,
                    strictBox,
                    errorText,
                    patch != null ? previews : new TextBlock { Text = T("(no preview for a streaming or RGB layer)"), Opacity = 0.7 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { okBtn, cancelBtn } },
                },
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            Update();
            await dialog.ShowDialog(this);
            return await tcs.Task;
        }
    }
}
