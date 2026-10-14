# Revit Geo Export

A Revit add-in that places your Revit model at its exact real-world position on
**Google Earth** (Pro, Web and mobile) and on a **live satellite/street web map**.

It adds a **Place on Map** button to the **Add-Ins** tab. Clicking it exports:

| File | Open it with | What you get |
| --- | --- | --- |
| `<name>.kmz` | Google Earth Pro (double-click), or earth.google.com › Projects › Import KML file | The full 3D model (every triangle), coloured per category, in one folder per category |
| `<name>_map.html` | Any browser (double-click) | A live 3D map on satellite or street imagery. No server or API key needed |
| `<name>.geojson` | MapLibre, Mapbox, Cesium, ArcGIS, QGIS, geojson.io | Element footprints with `base_height`/`height` for 3D extrusion |

## How the position is worked out

### Automatic: the model is already in real coordinates

If your shared coordinates are real survey eastings/northings, the model places itself and
you only press **Export**. The add-in checks, in order:

1. **The GIS coordinate system assigned in Revit** (set when you acquire coordinates from
   Civil 3D, a DWG with a coordinate system, or ACC). For example `UTM84-45N`, `WGS84.UTM-45N`,
   `UTM83-10`, `ETRS89.UTM-32N` or an EPSG code.
2. **The shared coordinates themselves.** If the project base point has shared coordinates
   in the hundreds of thousands, they are tried as UTM in the zone of the Revit site location
   (*Manage › Location*). They are used only if they land within 50 km of that location.

The detected system fills the *Coordinate system (EPSG)* field, and the manual latitude/longitude
fields are disabled. You can also type a code yourself, e.g. `32645` (UTM 45N, used across
eastern India). Supported systems: UTM on WGS84 (`326xx` north, `327xx` south), NAD83 (`269xx`),
ETRS89 (`258xx`), GDA94/GDA2020 MGA (`283xx`/`78xx`) and Web Mercator (`3857`). These datums are
treated as WGS84, which is within about 2 m. Others, such as British National Grid or US State
Plane, need the manual method below.

In this mode the shared Z is used as the elevation, so *Height of Z = 0 above sea* starts at 0.

### Manual: place by latitude/longitude

Clear the *Coordinate system* field to use this.

1. **Latitude/longitude** come from *Manage › Location* (Revit's site location). You can
   override them in the dialog. Paste them straight from Google Maps (right-click a spot
   and click the coordinates to copy `lat, lon`).
2. **Which point those coordinates describe**: the *Survey Point* (default), the
   *Project Base Point*, or the *Internal Origin*.
3. **Orientation** comes from Revit's shared coordinates, so the rotation set with
   *Manage › Position › Rotate True North* or *Acquire Coordinates* is respected.
   *Rotation adjustment* adds an extra clockwise rotation for fine-tuning against the imagery.
4. **Height**: *Sit on the terrain* puts the lowest point of the model on the ground
   (best for Google Earth). *Above sea level* adds the model's Z to *Height of Z = 0 above sea*.
   *Vertical offset* shifts the model up or down.

The maths converts local east/north metres to WGS84 through ECEF, so it stays accurate
over large sites. UTM uses the Krüger series and matches PROJ to about 1 cm. Tick *Save these coordinates to the Revit project location* to write
the latitude/longitude back into the model.

If the active view is a 3D view, only what that view shows is exported (hidden
categories, section box and phase filter all apply). Otherwise the whole model is exported.
Topography/toposolids, rooms, cameras and linked models are skipped.

## Build

Requires the .NET 8 SDK on Windows (the Revit API is pulled from NuGet, so Revit itself
is not needed to build).

```
dotnet build RevitGeoExport -c Release
```

| Output | Revit version |
| --- | --- |
| `RevitGeoExport/bin/Release/net48/` | 2024 |
| `RevitGeoExport/bin/Release/net8.0-windows/` | 2025, 2026 |

## Install

1. Copy `RevitGeoExport.addin` to `%AppData%\Autodesk\Revit\Addins\<version>\`.
2. Copy the contents of the matching output folder to
   `%AppData%\Autodesk\Revit\Addins\<version>\RevitGeoExport\`.
3. Start Revit and click **Add-Ins › Geo Export › Place on Map**. Approve the
   add-in the first time Revit asks.

## Tests

The geo-referencing and file writers live in `RevitGeoExport.Core`, which has no Revit
dependency, so it can be tested anywhere:

```
dotnet test RevitGeoExport.Core.Tests
```

## Limits

- Big models give big KMZ files, because Google Earth draws every triangle as a polygon.
  Export from a 3D view with only the categories you need, or use Coarse detail.
- The web map shows each element as an extruded footprint (the convex hull of its plan).
  That is right for walls, slabs, columns and boxes, but simplified for curved or
  L-shaped single elements. Google Earth always shows the true 3D shape.
- The web map loads MapLibre from unpkg.com and imagery from Esri/OpenStreetMap, so it
  needs an internet connection.
