using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RasterField.Interpolation;

namespace RasterField.Rasters
{
    /// <summary>Non-blocking design observations that require conscious acceptance.</summary>
    [Flags]
    public enum NeutralDesignWarning
    {
        /// <summary>No warning.</summary>
        None = 0,
        /// <summary>At least 45 degrees of direction change.</summary>
        SharpTurn = 1,
        /// <summary>More than 90 degrees of direction change.</summary>
        ReverseTurn = 2,
        /// <summary>Alternating turns of at least 30 degrees.</summary>
        Zigzag = 4,
        /// <summary>The new segment intersects a nonadjacent segment.</summary>
        SelfIntersection = 8,
        /// <summary>Sampled terrain deviation exceeds the configured threshold.</summary>
        TerrainDeviation = 16,
        /// <summary>Existing segment grade differs from the reference by more than 0.25 percentage points.</summary>
        GradeDeviation = 32
    }

    /// <summary>An accepted vertex with actual terrain elevation and cumulative horizontal distance.</summary>
    public readonly struct NeutralDesignVertex
    {
        internal NeutralDesignVertex(double x, double y, double elevation, double distance)
        { X = x; Y = y; Elevation = elevation; Distance = distance; }
        /// <summary>World easting.</summary>
        public double X { get; }
        /// <summary>World northing.</summary>
        public double Y { get; }
        /// <summary>Actual terrain elevation.</summary>
        public double Elevation { get; }
        /// <summary>Horizontal chainage from the start.</summary>
        public double Distance { get; }
    }

    /// <summary>An immutable, revision-bound preview of the next segment.</summary>
    public sealed class NeutralSegmentAssessment
    {
        internal NeutralSegmentAssessment(NeutralDesignSession owner, long revision, NeutralDesignVertex end,
            List<ProfileSample> profile, bool valid, double maximumDeviation, double turn, NeutralDesignWarning warnings)
        {
            Owner = owner; Revision = revision; End = end; Profile = profile.AsReadOnly(); IsValid = valid;
            MaximumDeviation = maximumDeviation; TurnDegrees = turn; Warnings = warnings;
        }
        internal NeutralDesignSession Owner { get; }
        internal long Revision { get; }
        /// <summary>Proposed endpoint.</summary>
        public NeutralDesignVertex End { get; }
        /// <summary>Actual terrain profile, including interior samples. Missing samples are retained.</summary>
        public IReadOnlyList<ProfileSample> Profile { get; }
        /// <summary>Whether every required sample is valid and the endpoint meets the requested grade.</summary>
        public bool IsValid { get; }
        /// <summary>Maximum sampled deviation; not an analytic bound between samples.</summary>
        public double MaximumDeviation { get; }
        /// <summary>Signed turn in degrees.</summary>
        public double TurnDegrees { get; }
        /// <summary>Warnings retained if the user accepts this segment.</summary>
        public NeutralDesignWarning Warnings { get; }
        /// <summary>Signed reference grade used by the shared segment checks.</summary>
        public double ReferenceGradePercent => Owner.GradePercent;
    }

    /// <summary>
    /// A single constant-grade trial. Caller serializes access and keeps the source unchanged.
    /// Searches/assessments do not mutate the session; stale assessments cannot be committed.
    /// </summary>
    public sealed partial class NeutralDesignSession
    {
        private readonly List<NeutralDesignVertex> _vertices = new List<NeutralDesignVertex>();
        private readonly List<NeutralSegmentAssessment> _segments = new List<NeutralSegmentAssessment>();
        private readonly Stack<NeutralSegmentAssessment> _redo = new Stack<NeutralSegmentAssessment>();
        private readonly IReadOnlyList<NeutralDesignVertex> _vertexView;
        private readonly IReadOnlyList<NeutralSegmentAssessment> _segmentView;
        private double _step;

        /// <summary>Starts a trial from valid, undegraded terrain with a fixed grade.</summary>
        public NeutralDesignSession(WorldSurfaceSampler surface, double x, double y, double gradePercent, double step = 20,
            double terrainDeviationThreshold = 0.5)
        {
            Surface = surface ?? throw new ArgumentNullException(nameof(surface));
            if (!NeutralCandidateSolver.Finite(gradePercent)) throw new ArgumentOutOfRangeException(nameof(gradePercent));
            if (!NeutralCandidateSolver.Finite(terrainDeviationThreshold) || terrainDeviationThreshold < 0)
                throw new ArgumentOutOfRangeException(nameof(terrainDeviationThreshold));
            var sample = surface.Sample(x, y);
            if (!sample.IsValid || sample.Flags != SurfaceSampleFlags.None)
                throw new ArgumentException("The start point needs a complete, valid terrain neighborhood.", nameof(x));
            GradePercent = gradePercent;
            TerrainDeviationThreshold = terrainDeviationThreshold;
            SetStep(step);
            _vertices.Add(new NeutralDesignVertex(x, y, sample.Value!.Value, 0));
            _vertexView = _vertices.AsReadOnly();
            _segmentView = _segments.AsReadOnly();
        }

