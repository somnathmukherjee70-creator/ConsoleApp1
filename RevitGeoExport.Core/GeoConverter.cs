using System;

namespace RevitGeoExport.Core
{
    /// <summary>Converts local East-North-Up offsets around a <see cref="GeoAnchor"/> to WGS84 coordinates.</summary>
    public sealed class GeoConverter
    {
        // WGS84 ellipsoid.
        private const double A = 6378137.0;
        private const double F = 1 / 298.257223563;
        private const double E2 = F * (2 - F);
        private const double DegToRad = Math.PI / 180;

        private readonly GeoAnchor _anchor;
        private readonly double _sinLat, _cosLat, _sinLon, _cosLon;
        private readonly double _x0, _y0, _z0;
        private readonly double _sinRot, _cosRot;

        public GeoConverter(GeoAnchor anchor)
        {
            _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));

            double lat = anchor.LatitudeDegrees * DegToRad;
            double lon = anchor.LongitudeDegrees * DegToRad;
            _sinLat = Math.Sin(lat);
            _cosLat = Math.Cos(lat);
            _sinLon = Math.Sin(lon);
            _cosLon = Math.Cos(lon);

            double n = A / Math.Sqrt(1 - E2 * _sinLat * _sinLat);
            _x0 = n * _cosLat * _cosLon;
            _y0 = n * _cosLat * _sinLon;
            _z0 = n * (1 - E2) * _sinLat;

            // Clockwise rotation seen from above is a negative angle in the math (counter-clockwise) convention.
            double rot = -anchor.RotationDegrees * DegToRad;
            _sinRot = Math.Sin(rot);
            _cosRot = Math.Cos(rot);
        }

        public GeoAnchor Anchor => _anchor;

        /// <summary>Applies the anchor's extra rotation to a local point.</summary>
        public Vec3 Rotate(Vec3 local) =>
            new Vec3(local.X * _cosRot - local.Y * _sinRot, local.X * _sinRot + local.Y * _cosRot, local.Z);

        /// <summary>
        /// Returns the longitude and latitude (degrees) of a local point. The point is taken on the
        /// anchor's tangent plane; height does not shift the horizontal position.
        /// </summary>
        public (double Longitude, double Latitude) ToLonLat(double east, double north)
        {
            var rotated = Rotate(new Vec3(east, north, 0));
            double e = rotated.X, nn = rotated.Y;

            // ENU offset -> ECEF.
            double x = _x0 - _sinLon * e - _sinLat * _cosLon * nn;
            double y = _y0 + _cosLon * e - _sinLat * _sinLon * nn;
            double z = _z0 + _cosLat * nn;

            // ECEF -> geodetic (iterative; converges to sub-millimetre in a few steps away from the poles).
            double lon = Math.Atan2(y, x);
            double p = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, p * (1 - E2));
            for (int i = 0; i < 6; i++)
            {
                double sinLat = Math.Sin(lat);
                double radius = A / Math.Sqrt(1 - E2 * sinLat * sinLat);
                double h = p / Math.Cos(lat) - radius;
                lat = Math.Atan2(z, p * (1 - E2 * radius / (radius + h)));
            }

            return (lon / DegToRad, lat / DegToRad);
        }
    }
}
