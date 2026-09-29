using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using RasterField.ErMapper;
using RasterField.Rasters;

namespace RasterField
{
    /// <summary>Options for <see cref="ErsDocument.Save(string, ErsSaveOptions)"/>.</summary>
    public sealed class ErsSaveOptions
    {
        /// <summary>Write the samples with this cell type instead of the current one (a format conversion).</summary>
        public ErsCellType? CellType { get; set; }

        /// <summary>Write the data file with this byte order instead of the current one.</summary>
        public ErsByteOrder? ByteOrder { get; set; }

        /// <summary>
        /// Name to record in the header's <c>DataFile</c> entry and to use for the data file on
        /// disk (relative to the <c>.ers</c> file). Defaults to the header file name without its
        /// extension.
        /// </summary>
        public string? DataFileName { get; set; }

        /// <summary>Keep any existing <see cref="ErsHeader.HeaderOffset"/> padding (default: clear it).</summary>
        public bool KeepHeaderOffset { get; set; }

        /// <summary>
        /// Set the header's <c>Name</c> entry to the saved <c>.ers</c> file name, as ER Mapper
        /// does whenever it edits a header. Default <see langword="true"/>.
        /// </summary>
        public bool UpdateName { get; set; } = true;

        /// <summary>
        /// Set the header's <c>LastUpdated</c> entry to the current UTC time. Default <see langword="true"/>.
        /// </summary>
        public bool UpdateTimestamp { get; set; } = true;
    }

    /// <summary>
    /// High-level entry point for an ER Mapper raster dataset: the parsed <see cref="ErsHeader"/>,
    /// the decoded band <see cref="Rasters.Raster"/>s and the <see cref="RasterGeoReference"/>
    /// that ties image cells to world coordinates. Supports both reading and writing.
    /// </summary>
    public sealed class ErsDocument
    {
        private readonly List<Raster> _bands = new List<Raster>();

        private ErsDocument(ErsHeader header, string? headerPath)
        {
            Header = header;
            HeaderPath = headerPath;
        }

        /// <summary>The dataset header (mutable so callers can adjust metadata before saving).</summary>
        public ErsHeader Header { get; }

        /// <summary>Path of the <c>.ers</c> file this document was loaded from / last saved to.</summary>
        public string? HeaderPath { get; private set; }

        /// <summary>Path of the binary data file, once known.</summary>
        public string? DataFilePath { get; private set; }

        /// <summary>The decoded bands.</summary>
        public IReadOnlyList<Raster> Bands => _bands;

        /// <summary>The first band, or <see langword="null"/> when no raster is loaded.</summary>
        public Raster? Band => _bands.Count > 0 ? _bands[0] : null;

        /// <summary>Image &#8596; world mapping derived from the current header.</summary>
        public RasterGeoReference GeoReference => RasterGeoReference.FromHeader(Header);

        /// <summary>
        /// Cell-count threshold above which <see cref="IsLargeDataset"/> reports
        /// <see langword="true"/>. The default (16 million cells, roughly 4000&#215;4000) is
        /// comfortably below what fits as a single in-memory <c>float[]</c> for any reasonable
        /// number of bands, while staying well inside typical desktop RAM.
        /// </summary>
        public static long LargeDatasetCellThreshold { get; set; } = 16_000_000;

        /// <summary>
        /// <see langword="true"/> when the header declares more cells than
        /// <see cref="LargeDatasetCellThreshold"/> — a hint that callers should avoid
        /// <see cref="LoadRaster()"/> (which reads the whole thing into memory) and instead use
        /// <see cref="OpenSource"/> for windowed access.
        /// </summary>
        public bool IsLargeDataset =>
            (long)Header.RasterInfo.NrOfCellsPerLine * Header.RasterInfo.NrOfLines > LargeDatasetCellThreshold;

        /// <summary>
        /// Opens a random-access <see cref="RasterSource"/> over this document's data file, for
        /// reading an arbitrary window (optionally decimated) without loading the whole raster —
        /// the recommended way to display or extract from a dataset where
        /// <see cref="IsLargeDataset"/> is <see langword="true"/>. The caller owns the returned
        /// instance and must dispose it.
        /// </summary>
        public RasterSource OpenSource()
        {
            if (HeaderPath == null)
                throw new InvalidOperationException("This document has no header path to resolve the data file from.");
            return RasterSource.Open(Header, HeaderPath);
        }

        // ---- construction ----------------------------------------------------------------

