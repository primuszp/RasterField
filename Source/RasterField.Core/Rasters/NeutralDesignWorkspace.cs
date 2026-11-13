using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RasterField.Interpolation;

namespace RasterField.Rasters
{
    /// <summary>Optional arrival area; reaching it never moves the endpoint to its centre.</summary>
    public sealed class NeutralDesignTarget
    {
        /// <summary>World easting of the target.</summary>
        public double X { get; set; }
        /// <summary>World northing of the target.</summary>
        public double Y { get; set; }
        /// <summary>Arrival radius in metres.</summary>
        public double Radius { get; set; } = 10;
        /// <summary>Target terrain elevation when available.</summary>
        public double? Elevation { get; set; }
        /// <summary>Horizontal distance to the target centre.</summary>
        public double Distance(double x, double y) => Math.Sqrt((x - X) * (x - X) + (y - Y) * (y - Y));
        /// <summary>Whether a point is within the arrival area.</summary>
        public bool Reached(double x, double y) => Distance(x, y) <= Radius;
        /// <summary>Whether a rising or falling trial has passed the target level outside its arrival area.</summary>
        public bool LevelPassed(NeutralDesignSession session) => Elevation.HasValue && !Reached(session.End.X, session.End.Y) &&
            (session.GradePercent > 0 ? session.End.Elevation > Elevation.Value : session.GradePercent < 0 && session.End.Elevation < Elevation.Value);
        /// <summary>Rejects malformed or unsupported stored data.</summary>
        public void Validate()
        {
            if (!NeutralCandidateSolver.Finite(X) || !NeutralCandidateSolver.Finite(Y) || !NeutralCandidateSolver.Finite(Radius) || Radius <= 0 ||
                (Elevation.HasValue && !NeutralCandidateSolver.Finite(Elevation.Value))) throw new FormatException("Invalid arrival area.");
        }
    }

    /// <summary>Portable draft, including undo/redo history and exact accepted decisions.</summary>
    public sealed class NeutralDesignDraft
    {
        /// <summary>Draft schema version.</summary>
        public int Version { get; set; } = 1;
        /// <summary>Version of the fixed turn, grade and terrain-check guidance rules.</summary>
        public int RuleSetVersion { get; set; } = 1;
        /// <summary>User-visible variant name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Project layer identifier for the terrain source.</summary>
        public string? SourceLayerId { get; set; }
        /// <summary>Hash of terrain values and spatial metadata used to guard continuation.</summary>
        public string SourceSignature { get; set; } = "";
        /// <summary>Stored coordinate projection for offline geometry.</summary>
        public string Projection { get; set; } = "";
        /// <summary>Stored datum for offline geometry.</summary>
        public string Datum { get; set; } = "";
        /// <summary>Stored horizontal units.</summary>
        public string Units { get; set; } = "";
        /// <summary>Explicit unit assumption made during design.</summary>
        public string? UnitAssumption { get; set; }
        /// <summary>Zero-based terrain band.</summary>
        public int Band { get; set; }
        /// <summary>Whether this variant is shown on the map.</summary>
        public bool Visible { get; set; } = true;
        /// <summary>All accepted and redo vertices, with stored decisions.</summary>
        public NeutralDesignRecord Record { get; set; } = new NeutralDesignRecord();
        /// <summary>Number of segments currently applied; remaining segments form redo history.</summary>
        public int AcceptedSegments { get; set; }
        /// <summary>Next step length.</summary>
        public double Step { get; set; } = 20;
        /// <summary>Optional arrival area.</summary>
        public NeutralDesignTarget? Target { get; set; }
        // Each sample is [chainage, x, y, elevation]. Null elevations retain NoData.
        /// <summary>Stored terrain samples: chainage, easting, northing, nullable elevation.</summary>
        public double?[][][] Profiles { get; set; } = Array.Empty<double?[][]>();
        /// <summary>Signed segment turn angles in degrees.</summary>
        public double[] Turns { get; set; } = Array.Empty<double>();

        /// <summary>Rejects malformed or unsupported stored data.</summary>
        public void Validate()
        {
            if (Record == null) throw new FormatException("Missing neutral design record.");
            Record.Validate(); Target?.Validate();
            if (Version != 1 || RuleSetVersion != 1 || Band < 0 || !NeutralCandidateSolver.Finite(Step) || Step <= 0 || AcceptedSegments < 0 ||
                AcceptedSegments > Record.Warnings.Length || Profiles == null || Turns == null ||
                Profiles.Length != Record.Warnings.Length || Turns.Length != Profiles.Length) throw new FormatException("Invalid neutral draft.");
            for (int i = 0; i < Profiles.Length; i++)
            {
                if (!NeutralCandidateSolver.Finite(Turns[i]) || Profiles[i] == null || Profiles[i].Length < 2 || Profiles[i].Length > 8193)
                    throw new FormatException("Invalid segment profile.");
                var start = Record.Vertices[i]; var end = Record.Vertices[i + 1];
                double last = start[3];
                foreach (var p in Profiles[i])
                {
                    if (p == null || p.Length != 4 || p.Any(v => !v.HasValue || !NeutralCandidateSolver.Finite(v.Value)) ||
                        Math.Abs(p[3]!.Value) > float.MaxValue || p[0] < last || p[0] > end[3] + 1e-8)
                        throw new FormatException("Invalid terrain sample.");
                    last = p[0]!.Value;
                    double t = (last - start[3]) / (end[3] - start[3]);
                    if (Math.Abs(p[1]!.Value - (start[0] + t * (end[0] - start[0]))) > 1e-5 || Math.Abs(p[2]!.Value - (start[1] + t * (end[1] - start[1]))) > 1e-5)
                        throw new FormatException("Profile does not follow the stored segment.");
                }
                if (Math.Abs(Profiles[i][0][0]!.Value - start[3]) > 1e-8 || Math.Abs(last - end[3]) > 1e-8) throw new FormatException("Incomplete segment profile.");
            }
        }
    }

