using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>
    /// Adapts one or more already loaded raster bands to <see cref="IRasterSource"/>. This lets
    /// windowed algorithms handle mixed loaded/streamed inputs through one bounded-read API.
    /// Disposing the adapter does not dispose or modify the supplied rasters.
    /// </summary>
    public sealed class MemoryRasterSource : IRasterSource
    {
        private readonly IReadOnlyList<Raster> _bands;

        /// <summary>Creates a single-band source over <paramref name="raster"/>.</summary>
        public MemoryRasterSource(Raster raster)
            : this(new[] { raster ?? throw new ArgumentNullException(nameof(raster)) })
        {
        }

        /// <summary>Creates a source over equally sized in-memory bands.</summary>
        public MemoryRasterSource(IReadOnlyList<Raster> bands)
        {
            if (bands == null) throw new ArgumentNullException(nameof(bands));
            if (bands.Count == 0) throw new ArgumentException("At least one raster band is required.", nameof(bands));
            Width = bands[0].Width;
            Height = bands[0].Height;
            for (int i = 1; i < bands.Count; i++)
            {
                if (bands[i].Width != Width || bands[i].Height != Height)
                    throw new ArgumentException("All raster bands must have the same dimensions.", nameof(bands));
            }
            _bands = bands;
        }

        /// <inheritdoc />
        public int Width { get; }

        /// <inheritdoc />
        public int Height { get; }

        /// <inheritdoc />
        public int BandCount => _bands.Count;

        /// <inheritdoc />
        public Raster ReadWindow(int x, int y, int width, int height, int stepX = 1, int stepY = 1, int band = 0)
        {
            if (stepX < 1) throw new ArgumentOutOfRangeException(nameof(stepX));
            if (stepY < 1) throw new ArgumentOutOfRangeException(nameof(stepY));
            if (band < 0 || band >= BandCount) throw new ArgumentOutOfRangeException(nameof(band));

            var window = RasterReadWindow.Create(Width, Height, x, y, width, height, stepX, stepY);
            int x0 = window.X, y0 = window.Y;
            int outputWidth = window.OutputWidth, outputHeight = window.OutputHeight;
            Raster source = _bands[band];
            var result = new Raster(outputWidth, outputHeight, source.NoDataValue);
            for (int row = 0; row < outputHeight; row++)
            {
                int sourceOffset = (y0 + row * stepY) * Width + x0;
                int resultOffset = row * outputWidth;
                for (int col = 0; col < outputWidth; col++)
                    result.Samples[resultOffset + col] = source.Samples[sourceOffset + col * stepX];
            }
            return result;
        }

        /// <inheritdoc />
        public Raster ReadOverview(int maxWidth, int maxHeight, int band = 0)
        {
            int step = RasterReadWindow.OverviewStep(Width, Height, maxWidth, maxHeight);
            return ReadWindow(0, 0, Width, Height, step, step, band);
        }

        /// <inheritdoc />
        public void Dispose() { }
    }
}
