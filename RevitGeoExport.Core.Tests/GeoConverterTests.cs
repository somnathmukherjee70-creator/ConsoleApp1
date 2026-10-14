using RevitGeoExport.Core;
using Xunit;

namespace RevitGeoExport.Core.Tests;

public class GeoConverterTests
{
    // Kolkata, Victoria Memorial.
    private static readonly GeoAnchor Anchor = new(22.544808, 88.342558);

    [Fact]
    public void Origin_maps_to_anchor()
    {
        var (lon, lat) = new GeoConverter(Anchor).ToLonLat(0, 0);

        Assert.Equal(88.342558, lon, 9);
        Assert.Equal(22.544808, lat, 9);
    }

    [Fact]
    public void Offsets_match_known_distances()
    {
        var converter = new GeoConverter(Anchor);

        // At 22.54°N one degree of latitude is ~110,760 m and one degree of longitude ~102,850 m.
        var (_, latNorth) = converter.ToLonLat(0, 1000);
        var (lonEast, _) = converter.ToLonLat(1000, 0);

        Assert.InRange((latNorth - 22.544808) * 110_760, 999.0, 1001.0);
        Assert.InRange((lonEast - 88.342558) * 102_850, 999.0, 1001.0);
    }

    [Fact]
    public void Rotation_is_clockwise_from_above()
    {
        // Rotating 90° clockwise moves a point north of the anchor to the east of it.
        var converter = new GeoConverter(new GeoAnchor(51.5, -0.12, rotationDegrees: 90));
        var (lon, lat) = converter.ToLonLat(0, 100);

        Assert.True(lon > -0.12);
        Assert.Equal(51.5, lat, 5);
    }

    [Fact]
    public void Invalid_coordinates_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoAnchor(91, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoAnchor(0, 181));
    }
}
