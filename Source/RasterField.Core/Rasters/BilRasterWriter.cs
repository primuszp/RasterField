using System;
using System.Collections.Generic;
using System.IO;
using RasterField.ErMapper;

namespace RasterField.Rasters
{
    /// <summary>
    /// Writes one or more <see cref="Raster"/> bands to an ER Mapper binary data file in
    /// Band-Interleaved-by-Line order, the exact inverse of <see cref="BilRasterReader"/>.
    /// Honours <see cref="ErsHeader.ByteOrder"/> and <see cref="ErsHeader.HeaderOffset"/>,
    /// rounds and clamps when writing integer cell types, and substitutes
    /// <see cref="RasterInfo.NullCellValue"/> (or 0) for no-data / NaN samples.
    /// </summary>
    public static class BilRasterWriter
    {
        /// <summary>Writes a single band.</summary>
        public static void Write(Stream data, ErsHeader header, Raster band) =>
            Write(data, header, new[] { band ?? throw new ArgumentNullException(nameof(band)) });

        /// <summary>Writes every band. All bands must share the size declared in <paramref name="header"/>.</summary>
        public static void Write(Stream data, ErsHeader header, IReadOnlyList<Raster> bands)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (bands == null || bands.Count == 0) throw new ArgumentException("At least one band is required.", nameof(bands));

            RasterInfo info = header.RasterInfo;
            int width = info.NrOfCellsPerLine;
            int height = info.NrOfLines;
            ErsCellType cellType = info.CellType;
            int sampleSize = cellType.SizeInBytes();

            if (sampleSize == 0)
                throw new InvalidOperationException($"Cannot write: CellType '{cellType}' is unknown.");
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Header does not declare a positive image size.");

            for (int b = 0; b < bands.Count; b++)
            {
                if (bands[b].Width != width || bands[b].Height != height)
                    throw new ArgumentException(
                        $"Band {b} is {bands[b].Width}x{bands[b].Height} but the header declares {width}x{height}. " +
                        "Use ErsDocument.Save / SyncHeaderToRasters to keep them consistent.", nameof(bands));
            }

            bool swap = NeedsByteSwap(header.ByteOrder);
            double nullValue = info.NullCellValue ?? 0.0;

            if (header.HeaderOffset > 0)
                data.Write(new byte[header.HeaderOffset], 0, checked((int)header.HeaderOffset));

            long lineBytes = (long)width * bands.Count * sampleSize;
            var line = new byte[lineBytes];
            var scratch = new byte[8];

            for (int row = 0; row < height; row++)
            {
                Array.Clear(line, 0, line.Length);
                for (int b = 0; b < bands.Count; b++)
                {
                    float[] samples = bands[b].Samples;
                    Raster raster = bands[b];
                    int rowStart = row * width;
                    int bandOffset = b * width * sampleSize;

                    for (int col = 0; col < width; col++)
                    {
                        float v = samples[rowStart + col];
                        double value = raster.IsNoData(v) ? nullValue : v;
                        Encode(value, cellType, swap, scratch);
                        Array.Copy(scratch, 0, line, bandOffset + col * sampleSize, sampleSize);
                    }
                }
                data.Write(line, 0, (int)lineBytes);
            }

            data.Flush();
        }

        private static bool NeedsByteSwap(ErsByteOrder order)
        {
            bool fileIsLittleEndian = order == ErsByteOrder.LsbFirst;
            if (order == ErsByteOrder.Unknown) fileIsLittleEndian = false; // MSBFirst default
            return fileIsLittleEndian != BitConverter.IsLittleEndian;
        }

        private static void Encode(double value, ErsCellType type, bool swap, byte[] scratch)
        {
            byte[] bytes;
            switch (type)
            {
                case ErsCellType.Unsigned8BitInteger:
                    scratch[0] = (byte)ClampRound(value, byte.MinValue, byte.MaxValue);
                    return;
                case ErsCellType.Signed8BitInteger:
                    scratch[0] = (byte)(sbyte)ClampRound(value, sbyte.MinValue, sbyte.MaxValue);
                    return;
                case ErsCellType.Unsigned16BitInteger:
                    bytes = BitConverter.GetBytes((ushort)ClampRound(value, ushort.MinValue, ushort.MaxValue));
                    break;
                case ErsCellType.Signed16BitInteger:
                    bytes = BitConverter.GetBytes((short)ClampRound(value, short.MinValue, short.MaxValue));
                    break;
                case ErsCellType.Unsigned32BitInteger:
                    bytes = BitConverter.GetBytes((uint)ClampRound(value, uint.MinValue, uint.MaxValue));
                    break;
                case ErsCellType.Signed32BitInteger:
                    bytes = BitConverter.GetBytes((int)ClampRound(value, int.MinValue, int.MaxValue));
                    break;
                case ErsCellType.IEEE4ByteReal:
                    bytes = BitConverter.GetBytes((float)value);
                    break;
                case ErsCellType.IEEE8ByteReal:
                    bytes = BitConverter.GetBytes(value);
                    break;
                default:
                    return;
            }

            if (swap) Array.Reverse(bytes);
            Array.Copy(bytes, scratch, bytes.Length);
        }

        private static double ClampRound(double value, double min, double max)
        {
            if (double.IsNaN(value)) return 0;
            double r = Math.Round(value, MidpointRounding.AwayFromZero);
            return r < min ? min : r > max ? max : r;
        }
    }
}
