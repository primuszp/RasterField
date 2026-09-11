using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>
    /// The <c>RegistrationCoord</c> block: the world location of the registration cell
    /// (<see cref="RasterInfo.RegistrationCellX"/> / <see cref="RasterInfo.RegistrationCellY"/>).
    /// The pair present depends on <see cref="CoordinateSpace.CoordinateType"/>:
    /// <c>Eastings/Northings</c>, <c>MetersX/MetersY</c> or <c>Latitude/Longitude</c>.
    /// </summary>
    public sealed class RegistrationCoord
    {
        /// <summary>Easting (or MetersX) of the registration cell.</summary>
        public double X { get; set; }

        /// <summary>Northing (or MetersY) of the registration cell.</summary>
        public double Y { get; set; }

        /// <summary>The concrete entry names that were present (for round-tripping and diagnostics).</summary>
        public RegistrationCoordKind Kind { get; set; } = RegistrationCoordKind.EastingsNorthings;

        /// <summary>Builds a <see cref="RegistrationCoord"/> from a parsed <c>RegistrationCoord</c> block.</summary>
        public static RegistrationCoord FromBlock(ErsBlock block)
        {
            var rc = new RegistrationCoord();
            if (block.TryGet("Eastings", out var e)) { rc.X = e.AsDouble(); rc.Kind = RegistrationCoordKind.EastingsNorthings; }
            else if (block.TryGet("MetersX", out var mx)) { rc.X = mx.AsDouble(); rc.Kind = RegistrationCoordKind.Meters; }
            else if (block.TryGet("Longitude", out var lon)) { rc.X = Angle.Parse(lon.Raw).Degrees; rc.Kind = RegistrationCoordKind.LatitudeLongitude; }

            if (block.TryGet("Northings", out var n)) rc.Y = n.AsDouble();
            else if (block.TryGet("MetersY", out var my)) rc.Y = my.AsDouble();
            else if (block.TryGet("Latitude", out var lat)) rc.Y = Angle.Parse(lat.Raw).Degrees;

            return rc;
        }

        internal void Write(ErsHeaderWriter w) => Write(w, "RegistrationCoord");

        /// <summary>
        /// Serialises this coordinate pair under an arbitrary block name — the same entry
        /// shape is reused for <c>RegistrationCoord</c> and for the vector <c>Extents</c>
        /// block's <c>TopLeftCorner</c> / <c>BottomRightCorner</c> sub-blocks.
        /// </summary>
        internal void Write(ErsHeaderWriter w, string blockName)
        {
            w.BeginBlock(blockName);
            switch (Kind)
            {
                case RegistrationCoordKind.Meters:
                    w.Number("MetersX", X);
                    w.Number("MetersY", Y);
                    break;
                case RegistrationCoordKind.LatitudeLongitude:
                    w.Keyword("Latitude", new Angle(Y).ToDmsString());
                    w.Keyword("Longitude", new Angle(X).ToDmsString());
                    break;
                default:
                    w.Number("Eastings", X);
                    w.Number("Northings", Y);
                    break;
            }
            w.EndBlock(blockName);
        }
    }

    /// <summary>Which coordinate entry names a <see cref="RegistrationCoord"/> was expressed with.</summary>
    public enum RegistrationCoordKind
    {
        /// <summary><c>Eastings</c> / <c>Northings</c>.</summary>
        EastingsNorthings,
        /// <summary><c>MetersX</c> / <c>MetersY</c>.</summary>
        Meters,
        /// <summary><c>Latitude</c> / <c>Longitude</c>.</summary>
        LatitudeLongitude,
    }
}
