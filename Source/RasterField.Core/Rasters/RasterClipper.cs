using System;

namespace RasterField.Rasters
{
    /// <summary>Crops a <see cref="Raster"/> to a pixel-space sub-rectangle.</summary>
    public static class RasterClipper
    {
        /// <summary>
        /// Returns a new raster containing the <paramref name="width"/> &#215;
        /// <paramref name="height"/> window starting at cell (<paramref name="x"/>, <paramref name="y"/>).
        /// The window is clamped to the source raster's bounds rather than throwing, so a
        /// window that only partially overlaps the source still succeeds (mirroring the source
        /// where they intersect and leaving the rest as no-data).
        /// </summary>
        public static Raster Crop(Raster source, int x, int y, int width, int height)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");

            var result = new Raster(width, height, source.NoDataValue);
            float noData = (float)source.NoDataValue;
            for (int i = 0; i < result.Samples.Length; i++) result.Samples[i] = noData;
            var window = RasterReadWindow.Create(source.Width, source.Height, x, y, width, height);
            int srcX0 = window.X, srcY0 = window.Y;
            int srcX1 = srcX0 + window.Width, srcY1 = srcY0 + window.Height;

            for (int sy = srcY0; sy < srcY1; sy++)
            {
                int dy = sy - y;
                for (int sx = srcX0; sx < srcX1; sx++)
                    result.SetValueFast(dy, sx - x, source[sy, sx]);
            }

            result.InvalidateStatistics();
            return result;
        }
    }
}
