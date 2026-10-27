using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>Parameters of bounded, continuous, two-point constant-grade route search.</summary>
    public sealed class NeutralRouteOptions
    {
        /// <summary>Signed rise divided by horizontal run, in percent.</summary>
        public double GradePercent { get; set; } = 5;
        /// <summary>Maximum permitted horizontal route length, also for zero-grade routes.</summary>
        public double MaximumLength { get; set; } = 1000;
        /// <summary>Maximum terrain discrepancy from the global straight grade profile.</summary>
        public double HeightTolerance { get; set; } = 0.02;
        /// <summary>Maximum local grade error in percentage points.</summary>
        public double GradeTolerancePercent { get; set; } = 0.1;
        /// <summary>Maximum number of expanded search states.</summary>
        public int MaximumStates { get; set; } = 25000;
        /// <summary>Maximum number of retained states, bounding memory independently of expansion.</summary>
        public int MaximumStoredStates { get; set; } = 100000;
    }

    /// <summary>Search outcome. Failure of a finite search does not prove non-existence.</summary>
    public enum NeutralRouteStatus
    {
        /// <summary>A complete route to the exact endpoint passed profile validation.</summary>
        Success,
        /// <summary>Start and end coincide.</summary>
        CoincidentEndpoints,
        /// <summary>At least one endpoint lacks a complete valid interpolation patch.</summary>
        EndpointNoData,
        /// <summary>Endpoint elevations and requested grade cannot satisfy the global height tolerance.</summary>
        IncompatibleGrade,
        /// <summary>The required length or the straight-line distance exceeds the length limit.</summary>
        LengthLimit,
        /// <summary>State expansion or storage budget was exhausted.</summary>
        SearchLimit,
        /// <summary>The discretised candidate search ended without a compliant connection.</summary>
        NoRouteFound
    }

    /// <summary>A complete checked connection, or an explicit failure without a partial route.</summary>
    public sealed class NeutralRouteResult
    {
        internal NeutralRouteResult(NeutralRouteStatus status, IReadOnlyList<ProfileSample> samples,
            double startElevation, double? requiredLength, int expandedStates, double heightError = 0, double gradeError = 0)
        {
            Status = status; Samples = samples; StartElevation = startElevation;
            RequiredLength = requiredLength; ExpandedStates = expandedStates;
            MaximumHeightError = heightError; MaximumGradeErrorPercent = gradeError;
        }
        /// <summary>Whether a complete connection was found.</summary>
        public bool Found => Status == NeutralRouteStatus.Success;
        /// <summary>Explicit outcome.</summary>
        public NeutralRouteStatus Status { get; }
        /// <summary>Terrain samples including exact endpoints, patch boundaries and segment interiors.</summary>
        public IReadOnlyList<ProfileSample> Samples { get; }
        /// <summary>Unrounded start elevation used for the global target profile.</summary>
        public double StartElevation { get; }
        /// <summary>Exact-model nominal length (z(end)-z(start))/grade; null for zero grade or invalid endpoints.</summary>
        public double? RequiredLength { get; }
        /// <summary>Number of expanded states.</summary>
        public int ExpandedStates { get; }
        /// <summary>Horizontal route length; zero for a failed search.</summary>
        public double Length => Samples.Count == 0 ? 0 : Samples[Samples.Count - 1].Distance;
        /// <summary>Largest terrain discrepancy from the global straight grade profile.</summary>
        public double MaximumHeightError { get; }
        /// <summary>Largest local grade discrepancy, in percentage points.</summary>
        public double MaximumGradeErrorPercent { get; }
    }
}
