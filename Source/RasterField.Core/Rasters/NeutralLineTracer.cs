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
    /// No smoothing, gap filling, branch switching or endpoint snapping is performed.
    /// </summary>
    public static class NeutralLineTracer
    {
        /// <summary>Traces both initial branches. Cancellation throws and returns no partial result.</summary>
        public static IReadOnlyList<NeutralLineBranch> Trace(Raster raster, RasterGeoReference geo,
            double startX, double startY, NeutralLineOptions options, CancellationToken cancellationToken = default)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!geo.IsInvertible || geo.Width != raster.Width || geo.Height != raster.Height)
                throw new ArgumentException("An invertible, matching georeference is required.", nameof(geo));
            if (!Finite(startX) || !Finite(startY) || !Finite(options.GradePercent) ||
                !Positive(options.MaximumLength) || !Positive(options.HeightTolerance) ||
                !Positive(options.GradeTolerancePercent) || options.MaximumSteps < 1)
                throw new ArgumentOutOfRangeException(nameof(options));
            var transform = geo.GeoTransform;
            if (!Finite(transform.A) || !Finite(transform.B) || !Finite(transform.C) ||
                !Finite(transform.D) || !Finite(transform.E) || !Finite(transform.F))
                throw new ArgumentException("Georeference coefficients must be finite.", nameof(geo));
            var surface = new Surface(raster, geo);
            if (!Positive(surface.CellScale)) throw new ArgumentException("Invalid cell scale.", nameof(geo));
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
                double baseStep = surface.CellScale * 0.25;
                var stop = NeutralLineStop.StepLimit;
                for (int iteration = 0; iteration < options.MaximumSteps; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.MaximumLength - s <= 1e-9 * Math.Max(1, options.MaximumLength))
                    { stop = NeutralLineStop.MaximumLength; break; }
                    if (!surface.At(x, y, out _, out double gx, out double gy))
                    { stop = NeutralLineStop.BoundaryOrNoData; break; }
                    double q = Math.Sqrt(gx * gx + gy * gy);
                    if (q < Math.Abs(grade) - 1e-12) { stop = NeutralLineStop.InsufficientSlope; break; }
                    if (q < 1e-12) { stop = NeutralLineStop.FlatSurface; break; }
                    double along = grade / q, across = Math.Sqrt(Math.Max(0, 1 - along * along));
                    double dx = along * gx / q - side * across * gy / q;
                    double dy = along * gy / q + side * across * gx / q;
                    // Preserve the initial oriented field; never jump to the opposite branch.
                    if (iteration > 0 && dx * previousX + dy * previousY <= 0)
                    { stop = NeutralLineStop.ToleranceLimit; break; }
                    double h = Math.Min(baseStep, options.MaximumLength - s);
                    bool accepted = false;
                    stop = NeutralLineStop.ToleranceLimit;
                    for (int retry = 0; retry < 20; retry++, h *= 0.5)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (h < surface.CellScale * 1e-7) break;
                        double theta = Math.Atan2(dy, dx), target = z0 + grade * (s + h);
                        bool corrected = false;
                        for (int k = 0; k < 16; k++)
                        {
                            double nx = x + h * Math.Cos(theta), ny = y + h * Math.Sin(theta);
                            if (!surface.At(nx, ny, out double z, out double ex, out double ey))
                            { stop = NeutralLineStop.BoundaryOrNoData; break; }
                            double residual = z - target;
                            if (Math.Abs(residual) <= Math.Min(options.HeightTolerance * 0.001, h * 1e-8))
                            { corrected = true; break; }
                            double derivative = h * (-ex * Math.Sin(theta) + ey * Math.Cos(theta));
                            if (Math.Abs(derivative) < 1e-14) break;
                            double correction = residual / derivative;
                            if (Math.Abs(correction) > 0.3) correction = Math.Sign(correction) * 0.3;
                            theta -= correction;
                            if (Math.Cos(theta) * dx + Math.Sin(theta) * dy < 0.7) break;
                        }
                        if (!corrected) continue;
                        double endX = x + h * Math.Cos(theta), endY = y + h * Math.Sin(theta);
                        var check = surface.Check(x, y, endX, endY, s, z0, grade);
                        if (check == null) { stop = NeutralLineStop.BoundaryOrNoData; continue; }
                        if (check.HeightError > options.HeightTolerance || check.GradeError * 100 > options.GradeTolerancePercent)
                        { stop = NeutralLineStop.ToleranceLimit; continue; }
                        samples.AddRange(check.Samples);
                        maxHeight = Math.Max(maxHeight, check.HeightError);
                        maxGrade = Math.Max(maxGrade, check.GradeError * 100);
                        previousX = (endX - x) / h; previousY = (endY - y) / h;
                        x = endX; y = endY; s += h;
                        accepted = true;
                        stop = NeutralLineStop.ToleranceLimit;
                        break;
                    }
                    if (!accepted) break;
                    if (iteration == options.MaximumSteps - 1) stop = NeutralLineStop.StepLimit;
                }
                if (Math.Abs(s - options.MaximumLength) <= 1e-9 * Math.Max(1, s)) stop = NeutralLineStop.MaximumLength;
                return new NeutralLineBranch(samples, stop, maxHeight, maxGrade);
            }
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static bool Positive(double v) => Finite(v) && v > 0;

        private sealed class SegmentCheck
        {
            public readonly List<ProfileSample> Samples = new List<ProfileSample>();
            public double HeightError, GradeError;
        }

        private sealed class Surface
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
            public bool At(double x, double y, out double z, out double gx, out double gy)
            {
                z = gx = gy = 0;
                var pixel = _geo.WorldToPixel(x, y);
                double u = pixel.Column - 0.5, v = pixel.Row - 0.5;
                if (u < 0 || v < 0 || u > _raster.Width - 1 || v > _raster.Height - 1 || _raster.Width < 2 || _raster.Height < 2) return false;
                int col = Math.Min((int)Math.Floor(u), _raster.Width - 2), row = Math.Min((int)Math.Floor(v), _raster.Height - 2);
                double a = _raster[row, col], b = _raster[row, col + 1], c = _raster[row + 1, col], d = _raster[row + 1, col + 1];
                if (!Valid(a) || !Valid(b) || !Valid(c) || !Valid(d)) return false;
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
                        !At(x+dx*tm, y+dy*tm, out double zm, out _, out _) ||
                        !At(x+dx*tb, y+dy*tb, out double zb, out _, out _)) return null;
                    // z(u)=a*u²+b*u+c on this patch, u in [0,1].
                    double a = 2*(za+zb-2*zm), b = zb-za-a, length = h*span;
                    result.GradeError = Math.Max(result.GradeError,
                        Math.Max(Math.Abs(b/length-grade), Math.Abs((2*a+b)/length-grade)));
                    double ea = za-(z0+grade*(s+h*ta)), eb = zb-(z0+grade*(s+h*tb));
                    result.HeightError = Math.Max(result.HeightError, Math.Max(Math.Abs(ea), Math.Abs(eb)));
                    double linearError = b-grade*length;
                    double extremum = Math.Abs(a) > 1e-18 ? -linearError/(2*a) : -1;
                    if (extremum > 0 && extremum < 1)
                        result.HeightError = Math.Max(result.HeightError, Math.Abs(ea+linearError*extremum+a*extremum*extremum));
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
