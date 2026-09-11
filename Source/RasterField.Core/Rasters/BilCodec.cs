using System;
using System.IO;
using RasterField.ErMapper;

namespace RasterField.Rasters
{
    /// <summary>
    /// Low-level sample decoding shared by <see cref="BilRasterReader"/> (whole-band reads) and
    /// <see cref="RasterSource"/> (windowed/decimated reads), so both honour byte order and cell
    /// type identically.
    /// </summary>
    internal static class BilCodec
    {
        /// <summary><see langword="true"/> when the file's declared byte order differs from the host's.</summary>
        public static bool NeedsByteSwap(ErsByteOrder order)
        {
            bool fileIsLittleEndian = order == ErsByteOrder.LsbFirst;
            // MSBFirst is the ER Mapper default; assume it when unspecified.
            if (order == ErsByteOrder.Unknown) fileIsLittleEndian = false;
            return fileIsLittleEndian != BitConverter.IsLittleEndian;
        }

        /// <summary>Decodes one sample of <paramref name="type"/> at <paramref name="offset"/> in <paramref name="buf"/>.</summary>
        public static float ReadSample(byte[] buf, int offset, int size, ErsCellType type, bool swap, byte[] scratch)
        {
            // Present the sample bytes to BitConverter in host byte order.
            byte[] src = buf;
            int o = offset;
            if (swap && size > 1)
            {
                for (int i = 0; i < size; i++) scratch[i] = buf[offset + size - 1 - i];
                src = scratch;
                o = 0;
            }

            switch (type)
            {
                case ErsCellType.Unsigned8BitInteger: return buf[offset];
                case ErsCellType.Signed8BitInteger: return (sbyte)buf[offset];
                case ErsCellType.Unsigned16BitInteger: return BitConverter.ToUInt16(src, o);
                case ErsCellType.Signed16BitInteger: return BitConverter.ToInt16(src, o);
                case ErsCellType.Unsigned32BitInteger: return BitConverter.ToUInt32(src, o);
                case ErsCellType.Signed32BitInteger: return BitConverter.ToInt32(src, o);
                case ErsCellType.IEEE4ByteReal: return BitConverter.ToSingle(src, o);
                case ErsCellType.IEEE8ByteReal: return (float)BitConverter.ToDouble(src, o);
                default: return 0f;
            }
        }

        /// <summary>
        /// Decodes <paramref name="count"/> consecutive <see cref="ErsCellType.IEEE4ByteReal"/>
        /// samples starting at <paramref name="srcOffset"/> in <paramref name="src"/> directly
        /// into <paramref name="dest"/> at <paramref name="destIndex"/> — a bulk byte copy
        /// (preceded by an in-place 4-byte-word swap when the file's byte order doesn't match the
        /// host's) instead of one <see cref="ReadSample"/> call per sample. A float32 sample is
        /// already bit-for-bit the destination's own representation once the byte order matches,
        /// so there's nothing left to "decode" — this is the single hottest path in the whole
        /// library (every pan/zoom re-read, every full-raster load), and the dominant real-world
        /// cell type here, so it's worth a dedicated fast path rather than the generic per-sample
        /// switch <see cref="ReadSample"/> uses for every other type.
        /// </summary>
        public static void DecodeFloat32Row(byte[] src, int srcOffset, float[] dest, int destIndex, int count, bool swap)
        {
            int byteCount = count * 4;
            if (!swap)
            {
                Buffer.BlockCopy(src, srcOffset, dest, destIndex * 4, byteCount);
                return;
            }

            // Swap into a scratch buffer first — callers may re-read/reuse the source row buffer,
            // so it must not be mutated in place.
            var tmp = new byte[byteCount];
            Buffer.BlockCopy(src, srcOffset, tmp, 0, byteCount);
            for (int i = 0; i < byteCount; i += 4)
            {
                (tmp[i], tmp[i + 3]) = (tmp[i + 3], tmp[i]);
                (tmp[i + 1], tmp[i + 2]) = (tmp[i + 2], tmp[i + 1]);
            }
            Buffer.BlockCopy(tmp, 0, dest, destIndex * 4, byteCount);
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes, or throws.</summary>
        public static void ReadExact(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n == 0)
                    throw new EndOfStreamException("The data file is shorter than the header declares.");
                read += n;
            }
        }

        /// <summary>Skips <paramref name="count"/> bytes on a non-seekable stream (or seeks, when possible).</summary>
        public static void SkipBytes(Stream s, long count)
        {
            if (count <= 0) return;
            if (s.CanSeek) { s.Seek(count, SeekOrigin.Current); return; }

            var tmp = new byte[Math.Min(count, 65536)];
            while (count > 0)
            {
                int n = s.Read(tmp, 0, (int)Math.Min(count, tmp.Length));
                if (n == 0) throw new EndOfStreamException("Could not skip the declared HeaderOffset.");
                count -= n;
            }
        }
    }
}
