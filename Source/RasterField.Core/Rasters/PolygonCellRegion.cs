using System;
using System.Collections.Generic;
using System.Threading;

namespace RasterField.Rasters
{
    internal readonly struct CellSpan
    {
        public CellSpan(int row, int from, int to) { Row = row; From = from; To = to; }
        public int Row { get; }
        public int From { get; }
        public int To { get; }
    }

    // Owns polygon rasterisation and bounded tile traversal, independent of the statistic computed.
    // Scanline intervals are [left, right): shared boundaries never count a cell twice.
    internal sealed class PolygonCellRegion
    {
        private readonly double[] _x, _y;
        private readonly int _width, _height;
        private readonly int _firstRow, _lastRow;

        public PolygonCellRegion(RasterGeoReference geo, IReadOnlyList<(double X, double Y)> polygon, int width, int height)
        {
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));
            if (polygon.Count < 3) throw new ArgumentException("A zone needs at least three vertices.", nameof(polygon));
            if (!geo.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");
            _width = width; _height = height;
            _x = new double[polygon.Count]; _y = new double[polygon.Count];
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < polygon.Count; i++)
            {
                var (x, y) = geo.WorldToPixel(polygon[i].X, polygon[i].Y);
                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                    throw new ArgumentException("Polygon coordinates must be finite.", nameof(polygon));
                _x[i] = x; _y[i] = y;
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            _firstRow = Bound(minY - 0.5, height);
            _lastRow = Bound(maxY - 0.5, height) - 1;
        }

        // Clamp before converting to int: polygons far beyond the raster must not overflow.
        private static int Bound(double value, int limit) => (int)Math.Ceiling(Math.Max(0, Math.Min(limit, value)));

        public IEnumerable<CellSpan> RowSpans(int row)
        {
            if (row < _firstRow || row > _lastRow) yield break;
            double y = row + 0.5;
            var crossings = new List<double>();
            for (int i = 0, j = _x.Length - 1; i < _x.Length; j = i++)
                if ((_y[i] > y) != (_y[j] > y))
                    crossings.Add(_x[i] + (y - _y[i]) / (_y[j] - _y[i]) * (_x[j] - _x[i]));
            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                int from = Bound(crossings[k] - 0.5, _width);
                int end = Bound(crossings[k + 1] - 0.5, _width);
                if (from < end) yield return new CellSpan(row, from, end - 1);
            }
        }

        public void VisitTiles(int tileSize, CancellationToken cancellationToken,
            Action<int, int, int, int, IReadOnlyList<CellSpan>> visit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_firstRow > _lastRow) return;
            for (int tileRow = _firstRow / tileSize; tileRow <= _lastRow / tileSize; tileRow++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int y = tileRow * tileSize;
                int height = Math.Min(tileSize, _height - y);
                var spansByTile = new Dictionary<int, List<CellSpan>>();
                for (int row = Math.Max(y, _firstRow); row <= Math.Min(y + height - 1, _lastRow); row++)
                    foreach (var span in RowSpans(row))
                        for (int column = span.From / tileSize; column <= span.To / tileSize; column++)
                        {
                            int x = column * tileSize;
                            if (!spansByTile.TryGetValue(column, out var spans))
                                spansByTile.Add(column, spans = new List<CellSpan>());
                            spans.Add(new CellSpan(row - y, Math.Max(span.From, x) - x,
                                Math.Min(span.To - x, tileSize - 1)));
                        }
                foreach (var entry in spansByTile)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int x = entry.Key * tileSize;
                    visit(x, y, Math.Min(tileSize, _width - x), height, entry.Value);
                }
            }
        }
    }
}
