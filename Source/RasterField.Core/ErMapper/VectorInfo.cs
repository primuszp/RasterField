using System;
using System.Collections.Generic;
using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>The <c>Type</c> entry of a <c>VectorInfo</c> block.</summary>
    public enum ErvType
    {
        /// <summary>ER Mapper vector format.</summary>
        ErVec,
        /// <summary>Accepted for backward compatibility only.</summary>
        Ers,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>The <c>FileFormat</c> entry of a <c>VectorInfo</c> block.</summary>
    public enum ErvFileFormat
    {
        /// <summary>ASCII object list (the only format allowed for <see cref="ErvType.ErVec"/>).</summary>
        Ascii,
        /// <summary>Binary (only meaningful together with <see cref="ErvType.Ers"/>).</summary>
        Binary,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>
    /// The <c>Extents</c> sub-block of <c>VectorInfo</c>: the world-coordinate bounding box of
    /// the vector data, given as its top-left and bottom-right corners.
    /// </summary>
    public sealed class Extents
    {
        /// <summary>North-west corner of the dataset extent.</summary>
        public RegistrationCoord? TopLeftCorner { get; set; }

        /// <summary>South-east corner of the dataset extent.</summary>
        public RegistrationCoord? BottomRightCorner { get; set; }

        /// <summary>Builds a <see cref="Extents"/> from a parsed <c>Extents</c> block.</summary>
        public static Extents FromBlock(ErsBlock block)
        {
            var e = new Extents();
            var tl = block.Block("TopLeftCorner");
            if (tl != null) e.TopLeftCorner = RegistrationCoord.FromBlock(tl);
            var br = block.Block("BottomRightCorner");
            if (br != null) e.BottomRightCorner = RegistrationCoord.FromBlock(br);
            return e;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("Extents");
            TopLeftCorner?.Write(w, "TopLeftCorner");
            BottomRightCorner?.Write(w, "BottomRightCorner");
            w.EndBlock("Extents");
        }
    }

    /// <summary>
    /// The compulsory <c>VectorInfo</c> block of a vector dataset header (<c>.erv</c>): it takes
    /// the place of <c>RasterInfo</c> and declares the vector storage type, file format and the
    /// data's world-coordinate extents.
    /// </summary>
    public sealed class VectorInfo
    {
        /// <summary>The type of data in the dataset.</summary>
        public ErvType Type { get; set; } = ErvType.ErVec;

        /// <summary>The data file's format.</summary>
        public ErvFileFormat FileFormat { get; set; } = ErvFileFormat.Ascii;

        /// <summary>The dataset's world-coordinate bounding box, when known.</summary>
        public Extents? Extents { get; set; }

        /// <summary>The parsed <c>VectorInfo</c> block, kept so the loss-less writer can re-emit unmodelled entries.</summary>
        public ErsBlock? RawBlock { get; private set; }

        internal static readonly ISet<string> HandledEntries =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Type", "FileFormat" };

        internal static readonly ISet<string> HandledBlocks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Extents" };

        /// <summary>Builds a <see cref="VectorInfo"/> from a parsed <c>VectorInfo</c> block.</summary>
        public static VectorInfo FromBlock(ErsBlock block)
        {
            var vi = new VectorInfo { RawBlock = block };
            if (block.TryGet("Type", out var t))
                vi.Type = t.AsKeyword().Equals("ERVEC", StringComparison.OrdinalIgnoreCase) ? ErvType.ErVec
                    : t.AsKeyword().Equals("ERS", StringComparison.OrdinalIgnoreCase) ? ErvType.Ers
                    : ErvType.Unknown;
            if (block.TryGet("FileFormat", out var f))
                vi.FileFormat = f.AsKeyword().Equals("ASCII", StringComparison.OrdinalIgnoreCase) ? ErvFileFormat.Ascii
                    : f.AsKeyword().Equals("BINARY", StringComparison.OrdinalIgnoreCase) ? ErvFileFormat.Binary
                    : ErvFileFormat.Unknown;

            var extents = block.Block("Extents");
            if (extents != null) vi.Extents = Extents.FromBlock(extents);

            return vi;
        }

        internal void Write(ErsHeaderWriter w)
        {
            w.BeginBlock("VectorInfo");
            w.Keyword("Type", Type == ErvType.Ers ? "ERS" : "ERVEC");
            w.Keyword("FileFormat", FileFormat == ErvFileFormat.Binary ? "BINARY" : "ASCII");
            Extents?.Write(w);
            w.EmitUnhandled(RawBlock, HandledEntries, HandledBlocks);
            w.EndBlock("VectorInfo");
        }
    }
}
