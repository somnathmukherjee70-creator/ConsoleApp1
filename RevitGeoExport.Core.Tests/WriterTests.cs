using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RevitGeoExport.Core;
using Xunit;

namespace RevitGeoExport.Core.Tests;

public class WriterTests
{
    private static readonly IGeoPlacement Anchor = new GeoConverter(new GeoAnchor(22.544808, 88.342558, elevationMeters: 10));

    private static List<GeoMesh> Box()
    {
        // A 10 m x 4 m x 3 m box whose base sits at z = 2 m.
        var mesh = new GeoMesh("123", "Basic Wall <Generic>", "Walls");
        var p = new[]
        {
            new Vec3(0, 0, 2), new Vec3(10, 0, 2), new Vec3(10, 4, 2), new Vec3(0, 4, 2),
            new Vec3(0, 0, 5), new Vec3(10, 0, 5), new Vec3(10, 4, 5), new Vec3(0, 4, 5),
        };
        int[][] faces = { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } };
        foreach (var f in faces)
        {
            mesh.AddTriangle(p[f[0]], p[f[1]], p[f[2]]);
            mesh.AddTriangle(p[f[0]], p[f[2]], p[f[3]]);
        }

        return new List<GeoMesh> { mesh };
    }

    [Fact]
    public void Kml_has_one_closed_polygon_per_triangle_on_the_ground()
    {
        using var stream = new MemoryStream();
        KmlWriter.WriteKml(stream, Anchor, Box(), new ExportOptions());

        var doc = XDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        var rings = doc.Descendants(kml + "coordinates").Skip(1).Select(c => c.Value.Split(' ')).ToList(); // first is the anchor point

        Assert.Equal(12, rings.Count);
        Assert.All(rings, r => Assert.Equal(r[0], r[3]));

        var heights = rings.SelectMany(r => r).Select(c => double.Parse(c.Split(',')[2], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(0, heights.Min());
        Assert.Equal(3, heights.Max());
        Assert.All(doc.Descendants(kml + "altitudeMode"), m => Assert.Equal("relativeToGround", m.Value));
    }

    [Fact]
    public void Kml_above_sea_level_adds_anchor_elevation()
    {
        using var stream = new MemoryStream();
        KmlWriter.WriteKml(stream, Anchor, Box(), new ExportOptions { AltitudeMode = AltitudeMode.AboveSeaLevel });

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("<altitudeMode>absolute</altitudeMode>", text);
        Assert.Contains(",12 ", text); // base: 10 m anchor + 2 m
        Assert.Contains(",15 ", text); // top: 10 m anchor + 5 m
    }

    [Fact]
    public void Kmz_contains_doc_kml()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".kmz");
        try
        {
            KmlWriter.WriteKmz(path, Anchor, Box(), new ExportOptions());
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            Assert.Equal("doc.kml", Assert.Single(zip.Entries).FullName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GeoJson_footprint_is_closed_hull_with_heights()
    {
        var json = GeoJsonWriter.ToGeoJson(Anchor, Box(), new ExportOptions());
        using var doc = JsonDocument.Parse(json);

        var feature = Assert.Single(doc.RootElement.GetProperty("features").EnumerateArray());
        var props = feature.GetProperty("properties");
        Assert.Equal("Walls", props.GetProperty("category").GetString());
        Assert.Equal(0, props.GetProperty("base_height").GetDouble());
        Assert.Equal(3, props.GetProperty("height").GetDouble());

        var ring = feature.GetProperty("geometry").GetProperty("coordinates")[0].EnumerateArray().ToList();
        Assert.Equal(5, ring.Count); // 4 corners + closing point
        Assert.Equal(ring[0].ToString(), ring[4].ToString());
    }

    [Fact]
    public void Web_map_embeds_model_and_escapes_names()
    {
        var meshes = Box();
        var tricky = new GeoMesh("9", "</script><b>\"x\"", "Walls");
        tricky.AddTriangle(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));
        meshes.Add(tricky);

        var html = WebMapWriter.ToHtml(Anchor, meshes, new ExportOptions { DocumentName = "Tower & Annex" });

        Assert.DoesNotContain("__MODEL_", html);
        Assert.DoesNotContain("</script><b>", html);
        Assert.Contains("Tower &amp; Annex", html);
        Assert.Contains("\"elementId\":\"123\"", html);
    }
}

public class ProjectedWriterTests
{
    [Fact]
    public void Model_in_utm_coordinates_lands_on_its_real_position()
    {
        // A 20 m box at Victoria Memorial, Kolkata, modelled directly in UTM 45N eastings/northings.
        var mesh = new GeoMesh("1", "Block", "Walls");
        double e = 638053.3884, n = 2493751.7931;
        mesh.AddTriangle(new Vec3(e - 10, n - 10, 0), new Vec3(e + 10, n - 10, 0), new Vec3(e + 10, n + 10, 6));
        mesh.AddTriangle(new Vec3(e - 10, n - 10, 0), new Vec3(e + 10, n + 10, 6), new Vec3(e - 10, n + 10, 6));

        var placement = new ProjectedPlacement(ProjectedCrs.Utm(45, south: false));
        using var doc = JsonDocument.Parse(GeoJsonWriter.ToGeoJson(placement, new[] { mesh }, new ExportOptions()));

        var center = doc.RootElement.GetProperty("center");
        Assert.Equal(22.544808, center.GetProperty("latitude").GetDouble(), 6);
        Assert.Equal(88.342558, center.GetProperty("longitude").GetDouble(), 6);
        Assert.Contains("EPSG:32645", doc.RootElement.GetProperty("placement").GetString());
    }
}

public class KmlSizeTests
{
    private static readonly IGeoPlacement Placement = new GeoConverter(new GeoAnchor(40.0223719, -79.8866653));

    private static GeoMesh Square(string id, double size)
    {
        var mesh = new GeoMesh(id, "Element " + id, "Walls");
        mesh.AddTriangle(new Vec3(0, 0, 0), new Vec3(size, 0, 0), new Vec3(size, 0, size));
        return mesh;
    }

    [Fact]
    public void Wall_face_with_window_is_one_polygon_with_a_hole()
    {
        var mesh = new GeoMesh("1", "Wall", "Walls");
        var outer = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(10, 0, 3), new(0, 0, 3) };
        var window = new List<Vec3> { new(4, 0, 1), new(6, 0, 1), new(6, 0, 2), new(4, 0, 2) };
        mesh.AddPolygon(new GeoPolygon(outer, new List<List<Vec3>> { window }));

        using var stream = new MemoryStream();
        KmlWriter.WriteKml(stream, Placement, new[] { mesh }, new ExportOptions());
        var doc = XDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        XNamespace kml = "http://www.opengis.net/kml/2.2";

        var polygon = Assert.Single(doc.Descendants(kml + "Polygon"));
        Assert.Single(polygon.Elements(kml + "outerBoundaryIs"));
        var hole = Assert.Single(polygon.Elements(kml + "innerBoundaryIs")).Value.Split(' ');
        Assert.Equal(5, hole.Length);
        Assert.Equal(hole[0], hole[4]);
    }

    [Fact]
    public void Over_budget_keeps_the_largest_elements()
    {
        var meshes = new[] { Square("small", 0.1), Square("big", 20), Square("medium", 2) };

        using var stream = new MemoryStream();
        var result = KmlWriter.WriteKml(stream, Placement, meshes, new ExportOptions { MaxKmlPoints = 8 });
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Equal(2, result.Written);
        Assert.Equal(1, result.Omitted);
        Assert.Contains("Element big", text);
        Assert.Contains("Element medium", text);
        Assert.DoesNotContain("Element small", text);
        Assert.Contains("1 of the smallest elements were left out", text);
    }

    [Fact]
    public void Output_is_compact()
    {
        using var stream = new MemoryStream();
        KmlWriter.WriteKml(stream, Placement, new[] { Square("1", 5) }, new ExportOptions());
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.DoesNotContain("\n  ", text);
        // Longitudes have at most 7 decimals.
        Assert.Matches(@"-79\.\d{1,7},", text);
        Assert.DoesNotMatch(@"-79\.\d{8}", text);
    }

    [Fact]
    public void Slivers_and_collapsed_faces_are_not_written()
    {
        var mesh = new GeoMesh("1", "Panel", "Curtain Panels");
        mesh.AddTriangle(new Vec3(0, 0, 0), new Vec3(5, 0, 0), new Vec3(10, 0, 0));          // a line: no area
        mesh.AddTriangle(new Vec3(0, 0, 0), new Vec3(0.001, 0, 0), new Vec3(0, 0, 0.001));  // collapses at 1 cm precision
        var sliverOnly = new GeoMesh("2", "Sliver", "Curtain Panels");
        sliverOnly.AddTriangle(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(2, 0, 0));
        mesh.AddTriangle(new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 0, 2));          // valid

        using var stream = new MemoryStream();
        KmlWriter.WriteKml(stream, Placement, new[] { mesh, sliverOnly }, new ExportOptions());
        var doc = XDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        XNamespace kml = "http://www.opengis.net/kml/2.2";

        Assert.Single(doc.Descendants(kml + "Polygon"));
        Assert.DoesNotContain(doc.Descendants(kml + "name"), n => n.Value == "Sliver");
        Assert.Empty(doc.Descendants(kml + "MultiGeometry"));
    }

    [Fact]
    public void Massing_writes_one_extruded_outline_per_element()
    {
        var mesh = new GeoMesh("1", "Tower", "Walls");
        mesh.AddTriangle(new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 30));
        mesh.AddTriangle(new Vec3(0, 0, 0), new Vec3(10, 10, 30), new Vec3(0, 10, 30));

        using var stream = new MemoryStream();
        KmlWriter.WriteKml(stream, Placement, new[] { mesh }, new ExportOptions { KmlMode = KmlMode.Massing });
        var doc = XDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        XNamespace kml = "http://www.opengis.net/kml/2.2";

        var polygon = Assert.Single(doc.Descendants(kml + "Polygon"));
        Assert.Equal("1", polygon.Element(kml + "extrude")!.Value);
        var heights = polygon.Descendants(kml + "coordinates").Single().Value.Split(' ').Select(c => c.Split(',')[2]).Distinct();
        Assert.Equal("30", Assert.Single(heights));
    }
}

