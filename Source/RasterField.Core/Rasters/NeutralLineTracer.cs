using System;
using System.Collections.Generic;
using System.Threading;

namespace RasterField.Rasters
{
    /// <summary>Termination of a constant-grade trace; a partial trace is not a completed route.</summary>
    public enum NeutralLineStop
    {
        /// <summary>Requested length reached.</summary>
        MaximumLength,
        /// <summary>Strict interpolation reaches the data footprint or an invalid cell.</summary>
        BoundaryOrNoData,
        /// <summary>Terrain gradient is smaller than the requested grade.</summary>
        InsufficientSlope,
        /// <summary>No unique initial direction exists on a flat patch.</summary>
        FlatSurface,
        /// <summary>No forward segment satisfies the numerical constraints.</summary>
        ToleranceLimit,
        /// <summary>Configured resource limit reached.</summary>
        StepLimit
    }

    /// <summary>Metric settings. Elevations and horizontal coordinates must use the same length unit.</summary>
    public sealed class NeutralLineOptions
    {
        /// <summary>Signed rise / horizontal run, in percent.</summary>
        public double GradePercent { get; set; } = 5;
        /// <summary>Maximum horizontal chainage per branch.</summary>
        public double MaximumLength { get; set; } = 1000;
        /// <summary>Maximum absolute deviation from z(start) + grade * chainage.</summary>
        public double HeightTolerance { get; set; } = 0.02;
        /// <summary>Maximum local grade deviation in percentage points.</summary>
        public double GradeTolerancePercent { get; set; } = 0.1;
        /// <summary>Resource limit, independently for each branch.</summary>
        public int MaximumSteps { get; set; } = 50000;
    }

    /// <summary>A traced branch and its independently evaluated terrain profile.</summary>
    public sealed class NeutralLineBranch
    {
        internal NeutralLineBranch(List<ProfileSample> samples, NeutralLineStop stop, double heightError, double gradeError)
        { Samples = samples.AsReadOnly(); Stop = stop; MaximumHeightError = heightError; MaximumGradeErrorPercent = gradeError; }
        /// <summary>Chainage, world position and actual terrain elevation (including segment interiors).</summary>
        public IReadOnlyList<ProfileSample> Samples { get; }
        /// <summary>Reason tracing stopped.</summary>
        public NeutralLineStop Stop { get; }
        /// <summary>Largest profile discrepancy on all accepted segments.</summary>
        public double MaximumHeightError { get; }
        /// <summary>Largest local grade discrepancy, in percentage points.</summary>
        public double MaximumGradeErrorPercent { get; }
        /// <summary>Horizontal length achieved.</summary>
        public double Length => Samples.Count == 0 ? 0 : Samples[Samples.Count - 1].Distance;
    }

    /// <summary>
    /// Predictor/corrector tracing on a strict, cell-centred bilinear surface. The gradient
    /// predicts the two initial directions. Each corrected endpoint lies on a circle of
    /// prescribed horizontal step and at the prescribed global profile elevation.
    /// Within each bilinear patch a straight segment has a quadratic elevation profile;
    /// its extrema and endpoint derivatives provide a whole-segment acceptance check.
    /// A bracketed circle search recovers alternatives when local correction fails.
    /// No smoothing, gap filling or endpoint snapping is performed.
    /// </summary>
    public static partial class NeutralLineTracer
    {
        /// <summary>Traces both initial branches. Cancellation throws and returns no partial result.</summary>
        public static IReadOnlyList<NeutralLineBranch> Trace(Raster raster, RasterGeoReference geo,
            double startX, double startY, NeutralLineOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!Finite(startX) || !Finite(startY) || !Finite(options.GradePercent) ||
                !Positive(options.MaximumLength) || !Positive(options.HeightTolerance) ||
                !Positive(options.GradeTolerancePercent) || options.MaximumSteps < 1)
                throw new ArgumentOutOfRangeException(nameof(options));
            var surface = CreateSurface(raster, geo);
            cancellationToken.ThrowIfCancellationRequested();
            return new[] { Follow(1), Follow(-1) };

