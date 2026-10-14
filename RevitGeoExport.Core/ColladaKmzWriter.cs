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
    /// Writes a KMZ holding the full 3D model as COLLADA, with its location in the KML. Google Earth Pro
    /// opens it in place with no input: the same architecture as the GLB, but self-positioning.
    /// </summary>
    public static class ColladaKmzWriter
    {
        private const string ModelPath = "models/model.dae";
        private const string ColladaNamespace = "http://www.collada.org/2005/11/COLLADASchema";

        // Google Earth draws at most 21,845 triangles (65,535 vertices) per mesh, so big categories are split.
        public const int MaxTrianglesPerMesh = 21845;

        public static GlbResult Write(string path, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options)
        {
            using (var file = File.Create(path))
                return Write(file, placement, meshes, options);
        }

        public static GlbResult Write(Stream stream, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options)
        {
            var (categories, result) = GlbWriter.Build(placement, meshes, options);
            bool onGround = options.AltitudeMode == AltitudeMode.OnGround;

            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var kml = zip.CreateEntry("doc.kml", CompressionLevel.Optimal).Open())
                    WriteKml(kml, result, categories, options.DocumentName, onGround);
                using (var dae = zip.CreateEntry(ModelPath, CompressionLevel.Optimal).Open())
                    WriteCollada(dae, categories);
            }

            return result;
        }

        private static void WriteKml(Stream stream, GlbResult result, List<KeyValuePair<string, List<float>>> categories, string name, bool onGround)
        {
            // Frame the camera on the model's extent (glTF x = east, z = -north).
            float extent = 50;
            foreach (var positions in categories.Select(c => c.Value))
                for (int i = 0; i + 2 < positions.Count; i += 3)
                    extent = Math.Max(extent, Math.Max(Math.Abs(positions[i]), Math.Abs(positions[i + 2])) * 2);

            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var xml = XmlWriter.Create(stream, settings))
            {
                xml.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
                xml.WriteStartElement("Document");
                xml.WriteElementString("name", name);
                xml.WriteStartElement("LookAt");
                xml.WriteElementString("longitude", KmlWriter.Format(result.Longitude));
                xml.WriteElementString("latitude", KmlWriter.Format(result.Latitude));
                xml.WriteElementString("altitude", "0");
                xml.WriteElementString("heading", "0");
                xml.WriteElementString("tilt", "60");
                xml.WriteElementString("range", KmlWriter.Format(extent * 2.5, 1));
                xml.WriteEndElement();

                xml.WriteStartElement("Placemark");
                xml.WriteElementString("name", name);
                xml.WriteStartElement("Model");
                xml.WriteElementString("altitudeMode", onGround ? "relativeToGround" : "absolute");
                xml.WriteStartElement("Location");
                xml.WriteElementString("longitude", KmlWriter.Format(result.Longitude));
                xml.WriteElementString("latitude", KmlWriter.Format(result.Latitude));
                xml.WriteElementString("altitude", KmlWriter.Format(onGround ? 0 : result.BaseAltitude, 2));
                xml.WriteEndElement();
                xml.WriteStartElement("Orientation");
                xml.WriteElementString("heading", "0");
                xml.WriteElementString("tilt", "0");
                xml.WriteElementString("roll", "0");
                xml.WriteEndElement();
                xml.WriteStartElement("Scale");
                xml.WriteElementString("x", "1");
                xml.WriteElementString("y", "1");
                xml.WriteElementString("z", "1");
                xml.WriteEndElement();
                xml.WriteStartElement("Link");
                xml.WriteElementString("href", ModelPath);
                xml.WriteEndElement();
                xml.WriteEndElement(); // Model
                xml.WriteEndElement(); // Placemark
                xml.WriteEndElement(); // Document
                xml.WriteEndElement(); // kml
            }
        }

        private static void WriteCollada(Stream stream, List<KeyValuePair<string, List<float>>> categories)
        {
            var settings = new XmlWriterSettings { Indent = false, Encoding = new UTF8Encoding(false) };
            using (var xml = XmlWriter.Create(stream, settings))
            {
                xml.WriteStartElement("COLLADA", ColladaNamespace);
                xml.WriteAttributeString("version", "1.4.1");

                xml.WriteStartElement("asset");
                xml.WriteStartElement("contributor");
                xml.WriteElementString("authoring_tool", "Revit Geo Export");
                xml.WriteEndElement();
                xml.WriteStartElement("unit");
                xml.WriteAttributeString("name", "meter");
                xml.WriteAttributeString("meter", "1");
                xml.WriteEndElement();
                xml.WriteElementString("up_axis", "Z_UP");
                xml.WriteEndElement();

                xml.WriteStartElement("library_effects");
                for (int i = 0; i < categories.Count; i++)
                    WriteEffect(xml, i, categories[i].Key);
                xml.WriteEndElement();

                xml.WriteStartElement("library_materials");
                for (int i = 0; i < categories.Count; i++)
                {
                    xml.WriteStartElement("material");
                    xml.WriteAttributeString("id", $"mat{i}");
                    xml.WriteAttributeString("name", categories[i].Key);
                    xml.WriteStartElement("instance_effect");
                    xml.WriteAttributeString("url", $"#fx{i}");
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();

                var chunks = new List<(int Material, string Name, List<float> Positions, int Start, int Count)>();
                for (int i = 0; i < categories.Count; i++)
                {
                    int triangles = categories[i].Value.Count / 9;
                    for (int start = 0; start < triangles; start += MaxTrianglesPerMesh)
                        chunks.Add((i, categories[i].Key, categories[i].Value, start, Math.Min(MaxTrianglesPerMesh, triangles - start)));
                }

                xml.WriteStartElement("library_geometries");
                for (int g = 0; g < chunks.Count; g++)
                    WriteGeometry(xml, g, chunks[g].Positions, chunks[g].Start, chunks[g].Count);
                xml.WriteEndElement();

                xml.WriteStartElement("library_visual_scenes");
                xml.WriteStartElement("visual_scene");
                xml.WriteAttributeString("id", "scene");
                for (int g = 0; g < chunks.Count; g++)
                {
                    xml.WriteStartElement("node");
                    xml.WriteAttributeString("id", $"node{g}");
                    xml.WriteAttributeString("name", chunks[g].Name);
                    xml.WriteStartElement("instance_geometry");
                    xml.WriteAttributeString("url", $"#geo{g}");
                    xml.WriteStartElement("bind_material");
                    xml.WriteStartElement("technique_common");
                    xml.WriteStartElement("instance_material");
                    xml.WriteAttributeString("symbol", "m");
                    xml.WriteAttributeString("target", $"#mat{chunks[g].Material}");
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
                xml.WriteEndElement();

                xml.WriteStartElement("scene");
                xml.WriteStartElement("instance_visual_scene");
                xml.WriteAttributeString("url", "#scene");
                xml.WriteEndElement();
                xml.WriteEndElement();

                xml.WriteEndElement(); // COLLADA
            }
        }

        private static void WriteEffect(XmlWriter xml, int index, string category)
        {
            var c = CategoryColors.For(category);
            xml.WriteStartElement("effect");
            xml.WriteAttributeString("id", $"fx{index}");
            xml.WriteStartElement("profile_COMMON");
            xml.WriteStartElement("technique");
            xml.WriteAttributeString("sid", "common");
            xml.WriteStartElement("lambert");
            xml.WriteStartElement("diffuse");
            xml.WriteElementString("color", $"{Channel(c.R)} {Channel(c.G)} {Channel(c.B)} 1");
            xml.WriteEndElement();
            if (c.A < 255)
            {
                // Opacity = transparent alpha × transparency (A_ONE mode), as SketchUp writes glass.
                xml.WriteStartElement("transparent");
                xml.WriteAttributeString("opaque", "A_ONE");
                xml.WriteElementString("color", $"1 1 1 {Channel(c.A)}");
                xml.WriteEndElement();
                xml.WriteStartElement("transparency");
                xml.WriteElementString("float", "1");
                xml.WriteEndElement();
            }

            xml.WriteEndElement(); // lambert
            xml.WriteEndElement(); // technique
            // Revit faces point either way; Google Earth would hide the back of each one otherwise.
            xml.WriteStartElement("extra");
            xml.WriteStartElement("technique");
            xml.WriteAttributeString("profile", "GOOGLEEARTH");
            xml.WriteElementString("double_sided", "1");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement(); // profile_COMMON
            xml.WriteEndElement(); // effect
        }

        private static void WriteGeometry(XmlWriter xml, int index, List<float> positions, int start, int count)
        {
            var points = new StringBuilder();
            var normals = new StringBuilder();
            for (int t = start; t < start + count; t++)
            {
                // glTF axes (east, up, -north) to COLLADA Z_UP (east, north, up).
                var v = new double[3][];
                for (int k = 0; k < 3; k++)
                {
                    int o = (t * 3 + k) * 3;
                    v[k] = new double[] { positions[o], -positions[o + 2], positions[o + 1] };
                    points.Append(Mm(v[k][0])).Append(' ').Append(Mm(v[k][1])).Append(' ').Append(Mm(v[k][2])).Append(' ');
                }

                var n = Normal(v[0], v[1], v[2]);
                string normal = $"{Unit(n[0])} {Unit(n[1])} {Unit(n[2])} ";
                normals.Append(normal).Append(normal).Append(normal);
            }

            int vertices = count * 3;
            string id = $"geo{index}";
            xml.WriteStartElement("geometry");
            xml.WriteAttributeString("id", id);
            xml.WriteStartElement("mesh");
            WriteSource(xml, id + "-pos", points.ToString().TrimEnd(), vertices);
            WriteSource(xml, id + "-nrm", normals.ToString().TrimEnd(), vertices);

            xml.WriteStartElement("vertices");
            xml.WriteAttributeString("id", id + "-vtx");
            WriteInput(xml, "POSITION", "#" + id + "-pos");
            WriteInput(xml, "NORMAL", "#" + id + "-nrm");
            xml.WriteEndElement();

            xml.WriteStartElement("triangles");
            xml.WriteAttributeString("material", "m");
            xml.WriteAttributeString("count", count.ToString(CultureInfo.InvariantCulture));
            xml.WriteStartElement("input");
            xml.WriteAttributeString("semantic", "VERTEX");
            xml.WriteAttributeString("source", "#" + id + "-vtx");
            xml.WriteAttributeString("offset", "0");
            xml.WriteEndElement();
            xml.WriteElementString("p", string.Join(" ", Enumerable.Range(0, vertices)));
            xml.WriteEndElement();

            xml.WriteEndElement(); // mesh
            xml.WriteEndElement(); // geometry
        }

        private static void WriteSource(XmlWriter xml, string id, string values, int count)
        {
            xml.WriteStartElement("source");
            xml.WriteAttributeString("id", id);
            xml.WriteStartElement("float_array");
            xml.WriteAttributeString("id", id + "-array");
            xml.WriteAttributeString("count", (count * 3).ToString(CultureInfo.InvariantCulture));
            xml.WriteString(values);
            xml.WriteEndElement();
            xml.WriteStartElement("technique_common");
            xml.WriteStartElement("accessor");
            xml.WriteAttributeString("source", "#" + id + "-array");
            xml.WriteAttributeString("count", count.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("stride", "3");
            foreach (var axis in new[] { "X", "Y", "Z" })
            {
                xml.WriteStartElement("param");
                xml.WriteAttributeString("name", axis);
                xml.WriteAttributeString("type", "float");
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        private static void WriteInput(XmlWriter xml, string semantic, string source)
        {
            xml.WriteStartElement("input");
            xml.WriteAttributeString("semantic", semantic);
            xml.WriteAttributeString("source", source);
            xml.WriteEndElement();
        }

        private static double[] Normal(double[] a, double[] b, double[] c)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
            double length = Math.Sqrt(x * x + y * y + z * z);
            return length == 0 ? new double[] { 0, 0, 1 } : new[] { x / length, y / length, z / length };
        }

        private static string Channel(byte value) => (value / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
        private static string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Unit(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
