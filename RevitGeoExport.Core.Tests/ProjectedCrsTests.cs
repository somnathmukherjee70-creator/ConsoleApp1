using RevitGeoExport.Core;
using Xunit;

namespace RevitGeoExport.Core.Tests;

public class ProjectedCrsTests
{
    // Reference values from PROJ (pyproj 3.x).
    [Theory]
    [InlineData(32645, 22.544808, 88.342558, 638053.3884, 2493751.7931)]   // Kolkata, UTM 45N
    [InlineData(32756, -33.856784, 151.215297, 334900.2613, 6252290.5224)] // Sydney, UTM 56S
    [InlineData(26910, 47.620422, -122.349358, 548889.8272, 5274318.1683)] // Seattle, NAD83 UTM 10N
    [InlineData(25832, 52.516275, 13.377704, 796987.2743, 5827473.0897)]   // Berlin, ETRS89 UTM 32N (far from the central meridian)
    [InlineData(7856, -33.856784, 151.215297, 334900.2613, 6252290.5225)]  // Sydney, GDA2020 MGA 56
    [InlineData(32631, 0.5, 4.1, 622405.3991, 55275.2918)]                 // Near the equator
    [InlineData(3997, 25.149071, 55.203167, 486875.5884, 2782573.6954)]   // Dubai, Dubai Local TM
    [InlineData(32640, 25.149071, 55.203167, 318882.3863, 2782661.5184)]  // Dubai, UTM 40N
    [InlineData(2272, 40.0223719, -79.8866653, 1370146.8139, 258246.0338)] // SW Pennsylvania, PA South (ftUS)
    [InlineData(32129, 40.0223719, -79.8866653, 417621.5841, 78713.5485)]  // SW Pennsylvania, PA South (m)
    [InlineData(2271, 41.79, -77.86, 1938502.4132, 591488.6526)]          // N Pennsylvania, PA North (ftUS)
    [InlineData(3857, 40.689247, -74.044502, -8242596.2587, 4966613.1574)] // New York, Web Mercator
    public void Matches_proj(int epsg, double lat, double lon, double easting, double northing)
    {
        Assert.True(ProjectedCrs.TryCreate(epsg, out var crs));

        var (lonOut, latOut) = crs.ToLonLat(easting, northing);
        Assert.Equal(lat, latOut, 7);  // ~1 cm
        Assert.Equal(lon, lonOut, 7);

        var (e, n) = crs.FromLonLat(lon, lat);
        Assert.Equal(easting, e, 2);
        Assert.Equal(northing, n, 2);
    }

    [Theory]
    [InlineData("EPSG:32645", 32645)]
    [InlineData("32645", 32645)]
    [InlineData("UTM84-45N", 32645)]
    [InlineData("UTM84-56S", 32756)]
    [InlineData("WGS84.UTM-45N", 32645)]
    [InlineData("UTM83-10", 26910)]
    [InlineData("ETRS89.UTM-32N", 25832)]
    [InlineData("GDA2020.UTM-56", 7856)]
    [InlineData("WGS84.PseudoMercator", 3857)]
    [InlineData("DubaiLocalTM", 3997)]
    [InlineData("PA83-SF", 2272)]
    [InlineData("PA83-NF", 2271)]
    [InlineData("PA83-S", 32129)]
    [InlineData("NAD83 / Pennsylvania South (ftUS)", 2272)]
    [InlineData("NAD83 / Pennsylvania North", 32128)]
    [InlineData("3997", 3997)]
    [InlineData("PROJCS[\"WGS 84 / UTM zone 45N\",GEOGCS[\"WGS 84\",AUTHORITY[\"EPSG\",\"4326\"]],AUTHORITY[\"EPSG\",\"32645\"]]", 32645)]
    public void Parses_revit_and_epsg_names(string text, int epsg)
    {
        Assert.True(ProjectedCrs.TryParse(text, out var crs));
        Assert.Equal(epsg, crs.Epsg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2263")]      // New York State Plane, not supported
    [InlineData("27700")]     // British National Grid needs a datum shift, not supported
    [InlineData("LL84")]
    public void Rejects_unsupported(string text) => Assert.False(ProjectedCrs.TryParse(text, out _));

    [Theory]
    [InlineData(88.34, 45)]
    [InlineData(-0.1, 30)]
    [InlineData(180, 60)]
    [InlineData(-180, 1)]
    public void Finds_utm_zone(double lon, int zone) => Assert.Equal(zone, ProjectedCrs.UtmZoneFor(lon));

    [Fact]
    public void Dubai_candidates_include_dltm_and_utm_40()
    {
        var codes = ProjectedCrs.CandidatesNear(25.149, 55.203).Select(c => c.Epsg).ToList();
        Assert.Contains(3997, codes);
        Assert.Equal(32640, codes[0]);
    }

    [Fact]
    public void Pennsylvania_candidates_include_state_plane()
    {
        var codes = ProjectedCrs.CandidatesNear(40.0223719, -79.8866653).Select(c => c.Epsg).ToList();
        Assert.Contains(2272, codes);
        Assert.Contains(32617, codes); // UTM 17N
    }

    [Fact]
    public void Us_survey_feet_typed_into_revit_as_feet_place_correctly()
    {
        // The Revit foot value equals the survey's ftUS number; the exporter hands placements metres.
        Assert.True(ProjectedCrs.TryCreate(2272, out var crs));
        var placement = new ProjectedPlacement(crs);
        var (lon, lat) = placement.ToLonLat(1370146.8139 * 0.3048, 258246.0338 * 0.3048);

        Assert.Equal(40.0223719, lat, 7);
        Assert.Equal(-79.8866653, lon, 7);
    }
}
