using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RevitGeoExport.Core;
using Xunit;

namespace RevitGeoExport.Core.Tests;

public class WriterTests
{
    private static readonly GeoAnchor Anchor = new(22.544808, 88.342558, elevationMeters: 10);

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
