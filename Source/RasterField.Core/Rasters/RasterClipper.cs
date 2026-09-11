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
            if (double.IsNaN(source.NoDataValue))
            {
                // No declared no-data value: pre-fill the out-of-source margin with NaN so it
                // reads as "no data" rather than a stray zero.
                var buf = result.Samples;
                for (int i = 0; i < buf.Length; i++) buf[i] = float.NaN;
            }

            int srcX0 = Math.Max(0, x), srcY0 = Math.Max(0, y);
            int srcX1 = Math.Min(source.Width, x + width), srcY1 = Math.Min(source.Height, y + height);

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
