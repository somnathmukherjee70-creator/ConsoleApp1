<img src="docs/logo.png" alt="" width="96" align="right">

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
   in the hundreds of thousands, they are tried in the grids used around the Revit site location
   (*Manage › Location*): its UTM zone, the neighbouring zones, and local grids such as Dubai
   DLTM. They are used only if they land within 50 km of that location.

The detected system fills the *Coordinate system (EPSG)* field, and the manual latitude/longitude
fields are disabled. You can also type a code yourself, e.g. `32645` (UTM 45N, used across
eastern India). Supported systems: UTM on WGS84 (`326xx` north, `327xx` south), NAD83 (`269xx`),
ETRS89 (`258xx`), GDA94/GDA2020 MGA (`283xx`/`78xx`), Dubai Local TM / DLTM (`3997`) and
Web Mercator (`3857`). These datums are
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

## Install

Works with Revit 2024, 2025 and 2026 on Windows.

1. Download the ready-built add-in: on GitHub open **Actions › Build Revit add-in**, click the
   latest green run, and download **RevitGeoExport** under *Artifacts* (you must be signed in).
2. Unzip it anywhere.
3. Close Revit and double-click **`install.bat`**. It finds every Revit version on the computer
   and copies the right build into `%AppData%\Autodesk\Revit\Addins\<version>\`.
4. Start Revit and click **Always Load** when it asks about *Revit Geo Export*.
5. The button is on the **Add-Ins** tab: **Geo Export › Place on Map**.

To uninstall, run `install.bat -Uninstall` from a command prompt.

<details>
<summary>Manual install, or building it yourself</summary>

Build with the .NET 8 SDK on Windows (Revit is not needed to build; its API comes from NuGet):

```
dotnet build RevitGeoExport -c Release
```

| Output | Revit version |
| --- | --- |
| `RevitGeoExport/bin/Release/net48/` | 2024 |
| `RevitGeoExport/bin/Release/net8.0-windows/` | 2025, 2026 |

Then copy `RevitGeoExport.addin` to `%AppData%\Autodesk\Revit\Addins\<version>\`, and the
`.dll` files from the matching output folder to `%AppData%\Autodesk\Revit\Addins\<version>\RevitGeoExport\`.
If you copied files from a download, right-click each `.dll` › *Properties* › tick **Unblock**.

</details>

## Troubleshooting

- **No Geo Export panel**: check that `RevitGeoExport.addin` is directly in the `Addins\<version>`
  folder, and that the DLLs are in the `RevitGeoExport` sub-folder next to it.
- **"Could not load file or assembly" when Revit starts**: the DLLs are blocked. Run `install.bat`
  again, which unblocks them, or unblock them by hand as described above.
- **Revit 2023 or older**: not supported.

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
