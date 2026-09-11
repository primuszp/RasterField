namespace RasterField.ErMapper
{
    /// <summary>Byte ordering of the samples in the binary data file (<c>ByteOrder</c> entry).</summary>
    public enum ErsByteOrder
    {
        /// <summary>Least significant byte first (little-endian, Intel).</summary>
        LsbFirst,
        /// <summary>Most significant byte first (big-endian, Motorola). This is ER Mapper's default.</summary>
        MsbFirst,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>Storage flavour of the dataset (<c>DataSetType</c> entry).</summary>
    public enum ErsDataSetType
    {
        /// <summary>Native ER Mapper storage.</summary>
        ErStorage,
        /// <summary>An external (non ER Mapper) format described by an ER Mapper header.</summary>
        Translated,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>Kind of data the header describes (<c>DataType</c> entry).</summary>
    public enum ErsDataType
    {
        /// <summary>Raster imagery.</summary>
        Raster,
        /// <summary>Vector data (described by a <c>.erv</c> header).</summary>
        Vector,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>
    /// How registration coordinates are expressed (<c>CoordinateType</c> entry).
    /// </summary>
    public enum ErsCoordinateType
    {
        /// <summary>Raw / master coordinates expressed in metres (no projection).</summary>
        Raw,
        /// <summary>Easting / Northing pair.</summary>
        En,
        /// <summary>Latitude / Longitude in degrees.</summary>
        Ll,
        /// <summary>Not specified.</summary>
        None,
    }

    /// <summary>
    /// The numeric type of a raster cell (<c>CellType</c> entry). Names match the ER Mapper keywords.
    /// </summary>
    public enum ErsCellType
    {
        /// <summary>8-bit unsigned integer.</summary>
        Unsigned8BitInteger,
        /// <summary>8-bit signed integer.</summary>
        Signed8BitInteger,
        /// <summary>16-bit unsigned integer.</summary>
        Unsigned16BitInteger,
        /// <summary>16-bit signed integer.</summary>
        Signed16BitInteger,
        /// <summary>32-bit unsigned integer.</summary>
        Unsigned32BitInteger,
        /// <summary>32-bit signed integer.</summary>
        Signed32BitInteger,
        /// <summary>IEEE 754 single-precision (4 byte) real.</summary>
        IEEE4ByteReal,
        /// <summary>IEEE 754 double-precision (8 byte) real.</summary>
        IEEE8ByteReal,
        /// <summary>Not specified / not recognised.</summary>
        Unknown,
    }

    /// <summary>Helpers for <see cref="ErsCellType"/>.</summary>
    public static class ErsCellTypeExtensions
    {
        /// <summary>Size, in bytes, of one sample of the given cell type.</summary>
        public static int SizeInBytes(this ErsCellType type) => type switch
        {
            ErsCellType.Unsigned8BitInteger => 1,
            ErsCellType.Signed8BitInteger => 1,
            ErsCellType.Unsigned16BitInteger => 2,
            ErsCellType.Signed16BitInteger => 2,
            ErsCellType.Unsigned32BitInteger => 4,
            ErsCellType.Signed32BitInteger => 4,
            ErsCellType.IEEE4ByteReal => 4,
            ErsCellType.IEEE8ByteReal => 8,
            _ => 0,
        };

        /// <summary><see langword="true"/> for the two IEEE floating point cell types.</summary>
        public static bool IsFloatingPoint(this ErsCellType type) =>
            type == ErsCellType.IEEE4ByteReal || type == ErsCellType.IEEE8ByteReal;
    }
}