        /// <summary>Parses only the header of a <c>.ers</c> file.</summary>
        public static ErsDocument LoadHeaderOnly(string ersPath)
        {
            if (ersPath == null) throw new ArgumentNullException(nameof(ersPath));
            return new ErsDocument(ErsHeader.Load(ersPath), Path.GetFullPath(ersPath));
        }

        /// <summary>Parses a <c>.ers</c> file and loads every band of its data file.</summary>
        public static ErsDocument Load(string ersPath)
        {
            var doc = LoadHeaderOnly(ersPath);
            doc.LoadRaster();
            return doc;
        }

        /// <summary>Parses a header from a stream (no raster data).</summary>
        public static ErsDocument FromHeaderStream(Stream headerStream) =>
            new ErsDocument(ErsHeader.Parse(headerStream), null);

        /// <summary>Wraps in-memory bands and a header into a document ready to <see cref="Save(string)"/>.</summary>
        public static ErsDocument Create(ErsHeader header, IEnumerable<Raster> bands)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            var doc = new ErsDocument(header, null);
            doc._bands.AddRange(bands ?? throw new ArgumentNullException(nameof(bands)));
            doc.SyncHeaderToRasters();
            return doc;
        }

        /// <summary>
        /// Builds a brand new dataset for a single band with an axis-aligned georeference:
        /// cell (0,0) is the north-west corner at (<paramref name="originX"/>, <paramref name="originY"/>).
        /// </summary>
        public static ErsDocument Create(
            Raster band,
            double originX, double originY,
            double cellSizeX, double cellSizeY,
            ErsCellType cellType = ErsCellType.IEEE4ByteReal,
            ErsByteOrder byteOrder = ErsByteOrder.LsbFirst,
            string? projection = null, string? datum = null)
        {
            if (band == null) throw new ArgumentNullException(nameof(band));

            var header = new ErsHeader
            {
                DataSetType = ErsDataSetType.ErStorage,
                DataType = ErsDataType.Raster,
                ByteOrder = byteOrder,
                CoordinateSpace = new CoordinateSpace
                {
                    Datum = datum ?? "RAW",
                    Projection = projection ?? "RAW",
                    CoordinateType = projection == null ? ErsCoordinateType.Raw : ErsCoordinateType.En,
                    Rotation = Angle.Zero,
                },
                RasterInfo = new RasterInfo
                {
                    CellType = cellType,
                    NrOfLines = band.Height,
                    NrOfCellsPerLine = band.Width,
                    NrOfBands = 1,
                    CellInfo = new CellInfo { XDimension = cellSizeX, YDimension = cellSizeY },
                    RegistrationCellX = 0,
                    RegistrationCellY = 0,
                    RegistrationCoord = new RegistrationCoord { X = originX, Y = originY },
                },
            };
            if (!double.IsNaN(band.NoDataValue)) header.RasterInfo.NullCellValue = band.NoDataValue;

            return Create(header, new[] { band });
        }

        // ---- reading -------------------------------------------------------------------

