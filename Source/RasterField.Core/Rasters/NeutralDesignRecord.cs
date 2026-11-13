using System;
using System.Linq;
using System.Text.Json;

namespace RasterField.Rasters
{
    /// <summary>Versioned, explicitly stored user decisions. Loading never reruns a route search.</summary>
    public sealed class NeutralDesignRecord
    {
        /// <summary>Record schema version.</summary>
        public int Version { get; set; } = 1;
        /// <summary>The surface method used to design the line.</summary>
        public string SurfaceMethod { get; set; } = "";
        /// <summary>Fixed signed grade.</summary>
        public double GradePercent { get; set; }
        /// <summary>Deviation warning threshold.</summary>
        public double TerrainDeviationThreshold { get; set; }
        /// <summary>Vertices as [world X, world Y, actual elevation, horizontal chainage].</summary>
        public double[][] Vertices { get; set; } = Array.Empty<double[]>();
        /// <summary>Explicitly accepted warning flags, one per segment.</summary>
        public NeutralDesignWarning[] Warnings { get; set; } = Array.Empty<NeutralDesignWarning>();
        /// <summary>Maximum sampled terrain deviation, one per segment.</summary>
        public double[] MaximumDeviations { get; set; } = Array.Empty<double>();

        /// <summary>Copies the actual trial geometry and segment decisions.</summary>
        public static NeutralDesignRecord Capture(NeutralDesignSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return new NeutralDesignRecord
            {
                SurfaceMethod = session.Surface.Surface.MethodId,
                GradePercent = session.GradePercent,
                TerrainDeviationThreshold = session.TerrainDeviationThreshold,
                Vertices = session.Vertices.Select(v => new[] { v.X, v.Y, v.Elevation, v.Distance }).ToArray(),
                Warnings = session.Segments.Select(s => s.Warnings).ToArray(),
                MaximumDeviations = session.Segments.Select(s => s.MaximumDeviation).ToArray()
            };
        }

        /// <summary>Serializes a completed line after validating its structure.</summary>
        public string ToJson() { Validate(); return JsonSerializer.Serialize(this); }
        /// <summary>Reads stored decisions, rejecting unsupported or malformed data.</summary>
        public static NeutralDesignRecord FromJson(string json)
        {
            var record = JsonSerializer.Deserialize<NeutralDesignRecord>(json) ?? throw new FormatException("Empty neutral design record.");
            record.Validate();
            return record;
        }

        internal void Validate()
        {
            if (Version != 1 || string.IsNullOrWhiteSpace(SurfaceMethod) || !NeutralCandidateSolver.Finite(GradePercent) ||
                !NeutralCandidateSolver.Finite(TerrainDeviationThreshold) || TerrainDeviationThreshold < 0 ||
                Vertices == null || Vertices.Length < 1 || Warnings == null || Warnings.Length != Vertices.Length - 1 ||
                MaximumDeviations == null || MaximumDeviations.Length != Warnings.Length)
                throw new FormatException("Invalid neutral design record.");
            for (int i = 0; i < Vertices.Length; i++)
            {
                var v = Vertices[i];
                if (v == null || v.Length != 4 || v.Any(n => !NeutralCandidateSolver.Finite(n)))
                    throw new FormatException("Invalid neutral design vertex.");
                if (i == 0) { if (v[3] != 0) throw new FormatException("Invalid start chainage."); continue; }
                var prior = Vertices[i - 1];
                double dx = v[0] - prior[0], dy = v[1] - prior[1], length = Math.Sqrt(dx * dx + dy * dy);
                if (!(v[3] > prior[3]) || !NeutralCandidateSolver.Finite(length) ||
                    Math.Abs(v[3] - prior[3] - length) > Math.Max(1e-5, length * 1e-8) ||
                    !NeutralCandidateSolver.Finite(MaximumDeviations[i - 1]) || MaximumDeviations[i - 1] < 0 ||
                    ((int)Warnings[i - 1] & ~63) != 0)
                    throw new FormatException("Invalid neutral design segment.");
            }
        }
    }
}
