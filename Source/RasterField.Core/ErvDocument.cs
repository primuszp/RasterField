using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RasterField.ErMapper;
using RasterField.Vectors;

namespace RasterField
{
    /// <summary>
    /// High-level entry point for an ER Mapper vector dataset (<c>.erv</c> header + ASCII object
    /// list data file) — the vector counterpart of <see cref="ErsDocument"/>. Supports both
    /// reading and writing.
    /// </summary>
    public sealed class ErvDocument
    {
        private readonly List<VectorObject> _objects = new List<VectorObject>();

        private ErvDocument(ErsHeader header, string? headerPath)
        {
            Header = header;
            HeaderPath = headerPath;
        }

        /// <summary>The dataset header (mutable so callers can adjust metadata before saving).</summary>
        public ErsHeader Header { get; }

        /// <summary>Path of the <c>.erv</c> file this document was loaded from / last saved to.</summary>
        public string? HeaderPath { get; private set; }

        /// <summary>Path of the ASCII object-list data file, once known.</summary>
        public string? DataFilePath { get; private set; }

        /// <summary>The vector objects, in file order.</summary>
        public IList<VectorObject> Objects => _objects;

        // ---- construction ----------------------------------------------------------------

        /// <summary>Parses only the header of a <c>.erv</c> file.</summary>
        public static ErvDocument LoadHeaderOnly(string ervPath)
        {
            if (ervPath == null) throw new ArgumentNullException(nameof(ervPath));
            var header = ErsHeader.Load(ervPath);
            if (header.VectorInfo == null)
                header.VectorInfo = new VectorInfo(); // tolerate a header that omitted VectorInfo
            return new ErvDocument(header, Path.GetFullPath(ervPath));
        }

        /// <summary>Parses a <c>.erv</c> file and loads every object of its data file.</summary>
        public static ErvDocument Load(string ervPath)
        {
            var doc = LoadHeaderOnly(ervPath);
            doc.LoadObjects();
            return doc;
        }

        /// <summary>Wraps in-memory objects and a header into a document ready to <see cref="Save(string)"/>.</summary>
        public static ErvDocument Create(ErsHeader header, IEnumerable<VectorObject>? objects = null)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (header.VectorInfo == null) header.VectorInfo = new VectorInfo();
            var doc = new ErvDocument(header, null);
            if (objects != null) doc._objects.AddRange(objects);
            return doc;
        }

        /// <summary>Builds a brand new vector dataset with a plain <c>CoordinateSpace</c>.</summary>
        public static ErvDocument Create(string? projection = null, string? datum = null, ErsByteOrder byteOrder = ErsByteOrder.MsbFirst)
        {
            var header = new ErsHeader
            {
                DataSetType = ErsDataSetType.ErStorage,
                DataType = ErsDataType.Vector,
                ByteOrder = byteOrder,
                CoordinateSpace = new CoordinateSpace
                {
                    Datum = datum ?? "RAW",
                    Projection = projection ?? "RAW",
                    CoordinateType = projection == null ? ErsCoordinateType.Raw : ErsCoordinateType.En,
                    Rotation = Angle.Zero,
                },
                VectorInfo = new VectorInfo(),
            };
            return Create(header);
        }

        // ---- reading -------------------------------------------------------------------

        /// <summary>Loads every object of the data file that accompanies <see cref="HeaderPath"/>.</summary>
        public void LoadObjects()
        {
            if (HeaderPath == null)
                throw new InvalidOperationException("This document has no header path; use LoadObjects(TextReader).");

            DataFilePath = Header.ResolveDataFilePath(HeaderPath);
            using var reader = new StreamReader(DataFilePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            LoadObjects(reader);
        }

        /// <summary>Loads every object from an explicit text reader.</summary>
        public void LoadObjects(TextReader reader)
        {
            _objects.Clear();
            _objects.AddRange(VectorDataReader.ReadObjects(reader ?? throw new ArgumentNullException(nameof(reader))));
        }

        // ---- writing ------------------------------------------------------------------

        /// <summary>Writes the <c>.erv</c> header and the ASCII object-list data file next to it.</summary>
        public void Save(string ervPath) => Save(ervPath, new ErsSaveOptions());

        /// <summary>Writes the <c>.erv</c> header and data file, applying <paramref name="options"/> (the same options type used by <see cref="ErsDocument"/>; <c>CellType</c> is ignored).</summary>
        public void Save(string ervPath, ErsSaveOptions options)
        {
            if (ervPath == null) throw new ArgumentNullException(nameof(ervPath));
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (Header.DataSetType == ErsDataSetType.Unknown) Header.DataSetType = ErsDataSetType.ErStorage;
            Header.DataType = ErsDataType.Vector;
            if (Header.ByteOrder == ErsByteOrder.Unknown) Header.ByteOrder = ErsByteOrder.MsbFirst;
            if (Header.VectorInfo == null) Header.VectorInfo = new VectorInfo();
            if (Header.VectorInfo.Extents == null) Header.VectorInfo.Extents = ComputeExtents();

            string dir = Path.GetDirectoryName(Path.GetFullPath(ervPath)) ?? ".";
            string dataName = options.DataFileName ?? Path.GetFileNameWithoutExtension(ervPath);
            Header.DataFile = dataName;
            if (options.UpdateName) Header.Name = Path.GetFileName(ervPath);
            if (options.UpdateTimestamp) Header.LastUpdated = DateTime.UtcNow;

            string dataPath = Path.Combine(dir, dataName);
            using (var writer = new StreamWriter(dataPath, append: false, new UTF8Encoding(false)))
                VectorDataWriter.Write(writer, _objects);

            File.WriteAllText(ervPath, Header.ToErsText(), new UTF8Encoding(false));

            HeaderPath = Path.GetFullPath(ervPath);
            DataFilePath = dataPath;
        }

        /// <summary>
        /// Best-effort world-coordinate bounding box over every coordinate this document's
        /// objects carry (point/box/oval/text anchors and polyline/polygon vertices). Treats
        /// page-relative objects the same as image-space ones; set
        /// <see cref="VectorInfo.Extents"/> explicitly if that distinction matters to you.
        /// </summary>
        public Extents? ComputeExtents()
        {
            bool any = false;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

            void Consider(double x, double y)
            {
                any = true;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }

            foreach (var obj in _objects)
            {
                switch (obj)
                {
                    case VectorPoint p: Consider(p.X, p.Y); break;
                    case VectorRectangleObject r: Consider(r.Ltx, r.Lty); Consider(r.Rbx, r.Rby); break;
                    case VectorPolyObject poly: foreach (var (x, y) in poly.Points) Consider(x, y); break;
                    case VectorVariableText vt: Consider(vt.Ltx, vt.Lty); Consider(vt.Rbx, vt.Rby); break;
                    case VectorTextObject t: Consider(t.X, t.Y); break;
                }
            }

            if (!any) return null;

            var kind = Header.CoordinateSpace.CoordinateType == ErsCoordinateType.Ll
                ? RegistrationCoordKind.LatitudeLongitude
                : Header.CoordinateSpace.IsRaw ? RegistrationCoordKind.Meters : RegistrationCoordKind.EastingsNorthings;

            return new Extents
            {
                TopLeftCorner = new RegistrationCoord { X = minX, Y = maxY, Kind = kind },
                BottomRightCorner = new RegistrationCoord { X = maxX, Y = minY, Kind = kind },
            };
        }
    }
}
