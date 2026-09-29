using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using OSGeo.GDAL;
using OSGeo.OSR;
using RasterField.ErMapper;
using RasterField.Rasters;

namespace RasterField.Gdal
{
    /// <summary>Opens and writes GeoTIFF datasets through the bundled GDAL runtime.</summary>
    public static class GeoTiffDataset
    {
        private static readonly Regex EpsgRegex = new Regex(
            @"(?:AUTHORITY|ID)\s*\[\s*""EPSG""\s*,\s*""?(\d+)""?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static (ErsDocument Document, GdalRasterSource? Source) Open(string path)
        {
            var source = new GdalRasterSource(path);
            try
            {
                var gt = source.GeoTransform;
                var geo = new RasterGeoReference(source.Width, source.Height, gt[0], gt[1], gt[2], gt[3], gt[4], gt[5]);
                double cellX = Math.Sqrt(gt[1] * gt[1] + gt[4] * gt[4]);
                double cellY = Math.Sqrt(gt[2] * gt[2] + gt[5] * gt[5]);
                double rotation = Math.Atan2(gt[4], gt[1]) * 180.0 / Math.PI;
                string projection = EpsgName(source.ProjectionWkt) ?? (string.IsNullOrWhiteSpace(source.ProjectionWkt) ? "RAW" : "WKT");

                var header = new ErsHeader
                {
                    Name = Path.GetFileName(path),
                    DataSetType = ErsDataSetType.Translated,
                    DataType = ErsDataType.Raster,
                    ByteOrder = BitConverter.IsLittleEndian ? ErsByteOrder.LsbFirst : ErsByteOrder.MsbFirst,
                    CoordinateSpace = new CoordinateSpace
                    {
                        Projection = projection,
                        Datum = projection == "RAW" ? "RAW" : null,
                        CoordinateType = projection == "EPSG:4326" ? ErsCoordinateType.Ll : projection == "RAW" ? ErsCoordinateType.Raw : ErsCoordinateType.En,
                        Rotation = new Angle(rotation),
                    },
                    RasterInfo = new RasterInfo
                    {
                        CellType = MapCellType(source.GetBandDataType(0)),
                        NrOfCellsPerLine = source.Width,
                        NrOfLines = source.Height,
                        NrOfBands = source.BandCount,
                        NullCellValue = NullIfNaN(source.GetNoDataValue(0)),
                        CellInfo = new CellInfo { XDimension = cellX, YDimension = cellY },
                        RegistrationCellX = 0,
                        RegistrationCellY = 0,
                        RegistrationCoord = new RegistrationCoord { X = gt[0], Y = gt[3] },
                    },
                };
                for (int band = 0; band < source.BandCount; band++)
                    header.RasterInfo.Bands.Add(new BandInfo { Value = $"Band {band + 1}", Units = source.GetBandUnit(band) });

                if ((long)source.Width * source.Height <= ErsDocument.LargeDatasetCellThreshold)
                {
                    var bands = new List<Raster>(source.BandCount);
                    for (int band = 0; band < source.BandCount; band++)
                        bands.Add(source.ReadWindow(0, 0, source.Width, source.Height, band: band));
                    var loaded = ErsDocument.CreateExternalSourceMetadata(header, path, geo, bands,
                        source.ProjectionWkt);
                    source.Dispose();
                    return (loaded, null);
                }

                return (ErsDocument.CreateExternalSourceMetadata(header, path, geo,
                    coordinateReferenceWkt: source.ProjectionWkt), source);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        public static void Save(ErsDocument document, string path)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (document.Bands.Count == 0)
                throw new InvalidOperationException("The document has no loaded bands to export.");

            GdalBootstrap.EnsureConfigured();
            Driver driver = OSGeo.GDAL.Gdal.GetDriverByName("GTiff") ?? throw new InvalidOperationException("The GDAL GeoTIFF driver is unavailable.");
            int width = document.Bands[0].Width, height = document.Bands[0].Height;
            string[] options = { "TILED=YES", "COMPRESS=DEFLATE", "PREDICTOR=3", "BIGTIFF=IF_SAFER" };
            using Dataset output = driver.Create(path, width, height, document.Bands.Count, DataType.GDT_Float32, options) ??
                throw new IOException("GDAL could not create the GeoTIFF dataset.");

            var gt = document.GeoReference.GeoTransform;
            output.SetGeoTransform(new[] { gt.A, gt.B, gt.C, gt.D, gt.E, gt.F });
            int? epsg = ProjectionRegistry.GetEpsg(document.Header.CoordinateSpace.Projection, document.Header.CoordinateSpace.Datum);
            if (epsg.HasValue)
            {
                using var spatialReference = new SpatialReference(null);
                if (spatialReference.ImportFromEPSG(epsg.Value) == 0)
                {
                    spatialReference.ExportToWkt(out string wkt, null);
                    output.SetProjection(wkt);
                }
            }
            else if (!string.IsNullOrWhiteSpace(document.CoordinateReferenceWkt))
            {
                output.SetProjection(document.CoordinateReferenceWkt);
            }

            for (int i = 0; i < document.Bands.Count; i++)
            {
                Raster raster = document.Bands[i];
                if (raster.Width != width || raster.Height != height)
                    throw new InvalidOperationException("All bands must have the same dimensions.");
                using Band band = output.GetRasterBand(i + 1);
                if (!double.IsNaN(raster.NoDataValue)) band.SetNoDataValue(raster.NoDataValue);
                string? unit = i < document.Header.RasterInfo.Bands.Count ? document.Header.RasterInfo.Bands[i].Units : null;
                if (!string.IsNullOrWhiteSpace(unit)) band.SetUnitType(unit);
                CPLErr error = band.WriteRaster(0, 0, width, height, raster.Samples, width, height, 0, 0);
                if (error != CPLErr.CE_None) throw new IOException($"GDAL failed to write band {i + 1} ({error}).");
                band.FlushCache();
            }
            output.FlushCache();
            document.RecordExternalSavePath(path);
        }

        private static string? EpsgName(string wkt)
        {
            if (string.IsNullOrWhiteSpace(wkt)) return null;
            MatchCollection matches = EpsgRegex.Matches(wkt);
            return matches.Count == 0 ? null : "EPSG:" + matches[matches.Count - 1].Groups[1].Value;
        }

        private static double? NullIfNaN(double value) => double.IsNaN(value) ? (double?)null : value;

        private static ErsCellType MapCellType(DataType type) => type switch
        {
            DataType.GDT_Byte => ErsCellType.Unsigned8BitInteger,
            DataType.GDT_Int8 => ErsCellType.Signed8BitInteger,
            DataType.GDT_UInt16 => ErsCellType.Unsigned16BitInteger,
            DataType.GDT_Int16 => ErsCellType.Signed16BitInteger,
            DataType.GDT_UInt32 => ErsCellType.Unsigned32BitInteger,
            DataType.GDT_Int32 => ErsCellType.Signed32BitInteger,
            DataType.GDT_Float64 => ErsCellType.IEEE8ByteReal,
            _ => ErsCellType.IEEE4ByteReal,
        };
    }
}
