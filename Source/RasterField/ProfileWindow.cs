using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RasterField.Rasters;

namespace RasterField
{
    /// <summary>
    /// Shows a cross-section / elevation profile sampled along a line drawn on the raster:
    /// a hand-drawn line chart (distance along the line vs. sampled value) with an "Export CSV…" button.
    /// </summary>
    public sealed class ProfileWindow : Window
    {
        private static readonly FilePickerFileType[] CsvFileTypes =
            { new("CSV (*.csv)") { Patterns = new[] { "*.csv" } } };

        private readonly IReadOnlyList<ProfileSample> _samples;
        private readonly string _distanceUnit;
        private readonly string? _valueUnit;

        public ProfileWindow(IReadOnlyList<ProfileSample> samples, string distanceUnit, string? valueUnit)
        {
            _samples = samples ?? throw new ArgumentNullException(nameof(samples));
            _distanceUnit = distanceUnit;
            _valueUnit = valueUnit;

            Title = "Profile";
            Width = 680;
            Height = 440;
            MinWidth = 420;
            MinHeight = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var chart = new ProfileChartControl(samples, distanceUnit, valueUnit) { Margin = new Thickness(12) };

            var exportBtn = new Button { Content = "Export CSV…" };
            exportBtn.Click += async (_, _) => await ExportCsvAsync();
            var closeBtn = new Button { Content = "Close" };
            closeBtn.Click += (_, _) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 12, 12),
            };
            buttons.Children.Add(exportBtn);
            buttons.Children.Add(closeBtn);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(chart);
            Content = root;
        }

        private async Task ExportCsvAsync()
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export profile as CSV",
                DefaultExtension = "csv",
                SuggestedFileName = "profile.csv",
                FileTypeChoices = CsvFileTypes,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            using var writer = new StreamWriter(path!);
            writer.WriteLine($"distance_{_distanceUnit},x,y,value{(_valueUnit != null ? "_" + _valueUnit : "")}");
            foreach (var s in _samples)
            {
                writer.WriteLine(string.Join(",",
                    s.Distance.ToString("g9", CultureInfo.InvariantCulture),
                    s.X.ToString("g9", CultureInfo.InvariantCulture),
                    s.Y.ToString("g9", CultureInfo.InvariantCulture),
                    s.Value.HasValue ? s.Value.Value.ToString("g9", CultureInfo.InvariantCulture) : ""));
            }
        }
    }

    /// <summary>Hand-drawn (no charting library) line chart of a profile: distance on X, sampled value on Y.</summary>
    internal sealed class ProfileChartControl : Control
    {
        private readonly IReadOnlyList<ProfileSample> _samples;
        private readonly string _distanceUnit;
        private readonly string? _valueUnit;

        public ProfileChartControl(IReadOnlyList<ProfileSample> samples, string distanceUnit, string? valueUnit)
        {
            _samples = samples;
            _distanceUnit = distanceUnit;
            _valueUnit = valueUnit;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)), bounds);

            var valid = _samples.Where(s => s.Value.HasValue).ToList();
            if (valid.Count < 2)
            {
                DrawCentredText(context, bounds, "Not enough valid samples along this line to chart.");
                return;
            }

            const double marginLeft = 60, marginRight = 16, marginTop = 16, marginBottom = 40;
            double plotW = Math.Max(1, Bounds.Width - marginLeft - marginRight);
            double plotH = Math.Max(1, Bounds.Height - marginTop - marginBottom);
            if (plotW <= 1 || plotH <= 1) return;

            double minDist = _samples[0].Distance, maxDist = _samples[^1].Distance;
            double minVal = valid.Min(s => s.Value!.Value), maxVal = valid.Max(s => s.Value!.Value);
            if (maxVal - minVal < 1e-9) { maxVal += 0.5; minVal -= 0.5; }
            if (maxDist - minDist < 1e-9) maxDist = minDist + 1;

            double Sx(double d) => marginLeft + (d - minDist) / (maxDist - minDist) * plotW;
            double Sy(double v) => marginTop + plotH - (v - minVal) / (maxVal - minVal) * plotH;

            var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 200, 200, 200)), 1);
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 200, 200, 200)), 1);

            // Horizontal gridlines + Y labels (5 bands).
            for (int i = 0; i <= 4; i++)
            {
                double v = minVal + (maxVal - minVal) * i / 4.0;
                double y = Sy(v);
                context.DrawLine(gridPen, new Point(marginLeft, y), new Point(marginLeft + plotW, y));
                DrawText(context, v.ToString("g4", CultureInfo.InvariantCulture), new Point(4, y - 7), Brushes.Gainsboro, 10);
            }

            // X-axis distance labels (start / mid / end).
            foreach (double d in new[] { minDist, (minDist + maxDist) / 2, maxDist })
            {
                double x = Sx(d);
                var t = FormatText(d.ToString("0.##", CultureInfo.InvariantCulture), 10, Brushes.Gainsboro);
                context.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, marginLeft, marginLeft + plotW - t.Width), marginTop + plotH + 6));
            }

            context.DrawLine(axisPen, new Point(marginLeft, marginTop), new Point(marginLeft, marginTop + plotH));
            context.DrawLine(axisPen, new Point(marginLeft, marginTop + plotH), new Point(marginLeft + plotW, marginTop + plotH));

            // The profile itself — one polyline, with gaps (no-data runs) breaking the stroke.
            var geometry = new StreamGeometry();
            using (var gc = geometry.Open())
            {
                bool open = false;
                foreach (var s in _samples)
                {
                    if (!s.Value.HasValue) { open = false; continue; }
                    var pt = new Point(Sx(s.Distance), Sy(s.Value.Value));
                    if (!open) { gc.BeginFigure(pt, isFilled: false); open = true; }
                    else gc.LineTo(pt);
                }
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(255, 80, 200, 255)), 2), geometry);

            string axisLabel = $"distance ({_distanceUnit})" + (_valueUnit != null ? $"   ·   value ({_valueUnit})" : "   ·   value");
            DrawText(context, axisLabel, new Point(marginLeft, Bounds.Height - 16), Brushes.Gray, 10);
        }

        private static void DrawCentredText(DrawingContext context, Rect bounds, string message)
        {
            var text = FormatText(message, 13, Brushes.Gainsboro);
            context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
        }

        private static void DrawText(DrawingContext context, string text, Point at, IBrush brush, double size) =>
            context.DrawText(FormatText(text, size, brush), at);

        private static FormattedText FormatText(string text, double size, IBrush brush) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
    }
}
