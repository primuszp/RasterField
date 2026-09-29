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
    /// <summary>One curve of a profile chart: a layer (or interpolation) sampled along the path.</summary>
    public sealed class ProfileSeries
    {
        public ProfileSeries(string name, IReadOnlyList<ProfileSample> samples, Color color, bool dashed = false)
        {
            Name = name;
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));
            Color = color;
            Dashed = dashed;
        }

        public string Name { get; }
        public IReadOnlyList<ProfileSample> Samples { get; }
        public Color Color { get; }
        public bool Dashed { get; }
    }

    /// <summary>
    /// A larger, resizable view of a profile (every series on one chart) with an "Export CSV…" button.
    /// The same chart is also docked in the main window's Analysis panel.
    /// </summary>
    public sealed class ProfileWindow : Window
    {
        private readonly IReadOnlyList<ProfileSeries> _series;
        private readonly string _distanceUnit;

        public ProfileWindow(IReadOnlyList<ProfileSeries> series, string distanceUnit, string? valueUnit)
        {
            _series = series ?? throw new ArgumentNullException(nameof(series));
            _distanceUnit = distanceUnit;

            Title = L.T("Profile");
            Width = 760;
            Height = 460;
            MinWidth = 420;
            MinHeight = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var chart = new ProfileChartControl { Margin = new Thickness(12) };
            chart.SetSeries(series, distanceUnit, valueUnit);

            var exportBtn = new Button { Content = L.T("Export CSV…") };
            exportBtn.Click += async (_, _) => await ExportCsvAsync(this, _series, _distanceUnit);
            var closeBtn = new Button { Content = L.T("Close") };
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

        private static readonly FilePickerFileType[] CsvFileTypes =
            { new("CSV (*.csv)") { Patterns = new[] { "*.csv" } } };

        /// <summary>Writes every series side by side: distance, x, y, then one value column per series.</summary>
        internal static async Task ExportCsvAsync(TopLevel owner, IReadOnlyList<ProfileSeries> series, string distanceUnit)
        {
            if (series.Count == 0) return;
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Export profile as CSV"),
                DefaultExtension = "csv",
                SuggestedFileName = "profile.csv",
                FileTypeChoices = CsvFileTypes,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            using var writer = new StreamWriter(path!);
            string Quote(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            writer.WriteLine($"distance_{distanceUnit},x,y," + string.Join(",", series.Select(s => Quote(s.Name))));
            var first = series[0].Samples;
            for (int i = 0; i < first.Count; i++)
            {
                var cells = new List<string>
                {
                    first[i].Distance.ToString("g9", CultureInfo.InvariantCulture),
                    first[i].X.ToString("g9", CultureInfo.InvariantCulture),
                    first[i].Y.ToString("g9", CultureInfo.InvariantCulture),
                };
                foreach (var s in series)
                    cells.Add(i < s.Samples.Count && s.Samples[i].Value.HasValue ? s.Samples[i].Value!.Value.ToString("g9", CultureInfo.InvariantCulture) : "");
                writer.WriteLine(string.Join(",", cells));
            }
        }
    }

    /// <summary>Hand-drawn (no charting library) multi-series line chart: distance on X, value on Y, with a legend.</summary>
    internal sealed class ProfileChartControl : Control
    {
        private IReadOnlyList<ProfileSeries> _series = Array.Empty<ProfileSeries>();
        private string _distanceUnit = "";
        private string? _valueUnit;

        public ProfileChartControl() { ClipToBounds = true; }

        public IReadOnlyList<ProfileSeries> Series => _series;

        public void SetSeries(IReadOnlyList<ProfileSeries> series, string distanceUnit, string? valueUnit)
        {
            _series = series;
            _distanceUnit = distanceUnit;
            _valueUnit = valueUnit;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(AppTheme.BarBackground, bounds, 6);

            var all = _series.SelectMany(s => s.Samples).Where(s => s.Value.HasValue).ToList();
            if (all.Count < 2)
            {
                DrawCentredText(context, bounds, L.T("Draw a path on the map (click to add points, double-click to finish)."));
                return;
            }

            const double marginLeft = 56, marginRight = 12, marginTop = 12;
            double marginBottom = 34 + 14 * Math.Min(_series.Count, 4);
            double plotW = Math.Max(1, Bounds.Width - marginLeft - marginRight);
            double plotH = Math.Max(1, Bounds.Height - marginTop - marginBottom);
            if (plotW <= 1 || plotH <= 1) return;

            double minDist = _series.Min(s => s.Samples.Count > 0 ? s.Samples[0].Distance : 0);
            double maxDist = _series.Max(s => s.Samples.Count > 0 ? s.Samples[^1].Distance : 0);
            double minVal = all.Min(s => s.Value!.Value), maxVal = all.Max(s => s.Value!.Value);
            if (maxVal - minVal < 1e-9) { maxVal += 0.5; minVal -= 0.5; }
            if (maxDist - minDist < 1e-9) maxDist = minDist + 1;

            double Sx(double d) => marginLeft + (d - minDist) / (maxDist - minDist) * plotW;
            double Sy(double v) => marginTop + plotH - (v - minVal) / (maxVal - minVal) * plotH;

            var axisPen = new Pen(AppTheme.TextSecondary, 1);
            var gridPen = new Pen(AppTheme.Border, 1);

            for (int i = 0; i <= 4; i++)
            {
                double v = minVal + (maxVal - minVal) * i / 4.0;
                double y = Sy(v);
                context.DrawLine(gridPen, new Point(marginLeft, y), new Point(marginLeft + plotW, y));
                DrawText(context, v.ToString("g4", CultureInfo.InvariantCulture), new Point(4, y - 7), AppTheme.TextSecondary, 10);
            }
            foreach (double d in new[] { minDist, (minDist + maxDist) / 2, maxDist })
            {
                double x = Sx(d);
                var t = FormatText(d.ToString("0.##", CultureInfo.InvariantCulture), 10, AppTheme.TextSecondary);
                context.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, marginLeft, marginLeft + plotW - t.Width), marginTop + plotH + 4));
            }
            context.DrawLine(axisPen, new Point(marginLeft, marginTop), new Point(marginLeft, marginTop + plotH));
            context.DrawLine(axisPen, new Point(marginLeft, marginTop + plotH), new Point(marginLeft + plotW, marginTop + plotH));

            // Each series: one polyline, gaps (no-data runs) break the stroke.
            foreach (var series in _series)
            {
                var geometry = new StreamGeometry();
                using (var gc = geometry.Open())
                {
                    bool open = false;
                    foreach (var s in series.Samples)
                    {
                        if (!s.Value.HasValue) { open = false; continue; }
                        var pt = new Point(Sx(s.Distance), Sy(s.Value.Value));
                        if (!open) { gc.BeginFigure(pt, isFilled: false); open = true; }
                        else gc.LineTo(pt);
                    }
                }
                var pen = new Pen(new SolidColorBrush(series.Color), series.Dashed ? 1.5 : 2,
                    series.Dashed ? new DashStyle(new double[] { 4, 3 }, 0) : null);
                context.DrawGeometry(null, pen, geometry);
            }

            string axisLabel = L.F("distance ({0})", _distanceUnit) + (_valueUnit != null ? "   ·   " + L.F("value ({0})", _valueUnit) : "");
            DrawText(context, axisLabel, new Point(marginLeft, marginTop + plotH + 18), AppTheme.TextSecondary, 10);

            double ly = marginTop + plotH + 32;
            foreach (var series in _series.Take(4))
            {
                context.DrawLine(new Pen(new SolidColorBrush(series.Color), 3), new Point(marginLeft, ly + 6), new Point(marginLeft + 18, ly + 6));
                DrawText(context, series.Name, new Point(marginLeft + 24, ly - 1), AppTheme.TextSecondary, 10);
                ly += 14;
            }
        }

        private static void DrawCentredText(DrawingContext context, Rect bounds, string message)
        {
            var text = FormatText(message, 12, AppTheme.TextSecondary);
            text.MaxTextWidth = Math.Max(50, bounds.Width - 20);
            context.DrawText(text, new Point(Math.Max(10, (bounds.Width - text.Width) / 2), (bounds.Height - text.Height) / 2));
        }

        private static void DrawText(DrawingContext context, string text, Point at, IBrush brush, double size) =>
            context.DrawText(FormatText(text, size, brush), at);

        private static FormattedText FormatText(string text, double size, IBrush brush) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
    }
}
