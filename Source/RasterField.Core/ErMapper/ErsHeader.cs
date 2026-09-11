using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>
    /// Strongly typed view of an ER Mapper dataset header — a <c>.ers</c> raster header or a
    /// <c>.erv</c> vector header, which share the same <c>DatasetHeader</c> / <c>CoordinateSpace</c>
    /// structure and differ only in their compulsory sub-block: <see cref="RasterInfo"/> for
    /// raster data, <see cref="VectorInfo"/> for vector data.
    /// </summary>
    /// <remarks>
    /// The full parsed block tree is still available through <see cref="RawBlock"/> so that
    /// less common blocks (SensorInfo, WarpControl, RegionInfo, FFTInfo, ...) remain reachable.
    /// </remarks>
    public sealed class ErsHeader
    {
        /// <summary><c>Version</c> entry, e.g. <c>"7.2"</c>.</summary>
        public string? Version { get; set; }

        /// <summary><c>Name</c> entry: the name ER Mapper records for the header file.</summary>
        public string? Name { get; set; }

        /// <summary>
        /// <c>DataFile</c> entry: the name of the binary data file, when it differs from the
        /// header file name with the extension removed.
        /// </summary>
        public string? DataFile { get; set; }

        /// <summary><c>DataSetType</c> entry.</summary>
        public ErsDataSetType DataSetType { get; set; } = ErsDataSetType.Unknown;

        /// <summary><c>DataType</c> entry.</summary>
        public ErsDataType DataType { get; set; } = ErsDataType.Unknown;

        /// <summary><c>ByteOrder</c> entry.</summary>
        public ErsByteOrder ByteOrder { get; set; } = ErsByteOrder.Unknown;

        /// <summary>
        /// <c>HeaderOffset</c> entry: number of leading bytes to skip in the data file
        /// (used for non ER Mapper fixed-length-header BIL files). Zero when absent.
        /// </summary>
        public long HeaderOffset { get; set; }

        /// <summary><c>LastUpdated</c> entry, parsed best-effort to UTC.</summary>
        public DateTime? LastUpdated { get; set; }

        /// <summary><c>SenseDate</c> entry, parsed best-effort to UTC.</summary>
        public DateTime? SenseDate { get; set; }

        /// <summary><c>SensorName</c> entry.</summary>
        public string? SensorName { get; set; }

        /// <summary>
        /// <c>SourceDataset</c> entry: path/name of the dataset training regions were defined
        /// on (used when processing classified datasets).
        /// </summary>
        public string? SourceDataset { get; set; }

        /// <summary><c>Comments</c> entry, when present as a scalar.</summary>
        public string? Comments { get; set; }

        /// <summary><c>Description</c> entry, when present.</summary>
        public string? Description { get; set; }

        /// <summary>The compulsory <c>CoordinateSpace</c> block.</summary>
        public CoordinateSpace CoordinateSpace { get; set; } = new CoordinateSpace();

        /// <summary>The compulsory <c>RasterInfo</c> block for a raster (<c>DataType = Raster</c>) header.</summary>
        public RasterInfo RasterInfo { get; set; } = new RasterInfo();

        /// <summary>
        /// The compulsory <c>VectorInfo</c> block for a vector (<c>DataType = Vector</c>, <c>.erv</c>)
        /// header. <see langword="null"/> for a raster header. When set, <see cref="ToErsText"/>
        /// writes this instead of <see cref="RasterInfo"/> — a header carries one or the other, never both.
        /// </summary>
        public VectorInfo? VectorInfo { get; set; }

        /// <summary>Typed read-only view of the optional <c>FFTInfo</c> block, if present.</summary>
        public FftInfo? FftInfo { get; private set; }

        /// <summary>The parsed block tree the header was built from (the <c>DatasetHeader</c> block), if any.</summary>
        public ErsBlock? RawBlock { get; private set; }

        private static readonly ISet<string> HandledEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Version", "Name", "DataFile", "DataSetType", "DataType", "ByteOrder", "HeaderOffset",
            "LastUpdated", "SensorName", "SenseDate", "SourceDataset", "Comments", "Description",
        };

        private static readonly ISet<string> HandledBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CoordinateSpace", "RasterInfo", "VectorInfo",
        };

        /// <summary>Bytes per sample implied by <see cref="RasterInfo"/>.<see cref="RasterInfo.CellType"/>.</summary>
        public int BytesPerSample => RasterInfo.CellType.SizeInBytes();

        /// <summary>Parses a header from ER Mapper ASCII text.</summary>
        public static ErsHeader Parse(string text) => FromRoot(ErsTextParser.Parse(text));

        /// <summary>Parses a header from a stream (not disposed).</summary>
        public static ErsHeader Parse(Stream stream, Encoding? encoding = null) => FromRoot(ErsTextParser.Parse(stream, encoding));

        /// <summary>Loads and parses a header from a <c>.ers</c> file on disk.</summary>
        public static ErsHeader Load(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Parse(fs);
        }

        /// <summary>Builds a typed header from an already parsed block tree (root or <c>DatasetHeader</c> block).</summary>
        public static ErsHeader FromRoot(ErsBlock root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            ErsBlock header = string.Equals(root.Name, "DatasetHeader", StringComparison.OrdinalIgnoreCase)
                ? root
                : root.Block("DatasetHeader") ?? root.Block("DataSetHeader")
                  ?? throw new FormatException("No 'DatasetHeader' block found in the .ers text.");

            var h = new ErsHeader { RawBlock = header };

            if (header.TryGet("Version", out var v)) h.Version = v.AsString();
            if (header.TryGet("Name", out var n)) h.Name = n.AsString();
            if (header.TryGet("DataFile", out var df)) h.DataFile = df.AsString();
            if (header.TryGet("SensorName", out var sn)) h.SensorName = sn.AsString();
            if (header.TryGet("SourceDataset", out var srcds)) h.SourceDataset = srcds.AsString();
            if (header.TryGet("Comments", out var cmt)) h.Comments = cmt.AsString();
            if (header.TryGet("Description", out var desc)) h.Description = desc.AsString();
            if (header.TryGet("HeaderOffset", out var ho)) h.HeaderOffset = ho.AsInt64();
            if (header.TryGet("LastUpdated", out var lu)) h.LastUpdated = lu.AsDateTimeUtc();
            if (header.TryGet("SenseDate", out var sd)) h.SenseDate = sd.AsDateTimeUtc();

            if (header.TryGet("DataSetType", out var dst))
                h.DataSetType = dst.AsKeyword().Equals("ERStorage", StringComparison.OrdinalIgnoreCase) ? ErsDataSetType.ErStorage
                    : dst.AsKeyword().Equals("Translated", StringComparison.OrdinalIgnoreCase) ? ErsDataSetType.Translated
                    : ErsDataSetType.Unknown;

            if (header.TryGet("DataType", out var dt))
                h.DataType = dt.AsEnum(ErsDataType.Unknown);

            if (header.TryGet("ByteOrder", out var bo))
                h.ByteOrder = bo.AsKeyword().Equals("LSBFirst", StringComparison.OrdinalIgnoreCase) ? ErsByteOrder.LsbFirst
                    : bo.AsKeyword().Equals("MSBFirst", StringComparison.OrdinalIgnoreCase) ? ErsByteOrder.MsbFirst
                    : ErsByteOrder.Unknown;

            var csBlock = header.Block("CoordinateSpace");
            if (csBlock != null) h.CoordinateSpace = CoordinateSpace.FromBlock(csBlock);

            var riBlock = header.Block("RasterInfo");
            if (riBlock != null) h.RasterInfo = RasterInfo.FromBlock(riBlock);

            var viBlock = header.Block("VectorInfo");
            if (viBlock != null) h.VectorInfo = VectorInfo.FromBlock(viBlock);

            var fftBlock = header.Block("FFTInfo");
            if (fftBlock != null) h.FftInfo = FftInfo.FromBlock(fftBlock);

            return h;
        }

        /// <summary>
        /// Resolves the path of the binary data file that this header describes, given the
        /// path of the <c>.ers</c> file. Honours the optional <c>DataFile</c> entry and the
        /// ER Mapper convention that the data file is the header path with the extension removed.
        /// </summary>
        public string ResolveDataFilePath(string headerPath)
        {
            if (headerPath == null) throw new ArgumentNullException(nameof(headerPath));
            string dir = Path.GetDirectoryName(Path.GetFullPath(headerPath)) ?? ".";

            if (!string.IsNullOrEmpty(DataFile))
            {
                string df = DataFile!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                if (df.EndsWith(".", StringComparison.Ordinal)) df = df.Substring(0, df.Length - 1);
                string candidate = Path.IsPathRooted(df) ? df : Path.Combine(dir, df);
                if (File.Exists(candidate)) return candidate;
            }

            string stripped = Path.Combine(dir, Path.GetFileNameWithoutExtension(headerPath));
            if (File.Exists(stripped)) return stripped;

            // Common alternative: a sibling ".dat" file.
            string dat = Path.ChangeExtension(headerPath, ".dat");
            if (File.Exists(dat)) return dat;

            return stripped;
        }

        /// <summary>
        /// Serialises this header back to ER Mapper ASCII form. Blocks and entries the typed
        /// model does not cover (SourceDataset, Comments, SensorInfo, WarpControl, RegionInfo,
        /// FFTInfo, vendor extensions, …) are re-emitted verbatim from <see cref="RawBlock"/>,
        /// so a parse → write round-trip preserves their content. Comments (<c>#</c>) and the
        /// original entry ordering are not preserved.
        /// </summary>
        public string ToErsText()
        {
            var w = new ErsHeaderWriter();
            w.BeginBlock("DatasetHeader");

            if (Version != null) w.Quoted("Version", Version);
            if (Name != null) w.Quoted("Name", Name);
            if (DataFile != null) w.Quoted("DataFile", DataFile);
            if (SourceDataset != null) w.Quoted("SourceDataset", SourceDataset);
            if (Description != null) w.Quoted("Description", Description);
            if (Comments != null) w.Quoted("Comments", Comments);
            if (LastUpdated.HasValue) w.Raw("LastUpdated", FormatGmt(LastUpdated.Value));
            if (SensorName != null) w.Quoted("SensorName", SensorName);
            if (SenseDate.HasValue) w.Raw("SenseDate", FormatGmt(SenseDate.Value));

            if (DataSetType != ErsDataSetType.Unknown)
                w.Keyword("DataSetType", DataSetType == ErsDataSetType.ErStorage ? "ERStorage" : DataSetType.ToString());
            if (DataType != ErsDataType.Unknown)
                w.Keyword("DataType", DataType.ToString());
            if (ByteOrder != ErsByteOrder.Unknown)
                w.Keyword("ByteOrder", ByteOrder == ErsByteOrder.LsbFirst ? "LSBFirst" : "MSBFirst");
            if (HeaderOffset != 0) w.Number("HeaderOffset", HeaderOffset);

            CoordinateSpace.Write(w, RawBlock?.Block("CoordinateSpace"));
            if (VectorInfo != null) VectorInfo.Write(w); else RasterInfo.Write(w);

            w.EmitUnhandled(RawBlock, HandledEntries, HandledBlocks);
            w.EndBlock("DatasetHeader");
            return w.ToString();
        }

        /// <summary>Writes <see cref="ToErsText"/> to a file.</summary>
        public void Save(string path) => File.WriteAllText(path, ToErsText(), new UTF8Encoding(false));

        private static string FormatGmt(DateTime utc) =>
            utc.ToString("ddd MMM d HH:mm:ss 'GMT' yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}
