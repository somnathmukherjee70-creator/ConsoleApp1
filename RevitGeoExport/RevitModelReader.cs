using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitGeoExport.Core;

namespace RevitGeoExport
{
    public enum AnchorPoint
    {
        SurveyPoint,
        ProjectBasePoint,
        InternalOrigin,
    }

    /// <summary>Where Revit thinks the project is, read from Manage › Location and the shared coordinates.</summary>
    public sealed class RevitLocation
    {
        private const double FeetToMeters = 0.3048;

        // Shared coordinates further than this from the origin are treated as real eastings/northings.
        private const double RealCoordinateThresholdMeters = 1000;

        // How close a UTM reading must land to the Revit site location to be trusted.
        private const double UtmMatchToleranceMeters = 50_000;

        private RevitLocation(Transform toShared, XYZ surveyShared, XYZ projectBaseShared, SiteLocation site)
        {
            ToShared = toShared;
            SurveyPointShared = surveyShared;
            ProjectBasePointShared = projectBaseShared;
            LatitudeDegrees = site.Latitude * 180 / Math.PI;
            LongitudeDegrees = site.Longitude * 180 / Math.PI;
            ElevationMeters = site.Elevation * FeetToMeters;
            PlaceName = site.PlaceName;
            DetectCoordinateSystem(site);
        }

        /// <summary>Internal coordinates (feet) to shared coordinates (feet, Y = true north).</summary>
        public Transform ToShared { get; }

        public XYZ SurveyPointShared { get; }
        public XYZ ProjectBasePointShared { get; }
        public double LatitudeDegrees { get; }
        public double LongitudeDegrees { get; }
        public double ElevationMeters { get; }
        public string PlaceName { get; }

        /// <summary>The coordinate system the shared coordinates are in, when one could be found.</summary>
        public ProjectedCrs DetectedCrs { get; private set; }

        /// <summary>What was found while detecting the coordinate system, for the dialog.</summary>
        public string DetectionNote { get; private set; }

        public static RevitLocation Read(Document doc)
        {
            var survey = BasePoint.GetSurveyPoint(doc);
            var projectBase = BasePoint.GetProjectBasePoint(doc);

            // GetTotalTransform's direction is easy to get wrong, so pick whichever direction maps the
            // survey point's internal position onto its known shared position.
            var total = doc.ActiveProjectLocation.GetTotalTransform();
            var toShared = total.Inverse;
            if (toShared.OfPoint(survey.Position).DistanceTo(survey.SharedPosition) > 1e-4 &&
                total.OfPoint(survey.Position).DistanceTo(survey.SharedPosition) <= 1e-4)
            {
                toShared = total;
            }

            return new RevitLocation(toShared, survey.SharedPosition, projectBase.SharedPosition, doc.SiteLocation);
        }

        /// <summary>Builds a function from internal coordinates to shared coordinates in metres (eastings, northings, elevation).</summary>
        public Func<XYZ, Vec3> ProjectedFrame()
        {
            var toShared = ToShared;
            return p =>
            {
                var s = toShared.OfPoint(p);
                return new Vec3(s.X * FeetToMeters, s.Y * FeetToMeters, s.Z * FeetToMeters);
            };
        }

