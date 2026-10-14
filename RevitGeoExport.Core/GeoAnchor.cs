using System;

namespace RevitGeoExport.Core
{
    /// <summary>
    /// The real-world position of the model's local origin, plus an optional extra rotation.
    /// Local model coordinates are East-North-Up metres relative to this point.
    /// </summary>
    public sealed class GeoAnchor
    {
        public GeoAnchor(double latitudeDegrees, double longitudeDegrees, double elevationMeters = 0, double rotationDegrees = 0)
        {
            if (latitudeDegrees < -90 || latitudeDegrees > 90)
                throw new ArgumentOutOfRangeException(nameof(latitudeDegrees), "Latitude must be between -90 and 90 degrees.");
            if (longitudeDegrees < -180 || longitudeDegrees > 180)
                throw new ArgumentOutOfRangeException(nameof(longitudeDegrees), "Longitude must be between -180 and 180 degrees.");

            LatitudeDegrees = latitudeDegrees;
            LongitudeDegrees = longitudeDegrees;
            ElevationMeters = elevationMeters;
            RotationDegrees = rotationDegrees;
        }

        public double LatitudeDegrees { get; }
        public double LongitudeDegrees { get; }

        /// <summary>Height of the anchor above sea level, used for absolute altitudes.</summary>
        public double ElevationMeters { get; }

        /// <summary>
        /// Extra clockwise rotation (as seen from above) applied to the model about the anchor, in degrees.
        /// Use it to fine-tune alignment against the imagery when true north is not set exactly in Revit.
        /// </summary>
        public double RotationDegrees { get; }
    }
}