public class GlbWriterTests
{
    private static (System.Text.Json.JsonDocument Json, byte[] Bin) Read(byte[] glb)
    {
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(glb, 4));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        int jsonLength = (int)BitConverter.ToUInt32(glb, 12);
        Assert.Equal(0x4E4F534Au, BitConverter.ToUInt32(glb, 16));
        var json = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
        int binStart = 20 + jsonLength;
        int binLength = (int)BitConverter.ToUInt32(glb, binStart);
        Assert.Equal(0x004E4942u, BitConverter.ToUInt32(glb, binStart + 4));
        return (json, glb.Skip(binStart + 8).Take(binLength).ToArray());
    }

    [Fact]
    public void Model_is_centred_on_its_location_with_north_along_minus_z()
    {
        // A 10 m wide, 20 m deep (north), 6 m tall wall pair in UTM 45N, 2 m above the datum.
        double e = 638053.3884, n = 2493751.7931;
        var walls = new GeoMesh("1", "Wall", "Walls");
        walls.AddTriangle(new Vec3(e - 5, n - 10, 2), new Vec3(e + 5, n - 10, 2), new Vec3(e + 5, n - 10, 8));
        walls.AddTriangle(new Vec3(e - 5, n + 10, 2), new Vec3(e + 5, n + 10, 2), new Vec3(e - 5, n + 10, 8));
        var glass = new GeoMesh("2", "Window", "Windows");
        glass.AddPolygon(new GeoPolygon(new List<Vec3> { new(e, n, 3), new(e + 1, n, 3), new(e + 1, n, 4) }));
        glass.PolygonTriangles.AddRange(new[] { new Vec3(e, n, 3), new Vec3(e + 1, n, 3), new Vec3(e + 1, n, 4) });

        using var stream = new MemoryStream();
        var result = GlbWriter.Write(stream, new ProjectedPlacement(ProjectedCrs.Utm(45, false)), new[] { walls, glass }, new ExportOptions());
        var (json, bin) = Read(stream.ToArray());

        Assert.Equal(22.544808, result.Latitude, 6);
        Assert.Equal(88.342558, result.Longitude, 6);
        Assert.Equal(3, result.Triangles);

        var root = json.RootElement;
        var location = root.GetProperty("asset").GetProperty("extras");
        Assert.Equal(22.544808, location.GetProperty("latitude").GetDouble(), 6);
        Assert.Equal(88.342558, location.GetProperty("longitude").GetDouble(), 6);
        Assert.Equal(2, root.GetProperty("meshes").GetArrayLength());
        var wallAccessor = root.GetProperty("accessors")[0]; // categories sorted: Walls, Windows
        Assert.Equal(6, wallAccessor.GetProperty("count").GetInt32());
        var min = wallAccessor.GetProperty("min").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var max = wallAccessor.GetProperty("max").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        // UTM grid north is ~0.5° off true north here; the model is turned to true north, which moves
        // the 20 m-long sides' ends by up to ~9 cm east-west.
        Assert.InRange(min[0], -5.15, -4.85); Assert.InRange(max[0], 4.85, 5.15);   // east
        Assert.Equal(0, min[1], 3); Assert.Equal(6, max[1], 2);                       // up, lowest point on the ground
        Assert.InRange(min[2], -10.05, -9.95); Assert.InRange(max[2], 9.95, 10.05);  // north is -Z

        Assert.Equal((3 + 6) * 12, root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32());
        Assert.True(bin.Length >= (3 + 6) * 12);
        Assert.Equal("BLEND", root.GetProperty("materials")[1].GetProperty("alphaMode").GetString());
    }
}

