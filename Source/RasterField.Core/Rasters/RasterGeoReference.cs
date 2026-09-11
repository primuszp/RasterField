using System;
using RasterField.ErMapper;

namespace RasterField.Rasters
{
    /// <summary>
    /// Maps between image (column, row) coordinates and world coordinates for an ER Mapper
    /// raster, following the <c>RegistrationCoord</c> / <c>RegistrationCell</c> / <c>CellInfo</c>
    /// / <c>Rotation</c> semantics from the header specification.
    /// </summary>
    /// <remarks>
    /// Column indices increase eastwards; row indices increase southwards (line 0 is the
    /// northern edge). A pixel coordinate whose components are whole numbers denotes a cell
    /// <i>corner</i>; add 0.5 to address a cell <i>centre</i>. The registration cell defaults
    /// to (0, 0) — the top-left corner of the image.
    /// <para>
    /// The transform is stored as an affine "geo-transform" (the same six coefficients used by
    /// world files and GDAL): <c>worldX = A + col·B + row·C</c>, <c>worldY = D + col·E + row·F</c>.
    /// </para>
    /// </remarks>
    public sealed class RasterGeoReference
    {
        private readonly double _a, _b, _c, _d, _e, _f;
        private readonly double _inv00, _inv01, _inv10, _inv11; // inverse of [[B,C],[E,F]]
        private readonly bool _invertible;

        /// <summary>Builds a georeference from explicit affine coefficients.</summary>
        public RasterGeoReference(int width, int height, double a, double b, double c, double d, double e, double f)
        {
            Width = width;
            Height = height;
            _a = a; _b = b; _c = c; _d = d; _e = e; _f = f;

            double det = b * f - c * e;
            _invertible = Math.Abs(det) > 1e-15;
            if (_invertible)
            {
                _inv00 = f / det;
                _inv01 = -c / det;
                _inv10 = -e / det;
                _inv11 = b / det;
            }
        }

        /// <summary>Image width in cells.</summary>
        public int Width { get; }

        /// <summary>Image height in cells.</summary>
        public int Height { get; }

        /// <summary>The affine coefficients <c>(A, B, C, D, E, F)</c>.</summary>
        public (double A, double B, double C, double D, double E, double F) GeoTransform => (_a, _b, _c, _d, _e, _f);

        /// <summary><see langword="true"/> when world→pixel mapping is possible (non-degenerate cells).</summary>
        public bool IsInvertible => _invertible;

        /// <summary>Builds a georeference from a parsed <see cref="RasterInfo"/> and a rotation angle.</summary>
        public static RasterGeoReference FromRasterInfo(RasterInfo info, Angle rotation)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            double sx = info.CellSizeX;
            double sy = info.CellSizeY;
            double regCol = info.RegistrationCellX;
            double regRow = info.RegistrationCellY;
            double regX = info.RegistrationCoord?.X ?? 0.0;
            double regY = info.RegistrationCoord?.Y ?? 0.0;

            double theta = rotation.Radians;
            double cos = Math.Cos(theta);
            double sin = Math.Sin(theta);

            // local frame: x_local = (col-regCol)*sx  (east),  y_local = -(row-regRow)*sy  (north)
            // world = reg + R(theta) * (x_local, y_local)
            double b = sx * cos;          // d worldX / d col
            double c = sy * sin;          // d worldX / d row   (= -(-sy)*(-sin)) -> sy*sin
            double e = sx * sin;          // d worldY / d col
            double f = -sy * cos;         // d worldY / d row

            double a = regX - (regCol * b + regRow * c);
            double d = regY - (regCol * e + regRow * f);

            return new RasterGeoReference(info.NrOfCellsPerLine, info.NrOfLines, a, b, c, d, e, f);
        }

        /// <summary>Builds a georeference straight from a typed <see cref="ErsHeader"/>.</summary>
        public static RasterGeoReference FromHeader(ErsHeader header)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            return FromRasterInfo(header.RasterInfo, header.CoordinateSpace.Rotation);
        }

        /// <summary>Transforms an image coordinate (column, row) to world coordinates.</summary>
        public (double X, double Y) PixelToWorld(double column, double row) =>
            (_a + column * _b + row * _c, _d + column * _e + row * _f);

        /// <summary>Transforms the centre of cell (col, row) to world coordinates.</summary>
        public (double X, double Y) CellCentreToWorld(int column, int row) =>
            PixelToWorld(column + 0.5, row + 0.5);

        /// <summary>Transforms a world coordinate to a (possibly fractional) image coordinate.</summary>
        public (double Column, double Row) WorldToPixel(double x, double y)
        {
            if (!_invertible) throw new InvalidOperationException("This georeference is not invertible.");
            double dx = x - _a;
            double dy = y - _d;
            return (dx * _inv00 + dy * _inv01, dx * _inv10 + dy * _inv11);
        }

        /// <summary>Returns the integer cell that contains the given world coordinate, or <see langword="null"/> when outside the image.</summary>
        public (int Column, int Row)? WorldToCell(double x, double y)
        {
            var (col, row) = WorldToPixel(x, y);
            int c = (int)Math.Floor(col);
            int r = (int)Math.Floor(row);
            if ((uint)c >= (uint)Width || (uint)r >= (uint)Height) return null;
            return (c, r);
        }

        /// <summary>Axis-aligned world bounding box of the whole image (accounts for rotation).</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) WorldBounds()
        {
            var corners = new[]
            {
                PixelToWorld(0, 0),
                PixelToWorld(Width, 0),
                PixelToWorld(Width, Height),
                PixelToWorld(0, Height),
            };

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var (x, y) in corners)
            {
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
            return (minX, minY, maxX, maxY);
        }
    }
}