            NeutralLineBranch Follow(int side)
            {
                var samples = new List<ProfileSample>();
                if (!surface.At(startX, startY, out double z0, out _, out _))
                    return new NeutralLineBranch(samples, NeutralLineStop.BoundaryOrNoData, 0, 0);
                samples.Add(new ProfileSample(0, startX, startY, (float)z0));
                double x = startX, y = startY, s = 0, previousX = 0, previousY = 0;
                double maxHeight = 0, maxGrade = 0, grade = options.GradePercent / 100;
                double nextStep = surface.CellScale * 0.25;
                double maximumStep = surface.CellScale;
                var stop = NeutralLineStop.StepLimit;
                for (int iteration = 0; iteration < options.MaximumSteps; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.MaximumLength - s <= 1e-9 * Math.Max(1, options.MaximumLength))
                    { stop = NeutralLineStop.MaximumLength; break; }
                    if (!surface.GradientAhead(x, y, previousX, previousY, out double gx, out double gy))
                    { stop = NeutralLineStop.BoundaryOrNoData; break; }
                    double q = Math.Sqrt(gx * gx + gy * gy);
                    if (q < Math.Abs(grade) - 1e-12) { stop = NeutralLineStop.InsufficientSlope; break; }
                    if (q < 1e-12) { stop = NeutralLineStop.FlatSurface; break; }
                    double along = grade / q, across = Math.Sqrt(Math.Max(0, 1 - along * along));
                    double dx = along * gx / q - side * across * gy / q;
                    double dy = along * gy / q + side * across * gx / q;
                    // At a bilinear patch boundary the gradient can jump. Continue the
                    // geometric branch by its incoming heading instead of an arbitrary
                    // +/- label of the new patch's gradient.
                    if (iteration > 0)
                    {
                        double otherX = along * gx / q + side * across * gy / q;
                        double otherY = along * gy / q - side * across * gx / q;
                        if (otherX * previousX + otherY * previousY > dx * previousX + dy * previousY)
                        { dx = otherX; dy = otherY; }
                    }
                    double heading = iteration == 0 ? Math.Atan2(dy, dx) : Math.Atan2(previousY, previousX);
                    double predicted = Math.Atan2(dy, dx);
                    double h = Math.Min(nextStep, options.MaximumLength - s);
                    bool accepted = false;
                    stop = NeutralLineStop.ToleranceLimit;
                    for (int retry = 0; retry < 20; retry++, h *= 0.5)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (h < surface.CellScale * 1e-7) break;
                        double target = z0 + grade * (s + h);
                        double rootTolerance = Math.Max(32 * 2.220446049250313e-16 * Math.Max(1, Math.Abs(target)),
                            Math.Min(options.HeightTolerance * 0.001, h * 1e-8));
                        var angles = new List<double>();
                        if (surface.CorrectAngle(x, y, h, target, predicted, rootTolerance, out double corrected, out bool invalidTerrain))
                            angles.Add(corrected);
                        if (invalidTerrain) stop = NeutralLineStop.BoundaryOrNoData;

                        // The fast local solve is normally enough. If it is unusable, search
                        // all circle arcs and test alternatives before reducing step length.
                        SegmentCheck? chosen = null;
                        double chosenAngle = 0, chosenLength = h;
                        var events = surface.BoundaryCandidates(x, y, h, z0 + grade * s, grade, heading);
                        events.Sort((a, b) => AngleDistance(a.Angle, heading).CompareTo(AngleDistance(b.Angle, heading)));
                        foreach (var boundary in events)
                        {
                            TryCandidate(boundary.Angle, boundary.Length);
                            if (chosen != null) break;
                        }
                        if (chosen == null && angles.Count > 0) TryCandidate(angles[0], h);
                        if (chosen == null)
                        {
                            angles = surface.CircleRoots(x, y, h, target, rootTolerance, cancellationToken);
                            angles.Sort((a, b) => AngleDistance(a, heading).CompareTo(AngleDistance(b, heading)));
                            foreach (double angle in angles)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                TryCandidate(angle, h);
                                if (chosen != null) break;
                            }
                        }
                        if (chosen == null) continue;
                        h = chosenLength;
                        double endX = x + h * Math.Cos(chosenAngle), endY = y + h * Math.Sin(chosenAngle);
                        samples.AddRange(chosen.Samples);
                        maxHeight = Math.Max(maxHeight, chosen.HeightError);
                        maxGrade = Math.Max(maxGrade, chosen.GradeError * 100);
                        previousX = Math.Cos(chosenAngle); previousY = Math.Sin(chosenAngle);
                        x = endX; y = endY; s += h;
                        // Grow only after a segment has passed the whole-profile check.
                        nextStep = Math.Min(maximumStep, h * 1.5);
                        accepted = true;
                        stop = NeutralLineStop.ToleranceLimit;
                        break;

