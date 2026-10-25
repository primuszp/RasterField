using System;

namespace RasterField.Rasters
{
    /// <summary>Clipped source window and subsampled output dimensions shared by raster adapters.</summary>
    public readonly struct RasterReadWindow
    {
        private RasterReadWindow(int x, int y, int width, int height, int stepX, int stepY)
        {
            X = x; Y = y; Width = width; Height = height;
            OutputWidth = width == 0 || height == 0 ? 0 : 1 + (width - 1) / stepX;
            OutputHeight = width == 0 || height == 0 ? 0 : 1 + (height - 1) / stepY;
        }
        /// <summary>First source column.</summary>
        public int X { get; }
        /// <summary>First source row.</summary>
        public int Y { get; }
        /// <summary>Clipped source width.</summary>
        public int Width { get; }
        /// <summary>Clipped source height.</summary>
        public int Height { get; }
        /// <summary>Number of output columns (zero for an empty intersection).</summary>
        public int OutputWidth { get; }
        /// <summary>Number of output rows (zero for an empty intersection).</summary>
        public int OutputHeight { get; }

        /// <summary>Clips a requested rectangle before decimation, using overflow-safe arithmetic.</summary>
        public static RasterReadWindow Create(int sourceWidth, int sourceHeight, int x, int y,
            int width, int height, int stepX = 1, int stepY = 1)
        {
            if (sourceWidth < 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
            if (sourceHeight < 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
            if (stepX < 1) throw new ArgumentOutOfRangeException(nameof(stepX));
            if (stepY < 1) throw new ArgumentOutOfRangeException(nameof(stepY));
            int x0 = Math.Min(sourceWidth, Math.Max(0, x)), y0 = Math.Min(sourceHeight, Math.Max(0, y));
            int x1 = (int)Math.Max(x0, Math.Min(sourceWidth, (long)x + Math.Max(0, width)));
            int y1 = (int)Math.Max(y0, Math.Min(sourceHeight, (long)y + Math.Max(0, height)));
            return new RasterReadWindow(x0, y0, x1 - x0, y1 - y0, stepX, stepY);
        }

        /// <summary>Smallest uniform sampling step that fits the requested preview bounds.</summary>
        public static int OverviewStep(int width, int height, int maxWidth, int maxHeight)
        {
            if (maxWidth < 1) throw new ArgumentOutOfRangeException(nameof(maxWidth));
            if (maxHeight < 1) throw new ArgumentOutOfRangeException(nameof(maxHeight));
            return Math.Max(1, Math.Max(width == 0 ? 0 : 1 + (width - 1) / maxWidth,
                height == 0 ? 0 : 1 + (height - 1) / maxHeight));
        }
    }
}