        /// <summary>The chosen surface, including its interpolation method.</summary>
        public WorldSurfaceSampler Surface { get; }
        /// <summary>Fixed signed grade in percent.</summary>
        public double GradePercent { get; }
        /// <summary>Sampled terrain-deviation warning threshold in metres.</summary>
        public double TerrainDeviationThreshold { get; }
        /// <summary>The next horizontal step in metres.</summary>
        public double Step => _step;
        /// <summary>Changes after every edit; guards asynchronous previews.</summary>
        public long Revision { get; private set; }
        /// <summary>Accepted vertices, including the immutable start.</summary>
        public IReadOnlyList<NeutralDesignVertex> Vertices => _vertexView;
        /// <summary>Accepted segments with their warnings and sampled profiles.</summary>
        public IReadOnlyList<NeutralSegmentAssessment> Segments => _segmentView;
        /// <summary>Current endpoint.</summary>
        public NeutralDesignVertex End => _vertices[_vertices.Count - 1];
        /// <summary>Whether an undone segment is available.</summary>
        public bool CanRedo => _redo.Count > 0;
        /// <summary>Global target elevation avoids accumulation of endpoint root errors.</summary>
        public double NextElevation => _vertices[0].Elevation + (End.Distance + Step) * GradePercent / 100;

        /// <summary>Changes only the next step; prior segment lengths remain intact.</summary>
        public void SetStep(double step)
        {
            if (!NeutralCandidateSolver.Finite(step) || step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
            if (step == _step) return;
            _step = step;
            Revision++;
        }

        /// <summary>Finds candidates without changing the current trial.</summary>
        public NeutralCandidateSearch FindCandidates(CancellationToken token = default) =>
            NeutralCandidateSolver.Find(Surface, End.X, End.Y, Step, NextElevation, token);

        /// <summary>Assesses a candidate, including interior terrain, bends and self-intersections.</summary>
        public NeutralSegmentAssessment Assess(NeutralCandidate candidate, CancellationToken token = default) => AssessSegment(candidate, true, token);

        private NeutralSegmentAssessment AssessSegment(NeutralCandidate candidate, bool requireGrade, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = End;
            double dx = candidate.X - start.X, dy = candidate.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!NeutralCandidateSolver.Finite(length) || length <= 0 || Math.Abs(length - Step) > Math.Max(1e-5, Step * 1e-8))
                throw new ArgumentException("Candidate does not belong to the current step.", nameof(candidate));
            var (_, b, c, _, e, f) = Surface.GeoReference.GeoTransform;
            double cell = Math.Min(Math.Sqrt(b * b + e * e), Math.Sqrt(c * c + f * f));
            double spacing = Math.Min(1.0, cell * 0.5);
            double required = Math.Max(2, Math.Ceiling(length / spacing));
            // Resource-limited checking must never masquerade as a fully checked segment.
            int count = (int)Math.Min(8192, required);
            bool valid = required <= 8192;
            double maximum = 0, endZ = candidate.Elevation;
            var profile = new List<ProfileSample>(count + 1);
            for (int i = 0; i <= count; i++)
            {
                token.ThrowIfCancellationRequested();
                double t = i / (double)count, x = start.X + dx * t, y = start.Y + dy * t;
                double distance = start.Distance + length * t;
                var sample = Surface.Sample(x, y);
                bool good = sample.IsValid && sample.Flags == SurfaceSampleFlags.None;
                valid &= good;
                profile.Add(new ProfileSample(distance, x, y, good ? (float?)sample.Value : null));
                if (good)
                {
                    double target = _vertices[0].Elevation + distance * GradePercent / 100;
                    maximum = Math.Max(maximum, Math.Abs(sample.Value!.Value - target));
                    if (i == count)
                    {
                        endZ = sample.Value.Value;
                        if (requireGrade) valid &= Math.Abs(endZ - target) <= NeutralCandidateSolver.ElevationTolerance * 2;
                    }
                }
            }
            var warnings = maximum > TerrainDeviationThreshold ? NeutralDesignWarning.TerrainDeviation : NeutralDesignWarning.None;
            if (!requireGrade && profile[0].Value.HasValue && profile[profile.Count - 1].Value.HasValue &&
                Math.Abs((endZ - start.Elevation) / length * 100 - GradePercent) > 0.25) warnings |= NeutralDesignWarning.GradeDeviation;
            double turn = 0;
            if (_vertices.Count >= 2)
            {
                var prior = _vertices[_vertices.Count - 2];
                double incoming = Math.Atan2(start.Y - prior.Y, start.X - prior.X);
                double outgoing = Math.Atan2(dy, dx);
                turn = Math.Atan2(Math.Sin(outgoing - incoming), Math.Cos(outgoing - incoming)) * 180 / Math.PI;
                if (Math.Abs(turn) > 90 + 1e-7) warnings |= NeutralDesignWarning.ReverseTurn;
                else if (Math.Abs(turn) >= 45 - 1e-7) warnings |= NeutralDesignWarning.SharpTurn;
                if (_segments.Count > 0 && Math.Abs(turn) >= 30 && Math.Abs(_segments[_segments.Count - 1].TurnDegrees) >= 30 &&
                    Math.Sign(turn) != Math.Sign(_segments[_segments.Count - 1].TurnDegrees)) warnings |= NeutralDesignWarning.Zigzag;
            }
            for (int i = 1; i < _vertices.Count - 1; i++)
                if (Intersects(_vertices[i - 1], _vertices[i], start.X, start.Y, candidate.X, candidate.Y))
                { warnings |= NeutralDesignWarning.SelfIntersection; break; }
            return new NeutralSegmentAssessment(this, Revision, new NeutralDesignVertex(candidate.X, candidate.Y, endZ, start.Distance + length),
                profile, valid, maximum, turn, warnings);
        }

