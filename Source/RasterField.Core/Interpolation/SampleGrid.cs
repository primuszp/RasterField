using System;
using System.Collections.Generic;
using System.Threading;
using RasterField.Rasters;

namespace RasterField.Interpolation
{
    internal interface ISampleGrid
    {
        int Width { get; }
        int Height { get; }
        float? Read(int col, int row);
    }

    internal readonly struct LoadedSampleGrid : ISampleGrid
    {
        private readonly Raster _raster;
        public LoadedSampleGrid(Raster raster) => _raster = raster;
        public int Width => _raster.Width;
        public int Height => _raster.Height;
        public float? Read(int col, int row)
        {
            float? value = _raster.GetValueOrNull(row, col);
            return value.HasValue && !float.IsInfinity(value.Value) ? value : null;
        }
    }

    // Bounded LRU; each sampler is used by a single caller at a time. Source ownership stays with the caller.
    internal sealed class WindowedSampleGrid : ISampleGrid
    {
        private const int MaxCachedTiles = 16;
        private readonly IRasterSource _source;
        private readonly int _band;
        private readonly int _tileSize;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<long, CachedTile> _tiles = new Dictionary<long, CachedTile>();
        private readonly LinkedList<long> _recent = new LinkedList<long>();

        public WindowedSampleGrid(IRasterSource source, int band, int tileSize, CancellationToken cancellationToken)
        {
            _source = source;
            _band = band;
            _tileSize = tileSize;
            _cancellationToken = cancellationToken;
        }

        public int Width => _source.Width;
        public int Height => _source.Height;

        public float? Read(int col, int row)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Raster tile = GetTile(col / _tileSize, row / _tileSize);
            float? value = tile.GetValueOrNull(row % _tileSize, col % _tileSize);
            return value.HasValue && !float.IsInfinity(value.Value) ? value : null;
        }

        private Raster GetTile(int tileX, int tileY)
        {
            long key = ((long)tileY << 32) | (uint)tileX;
            if (_tiles.TryGetValue(key, out CachedTile? cached))
            {
                _recent.Remove(cached.Node);
                _recent.AddFirst(cached.Node);
                return cached.Raster;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            int x = tileX * _tileSize;
            int y = tileY * _tileSize;
            int width = Math.Min(_tileSize, _source.Width - x);
            int height = Math.Min(_tileSize, _source.Height - y);
            Raster raster = _source.ReadWindow(x, y, width, height, band: _band);
            var node = _recent.AddFirst(key);
            _tiles.Add(key, new CachedTile(raster, node));

            if (_tiles.Count > MaxCachedTiles)
            {
                LinkedListNode<long>? oldest = _recent.Last;
                if (oldest != null)
                {
                    _recent.RemoveLast();
                    _tiles.Remove(oldest.Value);
                }
            }
            return raster;
        }

        private sealed class CachedTile
        {
            public CachedTile(Raster raster, LinkedListNode<long> node)
            {
                Raster = raster;
                Node = node;
            }

            public Raster Raster { get; }
            public LinkedListNode<long> Node { get; }
        }
    }
}
