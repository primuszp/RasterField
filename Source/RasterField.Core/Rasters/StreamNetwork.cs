using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>One reach of a stream network: the cells between a source/junction and the next junction/outlet.</summary>
    public sealed class StreamSegment
    {
        internal StreamSegment(IReadOnlyList<(double X, double Y)> points, int order, double accumulation)
        {
            Points = points;
            Order = order;
            Accumulation = accumulation;
        }

        /// <summary>Cell-centre vertices in world coordinates, upstream → downstream.</summary>
        public IReadOnlyList<(double X, double Y)> Points { get; }

        /// <summary>Strahler stream order (1 = headwater).</summary>
        public int Order { get; }

        /// <summary>Flow accumulation (upstream cell count) at the segment's last cell.</summary>
        public double Accumulation { get; }
    }

    /// <summary>
    /// Derives a vector stream network from D8 flow direction + flow accumulation rasters (see
    /// <see cref="HydrologyAnalysis"/>): every cell whose accumulation reaches a threshold is a
    /// stream cell; stream cells are chained downstream into segments that break at every
    /// confluence, and each segment gets its Strahler order.
    /// </summary>
    public static class StreamNetwork
    {
        /// <summary>Extracts the network of cells with accumulation ≥ <paramref name="threshold"/>.</summary>
        public static IReadOnlyList<StreamSegment> Extract(Raster flowDirection, Raster flowAccumulation, RasterGeoReference geoReference, double threshold)
        {
            if (flowDirection == null) throw new ArgumentNullException(nameof(flowDirection));
            if (flowAccumulation == null) throw new ArgumentNullException(nameof(flowAccumulation));
            if (geoReference == null) throw new ArgumentNullException(nameof(geoReference));
            if (flowDirection.Width != flowAccumulation.Width || flowDirection.Height != flowAccumulation.Height)
                throw new ArgumentException("Flow direction and accumulation rasters must be the same size.");

            int w = flowDirection.Width, h = flowDirection.Height, n = w * h;
            var isStream = new bool[n];
            for (int i = 0; i < n; i++)
            {
                float acc = flowAccumulation.Samples[i];
                isStream[i] = !flowAccumulation.IsNoData(acc) && acc >= threshold;
            }
            var graph = D8FlowGraph.Build(flowDirection, isStream);
            var down = graph.Downstream;
            var inDegree = graph.InDegree;
            var orders = new int[n];
            var maximumIncoming = new int[n];
            var maximumCount = new int[n];
            foreach (int cell in graph.Order)
            {
                int order = inDegree[cell] == 0 ? 1
                    : maximumIncoming[cell] + (maximumCount[cell] >= 2 ? 1 : 0);
                orders[cell] = order;
                int next = down[cell];
                if (next < 0) continue;
                if (order > maximumIncoming[next]) { maximumIncoming[next] = order; maximumCount[next] = 1; }
                else if (order == maximumIncoming[next]) maximumCount[next]++;
            }

            var result = new List<StreamSegment>();
            foreach (int head in graph.Order)
            {
                if (inDegree[head] == 1) continue;
                int order = orders[head];
                var points = new List<(double X, double Y)> { CellCentre(geoReference, head, w) };
                int cell = head;
                while (true)
                {
                    int next = down[cell];
                    if (next < 0) break;
                    points.Add(CellCentre(geoReference, next, w));
                    cell = next;
                    if (inDegree[next] != 1)
                        break;
                }

                if (points.Count >= 2)
                    result.Add(new StreamSegment(points, order, flowAccumulation.Samples[cell]));
            }
            return result;
        }

        private static (double X, double Y) CellCentre(RasterGeoReference geo, int index, int width) =>
            geo.CellCentreToWorld(index % width, index / width);

    }
}
