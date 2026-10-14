using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace RevitGeoExport.Core
{
    /// <summary>
    /// Writes meshes as KML/KMZ for Google Earth (Pro, Web and mobile). Each triangle becomes a
    /// KML polygon, grouped into one folder per Revit category.
    /// </summary>
    public static class KmlWriter
    {
        public static KmlResult WriteKmz(string path, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options)
        {
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("doc.kml", CompressionLevel.Optimal).Open())
            {
                return WriteKml(entry, placement, meshes, options);
            }
        }

        public static KmlResult WriteKml(Stream stream, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> all, ExportOptions options)
        {
            var meshes = WithinBudget(all, options.MaxKmlPoints);
            var altitude = new AltitudeResolver(options, placement, meshes);
            var center = placement.Center(meshes);
            string altitudeMode = altitude.IsRelativeToGround ? "relativeToGround" : "absolute";

            // No indentation: on big models whitespace alone adds tens of megabytes.
            var settings = new XmlWriterSettings { Indent = false, Encoding = new UTF8Encoding(false) };
            using (var xml = XmlWriter.Create(stream, settings))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
                xml.WriteStartElement("Document");
                xml.WriteElementString("name", options.DocumentName);
                xml.WriteElementString("open", "1");
                if (meshes.Count < all.Count)
                    xml.WriteElementString("description",
                        $"{all.Count - meshes.Count} of the smallest elements were left out to keep the file small enough for Google Earth.");

                WriteLookAt(xml, center, meshes);

                var categories = meshes.Select(m => m.Category).Distinct().OrderBy(c => c).ToList();
                foreach (var category in categories)
                    WriteStyle(xml, category);

                WriteLocationPlacemark(xml, center, placement);

                foreach (var group in meshes.GroupBy(m => m.Category).OrderBy(g => g.Key))
                {
                    xml.WriteStartElement("Folder");
                    xml.WriteElementString("name", group.Key);
                    if (options.OneFeaturePerCategory)
                    {
                        // Google Earth Web counts features per import (max 10,000), so merge each category.
                        var polygons = group.SelectMany(m => Polygons(m, placement, altitude, options.KmlMode)).ToList();
                        WritePlacemark(xml, group.Key, $"{group.Count()} elements", group.Key, polygons, altitudeMode, options.KmlMode);
                    }
                    else
                    {
                        foreach (var mesh in group)
                            WritePlacemark(xml, mesh.Name, $"{mesh.Category} · Element ID {mesh.ElementId}", mesh.Category,
                                Polygons(mesh, placement, altitude, options.KmlMode), altitudeMode, options.KmlMode);
                    }

                    xml.WriteEndElement();
                }

                xml.WriteEndElement(); // Document
                xml.WriteEndElement(); // kml
            }

            return new KmlResult(meshes.Count, all.Count - meshes.Count);
        }

        /// <summary>Keeps the largest elements that fit the point budget, so the building's shape survives.</summary>
        internal static List<GeoMesh> WithinBudget(IReadOnlyCollection<GeoMesh> meshes, int maxPoints) =>
            WithinBudget(meshes, maxPoints, m => m.KmlPointCount);

        internal static List<GeoMesh> WithinBudget(IReadOnlyCollection<GeoMesh> meshes, int max, Func<GeoMesh, int> cost)
        {
            if (max <= 0 || meshes.Sum(m => (long)cost(m)) <= max)
                return meshes.ToList();

            var kept = new List<GeoMesh>();
            long used = 0;
            foreach (var mesh in meshes.OrderByDescending(Size))
            {
                if (used + cost(mesh) > max)
                    continue;
                kept.Add(mesh);
                used += cost(mesh);
            }

            return kept;
        }

        private static double Size(GeoMesh mesh)
        {
            if (mesh.Vertices.Count == 0)
                return 0;
            double dx = mesh.Vertices.Max(v => v.X) - mesh.Vertices.Min(v => v.X);
            double dy = mesh.Vertices.Max(v => v.Y) - mesh.Vertices.Min(v => v.Y);
            double dz = mesh.Vertices.Max(v => v.Z) - mesh.Vertices.Min(v => v.Z);
            return dx * dx + dy * dy + dz * dz;
        }

        private static void WriteLookAt(XmlWriter xml, (double Longitude, double Latitude) center, IReadOnlyCollection<GeoMesh> meshes)
        {
            // Frame the model: distance from the camera grows with the model's extent.
            double extent = 50;
            var points = meshes.SelectMany(m => m.Vertices).ToList();
            if (points.Count > 0)
            {
                double dx = points.Max(p => p.X) - points.Min(p => p.X);
                double dy = points.Max(p => p.Y) - points.Min(p => p.Y);
                extent = System.Math.Max(extent, System.Math.Max(dx, dy));
            }

            xml.WriteStartElement("LookAt");
            xml.WriteElementString("longitude", Format(center.Longitude));
            xml.WriteElementString("latitude", Format(center.Latitude));
            xml.WriteElementString("altitude", "0");
            xml.WriteElementString("heading", "0");
            xml.WriteElementString("tilt", "55");
            xml.WriteElementString("range", Format(extent * 2.5));
            xml.WriteEndElement();
        }

        private static void WriteStyle(XmlWriter xml, string category)
        {
            xml.WriteStartElement("Style");
            xml.WriteAttributeString("id", StyleId(category));
            xml.WriteStartElement("LineStyle");
            xml.WriteElementString("width", "0");
            xml.WriteEndElement();
            xml.WriteStartElement("PolyStyle");
            xml.WriteElementString("color", CategoryColors.ToKml(category));
            xml.WriteElementString("outline", "0");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        private static void WriteLocationPlacemark(XmlWriter xml, (double Longitude, double Latitude) center, IGeoPlacement placement)
        {
            xml.WriteStartElement("Placemark");
            xml.WriteElementString("name", "Model location");
            xml.WriteElementString("description", placement.Description);
            xml.WriteStartElement("Point");
            xml.WriteElementString("coordinates", $"{Format(center.Longitude)},{Format(center.Latitude)},0");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        // Every polygon is built and validated before writing: Google Earth Web rejects the whole file
        // after 100 invalid ones, and an empty MultiGeometry is invalid too.
        private static List<(string Outer, List<string> Holes)> Polygons(GeoMesh mesh, IGeoPlacement placement, AltitudeResolver altitude, KmlMode mode) =>
            mesh.IsEmpty ? new List<(string, List<string>)>()
            : mode == KmlMode.Massing ? MassingPolygon(mesh, placement, altitude)
            : DetailedPolygons(mesh, placement, altitude);

        private static void WritePlacemark(XmlWriter xml, string name, string description, string category,
            List<(string Outer, List<string> Holes)> polygons, string altitudeMode, KmlMode mode)
        {
            if (polygons.Count == 0)
                return;

            xml.WriteStartElement("Placemark");
            xml.WriteElementString("name", name);
            xml.WriteElementString("description", description);
            xml.WriteElementString("styleUrl", "#" + StyleId(category));
            if (polygons.Count > 1)
                xml.WriteStartElement("MultiGeometry");

            foreach (var (outer, holes) in polygons)
            {
                xml.WriteStartElement("Polygon");
                if (mode == KmlMode.Massing)
                    xml.WriteElementString("extrude", "1");
                xml.WriteElementString("altitudeMode", altitudeMode);
                WriteRing(xml, "outerBoundaryIs", outer);
                foreach (var hole in holes)
                    WriteRing(xml, "innerBoundaryIs", hole);
                xml.WriteEndElement();
            }

            if (polygons.Count > 1)
                xml.WriteEndElement(); // MultiGeometry
            xml.WriteEndElement(); // Placemark
        }

        private static List<(string Outer, List<string> Holes)> DetailedPolygons(GeoMesh mesh, IGeoPlacement placement, AltitudeResolver altitude)
        {
            var result = new List<(string, List<string>)>();
            var triangle = new List<Vec3>(3);
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                triangle.Clear();
                for (int corner = 0; corner < 3; corner++)
                    triangle.Add(mesh.Vertices[mesh.Indices[t * 3 + corner]]);
                var outer = Ring(triangle, placement, altitude);
                if (outer != null)
                    result.Add((outer, new List<string>()));
            }

            foreach (var polygon in mesh.Polygons)
            {
                var outer = Ring(polygon.Outer, placement, altitude);
                if (outer == null)
                    continue;
                var holes = polygon.Holes.Select(h => Ring(h, placement, altitude)).Where(h => h != null).Select(h => h!).ToList();
                result.Add((outer, holes));
            }

            return result;
        }

        /// <summary>The element's plan outline at its top height, extruded down to the ground.</summary>
        private static List<(string Outer, List<string> Holes)> MassingPolygon(GeoMesh mesh, IGeoPlacement placement, AltitudeResolver altitude)
        {
            double top = mesh.Vertices.Max(v => v.Z);
            var hull = ConvexHull.Compute(mesh.Vertices.Select(v => (v.X, v.Y)));
            var ring = Ring(hull.Select(p => new Vec3(p.X, p.Y, top)).ToList(), placement, altitude);
            return ring == null
                ? new List<(string, List<string>)>()
                : new List<(string, List<string>)> { (ring, new List<string>()) };
        }

        /// <summary>Smallest face kept, in square metres; anything smaller is a sliver Google Earth rejects.</summary>
        private const double MinFaceArea = 1e-4;

        /// <summary>
        /// Formats a closed ring, or returns null when it isn't a valid KML ring: fewer than three distinct
        /// points once rounded to file precision, or no area (a sliver or a line).
        /// </summary>
        internal static string? Ring(List<Vec3> points, IGeoPlacement placement, AltitudeResolver altitude)
        {
            if (points.Count < 3 || Area(points) < MinFaceArea)
                return null;

            var formatted = new List<string>(points.Count + 1);
            foreach (var v in points)
            {
                var (lon, lat) = placement.ToLonLat(v.X, v.Y);
                // 7 decimals is about 1 cm; more only makes the file bigger.
                string point = Format(lon, 7) + "," + Format(lat, 7) + "," + Format(altitude.Resolve(v.Z), 2);
                if (formatted.Count == 0 || formatted[formatted.Count - 1] != point)
                    formatted.Add(point);
            }

            if (formatted.Count > 1 && formatted[formatted.Count - 1] == formatted[0])
                formatted.RemoveAt(formatted.Count - 1);
            if (formatted.Distinct().Count() < 3)
                return null;

            // A KML ring repeats its first point to close it.
            formatted.Add(formatted[0]);
            return string.Join(" ", formatted);
        }

        /// <summary>Area of a planar ring in 3D (Newell's method), in square metres.</summary>
        private static double Area(List<Vec3> ring)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                x += (a.Y - b.Y) * (a.Z + b.Z);
                y += (a.Z - b.Z) * (a.X + b.X);
                z += (a.X - b.X) * (a.Y + b.Y);
            }

            return System.Math.Sqrt(x * x + y * y + z * z) / 2;
        }

        private static void WriteRing(XmlWriter xml, string boundary, string coordinates)
        {
            xml.WriteStartElement(boundary);
            xml.WriteStartElement("LinearRing");
            xml.WriteElementString("coordinates", coordinates);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        private static string StyleId(string category)
        {
            var id = new StringBuilder("cat-");
            foreach (char c in category)
                id.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            return id.ToString();
        }

        internal static string Format(double value, int decimals = 7) =>
            System.Math.Round(value, decimals).ToString("0.#######", CultureInfo.InvariantCulture);
    }
}