        /// <summary>Loads every band of the data file that accompanies <see cref="HeaderPath"/>.</summary>
        public void LoadRaster()
        {
            if (HeaderPath == null)
                throw new InvalidOperationException("This document has no header path; use LoadRaster(Stream).");

            DataFilePath = Header.ResolveDataFilePath(HeaderPath);
            using var fs = new FileStream(DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            LoadRaster(fs);
        }

        /// <summary>Loads every band from an explicit data stream.</summary>
        public void LoadRaster(Stream dataStream)
        {
            _bands.Clear();
            _bands.AddRange(BilRasterReader.ReadAllBands(dataStream ?? throw new ArgumentNullException(nameof(dataStream)), Header));
        }

        // ---- writing ------------------------------------------------------------------

        /// <summary>Copies <see cref="Rasters.Raster"/> dimensions into the header's <c>RasterInfo</c>.</summary>
        public void SyncHeaderToRasters()
        {
            if (_bands.Count == 0) return;
            Header.RasterInfo.NrOfLines = _bands[0].Height;
            Header.RasterInfo.NrOfCellsPerLine = _bands[0].Width;
            Header.RasterInfo.NrOfBands = _bands.Count;
        }

        /// <summary>Replaces the bands and re-syncs the header.</summary>
        public void SetBands(IEnumerable<Raster> bands)
        {
            _bands.Clear();
            _bands.AddRange(bands ?? throw new ArgumentNullException(nameof(bands)));
            SyncHeaderToRasters();
        }

        /// <summary>Replaces a single band in place (e.g. after gap-filling), keeping its size and the header in sync.</summary>
        public void ReplaceBand(int index, Raster band)
        {
            if (band == null) throw new ArgumentNullException(nameof(band));
            if (index < 0 || index >= _bands.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (band.Width != _bands[index].Width || band.Height != _bands[index].Height)
                throw new ArgumentException("The replacement band must have the same dimensions as the one it replaces.", nameof(band));
            _bands[index] = band;
        }

        // ---- clipping / subsetting -----------------------------------------------------

        /// <summary>
        /// Creates a new, independent dataset containing only the <paramref name="width"/> &#215;
        /// <paramref name="height"/> pixel window starting at cell (<paramref name="x"/>,
        /// <paramref name="y"/>) of every band. The new document's georeference is re-anchored
        /// so the crop's cell (0, 0) sits at the same world location the original raster had at
        /// (<paramref name="x"/>, <paramref name="y"/>) — cell size and rotation are preserved.
        /// The window is clamped to the source image bounds.
        /// </summary>
        /// <remarks>
        /// Works whether or not <see cref="LoadRaster()"/> has been called: with bands already
        /// loaded it crops them in memory; for a streaming/large dataset (no bands loaded) it
        /// reads exactly the requested window straight from the data file via
        /// <see cref="OpenSource"/> — the whole raster is never materialised.
        /// </remarks>
        public ErsDocument Clip(int x, int y, int width, int height)
        {
            int totalWidth = Header.RasterInfo.NrOfCellsPerLine;
            int totalHeight = Header.RasterInfo.NrOfLines;
            if (totalWidth <= 0 || totalHeight <= 0)
                throw new InvalidOperationException("The header does not declare a raster size to clip.");
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");

            // Clamp the window to the image so callers can pass a generous rectangle (e.g. from
            // a mouse drag) without bounds-checking it themselves first.
            int x0 = Math.Max(0, Math.Min(x, totalWidth));
            int y0 = Math.Max(0, Math.Min(y, totalHeight));
            int x1 = Math.Max(x0, Math.Min(x + width, totalWidth));
            int y1 = Math.Max(y0, Math.Min(y + height, totalHeight));
            int cw = x1 - x0, ch = y1 - y0;
            if (cw <= 0 || ch <= 0)
                throw new ArgumentException("The requested window does not overlap the raster.");

            Raster[] croppedBands;
            if (_bands.Count > 0)
            {
                croppedBands = new Raster[_bands.Count];
                for (int i = 0; i < _bands.Count; i++)
                    croppedBands[i] = RasterClipper.Crop(_bands[i], x0, y0, cw, ch);
            }
            else
            {
                using var source = OpenSource();
                int bandCount = Math.Max(1, Header.RasterInfo.NrOfBands);
                croppedBands = new Raster[bandCount];
                for (int b = 0; b < bandCount; b++)
                    croppedBands[b] = source.ReadWindow(x0, y0, cw, ch, band: b);
            }

            var (originX, originY) = GeoReference.PixelToWorld(x0, y0);
            var newHeader = DeriveHeader(originX, originY, cw, ch, croppedBands.Length,
                Header.RasterInfo.CellInfo?.XDimension, Header.RasterInfo.CellInfo?.YDimension, Header.RasterInfo.CellType);
            return Create(newHeader, croppedBands);
        }

        /// <summary>
        /// A new header for a dataset derived from this one: same coordinate space (datum,
        /// projection, units, rotation), null value and band descriptions, but its own size, cell
        /// size and origin (the world position of its cell (0, 0) corner).
        /// </summary>
        private ErsHeader DeriveHeader(double originX, double originY, int width, int height, int bandCount,
            double? cellSizeX, double? cellSizeY, ErsCellType cellType)
        {
            var newHeader = new ErsHeader
            {
                DataSetType = Header.DataSetType,
                DataType = Header.DataType,
                ByteOrder = Header.ByteOrder,
                CoordinateSpace = new CoordinateSpace
                {
                    Datum = Header.CoordinateSpace.Datum,
                    Projection = Header.CoordinateSpace.Projection,
                    CoordinateType = Header.CoordinateSpace.CoordinateType,
                    CoordinateTypeRaw = Header.CoordinateSpace.CoordinateTypeRaw,
                    Units = Header.CoordinateSpace.Units,
                    Rotation = Header.CoordinateSpace.Rotation,
                },
                RasterInfo = new RasterInfo
                {
                    CellType = cellType,
                    NullCellValue = Header.RasterInfo.NullCellValue,
                    CellInfo = cellSizeX == null || cellSizeY == null ? null : new CellInfo
                    {
                        XDimension = cellSizeX.Value,
                        YDimension = cellSizeY.Value,
                    },
                    NrOfLines = height,
                    NrOfCellsPerLine = width,
                    NrOfBands = bandCount,
                    RegistrationCellX = 0,
                    RegistrationCellY = 0,
                    RegistrationCoord = new RegistrationCoord
                    {
                        X = originX,
                        Y = originY,
                        Kind = Header.RasterInfo.RegistrationCoord?.Kind ?? RegistrationCoordKind.EastingsNorthings,
                    },
                },
            };
            foreach (var band in Header.RasterInfo.Bands)
                newHeader.RasterInfo.Bands.Add(new BandInfo { Value = band.Value, Units = band.Units, Width = band.Width });
            return newHeader;
        }

        // ---- interpolation / subdivision -----------------------------------------------

        /// <summary>
        /// Creates a new dataset <see cref="BezierPatchOptions.Factor"/> times finer than this one,
        /// every band resampled with <see cref="BezierPatchInterpolator"/>. The extent, origin and
        /// rotation are unchanged; the cell size is divided by the factor. Integer cell types are
        /// promoted to <see cref="ErsCellType.IEEE4ByteReal"/> (an interpolated surface is not integral).
        /// </summary>
        /// <remarks>Works whether or not the bands are loaded (reads them from disk if not).</remarks>
        public ErsDocument Subdivide(BezierPatchOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            int w = Header.RasterInfo.NrOfCellsPerLine, h = Header.RasterInfo.NrOfLines;
            return Subdivide(0, 0, w, h, options, progress, cancellationToken);
        }

        /// <summary>
        /// As <see cref="Subdivide(BezierPatchOptions, IProgress{double}, CancellationToken)"/>, but only
        /// for the <paramref name="width"/> &#215; <paramref name="height"/> cell window at
        /// (<paramref name="x"/>, <paramref name="y"/>) (clamped to the image). Neighbouring cells just
        /// outside the window are still used, so the result joins seamlessly with the full surface.
        /// </summary>
        public ErsDocument Subdivide(int x, int y, int width, int height, BezierPatchOptions options,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();
            int totalWidth = Header.RasterInfo.NrOfCellsPerLine;
            int totalHeight = Header.RasterInfo.NrOfLines;
            int x0 = Math.Max(0, Math.Min(x, totalWidth));
            int y0 = Math.Max(0, Math.Min(y, totalHeight));
            int x1 = Math.Max(x0, Math.Min(x + width, totalWidth));
            int y1 = Math.Max(y0, Math.Min(y + height, totalHeight));
            if (x1 - x0 <= 0 || y1 - y0 <= 0) throw new ArgumentException("The requested window does not overlap the raster.");

            // Read the window plus up to 2 real neighbour cells per side (a patch needs a 4×4 stencil).
            int mx0 = Math.Max(0, x0 - 2), my0 = Math.Max(0, y0 - 2);
            int mx1 = Math.Min(totalWidth, x1 + 2), my1 = Math.Min(totalHeight, y1 + 2);
            int k = options.Factor;

            int bandCount = Math.Max(1, Header.RasterInfo.NrOfBands);
            var result = new Raster[bandCount];
            RasterSource? source = _bands.Count > 0 ? null : OpenSource();
            try
            {
                for (int b = 0; b < bandCount; b++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Raster window = source == null
                        ? RasterClipper.Crop(_bands[b], mx0, my0, mx1 - mx0, my1 - my0)
                        : source.ReadWindow(mx0, my0, mx1 - mx0, my1 - my0, band: b);
                    int band = b;
                    var bandProgress = progress == null ? null : new Progress<double>(p => progress.Report((band + p) / bandCount));
                    var fine = BezierPatchInterpolator.Subdivide(window, options, bandProgress, cancellationToken);
                    result[b] = RasterClipper.Crop(fine, (x0 - mx0) * k, (y0 - my0) * k, (x1 - x0) * k, (y1 - y0) * k);
                }
            }
            finally
            {
                source?.Dispose();
            }

            var (originX, originY) = GeoReference.PixelToWorld(x0, y0);
            var cellType = Header.RasterInfo.CellType == ErsCellType.IEEE8ByteReal ? ErsCellType.IEEE8ByteReal : ErsCellType.IEEE4ByteReal;
            var newHeader = DeriveHeader(originX, originY, (x1 - x0) * k, (y1 - y0) * k, bandCount,
                Header.RasterInfo.CellSizeX / k, Header.RasterInfo.CellSizeY / k, cellType);
            return Create(newHeader, result);
        }

        /// <summary>
        /// Creates a new, independent dataset covering the given world-coordinate extent
        /// (clamped to the source image). The extent's corners are mapped through the
        /// georeference (so this also works for a rotated image), then rounded outward to a
        /// whole-cell pixel window and passed to <see cref="Clip(int, int, int, int)"/>.
        /// </summary>
        public ErsDocument ClipToWorldExtent(double minX, double minY, double maxX, double maxY)
        {
            var geo = GeoReference;
            if (!geo.IsInvertible) throw new InvalidOperationException("The georeference is not invertible.");

            var corners = new[]
            {
                geo.WorldToPixel(minX, minY), geo.WorldToPixel(maxX, minY),
                geo.WorldToPixel(minX, maxY), geo.WorldToPixel(maxX, maxY),
            };

            double minCol = double.PositiveInfinity, minRow = double.PositiveInfinity;
            double maxCol = double.NegativeInfinity, maxRow = double.NegativeInfinity;
            foreach (var (col, row) in corners)
            {
                if (col < minCol) minCol = col;
                if (row < minRow) minRow = row;
                if (col > maxCol) maxCol = col;
                if (row > maxRow) maxRow = row;
            }

            int x = (int)Math.Floor(minCol);
            int y = (int)Math.Floor(minRow);
            int width = (int)Math.Ceiling(maxCol) - x;
            int height = (int)Math.Ceiling(maxRow) - y;
            return Clip(x, y, Math.Max(1, width), Math.Max(1, height));
        }

        // ---- mosaicking ----------------------------------------------------------------

        /// <summary>
        /// Merges several loaded documents' first band into one new dataset spanning their
        /// combined world extent, at the given output cell size. Each source is resampled
        /// through its own georeference (so differing rotations/registrations are all honoured),
        /// but the result itself is always axis-aligned.
        /// </summary>
        /// <remarks>
        /// Every input document must already have <see cref="Band"/> loaded (call
        /// <see cref="LoadRaster()"/> first). The new header borrows its coordinate system,
        /// cell type and null-cell value from the first document in <paramref name="documents"/>.
        /// </remarks>
        public static ErsDocument Mosaic(
            IReadOnlyList<ErsDocument> documents,
            double cellSizeX,
            double cellSizeY,
            MosaicOverlapMode overlapMode = MosaicOverlapMode.LastWins)
        {
            if (documents == null || documents.Count == 0) throw new ArgumentException("At least one document is required.", nameof(documents));

            var sources = new MosaicSource[documents.Count];
            for (int i = 0; i < documents.Count; i++)
            {
                var doc = documents[i];
                if (doc.Band == null)
                    throw new InvalidOperationException("Every document passed to Mosaic(...) must have its raster loaded (call LoadRaster() first).");
                sources[i] = new MosaicSource(doc.Band, doc.GeoReference);
            }

            var (merged, geo) = RasterMosaic.Merge(sources, cellSizeX, cellSizeY, overlapMode);

            var first = documents[0];
            var header = new ErsHeader
            {
                DataSetType = ErsDataSetType.ErStorage,
                DataType = ErsDataType.Raster,
                ByteOrder = first.Header.ByteOrder == ErsByteOrder.Unknown ? ErsByteOrder.LsbFirst : first.Header.ByteOrder,
                CoordinateSpace = new CoordinateSpace
                {
                    Datum = first.Header.CoordinateSpace.Datum,
                    Projection = first.Header.CoordinateSpace.Projection,
                    CoordinateType = first.Header.CoordinateSpace.CoordinateType,
                    CoordinateTypeRaw = first.Header.CoordinateSpace.CoordinateTypeRaw,
                    Units = first.Header.CoordinateSpace.Units,
                    Rotation = Angle.Zero,
                },
                RasterInfo = new RasterInfo
                {
                    CellType = first.Header.RasterInfo.CellType,
                    NullCellValue = double.IsNaN(merged.NoDataValue) ? (double?)null : merged.NoDataValue,
                    CellInfo = new CellInfo { XDimension = cellSizeX, YDimension = cellSizeY },
                    NrOfLines = merged.Height,
                    NrOfCellsPerLine = merged.Width,
                    NrOfBands = 1,
                    RegistrationCellX = 0,
                    RegistrationCellY = 0,
                    RegistrationCoord = new RegistrationCoord
                    {
                        X = geo.GeoTransform.A,
                        Y = geo.GeoTransform.D,
                        Kind = first.Header.RasterInfo.RegistrationCoord?.Kind ?? RegistrationCoordKind.EastingsNorthings,
                    },
                },
            };

            return Create(header, new[] { merged });
        }

        /// <summary>Writes just the <c>.ers</c> header text.</summary>
        public void SaveHeader(string ersPath) => SaveHeader(ersPath, new ErsSaveOptions());

        /// <summary>Writes just the <c>.ers</c> header text, applying the <c>UpdateName</c> / <c>UpdateTimestamp</c> options.</summary>
        public void SaveHeader(string ersPath, ErsSaveOptions options)
        {
            SyncHeaderToRasters();
            StampHeader(ersPath, options);
            File.WriteAllText(ersPath, Header.ToErsText(), new UTF8Encoding(false));
            HeaderPath = Path.GetFullPath(ersPath);
        }

        private void StampHeader(string ersPath, ErsSaveOptions options)
        {
            if (options.UpdateName) Header.Name = Path.GetFileName(ersPath);
            if (options.UpdateTimestamp) Header.LastUpdated = DateTime.UtcNow;
        }

        /// <summary>Writes the <c>.ers</c> header and the BIL data file next to it.</summary>
        public void Save(string ersPath) => Save(ersPath, new ErsSaveOptions());

        /// <summary>Writes the <c>.ers</c> header and the BIL data file, applying <paramref name="options"/>.</summary>
        public void Save(string ersPath, ErsSaveOptions options)
        {
            if (ersPath == null) throw new ArgumentNullException(nameof(ersPath));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (_bands.Count == 0)
                throw new InvalidOperationException(
                    "The document has no raster bands to write. Call LoadRaster() first — for a " +
                    "large (streaming) dataset, consider Clip(...) / ClipToWorldExtent(...) to " +
                    "extract a smaller region instead of loading the whole thing.");

            SyncHeaderToRasters();

            if (options.CellType.HasValue) Header.RasterInfo.CellType = options.CellType.Value;
            if (options.ByteOrder.HasValue) Header.ByteOrder = options.ByteOrder.Value;
            if (Header.ByteOrder == ErsByteOrder.Unknown) Header.ByteOrder = ErsByteOrder.LsbFirst;
            if (Header.DataSetType == ErsDataSetType.Unknown) Header.DataSetType = ErsDataSetType.ErStorage;
            if (Header.DataType == ErsDataType.Unknown) Header.DataType = ErsDataType.Raster;
            if (!options.KeepHeaderOffset) Header.HeaderOffset = 0;

            string dir = Path.GetDirectoryName(Path.GetFullPath(ersPath)) ?? ".";
            string dataName = options.DataFileName ?? Path.GetFileNameWithoutExtension(ersPath);
            Header.DataFile = dataName;
            StampHeader(ersPath, options);

            string dataPath = Path.Combine(dir, dataName);
            using (var fs = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None))
                BilRasterWriter.Write(fs, Header, _bands);

            File.WriteAllText(ersPath, Header.ToErsText(), new UTF8Encoding(false));

            HeaderPath = Path.GetFullPath(ersPath);
            DataFilePath = dataPath;
        }

        // ---- sampling ----------------------------------------------------------------

        /// <summary>
        /// Samples a band at a world coordinate (nearest cell). Returns <see langword="null"/>
        /// when the point is outside the image or the cell is no-data.
        /// </summary>
        public float? Sample(double worldX, double worldY, int band = 0)
        {
            if (band < 0 || band >= _bands.Count) return null;
            var geo = GeoReference;
            if (!geo.IsInvertible) return null;

            var cell = geo.WorldToCell(worldX, worldY);
            if (cell == null) return null;
            return _bands[band].GetValueOrNull(cell.Value.Row, cell.Value.Column);
        }

        /// <summary>Backwards-compatible name for <see cref="Sample(double, double, int)"/>, returning 0 for misses.</summary>
        [Obsolete("Use Sample(worldX, worldY) which returns null for no-data / out-of-range instead of 0.")]
        public double GetValue(double worldX, double worldY) => Sample(worldX, worldY) ?? 0.0;
    }
}
