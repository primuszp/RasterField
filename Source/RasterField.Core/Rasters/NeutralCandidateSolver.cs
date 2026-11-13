using System;
using System.Collections.Generic;
using System.Threading;
using RasterField.Interpolation;

namespace RasterField.Rasters
{
    /// <summary>A fixed-step endpoint on the sampled terrain, in metric world coordinates.</summary>
    public readonly struct NeutralCandidate
    {
        internal NeutralCandidate(double x, double y, double elevation, double heading)
        { X = x; Y = y; Elevation = elevation; Heading = heading; }
        /// <summary>World easting.</summary>
        public double X { get; }
        /// <summary>World northing.</summary>
        public double Y { get; }
        /// <summary>Actual surface elevation in metres.</summary>
        public double Elevation { get; }
        /// <summary>Counterclockwise angle from world +X, in radians.</summary>
        public double Heading { get; }
    }

    /// <summary>Numerical circle search results. An empty list does not prove that no route exists.</summary>
    public sealed class NeutralCandidateSearch
    {
        internal NeutralCandidateSearch(List<NeutralCandidate> candidates, bool levelCircle, bool missing)
        { Candidates = candidates.AsReadOnly(); SampledLevelCircle = levelCircle; HasMissingTerrain = missing; }
        /// <summary>Distinct refined solutions, ordered by heading.</summary>
        public IReadOnlyList<NeutralCandidate> Candidates { get; }
        /// <summary>
        /// All discovery samples match the requested level. Instead of listing hundreds of rays,
        /// evaluate the user's exact direction with <see cref="NeutralCandidateSolver.AtHeading"/>.
        /// This is a sampling observation, not a proof of a continuous solution set.
        /// </summary>
        public bool SampledLevelCircle { get; }
        /// <summary>Some angular samples had missing or degraded terrain data.</summary>
        public bool HasMissingTerrain { get; }
    }

    /// <summary>Finds fixed-radius terrain/level intersections without choosing a route for the user.</summary>
    public static class NeutralCandidateSolver
    {
        /// <summary>Endpoint height tolerance in metres; independent of terrain-deviation warning thresholds.</summary>
        public const double ElevationTolerance = 1e-6;
        private const double Turn = 2 * Math.PI;

        /// <summary>Searches the full circle, refining sign changes and sampled tangent minima.</summary>
        public static NeutralCandidateSearch Find(WorldSurfaceSampler surface, double x, double y,
            double step, double requiredElevation, CancellationToken cancellationToken = default)
        {
            Validate(surface, x, y, step, requiredElevation);
            var (_, b, c, _, e, f) = surface.GeoReference.GeoTransform;
            double cell = Math.Min(Math.Sqrt(b * b + e * e), Math.Sqrt(c * c + f * f));
            int count = (int)Math.Min(8192, Math.Max(720, Math.Ceiling(Turn * step / Math.Max(cell * 0.25, 1e-6))));
            double delta = Turn / count;
            var values = new double?[count];
            bool missing = false, level = true;
            double? Evaluate(double angle)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = surface.Sample(x + step * Math.Cos(angle), y + step * Math.Sin(angle));
                return sample.IsValid && sample.Flags == SurfaceSampleFlags.None ? sample.Value - requiredElevation : null;
            }
            for (int i = 0; i < count; i++)
            {
                values[i] = Evaluate(i * delta);
                missing |= !values[i].HasValue;
                level &= values[i].HasValue && Math.Abs(values[i]!.Value) <= ElevationTolerance;
            }
            var result = new List<NeutralCandidate>();
            if (level) return new NeutralCandidateSearch(result, true, false);

            void Add(double angle)
            {
                angle = (angle % Turn + Turn) % Turn;
                var candidate = AtHeading(surface, x, y, step, requiredElevation, angle);
                if (!candidate.HasValue) return;
                foreach (var prior in result)
                    if (AngleDistance(prior.Heading, angle) * step < 1e-5) return;
                result.Add(candidate.Value);
            }

            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double? current = values[i], next = values[(i + 1) % count], previous = values[(i + count - 1) % count];
                if (!current.HasValue) continue;
                if (Math.Abs(current.Value) <= ElevationTolerance) Add(i * delta);
                if (next.HasValue && current.Value * next.Value < 0)
                {
                    double left = i * delta, right = (i + 1) * delta, fl = current.Value;
                    for (int iteration = 0; iteration < 64; iteration++)
                    {
                        double mid = (left + right) / 2;
                        var fm = Evaluate(mid);
                        if (!fm.HasValue) break; // Never bridge an invalid arc.
                        if (Math.Abs(fm.Value) <= ElevationTolerance * 0.1) { Add(mid); break; }
                        if (fl * fm.Value <= 0) right = mid;
                        else { left = mid; fl = fm.Value; }
                    }
                }
                // Periodic neighborhood includes tangencies at the 0/2π seam.
                if (previous.HasValue && next.HasValue && Math.Abs(current.Value) < Math.Abs(previous.Value) &&
                    Math.Abs(current.Value) <= Math.Abs(next.Value))
                {
                    double left = (i - 1) * delta, right = (i + 1) * delta;
                    const double ratio = 0.6180339887498949;
                    double a = right - ratio * (right - left), d = left + ratio * (right - left);
                    double? fa = Evaluate(a), fd = Evaluate(d);
                    for (int iteration = 0; iteration < 64 && fa.HasValue && fd.HasValue; iteration++)
                    {
                        if (Math.Abs(fa.Value) < Math.Abs(fd.Value))
                        { right = d; d = a; fd = fa; a = right - ratio * (right - left); fa = Evaluate(a); }
                        else
                        { left = a; a = d; fa = fd; d = left + ratio * (right - left); fd = Evaluate(d); }
                    }
                    if (fa.HasValue && fd.HasValue) Add(Math.Abs(fa.Value) < Math.Abs(fd.Value) ? a : d);
                }
            }
            result.Sort((a, b) => a.Heading.CompareTo(b.Heading));
            return new NeutralCandidateSearch(result, false, missing);
        }

        /// <summary>Checks an exact cursor direction, particularly for a sampled level circle.</summary>
        public static NeutralCandidate? AtHeading(WorldSurfaceSampler surface, double x, double y,
            double step, double requiredElevation, double heading)
        {
            Validate(surface, x, y, step, requiredElevation);
            if (!Finite(heading)) throw new ArgumentOutOfRangeException(nameof(heading));
            double px = x + step * Math.Cos(heading), py = y + step * Math.Sin(heading);
            var sample = surface.Sample(px, py);
            if (!sample.IsValid || sample.Flags != SurfaceSampleFlags.None || Math.Abs(sample.Value!.Value - requiredElevation) > ElevationTolerance)
                return null;
            return new NeutralCandidate(px, py, sample.Value.Value, (heading % Turn + Turn) % Turn);
        }

        /// <summary>Smallest unsigned angle between headings.</summary>
        public static double AngleDistance(double a, double b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static void Validate(WorldSurfaceSampler surface, double x, double y, double step, double z)
        {
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (!Finite(x) || !Finite(y) || !Finite(step) || step <= 0 || !Finite(z)) throw new ArgumentOutOfRangeException(nameof(step));
        }
    }
}