    public sealed partial class NeutralDesignSession
    {
        /// <summary>Captures accepted decisions and undo history without changing the session.</summary>
        public NeutralDesignDraft CaptureDraft()
        {
            var all = _segments.Concat(_redo).ToArray();
            var record = NeutralDesignRecord.Capture(this);
            record.Vertices = new[] { _vertices[0] }.Concat(all.Select(s => s.End)).Select(v => new[] { v.X, v.Y, v.Elevation, v.Distance }).ToArray();
            record.Warnings = all.Select(s => s.Warnings).ToArray();
            record.MaximumDeviations = all.Select(s => s.MaximumDeviation).ToArray();
            return new NeutralDesignDraft
            {
                Record = record,
                AcceptedSegments = _segments.Count,
                Step = Step,
                Turns = all.Select(s => s.TurnDegrees).ToArray(),
                Profiles = all.Select(s => s.Profile.Select(p => new double?[] { p.Distance, p.X, p.Y, p.Value }).ToArray()).ToArray()
            };
        }

        /// <summary>Restores stored geometry without re-searching. The caller must verify source identity before continuing.</summary>
        public static NeutralDesignSession Restore(WorldSurfaceSampler surface, NeutralDesignDraft draft)
        {
            draft.Validate();
            if (surface.Surface.MethodId != draft.Record.SurfaceMethod) throw new ArgumentException("Surface method changed.");
            var first = draft.Record.Vertices[0];
            var session = new NeutralDesignSession(surface, first[0], first[1], draft.Record.GradePercent, draft.Step, draft.Record.TerrainDeviationThreshold);
            if (Math.Abs(session.End.Elevation - first[2]) > NeutralCandidateSolver.ElevationTolerance * 2) throw new ArgumentException("Source terrain changed at the start point.");
            session._vertices[0] = new NeutralDesignVertex(first[0], first[1], first[2], 0);
            var segments = new List<NeutralSegmentAssessment>();
            for (int i = 0; i < draft.Record.Warnings.Length; i++)
            {
                var v = draft.Record.Vertices[i + 1];
                segments.Add(new NeutralSegmentAssessment(session, session.Revision, new NeutralDesignVertex(v[0], v[1], v[2], v[3]),
                    draft.Profiles[i].Select(p => new ProfileSample(p[0]!.Value, p[1]!.Value, p[2]!.Value, (float?)p[3])).ToList(), true,
                    draft.Record.MaximumDeviations[i], draft.Turns[i], draft.Record.Warnings[i]));
            }
            session._segments.AddRange(segments.Take(draft.AcceptedSegments));
            session._vertices.AddRange(session._segments.Select(s => s.End));
            for (int i = segments.Count - 1; i >= draft.AcceptedSegments; i--) session._redo.Push(segments[i]);
            return session;
        }

        /// <summary>Checks original vertices with the same terrain, turn and intersection rules; does not change the input line.</summary>
        public static IReadOnlyList<NeutralSegmentAssessment> Review(WorldSurfaceSampler surface, IReadOnlyList<(double X, double Y)> points,
            double? gradePercent = null, CancellationToken token = default)
        {
            if (points == null || points.Count < 2) throw new ArgumentException("A line needs at least two points.");
            double length = 0;
            for (int i = 1; i < points.Count; i++) length += Math.Sqrt(Math.Pow(points[i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
            var start = surface.Sample(points[0].X, points[0].Y); var end = surface.Sample(points[points.Count - 1].X, points[points.Count - 1].Y);
            if (!start.IsValid || (!gradePercent.HasValue && !end.IsValid) || length <= 0) throw new ArgumentException("Cannot determine reference grade.");
            var session = new NeutralDesignSession(surface, points[0].X, points[0].Y, gradePercent ?? (end.Value!.Value - start.Value!.Value) / length * 100);
            for (int i = 1; i < points.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var p = points[i]; var prior = points[i - 1];
                session.SetStep(Math.Sqrt(Math.Pow(p.X - prior.X, 2) + Math.Pow(p.Y - prior.Y, 2)));
                var check = session.AssessSegment(new NeutralCandidate(p.X, p.Y, 0, Math.Atan2(p.Y - prior.Y, p.X - prior.X)), false, token);
                session._segments.Add(check); session._vertices.Add(check.End); session.Revision++;
            }
            return session.Segments;
        }
    }
}
