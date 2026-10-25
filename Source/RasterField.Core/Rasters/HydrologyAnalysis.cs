using System;

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
        /// <summary>
        /// Computes the D8 flow direction for every cell: the code (see the type remarks) of the
        /// neighbour with the steepest downhill slope. No-data elevation cells remain no-data.
        /// A valid cell without a strictly lower neighbour is a sink, represented by code 0.
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

                    foreach (var (code, dx, dy) in D8FlowGraph.Directions)
                    {
                        int nc = c + dx, nr = r + dy;
                        if ((uint)nc >= (uint)w || (uint)nr >= (uint)h) continue;

                        float nz = elevation[nr, nc];
                        if (elevation.IsNoData(nz)) continue;
                        if (nz >= z) continue;

                        double dist = (dx != 0 && dy != 0)
                            ? Math.Sqrt(cellSizeX * cellSizeX + cellSizeY * cellSizeY)
                            : (dx != 0 ? cellSizeX : cellSizeY);
                        double slope = ((double)z - nz) / dist;

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
        /// <exception cref="ArgumentException">The supplied flow directions contain a cycle.</exception>
        public static Raster FlowAccumulation(Raster flowDirection)
        {
            if (flowDirection == null) throw new ArgumentNullException(nameof(flowDirection));
            var graph = D8FlowGraph.Build(flowDirection);
            var accumulation = new double[flowDirection.Samples.Length];
            foreach (int cell in graph.Order)
            {
                int next = graph.Downstream[cell];
                if (next >= 0) accumulation[next] += accumulation[cell] + 1;
            }
            var output = new Raster(flowDirection.Width, flowDirection.Height, noDataValue: -1);
            for (int i = 0; i < accumulation.Length; i++)
                output.Samples[i] = graph.Valid[i] ? (float)accumulation[i] : float.NaN;
            return output;
        }
    }
}
