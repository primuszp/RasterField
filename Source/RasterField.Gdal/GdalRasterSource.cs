using System;
using System.IO;
using OSGeo.GDAL;
using RasterField.Rasters;

namespace RasterField.Gdal
{
    /// <summary>Windowed raster source backed by GDAL (used primarily for GeoTIFF).</summary>
    public sealed class GdalRasterSource : IRasterSource
    {
        private readonly Dataset _dataset;
        private readonly object _gate = new object();
        private bool _disposed;

        public GdalRasterSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("Raster dataset not found.", path);

            GdalBootstrap.EnsureConfigured();
            _dataset = OSGeo.GDAL.Gdal.Open(path, Access.GA_ReadOnly) ??
                throw new InvalidDataException($"GDAL could not open '{path}'.");
            Width = _dataset.RasterXSize;
            Height = _dataset.RasterYSize;
            BandCount = _dataset.RasterCount;
            if (Width <= 0 || Height <= 0 || BandCount <= 0)
                throw new InvalidDataException("The dataset has no readable raster bands.");

            GeoTransform = new double[6];
            _dataset.GetGeoTransform(GeoTransform);
            ProjectionWkt = _dataset.GetProjectionRef() ?? string.Empty;
        }

        public int Width { get; }
        public int Height { get; }
        public int BandCount { get; }
        public double[] GeoTransform { get; }
        public string ProjectionWkt { get; }

        public DataType GetBandDataType(int band)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ValidateBand(band);
                using Band rasterBand = _dataset.GetRasterBand(band + 1);
                return rasterBand.DataType;
            }
        }

        public double GetNoDataValue(int band)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ValidateBand(band);
                using Band rasterBand = _dataset.GetRasterBand(band + 1);
                rasterBand.GetNoDataValue(out double value, out int hasValue);
                return hasValue != 0 ? value : double.NaN;
            }
        }

        public string? GetBandUnit(int band)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ValidateBand(band);
                using Band rasterBand = _dataset.GetRasterBand(band + 1);
                string unit = rasterBand.GetUnitType();
                return string.IsNullOrWhiteSpace(unit) ? null : unit;
            }
        }

        public Raster ReadWindow(int x, int y, int width, int height, int stepX = 1, int stepY = 1, int band = 0)
        {
            if (stepX < 1) throw new ArgumentOutOfRangeException(nameof(stepX));
            if (stepY < 1) throw new ArgumentOutOfRangeException(nameof(stepY));
            ValidateBand(band);

            var window = RasterReadWindow.Create(Width, Height, x, y, width, height, stepX, stepY);
            int x0 = window.X, y0 = window.Y, sourceWidth = window.Width, sourceHeight = window.Height;
            int outputWidth = window.OutputWidth, outputHeight = window.OutputHeight;
            if (outputWidth == 0 || outputHeight == 0) return new Raster(0, 0, GetNoDataValue(band));
            var samples = new float[checked(outputWidth * outputHeight)];
            double noData;

            lock (_gate)
            {
                ThrowIfDisposed();
                using Band rasterBand = _dataset.GetRasterBand(band + 1);
                rasterBand.GetNoDataValue(out noData, out int hasNoData);
                if (hasNoData == 0) noData = double.NaN;
                CPLErr error = rasterBand.ReadRaster(x0, y0, sourceWidth, sourceHeight,
                    samples, outputWidth, outputHeight, 0, 0);
                if (error != CPLErr.CE_None)
                    throw new IOException($"GDAL failed to read raster window ({error}).");
            }
            return new Raster(outputWidth, outputHeight, samples, noData);
        }

        public Raster ReadOverview(int maxWidth, int maxHeight, int band = 0)
        {
            int step = RasterReadWindow.OverviewStep(Width, Height, maxWidth, maxHeight);
            return ReadWindow(0, 0, Width, Height, step, step, band);
        }

        private void ValidateBand(int band)
        {
            if (band < 0 || band >= BandCount) throw new ArgumentOutOfRangeException(nameof(band));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GdalRasterSource));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _dataset.Dispose();
            }
        }
    }
}
