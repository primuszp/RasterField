using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RasterField.Rendering;

namespace RasterField
{
    /// <summary>
    /// A vertical palette legend: a rounded colour ramp (high value at the top) with the exact
    /// minimum and maximum at its ends and round "nice" values between them, plus the unit as
    /// a small axis title.
    /// </summary>
    public sealed class LegendControl : Control
    {
        private RasterColorizer? _colorizer;
        private string? _unit;

        private static readonly Typeface Regular = new(FontFamily.Default);
        private static readonly Typeface Strong = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        /// <summary>Sets the colorizer whose range and palette the legend visualises.</summary>
        public void SetColorizer(RasterColorizer? colorizer, string? unit = null)
        {
            _colorizer = colorizer;
            _unit = unit;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (_colorizer == null) return;
            double min = _colorizer.Minimum, max = _colorizer.Maximum;

            const double barWidth = 14, left = 2;
            double top = 8, bottom = Bounds.Height - 8;
            if (!string.IsNullOrEmpty(_unit))
            {
                var unit = Text(_unit!, AppTheme.FontCaption - 0.5, Regular, AppTheme.TextSecondary);
                context.DrawText(unit, new Point(left, 0));
                top = unit.Height + 6;
            }
            var bar = new Rect(left, top, barWidth, Math.Max(10, bottom - top));
            var shape = new RoundedRect(bar, 4);

            // The ramp, one row per device pixel, clipped to the rounded bar.
            using (context.PushClip(shape))
            {
                int steps = Math.Max(2, (int)Math.Ceiling(bar.Height));
                double row = bar.Height / steps;
                for (int i = 0; i < steps; i++)
                {
                    double t = 1.0 - (i + 0.5) / steps;
                    var c = _colorizer.Map(min + (max - min) * t);
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)),
                        new Rect(bar.Left, bar.Top + i * row, bar.Width, row + 0.6));
                }
            }
            var hairline = new Pen(AppTheme.IsDark ? new SolidColorBrush(Colors.White, 0.12) : new SolidColorBrush(Colors.Black, 0.14), 1);
            context.DrawRectangle(null, hairline, shape.Deflate(0.5, 0.5));

            if (!(max > min) || double.IsNaN(min) || double.IsNaN(max))
            {
                Label(context, bar, bar.Center.Y, Format(min, 0), Strong, AppTheme.TextPrimary);
                return;
            }

            // Ends: the exact range, emphasised. Between: round values, skipped where they would collide.
            double step = NiceStep((max - min) / Math.Max(2, Math.Floor(bar.Height / 30)));
            int decimals = 0;
            while (decimals < 6 && Math.Abs(step * Math.Pow(10, decimals) - Math.Round(step * Math.Pow(10, decimals))) > 1e-6) decimals++;
            double Y(double v) => bar.Bottom - (v - min) / (max - min) * bar.Height;

            var tickPen = new Pen(AppTheme.TextSecondary, 1);
            double labelHeight = AppTheme.FontCaption + 4;
            var ticks = new List<double>();
            for (double v = Math.Ceiling(min / step) * step; v < max - step * 1e-6; v += step)
                if (Y(v) - bar.Top > labelHeight && bar.Bottom - Y(v) > labelHeight) ticks.Add(v);

            foreach (var v in ticks)
            {
                double y = Math.Round(Y(v)) + 0.5;
                context.DrawLine(tickPen, new Point(bar.Right + 3, y), new Point(bar.Right + 6, y));
                Label(context, bar, y, Format(v, decimals), Regular, AppTheme.TextSecondary);
            }
            int endDecimals = Math.Min(decimals + 1, 4);
            Label(context, bar, bar.Top + 4, Format(max, endDecimals), Strong, AppTheme.TextPrimary);
            Label(context, bar, bar.Bottom - 4, Format(min, endDecimals), Strong, AppTheme.TextPrimary);
        }

        private static void Label(DrawingContext context, Rect bar, double y, string text, Typeface face, IBrush ink)
        {
            var label = Text(text, AppTheme.FontCaption - 0.5, face, ink);
            context.DrawText(label, new Point(bar.Right + 9, y - label.Height / 2));
        }

        private static FormattedText Text(string text, double size, Typeface face, IBrush ink) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, ink);

        private static string Format(double value, int decimals) =>
            Math.Abs(value) >= 1e6 || (value != 0 && Math.Abs(value) < 1e-4)
                ? value.ToString("g4", CultureInfo.CurrentCulture)
                : value.ToString("N" + decimals, CultureInfo.CurrentCulture);

        /// <summary>The nearest 1, 2, 2.5 or 5 × 10ⁿ at or above <paramref name="raw"/>.</summary>
        private static double NiceStep(double raw)
        {
            if (!(raw > 0) || double.IsInfinity(raw)) return 1;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / magnitude;
            return magnitude * (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10);
        }
    }
}
