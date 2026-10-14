using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitGeoExport.Core
{
    /// <summary>
    /// Writes each element as a GeoJSON footprint (the convex hull of its plan outline) with base and
    /// top heights, ready for 3D extrusion in MapLibre, Mapbox, Cesium, ArcGIS or QGIS.
    /// </summary>
    public static class GeoJsonWriter
    {
        public static string ToGeoJson(IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options)
        {
            var altitude = new AltitudeResolver(options, placement, meshes);
            var center = placement.Center(meshes);
            var json = new StringBuilder();

            json.Append("{\"type\":\"FeatureCollection\",");
            json.Append("\"name\":").Append(Quote(options.DocumentName)).Append(',');
            json.Append("\"placement\":").Append(Quote(placement.Description)).Append(',');
            json.Append("\"center\":{\"longitude\":").Append(KmlWriter.Format(center.Longitude))
                .Append(",\"latitude\":").Append(KmlWriter.Format(center.Latitude)).Append("},");
            json.Append("\"features\":[");

            bool first = true;
            foreach (var mesh in meshes)
            {
                var hull = ConvexHull.Compute(mesh.Vertices.Select(v => (v.X, v.Y)));
                if (hull.Count < 3)
                    continue;

                if (!first)
                    json.Append(',');
                first = false;

                double baseHeight = altitude.Resolve(mesh.Vertices.Min(v => v.Z));
                double topHeight = altitude.Resolve(mesh.Vertices.Max(v => v.Z));

                json.Append("{\"type\":\"Feature\",\"properties\":{");
                json.Append("\"elementId\":").Append(Quote(mesh.ElementId)).Append(',');
                json.Append("\"name\":").Append(Quote(mesh.Name)).Append(',');
                json.Append("\"category\":").Append(Quote(mesh.Category)).Append(',');
                json.Append("\"color\":").Append(Quote(CategoryColors.ToCss(mesh.Category))).Append(',');
                json.Append("\"base_height\":").Append(KmlWriter.Format(baseHeight, 3)).Append(',');
                json.Append("\"height\":").Append(KmlWriter.Format(topHeight, 3)).Append(',');
                json.Append("\"altitude_mode\":").Append(Quote(altitude.IsRelativeToGround ? "relativeToGround" : "absolute"));
                json.Append("},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[");

                // GeoJSON rings are counter-clockwise and closed; the hull is already counter-clockwise.
                for (int i = 0; i <= hull.Count; i++)
                {
                    var (east, north) = hull[i % hull.Count];
                    var (lon, lat) = placement.ToLonLat(east, north);
                    if (i > 0)
                        json.Append(',');
                    json.Append('[').Append(KmlWriter.Format(lon)).Append(',').Append(KmlWriter.Format(lat)).Append(']');
                }

                json.Append("]]}}");
            }

            json.Append("]}");
            return json.ToString();
        }

        public static void Write(string path, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options) =>
            File.WriteAllText(path, ToGeoJson(placement, meshes, options), new UTF8Encoding(false));

        internal static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    // Keeps the JSON safe to embed inside an HTML <script> block.
                    case '<': sb.Append("\\u003c"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.Append('"').ToString();
        }
    }

    internal static class ConvexHull
    {
        /// <summary>Andrew's monotone chain; returns the hull counter-clockwise without repeating the first point.</summary>
        public static List<(double X, double Y)> Compute(IEnumerable<(double X, double Y)> points)
        {
            // Round to a millimetre so near-duplicate vertices from triangulation collapse.
            var pts = points
                .Select(p => (X: System.Math.Round(p.X, 3), Y: System.Math.Round(p.Y, 3)))
                .Distinct()
                .OrderBy(p => p.X).ThenBy(p => p.Y)
                .ToList();
            if (pts.Count < 3)
                return pts;

            var hull = new List<(double X, double Y)>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                foreach (var p in pts)
                {
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }

                hull.RemoveAt(hull.Count - 1);
                pts.Reverse();
            }

            return hull;
        }

        private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
