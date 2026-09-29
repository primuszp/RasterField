using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

        public ProfileWindow(IReadOnlyList<ProfileSeries> series, string distanceUnit, string? valueUnit,
            Action<ProfileSample?>? hoverChanged = null)
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
            if (hoverChanged != null)
            {
                chart.HoverChanged += (_, e) => hoverChanged(e.Sample);
                Closed += (_, _) => hoverChanged(null);
            }

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

    /// <summary>The profile sample currently selected by chart pointer interaction.</summary>
    internal sealed class ProfileHoverEventArgs : EventArgs
    {
        public ProfileHoverEventArgs(ProfileSample? sample) => Sample = sample;

        public ProfileSample? Sample { get; }
    }

    /// <summary>Hand-drawn (no charting library) multi-series line chart: distance on X, value on Y, with a legend.</summary>
    internal sealed class ProfileChartControl : Control
    {
        private const double MarginLeft = 56;
        private const double MarginRight = 12;
        private const double MarginTop = 12;
        private IReadOnlyList<ProfileSeries> _series = Array.Empty<ProfileSeries>();
        private string _distanceUnit = "";
        private string? _valueUnit;
        private bool _hasPlot;
        private double _minDistance;
        private double _maxDistance;
        private double _minValue;
        private double _maxValue;
        private int _hoverIndex = -1;
        private ProfileSeries? _hoverSeries;

        public ProfileChartControl()
        {
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Cross);
        }

        public IReadOnlyList<ProfileSeries> Series => _series;

        public event EventHandler<ProfileHoverEventArgs>? HoverChanged;

        public void SetSeries(IReadOnlyList<ProfileSeries> series, string distanceUnit, string? valueUnit)
        {
            _series = series;
            _distanceUnit = distanceUnit;
            _valueUnit = valueUnit;
            RecalculateExtents();
            ClearHover();
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(AppTheme.BarBackground, bounds, 6);

            if (!_hasPlot)
            {
                DrawCentredText(context, bounds, L.T("Draw a path on the map (click to add points, double-click to finish)."));
                return;
            }

            double marginBottom = 34 + 14 * Math.Min(_series.Count, 4);
            double plotW = Math.Max(1, Bounds.Width - MarginLeft - MarginRight);
            double plotH = Math.Max(1, Bounds.Height - MarginTop - marginBottom);
            if (plotW <= 1 || plotH <= 1) return;

            double Sx(double d) => MarginLeft + (d - _minDistance) / (_maxDistance - _minDistance) * plotW;
            double Sy(double v) => MarginTop + plotH - (v - _minValue) / (_maxValue - _minValue) * plotH;

            var axisPen = new Pen(AppTheme.TextSecondary, 1);
            var gridPen = new Pen(AppTheme.Border, 1);

            for (int i = 0; i <= 4; i++)
            {
                double v = _minValue + (_maxValue - _minValue) * i / 4.0;
                double y = Sy(v);
                context.DrawLine(gridPen, new Point(MarginLeft, y), new Point(MarginLeft + plotW, y));
                DrawText(context, v.ToString("g4", CultureInfo.InvariantCulture), new Point(4, y - 7), AppTheme.TextSecondary, 10);
            }
            foreach (double d in new[] { _minDistance, (_minDistance + _maxDistance) / 2, _maxDistance })
            {
                double x = Sx(d);
                var t = FormatText(d.ToString("0.##", CultureInfo.InvariantCulture), 10, AppTheme.TextSecondary);
                context.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, MarginLeft, MarginLeft + plotW - t.Width), MarginTop + plotH + 4));
            }
            context.DrawLine(axisPen, new Point(MarginLeft, MarginTop), new Point(MarginLeft, MarginTop + plotH));
            context.DrawLine(axisPen, new Point(MarginLeft, MarginTop + plotH), new Point(MarginLeft + plotW, MarginTop + plotH));

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

            DrawHover(context, Sx, Sy, plotW, plotH);

            string axisLabel = L.F("distance ({0})", _distanceUnit) + (_valueUnit != null ? "   ·   " + L.F("value ({0})", _valueUnit) : "");
            DrawText(context, axisLabel, new Point(MarginLeft, MarginTop + plotH + 18), AppTheme.TextSecondary, 10);

            double ly = MarginTop + plotH + 32;
            foreach (var series in _series.Take(4))
            {
                context.DrawLine(new Pen(new SolidColorBrush(series.Color), 3), new Point(MarginLeft, ly + 6), new Point(MarginLeft + 18, ly + 6));
                DrawText(context, series.Name, new Point(MarginLeft + 24, ly - 1), AppTheme.TextSecondary, 10);
                ly += 14;
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_hasPlot || !TryGetPlotSize(out double plotWidth, out double plotHeight))
            {
                ClearHover();
                return;
            }

            Point pointer = e.GetPosition(this);
            if (pointer.X < MarginLeft || pointer.X > MarginLeft + plotWidth ||
                pointer.Y < MarginTop || pointer.Y > MarginTop + plotHeight)
            {
                ClearHover();
                return;
            }

            ProfileSeries? reference = _series.FirstOrDefault(s => s.Samples.Count > 0);
            if (reference == null) { ClearHover(); return; }
            double distance = _minDistance + (pointer.X - MarginLeft) / plotWidth * (_maxDistance - _minDistance);
            int index = NearestSampleIndex(reference.Samples, distance);
            if (index == _hoverIndex && ReferenceEquals(reference, _hoverSeries)) return;
            _hoverIndex = index;
            _hoverSeries = reference;
            InvalidateVisual();
            HoverChanged?.Invoke(this, new ProfileHoverEventArgs(index >= 0 ? reference.Samples[index] : (ProfileSample?)null));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            ClearHover();
        }

        internal static int NearestSampleIndex(IReadOnlyList<ProfileSample> samples, double distance)
        {
            ArgumentNullException.ThrowIfNull(samples);
            if (samples.Count == 0) return -1;
            int low = 0, high = samples.Count - 1;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (samples[mid].Distance < distance) low = mid + 1;
                else high = mid;
            }
            if (low == 0) return 0;
            double before = Math.Abs(samples[low - 1].Distance - distance);
            double after = Math.Abs(samples[low].Distance - distance);
            return before <= after ? low - 1 : low;
        }

        private void RecalculateExtents()
        {
            _hasPlot = false;
            _minDistance = double.PositiveInfinity;
            _maxDistance = double.NegativeInfinity;
            _minValue = double.PositiveInfinity;
            _maxValue = double.NegativeInfinity;
            int validValues = 0;
            foreach (ProfileSeries series in _series)
            {
                foreach (ProfileSample sample in series.Samples)
                {
                    if (sample.Distance < _minDistance) _minDistance = sample.Distance;
                    if (sample.Distance > _maxDistance) _maxDistance = sample.Distance;
                    if (!sample.Value.HasValue) continue;
                    validValues++;
                    if (sample.Value.Value < _minValue) _minValue = sample.Value.Value;
                    if (sample.Value.Value > _maxValue) _maxValue = sample.Value.Value;
                }
            }
            if (validValues < 2 || double.IsInfinity(_minDistance) || double.IsInfinity(_maxDistance)) return;
            if (_maxValue - _minValue < 1e-9) { _maxValue += 0.5; _minValue -= 0.5; }
            if (_maxDistance - _minDistance < 1e-9) _maxDistance = _minDistance + 1;
            _hasPlot = true;
        }

        private bool TryGetPlotSize(out double width, out double height)
        {
            double marginBottom = 34 + 14 * Math.Min(_series.Count, 4);
            width = Math.Max(1, Bounds.Width - MarginLeft - MarginRight);
            height = Math.Max(1, Bounds.Height - MarginTop - marginBottom);
            return width > 1 && height > 1;
        }

        private void ClearHover()
        {
            if (_hoverIndex < 0 && _hoverSeries == null) return;
            _hoverIndex = -1;
            _hoverSeries = null;
            InvalidateVisual();
            HoverChanged?.Invoke(this, new ProfileHoverEventArgs(null));
        }

        private void DrawHover(DrawingContext context, Func<double, double> sx, Func<double, double> sy,
            double plotWidth, double plotHeight)
        {
            if (_hoverSeries == null || _hoverIndex < 0 || _hoverIndex >= _hoverSeries.Samples.Count) return;
            ProfileSample selected = _hoverSeries.Samples[_hoverIndex];
            double x = sx(selected.Distance);
            context.DrawLine(new Pen(AppTheme.Accent, 1, new DashStyle(new double[] { 3, 3 }, 0)),
                new Point(x, MarginTop), new Point(x, MarginTop + plotHeight));

            var lines = new List<string>
            {
                $"{selected.Distance:0.##} {_distanceUnit}",
            };
            foreach (ProfileSeries series in _series.Take(4))
            {
                int index = NearestSampleIndex(series.Samples, selected.Distance);
                if (index < 0) continue;
                ProfileSample sample = series.Samples[index];
                string value = sample.Value.HasValue
                    ? sample.Value.Value.ToString("g6", CultureInfo.InvariantCulture) +
                      (string.IsNullOrWhiteSpace(_valueUnit) ? "" : " " + _valueUnit)
                    : "—";
                lines.Add(series.Name + ": " + value);
                if (sample.Value.HasValue)
                {
                    Point point = new Point(sx(sample.Distance), sy(sample.Value.Value));
                    context.DrawEllipse(AppTheme.BarBackground, new Pen(new SolidColorBrush(series.Color), 2), point, 4, 4);
                }
            }

            var text = FormatText(string.Join("\n", lines), 10.5, AppTheme.TextPrimary);
            const double padding = 6;
            double tooltipX = x + 10;
            if (tooltipX + text.Width + padding * 2 > MarginLeft + plotWidth)
                tooltipX = x - text.Width - padding * 2 - 10;
            tooltipX = Math.Max(MarginLeft + 2, tooltipX);
            double tooltipY = MarginTop + 6;
            var box = new Rect(tooltipX, tooltipY, text.Width + padding * 2, text.Height + padding * 2);
            context.FillRectangle(AppTheme.PanelBackground, box, 4);
            context.DrawRectangle(null, new Pen(AppTheme.Accent, 1), box, 4, 4);
            context.DrawText(text, new Point(tooltipX + padding, tooltipY + padding));
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
