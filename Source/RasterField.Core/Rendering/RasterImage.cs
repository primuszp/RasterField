using System;
using RasterField.Rasters;

namespace RasterField.Rendering
{
    /// <summary>
    /// A framework-independent 32-bit image buffer in <c>BGRA8888</c> byte order
    /// (memory layout <c>B, G, R, A</c> per pixel, i.e. <c>0xAARRGGBB</c> little-endian).
    /// This matches Avalonia's <c>PixelFormat.Bgra8888</c>, WPF's <c>Bgra32</c> and
    /// SkiaSharp's <c>Bgra8888</c>, so the host UI can copy <see cref="Pixels"/> straight
    /// into a writeable bitmap with no per-pixel conversion.
    /// </summary>
    public sealed class RasterImage
    {
        /// <summary>Bytes per pixel (always 4).</summary>
        public const int BytesPerPixel = 4;

        /// <summary>Creates a transparent image of the given size.</summary>
        public RasterImage(int width, int height)
        {
            if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            Stride = checked(width * BytesPerPixel);
            Pixels = new byte[checked(Stride * height)];
        }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>Row length in bytes (<see cref="Width"/> &#215; 4).</summary>
        public int Stride { get; }

        /// <summary>The raw <c>BGRA</c> pixel bytes, row-major, top row first.</summary>
        public byte[] Pixels { get; }

        /// <summary>Writes one pixel.</summary>
        public void SetPixel(int x, int y, ColorRgba c)
        {
            int i = y * Stride + x * BytesPerPixel;
            Pixels[i] = c.B;
            Pixels[i + 1] = c.G;
            Pixels[i + 2] = c.R;
            Pixels[i + 3] = c.A;
        }

        /// <summary>Reads one pixel.</summary>
        public ColorRgba GetPixel(int x, int y)
        {
            int i = y * Stride + x * BytesPerPixel;
            return new ColorRgba(Pixels[i + 2], Pixels[i + 1], Pixels[i], Pixels[i + 3]);
        }

        /// <summary>
        /// Splits into three single-band 0-255 rasters (R, G, B) — for saving a rendered colour
        /// image (e.g. <see cref="ReliefShader.RenderSwissStyle"/>) as a true-colour, 3-band
        /// dataset rather than a flat image file. A fully transparent pixel (alpha 0) — this
        /// class's convention for "no data" — becomes (0,0,0) in all three bands, each declared
        /// with no-data value 0, so it round-trips through the usual no-data machinery.
        /// </summary>
        public (Raster R, Raster G, Raster B) ToRgbBands()
        {
            var r = new Raster(Width, Height, noDataValue: 0);
            var g = new Raster(Width, Height, noDataValue: 0);
            var b = new Raster(Width, Height, noDataValue: 0);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    ColorRgba c = GetPixel(x, y);
                    bool transparent = c.A == 0;
                    r.SetValueFast(y, x, transparent ? 0 : c.R);
                    g.SetValueFast(y, x, transparent ? 0 : c.G);
                    b.SetValueFast(y, x, transparent ? 0 : c.B);
                }
            }

            r.InvalidateStatistics(); g.InvalidateStatistics(); b.InvalidateStatistics();
            return (r, g, b);
        }
    }

    /// <summary>Colourises a <see cref="Raster"/> into a <see cref="RasterImage"/> (one pixel per cell, line 0 at the top).</summary>
    public static class RasterImageRenderer
    {
        /// <summary>Renders into a new <see cref="RasterImage"/>.</summary>
        public static RasterImage Render(Raster raster, RasterColorizer colorizer)
        {
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            var image = new RasterImage(raster.Width, raster.Height);
            RenderInto(image, raster, colorizer);
            return image;
        }

        /// <summary>Number of buckets in the lookup table <see cref="RenderInto"/> pre-bakes per render — far finer than any display can show a step at, so this is imperceptible, not a quality trade-off.</summary>
        private const int LutSize = 4096;

        /// <summary>Rows at or above which rendering is split across threads; below it the fixed cost of scheduling isn't worth it.</summary>
        private const int ParallelRowThreshold = 64;

        /// <summary>Renders into an existing buffer, which must have the raster's dimensions (lets the host reuse one allocation).</summary>
        public static void RenderInto(RasterImage image, Raster raster, RasterColorizer colorizer)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (raster == null) throw new ArgumentNullException(nameof(raster));
            if (colorizer == null) throw new ArgumentNullException(nameof(colorizer));
            if (image.Width != raster.Width || image.Height != raster.Height)
                throw new ArgumentException("Image size does not match the raster.", nameof(image));

            byte[] dst = image.Pixels;
            float[] src = raster.Samples;
            int width = raster.Width;
            int height = raster.Height;
            int stride = image.Stride;

            // The expensive part of colourisation — palette sampling, gamma's Math.Pow, the
            // render-mode switch — happens once per LUT entry here, not once per pixel below.
            // Same technique GDAL/QGIS/etc. use to colourise a raster fast.
            int[] lut = colorizer.BuildArgbLut(LutSize);
            int noDataArgb = colorizer.NoDataColor.ToArgb();
            double min = colorizer.Minimum;
            double span = colorizer.Maximum - min;
            double scale = span > 0 ? (LutSize - 1) / span : 0.0;

            void RenderRow(int y)
            {
                int rowStart = y * width;
                int p = y * stride;
                for (int x = 0; x < width; x++)
                {
                    float v = src[rowStart + x];
                    int argb;
                    if (raster.IsNoData(v))
                    {
                        argb = noDataArgb;
                    }
                    else
                    {
                        double t = (v - min) * scale;
                        int idx = t <= 0 ? 0 : t >= LutSize - 1 ? LutSize - 1 : (int)(t + 0.5);
                        argb = lut[idx];
                    }
                    // ColorRgba.ToArgb() packs (A<<24)|(R<<16)|(G<<8)|B; unpack into BGRA byte order.
                    dst[p] = (byte)argb;
                    dst[p + 1] = (byte)(argb >> 8);
                    dst[p + 2] = (byte)(argb >> 16);
                    dst[p + 3] = (byte)(argb >> 24);
                    p += 4;
                }
            }

            if (height >= ParallelRowThreshold)
                System.Threading.Tasks.Parallel.For(0, height, RenderRow);
            else
                for (int y = 0; y < height; y++) RenderRow(y);
        }
    }
}
