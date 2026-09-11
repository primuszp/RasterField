using System;
using System.Collections.Generic;
using System.Globalization;

namespace RasterField.ErMapper
{
    /// <summary>
    /// Best-effort mapping of the opaque <c>Projection</c> / <c>Datum</c> name strings found in
    /// <c>.ers</c> headers onto EPSG codes.
    /// </summary>
    /// <remarks>
    /// The <c>.ers</c> format carries no projection parameters — only names that ER Mapper
    /// resolves through its <c>GDT_DATA</c> tables. This registry recognises the common forms
    /// (an <c>EPSG:xxxx</c> or bare numeric name, UTM zones, Australian AMG/MGA zones, Web
    /// Mercator, geographic lat/long, and the Hungarian EOV) so callers can hand the result to
    /// a real CRS/transform library. It is a convenience, not an authoritative catalogue, and
    /// performs no coordinate transformation itself.
    /// </remarks>
    public static class ProjectionRegistry
    {
        // Exact, case-insensitive name → EPSG.
        private static readonly Dictionary<string, int> Exact = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["EOV"] = 23700,
            ["BMG:EOV"] = 23700,
            ["HD72/EOV"] = 23700,
            ["HD72 / EOV"] = 23700,
            ["EPSG:23700"] = 23700,

            ["GEODETIC"] = 4326,
            ["LL"] = 4326,
            ["LATLONG"] = 4326,
            ["LONGLAT"] = 4326,
            ["WGS84"] = 4326,
            ["WGS84LL"] = 4326,
            ["EPSG:4326"] = 4326,

            ["WEBMERCATOR"] = 3857,
            ["WEB MERCATOR"] = 3857,
            ["EPSG:3857"] = 3857,

            ["GOOGLE"] = 3857,
        };

        // Datum name → geographic-CRS EPSG (used only when the projection itself is unknown/geodetic).
        private static readonly Dictionary<string, int> DatumGeographic = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["WGS84"] = 4326,
            ["WGS 84"] = 4326,
            ["EPSG:6326"] = 4326,
            ["NAD83"] = 4269,
            ["NAD27"] = 4267,
            ["GDA94"] = 4283,
            ["AGD66"] = 4202,
            ["AGD84"] = 4203,
            ["ETRS89"] = 4258,
            ["HD72"] = 4237,
            ["EPSG:6237"] = 4237,
            ["OSGB36"] = 4277,
        };

        /// <summary>
        /// Resolves a projection (and optionally a datum) name to an EPSG code.
        /// Returns <see langword="false"/> when nothing recognisable matches.
        /// </summary>
        public static bool TryGetEpsg(string? projection, string? datum, out int epsg)
        {
            epsg = 0;

            string? proj = Normalise(projection);
            string? dat = Normalise(datum);

            // 1. Explicit EPSG / numeric names on the projection, then the datum.
            if (TryEpsgToken(proj, out epsg)) return true;

            // 2. Curated exact matches.
            if (proj != null && Exact.TryGetValue(proj, out epsg)) return true;

            // 3. Pattern families on the projection name.
            if (proj != null && TryPattern(proj, out epsg)) return true;

            // 4. RAW / unset projection → fall back to the datum's geographic CRS.
            if (IsRawOrEmpty(proj))
            {
                if (TryEpsgToken(dat, out epsg)) return true;
                if (dat != null && DatumGeographic.TryGetValue(dat, out epsg)) return true;
                if (dat != null && Exact.TryGetValue(dat, out epsg)) return true;
            }

            epsg = 0;
            return false;
        }

        /// <summary>Nullable form of <see cref="TryGetEpsg"/>.</summary>
        public static int? GetEpsg(string? projection, string? datum) =>
            TryGetEpsg(projection, datum, out int e) ? e : (int?)null;

        private static string? Normalise(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return s!.Trim().Trim('"').Trim();
        }

        private static bool IsRawOrEmpty(string? s) =>
            s == null
            || string.Equals(s, "RAW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "LOCAL", StringComparison.OrdinalIgnoreCase);

        private static bool TryEpsgToken(string? s, out int epsg)
        {
            epsg = 0;
            if (s == null) return false;

            string token = s;
            int colon = token.IndexOf(':');
            if (colon >= 0 && token.Substring(0, colon).Trim().Equals("EPSG", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(colon + 1).Trim();

            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out epsg) && epsg > 0;
        }

        private static bool TryPattern(string name, out int epsg)
        {
            epsg = 0;
            string n = name.Replace(" ", string.Empty).ToUpperInvariant();

            // ER Mapper north/south UTM: NUTM<zone> / SUTM<zone> (WGS84 base).
            if (TryZone(n, "NUTM", 1, 60, out int z)) { epsg = 32600 + z; return true; }
            if (TryZone(n, "SUTM", 1, 60, out z)) { epsg = 32700 + z; return true; }

            // Generic "UTM<zone>N" / "UTM<zone>S".
            if (n.StartsWith("UTM", StringComparison.Ordinal) && n.Length >= 5)
            {
                char hemi = n[n.Length - 1];
                string digits = n.Substring(3, n.Length - 4);
                if ((hemi == 'N' || hemi == 'S') && int.TryParse(digits, out z) && z >= 1 && z <= 60)
                {
                    epsg = (hemi == 'N' ? 32600 : 32700) + z;
                    return true;
                }
            }

            // Australian AMG (AGD66) and MGA (GDA94) zones.
            if (TryZone(n, "TMAMG", 48, 58, out z)) { epsg = 20200 + z; return true; }
            if (TryZone(n, "AMG", 48, 58, out z)) { epsg = 20200 + z; return true; }
            if (TryZone(n, "MGA", 46, 59, out z)) { epsg = 28300 + z; return true; }

            return false;
        }

        private static bool TryZone(string name, string prefix, int min, int max, out int zone)
        {
            zone = 0;
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string rest = name.Substring(prefix.Length);
            return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out zone) && zone >= min && zone <= max;
        }
    }
}
