using System.Linq;
using Avalonia.Platform.Storage;
using static RasterField.L;

namespace RasterField
{
    /// <summary>
    /// The file types the Open/Save panels offer, in one place and in the interface language. The
    /// "(*.ers, *.tif)" part is generated from the patterns, so name and filter cannot disagree.
    /// </summary>
    internal static class FileTypes
    {
        private static FilePickerFileType Type(string englishName, params string[] extensions)
        {
            var patterns = extensions.Select(e => "*." + e).ToArray();
            return new FilePickerFileType(T(englishName) + " (" + string.Join(", ", patterns) + ")") { Patterns = patterns };
        }

        public static FilePickerFileType ErsHeader => Type("ER Mapper header", "ers");
        public static FilePickerFileType GeoTiff => Type("GeoTIFF raster", "tif", "tiff");
        public static FilePickerFileType Rasters => Type("Raster datasets", "ers", "tif", "tiff");
        public static FilePickerFileType ErvHeader => Type("ER Mapper vector header", "erv");
        public static FilePickerFileType GeoJson => Type("GeoJSON", "geojson", "json");
        public static FilePickerFileType CsvPoints => Type("CSV points", "csv", "txt");
        public static FilePickerFileType Csv => Type("CSV table", "csv");
        public static FilePickerFileType Png => Type("PNG image", "png");
        public static FilePickerFileType Pdf => Type("PDF document", "pdf");
        public static FilePickerFileType Project => Type("RasterField project", "rfproj");
        public static FilePickerFileType Palette => Type("DigiTerra palette", "pal");
        public static FilePickerFileType AnyLayer => Type("Layers", "ers", "tif", "tiff", "erv", "geojson", "json", "csv");

        /// <summary>File ▸ Open raster, Mosaic.</summary>
        public static FilePickerFileType[] OpenRaster => new[] { Rasters, ErsHeader, GeoTiff, FilePickerFileTypes.All };

        /// <summary>Add layer(s): every raster and vector format.</summary>
        public static FilePickerFileType[] OpenLayer => new[] { AnyLayer, Rasters, ErsHeader, GeoTiff, ErvHeader, GeoJson, CsvPoints, FilePickerFileTypes.All };

        /// <summary>Saving a raster: ER Mapper or GeoTIFF.</summary>
        public static FilePickerFileType[] SaveRaster => new[] { ErsHeader, GeoTiff };

        /// <summary>A single type, for a Save panel or an Open filter.</summary>
        public static FilePickerFileType[] Only(FilePickerFileType type) => new[] { type };
    }
}
