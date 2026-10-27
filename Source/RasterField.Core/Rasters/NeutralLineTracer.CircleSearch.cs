using System;
using System.Collections.Generic;
using System.Threading;

namespace RasterField.Rasters
{
    public static partial class NeutralLineTracer
    {
        private sealed partial class Surface
        {
            public bool CorrectAngle(double x, double y, double radius, double target, double predicted,
                double tolerance, out double angle, out bool invalidTerrain)
            {
                angle = predicted;
                invalidTerrain = false;
                for (int iteration = 0; iteration < 16; iteration++)
                {
                    if (!At(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle),
                        out double z, out double gx, out double gy))
                    { invalidTerrain = true; return false; }
                    double residual = z - target;
                    if (Math.Abs(residual) <= tolerance) return true;
                    double derivative = radius * (-gx * Math.Sin(angle) + gy * Math.Cos(angle));
                    if (Math.Abs(derivative) < 1e-14) return false;
                    double correction = residual / derivative;
                    angle -= Math.Max(-0.3, Math.Min(0.3, correction));
                    if (Math.Cos(angle - predicted) < 0.7) return false;
                }
                return false;
            }

            // Original implementation of circular root isolation, inspired by FRD's
            // candidate-circle idea. Unlike a sample-only sign test it splits at raster
            // patch boundaries, checks sampled zeroes and stationary contacts, and refines
            // brackets against the terrain itself. The angular search is bounded; this
            // is not a proof that every possible terrain continuation has been found.
            public List<double> CircleRoots(double x, double y, double radius, double target,
                double tolerance, CancellationToken cancellationToken)
            {
                const double turn = 2 * Math.PI;
                var cuts = new List<double>();
                for (int i = 0; i <= 64; i++) cuts.Add(turn * i / 64);
                var pixel = _geo.WorldToPixel(x, y);
                AddGridCuts(pixel.Column - 0.5, radius * _f / _det, -radius * _c / _det);
                AddGridCuts(pixel.Row - 0.5, -radius * _e / _det, radius * _b / _det);
                cuts.Sort();
                var roots = new List<double>();
                for (int i = 1; i < cuts.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double left = cuts[i - 1], right = cuts[i];
                    if (right - left < 1e-12) continue;
                    double middle = (left + right) * 0.5;
                    // The middle detects invalid patches; brackets never span a NoData arc.
                    if (!Evaluate(middle, out double fm, out double dm)) continue;
                    if (!Evaluate(left, out double fl, out double dl) || !Evaluate(right, out double fr, out double dr)) continue;
                    AddIfRoot(left, fl); AddIfRoot(middle, fm); AddIfRoot(right, fr);
                    SearchHalf(left, middle, fl, fm, dl, dm);
                    SearchHalf(middle, right, fm, fr, dm, dr);
                }
                return roots;

                bool Evaluate(double angle, out double value, out double derivative)
                {
                    value = derivative = 0;
                    if (!At(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle),
                        out double z, out double gx, out double gy)) return false;
                    value = z - target;
                    derivative = radius * (-gx * Math.Sin(angle) + gy * Math.Cos(angle));
                    return true;
                }

                void AddIfRoot(double angle, double value)
                {
                    if (Math.Abs(value) > tolerance) return;
                    angle = (angle % turn + turn) % turn;
                    foreach (double existing in roots)
                        if (AngleDistance(existing, angle) < 1e-8) return;
                    roots.Add(angle);
                }

                void SearchHalf(double left, double right, double fl, double fr, double dl, double dr)
                {
                    if (fl * fr < 0)
                    {
                        Refine(left, right, fl, false, out double root);
                        if (Evaluate(root, out double value, out _)) AddIfRoot(root, value);
                    }
                    // A tangent root has no sign change. A stationary point also separates
                    // two close crossings that could otherwise hide between angular samples.
                    if (dl * dr < 0)
                    {
                        Refine(left, right, dl, true, out double stationary);
                        if (!Evaluate(stationary, out double value, out _)) return;
                        AddIfRoot(stationary, value);
                        if (fl * value < 0)
                        {
                            Refine(left, stationary, fl, false, out double root);
                            if (Evaluate(root, out double f, out _)) AddIfRoot(root, f);
                        }
                        if (value * fr < 0)
                        {
                            Refine(stationary, right, value, false, out double root);
                            if (Evaluate(root, out double f, out _)) AddIfRoot(root, f);
                        }
                    }
                }

                void Refine(double left, double right, double fl, bool derivativeRoot, out double root)
                {
                    root = (left + right) * 0.5;
                    for (int iteration = 0; iteration < 64; iteration++)
                    {
                        if ((iteration & 7) == 0) cancellationToken.ThrowIfCancellationRequested();
                        root = (left + right) * 0.5;
                        if (!Evaluate(root, out double value, out double derivative)) return;
                        double f = derivativeRoot ? derivative : value;
                        if (derivativeRoot ? Math.Abs(f) <= 1e-14 : Math.Abs(f) <= tolerance) return;
                        if (right - left <= 1e-14) return;
                        if ((fl < 0) == (f < 0)) { left = root; fl = f; }
                        else right = root;
                    }
                }

                void AddGridCuts(double centre, double cosCoefficient, double sinCoefficient)
                {
                    double amplitude = Math.Sqrt(cosCoefficient * cosCoefficient + sinCoefficient * sinCoefficient);
                    if (amplitude < 1e-15) return;
                    double phase = Math.Atan2(sinCoefficient, cosCoefficient);
                    for (double grid = Math.Ceiling(centre - amplitude); grid <= centre + amplitude; grid++)
                    {
                        double ratio = Math.Max(-1, Math.Min(1, (grid - centre) / amplitude));
                        double offset = Math.Acos(ratio);
                        cuts.Add(((phase + offset) % turn + turn) % turn);
                        cuts.Add(((phase - offset) % turn + turn) % turn);
                    }
                }
            }
        }
    }
}