                        void TryCandidate(double angle, double length)
                        {
                            if (Math.Cos(angle - heading) <= 0) return; // never double back
                            // At the start keep the two sides distinct, even for nearby roots.
                            if (iteration == 0 && side * (gx * Math.Sin(angle) - gy * Math.Cos(angle)) < -1e-12 * q) return;
                            double ex = x + length * Math.Cos(angle), ey = y + length * Math.Sin(angle);
                            var check = surface.Check(x, y, ex, ey, s, z0, grade);
                            if (check == null) { stop = NeutralLineStop.BoundaryOrNoData; return; }
                            if (check.HeightError > options.HeightTolerance || check.GradeError * 100 > options.GradeTolerancePercent)
                            { stop = NeutralLineStop.ToleranceLimit; return; }
                            chosen = check;
                            chosenAngle = angle;
                            chosenLength = length;
                        }
                    }
                    if (!accepted) break;
                    if (iteration == options.MaximumSteps - 1) stop = NeutralLineStop.StepLimit;
                }
                if (Math.Abs(s - options.MaximumLength) <= 1e-9 * Math.Max(1, s)) stop = NeutralLineStop.MaximumLength;
                return new NeutralLineBranch(samples, stop, maxHeight, maxGrade);
            }
        }

        private static Surface CreateSurface(Raster raster, RasterGeoReference geo)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (!geo.IsInvertible || geo.Width != raster.Width || geo.Height != raster.Height)
                throw new ArgumentException("An invertible, matching georeference is required.", nameof(geo));
            var transform = geo.GeoTransform;
            if (!Finite(transform.A) || !Finite(transform.B) || !Finite(transform.C) ||
                !Finite(transform.D) || !Finite(transform.E) || !Finite(transform.F))
                throw new ArgumentException("Georeference coefficients must be finite.", nameof(geo));
            var surface = new Surface(raster, geo);
            if (!Positive(surface.CellScale)) throw new ArgumentException("Invalid cell scale.", nameof(geo));
            return surface;
        }

        private static double AngleDistance(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static bool Positive(double v) => Finite(v) && v > 0;

        private sealed class SegmentCheck
        {
            public readonly List<ProfileSample> Samples = new List<ProfileSample>();
            public double HeightError, GradeError;
        }

        private sealed partial class Surface
        {
            private readonly Raster _raster;
            private readonly RasterGeoReference _geo;
            private readonly double _b, _c, _e, _f, _det;
            public double CellScale { get; }
            public Surface(Raster raster, RasterGeoReference geo)
            {
                _raster = raster; _geo = geo;
                var t = geo.GeoTransform;
                _b = t.B; _c = t.C; _e = t.E; _f = t.F; _det = _b * _f - _c * _e;
                // Lower bound for the smallest singular value, also safe for skewed cells.
                CellScale = Math.Abs(_det) / Math.Sqrt(_b * _b + _c * _c + _e * _e + _f * _f);
            }
            public bool At(double x, double y, out double z, out double gx, out double gy) =>
                At(x, y, out z, out gx, out gy, out _);

            private bool At(double x, double y, out double z, out double gx, out double gy, out double mixed)
            {
                z = gx = gy = mixed = 0;
                var pixel = _geo.WorldToPixel(x, y);
                double u = pixel.Column - 0.5, v = pixel.Row - 0.5;
                if (u < 0 || v < 0 || u > _raster.Width - 1 || v > _raster.Height - 1 || _raster.Width < 2 || _raster.Height < 2) return false;
                int col = Math.Min((int)Math.Floor(u), _raster.Width - 2), row = Math.Min((int)Math.Floor(v), _raster.Height - 2);
                double a = _raster[row, col], b = _raster[row, col + 1], c = _raster[row + 1, col], d = _raster[row + 1, col + 1];
                if (!Valid(a) || !Valid(b) || !Valid(c) || !Valid(d)) return false;
                mixed = a - b - c + d;
                u -= col; v -= row;
                z = a * (1-u) * (1-v) + b * u * (1-v) + c * (1-u) * v + d * u * v;
                double du = (b-a) * (1-v) + (d-c) * v, dv = (c-a) * (1-u) + (d-b) * u;
                gx = (du * _f - dv * _e) / _det;
                gy = (-du * _c + dv * _b) / _det;
                return true;
            }
            private bool Valid(double z) => Finite(z) && !_raster.IsNoData((float)z);

            public SegmentCheck? Check(double x, double y, double endX, double endY, double s, double z0, double grade)
            {
                double dx = endX - x, dy = endY - y, h = Math.Sqrt(dx * dx + dy * dy);
                var p = _geo.WorldToPixel(x, y); var end = _geo.WorldToPixel(endX, endY);
                var cuts = new List<double> { 0, 1 };
                AddCuts(p.Column - 0.5, end.Column - 0.5);
                AddCuts(p.Row - 0.5, end.Row - 0.5);
                cuts.Sort();
                var result = new SegmentCheck();
                for (int i = 1; i < cuts.Count; i++)
                {
                    double ta = cuts[i-1], tb = cuts[i], span = tb-ta;
                    if (span < 1e-12) continue;
                    double tm = (ta+tb)/2;
                    if (!At(x+dx*ta, y+dy*ta, out double za, out _, out _) ||
                        !At(x+dx*tm, y+dy*tm, out double zm, out double gx, out double gy, out double mixed) ||
                        !At(x+dx*tb, y+dy*tb, out double zb, out _, out _)) return null;
                    // Evaluate exact bilinear derivatives instead of differencing close
                    // elevations. Tiny intervals next to patch boundaries otherwise amplify
                    // floating-point subtraction into a spurious local-grade violation.
                    double length = h * span;
                    double colRate = (_f * dx - _c * dy) / (_det * h);
                    double rowRate = (-_e * dx + _b * dy) / (_det * h);
                    double curvature = mixed * colRate * rowRate;
                    double midpointGrade = (gx * dx + gy * dy) / h;
                    result.GradeError = Math.Max(result.GradeError,
                        Math.Max(Math.Abs(midpointGrade - curvature * length - grade),
                            Math.Abs(midpointGrade + curvature * length - grade)));
                    double ea = za-(z0+grade*(s+h*ta)), eb = zb-(z0+grade*(s+h*tb));
                    result.HeightError = Math.Max(result.HeightError, Math.Max(Math.Abs(ea), Math.Abs(eb)));
                    double quadratic = curvature * length * length;
                    double linear = (midpointGrade - curvature * length - grade) * length;
                    double extremum = Math.Abs(quadratic) > 1e-24 ? -linear/(2*quadratic) : -1;
                    if (extremum > 0 && extremum < 1)
                        result.HeightError = Math.Max(result.HeightError, Math.Abs(ea + linear * extremum + quadratic * extremum * extremum));
                    result.Samples.Add(new ProfileSample(s+h*tm, x+dx*tm, y+dy*tm, (float)zm));
                    result.Samples.Add(new ProfileSample(s+h*tb, x+dx*tb, y+dy*tb, (float)zb));
                }
                return result;
                void AddCuts(double from, double to)
                {
                    if (Math.Abs(to-from) < 1e-14) return;
                    for (double grid = Math.Floor(Math.Min(from,to))+1; grid < Math.Max(from,to); grid++)
                        cuts.Add((grid-from)/(to-from));
                }
            }
        }
    }
}
