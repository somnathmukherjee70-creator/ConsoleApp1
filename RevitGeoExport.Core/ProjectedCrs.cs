using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitGeoExport.Core
{
    /// <summary>
    /// A projected coordinate system the exporter can convert from, so a model whose shared
    /// coordinates are real eastings/northings can be placed without entering a latitude/longitude.
    /// Supports UTM on WGS84, NAD83, ETRS89, GDA94 and GDA2020, Dubai Local TM (DLTM), Pennsylvania
    /// State Plane and Web Mercator. Coordinates are in the system's own units (metres or US survey feet). These datums are
    /// treated as WGS84, which is within about 2 m and invisible at map scale.
    /// </summary>
    public sealed class ProjectedCrs
    {
        private const double DegToRad = Math.PI / 180;

        // WGS84 / GRS80 (identical to well under a millimetre for this purpose).
        private const double A = 6378137.0;
        private const double F = 1 / 298.257223563;
        private const double UtmScale = 0.9996;
        private const int DltmEpsg = 3997;

        // Krüger series coefficients, from the third flattening n.
        private const double N = F / (2 - F);
        private const double Rectifying = A / (1 + N) * (1 + N * N / 4 + N * N * N * N / 64);
        private static readonly double[] Alpha = { N / 2 - 2 * N * N / 3 + 5 * N * N * N / 16, 13 * N * N / 48 - 3 * N * N * N / 5, 61 * N * N * N / 240 };
        private static readonly double[] Beta = { N / 2 - 2 * N * N / 3 + 37 * N * N * N / 96, N * N / 48 + N * N * N / 15, 17 * N * N * N / 480 };
        private static readonly double[] Delta = { 2 * N - 2 * N * N / 3 - 2 * N * N * N, 7 * N * N / 3 - 8 * N * N * N / 5, 56 * N * N * N / 15 };

        private readonly bool _webMercator;
        private readonly double _centralMeridian;
        private readonly double _falseNorthing;
        private readonly double _k0;
        private readonly LambertConic _lambert;

        private ProjectedCrs(int epsg, string name, bool webMercator, double centralMeridianDegrees = 0, double falseNorthing = 0, double scale = UtmScale)
        {
            _k0 = scale;
            Epsg = epsg;
            Name = name;
            _webMercator = webMercator;
            _centralMeridian = centralMeridianDegrees * DegToRad;
            _falseNorthing = falseNorthing;
        }

        private ProjectedCrs(int epsg, string name, LambertConic lambert)
        {
            Epsg = epsg;
            Name = name;
            _lambert = lambert;
            UnitMeters = lambert.UnitMeters;
        }

        public int Epsg { get; }
        public string Name { get; }

        /// <summary>Length of one coordinate unit in metres: 1, or 1200/3937 for US survey feet.</summary>
        public double UnitMeters { get; } = 1;

        public bool IsUsSurveyFeet => UnitMeters != 1;

        public static bool TryCreate(int epsg, out ProjectedCrs crs)
        {
            crs = null!;
            if (epsg == 3857 || epsg == 900913 || epsg == 3785)
            {
                crs = new ProjectedCrs(epsg, "WGS 84 / Pseudo-Mercator", webMercator: true);
                return true;
            }

            if (epsg == DltmEpsg)
            {
                // Dubai Local Transverse Mercator: central meridian 55°20' E, scale 1.
                crs = new ProjectedCrs(epsg, "WGS 84 / Dubai Local TM", webMercator: false, centralMeridianDegrees: 55 + 1.0 / 3, scale: 1.0);
                return true;
            }

            if (LambertConic.TryCreate(epsg, out string lambertName, out var lambert))
            {
                crs = new ProjectedCrs(epsg, lambertName, lambert);
                return true;
            }

            if (!TryGetUtmZone(epsg, out string datum, out int zone, out bool south))
                return false;

            crs = new ProjectedCrs(epsg, $"{datum} / UTM zone {zone}{(south ? "S" : "N")}", webMercator: false,
                centralMeridianDegrees: zone * 6 - 183, falseNorthing: south ? 10_000_000 : 0);
            return true;
        }

        public static ProjectedCrs Utm(int zone, bool south) =>
            TryCreate((south ? 32700 : 32600) + zone, out var crs) ? crs : throw new ArgumentOutOfRangeException(nameof(zone));

        /// <summary>
        /// Coordinate systems a survey near this location is likely to use: its UTM zone, the
        /// neighbouring zones (sites near a zone edge often use the next one), and local grids.
        /// </summary>
        public static IEnumerable<ProjectedCrs> CandidatesNear(double latitudeDegrees, double longitudeDegrees)
        {
            bool south = latitudeDegrees < 0;
            int zone = UtmZoneFor(longitudeDegrees);
            yield return Utm(zone, south);
            if (zone > 1) yield return Utm(zone - 1, south);
            if (zone < 60) yield return Utm(zone + 1, south);

            if (latitudeDegrees > 22 && latitudeDegrees < 27 && longitudeDegrees > 51 && longitudeDegrees < 57 && TryCreate(3997, out var dltm))
                yield return dltm;

            // Pennsylvania State Plane, US survey feet first as most PA surveys use it.
            if (latitudeDegrees > 39.5 && latitudeDegrees < 42.5 && longitudeDegrees > -81 && longitudeDegrees < -74.5)
            {
                foreach (int code in new[] { 2272, 2271, 32129, 32128 })
                    if (TryCreate(code, out var statePlane))
                        yield return statePlane;
            }
        }

        /// <summary>The WGS84 UTM zone containing a longitude.</summary>
        public static int UtmZoneFor(double longitudeDegrees) =>
            Math.Min(60, Math.Max(1, (int)Math.Floor((longitudeDegrees + 180) / 6) + 1));

        /// <summary>
        /// Reads an EPSG code from a coordinate system name or definition: "EPSG:32645", "32645",
        /// Autodesk/CS-MAP codes such as "UTM84-45N", "WGS84.UTM-45N", "UTM83-10", "ETRS89.UTM-32N",
        /// "WGS84.PseudoMercator", or WKT/XML text containing an EPSG authority code.
        /// </summary>
        public static bool TryParse(string? text, out ProjectedCrs crs)
        {
            crs = null!;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text!.Trim();

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int plain))
                return TryCreate(plain, out crs);

            var csMap = Regex.Match(text, @"^(?:(WGS84|ETRS89|NAD83|GDA94|GDA2020)\.UTM-|UTM(84|83)-)(\d{1,2})([NS])?$", RegexOptions.IgnoreCase);
            if (csMap.Success)
            {
                string datum = csMap.Groups[1].Success ? csMap.Groups[1].Value.ToUpperInvariant() : csMap.Groups[2].Value == "84" ? "WGS84" : "NAD83";
                int zone = int.Parse(csMap.Groups[3].Value, CultureInfo.InvariantCulture);
                bool south = csMap.Groups[4].Value.Equals("S", StringComparison.OrdinalIgnoreCase)
                    || (!csMap.Groups[4].Success && (datum == "GDA94" || datum == "GDA2020"));
                return TryCreate(UtmEpsg(datum, zone, south), out crs);
            }

            // Pennsylvania State Plane: Autodesk/CS-MAP codes (PA83-SF = South, US feet) and descriptive names.
            var pa = Regex.Match(text, @"^PA83-([NS])(F)?$", RegexOptions.IgnoreCase);
            if (pa.Success)
                return TryCreate(PennsylvaniaEpsg(pa.Groups[1].Value.ToUpperInvariant() == "S", pa.Groups[2].Success), out crs);
            var paName = Regex.Match(text, @"Pennsylvania\W*(South|North)", RegexOptions.IgnoreCase);
            if (paName.Success && !Regex.IsMatch(text, @"EPSG\W{0,4}\d{4,6}", RegexOptions.IgnoreCase))
                return TryCreate(PennsylvaniaEpsg(paName.Groups[1].Value.Equals("South", StringComparison.OrdinalIgnoreCase),
                    Regex.IsMatch(text, @"ftUS|US\s*survey\s*f|feet|\bft\b", RegexOptions.IgnoreCase)), out crs);

            if (Regex.IsMatch(text, @"Dubai|DLTM", RegexOptions.IgnoreCase))
                return TryCreate(3997, out crs);

            if (Regex.IsMatch(text, @"PseudoMercator|Pseudo-Mercator|WebMercator|Web Mercator", RegexOptions.IgnoreCase))
                return TryCreate(3857, out crs);

            // In WKT the coordinate system's own AUTHORITY comes last, after those of its datum and units.
            var codes = Regex.Matches(text, @"EPSG\W{0,4}(\d{4,6})", RegexOptions.IgnoreCase);
            for (int i = codes.Count - 1; i >= 0; i--)
            {
                if (TryCreate(int.Parse(codes[i].Groups[1].Value, CultureInfo.InvariantCulture), out crs))
                    return true;
            }

            return false;
        }

        /// <summary>Converts easting/northing in metres to longitude/latitude in degrees.</summary>
        public (double Longitude, double Latitude) ToLonLat(double easting, double northing)
        {
            if (_lambert != null)
                return _lambert.Inverse(easting * UnitMeters, northing * UnitMeters);
            if (_webMercator)
                return (easting / A / DegToRad, Math.Atan(Math.Sinh(northing / A)) / DegToRad);

            // Inverse transverse Mercator, Krüger series (millimetre accuracy across a whole UTM zone).
            double xi = (northing - _falseNorthing) / (_k0 * Rectifying);
            double eta = (easting - 500_000) / (_k0 * Rectifying);
            double xiP = xi, etaP = eta;
            for (int j = 1; j <= 3; j++)
            {
                xiP -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
                etaP -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            }

            double chi = Math.Asin(Math.Sin(xiP) / Math.Cosh(etaP));
            double lat = chi;
            for (int j = 1; j <= 3; j++)
                lat += Delta[j - 1] * Math.Sin(2 * j * chi);
            double lon = _centralMeridian + Math.Atan2(Math.Sinh(etaP), Math.Cos(xiP));

            return (lon / DegToRad, lat / DegToRad);
        }

        /// <summary>Converts longitude/latitude in degrees to easting/northing in metres.</summary>
        public (double Easting, double Northing) FromLonLat(double longitudeDegrees, double latitudeDegrees)
        {
            double phi = latitudeDegrees * DegToRad;
            double lambda = longitudeDegrees * DegToRad;
            if (_lambert != null)
            {
                var (lx, ly) = _lambert.Forward(phi, lambda);
                return (lx / UnitMeters, ly / UnitMeters);
            }

            if (_webMercator)
                return (A * lambda, A * Math.Log(Math.Tan(Math.PI / 4 + phi / 2)));

            // Forward transverse Mercator, Krüger series.
            double k = 2 * Math.Sqrt(N) / (1 + N);
            double t = Math.Sinh(Atanh(Math.Sin(phi)) - k * Atanh(k * Math.Sin(phi)));
            double dLambda = lambda - _centralMeridian;
            double xiP = Math.Atan2(t, Math.Cos(dLambda));
            double etaP = Atanh(Math.Sin(dLambda) / Math.Sqrt(1 + t * t));
            double xi = xiP, eta = etaP;
            for (int j = 1; j <= 3; j++)
            {
                xi += Alpha[j - 1] * Math.Sin(2 * j * xiP) * Math.Cosh(2 * j * etaP);
                eta += Alpha[j - 1] * Math.Cos(2 * j * xiP) * Math.Sinh(2 * j * etaP);
            }

            double x = _k0 * Rectifying * eta;
            double y = _k0 * Rectifying * xi;
            return (x + 500_000, y + _falseNorthing);
        }

        /// <summary>Lambert Conformal Conic with two standard parallels (EPSG method 9802, Snyder eqs. 15-1 to 15-11).</summary>
        private sealed class LambertConic
        {
            private const double UsSurveyFoot = 1200.0 / 3937;
            private static readonly double E = Math.Sqrt(F * (2 - F));

            private readonly double _lon0, _falseEasting, _falseNorthing, _n, _af, _rho0;

            private LambertConic(double lat0, double lon0, double lat1, double lat2, double falseEastingMeters, double unitMeters)
            {
                _lon0 = lon0 * DegToRad;
                _falseEasting = falseEastingMeters;
                UnitMeters = unitMeters;
                double p1 = lat1 * DegToRad, p2 = lat2 * DegToRad;
                _n = (Math.Log(M(p1)) - Math.Log(M(p2))) / (Math.Log(T(p1)) - Math.Log(T(p2)));
                _af = A * M(p1) / (_n * Math.Pow(T(p1), _n));
                _rho0 = _af * Math.Pow(T(lat0 * DegToRad), _n);
            }

            public double UnitMeters { get; }

            public static bool TryCreate(int epsg, out string name, out LambertConic lambert)
            {
                // NAD83 Pennsylvania South / North, in US survey feet and metres.
                switch (epsg)
                {
                    case 2272: name = "NAD83 / Pennsylvania South (ftUS)"; lambert = South(UsSurveyFoot); return true;
                    case 32129: name = "NAD83 / Pennsylvania South"; lambert = South(1); return true;
                    case 2271: name = "NAD83 / Pennsylvania North (ftUS)"; lambert = North(UsSurveyFoot); return true;
                    case 32128: name = "NAD83 / Pennsylvania North"; lambert = North(1); return true;
                    default: name = ""; lambert = null!; return false;
                }
            }

            private static LambertConic South(double unit) => new LambertConic(39 + 20.0 / 60, -77.75, 40 + 58.0 / 60, 39 + 56.0 / 60, 600_000, unit);
            private static LambertConic North(double unit) => new LambertConic(40 + 10.0 / 60, -77.75, 41 + 57.0 / 60, 40 + 53.0 / 60, 600_000, unit);

            public (double X, double Y) Forward(double phi, double lambda)
            {
                double rho = _af * Math.Pow(T(phi), _n);
                double theta = _n * (lambda - _lon0);
                return (_falseEasting + rho * Math.Sin(theta), _falseNorthing + _rho0 - rho * Math.Cos(theta));
            }

            public (double Longitude, double Latitude) Inverse(double x, double y)
            {
                double dx = x - _falseEasting, dy = _rho0 - (y - _falseNorthing);
                double rho = Math.Sign(_n) * Math.Sqrt(dx * dx + dy * dy);
                double t = Math.Pow(rho / _af, 1 / _n);
                double theta = Math.Atan2(dx, dy);

                double phi = Math.PI / 2 - 2 * Math.Atan(t);
                for (int i = 0; i < 10; i++)
                {
                    double es = E * Math.Sin(phi);
                    phi = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - es) / (1 + es), E / 2));
                }

                return ((theta / _n + _lon0) / DegToRad, phi / DegToRad);
            }

            private static double M(double phi) => Math.Cos(phi) / Math.Sqrt(1 - E * E * Math.Sin(phi) * Math.Sin(phi));

            private static double T(double phi)
            {
                double es = E * Math.Sin(phi);
                return Math.Tan(Math.PI / 4 - phi / 2) / Math.Pow((1 - es) / (1 + es), E / 2);
            }
        }

        // Math.Atanh is not in netstandard2.0.
        private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));

        public override string ToString() => $"EPSG:{Epsg} ({Name})";

        private static int PennsylvaniaEpsg(bool south, bool usFeet) =>
            south ? (usFeet ? 2272 : 32129) : (usFeet ? 2271 : 32128);

        private static int UtmEpsg(string datum, int zone, bool south)
        {
            switch (datum)
            {
                case "NAD83": return 26900 + zone;
                case "ETRS89": return 25800 + zone;
                case "GDA94": return 28300 + zone;
                case "GDA2020": return 7800 + zone;
                default: return (south ? 32700 : 32600) + zone;
            }
        }

        private static bool TryGetUtmZone(int epsg, out string datum, out int zone, out bool south)
        {
            south = false;
            if (epsg >= 32601 && epsg <= 32660) { datum = "WGS 84"; zone = epsg - 32600; return true; }
            if (epsg >= 32701 && epsg <= 32760) { datum = "WGS 84"; zone = epsg - 32700; south = true; return true; }
            if (epsg >= 26901 && epsg <= 26923) { datum = "NAD83"; zone = epsg - 26900; return true; }
            if (epsg >= 25828 && epsg <= 25838) { datum = "ETRS89"; zone = epsg - 25800; return true; }
            if (epsg >= 28348 && epsg <= 28358) { datum = "GDA94 / MGA"; zone = epsg - 28300; south = true; return true; }
            if (epsg >= 7846 && epsg <= 7859) { datum = "GDA2020 / MGA"; zone = epsg - 7800; south = true; return true; }

            datum = "";
            zone = 0;
            return false;
        }
    }
}
