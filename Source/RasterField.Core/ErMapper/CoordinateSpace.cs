using System;
using System.Collections.Generic;
using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>
    /// The <c>CoordinateSpace</c> block: datum, projection, coordinate convention,
    /// linear units and the rotation of the image from true north.
    /// </summary>
    /// <remarks>
    /// The <c>.ers</c> format stores the coordinate reference system only as opaque
    /// <see cref="Datum"/> / <see cref="Projection"/> name strings plus the
    /// <see cref="CoordinateType"/> convention — there are no projection parameters in the
    /// file (ER Mapper resolves the names through its <c>GDT_DATA</c> tables). The names are
    /// preserved verbatim on read and write; see <see cref="ProjectionRegistry"/> for a
    /// best-effort mapping of common names to EPSG codes.
    /// </remarks>
    public sealed class CoordinateSpace
    {
        /// <summary>Datum name, e.g. <c>"RAW"</c>, <c>"AGD66"</c> or <c>"EPSG:6237"</c>. May be <see langword="null"/>.</summary>
        public string? Datum { get; set; }

        /// <summary>Projection name, e.g. <c>"RAW"</c>, <c>"NUTM11"</c> or <c>"BMG:EOV"</c>. May be <see langword="null"/>.</summary>
        public string? Projection { get; set; }

        /// <summary>How registration coordinates are expressed (recognised keywords only).</summary>
        public ErsCoordinateType CoordinateType { get; set; } = ErsCoordinateType.None;

        /// <summary>
        /// The coordinate-convention keyword exactly as it appeared in the file (from either
        /// <c>CoordinateType</c> or the <c>CoordinateSystem</c> alias). Preserved so an
        /// unrecognised value round-trips unchanged.
        /// </summary>
        public string? CoordinateTypeRaw { get; set; }

        /// <summary>Linear units exactly as written, e.g. <c>"METERS"</c> or <c>"natural"</c>; <see langword="null"/> when absent.</summary>
        public string? Units { get; set; }

        /// <summary>
        /// The units to assume when <see cref="Units"/> is absent, per the specification:
        /// <c>"METERS"</c> for RAW coordinate spaces, otherwise <c>"natural"</c> (the natural
        /// units of the projection).
        /// </summary>
        public string EffectiveUnits =>
            !string.IsNullOrEmpty(Units) ? Units! : IsRaw ? "METERS" : "natural";

        /// <summary>Rotation of the image from true north, counter-clockwise. Defaults to zero.</summary>
        public Angle Rotation { get; set; } = Angle.Zero;

        /// <summary>
        /// Best-effort EPSG code for this coordinate space, or <see langword="null"/> when the
        /// <see cref="Projection"/> / <see cref="Datum"/> names are not recognised. See
        /// <see cref="ProjectionRegistry"/> — this performs no coordinate transformation.
        /// </summary>
        public int? EpsgCode => ProjectionRegistry.GetEpsg(Projection, Datum);

        /// <summary>Best-effort EPSG lookup; see <see cref="EpsgCode"/>.</summary>
        public bool TryGetEpsg(out int epsg) => ProjectionRegistry.TryGetEpsg(Projection, Datum, out epsg);

        /// <summary><see langword="true"/> for a raw / unprojected coordinate space.</summary>
        public bool IsRaw =>
            CoordinateType == ErsCoordinateType.Raw ||
            (CoordinateType == ErsCoordinateType.None &&
             (string.Equals(Projection, "RAW", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(Datum, "RAW", StringComparison.OrdinalIgnoreCase) ||
              (Projection == null && Datum == null)));

        /// <summary>Entry names this type serialises itself (used by the loss-less writer).</summary>
        internal static readonly ISet<string> HandledEntries =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Datum", "Projection", "CoordinateType", "CoordinateSystem", "Units", "Rotation" };

        /// <summary>Builds a <see cref="CoordinateSpace"/> from a parsed <c>CoordinateSpace</c> block.</summary>
        public static CoordinateSpace FromBlock(ErsBlock block)
        {
            var cs = new CoordinateSpace();
            if (block.TryGet("Datum", out var datum)) cs.Datum = datum.AsString();
            if (block.TryGet("Projection", out var proj)) cs.Projection = proj.AsString();
            if (block.TryGet("Units", out var units)) cs.Units = units.AsString();

            // "CoordinateType" is the documented name; "CoordinateSystem" appears as an alias.
            if (block.TryGet("CoordinateType", out var ct) || block.TryGet("CoordinateSystem", out ct))
            {
                cs.CoordinateTypeRaw = ct.AsKeyword();
                cs.CoordinateType = ct.AsEnum(ErsCoordinateType.None);
            }

            if (block.TryGet("Rotation", out var rot) && Angle.TryParse(rot.Raw, out var angle))
                cs.Rotation = angle;
            return cs;
        }

        /// <summary>Serialises the modelled entries, then any unmodelled ones from <paramref name="raw"/>.</summary>
        internal void Write(ErsHeaderWriter w, ErsBlock? raw = null)
        {
            w.BeginBlock("CoordinateSpace");
            if (Datum != null) w.Quoted("Datum", Datum);
            if (Projection != null) w.Quoted("Projection", Projection);

            if (CoordinateType != ErsCoordinateType.None)
                w.Keyword("CoordinateType", CoordinateType.ToString().ToUpperInvariant());
            else if (!string.IsNullOrEmpty(CoordinateTypeRaw))
                w.Keyword("CoordinateType", CoordinateTypeRaw!);

            if (Units != null) w.Quoted("Units", Units);
            w.Keyword("Rotation", Rotation.ToDmsString());

            w.EmitUnhandled(raw, HandledEntries, EmptyBlocks);
            w.EndBlock("CoordinateSpace");
        }

        private static readonly ISet<string> EmptyBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
