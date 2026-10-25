using System;
using System.Threading;
using System.Threading.Tasks;

namespace RasterField
{
    /// <summary>
    /// The per-row loop every raster algorithm and renderer uses: rows run in parallel when the
    /// raster is tall enough to pay for it (64 rows or more), sequentially otherwise, and
    /// cancellation is honoured either way.
    /// </summary>
    internal static class ParallelRows
    {
        /// <summary>Below this many rows the scheduling overhead outweighs the gain.</summary>
        public const int Threshold = 64;

        /// <summary>Runs <paramref name="row"/> for every row index 0 … <paramref name="height"/>−1.</summary>
        public static void For(int height, Action<int> row, CancellationToken cancellationToken = default)
        {
            if (height >= Threshold)
            {
                Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, row);
                return;
            }
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                row(y);
            }
        }
    }
}
