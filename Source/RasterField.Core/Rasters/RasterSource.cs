using System;
using System.IO;
using RasterField.ErMapper;

namespace RasterField.Rasters
{
    /// <summary>
    /// Random-access reader over a BIL data file: reads only the pixels you ask for — an
    /// arbitrary sub-window, optionally decimated (every <i>n</i>-th row/column) — instead of
    /// loading the whole raster into memory like <see cref="BilRasterReader"/> does.
    /// </summary>
    /// <remarks>
    /// This is what makes a raster too large to comfortably hold as a single <c>float[]</c>
    /// (tens of millions of cells and up — a 15,000&#215;15,000 scene is 225 million) practical
    /// to view: <see cref="ReadWindow"/> pulls just the visible area at 1:1, and
    /// <see cref="ReadOverview"/> pulls a whole-image preview decimated down to a chosen size —
    /// a "virtual" overview computed on demand by skipping rows/columns during the read, rather
    /// than a persisted pyramid file (no <c>.ovr</c>-style sidecar is written). Seeking past
    /// skipped rows is cheap, so both stay fast regardless of the source's true size.
    /// Not thread-safe: use one <see cref="RasterSource"/> from one thread at a time.
    /// </remarks>
    public sealed class RasterSource : IRasterSource
    {
        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly long _headerOffset;
        private readonly int _sampleSize;
        private readonly bool _swap;
        private readonly ErsCellType _cellType;
        private bool _disposed;

        private RasterSource(Stream stream, bool ownsStream, ErsHeader header)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _ownsStream = ownsStream;
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (!stream.CanSeek) throw new ArgumentException("The stream must be seekable for windowed access.", nameof(stream));

            RasterInfo info = header.RasterInfo;
            Width = info.NrOfCellsPerLine;
            Height = info.NrOfLines;
            BandCount = Math.Max(1, info.NrOfBands);
            _cellType = info.CellType;
            _sampleSize = _cellType.SizeInBytes();
            _swap = BilCodec.NeedsByteSwap(header.ByteOrder);
            _headerOffset = header.HeaderOffset;
            NoDataValue = info.NullCellValue ?? double.NaN;

            if (Width <= 0 || Height <= 0)
                throw new InvalidDataException("Header does not declare a positive image size (NrOfLines / NrOfCellsPerLine).");
            if (_sampleSize == 0)
                throw new InvalidDataException($"Unsupported or missing CellType '{_cellType}'.");
        }

        /// <summary>Number of cells per line in the source raster.</summary>
        public int Width { get; }

        /// <summary>Number of lines in the source raster.</summary>
        public int Height { get; }

        /// <summary>Number of bands in the source raster.</summary>
        public int BandCount { get; }

        /// <summary>The no-data value declared by the header (<see cref="double.NaN"/> when none).</summary>
        public double NoDataValue { get; }

        /// <summary>Opens the header at <paramref name="headerPath"/> and its data file for windowed reading.</summary>
        public static RasterSource Open(string headerPath)
        {
            var header = ErsHeader.Load(headerPath);
            return Open(header, headerPath);
        }

        /// <summary>Opens the data file that <paramref name="header"/> describes (resolved relative to <paramref name="headerPath"/>) for windowed reading.</summary>
        public static RasterSource Open(ErsHeader header, string headerPath)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            var fs = BilRasterReader.OpenData(header, headerPath);
            return new RasterSource(fs, ownsStream: true, header);
        }

        /// <summary>Wraps an already-open seekable stream for windowed reading. Set <paramref name="ownsStream"/> to have <see cref="Dispose"/> close it.</summary>
        public static RasterSource FromStream(Stream stream, ErsHeader header, bool ownsStream = false) =>
            new RasterSource(stream, ownsStream, header);

        /// <summary>
        /// Reads the sub-window <c>[x, x+width) &#215; [y, y+height)</c> of one band, keeping
        /// every <paramref name="stepX"/>-th column and <paramref name="stepY"/>-th row (1 = every
        /// one, no decimation). The window is clamped to the source bounds; a window that misses
        /// the raster entirely yields a 0&#215;0 result.
        /// </summary>
        public Raster ReadWindow(int x, int y, int width, int height, int stepX = 1, int stepY = 1, int band = 0)
        {
            ThrowIfDisposed();
            if (stepX < 1) throw new ArgumentOutOfRangeException(nameof(stepX));
            if (stepY < 1) throw new ArgumentOutOfRangeException(nameof(stepY));
            if (band < 0 || band >= BandCount) throw new ArgumentOutOfRangeException(nameof(band));

            int x0 = Math.Max(0, x);
            int y0 = Math.Max(0, y);
            int x1 = Math.Min(Width, x + Math.Max(0, width));
            int y1 = Math.Min(Height, y + Math.Max(0, height));
            int spanW = x1 - x0;
            int spanH = y1 - y0;

            if (spanW <= 0 || spanH <= 0)
                return new Raster(0, 0, NoDataValue);

            int outWidth = (spanW + stepX - 1) / stepX;
            int outHeight = (spanH + stepY - 1) / stepY;
            var result = new Raster(outWidth, outHeight, NoDataValue);

            long lineBytes = (long)Width * BandCount * _sampleSize;
            var rowBuf = new byte[spanW * _sampleSize];
            var scratch = new byte[8];

            for (int oy = 0; oy < outHeight; oy++)
            {
                int sy = y0 + oy * stepY;
                long offset = _headerOffset + (long)sy * lineBytes + (long)band * Width * _sampleSize + (long)x0 * _sampleSize;
                _stream.Seek(offset, SeekOrigin.Begin);
                BilCodec.ReadExact(_stream, rowBuf, rowBuf.Length);

                if (_cellType == ErsCellType.IEEE4ByteReal && stepX == 1)
                {
                    BilCodec.DecodeFloat32Row(rowBuf, 0, result.Samples, oy * outWidth, outWidth, _swap);
                    continue;
                }

                for (int ox = 0; ox < outWidth; ox++)
                {
                    int sx = ox * stepX * _sampleSize;
                    result.SetValueFast(oy, ox, BilCodec.ReadSample(rowBuf, sx, _sampleSize, _cellType, _swap, scratch));
                }
            }

            result.InvalidateStatistics();
            return result;
        }

        /// <summary>
        /// Reads a decimated preview of the whole raster, sized to fit within
        /// <paramref name="maxWidth"/> &#215; <paramref name="maxHeight"/> pixels (isotropic —
        /// the same step is used in both directions so cells stay square).
        /// </summary>
        public Raster ReadOverview(int maxWidth, int maxHeight, int band = 0)
        {
            if (maxWidth < 1) throw new ArgumentOutOfRangeException(nameof(maxWidth));
            if (maxHeight < 1) throw new ArgumentOutOfRangeException(nameof(maxHeight));

            int stepX = Math.Max(1, (Width + maxWidth - 1) / maxWidth);
            int stepY = Math.Max(1, (Height + maxHeight - 1) / maxHeight);
            int step = Math.Max(stepX, stepY);
            return ReadWindow(0, 0, Width, Height, step, step, band);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RasterSource));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsStream) _stream.Dispose();
        }
    }
}
