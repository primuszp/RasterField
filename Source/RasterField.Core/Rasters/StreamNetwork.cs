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
        private static readonly (int Code, int Dx, int Dy)[] Directions =
        {
            (1, 1, 0), (2, 1, 1), (4, 0, 1), (8, -1, 1),
            (16, -1, 0), (32, -1, -1), (64, 0, -1), (128, 1, -1),
        };

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
            var down = new int[n];
            for (int i = 0; i < n; i++)
            {
                down[i] = -1;
                float acc = flowAccumulation.Samples[i];
                isStream[i] = !flowAccumulation.IsNoData(acc) && acc >= threshold;
            }

            var inDegree = new int[n];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    int i = r * w + c;
                    if (!isStream[i]) continue;
                    float code = flowDirection.Samples[i];
                    if (flowDirection.IsNoData(code)) continue;
                    var (dx, dy) = Decode((int)Math.Round(code));
                    int nc = c + dx, nr = r + dy;
                    if ((dx == 0 && dy == 0) || (uint)nc >= (uint)w || (uint)nr >= (uint)h) continue;
                    int j = nr * w + nc;
                    if (!isStream[j]) continue; // cannot really happen (accumulation grows downstream), but be safe
                    down[i] = j;
                    inDegree[j]++;
                }

            // Segment heads: sources (in-degree 0) and confluences (in-degree ≥ 2).
            var heads = new List<int>();
            for (int i = 0; i < n; i++)
                if (isStream[i] && inDegree[i] != 1) heads.Add(i);
            // Upstream first: accumulation strictly increases downstream along D8 paths.
            heads.Sort((a, b) => flowAccumulation.Samples[a].CompareTo(flowAccumulation.Samples[b]));

            var ordersArrivingAt = new Dictionary<int, List<int>>(); // confluence cell → orders of reaches ending there
            var result = new List<StreamSegment>(heads.Count);
            foreach (int head in heads)
            {
                int order = 1;
                if (inDegree[head] >= 2 && ordersArrivingAt.TryGetValue(head, out var incoming))
                {
                    int max = 0, countMax = 0;
                    foreach (int o in incoming)
                    {
                        if (o > max) { max = o; countMax = 1; }
                        else if (o == max) countMax++;
                    }
                    order = countMax >= 2 ? max + 1 : max;
                }

                var points = new List<(double X, double Y)> { CellCentre(geoReference, head, w) };
                int cell = head;
                while (true)
                {
                    int next = down[cell];
                    if (next < 0) break;
                    points.Add(CellCentre(geoReference, next, w));
                    cell = next;
                    if (inDegree[next] != 1)
                    {
                        if (!ordersArrivingAt.TryGetValue(next, out var list)) ordersArrivingAt[next] = list = new List<int>();
                        list.Add(order);
                        break;
                    }
                }

                if (points.Count >= 2)
                    result.Add(new StreamSegment(points, order, flowAccumulation.Samples[cell]));
            }
            return result;
        }

        private static (double X, double Y) CellCentre(RasterGeoReference geo, int index, int width) =>
            geo.CellCentreToWorld(index % width, index / width);

        private static (int Dx, int Dy) Decode(int code)
        {
            foreach (var (c, dx, dy) in Directions)
                if (c == code) return (dx, dy);
            return (0, 0);
        }
    }
}
