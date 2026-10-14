using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitGeoExport.Core
{
    /// <summary>Where to place an exported GLB model, as Google Earth's import asks for it.</summary>
    public sealed class GlbResult
    {
        public GlbResult(double latitude, double longitude, int triangles, int written, int omitted, double baseAltitude = 0)
        {
            BaseAltitude = baseAltitude;
            Latitude = latitude;
            Longitude = longitude;
            Triangles = triangles;
            Written = written;
            Omitted = omitted;
        }

        /// <summary>The model's origin: the centre of its plan, at its lowest point.</summary>
        public double Latitude { get; }
        public double Longitude { get; }
        public int Triangles { get; }
        public int Written { get; }
        public int Omitted { get; }

        /// <summary>Altitude of the model's lowest point: 0 when sitting on the terrain, else metres above sea level.</summary>
        public double BaseAltitude { get; }

        public string Instructions(string fileName) => string.Format(CultureInfo.InvariantCulture,
            "Google Earth Web: New project › Import › 3D model (.glb) › {0}{1}" +
            "Latitude {2:0.0000000}, longitude {3:0.0000000}, altitude 0 m relative to ground, heading 0°, scale 1.{1}" +
            "The model is already turned to true north; if it looks rotated, adjust the heading.",
            fileName, Environment.NewLine, Latitude, Longitude);
    }

    /// <summary>
    /// Writes the full 3D model as a binary glTF (GLB), the 3D format Google Earth Web imports. The model
    /// is in metres around its own origin (the plan centre at its lowest point), with glTF's axes: +X east,
    /// +Y up, -Z north. One mesh per Revit category, coloured like the other exports.
    /// </summary>
    public static class GlbWriter
    {
        private const float MinTriangleArea = 1e-6f;

        public static GlbResult Write(string path, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> all, ExportOptions options)
        {
            using (var file = File.Create(path))
                return Write(file, placement, all, options);
        }

        public static GlbResult Write(Stream stream, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> all, ExportOptions options)
        {
            var model = Build(placement, all, options);
            WriteGlb(stream, model.Categories, options.DocumentName, model.Result.Latitude, model.Result.Longitude);
            return model.Result;
        }

        /// <summary>
        /// Triangles per category in model space (glTF axes: +X east, +Y up, -Z north, metres around the
        /// plan centre, lowest point at 0), three vertices per triangle so faces shade flat.
        /// </summary>
        internal static (List<KeyValuePair<string, List<float>>> Categories, GlbResult Result) Build(
            IGeoPlacement placement, IReadOnlyCollection<GeoMesh> all, ExportOptions options)
        {
            var meshes = KmlWriter.WithinBudget(all, options.MaxModelTriangles, m => m.ModelTriangleCount);
            var altitude = new AltitudeResolver(options, placement, meshes);
            var (lon0, lat0) = placement.Center(meshes);
            var frame = new TangentFrame(lon0, lat0);
            double ground = meshes.Count == 0 ? 0 : meshes.SelectMany(m => m.Vertices).Min(v => altitude.Resolve(v.Z));

            var categories = new SortedDictionary<string, List<float>>(StringComparer.Ordinal);
            int triangles = 0;
            foreach (var mesh in meshes)
            {
                if (!categories.TryGetValue(mesh.Category, out var positions))
                    categories[mesh.Category] = positions = new List<float>();

                foreach (var (a, b, c) in Triangles(mesh))
                {
                    var pa = ToModel(a);
                    var pb = ToModel(b);
                    var pc = ToModel(c);
                    if (Area(pa, pb, pc) < MinTriangleArea)
                        continue;
                    positions.AddRange(pa);
                    positions.AddRange(pb);
                    positions.AddRange(pc);
                    triangles++;
                }
            }

            return (categories.Where(c => c.Value.Count > 0).ToList(),
                new GlbResult(lat0, lon0, triangles, meshes.Count, all.Count - meshes.Count, ground));

            float[] ToModel(Vec3 v)
            {
                var (lon, lat) = placement.ToLonLat(v.X, v.Y);
                var (east, north) = frame.ToLocal(lon, lat);
                return new[] { (float)east, (float)(altitude.Resolve(v.Z) - ground), (float)-north };
            }
        }

        internal static string Num(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static IEnumerable<(Vec3, Vec3, Vec3)> Triangles(GeoMesh mesh)
        {
            for (int t = 0; t < mesh.TriangleCount; t++)
                yield return (mesh.Vertices[mesh.Indices[t * 3]], mesh.Vertices[mesh.Indices[t * 3 + 1]], mesh.Vertices[mesh.Indices[t * 3 + 2]]);
            for (int i = 0; i + 2 < mesh.PolygonTriangles.Count; i += 3)
                yield return (mesh.PolygonTriangles[i], mesh.PolygonTriangles[i + 1], mesh.PolygonTriangles[i + 2]);
        }

        private static float Area(float[] a, float[] b, float[] c)
        {
            float ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            float vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            float x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
            return (float)Math.Sqrt(x * x + y * y + z * z) / 2;
        }

        private static void WriteGlb(Stream stream, List<KeyValuePair<string, List<float>>> categories, string name, double latitude, double longitude)
        {
            var bin = new MemoryStream();
            var json = new StringBuilder();
            // glTF has no standard place for a geographic location; record it in the asset's extras
            // so tools (and people) can find where the model's origin belongs.
            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"Revit Geo Export\",\"extras\":{")
                .Append("\"latitude\":").Append(latitude.ToString("0.0000000", CultureInfo.InvariantCulture))
                .Append(",\"longitude\":").Append(longitude.ToString("0.0000000", CultureInfo.InvariantCulture))
                .Append(",\"altitude\":0,\"altitudeReference\":\"relativeToGround\",\"heading\":0")
                .Append(",\"axes\":\"+X east, +Y up, -Z north, metres\"}},\"scene\":0,");
            json.Append("\"scenes\":[{\"name\":").Append(GeoJsonWriter.Quote(name)).Append(",\"nodes\":[")
                .Append(string.Join(",", Enumerable.Range(0, categories.Count))).Append("]}],");

            var nodes = new List<string>();
            var meshes = new List<string>();
            var materials = new List<string>();
            var accessors = new List<string>();
            var views = new List<string>();
            using (var writer = new BinaryWriter(bin, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = 0; i < categories.Count; i++)
                {
                    var (category, positions) = (categories[i].Key, categories[i].Value);
                    long offset = bin.Position;
                    foreach (float f in positions)
                        writer.Write(f);

                    var min = new float[3];
                    var max = new float[3];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        min[axis] = float.MaxValue;
                        max[axis] = float.MinValue;
                        for (int p = axis; p < positions.Count; p += 3)
                        {
                            min[axis] = Math.Min(min[axis], positions[p]);
                            max[axis] = Math.Max(max[axis], positions[p]);
                        }
                    }

                    views.Add($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{positions.Count * 4},\"target\":34962}}");
                    accessors.Add($"{{\"bufferView\":{i},\"componentType\":5126,\"count\":{positions.Count / 3},\"type\":\"VEC3\"," +
                        $"\"min\":[{Num(min[0])},{Num(min[1])},{Num(min[2])}],\"max\":[{Num(max[0])},{Num(max[1])},{Num(max[2])}]}}");
                    materials.Add(Material(category));
                    meshes.Add($"{{\"name\":{GeoJsonWriter.Quote(category)},\"primitives\":[{{\"attributes\":{{\"POSITION\":{i}}},\"material\":{i},\"mode\":4}}]}}");
                    nodes.Add($"{{\"name\":{GeoJsonWriter.Quote(category)},\"mesh\":{i}}}");
                }
            }

            json.Append("\"nodes\":[").Append(string.Join(",", nodes)).Append("],");
            json.Append("\"meshes\":[").Append(string.Join(",", meshes)).Append("],");
            json.Append("\"materials\":[").Append(string.Join(",", materials)).Append("],");
            json.Append("\"accessors\":[").Append(string.Join(",", accessors)).Append("],");
            json.Append("\"bufferViews\":[").Append(string.Join(",", views)).Append("],");
            json.Append("\"buffers\":[{\"byteLength\":").Append(bin.Length).Append("}]}");

            byte[] jsonBytes = Pad(Encoding.UTF8.GetBytes(json.ToString()), (byte)' ');
            byte[] binBytes = Pad(bin.ToArray(), 0);

            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x46546C67u); // "glTF"
                writer.Write(2u);
                writer.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binBytes.Length));
                writer.Write((uint)jsonBytes.Length);
                writer.Write(0x4E4F534Au); // "JSON"
                writer.Write(jsonBytes);
                writer.Write((uint)binBytes.Length);
                writer.Write(0x004E4942u); // "BIN\0"
                writer.Write(binBytes);
            }
        }

        private static string Material(string category)
        {
            var c = CategoryColors.For(category);
            // glTF colours are linear; the category colours are sRGB.
            string Linear(byte v) => Num((float)Math.Pow(v / 255.0, 2.2));
            string alpha = Num(c.A / 255f);
            string mode = c.A < 255 ? ",\"alphaMode\":\"BLEND\"" : "";
            return $"{{\"name\":{GeoJsonWriter.Quote(category)},\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{Linear(c.R)},{Linear(c.G)},{Linear(c.B)},{alpha}]," +
                $"\"metallicFactor\":0,\"roughnessFactor\":0.9}},\"doubleSided\":true{mode}}}";
        }

        private static byte[] Pad(byte[] data, byte fill)
        {
            int padded = (data.Length + 3) & ~3;
            if (padded == data.Length)
                return data;
            var result = new byte[padded];
            Array.Copy(data, result, data.Length);
            for (int i = data.Length; i < padded; i++)
                result[i] = fill;
            return result;
        }


        /// <summary>East/north metres around a point, using the WGS84 radii there (millimetre-accurate at building scale).</summary>
        private sealed class TangentFrame
        {
            private const double A = 6378137.0;
            private const double E2 = 0.00669437999014;
            private readonly double _lon0, _lat0, _metersPerLon, _metersPerLat;

            public TangentFrame(double lon0, double lat0)
            {
                _lon0 = lon0;
                _lat0 = lat0;
                double phi = lat0 * Math.PI / 180;
                double s = 1 - E2 * Math.Sin(phi) * Math.Sin(phi);
                _metersPerLon = Math.PI / 180 * A * Math.Cos(phi) / Math.Sqrt(s);
                _metersPerLat = Math.PI / 180 * A * (1 - E2) / Math.Pow(s, 1.5);
            }

            public (double East, double North) ToLocal(double lon, double lat) =>
                ((lon - _lon0) * _metersPerLon, (lat - _lat0) * _metersPerLat);
        }
    }
}