        /// <summary>Commits only a valid preview from this exact session revision.</summary>
        public bool Accept(NeutralSegmentAssessment assessment, bool acceptWarnings = false)
        {
            if (assessment == null) throw new ArgumentNullException(nameof(assessment));
            if (!ReferenceEquals(assessment.Owner, this) || assessment.Revision != Revision || !assessment.IsValid ||
                (assessment.Warnings != NeutralDesignWarning.None && !acceptWarnings)) return false;
            _vertices.Add(assessment.End);
            _segments.Add(assessment);
            _redo.Clear();
            Revision++;
            return true;
        }

        /// <summary>Undoes one segment, retaining its warnings and profile for redo.</summary>
        public bool Undo()
        {
            if (_segments.Count == 0) return false;
            _redo.Push(_segments[_segments.Count - 1]);
            _segments.RemoveAt(_segments.Count - 1);
            _vertices.RemoveAt(_vertices.Count - 1);
            Revision++;
            return true;
        }

        /// <summary>Reapplies the last undone segment exactly, even if the next-step setting changed.</summary>
        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            var segment = _redo.Pop();
            _vertices.Add(segment.End);
            _segments.Add(segment);
            Revision++;
            return true;
        }

        /// <summary>Combined actual terrain profile without duplicate segment junctions.</summary>
        public IReadOnlyList<ProfileSample> Profile() => _segments.Count == 0
            ? new[] { new ProfileSample(0, End.X, End.Y, (float)End.Elevation) }
            : _segments.SelectMany((s, i) => i == 0 ? s.Profile : s.Profile.Skip(1)).ToArray();

        private static bool Intersects(NeutralDesignVertex a, NeutralDesignVertex b, double cx, double cy, double dx, double dy)
        {
            static double Cross(double ax, double ay, double bx, double by, double px, double py) => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
            double c1 = Cross(a.X, a.Y, b.X, b.Y, cx, cy), c2 = Cross(a.X, a.Y, b.X, b.Y, dx, dy);
            double c3 = Cross(cx, cy, dx, dy, a.X, a.Y), c4 = Cross(cx, cy, dx, dy, b.X, b.Y);
            const double eps = 1e-8;
            return Math.Min(a.X, b.X) <= Math.Max(cx, dx) + eps && Math.Max(a.X, b.X) >= Math.Min(cx, dx) - eps &&
                Math.Min(a.Y, b.Y) <= Math.Max(cy, dy) + eps && Math.Max(a.Y, b.Y) >= Math.Min(cy, dy) - eps &&
                c1 * c2 <= eps && c3 * c4 <= eps;
        }
    }
}
