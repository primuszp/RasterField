namespace RasterField.Tests
{
    /// <summary>Shared fixtures for the test suite.</summary>
    internal static class SampleData
    {
        /// <summary>The real <c>P_00_01.ers</c> header shipped with the repository.</summary>
        public const string P0001Ers =
            "DatasetHeader Begin\n" +
            "\tDataFile = \"P_00_01.dat\"\n" +
            "\tDataSetType = ERStorage\n" +
            "\tDataType = Raster\n" +
            "\tByteOrder = LSBFirst\n" +
            "\tCoordinateSpace Begin\n" +
            "\t\tDatum = \"EPSG:6237\"\n" +
            "\t\tProjection = \"BMG:EOV\"\n" +
            "\t\tCoordinateType = EN\n" +
            "\t\tRotation = 0:0:0.0\n" +
            "\tCoordinateSpace End\n" +
            "\tRasterInfo Begin\n" +
            "\t\tCellType = IEEE4ByteReal\n" +
            "\t\tCellInfo Begin\n" +
            "\t\t\tXdimension = 1000\n" +
            "\t\t\tYdimension = 1000\n" +
            "\t\tCellInfo End\n" +
            "\t\tNrOfLines = 450\n" +
            "\t\tNrOfCellsPerLine = 640\n" +
            "\t\tRegistrationCoord Begin\n" +
            "\t\t\tEastings = 360000\n" +
            "\t\t\tNorthings = 430000\n" +
            "\t\tRegistrationCoord End\n" +
            "\t\tNrOfBands\t= 1\n" +
            "\tRasterInfo End\n" +
            "DatasetHeader End\n";

        /// <summary>A tiny synthetic <c>.pal</c> body (grey ramp start, blue end).</summary>
        public const string TinyPal =
            "// DigiTerra Raster Palette, RGB order\n" +
            "0\t0\t0\n" +
            "10\t10\t10\n" +
            "20\t20\t20\n" +
            "0\t0\t255\n";
    }
}
