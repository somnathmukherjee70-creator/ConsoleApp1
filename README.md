# Sightline: free online IFC viewer

A website where people open IFC building models in their browser, review them and export reports.
Built with [three.js](https://threejs.org) and [web-ifc](https://github.com/ThatOpen/engine_web-ifc).
Files are processed in the visitor's browser and never uploaded, so there is no server to run.

```
docs/
  index.html        landing page (search-engine friendly)
  viewer/index.html the viewer app (one file, no build step)
  assets/           favicon and the screenshot used on the landing page
  robots.txt, sitemap.xml
```

## Publish it with GitHub Pages (free)

1. The repository must be public (or on a paid GitHub plan).
2. On GitHub: **Settings → Pages → Build and deployment → Deploy from a branch**.
3. Choose the branch with this code and the **`/docs`** folder, then **Save**.
4. After a minute the site is live at https://somnathmukherjee70-creator.github.io/Sightline/

To make it show up in Google, add the site in [Google Search Console](https://search.google.com/search-console)
and submit `sitemap.xml`. If you later use your own domain, update the addresses in
`docs/index.html`, `docs/viewer/index.html`, `docs/robots.txt` and `docs/sitemap.xml`.

## Run it locally

```bash
cd docs
python3 -m http.server 8000   # or: npx serve .
```

Then open http://localhost:8000 for the landing page, or http://localhost:8000/viewer/ for the viewer. The page opens on a built-in sample building (Riverside Office Block).
Use **Open IFC** to replace the model with your own `.ifc` files (IFC2X3 / IFC4), or **Add IFC**
(or drag files onto the viewport) to merge more files into the current model.
Libraries are loaded from cdn.jsdelivr.net, so the browser needs internet access.

## Viewer features

- Orbit, pan, zoom; Iso / Plan / South / East / Fit camera presets
- Click to select, double-click to zoom to an element; hover highlight
- Model tree grouped by storey and IFC class, with search and per-element / per-storey visibility
- IFC class filter chips (e.g. hide all windows)
- Properties panel: GlobalId, storey, material, property sets and quantity sets
- Hide, isolate element, isolate storey, show all
- **Merge files**: load architecture, structure, MEP… as one federated model. Every file after the
  first gets the same origin shift, so they stay aligned. Each file appears in the tree, where you can
  hide or remove it, and **Colour by file** tints each discipline.
- **Sectioning in any direction**:
  - `+ X` / `+ Y` / `+ Z`: planes on the main axes
  - **On surface**: click any face to put a plane on it
  - **From view**: cut square to your line of sight
  - **Box**: a six-sided section box (or **Section box** around the selected element)
  - Drag the arrows in 3D, or use each cut's slider; rotate a plane to any angle with Turn and Tilt;
    flip it to cut the other side; stack as many cuts as you like
  - Cut faces are filled solid, like poché on a drawing
- **Clash coordinator**: test selection A against selection B (by file and IFC class) for hard clashes
  with a tolerance, or for a minimum clearance. Exact triangle tests (three-mesh-bvh) after a fast
  bounding-box sweep, so typical models finish in seconds. Each clash is framed close up with its two
  elements in red and blue, has a status (New, Active, Reviewed, Approved, Resolved) and a comment,
  and shows as a pin in 3D. Running a test again keeps statuses and marks fixed clashes Resolved.
  Export a PDF clash report with a picture of every clash, a CSV, or BCF 2.1 for Revit, Navisworks,
  BIMcollab and Solibri
- **Model check**: one click scores the model out of 100 and lists 14 kinds of common IFC problems
  (duplicate or missing GlobalIds, duplicate elements, misaligned files, proxies, missing storeys,
  properties, materials, names and sizes, tiny geometry, empty storeys). Click an issue to zoom to it,
  show a whole group in the model, or export the list as CSV
- **Export reports**: a printable model report (save as PDF) with a 3D view, element counts per storey,
  quantity totals and a room schedule; CSV files (open in Excel) of every element with all its
  properties, quantities by IFC class, and rooms
- X-ray mode and exploded storeys
- Two-point measuring tool (distance and height difference in mm)
- Room tags and a room schedule; room types use the same rule as `ConsoleApp1/Program.cs`
  (< 10 m² Store, < 30 m² Office, otherwise Meeting room)
- Light and dark themes, works on phones

## Keyboard

| Key | Action |
| --- | --- |
| `F` | Zoom to the selection |
| `H` | Hide the selection |
| `M` | Toggle measuring |
| `X` | Toggle X-ray |
| `Esc` | Cancel a measurement / clear the selection |