public class ColladaKmzWriterTests
{
    [Fact]
    public void Kmz_places_a_collada_model_at_the_building_and_splits_big_meshes()
    {
        double e = 638053.3884, n = 2493751.7931;
        var wall = new GeoMesh("1", "Wall", "Walls");
        // More triangles than Google Earth allows in one mesh, to force a split.
        for (int i = 0; i < 22000; i++)
            wall.AddTriangle(new Vec3(e + i * 0.001, n, 0), new Vec3(e + i * 0.001 + 1, n, 0), new Vec3(e + i * 0.001, n, 3));

        using var stream = new MemoryStream();
        var result = ColladaKmzWriter.Write(stream, new ProjectedPlacement(ProjectedCrs.Utm(45, false)), new[] { wall }, new ExportOptions());
        Assert.Equal(22000, result.Triangles);

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(stream.ToArray()));
        Assert.Equal(new[] { "doc.kml", "models/model.dae" }, zip.Entries.Select(x => x.FullName).ToArray());

        XNamespace kml = "http://www.opengis.net/kml/2.2";
        var doc = XDocument.Load(zip.GetEntry("doc.kml")!.Open());
        var location = doc.Descendants(kml + "Model").Single().Element(kml + "Location")!;
        Assert.Equal(22.5448, double.Parse(location.Element(kml + "latitude")!.Value, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal("models/model.dae", doc.Descendants(kml + "href").Single().Value);

        XNamespace dae = "http://www.collada.org/2005/11/COLLADASchema";
        var model = XDocument.Load(zip.GetEntry("models/model.dae")!.Open());
        Assert.Equal("Z_UP", model.Descendants(dae + "up_axis").Single().Value);
        var counts = model.Descendants(dae + "triangles").Select(t => int.Parse(t.Attribute("count")!.Value)).ToList();
        Assert.Equal(new[] { 21845, 155 }, counts);
        Assert.All(counts, c => Assert.True(c <= ColladaKmzWriter.MaxTrianglesPerMesh));
        Assert.Equal("1", model.Descendants(dae + "double_sided").First().Value);
    }
}

public class GoogleEarthWebKmlTests
{
    [Fact]
    public void Earth_web_kml_stays_within_its_import_limits()
    {
        var placement = new GeoConverter(new GeoAnchor(40.0223719, -79.8866653));
        var rnd = new Random(3);
        var meshes = new List<GeoMesh>();
        foreach (var category in new[] { "Walls", "Windows", "Curtain Panels" })
        {
            for (int i = 0; i < 5000; i++)
            {
                var m = new GeoMesh($"{category}{i}", "Element", category);
                double x = rnd.NextDouble() * 60, y = rnd.NextDouble() * 40, size = 0.2 + rnd.NextDouble() * 3;
                for (int t = 0; t < 6; t++)
                    m.AddTriangle(new Vec3(x, y, t), new Vec3(x + size, y, t), new Vec3(x, y + size * 0.5, t + size));
                meshes.Add(m);
            }
        }

        using var stream = new MemoryStream();
        var result = KmlWriter.WriteKml(stream, placement, meshes, ExportOptions.ForGoogleEarthWeb(new ExportOptions()));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        XNamespace kml = "http://www.opengis.net/kml/2.2";

        int features = doc.Descendants(kml + "Placemark").Count();
        int vertices = doc.Descendants(kml + "coordinates").Sum(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(features <= 10_000, $"{features} features");
        Assert.True(vertices <= 250_000, $"{vertices} vertices");
        Assert.Equal(1 + 3, features); // location pin + one per category
        Assert.True(result.Omitted > 0);
    }
}
