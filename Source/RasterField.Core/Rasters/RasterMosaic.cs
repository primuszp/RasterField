using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    /// <summary>How <see cref="RasterMosaic.Merge"/> resolves cells where more than one source contributes.</summary>
    public enum MosaicOverlapMode
    {
        /// <summary>The first source (in list order) that has data at a cell wins; later sources never overwrite it.</summary>
        FirstWins,
        /// <summary>The last source (in list order) that has data at a cell wins, overwriting earlier ones.</summary>
        LastWins,
        /// <summary>The mean of every contributing source's value at that cell.</summary>
        Average,
    }

    /// <summary>One input to <see cref="RasterMosaic.Merge"/>: a band plus the georeference that places it in world space.</summary>
    public sealed class MosaicSource
    {
        /// <summary>Pairs a band with the georeference that places it in world space.</summary>
        public MosaicSource(Raster raster, RasterGeoReference geoReference)
        {
            Raster = raster ?? throw new ArgumentNullException(nameof(raster));
            GeoReference = geoReference ?? throw new ArgumentNullException(nameof(geoReference));
            if (!geoReference.IsInvertible)
                throw new ArgumentException("The georeference must be invertible (non-degenerate cells).", nameof(geoReference));
        }

        /// <summary>The band's samples.</summary>
        public Raster Raster { get; }

        /// <summary>Where the band sits in world space.</summary>
        public RasterGeoReference GeoReference { get; }
    }

    /// <summary>
    /// Merges several overlapping or adjacent rasters (e.g. neighbouring tiles, or the same area
    /// from different flights) into one, over the union of their world extents.
    /// </summary>
    /// <remarks>
    /// The output grid is always axis-aligned, at the requested cell size — a source's own
    /// rotation is respected when sampling it (world coordinates are converted through its own
    /// georeference), but the merged result is not. Sampling is nearest-cell (no resampling
    /// filter), matching the source data's own resolution semantics.
    /// </remarks>
    public static class RasterMosaic
    {
        /// <summary>
        /// Merges <paramref name="sources"/> into a single raster covering their combined world
        /// extent, at the given output cell size. <paramref name="overlapMode"/> decides which
        /// value wins (or whether they're averaged) where more than one source covers a cell.
        /// </summary>
        public static (Raster Raster, RasterGeoReference GeoReference) Merge(
            IReadOnlyList<MosaicSource> sources,
            double cellSizeX,
            double cellSizeY,
            MosaicOverlapMode overlapMode = MosaicOverlapMode.LastWins,
            float? noDataValue = null)
        {
            if (sources == null || sources.Count == 0) throw new ArgumentException("At least one source is required.", nameof(sources));
            if (cellSizeX <= 0 || cellSizeY <= 0) throw new ArgumentOutOfRangeException(nameof(cellSizeX), "Cell sizes must be positive.");

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var src in sources)
            {
                var (sMinX, sMinY, sMaxX, sMaxY) = src.GeoReference.WorldBounds();
                if (sMinX < minX) minX = sMinX;
                if (sMinY < minY) minY = sMinY;
                if (sMaxX > maxX) maxX = sMaxX;
                if (sMaxY > maxY) maxY = sMaxY;
            }

            int width = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellSizeX));
            int height = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellSizeY));

            // North-west origin; rows increase southwards, same convention as every other ERS raster here.
            var outGeo = new RasterGeoReference(width, height, minX, cellSizeX, 0, maxY, 0, -cellSizeY);

            float noData = noDataValue ?? FirstRealNoData(sources);
            var output = new Raster(width, height, noData);

            if (overlapMode == MosaicOverlapMode.Average)
            {
                var sum = new double[width * height];
                var count = new int[width * height];

                foreach (var src in sources)
                    AccumulateAverage(src, outGeo, sum, count);

                for (int i = 0; i < sum.Length; i++)
                    output.Samples[i] = count[i] > 0 ? (float)(sum[i] / count[i]) : noData;
            }
            else
            {
                // Seed with NaN as an internal "not yet covered by any source" sentinel — the
                // Raster constructor only records noData as metadata, it doesn't pre-fill the
                // buffer with it, and a stray 0 must not be mistaken for "already written".
                var buffer = output.Samples;
                for (int i = 0; i < buffer.Length; i++) buffer[i] = float.NaN;

                // FirstWins: iterate sources in order, only filling still-empty cells.
                // LastWins: iterate sources in order, letting later ones overwrite.
                foreach (var src in sources)
                    Blit(src, outGeo, output, overwrite: overlapMode == MosaicOverlapMode.LastWins);

                // Cells no source ever covered: settle them on the declared no-data value.
                if (!float.IsNaN(noData))
                    for (int i = 0; i < buffer.Length; i++)
                        if (float.IsNaN(buffer[i])) buffer[i] = noData;
            }

            output.InvalidateStatistics();
            return (output, outGeo);
        }

        private static float FirstRealNoData(IReadOnlyList<MosaicSource> sources)
        {
            foreach (var src in sources)
                if (!double.IsNaN(src.Raster.NoDataValue)) return (float)src.Raster.NoDataValue;
            return float.NaN;
        }

        private static void Blit(MosaicSource src, RasterGeoReference outGeo, Raster output, bool overwrite)
        {
            var raster = src.Raster;
            var geo = src.GeoReference;

            for (int row = 0; row < output.Height; row++)
            {
                for (int col = 0; col < output.Width; col++)
                {
                    var (wx, wy) = outGeo.PixelToWorld(col + 0.5, row + 0.5);
                    var cell = geo.WorldToCell(wx, wy);
                    if (cell == null) continue;

                    float v = raster[cell.Value.Row, cell.Value.Column];
                    if (raster.IsNoData(v)) continue;

                    float existing = output[row, col];
                    if (overwrite || output.IsNoData(existing))
                        output.SetValueFast(row, col, v);
                }
            }
        }

        private static void AccumulateAverage(MosaicSource src, RasterGeoReference outGeo, double[] sum, int[] count)
        {
            var raster = src.Raster;
            var geo = src.GeoReference;
            int width = outGeo.Width, height = outGeo.Height;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    var (wx, wy) = outGeo.PixelToWorld(col + 0.5, row + 0.5);
                    var cell = geo.WorldToCell(wx, wy);
                    if (cell == null) continue;

                    float v = raster[cell.Value.Row, cell.Value.Column];
                    if (raster.IsNoData(v)) continue;

                    int i = row * width + col;
                    sum[i] += v;
                    count[i]++;
                }
            }
        }
    }
}
