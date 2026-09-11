using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RasterField.Rendering;

namespace RasterField
{
    /// <summary>One editable gradient stop of a <see cref="PaletteEditorControl"/>.</summary>
    public sealed class PaletteStopVm
    {
        public double Position { get; set; }
        public ColorRgba Color { get; set; }
    }

    /// <summary>
    /// An interactive gradient-stop editor: a colour bar on top, draggable markers below it.
    /// Click the bar to add a stop at that position; drag a marker to move it; select a marker
    /// and use <see cref="SetSelectedColor"/> / <see cref="SetSelectedPosition"/> (wired to a
    /// host panel's colour/position fields) to fine-tune it; <c>Delete</c> removes the selection.
    /// </summary>
    public sealed class PaletteEditorControl : Control
    {
        private readonly List<PaletteStopVm> _stops = new List<PaletteStopVm>();
        private int _selected = -1;
        private bool _dragging;

        private const double BarTop = 4, BarHeight = 32;
        private const double MarkerY = BarTop + BarHeight + 14;
        private const double HitRadius = 9;

        public PaletteEditorControl()
        {
            Focusable = true;
            ClipToBounds = true;
            MinHeight = BarTop + BarHeight + 28;
            SetStops(new (double, ColorRgba)[] { (0.0, new ColorRgba(0, 0, 0)), (1.0, new ColorRgba(255, 255, 255)) });
        }

        /// <summary>Raised whenever a stop is added, removed, moved or recoloured.</summary>
        public event EventHandler? Changed;

        /// <summary>Raised when the selected stop changes.</summary>
        public event EventHandler? SelectionChanged;

        /// <summary>The stops, in position order.</summary>
        public IReadOnlyList<PaletteStopVm> Stops => _stops;

        /// <summary>Index of the selected stop, or -1.</summary>
        public int SelectedIndex => _selected;

        /// <summary>The selected stop, or <see langword="null"/>.</summary>
        public PaletteStopVm? Selected => (uint)_selected < (uint)_stops.Count ? _stops[_selected] : null;

        /// <summary>Replaces every stop.</summary>
        public void SetStops(IEnumerable<(double Position, ColorRgba Color)> stops)
        {
            _stops.Clear();
            foreach (var (p, c) in stops)
                _stops.Add(new PaletteStopVm { Position = Clamp01(p), Color = c });
            SortStops();
            _selected = _stops.Count > 0 ? _stops.Count - 1 : -1;
            RaiseChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Bakes the current stops into a 256-entry palette.</summary>
        public Palette Bake(string name) =>
            Palette.FromStops(name, _stops.Select(s => (s.Position, s.Color)).ToArray());

        /// <summary>Adds a new stop at <paramref name="position"/> (0-1), coloured by sampling the current gradient there.</summary>
        public void AddStopAt(double position)
        {
            position = Clamp01(position);
            ColorRgba color = _stops.Count > 0 ? Bake("tmp").Sample(position) : ColorRgba.White;
            var stop = new PaletteStopVm { Position = position, Color = color };
            _stops.Add(stop);
            SortStops();
            _selected = _stops.IndexOf(stop);
            RaiseChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Removes the selected stop (a palette always keeps at least two).</summary>
        public void RemoveSelected()
        {
            if (_stops.Count <= 2 || (uint)_selected >= (uint)_stops.Count) return;
            _stops.RemoveAt(_selected);
            _selected = Math.Min(_selected, _stops.Count - 1);
            RaiseChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Recolours the selected stop.</summary>
        public void SetSelectedColor(ColorRgba color)
        {
            var s = Selected;
            if (s == null) return;
            s.Color = color;
            RaiseChanged();
        }

        /// <summary>Repositions the selected stop (0-1), keeping the stop list sorted.</summary>
        public void SetSelectedPosition(double position)
        {
            var s = Selected;
            if (s == null) return;
            s.Position = Clamp01(position);
            SortStops();
            _selected = _stops.IndexOf(s);
            RaiseChanged();
        }

        /// <summary>Reverses the gradient in place.</summary>
        public void Reverse()
        {
            foreach (var s in _stops) s.Position = 1.0 - s.Position;
            SortStops();
            RaiseChanged();
        }

        private void SortStops() => _stops.Sort((a, b) => a.Position.CompareTo(b.Position));

        private void RaiseChanged()
        {
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ---- rendering ------------------------------------------------------------

        public override void Render(DrawingContext context)
        {
            double w = Math.Max(1, Bounds.Width);
            var barRect = new Rect(0, BarTop, w, BarHeight);

            if (_stops.Count > 0)
            {
                var gradientStops = new GradientStops();
                foreach (var s in _stops)
                    gradientStops.Add(new GradientStop(Color.FromArgb(s.Color.A, s.Color.R, s.Color.G, s.Color.B), s.Position));

                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = gradientStops,
                };
                context.FillRectangle(brush, barRect);
            }
            context.DrawRectangle(null, new Pen(Brushes.Black, 1), barRect);

            for (int i = 0; i < _stops.Count; i++)
            {
                var s = _stops[i];
                double x = s.Position * w;
                bool sel = i == _selected;

                var marker = new Point(x, MarkerY);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    double r = sel ? 8 : 6;
                    g.BeginFigure(new Point(x, MarkerY - r), true);
                    g.LineTo(new Point(x + r, MarkerY + r * 0.6));
                    g.LineTo(new Point(x - r, MarkerY + r * 0.6));
                    g.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(s.Color.A, s.Color.R, s.Color.G, s.Color.B)),
                    new Pen(sel ? Brushes.White : Brushes.Black, sel ? 2 : 1), geo);

                // thin connector line from the marker up to the gradient bar for clarity
                context.DrawLine(new Pen(Brushes.Gray, 1), new Point(x, BarTop + BarHeight), new Point(x, MarkerY - 8));
            }
        }

        // ---- input ------------------------------------------------------------

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            Point p = e.GetPosition(this);
            double w = Math.Max(1, Bounds.Width);

            int hit = HitTestMarker(p, w);
            if (hit >= 0)
            {
                _selected = hit;
                _dragging = true;
                e.Pointer.Capture(this);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                return;
            }

            if (p.Y >= BarTop && p.Y <= BarTop + BarHeight)
                AddStopAt(p.X / w);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging || (uint)_selected >= (uint)_stops.Count) return;

            double w = Math.Max(1, Bounds.Width);
            var s = _stops[_selected];
            s.Position = Clamp01(e.GetPosition(this).X / w);
            SortStops();
            _selected = _stops.IndexOf(s);
            RaiseChanged();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragging)
            {
                _dragging = false;
                e.Pointer.Capture(null);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                RemoveSelected();
                e.Handled = true;
            }
        }

        private int HitTestMarker(Point p, double w)
        {
            for (int i = 0; i < _stops.Count; i++)
            {
                double x = _stops[i].Position * w;
                double dx = p.X - x, dy = p.Y - MarkerY;
                if (Math.Sqrt(dx * dx + dy * dy) <= HitRadius) return i;
            }
            return -1;
        }
    }
}
