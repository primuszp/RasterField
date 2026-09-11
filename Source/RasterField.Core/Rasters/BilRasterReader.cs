using System;
using System.IO;
using RasterField.ErMapper;

namespace RasterField.Rasters
{
    /// <summary>
    /// Reads the binary data file that accompanies an ER Mapper <c>.ers</c> header, in full.
    /// The data is Band-Interleaved-by-Line (BIL): for every image line, all cells of
    /// band&#160;1 are stored, then all cells of band&#160;2, and so on. Honours
    /// <see cref="ErsHeader.ByteOrder"/> and <see cref="ErsHeader.HeaderOffset"/> and maps
    /// <see cref="RasterInfo.NullCellValue"/> onto <see cref="Raster.NoDataValue"/>.
    /// </summary>
    /// <remarks>
    /// This loads every requested cell into memory, which is fine for typical datasets but not
    /// for a very large one (tens of millions of cells and up) — use <see cref="RasterSource"/>
    /// there to read only the window you need, at whatever resolution you need it.
    /// </remarks>
    public static class BilRasterReader
    {
        /// <summary>Reads a single band (0-based) from an open data stream.</summary>
        public static Raster ReadBand(Stream data, ErsHeader header, int bandIndex = 0)
        {
            var bands = ReadBands(data, header, bandIndex, bandIndex);
            return bands[0];
        }

        /// <summary>Reads every band from an open data stream.</summary>
        public static Raster[] ReadAllBands(Stream data, ErsHeader header) =>
            ReadBands(data, header, 0, header.RasterInfo.NrOfBands - 1);

        /// <summary>Opens the data file for a header on disk and reads a single band.</summary>
        public static Raster ReadBand(string headerPath, int bandIndex = 0)
        {
            var header = ErsHeader.Load(headerPath);
            using var fs = OpenData(header, headerPath);
            return ReadBand(fs, header, bandIndex);
        }

        /// <summary>Opens the data file for a header on disk and reads all bands.</summary>
        public static Raster[] ReadAllBands(string headerPath)
        {
            var header = ErsHeader.Load(headerPath);
            using var fs = OpenData(header, headerPath);
            return ReadAllBands(fs, header);
        }

        internal static FileStream OpenData(ErsHeader header, string headerPath)
        {
            string path = header.ResolveDataFilePath(headerPath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Data file for '{headerPath}' not found (looked for '{path}').", path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private static Raster[] ReadBands(Stream data, ErsHeader header, int firstBand, int lastBand)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (header == null) throw new ArgumentNullException(nameof(header));

            RasterInfo info = header.RasterInfo;
            int width = info.NrOfCellsPerLine;
            int height = info.NrOfLines;
            int bandCount = Math.Max(1, info.NrOfBands);
            ErsCellType cellType = info.CellType;
            int sampleSize = cellType.SizeInBytes();

            if (width <= 0 || height <= 0)
                throw new InvalidDataException("Header does not declare a positive image size (NrOfLines / NrOfCellsPerLine).");
            if (sampleSize == 0)
                throw new InvalidDataException($"Unsupported or missing CellType '{cellType}'.");
            if (firstBand < 0 || lastBand >= bandCount || lastBand < firstBand)
                throw new ArgumentOutOfRangeException(nameof(firstBand), "Requested band range is outside the image.");

            bool swap = BilCodec.NeedsByteSwap(header.ByteOrder);
            double noData = info.NullCellValue ?? double.NaN;

            int wanted = lastBand - firstBand + 1;
            var rasters = new Raster[wanted];
            for (int i = 0; i < wanted; i++)
                rasters[i] = new Raster(width, height, noData);

            if (header.HeaderOffset > 0) BilCodec.SkipBytes(data, header.HeaderOffset);

            long lineBytes = (long)width * bandCount * sampleSize;
            var lineBuffer = new byte[lineBytes];
            var scratch = new byte[8];

            for (int row = 0; row < height; row++)
            {
                BilCodec.ReadExact(data, lineBuffer, (int)lineBytes);

                for (int b = firstBand; b <= lastBand; b++)
                {
                    Raster target = rasters[b - firstBand];
                    int rowStart = row * width;
                    int bandOffset = b * width * sampleSize;

                    if (cellType == ErsCellType.IEEE4ByteReal)
                    {
                        BilCodec.DecodeFloat32Row(lineBuffer, bandOffset, target.Samples, rowStart, width, swap);
                        continue;
                    }

                    for (int col = 0; col < width; col++)
                    {
                        int p = bandOffset + col * sampleSize;
                        target.Samples[rowStart + col] = BilCodec.ReadSample(lineBuffer, p, sampleSize, cellType, swap, scratch);
                    }
                }
            }

            foreach (var r in rasters) r.InvalidateStatistics();
            return rasters;
        }
    }
}
