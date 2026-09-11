using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RasterField.Rendering;

namespace RasterField
{
    /// <summary>A vertical palette legend: colour bar (high value at the top) with value ticks.</summary>
    public sealed class LegendControl : Control
    {
        private RasterColorizer? _colorizer;
        private string? _unit;

        /// <summary>Sets the colorizer whose range and palette the legend visualises.</summary>
        public void SetColorizer(RasterColorizer? colorizer, string? unit = null)
        {
            _colorizer = colorizer;
            _unit = unit;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1C)), new Rect(Bounds.Size));
            if (_colorizer == null) return;

            double pad = 8;
            double barWidth = 20;
            var bar = new Rect(pad, pad, barWidth, Math.Max(10, Bounds.Height - 2 * pad));

            int steps = Math.Max(2, (int)bar.Height);
            for (int i = 0; i < steps; i++)
            {
                double t = 1.0 - i / (double)(steps - 1);
                double value = _colorizer.Minimum + (_colorizer.Maximum - _colorizer.Minimum) * t;
                var c = _colorizer.Map(value);
                double y = bar.Top + i * bar.Height / steps;
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)),
                    new Rect(bar.Left, y, bar.Width, bar.Height / steps + 1));
            }

            var ink = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
            var pen = new Pen(ink, 1);
            context.DrawRectangle(null, pen, bar);

            const int ticks = 6;
            for (int i = 0; i < ticks; i++)
            {
                double f = i / (double)(ticks - 1);
                double y = bar.Bottom - f * bar.Height;
                double value = _colorizer.Minimum + (_colorizer.Maximum - _colorizer.Minimum) * f;

                context.DrawLine(pen, new Point(bar.Right, y), new Point(bar.Right + 4, y));
                var label = new FormattedText(value.ToString("g4", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
                context.DrawText(label, new Point(bar.Right + 8, y - label.Height / 2));
            }

            if (!string.IsNullOrEmpty(_unit))
            {
                var u = new FormattedText(_unit!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 11, ink);
                context.DrawText(u, new Point(bar.Left, bar.Bottom + 4));
            }
        }
    }
}
