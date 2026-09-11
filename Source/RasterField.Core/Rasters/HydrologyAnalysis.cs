using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>
    /// Hydrological terrain derivatives from an elevation raster: D8 flow direction and the flow
    /// accumulation derived from it — the basis for delineating streams and catchments.
    /// </summary>
    /// <remarks>
    /// Uses the classic "D8" (eight-direction, steepest single descent) model with the standard
    /// Esri direction encoding (a power of two, clockwise from east):
    /// <code>
    /// 32  64  128
    /// 16   0    1
    ///  8   4    2
    /// </code>
    /// 0 marks a sink (no downhill neighbour — a local minimum or a cell whose only lower
    /// neighbours are no-data/out of bounds). D8's steepest-single-descent rule means elevation
    /// strictly decreases along every flow path, so the flow-direction graph can never contain a
    /// cycle — flow accumulation can always be computed in one topological pass, with no
    /// convergence loop needed.
    /// </remarks>
    public static class HydrologyAnalysis
    {
        /// <summary>D8 direction codes, Esri convention (clockwise from east, 0 = sink).</summary>
        private static readonly (int Code, int Dx, int Dy)[] Directions =
        {
            (1, 1, 0), (2, 1, 1), (4, 0, 1), (8, -1, 1),
            (16, -1, 0), (32, -1, -1), (64, 0, -1), (128, 1, -1),
        };

        /// <summary>
        /// Computes the D8 flow direction for every cell: the code (see the type remarks) of the
        /// neighbour with the steepest downhill slope. A no-data elevation cell, or one whose
        /// every in-bounds, non-no-data neighbour is at the same height or higher, gets no-data
        /// in the result too (the latter — a sink/flat cell with no strict descent — is
        /// represented as no-data rather than 0, so it round-trips through the raster's no-data
        /// machinery; use <see cref="Raster.IsNoData"/> to test for it either way).
        /// </summary>
        public static Raster FlowDirection(Raster elevation, double cellSizeX, double cellSizeY)
        {
            if (elevation == null) throw new ArgumentNullException(nameof(elevation));
            if (cellSizeX <= 0 || cellSizeY <= 0) throw new ArgumentOutOfRangeException(nameof(cellSizeX), "Cell sizes must be positive.");

            int w = elevation.Width, h = elevation.Height;
            var output = new Raster(w, h, noDataValue: -1);

            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    float z = elevation[r, c];
                    if (elevation.IsNoData(z)) { output.SetValueFast(r, c, float.NaN); continue; }

                    int bestCode = 0;
                    double bestSlope = 0.0; // strictly positive required: must be a genuine descent

                    foreach (var (code, dx, dy) in Directions)
                    {
                        int nc = c + dx, nr = r + dy;
                        if ((uint)nc >= (uint)w || (uint)nr >= (uint)h) continue;

                        float nz = elevation[nr, nc];
                        if (elevation.IsNoData(nz)) continue;
                        if (nz >= z) continue;

                        double dist = (dx != 0 && dy != 0)
                            ? Math.Sqrt(cellSizeX * cellSizeX + cellSizeY * cellSizeY)
                            : (dx != 0 ? cellSizeX : cellSizeY);
                        double slope = (z - nz) / dist;

                        if (slope > bestSlope) { bestSlope = slope; bestCode = code; }
                    }

                    // bestCode stays 0 (with bestSlope still 0) for a genuine sink; distinguish
                    // "no downhill neighbour" (0) from "unset" is unnecessary here since both
                    // cases correctly want the sink code 0.
                    output.SetValueFast(r, c, bestCode);
                }
            }

            output.InvalidateStatistics();
            return output;
        }

        /// <summary>
        /// Computes flow accumulation from a <see cref="FlowDirection"/> raster: for every cell,
        /// the number of cells whose flow path passes through it (not counting the cell itself) —
        /// i.e. its upstream contributing cell count. A ridge/source cell (nothing flows into it)
        /// has accumulation 0; a cell downstream of <c>N</c> others has accumulation &#8805; <c>N</c>.
        /// No-data cells in <paramref name="flowDirection"/> get no-data in the result.
        /// </summary>
        /// <remarks>
        /// Processed as a single topological (Kahn's-algorithm) pass over the flow-direction DAG:
        /// a cell is finalised only once every cell that flows into it has already contributed,
        /// then its own total (contributors + itself) is added to whatever it flows into. O(cells).
        /// </remarks>
        public static Raster FlowAccumulation(Raster flowDirection)
        {
            if (flowDirection == null) throw new ArgumentNullException(nameof(flowDirection));
            int w = flowDirection.Width, h = flowDirection.Height;
            int n = w * h;

            var target = new int[n]; // flat index this cell flows into, or -1 (sink / no-data)
            var inDegree = new int[n];
            var isValid = new bool[n];

            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    int idx = r * w + c;
                    float code = flowDirection[r, c];
                    if (flowDirection.IsNoData(code)) { target[idx] = -1; continue; }

                    isValid[idx] = true;
                    var (dx, dy) = DecodeDirection((int)Math.Round(code));
                    if (dx == 0 && dy == 0) { target[idx] = -1; continue; } // sink (code 0)

                    int nc = c + dx, nr = r + dy;
                    if ((uint)nc >= (uint)w || (uint)nr >= (uint)h) { target[idx] = -1; continue; }

                    int t = nr * w + nc;
                    target[idx] = t;
                    inDegree[t]++;
                }
            }

            var acc = new double[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
                if (isValid[i] && inDegree[i] == 0) queue.Enqueue(i);

            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int t = target[cell];
                if (t < 0) continue;

                acc[t] += acc[cell] + 1.0;
                if (--inDegree[t] == 0) queue.Enqueue(t);
            }

            var output = new Raster(w, h, noDataValue: -1);
            for (int i = 0; i < n; i++)
                output.Samples[i] = isValid[i] ? (float)acc[i] : float.NaN;

            output.InvalidateStatistics();
            return output;
        }

        private static (int Dx, int Dy) DecodeDirection(int code)
        {
            foreach (var (c, dx, dy) in Directions)
                if (c == code) return (dx, dy);
            return (0, 0); // 0 (sink) or an unrecognised code — treated the same, as "no target"
        }
    }
}
