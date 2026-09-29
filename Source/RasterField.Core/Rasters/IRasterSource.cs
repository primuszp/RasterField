using System;

namespace RasterField.Rasters
{
    /// <summary>
    /// Format-independent, windowed access to a raster dataset. Implementations may read ERS/BIL,
    /// GeoTIFF or another seekable source without materialising the complete dataset in memory.
    /// </summary>
    public interface IRasterSource : IDisposable
    {
        /// <summary>Gets the full raster width in cells.</summary>
        int Width { get; }

        /// <summary>Gets the full raster height in cells.</summary>
        int Height { get; }

        /// <summary>Gets the number of available raster bands.</summary>
        int BandCount { get; }

        /// <summary>Reads and optionally subsamples a rectangular window from a band.</summary>
        Raster ReadWindow(int x, int y, int width, int height, int stepX = 1, int stepY = 1, int band = 0);

        /// <summary>Reads an overview that fits within the requested dimensions.</summary>
        Raster ReadOverview(int maxWidth, int maxHeight, int band = 0);
    }
}
