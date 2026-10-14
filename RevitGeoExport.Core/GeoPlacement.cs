using System.Collections.Generic;
using System.Linq;

namespace RevitGeoExport.Core
{
    /// <summary>Maps the model's horizontal coordinates (metres) to longitude/latitude.</summary>
    public interface IGeoPlacement
    {
        (double Longitude, double Latitude) ToLonLat(double x, double y);

        /// <summary>Added to model heights when exporting above sea level.</summary>
        double ElevationMeters { get; }

        /// <summary>One line describing how the model was placed, shown in the exports.</summary>
        string Description { get; }
    }

    /// <summary>
    /// Places a model whose coordinates are already eastings/northings in a projected coordinate
    /// system (for example shared coordinates acquired from a survey in UTM).
    /// </summary>
    public sealed class ProjectedPlacement : IGeoPlacement
    {
        public ProjectedPlacement(ProjectedCrs crs, double elevationMeters = 0)
        {
            Crs = crs;
            ElevationMeters = elevationMeters;
        }

        public ProjectedCrs Crs { get; }
        public double ElevationMeters { get; }
        public string Description => $"Model coordinates in {Crs}";

        /// <param name="x">Easting from Revit, in metres (Revit feet × 0.3048).</param>
        public (double Longitude, double Latitude) ToLonLat(double x, double y)
        {
            // Surveys in US survey feet are typed into Revit as plain feet, so the Revit foot value
            // is the survey number itself.
            if (Crs.IsUsSurveyFeet)
                return Crs.ToLonLat(x / RevitFootMeters, y / RevitFootMeters);
            return Crs.ToLonLat(x, y);
        }

        private const double RevitFootMeters = 0.3048;
    }

    internal static class PlacementExtensions
    {
        /// <summary>Longitude/latitude of the centre of the meshes' plan bounding box.</summary>
        public static (double Longitude, double Latitude) Center(this IGeoPlacement placement, IEnumerable<GeoMesh> meshes)
        {
            var points = meshes.SelectMany(m => m.Vertices).ToList();
            if (points.Count == 0)
                return placement.ToLonLat(0, 0);
            return placement.ToLonLat(
                (points.Min(p => p.X) + points.Max(p => p.X)) / 2,
                (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);
        }
    }
}
