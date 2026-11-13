using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using RasterField.Rasters;

namespace RasterField
{
    public sealed partial class RasterView
    {
        private IReadOnlyList<NeutralDesignVertex> _designPath = Array.Empty<NeutralDesignVertex>();
        private IReadOnlyList<NeutralCandidate> _designCandidates = Array.Empty<NeutralCandidate>();
        private int _designSelected = -1;
        private double _designStep;
        private NeutralDesignTarget? _designTarget;
        private (double X1, double Y1, double X2, double Y2)? _designReviewSegment;
        public void SetNeutralReviewSegment((double X1, double Y1, double X2, double Y2)? segment) { _designReviewSegment = segment; InvalidateVisual(); }
        private IReadOnlyList<(double X, double Y)[]> _designVariants = Array.Empty<(double, double)[]>();
        public void SetNeutralDesignTarget(NeutralDesignTarget? target) { _designTarget = target; InvalidateVisual(); }
        public void SetNeutralDesignVariants(IReadOnlyList<(double X, double Y)[]> variants) { _designVariants = variants; InvalidateVisual(); }

        /// <summary>Routes editing shortcuts to the interactive design controller.</summary>
        public bool NeutralDesignActive { get; set; }
        /// <summary>Pointer positions for angular candidate selection; never performs a terrain search.</summary>
        public event EventHandler<RasterReadoutEventArgs>? NeutralDesignPointerMoved;
        /// <summary>Design-specific undo, redo, pause and candidate shortcuts.</summary>
        public event EventHandler<KeyEventArgs>? NeutralDesignKey;

        /// <summary>Updates the independent working overlay, which survives tool switches.</summary>
        public void SetNeutralDesignOverlay(IReadOnlyList<NeutralDesignVertex> path,
            IReadOnlyList<NeutralCandidate> candidates, int selected, double step)
        {
            _designPath = path; _designCandidates = candidates; _designSelected = selected; _designStep = step;
            InvalidateVisual();
        }

        private void DrawNeutralDesign(DrawingContext context, RasterLayer active)
        {
            if (_designReviewSegment is { } review)
                context.DrawLine(new Pen(Brushes.OrangeRed, 5), WorldToScreen(active, review.X1, review.Y1), WorldToScreen(active, review.X2, review.Y2));
            foreach (var variant in _designVariants)
                for (int i = 1; i < variant.Length; i++) context.DrawLine(new Pen(Brushes.MediumPurple, 2, DashStyle.Dash),
                    WorldToScreen(active, variant[i - 1].X, variant[i - 1].Y), WorldToScreen(active, variant[i].X, variant[i].Y));
            if (_designTarget is { } target)
            {
                var ring = new StreamGeometry();
                using (var gc = ring.Open())
                {
                    gc.BeginFigure(WorldToScreen(active, target.X + target.Radius, target.Y), false);
                    for (int i = 1; i <= 96; i++) gc.LineTo(WorldToScreen(active, target.X + target.Radius * Math.Cos(i * Math.PI / 48), target.Y + target.Radius * Math.Sin(i * Math.PI / 48)));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(null, new Pen(Brushes.OrangeRed, 2, DashStyle.Dash), ring);
            }
            if (_designPath.Count == 0) return;
            var line = new Pen(Brushes.DeepSkyBlue, 2.5);
            Point? prior = null;
            foreach (var vertex in _designPath)
            {
                var p = WorldToScreen(active, vertex.X, vertex.Y);
                if (prior.HasValue)
                {
                    context.DrawLine(new Pen(new SolidColorBrush(Colors.Black, 0.65), 5), prior.Value, p);
                    context.DrawLine(line, prior.Value, p);
                }
                context.DrawEllipse(Brushes.DeepSkyBlue, new Pen(Brushes.White, 1), p, 3, 3);
                prior = p;
            }
            var start = _designPath[0];
            context.DrawEllipse(Brushes.Teal, new Pen(Brushes.White, 2), WorldToScreen(active, start.X, start.Y), 6, 6);
            var end = _designPath[_designPath.Count - 1];
            var origin = WorldToScreen(active, end.X, end.Y);
            if (NeutralDesignActive && _designStep > 0)
            {
                // Draw in world coordinates so rotated/non-square source pixels do not distort the circle.
                var circle = new StreamGeometry();
                using (var gc = circle.Open())
                {
                    gc.BeginFigure(WorldToScreen(active, end.X + _designStep, end.Y), false);
                    for (int i = 1; i <= 96; i++)
                    {
                        double angle = i * Math.PI * 2 / 96;
                        gc.LineTo(WorldToScreen(active, end.X + _designStep * Math.Cos(angle), end.Y + _designStep * Math.Sin(angle)));
                    }
                    gc.EndFigure(true);
                }
                context.DrawGeometry(null, new Pen(new SolidColorBrush(Colors.White, 0.45), 1, DashStyle.Dash), circle);
                for (int i = 0; i < _designCandidates.Count; i++)
                {
                    var candidate = _designCandidates[i];
                    var p = WorldToScreen(active, candidate.X, candidate.Y);
                    bool selected = i == _designSelected;
                    var brush = selected ? Brushes.LimeGreen : Brushes.LightSkyBlue;
                    context.DrawLine(new Pen(new SolidColorBrush(Colors.Black, 0.4), selected ? 6 : 3), origin, p);
                    context.DrawLine(new Pen(brush, selected ? 3 : 1.5), origin, p);
                    context.DrawEllipse(brush, new Pen(Brushes.White, 1), p, selected ? 6 : 3, selected ? 6 : 3);
                }
            }
        }
    }
}