        private void DetectCoordinateSystem(SiteLocation site)
        {
            // 1. A GIS coordinate system assigned in Revit (acquired from Civil 3D, a DWG or ACC).
            string id = site.GeoCoordinateSystemId;
            if (!string.IsNullOrEmpty(id))
            {
                if (ProjectedCrs.TryParse(id, out var crs) || ProjectedCrs.TryParse(site.GeoCoordinateSystemDefinition, out crs))
                {
                    DetectedCrs = crs;
                    DetectionNote = $"Found in the Revit project: {id}.";
                }
                else
                {
                    DetectionNote = $"The project uses coordinate system \"{id}\", which isn't supported. Enter its EPSG code if it is UTM, or place it by latitude/longitude.";
                }

                return;
            }

            // 2. Shared coordinates that land near the Revit site location in a grid used there (UTM zones, local grids).
            var shared = ProjectBasePointShared;
            if (Math.Abs(shared.X) * FeetToMeters < RealCoordinateThresholdMeters && Math.Abs(shared.Y) * FeetToMeters < RealCoordinateThresholdMeters)
                shared = ToShared.OfPoint(XYZ.Zero);
            double east = shared.X * FeetToMeters, north = shared.Y * FeetToMeters;
            if (Math.Abs(east) < RealCoordinateThresholdMeters && Math.Abs(north) < RealCoordinateThresholdMeters)
            {
                DetectionNote = "No coordinate system found; the model is placed by latitude/longitude.";
                return;
            }

            ProjectedCrs best = null;
            double bestDistance = UtmMatchToleranceMeters;
            foreach (var candidate in ProjectedCrs.CandidatesNear(LatitudeDegrees, LongitudeDegrees))
            {
                var (lon, lat) = candidate.ToLonLat(east, north);
                double distance = DistanceMeters(lat, lon, LatitudeDegrees, LongitudeDegrees);
                if (distance <= bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            string coordinates = string.Format(System.Globalization.CultureInfo.InvariantCulture, "E {0:N0}, N {1:N0}", east, north);
            if (best != null)
            {
                DetectedCrs = best;
                DetectionNote = $"Shared coordinates {coordinates} match {best.Name} near {PlaceName}.";
            }
            else
            {
                DetectionNote = $"The shared coordinates ({coordinates}) look like real eastings/northings but don't match a known grid near the Revit location. Enter their EPSG code to place the model by them.";
            }
        }

        private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            double toRad = Math.PI / 180;
            double x = (lon2 - lon1) * toRad * Math.Cos((lat1 + lat2) / 2 * toRad);
            double y = (lat2 - lat1) * toRad;
            return Math.Sqrt(x * x + y * y) * 6_371_000;
        }

        /// <summary>Builds a function from internal coordinates to East-North-Up metres around the chosen anchor.</summary>
        public Func<XYZ, Vec3> LocalFrame(AnchorPoint anchor)
        {
            XYZ origin;
            switch (anchor)
            {
                case AnchorPoint.ProjectBasePoint: origin = ProjectBasePointShared; break;
                case AnchorPoint.InternalOrigin: origin = ToShared.OfPoint(XYZ.Zero); break;
                default: origin = SurveyPointShared; break;
            }

            var toShared = ToShared;
            return p =>
            {
                var d = toShared.OfPoint(p) - origin;
                return new Vec3(d.X * FeetToMeters, d.Y * FeetToMeters, d.Z * FeetToMeters);
            };
        }
    }

    /// <summary>Collects triangulated model geometry, one <see cref="GeoMesh"/> per element.</summary>
    public static class RevitModelReader
    {
        private static readonly HashSet<BuiltInCategory> Excluded = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Topography,
            BuiltInCategory.OST_Toposolid,
            BuiltInCategory.OST_Cameras,
            BuiltInCategory.OST_Lines,
            BuiltInCategory.OST_SectionBox,
            BuiltInCategory.OST_RvtLinks,
            BuiltInCategory.OST_PointClouds,
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_Areas,
            BuiltInCategory.OST_MEPSpaces,
            BuiltInCategory.OST_ProjectBasePoint,
            BuiltInCategory.OST_SharedBasePoint,
        };

        /// <param name="view">When set, only elements visible in this 3D view are exported, as the view shows them.</param>
        public static List<GeoMesh> Read(Document doc, View3D view, Func<XYZ, Vec3> toLocal)
        {
            var options = new Options { ComputeReferences = false };
            FilteredElementCollector collector;
            if (view != null)
            {
                options.View = view;
                collector = new FilteredElementCollector(doc, view.Id);
            }
            else
            {
                options.DetailLevel = ViewDetailLevel.Medium;
                collector = new FilteredElementCollector(doc);
            }

            var meshes = new List<GeoMesh>();
            foreach (var element in collector.WhereElementIsNotElementType().WhereElementIsViewIndependent())
            {
                var category = element.Category;
                if (category == null || category.CategoryType != CategoryType.Model || Excluded.Contains(category.BuiltInCategory))
                    continue;

                var geometry = element.get_Geometry(options);
                if (geometry == null)
                    continue;

                var mesh = new GeoMesh(element.Id.ToString(), element.Name, category.Name);
                AddGeometry(geometry, mesh, toLocal);
                if (mesh.TriangleCount > 0)
                    meshes.Add(mesh);
            }

            return meshes;
        }

        private static void AddGeometry(GeometryElement geometry, GeoMesh target, Func<XYZ, Vec3> toLocal)
        {
            foreach (var obj in geometry)
            {
                switch (obj)
                {
                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face face in solid.Faces)
                            AddMesh(face.Triangulate(), target, toLocal);
                        break;
                    case Mesh mesh:
                        AddMesh(mesh, target, toLocal);
                        break;
                    case GeometryInstance instance:
                        // Instance geometry is already transformed into model coordinates.
                        AddGeometry(instance.GetInstanceGeometry(), target, toLocal);
                        break;
                }
            }
        }

        private static void AddMesh(Mesh mesh, GeoMesh target, Func<XYZ, Vec3> toLocal)
        {
            if (mesh == null)
                return;

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                var triangle = mesh.get_Triangle(i);
                target.AddTriangle(toLocal(triangle.get_Vertex(0)), toLocal(triangle.get_Vertex(1)), toLocal(triangle.get_Vertex(2)));
            }
        }
    }
}
