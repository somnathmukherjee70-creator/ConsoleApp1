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
        public static void WriteKmz(string path, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options)
        {
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("doc.kml", CompressionLevel.Optimal).Open())
            {
                WriteKml(entry, placement, meshes, options);
            }
        }

        public static void WriteKml(Stream stream, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options)
        {
            var altitude = new AltitudeResolver(options, placement, meshes);
            var center = placement.Center(meshes);
            string altitudeMode = altitude.IsRelativeToGround ? "relativeToGround" : "absolute";

            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var xml = XmlWriter.Create(stream, settings))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
                xml.WriteStartElement("Document");
                xml.WriteElementString("name", options.DocumentName);
                xml.WriteElementString("open", "1");

                WriteLookAt(xml, center, meshes);

                var categories = meshes.Select(m => m.Category).Distinct().OrderBy(c => c).ToList();
                foreach (var category in categories)
                    WriteStyle(xml, category);

                WriteLocationPlacemark(xml, center, placement);

                foreach (var group in meshes.GroupBy(m => m.Category).OrderBy(g => g.Key))
                {
                    xml.WriteStartElement("Folder");
                    xml.WriteElementString("name", group.Key);
                    foreach (var mesh in group)
                        WriteMeshPlacemark(xml, mesh, placement, altitude, altitudeMode);
                    xml.WriteEndElement();
                }

                xml.WriteEndElement(); // Document
                xml.WriteEndElement(); // kml
            }
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

        private static void WriteMeshPlacemark(XmlWriter xml, GeoMesh mesh, IGeoPlacement placement, AltitudeResolver altitude, string altitudeMode)
        {
            if (mesh.TriangleCount == 0)
                return;

            xml.WriteStartElement("Placemark");
            xml.WriteElementString("name", mesh.Name);
            xml.WriteElementString("description", $"{mesh.Category} · Element ID {mesh.ElementId}");
            xml.WriteElementString("styleUrl", "#" + StyleId(mesh.Category));
            xml.WriteStartElement("MultiGeometry");

            var ring = new StringBuilder();
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                ring.Clear();
                for (int corner = 0; corner <= 3; corner++)
                {
                    // A KML ring repeats its first point to close it.
                    var v = mesh.Vertices[mesh.Indices[t * 3 + corner % 3]];
                    var (lon, lat) = placement.ToLonLat(v.X, v.Y);
                    if (corner > 0)
                        ring.Append(' ');
                    ring.Append(Format(lon, 8)).Append(',').Append(Format(lat, 8)).Append(',').Append(Format(altitude.Resolve(v.Z), 3));
                }

                xml.WriteStartElement("Polygon");
                xml.WriteElementString("altitudeMode", altitudeMode);
                xml.WriteStartElement("outerBoundaryIs");
                xml.WriteStartElement("LinearRing");
                xml.WriteElementString("coordinates", ring.ToString());
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();
            }

            xml.WriteEndElement(); // MultiGeometry
            xml.WriteEndElement(); // Placemark
        }

        private static string StyleId(string category)
        {
            var id = new StringBuilder("cat-");
            foreach (char c in category)
                id.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            return id.ToString();
        }

        internal static string Format(double value, int decimals = 8) =>
            System.Math.Round(value, decimals).ToString("0.########", CultureInfo.InvariantCulture);
    }
}
