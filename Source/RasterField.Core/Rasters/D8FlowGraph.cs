using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    // Single owner of D8 encoding, downstream edges and acyclic traversal.
    internal sealed class D8FlowGraph
    {
        internal static readonly (int Code, int Dx, int Dy)[] Directions =
        {
            (1, 1, 0), (2, 1, 1), (4, 0, 1), (8, -1, 1),
            (16, -1, 0), (32, -1, -1), (64, 0, -1), (128, 1, -1),
        };

        private D8FlowGraph(int count)
        {
            Downstream = new int[count];
            InDegree = new int[count];
            Valid = new bool[count];
        }
        public int[] Downstream { get; }
        public int[] InDegree { get; }
        public bool[] Valid { get; }
        public List<int> Order { get; } = new List<int>();

        public static D8FlowGraph Build(Raster direction, bool[]? include = null)
        {
            int width = direction.Width, height = direction.Height;
            var graph = new D8FlowGraph(direction.Samples.Length);
            int validCount = 0;
            for (int i = 0; i < graph.Valid.Length; i++)
            {
                graph.Downstream[i] = -1;
                graph.Valid[i] = (include == null || include[i]) && !direction.IsNoData(direction.Samples[i]);
                if (graph.Valid[i]) validCount++;
            }
            for (int i = 0; i < graph.Valid.Length; i++)
            {
                if (!graph.Valid[i]) continue;
                var (dx, dy) = Decode(direction.Samples[i]);
                if (dx == 0 && dy == 0) continue;
                int x = i % width + dx, y = i / width + dy;
                if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
                int next = y * width + x;
                if (!graph.Valid[next]) continue;
                graph.Downstream[i] = next;
                graph.InDegree[next]++;
            }
            var remaining = (int[])graph.InDegree.Clone();
            var queue = new Queue<int>();
            for (int i = 0; i < remaining.Length; i++)
                if (graph.Valid[i] && remaining[i] == 0) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                graph.Order.Add(cell);
                int next = graph.Downstream[cell];
                if (next >= 0 && --remaining[next] == 0) queue.Enqueue(next);
            }
            if (graph.Order.Count != validCount)
                throw new ArgumentException("Flow directions contain a cycle; an acyclic drainage network is required.", nameof(direction));
            return graph;
        }

        private static (int Dx, int Dy) Decode(float code)
        {
            if (code == 0) return (0, 0);
            foreach (var direction in Directions)
                if (direction.Code == code) return (direction.Dx, direction.Dy);
            throw new ArgumentException("Flow direction must be zero or a single D8 power-of-two code.", nameof(code));
        }
    }
}
